pub mod blur;
pub mod bsdiff;
pub mod hash_scan;
pub mod search;

use std::mem::{align_of, size_of};
use std::panic::{AssertUnwindSafe, catch_unwind};

/// Current C ABI version for `pcl_native`.
pub const PCL_NATIVE_ABI_VERSION: u32 = 2;

/// Operation succeeded.
pub const PCL_OK: i32 = 0;
/// A required pointer argument was null.
pub const PCL_ERR_NULL_PTR: i32 = -1;
/// An argument value was out of range, misaligned, overlapping, or otherwise invalid.
pub const PCL_ERR_INVALID_ARG: i32 = -2;
/// Input data header, stream, or container format was malformed or truncated.
pub const PCL_ERR_INVALID_FORMAT: i32 = -3;
/// An operating-system I/O error occurred while opening or reading a file.
pub const PCL_ERR_IO: i32 = -4;
/// Caller-provided output buffer capacity was smaller than required.
pub const PCL_ERR_BUFFER_TOO_SMALL: i32 = -5;
/// An unexpected Rust panic was caught at the FFI boundary.
pub const PCL_ERR_PANIC: i32 = -99;

/// Returns the C ABI version implemented by this library.
#[unsafe(no_mangle)]
pub extern "C" fn pcl_native_abi_version() -> u32 {
    PCL_NATIVE_ABI_VERSION
}

/// Wraps an FFI entry point so Rust panics cannot unwind across the C ABI boundary.
#[inline]
pub(crate) fn ffi_guard<F>(f: F) -> i32
where
    F: FnOnce() -> i32,
{
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(code) => code,
        Err(_) => PCL_ERR_PANIC,
    }
}

/// Converts a raw const pointer and element count into a shared slice after validating
/// non-nullity (when `len > 0`), alignment, and total byte size (`<= isize::MAX`).
///
/// # Safety
/// When `len > 0`, `ptr` must point to `len` initialized, properly aligned `T` elements
/// valid for reads for lifetime `'a`.
pub(crate) unsafe fn slice_from_raw_parts_checked<'a, T>(
    ptr: *const T,
    len: usize,
) -> Result<&'a [T], i32> {
    if len == 0 {
        return Ok(&[]);
    }
    if ptr.is_null() {
        return Err(PCL_ERR_NULL_PTR);
    }
    if !(ptr as usize).is_multiple_of(align_of::<T>()) {
        return Err(PCL_ERR_INVALID_ARG);
    }
    let byte_len = len
        .checked_mul(size_of::<T>())
        .filter(|&b| b <= isize::MAX as usize)
        .ok_or(PCL_ERR_INVALID_ARG)?;
    let _ = byte_len;
    // SAFETY: Caller guarantees `ptr` is valid for `len` reads of `T` for `'a`, and we checked
    // non-nullity, alignment, and total byte length `<= isize::MAX`.
    Ok(unsafe { std::slice::from_raw_parts(ptr, len) })
}

/// Converts a raw mut pointer and element count into a mutable slice after validating
/// non-nullity (when `len > 0`), alignment, and total byte size (`<= isize::MAX`).
///
/// # Safety
/// When `len > 0`, `ptr` must point to `len` writable, non-aliased, properly aligned `T`
/// elements valid for reads and writes for lifetime `'a`.
pub(crate) unsafe fn mut_slice_from_raw_parts_checked<'a, T>(
    ptr: *mut T,
    len: usize,
) -> Result<&'a mut [T], i32> {
    if len == 0 {
        return Ok(&mut []);
    }
    if ptr.is_null() {
        return Err(PCL_ERR_NULL_PTR);
    }
    if !(ptr as usize).is_multiple_of(align_of::<T>()) {
        return Err(PCL_ERR_INVALID_ARG);
    }
    let byte_len = len
        .checked_mul(size_of::<T>())
        .filter(|&b| b <= isize::MAX as usize)
        .ok_or(PCL_ERR_INVALID_ARG)?;
    let _ = byte_len;
    // SAFETY: Caller guarantees `ptr` is valid and non-aliased for `len` reads/writes of `T`
    // for `'a`, and we checked non-nullity, alignment, and total byte length `<= isize::MAX`.
    Ok(unsafe { std::slice::from_raw_parts_mut(ptr, len) })
}

/// Checks whether two byte spans in memory overlap.
#[inline]
pub(crate) fn ranges_overlap(
    a_ptr: *const u8,
    a_len: usize,
    b_ptr: *const u8,
    b_len: usize,
) -> bool {
    if a_len == 0 || b_len == 0 {
        return false;
    }
    let a_start = a_ptr as usize;
    let b_start = b_ptr as usize;
    let a_end = a_start.saturating_add(a_len);
    let b_end = b_start.saturating_add(b_len);
    a_start < b_end && b_start < a_end
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn abi_version_is_positive() {
        assert_eq!(pcl_native_abi_version(), 2);
    }

    #[test]
    fn ffi_guard_catches_panic_and_returns_err_panic() {
        let code = ffi_guard(|| -> i32 {
            panic!("synthetic panic across FFI boundary");
        });
        assert_eq!(code, PCL_ERR_PANIC);
    }
}
