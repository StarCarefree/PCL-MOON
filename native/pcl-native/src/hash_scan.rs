use std::cell::RefCell;
use std::fs::{File, OpenOptions};
use std::io::{BufReader, Cursor, Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};

use rayon::prelude::*;
use sha1::{Digest, Sha1};
use zip::ZipArchive;

use crate::{
    PCL_ERR_INVALID_ARG, PCL_ERR_INVALID_FORMAT, PCL_ERR_IO, PCL_ERR_NULL_PTR, PCL_OK, ffi_guard,
    mut_slice_from_raw_parts_checked, slice_from_raw_parts_checked,
};

const MURMUR2_MUL: u32 = 0x5BD1_E995;
const IN_MEMORY_READ_LIMIT: u64 = 16 * 1024 * 1024;
const STREAM_CHUNK_SIZE: usize = 64 * 1024;
const MAX_METADATA_BYTES: u64 = 16 * 1024 * 1024;

/// Mod loader identifiers returned by `pcl_native_batch_scan_jars`.
pub const PCL_LOADER_UNKNOWN: u8 = 0;
pub const PCL_LOADER_FABRIC: u8 = 1;
pub const PCL_LOADER_QUILT: u8 = 2;
pub const PCL_LOADER_FORGE: u8 = 3;
pub const PCL_LOADER_NEOFORGE: u8 = 4;

/// Metadata file kinds returned by `pcl_native_batch_scan_jars`.
pub const PCL_META_NONE: u8 = 0;
pub const PCL_META_FABRIC_JSON: u8 = 1;
pub const PCL_META_QUILT_JSON: u8 = 2;
pub const PCL_META_MODS_TOML: u8 = 3;
pub const PCL_META_NEOFORGE_MODS_TOML: u8 = 4;
pub const PCL_META_MCMOD_INFO: u8 = 5;

#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct PclUtf16Span {
    pub offset: u32,
    pub len: u32,
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct PclFileHashResult {
    pub status: i32,
    pub murmur2: u32,
    pub sha1: [u8; 20],
    pub file_size: u64,
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct PclJarScanItem {
    pub status: i32,
    pub detected_loader: u8,
    pub metadata_kind: u8,
    pub _reserved: [u8; 2],
    pub murmur2: u32,
    pub sha1: [u8; 20],
    pub file_size: u64,
    pub metadata_offset: u32,
    pub metadata_len: u32,
}

#[repr(C)]
pub struct PclJarScanBatch {
    pub items_ptr: *const PclJarScanItem,
    pub item_count: usize,
    pub data_ptr: *const u8,
    pub data_len: usize,
}

thread_local! {
    static FILE_SCRATCH_BUF: RefCell<Vec<u8>> = const { RefCell::new(Vec::new()) };
}

#[inline]
fn is_murmur_whitespace(b: u8) -> bool {
    matches!(b, 9 | 10 | 13 | 32)
}

#[inline]
fn count_filtered_bytes(data: &[u8]) -> usize {
    let mut count = 0_usize;
    for &b in data {
        count += usize::from(!is_murmur_whitespace(b));
    }
    count
}

struct Murmur2Hasher {
    h: u32,
    tail: [u8; 4],
    tail_len: usize,
}

impl Murmur2Hasher {
    #[inline]
    fn new(filtered_len: u32) -> Self {
        Self {
            h: 1_u32 ^ filtered_len,
            tail: [0; 4],
            tail_len: 0,
        }
    }

    #[inline]
    fn mix_word(&mut self, mut k: u32) {
        k = k.wrapping_mul(MURMUR2_MUL);
        k ^= k >> 24;
        k = k.wrapping_mul(MURMUR2_MUL);
        self.h = self.h.wrapping_mul(MURMUR2_MUL);
        self.h ^= k;
    }

    fn update_filtered(&mut self, mut bytes: &[u8]) {
        if bytes.is_empty() {
            return;
        }
        if self.tail_len > 0 {
            let take = (4 - self.tail_len).min(bytes.len());
            self.tail[self.tail_len..self.tail_len + take].copy_from_slice(&bytes[..take]);
            self.tail_len += take;
            bytes = &bytes[take..];
            if self.tail_len == 4 {
                let k = u32::from_le_bytes(self.tail);
                self.mix_word(k);
                self.tail_len = 0;
            }
        }
        let (chunks, rem) = bytes.as_chunks::<4>();
        for &chunk in chunks {
            self.mix_word(u32::from_le_bytes(chunk));
        }
        if !rem.is_empty() {
            self.tail[..rem.len()].copy_from_slice(rem);
            self.tail_len = rem.len();
        }
    }

    fn update_unfiltered(&mut self, data: &[u8]) {
        let mut buf = [0u8; 4096];
        for chunk in data.chunks(buf.len()) {
            let mut n = 0_usize;
            for &b in chunk {
                buf[n] = b;
                n += usize::from(!is_murmur_whitespace(b));
            }
            self.update_filtered(&buf[..n]);
        }
    }

    #[inline]
    fn finish(mut self) -> u32 {
        match self.tail_len {
            3 => {
                self.h ^= (self.tail[0] as u32) | ((self.tail[1] as u32) << 8);
                self.h ^= (self.tail[2] as u32) << 16;
                self.h = self.h.wrapping_mul(MURMUR2_MUL);
            }
            2 => {
                self.h ^= (self.tail[0] as u32) | ((self.tail[1] as u32) << 8);
                self.h = self.h.wrapping_mul(MURMUR2_MUL);
            }
            1 => {
                self.h ^= self.tail[0] as u32;
                self.h = self.h.wrapping_mul(MURMUR2_MUL);
            }
            _ => {}
        }
        self.h ^= self.h >> 13;
        self.h = self.h.wrapping_mul(MURMUR2_MUL);
        self.h ^= self.h >> 15;
        self.h
    }
}

/// Computes CurseForge MurmurHash2 over an in-memory byte slice, matching `MurmurHash2Provider`.
pub fn murmur2_slice(data: &[u8]) -> u32 {
    let filtered_len = count_filtered_bytes(data);
    let mut hasher = Murmur2Hasher::new(filtered_len as u32);
    hasher.update_unfiltered(data);
    hasher.finish()
}

fn utf16_to_path_buf(wide: &[u16]) -> Result<PathBuf, i32> {
    if wide.is_empty() {
        return Err(PCL_ERR_INVALID_ARG);
    }
    #[cfg(windows)]
    {
        use std::ffi::OsString;
        use std::os::windows::ffi::OsStringExt;
        Ok(PathBuf::from(OsString::from_wide(wide)))
    }
    #[cfg(not(windows))]
    {
        let s = String::from_utf16(wide).map_err(|_| PCL_ERR_INVALID_ARG)?;
        Ok(PathBuf::from(s))
    }
}

fn open_file_shared_read(path: &Path) -> Result<File, i32> {
    let mut opts = OpenOptions::new();
    opts.read(true);
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        // FILE_SHARE_READ (1) | FILE_SHARE_WRITE (2) | FILE_SHARE_DELETE (4)
        opts.share_mode(0x7);
    }
    opts.open(path).map_err(|_| PCL_ERR_IO)
}

