use std::io::Read;

use bzip2::read::BzDecoder;

use crate::{
    PCL_ERR_BUFFER_TOO_SMALL, PCL_ERR_INVALID_ARG, PCL_ERR_INVALID_FORMAT, PCL_ERR_NULL_PTR,
    PCL_OK, ffi_guard, mut_slice_from_raw_parts_checked, ranges_overlap,
    slice_from_raw_parts_checked,
};

const BSDIFF_HEADER_SIZE: usize = 32;
const BSDIFF_MAGIC: &[u8; 8] = b"BSDIFF40";

struct BsDiffHeader {
    ctrl_len: usize,
    diff_block_len: usize,
    new_len: usize,
}

fn parse_bsdiff_header(diff: &[u8]) -> Result<BsDiffHeader, i32> {
    if diff.len() < BSDIFF_HEADER_SIZE {
        return Err(PCL_ERR_INVALID_FORMAT);
    }
    if &diff[0..8] != BSDIFF_MAGIC {
        return Err(PCL_ERR_INVALID_FORMAT);
    }

    let ctrl_len_i64 =
        i64::from_le_bytes(diff[8..16].try_into().map_err(|_| PCL_ERR_INVALID_FORMAT)?);
    let diff_len_i64 = i64::from_le_bytes(
        diff[16..24]
            .try_into()
            .map_err(|_| PCL_ERR_INVALID_FORMAT)?,
    );
    let new_len_i64 = i64::from_le_bytes(
        diff[24..32]
            .try_into()
            .map_err(|_| PCL_ERR_INVALID_FORMAT)?,
    );

    if ctrl_len_i64 < 0 || diff_len_i64 < 0 || new_len_i64 < 0 {
        return Err(PCL_ERR_INVALID_FORMAT);
    }

    let header_and_blocks =
        (BSDIFF_HEADER_SIZE as i128) + (ctrl_len_i64 as i128) + (diff_len_i64 as i128);
    if header_and_blocks > diff.len() as i128 {
        return Err(PCL_ERR_INVALID_FORMAT);
    }

    let ctrl_len = usize::try_from(ctrl_len_i64).map_err(|_| PCL_ERR_INVALID_ARG)?;
    let diff_block_len = usize::try_from(diff_len_i64).map_err(|_| PCL_ERR_INVALID_ARG)?;
    let new_len = usize::try_from(new_len_i64)
        .ok()
        .filter(|&n| n <= isize::MAX as usize)
        .ok_or(PCL_ERR_INVALID_ARG)?;

    Ok(BsDiffHeader {
        ctrl_len,
        diff_block_len,
        new_len,
    })
}

/// Decodes a bsdiff signed-magnitude 64-bit integer matching `BsDiff.ReadInt64`.
#[inline]
pub(crate) fn decode_bsdiff_int64(bytes: [u8; 8]) -> i64 {
    let v = i64::from_le_bytes(bytes);
    let mask = v >> 63;
    (!mask & v) | (((v & i64::MIN).wrapping_sub(v)) & mask)
}

/// Validates a `BSDIFF40` patch header and returns the uncompressed target byte length (`new_len`).
///
/// # Safety
/// - `diff_ptr` must be valid for reads of `diff_len` bytes (when `diff_len > 0`).
/// - `out_new_len` must be a valid non-null writable pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn pcl_native_bsdiff_inspect(
    diff_ptr: *const u8,
    diff_len: usize,
    out_new_len: *mut u64,
) -> i32 {
    ffi_guard(|| {
        if out_new_len.is_null() {
            return PCL_ERR_NULL_PTR;
        }
        // SAFETY: Caller guarantees `diff_ptr` is valid for `diff_len` bytes.
        let diff = match unsafe { slice_from_raw_parts_checked(diff_ptr, diff_len) } {
            Ok(s) => s,
            Err(code) => return code,
        };
        let header = match parse_bsdiff_header(diff) {
            Ok(h) => h,
            Err(code) => return code,
        };
        // SAFETY: Checked `out_new_len` is non-null.
        unsafe {
            *out_new_len = header.new_len as u64;
        }
        PCL_OK
    })
}

