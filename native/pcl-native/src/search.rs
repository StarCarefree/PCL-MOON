use std::sync::Mutex;

use rayon::prelude::*;

use crate::{
    PCL_ERR_INVALID_ARG, PCL_ERR_NULL_PTR, PCL_OK, ffi_guard, mut_slice_from_raw_parts_checked,
    slice_from_raw_parts_checked,
};

/// Search algorithm matching `PCL.Core.Utils.SimilaritySearch`.
pub const PCL_SEARCH_MODE_SIMILARITY: i32 = 0;
/// Search algorithm matching `Plain Craft Launcher 2` `ModBase.Search`.
pub const PCL_SEARCH_MODE_MODBASE: i32 = 1;

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct PclSearchEntryDesc {
    pub source_start: u32,
    pub source_count: u32,
}

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct PclSearchSourceDesc {
    pub alias_start: u32,
    pub alias_count: u32,
    pub weight: f64,
}

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct PclSearchAliasSpan {
    pub text_offset: u32,
    pub text_len: u32,
}

#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PclSearchResult {
    pub entry_index: u32,
    pub similarity: f64,
    pub absolute_right: u8,
}

#[derive(Clone, Copy)]
struct IndexedEntry {
    source_start: u32,
    source_count: u32,
}

#[derive(Clone, Copy)]
struct IndexedSource {
    alias_start: u32,
    alias_count: u32,
    weight: f64,
}

#[derive(Clone, Copy)]
struct IndexedAlias {
    text_start: u32,
    text_len: u32,
    char_mask: u64,
}

#[derive(Clone, Copy, Default)]
struct ScoreRow {
    similarity: f64,
    absolute_right: bool,
}

#[derive(Default)]
struct SearchScratch {
    scores: Vec<ScoreRow>,
    candidates: Vec<PclSearchResult>,
}

/// Immutable UTF-16 fuzzy search index with pre-lowercased, space-stripped entries.
pub struct PclSearchIndex {
    entries: Vec<IndexedEntry>,
    sources: Vec<IndexedSource>,
    aliases: Vec<IndexedAlias>,
    arena: Vec<u16>,
    scratch: Mutex<SearchScratch>,
}

#[inline]
fn compute_char_mask(slice: &[u16]) -> u64 {
    let mut mask = 0u64;
    for &cu in slice {
        let bit = ((cu as u32).wrapping_mul(0x9E37_79B1) >> 26) & 63;
        mask |= 1u64 << bit;
    }
    mask
}

/// Lowercases a UTF-16 slice and removes ASCII `' '` (`0x0020`), preserving unpaired surrogates.
fn preprocess_utf16_nospace(input: &[u16], out: &mut Vec<u16>) {
    out.clear();
    if input.iter().all(|&cu| cu < 0x80) {
        out.reserve(input.len());
        for &cu in input {
            if cu == 0x20 {
                continue;
            }
            let lower = if (0x41..=0x5A).contains(&cu) {
                cu + 32
            } else {
                cu
            };
            out.push(lower);
        }
        return;
    }

    out.reserve(input.len());
    for decoded in char::decode_utf16(input.iter().copied()) {
        match decoded {
            Ok(ch) => {
                for lc in ch.to_lowercase() {
                    if lc != ' ' {
                        let mut buf = [0u16; 2];
                        out.extend_from_slice(lc.encode_utf16(&mut buf));
                    }
                }
            }
            Err(unpaired) => {
                out.push(unpaired.unpaired_surrogate());
            }
        }
    }
}

/// Lowercases a UTF-16 slice without removing spaces.
fn lowercase_utf16(input: &[u16], out: &mut Vec<u16>) {
    out.clear();
    if input.iter().all(|&cu| cu < 0x80) {
        out.reserve(input.len());
        for &cu in input {
            let lower = if (0x41..=0x5A).contains(&cu) {
                cu + 32
            } else {
                cu
            };
            out.push(lower);
        }
        return;
    }

    out.reserve(input.len());
    for decoded in char::decode_utf16(input.iter().copied()) {
        match decoded {
            Ok(ch) => {
                for lc in ch.to_lowercase() {
                    let mut buf = [0u16; 2];
                    out.extend_from_slice(lc.encode_utf16(&mut buf));
                }
            }
            Err(unpaired) => {
                out.push(unpaired.unpaired_surrogate());
            }
        }
    }
}

fn is_all_unicode_whitespace_utf16(input: &[u16]) -> bool {
    char::decode_utf16(input.iter().copied()).all(|r| r.is_ok_and(char::is_whitespace))
}