/// Computes CurseForge MurmurHash2 and optional SHA-1 from a file without per-file LOH growth.
fn hash_file_with_scratch(
    file: &mut File,
    compute_sha1: bool,
    scratch: &mut Vec<u8>,
) -> Result<(u32, [u8; 20], u64), i32> {
    let meta_len = file.metadata().map(|m| m.len()).ok();
    if let Some(len) = meta_len
        && len <= IN_MEMORY_READ_LIMIT
    {
        scratch.clear();
        scratch.reserve(len as usize);
        file.read_to_end(scratch).map_err(|_| PCL_ERR_IO)?;
        let file_size = scratch.len() as u64;
        let sha1_digest: [u8; 20] = if compute_sha1 {
            Sha1::digest(scratch.as_slice()).into()
        } else {
            [0u8; 20]
        };
        let murmur2 = murmur2_slice(scratch.as_slice());
        if scratch.capacity() > (IN_MEMORY_READ_LIMIT as usize) * 2 {
            scratch.shrink_to(4 * 1024 * 1024);
        }
        return Ok((murmur2, sha1_digest, file_size));
    }

    // Streaming 2-pass path for huge files (> 16 MiB) using bounded 64 KiB buffer.
    let mut chunk = [0u8; STREAM_CHUNK_SIZE];
    let mut sha1_hasher = compute_sha1.then(Sha1::new);
    let mut filtered_len = 0_u64;
    let mut file_size = 0_u64;

    loop {
        let n = file.read(&mut chunk).map_err(|_| PCL_ERR_IO)?;
        if n == 0 {
            break;
        }
        let slice = &chunk[..n];
        file_size += n as u64;
        filtered_len += count_filtered_bytes(slice) as u64;
        if let Some(ref mut h) = sha1_hasher {
            h.update(slice);
        }
    }

    let sha1_digest: [u8; 20] = match sha1_hasher {
        Some(h) => h.finalize().into(),
        None => [0u8; 20],
    };

    file.seek(SeekFrom::Start(0)).map_err(|_| PCL_ERR_IO)?;
    let mut murmur = Murmur2Hasher::new(filtered_len as u32);
    loop {
        let n = file.read(&mut chunk).map_err(|_| PCL_ERR_IO)?;
        if n == 0 {
            break;
        }
        murmur.update_unfiltered(&chunk[..n]);
    }

    Ok((murmur.finish(), sha1_digest, file_size))
}

/// Computes CurseForge MurmurHash2 on an in-memory byte slice, skipping ASCII whitespace
/// bytes `[9, 10, 13, 32]`, matching `PCL.Core.Utils.Hash.MurmurHash2Provider`.
///
/// # Safety
/// - `data_ptr` must be valid for reads of `data_len` bytes when `data_len > 0`.
/// - `out_hash` must be a valid non-null writable pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn pcl_native_murmur2_slice(
    data_ptr: *const u8,
    data_len: usize,
    out_hash: *mut u32,
) -> i32 {
    ffi_guard(|| {
        if out_hash.is_null() {
            return PCL_ERR_NULL_PTR;
        }
        // SAFETY: Caller guarantees `data_ptr` is valid for `data_len` bytes.
        let data = match unsafe { slice_from_raw_parts_checked(data_ptr, data_len) } {
            Ok(s) => s,
            Err(code) => return code,
        };
        let hash = murmur2_slice(data);
        // SAFETY: Checked `out_hash` is non-null.
        unsafe {
            *out_hash = hash;
        }
        PCL_OK
    })
}

/// Computes CurseForge MurmurHash2 of a file specified by a UTF-16 path without LOH inflation.
///
/// # Safety
/// - `path_ptr` must be valid for reads of `path_len` UTF-16 code units.
/// - `out_hash` must be a valid non-null writable pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn pcl_native_murmur2_file(
    path_ptr: *const u16,
    path_len: usize,
    out_hash: *mut u32,
) -> i32 {
    ffi_guard(|| {
        if out_hash.is_null() {
            return PCL_ERR_NULL_PTR;
        }
        // SAFETY: Caller guarantees `path_ptr` is valid for `path_len` `u16`s.
        let wide = match unsafe { slice_from_raw_parts_checked(path_ptr, path_len) } {
            Ok(s) => s,
            Err(code) => return code,
        };
        let path = match utf16_to_path_buf(wide) {
            Ok(p) => p,
            Err(code) => return code,
        };
        let mut file = match open_file_shared_read(&path) {
            Ok(f) => f,
            Err(code) => return code,
        };
        let res = FILE_SCRATCH_BUF.with(|cell| {
            let mut scratch = cell.borrow_mut();
            hash_file_with_scratch(&mut file, false, &mut scratch)
        });
        match res {
            Ok((hash, _, _)) => {
                // SAFETY: Checked `out_hash` is non-null.
                unsafe {
                    *out_hash = hash;
                }
                PCL_OK
            }
            Err(code) => code,
        }
    })
}