/// Applies a `BSDIFF40` binary patch to `origin` and writes the patched bytes directly into
/// caller-allocated `out_ptr`, matching `PCL.Core.Utils.Diff.BsDiff.ApplyAsync`.
///
/// # Safety
/// - `origin_ptr` must be valid for reads of `origin_len` bytes (when `origin_len > 0`).
/// - `diff_ptr` must be valid for reads of `diff_len` bytes (when `diff_len > 0`).
/// - `out_ptr` must be valid for writes of `out_cap` bytes (when `out_cap > 0`) and must not
///   overlap with `origin_ptr` or `diff_ptr`.
/// - `out_written_len`, if non-null, must be a valid writable pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn pcl_native_bsdiff_apply(
    origin_ptr: *const u8,
    origin_len: usize,
    diff_ptr: *const u8,
    diff_len: usize,
    out_ptr: *mut u8,
    out_cap: usize,
    out_written_len: *mut u64,
) -> i32 {
    ffi_guard(|| {
        // SAFETY: Caller guarantees valid slices for the respective pointer/length pairs.
        let origin = match unsafe { slice_from_raw_parts_checked(origin_ptr, origin_len) } {
            Ok(s) => s,
            Err(code) => return code,
        };
        // SAFETY: Caller guarantees `diff_ptr` is valid for `diff_len` bytes.
        let diff = match unsafe { slice_from_raw_parts_checked(diff_ptr, diff_len) } {
            Ok(s) => s,
            Err(code) => return code,
        };
        let header = match parse_bsdiff_header(diff) {
            Ok(h) => h,
            Err(code) => return code,
        };
        if out_cap < header.new_len {
            return PCL_ERR_BUFFER_TOO_SMALL;
        }
        // SAFETY: Caller guarantees `out_ptr` is valid for writes of `out_cap` bytes.
        let out_full = match unsafe { mut_slice_from_raw_parts_checked(out_ptr, out_cap) } {
            Ok(s) => s,
            Err(code) => return code,
        };
        if ranges_overlap(out_ptr, header.new_len, origin_ptr, origin_len)
            || ranges_overlap(out_ptr, header.new_len, diff_ptr, diff_len)
        {
            return PCL_ERR_INVALID_ARG;
        }

        let out = &mut out_full[..header.new_len];
        if let Err(code) = bsdiff_apply_parsed(origin, diff, &header, out) {
            return code;
        }

        if !out_written_len.is_null() {
            // SAFETY: Checked `out_written_len` is non-null.
            unsafe {
                *out_written_len = header.new_len as u64;
            }
        }
        PCL_OK
    })
}