fn trim_unicode_whitespace_utf16(slice: &[u16]) -> &[u16] {
    if slice.is_empty() {
        return slice;
    }
    if slice.iter().all(|&cu| cu < 0x80) {
        let mut start = 0;
        let mut end = slice.len();
        while start < end && (slice[start] as u8).is_ascii_whitespace() {
            start += 1;
        }
        while end > start && (slice[end - 1] as u8).is_ascii_whitespace() {
            end -= 1;
        }
        return &slice[start..end];
    }

    let mut start = 0;
    while start < slice.len() {
        let cu = slice[start];
        if (0xD800..=0xDBFF).contains(&cu) && start + 1 < slice.len() {
            let low = slice[start + 1];
            if (0xDC00..=0xDFFF).contains(&low) {
                let cp = 0x10000 + (((cu as u32 - 0xD800) << 10) | (low as u32 - 0xDC00));
                if let Some(ch) = char::from_u32(cp)
                    && ch.is_whitespace()
                {
                    start += 2;
                    continue;
                }
            }
            break;
        } else if let Some(ch) = char::from_u32(cu as u32)
            && ch.is_whitespace()
        {
            start += 1;
        } else {
            break;
        }
    }

    let mut end = slice.len();
    while end > start {
        let cu = slice[end - 1];
        if (0xDC00..=0xDFFF).contains(&cu) && end >= start + 2 {
            let high = slice[end - 2];
            if (0xD800..=0xDBFF).contains(&high) {
                let cp = 0x10000 + (((high as u32 - 0xD800) << 10) | (cu as u32 - 0xDC00));
                if let Some(ch) = char::from_u32(cp)
                    && ch.is_whitespace()
                {
                    end -= 2;
                    continue;
                }
            }
            break;
        } else if let Some(ch) = char::from_u32(cu as u32)
            && ch.is_whitespace()
        {
            end -= 1;
        } else {
            break;
        }
    }

    &slice[start..end]
}

#[inline]
fn contains_u16(haystack: &[u16], needle: &[u16]) -> bool {
    if needle.is_empty() {
        return true;
    }
    if needle.len() > haystack.len() {
        return false;
    }
    let first = needle[0];
    let limit = haystack.len() - needle.len();
    let rest = &needle[1..];
    let mut i = 0;
    while i <= limit {
        if haystack[i] == first && &haystack[i + 1..i + needle.len()] == rest {
            return true;
        }
        i += 1;
    }
    false
}

/// Computes `SimilaritySearch._SearchSimilarity` over preprocessed UTF-16 buffers.
fn sim_similarity_search(
    source: &[u16],
    source_mask: u64,
    query_nospace: &[u16],
    query_mask: u64,
    raw_query_len: usize,
    used_buf: &mut Vec<u8>,
) -> f64 {
    if source.is_empty() || query_nospace.is_empty() || (source_mask & query_mask) == 0 {
        return 0.0;
    }

    used_buf.clear();
    used_buf.resize(source.len(), 0);

    let mut weighted_length_sum = 0.0_f64;
    let mut query_index = 0_usize;
    let q_len = query_nospace.len();
    let s_len = source.len();

    while query_index < q_len {
        let mut longest_match_len = 0_usize;
        let mut best_match_start = 0_usize;
        let q_char = query_nospace[query_index];
        let q_rem = q_len - query_index;

        for source_index in 0..s_len {
            let s_rem = s_len - source_index;
            if s_rem <= longest_match_len {
                break;
            }
            if used_buf[source_index] != 0 || source[source_index] != q_char {
                continue;
            }
            let max_possible = q_rem.min(s_rem);
            let mut cur_len = 1_usize;
            while cur_len < max_possible
                && used_buf[source_index + cur_len] == 0
                && source[source_index + cur_len] == query_nospace[query_index + cur_len]
            {
                cur_len += 1;
            }
            if cur_len > longest_match_len {
                longest_match_len = cur_len;
                best_match_start = source_index;
            }
        }

        if longest_match_len > 0 {
            used_buf[best_match_start..best_match_start + longest_match_len].fill(1);
            let inc_weight = 1.4_f64.powf((3 + longest_match_len) as f64) - 3.6;
            let pos_diff = query_index.abs_diff(best_match_start);
            let pos_bonus = 1.0 + 0.3 * (3_usize.saturating_sub(pos_diff) as f64);
            weighted_length_sum += inc_weight * pos_bonus;
        }

        query_index += longest_match_len.max(1);
    }

    let normalized_score = weighted_length_sum / (q_len as f64);
    let source_length_penalty = 3.0 / ((s_len as f64 + 15.0).sqrt());
    let short_query_bonus = if raw_query_len == 1 { 2.0 } else { 1.0 };
    normalized_score * source_length_penalty * short_query_bonus
}

/// Computes `ModBase.SearchSimilarity` over preprocessed UTF-16 buffers, preserving
/// `sp += Math.Max(1, len)` and slice-removal splicing semantics.
fn sim_modbase(
    source: &[u16],
    source_mask: u64,
    query_nospace: &[u16],
    query_mask: u64,
    work_buf: &mut Vec<u16>,
) -> f64 {
    if source.is_empty() || query_nospace.is_empty() || (source_mask & query_mask) == 0 {
        return 0.0;
    }

    let orig_source_len = source.len();
    let q_len = query_nospace.len();
    work_buf.clear();
    work_buf.extend_from_slice(source);

    let mut qp = 0_usize;
    let mut len_sum = 0.0_f64;

    while qp < q_len {
        let mut sp = 0_usize;
        let mut len_max = 0_usize;
        let mut sp_max = 0_usize;
        let q_rem = q_len - qp;
        let cur_len = work_buf.len();

        while sp < cur_len {
            let s_rem = cur_len - sp;
            if s_rem <= len_max {
                break;
            }
            let max_possible = q_rem.min(s_rem);
            let mut len = 0_usize;
            while len < max_possible && work_buf[sp + len] == query_nospace[qp + len] {
                len += 1;
            }
            if len > len_max {
                len_max = len;
                sp_max = sp;
            }
            sp += len.max(1);
        }

        if len_max > 0 {
            let tail_start = sp_max + len_max;
            if tail_start < work_buf.len() {
                work_buf.copy_within(tail_start.., sp_max);
            }
            work_buf.truncate(work_buf.len() - len_max);

            let inc_weight = 1.4_f64.powf((3 + len_max) as f64) - 3.6;
            let pos_diff = qp.abs_diff(sp_max);
            let inc_weight = inc_weight * (1.0 + 0.3 * (3_usize.saturating_sub(pos_diff) as f64));
            len_sum += inc_weight;
        }

        qp += len_max.max(1);
    }

    let short_query_bonus = if q_len <= 2 { (3 - q_len) as f64 } else { 1.0 };
    (len_sum / (q_len as f64))
        * (3.0 / ((orig_source_len as f64 + 15.0).powf(0.5)))
        * short_query_bonus
}