fn validate_path_spans(text_buf: &[u16], spans: &[PclUtf16Span]) -> Result<(), i32> {
    for sp in spans {
        let off = sp.offset as usize;
        let len = sp.len as usize;
        if len == 0 {
            return Err(PCL_ERR_INVALID_ARG);
        }
        off.checked_add(len)
            .filter(|&end| end <= text_buf.len())
            .ok_or(PCL_ERR_INVALID_ARG)?;
    }
    Ok(())
}

fn run_with_optional_pool<F>(max_threads: u32, f: F) -> Result<(), i32>
where
    F: FnOnce() + Send,
{
    if max_threads == 0 {
        f();
        Ok(())
    } else {
        let threads = (max_threads as usize).clamp(1, 64);
        let pool = rayon::ThreadPoolBuilder::new()
            .num_threads(threads)
            .build()
            .map_err(|_| PCL_ERR_IO)?;
        pool.install(f);
        Ok(())
    }
}

/// Computes CurseForge `MurmurHash2` (`u32`) and `SHA-1` (`[u8; 20]`) in a single parallel
/// disk-read pass over multiple UTF-16 file paths, writing directly into `out_results_ptr`.
///
/// # Safety
/// - `text_buf_ptr` must be valid for `text_buf_len` `u16` reads.
/// - `spans_ptr` must be valid for `file_count` `PclUtf16Span` reads.
/// - `out_results_ptr` must be valid for `file_count` `PclFileHashResult` writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn pcl_native_batch_hash_files(
    text_buf_ptr: *const u16,
    text_buf_len: usize,
    spans_ptr: *const PclUtf16Span,
    file_count: usize,
    max_threads: u32,
    out_results_ptr: *mut PclFileHashResult,
) -> i32 {
    ffi_guard(|| {
        // SAFETY: Caller guarantees valid slices for the given counts.
        let text_buf = match unsafe { slice_from_raw_parts_checked(text_buf_ptr, text_buf_len) } {
            Ok(s) => s,
            Err(code) => return code,
        };
        // SAFETY: Same as above.
        let spans = match unsafe { slice_from_raw_parts_checked(spans_ptr, file_count) } {
            Ok(s) => s,
            Err(code) => return code,
        };
        // SAFETY: Same as above.
        let out_results =
            match unsafe { mut_slice_from_raw_parts_checked(out_results_ptr, file_count) } {
                Ok(s) => s,
                Err(code) => return code,
            };

        if let Err(code) = validate_path_spans(text_buf, spans) {
            return code;
        }
        if file_count == 0 {
            return PCL_OK;
        }

        let res = run_with_optional_pool(max_threads, || {
            out_results
                .par_iter_mut()
                .zip(spans.par_iter())
                .for_each(|(out, sp)| {
                    let wide = &text_buf[sp.offset as usize..(sp.offset + sp.len) as usize];
                    let path = match utf16_to_path_buf(wide) {
                        Ok(p) => p,
                        Err(code) => {
                            *out = PclFileHashResult {
                                status: code,
                                ..Default::default()
                            };
                            return;
                        }
                    };
                    let mut file = match open_file_shared_read(&path) {
                        Ok(f) => f,
                        Err(code) => {
                            *out = PclFileHashResult {
                                status: code,
                                ..Default::default()
                            };
                            return;
                        }
                    };
                    let hashed = FILE_SCRATCH_BUF.with(|cell| {
                        let mut scratch = cell.borrow_mut();
                        hash_file_with_scratch(&mut file, true, &mut scratch)
                    });
                    match hashed {
                        Ok((murmur2, sha1, file_size)) => {
                            *out = PclFileHashResult {
                                status: PCL_OK,
                                murmur2,
                                sha1,
                                file_size,
                            };
                        }
                        Err(code) => {
                            *out = PclFileHashResult {
                                status: code,
                                ..Default::default()
                            };
                        }
                    }
                });
        });

        match res {
            Ok(()) => PCL_OK,
            Err(code) => code,
        }
    })
}