fn bsdiff_apply_parsed(
    origin: &[u8],
    diff: &[u8],
    header: &BsDiffHeader,
    out: &mut [u8],
) -> Result<(), i32> {
    let ctrl_start = BSDIFF_HEADER_SIZE;
    let diff_start = ctrl_start + header.ctrl_len;
    let extra_start = diff_start + header.diff_block_len;

    let ctrl_slice = &diff[ctrl_start..diff_start];
    let diff_slice = &diff[diff_start..extra_start];
    let extra_slice = &diff[extra_start..];

    let mut ctrl_decoder = BzDecoder::new(ctrl_slice);
    let mut diff_decoder = BzDecoder::new(diff_slice);
    let mut extra_decoder = BzDecoder::new(extra_slice);

    let origin_len_i64 = i64::try_from(origin.len()).map_err(|_| PCL_ERR_INVALID_ARG)?;
    let mut new_pos: usize = 0;
    let mut old_pos: i64 = 0;
    let mut ctrl_buf = [0u8; 24];

    while new_pos < header.new_len {
        ctrl_decoder
            .read_exact(&mut ctrl_buf)
            .map_err(|_| PCL_ERR_INVALID_FORMAT)?;

        let add_range_i64 = decode_bsdiff_int64(ctrl_buf[0..8].try_into().unwrap());
        let copy_range_i64 = decode_bsdiff_int64(ctrl_buf[8..16].try_into().unwrap());
        let seek_pos = decode_bsdiff_int64(ctrl_buf[16..24].try_into().unwrap());

        if add_range_i64 < 0 || copy_range_i64 < 0 {
            return Err(PCL_ERR_INVALID_FORMAT);
        }
        let add_range = usize::try_from(add_range_i64).map_err(|_| PCL_ERR_INVALID_FORMAT)?;
        let copy_range = usize::try_from(copy_range_i64).map_err(|_| PCL_ERR_INVALID_FORMAT)?;

        let after_add = new_pos
            .checked_add(add_range)
            .filter(|&p| p <= header.new_len)
            .ok_or(PCL_ERR_INVALID_FORMAT)?;

        if add_range > 0 {
            if old_pos < 0 {
                return Err(PCL_ERR_INVALID_FORMAT);
            }
            diff_decoder
                .read_exact(&mut out[new_pos..after_add])
                .map_err(|_| PCL_ERR_INVALID_FORMAT)?;

            let old_u = old_pos as usize;
            if old_u < origin.len() {
                let avail = (origin.len() - old_u).min(add_range);
                let dst_slice = &mut out[new_pos..new_pos + avail];
                let orig_slice = &origin[old_u..old_u + avail];
                for (dst_b, &orig_b) in dst_slice.iter_mut().zip(orig_slice.iter()) {
                    *dst_b = dst_b.wrapping_add(orig_b);
                }
            }
        }

        new_pos = after_add;
        old_pos = old_pos
            .checked_add(add_range_i64)
            .ok_or(PCL_ERR_INVALID_FORMAT)?;

        let after_copy = new_pos
            .checked_add(copy_range)
            .filter(|&p| p <= header.new_len)
            .ok_or(PCL_ERR_INVALID_FORMAT)?;

        if copy_range > 0 {
            extra_decoder
                .read_exact(&mut out[new_pos..after_copy])
                .map_err(|_| PCL_ERR_INVALID_FORMAT)?;
        }

        new_pos = after_copy;
        old_pos = old_pos
            .checked_add(seek_pos)
            .ok_or(PCL_ERR_INVALID_FORMAT)?;
        if old_pos > origin_len_i64 {
            return Err(PCL_ERR_INVALID_FORMAT);
        }
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use std::io::Write;

    use bzip2::Compression;
    use bzip2::write::BzEncoder;

    use super::*;

    fn encode_bsdiff_int64(val: i64) -> [u8; 8] {
        if val < 0 {
            let abs = val.unsigned_abs();
            (abs | 0x8000_0000_0000_0000).to_le_bytes()
        } else {
            (val as u64).to_le_bytes()
        }
    }

    fn bz2_compress(data: &[u8]) -> Vec<u8> {
        let mut enc = BzEncoder::new(Vec::new(), Compression::fast());
        enc.write_all(data).unwrap();
        enc.finish().unwrap()
    }

    fn build_synthetic_patch(
        triples: &[(i64, i64, i64)],
        diff_raw: &[u8],
        extra_raw: &[u8],
        new_len: i64,
    ) -> Vec<u8> {
        let mut ctrl_raw = Vec::with_capacity(triples.len() * 24);
        for &(a, c, s) in triples {
            ctrl_raw.extend_from_slice(&encode_bsdiff_int64(a));
            ctrl_raw.extend_from_slice(&encode_bsdiff_int64(c));
            ctrl_raw.extend_from_slice(&encode_bsdiff_int64(s));
        }
        let ctrl_bz = bz2_compress(&ctrl_raw);
        let diff_bz = bz2_compress(diff_raw);
        let extra_bz = bz2_compress(extra_raw);

        let mut patch = Vec::new();
        patch.extend_from_slice(BSDIFF_MAGIC);
        patch.extend_from_slice(&(ctrl_bz.len() as i64).to_le_bytes());
        patch.extend_from_slice(&(diff_bz.len() as i64).to_le_bytes());
        patch.extend_from_slice(&new_len.to_le_bytes());
        patch.extend_from_slice(&ctrl_bz);
        patch.extend_from_slice(&diff_bz);
        patch.extend_from_slice(&extra_bz);
        patch
    }

    #[test]
    fn read_int64_handles_positive_negative_and_neg_zero() {
        assert_eq!(decode_bsdiff_int64(encode_bsdiff_int64(0)), 0);
        assert_eq!(
            decode_bsdiff_int64(encode_bsdiff_int64(123456789)),
            123456789
        );
        assert_eq!(decode_bsdiff_int64(encode_bsdiff_int64(-1)), -1);
        assert_eq!(
            decode_bsdiff_int64(encode_bsdiff_int64(-9876543210)),
            -9876543210
        );
        // Signed-magnitude -0 (0x8000_0000_0000_0000) decodes to 0
        assert_eq!(
            decode_bsdiff_int64(0x8000_0000_0000_0000_u64.to_le_bytes()),
            0
        );
    }

    #[test]
    fn bsdiff_inspect_and_apply_with_negative_seek_and_tail_beyond_origin() {
        let origin = b"0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        // Step 1: add 6 bytes at old=0 ("012345" + diff -> "AAAAAA"), copy 2 ("!!"), seek +10 -> old=16 ('G')
        // Step 2: add 4 bytes at old=16 ("GHIJ" + diff -> "ghij"), copy 1 ("?"), seek -15 -> old=5 ('5')
        // Step 3: add 5 bytes at old=5 ("56789" + diff), copy 0, seek +24 -> old=34 ('Y')
        // Step 4: add 4 bytes at old=34 (2 bytes from origin[34..36] + 2 bytes past origin len -> raw diff bytes), copy 2 ("##"), seek -3 -> old=35 (<= 36)
        let diff1: Vec<u8> = origin[0..6].iter().map(|&b| b'A'.wrapping_sub(b)).collect();
        let mut diff2_exact = Vec::new();
        for (idx, &b) in origin[16..20].iter().enumerate() {
            diff2_exact.push((b'g' + idx as u8).wrapping_sub(b));
        }
        let diff3: Vec<u8> = origin[5..10]
            .iter()
            .map(|&b| (b ^ 0x01).wrapping_sub(b))
            .collect();
        let mut diff4 = vec![b'W'.wrapping_sub(origin[34]), b'X'.wrapping_sub(origin[35])];
        diff4.extend_from_slice(b"YZ");

        let mut diff_raw = Vec::new();
        diff_raw.extend_from_slice(&diff1);
        diff_raw.extend_from_slice(&diff2_exact);
        diff_raw.extend_from_slice(&diff3);
        diff_raw.extend_from_slice(&diff4);

        let extra_raw = b"!!?##";
        let triples = [(6, 2, 10), (4, 1, -15), (5, 0, 24), (4, 2, -3)];
        let expected_new: Vec<u8> = [
            &b"AAAAAA!!"[..],
            &b"ghij?"[..],
            &origin[5..10].iter().map(|&b| b ^ 0x01).collect::<Vec<u8>>()[..],
            &b"WXYZ##"[..],
        ]
        .concat();

        let patch =
            build_synthetic_patch(&triples, &diff_raw, extra_raw, expected_new.len() as i64);

        let mut inspected_len = 0u64;
        let mut tiny = vec![0u8; expected_new.len() - 1];
        let mut out = vec![0u8; expected_new.len()];
        let mut written = 0u64;
        // SAFETY: All slice pointers and output pointers are valid for their lengths.
        unsafe {
            assert_eq!(
                pcl_native_bsdiff_inspect(patch.as_ptr(), patch.len(), &mut inspected_len),
                PCL_OK
            );
            assert_eq!(inspected_len as usize, expected_new.len());

            assert_eq!(
                pcl_native_bsdiff_apply(
                    origin.as_ptr(),
                    origin.len(),
                    patch.as_ptr(),
                    patch.len(),
                    tiny.as_mut_ptr(),
                    tiny.len(),
                    std::ptr::null_mut(),
                ),
                PCL_ERR_BUFFER_TOO_SMALL
            );

            assert_eq!(
                pcl_native_bsdiff_apply(
                    origin.as_ptr(),
                    origin.len(),
                    patch.as_ptr(),
                    patch.len(),
                    out.as_mut_ptr(),
                    out.len(),
                    &mut written,
                ),
                PCL_OK
            );
        }
        assert_eq!(written as usize, expected_new.len());
        assert_eq!(out, expected_new);
    }

    #[test]
    fn bsdiff_handles_empty_output_and_empty_origin() {
        let patch = build_synthetic_patch(&[], &[], &[], 0);
        let mut len = 99u64;
        let mut written = 99u64;
        // SAFETY: Valid patch slice, output pointers, and zero-length empty origin/out pointers.
        unsafe {
            assert_eq!(
                pcl_native_bsdiff_inspect(patch.as_ptr(), patch.len(), &mut len),
                PCL_OK
            );
            assert_eq!(len, 0);

            assert_eq!(
                pcl_native_bsdiff_apply(
                    std::ptr::null(),
                    0,
                    patch.as_ptr(),
                    patch.len(),
                    std::ptr::null_mut(),
                    0,
                    &mut written,
                ),
                PCL_OK
            );
        }
        assert_eq!(written, 0);
    }

    #[test]
    fn bsdiff_rejects_bad_headers_overflows_and_negative_old_pos() {
        let mut len = 0u64;
        let mut bad_magic = [0u8; 32];
        bad_magic[..8].copy_from_slice(b"BADMAGIC");

        let origin = b"abcdefgh";
        let patch_neg_old = build_synthetic_patch(&[(2, 0, -10), (2, 0, 0)], b"1234", &[], 4);
        let patch_old_overflow = build_synthetic_patch(&[(2, 0, 20), (1, 0, 0)], b"123", &[], 3);
        let patch_add_overflow = build_synthetic_patch(&[(5, 0, 0)], b"12345", &[], 3);
        let mut corrupt_bz = build_synthetic_patch(&[(2, 0, 0)], b"12", &[], 2);
        let last = corrupt_bz.len() - 1;
        corrupt_bz[34] ^= 0xFF;
        corrupt_bz[last] ^= 0xFF;
        let mut neg_hdr = [0u8; 32];
        neg_hdr[..8].copy_from_slice(BSDIFF_MAGIC);
        neg_hdr[8..16].copy_from_slice(&(-1_i64).to_le_bytes());
        let mut oversized_hdr = [0u8; 32];
        oversized_hdr[..8].copy_from_slice(BSDIFF_MAGIC);
        oversized_hdr[8..16].copy_from_slice(&100_i64.to_le_bytes());
        let mut out = [0u8; 4];

        // SAFETY: Testing error handling on null pointers, truncated headers, and malformed patches.
        unsafe {
            assert_eq!(
                pcl_native_bsdiff_inspect(std::ptr::null(), 32, &mut len),
                PCL_ERR_NULL_PTR
            );
            assert_eq!(
                pcl_native_bsdiff_inspect(b"short".as_ptr(), 5, std::ptr::null_mut()),
                PCL_ERR_NULL_PTR
            );
            assert_eq!(
                pcl_native_bsdiff_inspect(b"short".as_ptr(), 5, &mut len),
                PCL_ERR_INVALID_FORMAT
            );
            assert_eq!(
                pcl_native_bsdiff_inspect(bad_magic.as_ptr(), bad_magic.len(), &mut len),
                PCL_ERR_INVALID_FORMAT
            );
            assert_eq!(
                pcl_native_bsdiff_apply(
                    origin.as_ptr(),
                    origin.len(),
                    patch_neg_old.as_ptr(),
                    patch_neg_old.len(),
                    out.as_mut_ptr(),
                    out.len(),
                    std::ptr::null_mut(),
                ),
                PCL_ERR_INVALID_FORMAT
            );
            assert_eq!(
                pcl_native_bsdiff_apply(
                    origin.as_ptr(),
                    origin.len(),
                    patch_old_overflow.as_ptr(),
                    patch_old_overflow.len(),
                    out.as_mut_ptr(),
                    out.len(),
                    std::ptr::null_mut(),
                ),
                PCL_ERR_INVALID_FORMAT
            );
            assert_eq!(
                pcl_native_bsdiff_apply(
                    origin.as_ptr(),
                    origin.len(),
                    patch_add_overflow.as_ptr(),
                    patch_add_overflow.len(),
                    out.as_mut_ptr(),
                    out.len(),
                    std::ptr::null_mut(),
                ),
                PCL_ERR_INVALID_FORMAT
            );
            assert_eq!(
                pcl_native_bsdiff_apply(
                    origin.as_ptr(),
                    origin.len(),
                    corrupt_bz.as_ptr(),
                    corrupt_bz.len(),
                    out.as_mut_ptr(),
                    out.len(),
                    std::ptr::null_mut(),
                ),
                PCL_ERR_INVALID_FORMAT
            );
            assert_eq!(
                pcl_native_bsdiff_inspect(neg_hdr.as_ptr(), neg_hdr.len(), &mut len),
                PCL_ERR_INVALID_FORMAT
            );
            assert_eq!(
                pcl_native_bsdiff_inspect(oversized_hdr.as_ptr(), oversized_hdr.len(), &mut len),
                PCL_ERR_INVALID_FORMAT
            );
        }
    }

    #[test]
    fn bsdiff_allows_temporary_negative_old_pos_when_followed_by_positive_seek() {
        let origin = b"ABCDEF";
        // Triple 1: add 2 ("AB"), copy 0, seek -10 -> old_pos = 2 - 10 = -8
        // Triple 2: add 0, copy 1 ("!"), seek +10 -> old_pos = +2
        // Triple 3: add 2 ("CD"), copy 0, seek 0 -> old_pos = 4
        let diff_raw = [0u8; 4];
        let extra_raw = b"!";
        let triples = [(2, 0, -10), (0, 1, 10), (2, 0, 0)];
        let patch = build_synthetic_patch(&triples, &diff_raw, extra_raw, 5);
        let mut out = [0u8; 5];
        // SAFETY: Valid origin, patch, and output slices.
        let code = unsafe {
            pcl_native_bsdiff_apply(
                origin.as_ptr(),
                origin.len(),
                patch.as_ptr(),
                patch.len(),
                out.as_mut_ptr(),
                out.len(),
                std::ptr::null_mut(),
            )
        };
        assert_eq!(code, PCL_OK);
        assert_eq!(&out, b"AB!CD");
    }
}