impl PclSearchIndex {
    pub fn build(
        entry_descs: &[PclSearchEntryDesc],
        source_descs: &[PclSearchSourceDesc],
        alias_spans: &[PclSearchAliasSpan],
        text_buf: &[u16],
    ) -> Result<Self, i32> {
        let mut entries = Vec::with_capacity(entry_descs.len());
        for ed in entry_descs {
            let s_start = ed.source_start as usize;
            let s_count = ed.source_count as usize;
            let s_end = s_start
                .checked_add(s_count)
                .filter(|&e| e <= source_descs.len())
                .ok_or(PCL_ERR_INVALID_ARG)?;
            let _ = s_end;
            entries.push(IndexedEntry {
                source_start: ed.source_start,
                source_count: ed.source_count,
            });
        }

        let mut sources = Vec::with_capacity(source_descs.len());
        for sd in source_descs {
            if !sd.weight.is_finite() || sd.weight < 0.0 {
                return Err(PCL_ERR_INVALID_ARG);
            }
            let a_start = sd.alias_start as usize;
            let a_count = sd.alias_count as usize;
            let a_end = a_start
                .checked_add(a_count)
                .filter(|&e| e <= alias_spans.len())
                .ok_or(PCL_ERR_INVALID_ARG)?;
            let _ = a_end;
            sources.push(IndexedSource {
                alias_start: sd.alias_start,
                alias_count: sd.alias_count,
                weight: sd.weight,
            });
        }

        let mut aliases = Vec::with_capacity(alias_spans.len());
        let mut arena = Vec::with_capacity(text_buf.len());
        let mut tmp = Vec::new();

        for sp in alias_spans {
            let t_off = sp.text_offset as usize;
            let t_len = sp.text_len as usize;
            let t_end = t_off
                .checked_add(t_len)
                .filter(|&e| e <= text_buf.len())
                .ok_or(PCL_ERR_INVALID_ARG)?;
            preprocess_utf16_nospace(&text_buf[t_off..t_end], &mut tmp);
            let start_u32 = u32::try_from(arena.len()).map_err(|_| PCL_ERR_INVALID_ARG)?;
            let len_u32 = u32::try_from(tmp.len()).map_err(|_| PCL_ERR_INVALID_ARG)?;
            let mask = compute_char_mask(&tmp);
            arena.extend_from_slice(&tmp);
            aliases.push(IndexedAlias {
                text_start: start_u32,
                text_len: len_u32,
                char_mask: mask,
            });
        }

        let scratch = SearchScratch {
            scores: vec![ScoreRow::default(); entries.len()],
            candidates: Vec::with_capacity(entries.len().min(512)),
        };

        Ok(Self {
            entries,
            sources,
            aliases,
            arena,
            scratch: Mutex::new(scratch),
        })
    }