fn detect_loader_and_select_metadata<R: Read + Seek>(
    archive: &mut ZipArchive<R>,
    preferred_loader: u8,
) -> (u8, u8, Vec<u8>, i32) {
    let idx_fabric = archive.index_for_name("fabric.mod.json");
    let idx_quilt = archive.index_for_name("quilt.mod.json");
    let idx_neoforge = archive.index_for_name("META-INF/neoforge.mods.toml");
    let idx_mods_toml = archive.index_for_name("META-INF/mods.toml");
    let idx_mcmod = archive.index_for_name("mcmod.info");

    let has_fabric = idx_fabric.is_some();
    let has_quilt = idx_quilt.is_some();
    let has_neoforge = idx_neoforge.is_some();
    let has_forge = idx_mods_toml.is_some() || idx_mcmod.is_some();

    let detected_loader = if preferred_loader == PCL_LOADER_NEOFORGE && has_neoforge {
        PCL_LOADER_NEOFORGE
    } else if preferred_loader == PCL_LOADER_FORGE && has_forge {
        PCL_LOADER_FORGE
    } else if preferred_loader == PCL_LOADER_FABRIC && has_fabric {
        PCL_LOADER_FABRIC
    } else if preferred_loader == PCL_LOADER_QUILT && has_quilt {
        PCL_LOADER_QUILT
    } else if has_fabric {
        PCL_LOADER_FABRIC
    } else if has_quilt {
        PCL_LOADER_QUILT
    } else if has_neoforge {
        PCL_LOADER_NEOFORGE
    } else if has_forge {
        PCL_LOADER_FORGE
    } else {
        PCL_LOADER_UNKNOWN
    };

    let has_modern = has_fabric || has_quilt || idx_mods_toml.is_some() || has_neoforge;
    let selected: Option<(u8, usize)> = match detected_loader {
        PCL_LOADER_FABRIC => idx_fabric.map(|i| (PCL_META_FABRIC_JSON, i)),
        PCL_LOADER_QUILT => idx_quilt.map(|i| (PCL_META_QUILT_JSON, i)),
        PCL_LOADER_NEOFORGE => idx_neoforge
            .map(|i| (PCL_META_NEOFORGE_MODS_TOML, i))
            .or_else(|| idx_mods_toml.map(|i| (PCL_META_MODS_TOML, i))),
        PCL_LOADER_FORGE => {
            if let Some(i) = idx_mods_toml {
                Some((PCL_META_MODS_TOML, i))
            } else if let Some(i) = idx_neoforge {
                Some((PCL_META_NEOFORGE_MODS_TOML, i))
            } else if !has_modern {
                idx_mcmod.map(|i| (PCL_META_MCMOD_INFO, i))
            } else {
                None
            }
        }
        _ => None,
    };

    let Some((meta_kind, entry_idx)) = selected else {
        return (detected_loader, PCL_META_NONE, Vec::new(), PCL_OK);
    };

    let mut entry = match archive.by_index(entry_idx) {
        Ok(e) => e,
        Err(_) => {
            return (
                detected_loader,
                PCL_META_NONE,
                Vec::new(),
                PCL_ERR_INVALID_FORMAT,
            );
        }
    };
    let cap_hint = (entry.size().min(MAX_METADATA_BYTES)) as usize;
    let mut raw = Vec::with_capacity(cap_hint);
    let mut limited = (&mut entry).take(MAX_METADATA_BYTES + 1);
    if limited.read_to_end(&mut raw).is_err() || raw.len() as u64 > MAX_METADATA_BYTES {
        return (
            detected_loader,
            PCL_META_NONE,
            Vec::new(),
            PCL_ERR_INVALID_FORMAT,
        );
    }

    (detected_loader, meta_kind, raw, PCL_OK)
}

fn scan_single_jar(
    wide_path: &[u16],
    preferred_loader: u8,
    include_hashes: bool,
) -> (PclJarScanItem, Vec<u8>) {
    let path = match utf16_to_path_buf(wide_path) {
        Ok(p) => p,
        Err(code) => {
            return (
                PclJarScanItem {
                    status: code,
                    ..Default::default()
                },
                Vec::new(),
            );
        }
    };
    let mut file = match open_file_shared_read(&path) {
        Ok(f) => f,
        Err(code) => {
            return (
                PclJarScanItem {
                    status: code,
                    ..Default::default()
                },
                Vec::new(),
            );
        }
    };

    let meta_len = file.metadata().map(|m| m.len()).ok();
    if let Some(len) = meta_len
        && len <= IN_MEMORY_READ_LIMIT
    {
        return FILE_SCRATCH_BUF.with(|cell| {
            let mut scratch = cell.borrow_mut();
            scratch.clear();
            scratch.reserve(len as usize);
            if file.read_to_end(&mut scratch).is_err() {
                return (
                    PclJarScanItem {
                        status: PCL_ERR_IO,
                        ..Default::default()
                    },
                    Vec::new(),
                );
            }
            let file_size = scratch.len() as u64;
            let (murmur2, sha1) = if include_hashes {
                let s: [u8; 20] = Sha1::digest(scratch.as_slice()).into();
                let m = murmur2_slice(scratch.as_slice());
                (m, s)
            } else {
                (0, [0u8; 20])
            };

            let cursor = Cursor::new(scratch.as_slice());
            let result = match ZipArchive::new(cursor) {
                Ok(mut archive) => {
                    let (loader, meta_kind, meta_bytes, status) =
                        detect_loader_and_select_metadata(&mut archive, preferred_loader);
                    (
                        PclJarScanItem {
                            status,
                            detected_loader: loader,
                            metadata_kind: meta_kind,
                            _reserved: [0; 2],
                            murmur2,
                            sha1,
                            file_size,
                            metadata_offset: 0,
                            metadata_len: 0,
                        },
                        meta_bytes,
                    )
                }
                Err(_) => (
                    PclJarScanItem {
                        status: PCL_ERR_INVALID_FORMAT,
                        murmur2,
                        sha1,
                        file_size,
                        ..Default::default()
                    },
                    Vec::new(),
                ),
            };

            if scratch.capacity() > (IN_MEMORY_READ_LIMIT as usize) * 2 {
                scratch.shrink_to(4 * 1024 * 1024);
            }
            result
        });
    }

    // Large JAR path (> 16 MiB): stream hashes if needed, then read central directory via BufReader.
    let (murmur2, sha1, file_size) = if include_hashes {
        let hashed = FILE_SCRATCH_BUF.with(|cell| {
            let mut scratch = cell.borrow_mut();
            hash_file_with_scratch(&mut file, true, &mut scratch)
        });
        match hashed {
            Ok(v) => v,
            Err(code) => {
                return (
                    PclJarScanItem {
                        status: code,
                        ..Default::default()
                    },
                    Vec::new(),
                );
            }
        }
    } else {
        (0, [0u8; 20], meta_len.unwrap_or(0))
    };

    if file.seek(SeekFrom::Start(0)).is_err() {
        return (
            PclJarScanItem {
                status: PCL_ERR_IO,
                murmur2,
                sha1,
                file_size,
                ..Default::default()
            },
            Vec::new(),
        );
    }

    match ZipArchive::new(BufReader::new(file)) {
        Ok(mut archive) => {
            let (loader, meta_kind, meta_bytes, status) =
                detect_loader_and_select_metadata(&mut archive, preferred_loader);
            (
                PclJarScanItem {
                    status,
                    detected_loader: loader,
                    metadata_kind: meta_kind,
                    _reserved: [0; 2],
                    murmur2,
                    sha1,
                    file_size,
                    metadata_offset: 0,
                    metadata_len: 0,
                },
                meta_bytes,
            )
        }
        Err(_) => (
            PclJarScanItem {
                status: PCL_ERR_INVALID_FORMAT,
                murmur2,
                sha1,
                file_size,
                ..Default::default()
            },
            Vec::new(),
        ),
    }
}

/// Scans a batch of Mod JAR files in parallel, detecting loaders via ZIP central directory,
/// extracting raw metadata bytes (`fabric.mod.json` / `quilt.mod.json` / `META-INF/mods.toml` /
/// `META-INF/neoforge.mods.toml` / `mcmod.info`), and optionally computing `MurmurHash2` + `SHA-1`
/// in the same disk-read pass. Must be freed via `pcl_native_free_jar_scan_results`.
///
/// # Safety
/// - `text_buf_ptr` must be valid for `text_buf_len` `u16` reads.
/// - `spans_ptr` must be valid for `file_count` `PclUtf16Span` reads.
/// - `out_batch` must be a valid non-null writable pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn pcl_native_batch_scan_jars(
    text_buf_ptr: *const u16,
    text_buf_len: usize,
    spans_ptr: *const PclUtf16Span,
    file_count: usize,
    preferred_loader: u8,
    include_hashes: u8,
    max_threads: u32,
    out_batch: *mut *mut PclJarScanBatch,
) -> i32 {
    ffi_guard(|| {
        if out_batch.is_null() {
            return PCL_ERR_NULL_PTR;
        }
        // SAFETY: Checked `out_batch` is non-null.
        unsafe {
            *out_batch = std::ptr::null_mut();
        }
        if preferred_loader > PCL_LOADER_NEOFORGE || include_hashes > 1 {
            return PCL_ERR_INVALID_ARG;
        }

        // SAFETY: Caller guarantees valid slices for the given counts.
        let text_buf = match unsafe { slice_from_raw_parts_checked(text_buf_ptr, text_buf_len) } {
            Ok(s) => s,
            Err(code) => return code,
        };
        // SAFETY: Same as above.
        let spans = match unsafe { slice_from_raw_parts_checked(spans_ptr, file_count) } {
            Ok(s) => s,
            Err(code) => return code,
        };
        if let Err(code) = validate_path_spans(text_buf, spans) {
            return code;
        }

        let hashes = include_hashes != 0;
        let mut scanned: Vec<(PclJarScanItem, Vec<u8>)> = Vec::new();
        let pool_res = run_with_optional_pool(max_threads, || {
            scanned = spans
                .par_iter()
                .map(|sp| {
                    let wide = &text_buf[sp.offset as usize..(sp.offset + sp.len) as usize];
                    scan_single_jar(wide, preferred_loader, hashes)
                })
                .collect();
        });
        if let Err(code) = pool_res {
            return code;
        }

        let total_data_len: usize = scanned.iter().map(|(_, b)| b.len()).sum();
        if total_data_len > u32::MAX as usize {
            return PCL_ERR_INVALID_ARG;
        }

        let mut items = Vec::with_capacity(scanned.len());
        let mut arena = Vec::with_capacity(total_data_len);
        for (mut item, bytes) in scanned {
            item.metadata_offset = arena.len() as u32;
            item.metadata_len = bytes.len() as u32;
            arena.extend_from_slice(&bytes);
            items.push(item);
        }

        let boxed_items = items.into_boxed_slice();
        let item_count = boxed_items.len();
        let items_ptr = Box::into_raw(boxed_items) as *const PclJarScanItem;

        let boxed_data = arena.into_boxed_slice();
        let data_len = boxed_data.len();
        let data_ptr = Box::into_raw(boxed_data) as *const u8;

        let batch = Box::new(PclJarScanBatch {
            items_ptr,
            item_count,
            data_ptr,
            data_len,
        });
        // SAFETY: `out_batch` is non-null and writable. Ownership transfers to caller.
        unsafe {
            *out_batch = Box::into_raw(batch);
        }
        PCL_OK
    })
}

/// Frees a `PclJarScanBatch` allocated by `pcl_native_batch_scan_jars`. Null is a no-op.
///
/// # Safety
/// - `batch` must either be null or a pointer returned by `pcl_native_batch_scan_jars`
///   that has not yet been freed and is not concurrently accessed.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn pcl_native_free_jar_scan_results(batch: *mut PclJarScanBatch) -> i32 {
    ffi_guard(|| {
        if !batch.is_null() {
            // SAFETY: `batch` and its inner boxed slices were created via `Box::into_raw`
            // in `pcl_native_batch_scan_jars` and are not accessed after this call.
            unsafe {
                let b = Box::from_raw(batch);
                let _items = Box::from_raw(std::ptr::slice_from_raw_parts_mut(
                    b.items_ptr.cast_mut(),
                    b.item_count,
                ));
                let _data = Box::from_raw(std::ptr::slice_from_raw_parts_mut(
                    b.data_ptr.cast_mut(),
                    b.data_len,
                ));
            }
        }
        PCL_OK
    })
}