    pub fn query(
        &self,
        mode: i32,
        query: &[u16],
        max_blur_count: usize,
        min_blur_similarity: f64,
        out_results: &mut [PclSearchResult],
    ) -> Result<usize, i32> {
        if mode != PCL_SEARCH_MODE_SIMILARITY && mode != PCL_SEARCH_MODE_MODBASE {
            return Err(PCL_ERR_INVALID_ARG);
        }
        if !min_blur_similarity.is_finite() {
            return Err(PCL_ERR_INVALID_ARG);
        }
        if self.entries.is_empty() {
            return Ok(0);
        }

        // Parse query parts according to the selected mode
        let mut query_parts: Vec<(Vec<u16>, u64)> = Vec::new();
        if mode == PCL_SEARCH_MODE_SIMILARITY {
            if is_all_unicode_whitespace_utf16(query) {
                return Ok(self.emit_unfiltered(out_results));
            }
            for seg in query.split(|&cu| cu == 0x20) {
                let trimmed = trim_unicode_whitespace_utf16(seg);
                if !trimmed.is_empty() {
                    let mut lower = Vec::new();
                    lowercase_utf16(trimmed, &mut lower);
                    if !lower.is_empty() {
                        let mask = compute_char_mask(&lower);
                        query_parts.push((lower, mask));
                    }
                }
            }
            if query_parts.is_empty() {
                return Ok(self.emit_unfiltered(out_results));
            }
        } else {
            for seg in query.split(|&cu| cu == 0x20) {
                if !seg.is_empty() {
                    let mut lower = Vec::new();
                    lowercase_utf16(seg, &mut lower);
                    if !lower.is_empty() {
                        let mask = compute_char_mask(&lower);
                        query_parts.push((lower, mask));
                    }
                }
            }
            if query_parts.is_empty() {
                return Ok(self.emit_unfiltered(out_results));
            }
        }

        let mut query_nospace = Vec::with_capacity(query.len());
        preprocess_utf16_nospace(query, &mut query_nospace);
        let query_mask = compute_char_mask(&query_nospace);
        let raw_query_len = query.len();

        let mut scratch = self
            .scratch
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let SearchScratch { scores, candidates } = &mut *scratch;
        if scores.len() != self.entries.len() {
            scores.resize(self.entries.len(), ScoreRow::default());
        }

        let eval_entry = |entry: &IndexedEntry, used_buf: &mut Vec<u8>, work_buf: &mut Vec<u16>| {
            let s_start = entry.source_start as usize;
            let s_end = s_start + entry.source_count as usize;
            let entry_sources = &self.sources[s_start..s_end];

            let similarity = if mode == PCL_SEARCH_MODE_SIMILARITY {
                let mut total_weight = 0.0_f64;
                let mut weighted_sum = 0.0_f64;
                for src in entry_sources {
                    let a_start = src.alias_start as usize;
                    let a_end = a_start + src.alias_count as usize;
                    for alias in &self.aliases[a_start..a_end] {
                        let t_start = alias.text_start as usize;
                        let t_end = t_start + alias.text_len as usize;
                        let s = sim_similarity_search(
                            &self.arena[t_start..t_end],
                            alias.char_mask,
                            &query_nospace,
                            query_mask,
                            raw_query_len,
                            used_buf,
                        );
                        weighted_sum += s * src.weight;
                        total_weight += src.weight;
                    }
                }
                if total_weight == 0.0 {
                    0.0
                } else {
                    weighted_sum / total_weight
                }
            } else {
                let mut total_weight = 0.0_f64;
                let mut sum = 0.0_f64;
                for src in entry_sources {
                    let a_start = src.alias_start as usize;
                    let a_end = a_start + src.alias_count as usize;
                    if a_start < a_end {
                        let mut max_sim = f64::NEG_INFINITY;
                        for alias in &self.aliases[a_start..a_end] {
                            let t_start = alias.text_start as usize;
                            let t_end = t_start + alias.text_len as usize;
                            let s = sim_modbase(
                                &self.arena[t_start..t_end],
                                alias.char_mask,
                                &query_nospace,
                                query_mask,
                                work_buf,
                            );
                            if s > max_sim {
                                max_sim = s;
                            }
                        }
                        sum += max_sim * src.weight;
                    }
                    total_weight += src.weight;
                }
                if total_weight == 0.0 {
                    0.0
                } else {
                    sum / total_weight
                }
            };

            let mut absolute_right = true;
            for (qp, qp_mask) in &query_parts {
                let mut found = false;
                'sources: for src in entry_sources {
                    let a_start = src.alias_start as usize;
                    let a_end = a_start + src.alias_count as usize;
                    for alias in &self.aliases[a_start..a_end] {
                        if (alias.char_mask & *qp_mask) == *qp_mask {
                            let t_start = alias.text_start as usize;
                            let t_end = t_start + alias.text_len as usize;
                            if contains_u16(&self.arena[t_start..t_end], qp) {
                                found = true;
                                break 'sources;
                            }
                        }
                    }
                }
                if !found {
                    absolute_right = false;
                    break;
                }
            }

            ScoreRow {
                similarity: if similarity == 0.0 { 0.0 } else { similarity },
                absolute_right,
            }
        };

        if self.entries.len() >= 256 {
            scores.par_chunks_mut(256).enumerate().for_each_init(
                || (Vec::<u8>::new(), Vec::<u16>::new()),
                |(used_buf, work_buf), (chunk_idx, out_chunk)| {
                    let base = chunk_idx * 256;
                    for (offset, slot) in out_chunk.iter_mut().enumerate() {
                        *slot = eval_entry(&self.entries[base + offset], used_buf, work_buf);
                    }
                },
            );
        } else {
            let mut used_buf = Vec::new();
            let mut work_buf = Vec::new();
            for (i, entry) in self.entries.iter().enumerate() {
                scores[i] = eval_entry(entry, &mut used_buf, &mut work_buf);
            }
        }

        candidates.clear();
        for (i, row) in scores.iter().enumerate() {
            if row.absolute_right || (max_blur_count > 0 && row.similarity >= min_blur_similarity) {
                candidates.push(PclSearchResult {
                    entry_index: i as u32,
                    similarity: row.similarity,
                    absolute_right: u8::from(row.absolute_right),
                });
            }
        }

        // Stable sort order: absolute_right desc, similarity desc, original entry_index asc
        candidates.sort_unstable_by(|a, b| {
            b.absolute_right
                .cmp(&a.absolute_right)
                .then_with(|| b.similarity.total_cmp(&a.similarity))
                .then_with(|| a.entry_index.cmp(&b.entry_index))
        });

        let mut total_count = 0_usize;
        let mut blur_count = 0_usize;
        for cand in candidates.iter() {
            if cand.absolute_right != 0 {
                if total_count < out_results.len() {
                    out_results[total_count] = *cand;
                }
                total_count += 1;
            } else {
                if cand.similarity < min_blur_similarity || blur_count >= max_blur_count {
                    break;
                }
                if total_count < out_results.len() {
                    out_results[total_count] = *cand;
                }
                total_count += 1;
                blur_count += 1;
            }
        }

        Ok(total_count)
    }

    fn emit_unfiltered(&self, out_results: &mut [PclSearchResult]) -> usize {
        let copy_len = out_results.len().min(self.entries.len());
        for (i, slot) in out_results[..copy_len].iter_mut().enumerate() {
            *slot = PclSearchResult {
                entry_index: i as u32,
                similarity: 0.0,
                absolute_right: 0,
            };
        }
        self.entries.len()
    }
}

/// Builds an immutable UTF-16 fuzzy search index (`PclSearchIndex`) on the Rust heap.
/// Must be freed with `pcl_native_free_search_index`.
///
/// # Safety
/// - `entries_ptr`, `sources_ptr`, `aliases_ptr`, `text_buf_ptr` must be valid for reads of
///   their respective element counts when count > 0.
/// - `out_index` must be a valid non-null writable pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn pcl_native_search_index_create(
    entries_ptr: *const PclSearchEntryDesc,
    entry_count: usize,
    sources_ptr: *const PclSearchSourceDesc,
    source_count: usize,
    aliases_ptr: *const PclSearchAliasSpan,
    alias_count: usize,
    text_buf_ptr: *const u16,
    text_buf_len: usize,
    out_index: *mut *mut PclSearchIndex,
) -> i32 {
    ffi_guard(|| {
        if out_index.is_null() {
            return PCL_ERR_NULL_PTR;
        }
        // SAFETY: Checked `out_index` is non-null.
        unsafe {
            *out_index = std::ptr::null_mut();
        }

        // SAFETY: Caller guarantees each buffer is valid for its specified length.
        let entries = match unsafe { slice_from_raw_parts_checked(entries_ptr, entry_count) } {
            Ok(s) => s,
            Err(code) => return code,
        };
        // SAFETY: Same as above.
        let sources = match unsafe { slice_from_raw_parts_checked(sources_ptr, source_count) } {
            Ok(s) => s,
            Err(code) => return code,
        };
        // SAFETY: Same as above.
        let aliases = match unsafe { slice_from_raw_parts_checked(aliases_ptr, alias_count) } {
            Ok(s) => s,
            Err(code) => return code,
        };
        // SAFETY: Same as above.
        let text_buf = match unsafe { slice_from_raw_parts_checked(text_buf_ptr, text_buf_len) } {
            Ok(s) => s,
            Err(code) => return code,
        };

        let index = match PclSearchIndex::build(entries, sources, aliases, text_buf) {
            Ok(idx) => idx,
            Err(code) => return code,
        };

        // SAFETY: `out_index` is non-null and writable. Ownership transfers to caller.
        unsafe {
            *out_index = Box::into_raw(Box::new(index));
        }
        PCL_OK
    })
}

/// Queries a `PclSearchIndex` and writes sorted results into caller-provided `out_results_ptr`.
///
/// # Safety
/// - `index` must be a valid pointer returned by `pcl_native_search_index_create`.
/// - `query_ptr` must be valid for reads of `query_len` `u16`s when `query_len > 0`.
/// - `out_results_ptr` must be valid for writes of `out_results_cap` `PclSearchResult`s when `out_results_cap > 0`.
/// - `out_total_count`, if non-null, must be a valid writable pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn pcl_native_search_index_query(
    index: *const PclSearchIndex,
    mode: i32,
    query_ptr: *const u16,
    query_len: usize,
    max_blur_count: usize,
    min_blur_similarity: f64,
    out_results_ptr: *mut PclSearchResult,
    out_results_cap: usize,
    out_total_count: *mut usize,
) -> i32 {
    ffi_guard(|| {
        if index.is_null() {
            return PCL_ERR_NULL_PTR;
        }
        // SAFETY: Caller guarantees `query_ptr` is valid for `query_len` `u16` elements.
        let query = match unsafe { slice_from_raw_parts_checked(query_ptr, query_len) } {
            Ok(s) => s,
            Err(code) => return code,
        };
        // SAFETY: Caller guarantees `out_results_ptr` is valid for `out_results_cap` writes.
        let out_results =
            match unsafe { mut_slice_from_raw_parts_checked(out_results_ptr, out_results_cap) } {
                Ok(s) => s,
                Err(code) => return code,
            };

        // SAFETY: Caller guarantees `index` is a live `PclSearchIndex` pointer.
        let idx_ref = unsafe { &*index };
        let total = match idx_ref.query(
            mode,
            query,
            max_blur_count,
            min_blur_similarity,
            out_results,
        ) {
            Ok(n) => n,
            Err(code) => return code,
        };

        if !out_total_count.is_null() {
            // SAFETY: Checked `out_total_count` is non-null.
            unsafe {
                *out_total_count = total;
            }
        }
        PCL_OK
    })
}

/// Returns the number of indexed entries in a `PclSearchIndex`.
///
/// # Safety
/// - `index` must be a valid pointer returned by `pcl_native_search_index_create`.
/// - `out_len` must be a valid non-null writable pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn pcl_native_search_index_len(
    index: *const PclSearchIndex,
    out_len: *mut usize,
) -> i32 {
    ffi_guard(|| {
        if index.is_null() || out_len.is_null() {
            return PCL_ERR_NULL_PTR;
        }
        // SAFETY: Checked both pointers non-null; caller guarantees `index` is live.
        unsafe {
            *out_len = (*index).entries.len();
        }
        PCL_OK
    })
}