#[cfg(test)]
mod tests {
    use std::io::Write;
    use std::time::{SystemTime, UNIX_EPOCH};

    use zip::ZipWriter;
    use zip::write::SimpleFileOptions;

    use super::*;

    /// Exact port of `PCL.Core.Utils.Hash.MurmurHash2Provider` for equivalence tests.
    fn ref_murmur2_cs(data: &[u8]) -> u32 {
        let filtered: Vec<u8> = data
            .iter()
            .copied()
            .filter(|&b| b != 9 && b != 10 && b != 13 && b != 32)
            .collect();
        let mut h = 1_u32 ^ (filtered.len() as u32);
        let mut i = 0_usize;
        while i + 4 <= filtered.len() {
            let mut k = (filtered[i] as u32)
                | ((filtered[i + 1] as u32) << 8)
                | ((filtered[i + 2] as u32) << 16)
                | ((filtered[i + 3] as u32) << 24);
            k = k.wrapping_mul(0x5BD1_E995);
            k ^= k >> 24;
            k = k.wrapping_mul(0x5BD1_E995);
            h = h.wrapping_mul(0x5BD1_E995);
            h ^= k;
            i += 4;
        }
        match filtered.len() - i {
            3 => {
                h ^= (filtered[i] as u32) | ((filtered[i + 1] as u32) << 8);
                h ^= (filtered[i + 2] as u32) << 16;
                h = h.wrapping_mul(0x5BD1_E995);
            }
            2 => {
                h ^= (filtered[i] as u32) | ((filtered[i + 1] as u32) << 8);
                h = h.wrapping_mul(0x5BD1_E995);
            }
            1 => {
                h ^= filtered[i] as u32;
                h = h.wrapping_mul(0x5BD1_E995);
            }
            _ => {}
        }
        h ^= h >> 13;
        h = h.wrapping_mul(0x5BD1_E995);
        h ^= h >> 15;
        h
    }

    struct TempDirGuard {
        path: PathBuf,
    }

    impl TempDirGuard {
        fn new(tag: &str) -> Self {
            let nanos = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos();
            let dir = Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("target")
                .join("test_tmp")
                .join(format!("pcl_native_{tag}_{}_{}", std::process::id(), nanos));
            std::fs::create_dir_all(&dir).unwrap();
            Self { path: dir }
        }
    }