/// Frees a `PclSearchIndex` created by `pcl_native_search_index_create`. Null is a no-op.
///
/// # Safety
/// - `index` must either be null or a pointer previously returned by `pcl_native_search_index_create`
///   that has not yet been freed and is not concurrently accessed.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn pcl_native_free_search_index(index: *mut PclSearchIndex) -> i32 {
    ffi_guard(|| {
        if !index.is_null() {
            // SAFETY: `index` came from `Box::into_raw` in `pcl_native_search_index_create`.
            unsafe {
                drop(Box::from_raw(index));
            }
        }
        PCL_OK
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn to_utf16(s: &str) -> Vec<u16> {
        s.encode_utf16().collect()
    }

    struct IndexBuilder {
        entries: Vec<PclSearchEntryDesc>,
        sources: Vec<PclSearchSourceDesc>,
        aliases: Vec<PclSearchAliasSpan>,
        text: Vec<u16>,
    }

    impl IndexBuilder {
        fn new() -> Self {
            Self {
                entries: Vec::new(),
                sources: Vec::new(),
                aliases: Vec::new(),
                text: Vec::new(),
            }
        }

        fn push_entry(&mut self, entry_sources: &[(&[&str], f64)]) {
            let source_start = self.sources.len() as u32;
            for &(aliases, weight) in entry_sources {
                let alias_start = self.aliases.len() as u32;
                for &a in aliases {
                    let off = self.text.len() as u32;
                    let u16s: Vec<u16> = a.encode_utf16().collect();
                    self.text.extend_from_slice(&u16s);
                    self.aliases.push(PclSearchAliasSpan {
                        text_offset: off,
                        text_len: u16s.len() as u32,
                    });
                }
                self.sources.push(PclSearchSourceDesc {
                    alias_start,
                    alias_count: aliases.len() as u32,
                    weight,
                });
            }
            self.entries.push(PclSearchEntryDesc {
                source_start,
                source_count: entry_sources.len() as u32,
            });
        }

        fn build_ffi(&self) -> *mut PclSearchIndex {
            let mut ptr = std::ptr::null_mut();
            // SAFETY: All CSR slices and `out_index` pointer are valid.
            let code = unsafe {
                pcl_native_search_index_create(
                    self.entries.as_ptr(),
                    self.entries.len(),
                    self.sources.as_ptr(),
                    self.sources.len(),
                    self.aliases.as_ptr(),
                    self.aliases.len(),
                    self.text.as_ptr(),
                    self.text.len(),
                    &mut ptr,
                )
            };
            assert_eq!(code, PCL_OK);
            assert!(!ptr.is_null());
            ptr
        }
    }

    /// Reference implementation of `SimilaritySearch._SearchSimilarity` over UTF-16 slices.
    fn ref_similarity_search(source_raw: &str, query_raw: &str) -> f64 {
        let mut s = Vec::new();
        let mut q = Vec::new();
        preprocess_utf16_nospace(&to_utf16(source_raw), &mut s);
        preprocess_utf16_nospace(&to_utf16(query_raw), &mut q);
        if s.is_empty() || q.is_empty() {
            return 0.0;
        }
        let mut used = vec![false; s.len()];
        let mut weighted_sum = 0.0_f64;
        let mut qi = 0;
        while qi < q.len() {
            let mut longest = 0;
            let mut best_si = 0;
            for si in 0..s.len() {
                let mut cur = 0;
                while qi + cur < q.len()
                    && si + cur < s.len()
                    && !used[si + cur]
                    && s[si + cur] == q[qi + cur]
                {
                    cur += 1;
                }
                if cur <= longest {
                    continue;
                }
                longest = cur;
                best_si = si;
            }
            if longest > 0 {
                for slot in &mut used[best_si..best_si + longest] {
                    *slot = true;
                }
                let mut inc = 1.4_f64.powf((3 + longest) as f64) - 3.6;
                let pos_diff = qi.abs_diff(best_si);
                inc *= 1.0 + 0.3 * (3_usize.saturating_sub(pos_diff) as f64);
                weighted_sum += inc;
            }
            qi += longest.max(1);
        }
        let raw_len = to_utf16(query_raw).len();
        (weighted_sum / q.len() as f64)
            * (3.0 / ((s.len() as f64 + 15.0).sqrt()))
            * (if raw_len == 1 { 2.0 } else { 1.0 })
    }

    /// Reference implementation of `ModBase.SearchSimilarity` over UTF-16 slices.
    fn ref_modbase_similarity(source_raw: &str, query_raw: &str) -> f64 {
        let mut s = Vec::new();
        let mut q = Vec::new();
        preprocess_utf16_nospace(&to_utf16(source_raw), &mut s);
        preprocess_utf16_nospace(&to_utf16(query_raw), &mut q);
        if s.is_empty() || q.is_empty() {
            return 0.0;
        }
        let orig_len = s.len();
        let q_len = q.len();
        let mut qp = 0;
        let mut len_sum = 0.0_f64;
        while qp < q_len {
            let mut sp = 0;
            let mut len_max = 0;
            let mut sp_max = 0;
            while sp < s.len() {
                let mut len = 0;
                while qp + len < q_len && sp + len < s.len() && s[sp + len] == q[qp + len] {
                    len += 1;
                }
                if len > len_max {
                    len_max = len;
                    sp_max = sp;
                }
                sp += len.max(1);
            }
            if len_max > 0 {
                let mut next = s[..sp_max].to_vec();
                if s.len() > sp_max + len_max {
                    next.extend_from_slice(&s[sp_max + len_max..]);
                }
                s = next;
                let mut inc = 1.4_f64.powf((3 + len_max) as f64) - 3.6;
                inc *= 1.0 + 0.3 * (3_usize.saturating_sub(qp.abs_diff(sp_max)) as f64);
                len_sum += inc;
            }
            qp += len_max.max(1);
        }
        (len_sum / q_len as f64)
            * (3.0 / ((orig_len as f64 + 15.0).powf(0.5)))
            * (if q_len <= 2 { (3 - q_len) as f64 } else { 1.0 })
    }

    #[test]
    fn search_distinguishes_similarity_and_modbase_modes_and_utf16_emoji() {
        // Test specific divergence cases between SimilaritySearch and ModBase:
        // 1) "aaab" vs "aab": ModBase skips sp=1 (`sp += len.max(1)`), SimilaritySearch scans every index!
        // 2) "xabcy" with query "abcxy": SimilaritySearch keeps gap (`usedSourceIndices`), ModBase splices `abc` leaving `xy` contiguous!
        // 3) Query " a": raw len = 2 vs nospace len = 1! SimilaritySearch gives 1.0 bonus, ModBase gives 2.0 bonus!
        // 4) UTF-16 CJK + Emoji surrogate pair "机械动力😀Create": Emoji is 2 UTF-16 code units!
        let cases = [
            ("aaab", "aab"),
            ("xabcy", "abcxy"),
            ("abc", " a"),
            ("机械动力 😀 Create Mod", "动力 😀"),
            ("Applied Energistics 2", "applied energ"),
        ];

        for (src, qry) in cases {
            let mut b = IndexBuilder::new();
            b.push_entry(&[(&[src], 1.0)]);
            let idx = b.build_ffi();
            let q16 = to_utf16(qry);

            let mut out = [PclSearchResult {
                entry_index: 0,
                similarity: 0.0,
                absolute_right: 0,
            }; 1];
            let mut total = 0;

            // SAFETY: `idx` and output/query buffers are valid and freed after assertions.
            unsafe {
                assert_eq!(
                    pcl_native_search_index_query(
                        idx,
                        PCL_SEARCH_MODE_SIMILARITY,
                        q16.as_ptr(),
                        q16.len(),
                        5,
                        -1.0,
                        out.as_mut_ptr(),
                        1,
                        &mut total,
                    ),
                    PCL_OK
                );
                let exp_sim = ref_similarity_search(src, qry);
                assert!(
                    (out[0].similarity - exp_sim).abs() < 1e-12,
                    "SimilaritySearch mismatch for ({src:?}, {qry:?}): got {} exp {exp_sim}",
                    out[0].similarity
                );

                assert_eq!(
                    pcl_native_search_index_query(
                        idx,
                        PCL_SEARCH_MODE_MODBASE,
                        q16.as_ptr(),
                        q16.len(),
                        5,
                        -1.0,
                        out.as_mut_ptr(),
                        1,
                        &mut total,
                    ),
                    PCL_OK
                );
                let exp_mb = ref_modbase_similarity(src, qry);
                assert!(
                    (out[0].similarity - exp_mb).abs() < 1e-12,
                    "ModBase mismatch for ({src:?}, {qry:?}): got {} exp {exp_mb}",
                    out[0].similarity
                );

                assert_eq!(pcl_native_free_search_index(idx), PCL_OK);
            }
        }
    }

    #[test]
    fn search_modbase_takes_max_alias_and_preserves_stable_tie_order() {
        let mut b = IndexBuilder::new();
        b.push_entry(&[(&["unrelated", "机械动力"], 1.0)]);
        b.push_entry(&[(&["机械动力"], 1.0)]);
        b.push_entry(&[(&["机械"], 1.0)]);
        b.push_entry(&[(&["机械"], 0.5), (&["动力"], 0.5)]);

        let idx = b.build_ffi();
        let q16 = to_utf16("机械 动力");
        let mut out = [PclSearchResult {
            entry_index: 0,
            similarity: 0.0,
            absolute_right: 0,
        }; 8];
        let mut total = 0;

        // SAFETY: Valid index, query, and output slices.
        unsafe {
            assert_eq!(
                pcl_native_search_index_query(
                    idx,
                    PCL_SEARCH_MODE_MODBASE,
                    q16.as_ptr(),
                    q16.len(),
                    1,
                    0.01,
                    out.as_mut_ptr(),
                    out.len(),
                    &mut total,
                ),
                PCL_OK
            );

            assert_eq!(total, 4);
            assert_eq!(out[0].entry_index, 0);
            assert_eq!(out[0].absolute_right, 1);
            assert_eq!(out[1].entry_index, 1);
            assert_eq!(out[1].absolute_right, 1);
            assert!((out[0].similarity - out[1].similarity).abs() < 1e-12);
            assert_eq!(out[2].entry_index, 3);
            assert_eq!(out[2].absolute_right, 1);
            assert_eq!(out[3].entry_index, 2);
            assert_eq!(out[3].absolute_right, 0);

            assert_eq!(pcl_native_free_search_index(idx), PCL_OK);
        }
    }

    #[test]
    fn search_empty_query_parallel_path_and_null_guards() {
        let mut b = IndexBuilder::new();
        for i in 0..300 {
            let label = if i == 250 {
                "Applied Energistics 2 😀".to_string()
            } else {
                format!("Synthetic Mod {i}")
            };
            b.push_entry(&[(&[&label], 1.0)]);
        }
        let idx = b.build_ffi();
        let mut len = 0;
        let empty_q = to_utf16("   ");
        let emoji_q = to_utf16("energistics 😀");
        let mut out = vec![
            PclSearchResult {
                entry_index: 0,
                similarity: 0.0,
                absolute_right: 0,
            };
            10
        ];
        let mut total = 0;

        // SAFETY: Valid index and buffers; null/invalid arguments checked cleanly.
        unsafe {
            assert_eq!(pcl_native_search_index_len(idx, &mut len), PCL_OK);
            assert_eq!(len, 300);

            assert_eq!(
                pcl_native_search_index_query(
                    idx,
                    PCL_SEARCH_MODE_SIMILARITY,
                    empty_q.as_ptr(),
                    empty_q.len(),
                    5,
                    0.1,
                    out.as_mut_ptr(),
                    out.len(),
                    &mut total,
                ),
                PCL_OK
            );
            assert_eq!(total, 300);
            for (i, item) in out.iter().enumerate() {
                assert_eq!(item.entry_index, i as u32);
            }

            assert_eq!(
                pcl_native_search_index_query(
                    idx,
                    PCL_SEARCH_MODE_SIMILARITY,
                    emoji_q.as_ptr(),
                    emoji_q.len(),
                    5,
                    0.2,
                    out.as_mut_ptr(),
                    out.len(),
                    &mut total,
                ),
                PCL_OK
            );
            assert!(total >= 1);
            assert_eq!(out[0].entry_index, 250);
            assert_eq!(out[0].absolute_right, 1);

            assert_eq!(
                pcl_native_search_index_query(
                    std::ptr::null(),
                    0,
                    emoji_q.as_ptr(),
                    emoji_q.len(),
                    5,
                    0.1,
                    out.as_mut_ptr(),
                    out.len(),
                    &mut total,
                ),
                PCL_ERR_NULL_PTR
            );
            assert_eq!(
                pcl_native_search_index_query(
                    idx,
                    99,
                    emoji_q.as_ptr(),
                    emoji_q.len(),
                    5,
                    0.1,
                    out.as_mut_ptr(),
                    out.len(),
                    &mut total,
                ),
                PCL_ERR_INVALID_ARG
            );
            assert_eq!(pcl_native_free_search_index(std::ptr::null_mut()), PCL_OK);
            assert_eq!(pcl_native_free_search_index(idx), PCL_OK);
        }
    }

    #[test]
    fn search_handles_unpaired_surrogates_zero_sources_and_small_out_cap() {
        let mut text = Vec::new();
        let off = text.len() as u32;
        text.extend_from_slice(&[0xD800, 0x0041]);
        text.extend_from_slice(&to_utf16("模组"));
        let entries = [
            PclSearchEntryDesc {
                source_start: 0,
                source_count: 0,
            },
            PclSearchEntryDesc {
                source_start: 0,
                source_count: 1,
            },
            PclSearchEntryDesc {
                source_start: 1,
                source_count: 1,
            },
        ];
        let sources = [
            PclSearchSourceDesc {
                alias_start: 0,
                alias_count: 0,
                weight: 1.0,
            },
            PclSearchSourceDesc {
                alias_start: 0,
                alias_count: 1,
                weight: 1.0,
            },
        ];
        let aliases = [PclSearchAliasSpan {
            text_offset: off,
            text_len: 4,
        }];
        let mut idx = std::ptr::null_mut();
        let q = [0xD800, 0x0061];
        let mut out = [PclSearchResult {
            entry_index: 99,
            similarity: 0.0,
            absolute_right: 0,
        }; 1];
        let mut total = 0;

        // SAFETY: Valid CSR descriptors, query, and output pointers.
        unsafe {
            assert_eq!(
                pcl_native_search_index_create(
                    entries.as_ptr(),
                    entries.len(),
                    sources.as_ptr(),
                    sources.len(),
                    aliases.as_ptr(),
                    aliases.len(),
                    text.as_ptr(),
                    text.len(),
                    &mut idx,
                ),
                PCL_OK
            );

            assert_eq!(
                pcl_native_search_index_query(
                    idx,
                    PCL_SEARCH_MODE_MODBASE,
                    q.as_ptr(),
                    q.len(),
                    5,
                    0.0,
                    out.as_mut_ptr(),
                    out.len(),
                    &mut total,
                ),
                PCL_OK
            );
            assert_eq!(total, 3);
            assert_eq!(out[0].entry_index, 2);
            assert_eq!(out[0].absolute_right, 1);

            assert_eq!(
                pcl_native_search_index_query(
                    idx,
                    PCL_SEARCH_MODE_SIMILARITY,
                    q.as_ptr(),
                    q.len(),
                    0,
                    0.0,
                    out.as_mut_ptr(),
                    out.len(),
                    &mut total,
                ),
                PCL_OK
            );
            assert_eq!(total, 1);
            assert_eq!(out[0].entry_index, 2);

            assert_eq!(pcl_native_free_search_index(idx), PCL_OK);
        }
    }
}