    impl Drop for TempDirGuard {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.path);
        }
    }

    #[test]
    fn murmur2_slice_matches_reference_across_lengths_and_whitespace() {
        let samples: &[&[u8]] = &[
            b"",
            b" \t\r\n ",
            b"a",
            b"ab",
            b"abc",
            b"abcd",
            b"abcde",
            b" a\tb\rc\nd\t e ",
        ];
        for &s in samples {
            let mut out = 0u32;
            // SAFETY: Valid slice pointer and output pointer.
            let code = unsafe { pcl_native_murmur2_slice(s.as_ptr(), s.len(), &mut out) };
            assert_eq!(code, PCL_OK);
            assert_eq!(out, ref_murmur2_cs(s), "failed for {s:?}");
        }

        // Larger synthetic payload spanning multiple 4 KiB chunks with interleaved whitespace
        let big: Vec<u8> = (0..13_107)
            .map(|i| match i % 17 {
                0 => b'\t',
                1 => b'\n',
                2 => b'\r',
                3 => b' ',
                _ => ((i * 31 + 7) & 0xFF) as u8,
            })
            .collect();
        let mut out = 0u32;
        // SAFETY: Valid slice pointer, zero-len empty pointer, and output pointer.
        unsafe {
            assert_eq!(
                pcl_native_murmur2_slice(big.as_ptr(), big.len(), &mut out),
                PCL_OK
            );
            assert_eq!(out, ref_murmur2_cs(&big));

            assert_eq!(
                pcl_native_murmur2_slice(std::ptr::null(), 0, &mut out),
                PCL_OK
            );
            assert_eq!(out, ref_murmur2_cs(&[]));
        }
    }

    #[test]
    fn batch_hash_and_jar_scan_with_utf16_chinese_and_emoji_paths() {
        let tmp = TempDirGuard::new("jar_scan");
        let sub = tmp.path.join("模组目录_😀");
        std::fs::create_dir_all(&sub).unwrap();

        let fabric_path = sub.join("fabric-mod-示例.jar");
        let forge_legacy_path = sub.join("legacy-forge-😀.jar");
        let neoforge_path = sub.join("neoforge-mod.jar");
        let missing_path = sub.join("does-not-exist.jar");

        let fabric_json = br#"{"schemaVersion":1,"id":"create_test","name":"Create"}"#;
        let mcmod_json = br#"[{"modid":"legacy","name":"Legacy Mod"}]"#;
        let neoforge_toml = br#"modLoader="javafml"
[[mods]]
modId="neo_test""#;

        {
            let f = File::create(&fabric_path).unwrap();
            let mut zw = ZipWriter::new(f);
            zw.start_file("fabric.mod.json", SimpleFileOptions::default())
                .unwrap();
            zw.write_all(fabric_json).unwrap();
            zw.start_file("mcmod.info", SimpleFileOptions::default())
                .unwrap();
            zw.write_all(mcmod_json).unwrap();
            zw.finish().unwrap();
        }
        {
            let f = File::create(&forge_legacy_path).unwrap();
            let mut zw = ZipWriter::new(f);
            zw.start_file("mcmod.info", SimpleFileOptions::default())
                .unwrap();
            zw.write_all(mcmod_json).unwrap();
            zw.finish().unwrap();
        }
        {
            let f = File::create(&neoforge_path).unwrap();
            let mut zw = ZipWriter::new(f);
            zw.start_file("META-INF/neoforge.mods.toml", SimpleFileOptions::default())
                .unwrap();
            zw.write_all(neoforge_toml).unwrap();
            zw.finish().unwrap();
        }

        let f_w: Vec<u16> = fabric_path.to_string_lossy().encode_utf16().collect();
        let mut single_m2 = 0u32;
        let fabric_bytes = std::fs::read(&fabric_path).unwrap();

        let paths = [
            &fabric_path,
            &forge_legacy_path,
            &neoforge_path,
            &missing_path,
        ];
        let mut text_buf = Vec::new();
        let mut spans = Vec::new();
        for p in paths {
            let off = text_buf.len() as u32;
            let u16s: Vec<u16> = p.to_string_lossy().encode_utf16().collect();
            text_buf.extend_from_slice(&u16s);
            spans.push(PclUtf16Span {
                offset: off,
                len: u16s.len() as u32,
            });
        }

        let mut hash_results = vec![PclFileHashResult::default(); paths.len()];
        let mut batch_ptr = std::ptr::null_mut();

        // SAFETY: Valid UTF-16 buffers, path spans, output slice, and batch pointer.
        unsafe {
            assert_eq!(
                pcl_native_murmur2_file(f_w.as_ptr(), f_w.len(), &mut single_m2),
                PCL_OK
            );
            assert_eq!(single_m2, ref_murmur2_cs(&fabric_bytes));

            assert_eq!(
                pcl_native_batch_hash_files(
                    text_buf.as_ptr(),
                    text_buf.len(),
                    spans.as_ptr(),
                    spans.len(),
                    2,
                    hash_results.as_mut_ptr(),
                ),
                PCL_OK
            );
            assert_eq!(hash_results[0].status, PCL_OK);
            assert_eq!(hash_results[0].murmur2, single_m2);
            let exp_sha1: [u8; 20] = Sha1::digest(&fabric_bytes).into();
            assert_eq!(hash_results[0].sha1, exp_sha1);
            assert_eq!(hash_results[0].file_size, fabric_bytes.len() as u64);
            assert_eq!(hash_results[3].status, PCL_ERR_IO);

            assert_eq!(
                pcl_native_batch_scan_jars(
                    text_buf.as_ptr(),
                    text_buf.len(),
                    spans.as_ptr(),
                    spans.len(),
                    PCL_LOADER_UNKNOWN,
                    1,
                    0,
                    &mut batch_ptr,
                ),
                PCL_OK
            );
            assert!(!batch_ptr.is_null());

            let batch = &*batch_ptr;
            assert_eq!(batch.item_count, 4);
            let items = std::slice::from_raw_parts(batch.items_ptr, batch.item_count);
            let arena = std::slice::from_raw_parts(batch.data_ptr, batch.data_len);

            assert_eq!(items[0].status, PCL_OK);
            assert_eq!(items[0].detected_loader, PCL_LOADER_FABRIC);
            assert_eq!(items[0].metadata_kind, PCL_META_FABRIC_JSON);
            assert_eq!(items[0].murmur2, single_m2);
            assert_eq!(items[0].sha1, exp_sha1);
            assert_eq!(
                &arena[items[0].metadata_offset as usize
                    ..(items[0].metadata_offset + items[0].metadata_len) as usize],
                fabric_json
            );

            assert_eq!(items[1].status, PCL_OK);
            assert_eq!(items[1].detected_loader, PCL_LOADER_FORGE);
            assert_eq!(items[1].metadata_kind, PCL_META_MCMOD_INFO);
            assert_eq!(
                &arena[items[1].metadata_offset as usize
                    ..(items[1].metadata_offset + items[1].metadata_len) as usize],
                mcmod_json
            );

            assert_eq!(items[2].status, PCL_OK);
            assert_eq!(items[2].detected_loader, PCL_LOADER_NEOFORGE);
            assert_eq!(items[2].metadata_kind, PCL_META_NEOFORGE_MODS_TOML);
            assert_eq!(
                &arena[items[2].metadata_offset as usize
                    ..(items[2].metadata_offset + items[2].metadata_len) as usize],
                neoforge_toml
            );

            assert_eq!(items[3].status, PCL_ERR_IO);

            assert_eq!(pcl_native_free_jar_scan_results(batch_ptr), PCL_OK);
            assert_eq!(
                pcl_native_free_jar_scan_results(std::ptr::null_mut()),
                PCL_OK
            );
        }
    }

    #[test]
    fn hash_scan_rejects_null_and_bad_args() {
        let mut h = 0u32;
        let mut batch_ptr = std::ptr::null_mut();
        // SAFETY: Testing null/invalid argument checks at FFI boundary.
        unsafe {
            assert_eq!(
                pcl_native_murmur2_slice(std::ptr::null(), 4, &mut h),
                PCL_ERR_NULL_PTR
            );
            assert_eq!(
                pcl_native_murmur2_slice(b"abc".as_ptr(), 3, std::ptr::null_mut()),
                PCL_ERR_NULL_PTR
            );
            assert_eq!(
                pcl_native_murmur2_file(std::ptr::null(), 0, &mut h),
                PCL_ERR_INVALID_ARG
            );
            assert_eq!(
                pcl_native_batch_scan_jars(
                    std::ptr::null(),
                    0,
                    std::ptr::null(),
                    0,
                    99,
                    0,
                    0,
                    &mut batch_ptr,
                ),
                PCL_ERR_INVALID_ARG
            );
            assert_eq!(
                pcl_native_batch_scan_jars(
                    std::ptr::null(),
                    0,
                    std::ptr::null(),
                    0,
                    0,
                    0,
                    0,
                    std::ptr::null_mut(),
                ),
                PCL_ERR_NULL_PTR
            );
        }
    }

    #[test]
    fn jar_scan_respects_preferred_loader_handles_corrupt_zip_and_streaming_large_file() {
        let tmp = TempDirGuard::new("loader_and_large");
        let dual_jar = tmp.path.join("dual-fabric-neoforge.jar");
        let mods_and_mcmod_jar = tmp.path.join("forge-both.jar");
        let corrupt_jar = tmp.path.join("corrupt.jar");
        let large_file = tmp.path.join("large-17mib.bin");

        {
            let f = File::create(&dual_jar).unwrap();
            let mut zw = ZipWriter::new(f);
            zw.start_file("fabric.mod.json", SimpleFileOptions::default())
                .unwrap();
            zw.write_all(br#"{"id":"dual_fabric"}"#).unwrap();
            zw.start_file("META-INF/neoforge.mods.toml", SimpleFileOptions::default())
                .unwrap();
            zw.write_all(br#"modId="dual_neoforge""#).unwrap();
            zw.finish().unwrap();
        }
        {
            let f = File::create(&mods_and_mcmod_jar).unwrap();
            let mut zw = ZipWriter::new(f);
            zw.start_file("META-INF/mods.toml", SimpleFileOptions::default())
                .unwrap();
            zw.write_all(br#"modId="modern_forge""#).unwrap();
            zw.start_file("mcmod.info", SimpleFileOptions::default())
                .unwrap();
            zw.write_all(br#"[{"modid":"legacy"}]"#).unwrap();
            zw.finish().unwrap();
        }
        std::fs::write(&corrupt_jar, b"not a zip archive at all \t\r\n ").unwrap();

        let large_len = 17 * 1024 * 1024 + 13;
        let large_bytes: Vec<u8> = (0..large_len)
            .map(|i| match i % 23 {
                0 => b' ',
                1 => b'\n',
                _ => ((i * 17 + 3) & 0xFF) as u8,
            })
            .collect();
        std::fs::write(&large_file, &large_bytes).unwrap();

        let large_w: Vec<u16> = large_file.to_string_lossy().encode_utf16().collect();
        let mut streamed_m2 = 0u32;
        let mut slice_m2 = 0u32;

        let mut text_buf = Vec::new();
        let mut spans = Vec::new();
        for p in [&dual_jar, &mods_and_mcmod_jar, &corrupt_jar, &large_file] {
            let off = text_buf.len() as u32;
            let u16s: Vec<u16> = p.to_string_lossy().encode_utf16().collect();
            text_buf.extend_from_slice(&u16s);
            spans.push(PclUtf16Span {
                offset: off,
                len: u16s.len() as u32,
            });
        }

        let mut hash_out = vec![PclFileHashResult::default(); 4];
        let mut batch_ptr = std::ptr::null_mut();

        // SAFETY: Valid path strings, buffers, and output pointers.
        unsafe {
            assert_eq!(
                pcl_native_murmur2_file(large_w.as_ptr(), large_w.len(), &mut streamed_m2),
                PCL_OK
            );
            assert_eq!(
                pcl_native_murmur2_slice(large_bytes.as_ptr(), large_bytes.len(), &mut slice_m2),
                PCL_OK
            );
            assert_eq!(streamed_m2, slice_m2);

            assert_eq!(
                pcl_native_batch_hash_files(
                    text_buf.as_ptr(),
                    text_buf.len(),
                    spans.as_ptr(),
                    spans.len(),
                    0,
                    hash_out.as_mut_ptr(),
                ),
                PCL_OK
            );
            assert_eq!(hash_out[3].status, PCL_OK);
            assert_eq!(hash_out[3].murmur2, slice_m2);
            let exp_large_sha1: [u8; 20] = Sha1::digest(&large_bytes).into();
            assert_eq!(hash_out[3].sha1, exp_large_sha1);
            assert_eq!(hash_out[3].file_size, large_bytes.len() as u64);

            assert_eq!(
                pcl_native_batch_scan_jars(
                    text_buf.as_ptr(),
                    text_buf.len(),
                    spans.as_ptr(),
                    3,
                    PCL_LOADER_NEOFORGE,
                    1,
                    0,
                    &mut batch_ptr,
                ),
                PCL_OK
            );
            let batch = &*batch_ptr;
            let items = std::slice::from_raw_parts(batch.items_ptr, batch.item_count);
            let arena = std::slice::from_raw_parts(batch.data_ptr, batch.data_len);

            assert_eq!(items[0].status, PCL_OK);
            assert_eq!(items[0].detected_loader, PCL_LOADER_NEOFORGE);
            assert_eq!(items[0].metadata_kind, PCL_META_NEOFORGE_MODS_TOML);

            assert_eq!(items[1].status, PCL_OK);
            assert_eq!(items[1].detected_loader, PCL_LOADER_FORGE);
            assert_eq!(items[1].metadata_kind, PCL_META_MODS_TOML);
            assert_eq!(
                &arena[items[1].metadata_offset as usize
                    ..(items[1].metadata_offset + items[1].metadata_len) as usize],
                br#"modId="modern_forge""#
            );

            assert_eq!(items[2].status, PCL_ERR_INVALID_FORMAT);
            assert_eq!(
                items[2].murmur2,
                ref_murmur2_cs(b"not a zip archive at all \t\r\n ")
            );
            let exp_corrupt_sha1: [u8; 20] =
                Sha1::digest(b"not a zip archive at all \t\r\n ").into();
            assert_eq!(items[2].sha1, exp_corrupt_sha1);

            assert_eq!(pcl_native_free_jar_scan_results(batch_ptr), PCL_OK);
        }
    }
}
