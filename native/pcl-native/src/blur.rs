use std::cell::RefCell;

use rayon::prelude::*;

use crate::{
    PCL_ERR_INVALID_ARG, PCL_ERR_NULL_PTR, PCL_OK, ffi_guard, mut_slice_from_raw_parts_checked,
    ranges_overlap, slice_from_raw_parts_checked,
};

/// Rendering bias matching `System.Windows.Media.Effects.RenderingBias`.
pub const PCL_RENDERING_BIAS_PERFORMANCE: i32 = 0;
pub const PCL_RENDERING_BIAS_QUALITY: i32 = 1;

/// Kernel type matching `System.Windows.Media.Effects.KernelType`.
pub const PCL_KERNEL_GAUSSIAN: i32 = 0;
pub const PCL_KERNEL_BOX: i32 = 1;

/// Exact 32 Poisson disk sample offsets produced by `.NET System.Random(42)` in
/// `SamplingBlurProcessor._GeneratePoissonDiskSamples()`.
const POISSON_SAMPLES: [(f32, f32); 32] = [
    (f32::from_bits(0x3EAC_241A), f32::from_bits(0xBF37_DB00)),
    (f32::from_bits(0xBF3F_BC11), f32::from_bits(0x3D3A_7C26)),
    (f32::from_bits(0x3EE5_CB4E), f32::from_bits(0x3CD3_BA1E)),
    (f32::from_bits(0xBE01_2000), f32::from_bits(0x3F15_A68C)),
    (f32::from_bits(0x3F1C_3B39), f32::from_bits(0x3F42_9B89)),
    (f32::from_bits(0xBEBE_CD6F), f32::from_bits(0xBF28_2EB7)),
    (f32::from_bits(0x3E9C_BF44), f32::from_bits(0x3F3D_35E5)),
    (f32::from_bits(0x3E2D_22DB), f32::from_bits(0x3F59_9A83)),
    (f32::from_bits(0x2483_C784), f32::from_bits(0x3F6E_EEEF)),
    (f32::from_bits(0xBE1F_D168), f32::from_bits(0x3F48_DD65)),
    (f32::from_bits(0xBEA9_CF35), f32::from_bits(0x3F4C_FA63)),
    (f32::from_bits(0xBF04_BE87), f32::from_bits(0x3F46_AA72)),
    (f32::from_bits(0xBF10_D0C3), f32::from_bits(0x3F10_D0C3)),
    (f32::from_bits(0xBF38_79B3), f32::from_bits(0x3EF6_8669)),
    (f32::from_bits(0xBF5C_BEE1), f32::from_bits(0x3EB6_DF25)),
    (f32::from_bits(0xBF48_DD65), f32::from_bits(0x3E1F_D168)),
    (f32::from_bits(0xBF5D_DDDE), f32::from_bits(0x24F4_BBAB)),
    (f32::from_bits(0xBF6A_57A1), f32::from_bits(0xBE3A_744F)),
    (f32::from_bits(0xBF3D_35E5), f32::from_bits(0xBE9C_BF44)),
    (f32::from_bits(0xBF38_79B3), f32::from_bits(0xBEF6_8669)),
    (f32::from_bits(0xBF28_F38E), f32::from_bits(0xBF28_F38E)),
    (f32::from_bits(0xBEE3_8FC3), f32::from_bits(0xBF2A_48F4)),
    (f32::from_bits(0xBEA9_CF35), f32::from_bits(0xBF4C_FA63)),
    (f32::from_bits(0xBE3A_744F), f32::from_bits(0xBF6A_57A1)),
    (f32::from_bits(0xA529_6E3C), f32::from_bits(0xBF4C_CCCD)),
    (f32::from_bits(0x3E2D_22DB), f32::from_bits(0xBF59_9A83)),
    (f32::from_bits(0x3EB6_DF25), f32::from_bits(0xBF5C_BEE1)),
    (f32::from_bits(0x3EE3_8FC3), f32::from_bits(0xBF2A_48F4)),
    (f32::from_bits(0x3F1C_E228), f32::from_bits(0xBF1C_E228)),
    (f32::from_bits(0x3F46_AA72), f32::from_bits(0xBF04_BE87)),
    (f32::from_bits(0x3F3D_35E5), f32::from_bits(0xBE9C_BF44)),
    (f32::from_bits(0x3F59_9A83), f32::from_bits(0xBE2D_22DB)),
];

thread_local! {
    static BLUR_ROW_SCRATCH: RefCell<(Vec<f32>, Vec<f32>)> = const { RefCell::new((Vec::new(), Vec::new())) };
}

/// Applies CPU sampling blur on `Bgra32`/`Pbgra32` (`[B, G, R, A]` little-endian) buffers,
/// semantically matching `PCL.Core.UI.Effects.SamplingBlurProcessor`.
///
/// # Safety
/// - `src_ptr` must be valid for reads of `src_stride * height` bytes.
/// - `dst_ptr` must be valid for writes of `dst_stride * height` bytes.
/// - `src_ptr` and `dst_ptr` must not overlap.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn pcl_native_blur_bgra32(
    src_ptr: *const u8,
    src_stride: i32,
    dst_ptr: *mut u8,
    dst_stride: i32,
    width: i32,
    height: i32,
    radius: f64,
    sampling_rate: f64,
    rendering_bias: i32,
    kernel_type: i32,
) -> i32 {
    ffi_guard(|| {
        if src_ptr.is_null() || dst_ptr.is_null() {
            return PCL_ERR_NULL_PTR;
        }
        if width <= 0 || height <= 0 || src_stride <= 0 || dst_stride <= 0 {
            return PCL_ERR_INVALID_ARG;
        }
        if !radius.is_finite() || radius <= 0.0 || !sampling_rate.is_finite() || sampling_rate < 0.0
        {
            return PCL_ERR_INVALID_ARG;
        }
        if rendering_bias != PCL_RENDERING_BIAS_PERFORMANCE
            && rendering_bias != PCL_RENDERING_BIAS_QUALITY
        {
            return PCL_ERR_INVALID_ARG;
        }
        if kernel_type != PCL_KERNEL_GAUSSIAN && kernel_type != PCL_KERNEL_BOX {
            return PCL_ERR_INVALID_ARG;
        }

        let w = width as usize;
        let h = height as usize;
        let s_stride = src_stride as usize;
        let d_stride = dst_stride as usize;
        let Some(row_bytes) = w.checked_mul(4) else {
            return PCL_ERR_INVALID_ARG;
        };
        if s_stride < row_bytes || d_stride < row_bytes {
            return PCL_ERR_INVALID_ARG;
        }

        let Some(src_total_bytes) = s_stride.checked_mul(h) else {
            return PCL_ERR_INVALID_ARG;
        };
        let Some(dst_total_bytes) = d_stride.checked_mul(h) else {
            return PCL_ERR_INVALID_ARG;
        };
        if src_total_bytes > isize::MAX as usize || dst_total_bytes > isize::MAX as usize {
            return PCL_ERR_INVALID_ARG;
        }
        if ranges_overlap(src_ptr, src_total_bytes, dst_ptr, dst_total_bytes) {
            return PCL_ERR_INVALID_ARG;
        }

        // SAFETY: Validated non-null, positive sizes, stride >= width*4, total bytes <= isize::MAX,
        // and non-overlapping src/dst regions.
        let src = match unsafe { slice_from_raw_parts_checked(src_ptr, src_total_bytes) } {
            Ok(s) => s,
            Err(code) => return code,
        };
        // SAFETY: Validated non-null, non-overlapping mutable buffer of `dst_total_bytes`.
        let dst = match unsafe { mut_slice_from_raw_parts_checked(dst_ptr, dst_total_bytes) } {
            Ok(d) => d,
            Err(code) => return code,
        };

        blur_bgra32_impl(
            src,
            s_stride,
            dst,
            d_stride,
            w,
            h,
            radius,
            sampling_rate,
            rendering_bias,
            kernel_type,
        )
    })
}

#[allow(clippy::too_many_arguments)]
pub fn blur_bgra32_impl(
    src: &[u8],
    src_stride: usize,
    dst: &mut [u8],
    dst_stride: usize,
    width: usize,
    height: usize,
    radius: f64,
    sampling_rate: f64,
    rendering_bias: i32,
    kernel_type: i32,
) -> i32 {
    if rendering_bias == PCL_RENDERING_BIAS_QUALITY {
        apply_quality_blur(
            src,
            src_stride,
            dst,
            dst_stride,
            width,
            height,
            radius,
            sampling_rate,
            kernel_type,
        )
    } else {
        apply_performance_blur(
            src,
            src_stride,
            dst,
            dst_stride,
            width,
            height,
            radius,
            sampling_rate,
        )
    }
}

fn copy_image_rows(src: &[u8], src_stride: usize, dst: &mut [u8], dst_stride: usize, width: usize) {
    let row_bytes = width * 4;
    dst.par_chunks_mut(dst_stride)
        .enumerate()
        .for_each(|(y, dst_row)| {
            let src_row = &src[y * src_stride..y * src_stride + row_bytes];
            dst_row[..row_bytes].copy_from_slice(src_row);
        });
}

#[derive(Clone, Copy)]
struct QualityTap {
    dx: isize,
    dy: isize,
    weight: f32,
    norm_weight: f32,
}

#[allow(clippy::too_many_arguments)]
fn apply_quality_blur(
    src: &[u8],
    src_stride: usize,
    dst: &mut [u8],
    dst_stride: usize,
    width: usize,
    height: usize,
    radius: f64,
    sampling_rate: f64,
    kernel_type: i32,
) -> i32 {
    let ceil_radius = radius.ceil();
    if ceil_radius > i32::MAX as f64 {
        return PCL_ERR_INVALID_ARG;
    }
    let int_radius = ceil_radius as i32;
    let sigma = radius / 3.0;
    let two_sigma_sq = 2.0 * sigma * sigma;

    let sample_count = if kernel_type == PCL_KERNEL_GAUSSIAN {
        ((32.0 * sampling_rate) as usize).min(POISSON_SAMPLES.len())
    } else {
        ((16.0 * sampling_rate) as usize).min(16)
    };

    if sample_count == 0 || int_radius <= 0 {
        copy_image_rows(src, src_stride, dst, dst_stride, width);
        return PCL_OK;
    }

    let r_f32 = int_radius as f32;
    let r_f64 = int_radius as f64;
    let mut raw_taps = [QualityTap {
        dx: 0,
        dy: 0,
        weight: 0.0,
        norm_weight: 0.0,
    }; 32];
    let mut raw_weights_f64 = [0.0_f64; 32];
    let mut total_weight_f64 = 0.0_f64;
    let mut min_dx = 0_isize;
    let mut max_dx = 0_isize;
    let mut min_dy = 0_isize;
    let mut max_dy = 0_isize;

    for (i, &(px, py)) in POISSON_SAMPLES.iter().take(sample_count).enumerate() {
        let ox = px * r_f32;
        let oy = py * r_f32;
        let dx = (ox as f64).round_ties_even() as isize;
        let dy = (oy as f64).round_ties_even() as isize;
        let dist_sq_f32 = ox * ox + oy * oy;
        let dist = dist_sq_f32.sqrt() as f64;

        let w = if kernel_type == PCL_KERNEL_GAUSSIAN {
            (-dist * dist / two_sigma_sq).exp()
        } else {
            (1.0 - dist / r_f64).max(0.0)
        };

        min_dx = min_dx.min(dx);
        max_dx = max_dx.max(dx);
        min_dy = min_dy.min(dy);
        max_dy = max_dy.max(dy);
        total_weight_f64 += w;
        raw_weights_f64[i] = w;
        raw_taps[i] = QualityTap {
            dx,
            dy,
            weight: w as f32,
            norm_weight: 0.0,
        };
    }

    if total_weight_f64 <= 0.0 {
        copy_image_rows(src, src_stride, dst, dst_stride, width);
        return PCL_OK;
    }

    let inv_total = 1.0 / total_weight_f64;
    for (tap, &w_f64) in raw_taps[..sample_count]
        .iter_mut()
        .zip(&raw_weights_f64[..sample_count])
    {
        tap.norm_weight = (w_f64 * inv_total) as f32;
    }
    let taps = &raw_taps[..sample_count];

    let x_int_start = ((-min_dx).max(0) as usize).min(width);
    let x_int_end = width
        .saturating_sub(max_dx.max(0) as usize)
        .max(x_int_start);
    let y_int_start = ((-min_dy).max(0) as usize).min(height);
    let y_int_end = height
        .saturating_sub(max_dy.max(0) as usize)
        .max(y_int_start);
    let row_bytes = width * 4;

    dst.par_chunks_mut(dst_stride)
        .enumerate()
        .for_each(|(y, dst_row)| {
            let dst_pixels = &mut dst_row[..row_bytes];
            let is_interior_row = y >= y_int_start && y < y_int_end;

            BLUR_ROW_SCRATCH.with(|cell| {
                let mut borrow = cell.borrow_mut();
                let (acc_rgba, acc_w) = &mut *borrow;
                if acc_rgba.len() < row_bytes {
                    acc_rgba.resize(row_bytes, 0.0);
                }

                if is_interior_row {
                    // Left border columns
                    for x in 0..x_int_start {
                        blur_quality_pixel(src, src_stride, width, height, x, y, taps, dst_pixels);
                    }

                    // Interior span: every tap is guaranteed in-bounds, so total weight is constant
                    // and each tap reads sequentially along a contiguous source row slice.
                    if x_int_end > x_int_start {
                        let acc_slice = &mut acc_rgba[x_int_start * 4..x_int_end * 4];
                        let tap0 = taps[0];
                        let sy0 = (y as isize + tap0.dy) as usize;
                        let sx0_start = (x_int_start as isize + tap0.dx) as usize;
                        let sx0_end = (x_int_end as isize + tap0.dx) as usize;
                        let src0 =
                            &src[sy0 * src_stride + sx0_start * 4..sy0 * src_stride + sx0_end * 4];
                        let w0 = tap0.norm_weight;
                        for (acc_v, &src_b) in acc_slice.iter_mut().zip(src0.iter()) {
                            *acc_v = (src_b as f32) * w0;
                        }

                        for tap in &taps[1..] {
                            let sy = (y as isize + tap.dy) as usize;
                            let sx_start = (x_int_start as isize + tap.dx) as usize;
                            let sx_end = (x_int_end as isize + tap.dx) as usize;
                            let src_span =
                                &src[sy * src_stride + sx_start * 4..sy * src_stride + sx_end * 4];
                            let w = tap.norm_weight;
                            for (acc_v, &src_b) in acc_slice.iter_mut().zip(src_span.iter()) {
                                *acc_v += (src_b as f32) * w;
                            }
                        }

                        let dst_int = &mut dst_pixels[x_int_start * 4..x_int_end * 4];
                        for (out_b, &val) in dst_int.iter_mut().zip(acc_slice.iter()) {
                            *out_b = val.min(255.0) as u8;
                        }
                    }

                    // Right border columns
                    for x in x_int_end..width {
                        blur_quality_pixel(src, src_stride, width, height, x, y, taps, dst_pixels);
                    }
                } else {
                    // Border row: accumulate across valid rows into row scratch buffers.
                    if acc_w.len() < width {
                        acc_w.resize(width, 0.0);
                    }
                    acc_rgba[..row_bytes].fill(0.0);
                    acc_w[..width].fill(0.0);

                    for tap in taps {
                        let sy = y as isize + tap.dy;
                        if sy < 0 || sy >= height as isize {
                            continue;
                        }
                        let sy_u = sy as usize;
                        let src_row = &src[sy_u * src_stride..sy_u * src_stride + row_bytes];
                        let w = tap.weight;
                        let x_start = ((-tap.dx).max(0) as usize).min(width);
                        let x_end = width.saturating_sub(tap.dx.max(0) as usize).max(x_start);
                        if x_end <= x_start {
                            continue;
                        }
                        let sx_start = (x_start as isize + tap.dx) as usize;
                        let sx_end = (x_end as isize + tap.dx) as usize;
                        let src_span = &src_row[sx_start * 4..sx_end * 4];
                        let acc_span = &mut acc_rgba[x_start * 4..x_end * 4];
                        for (acc_v, &src_b) in acc_span.iter_mut().zip(src_span.iter()) {
                            *acc_v += (src_b as f32) * w;
                        }
                        for wt in &mut acc_w[x_start..x_end] {
                            *wt += w;
                        }
                    }

                    let src_center_row = &src[y * src_stride..y * src_stride + row_bytes];
                    for (x, &wt) in acc_w.iter().enumerate().take(width) {
                        let base = x * 4;
                        if wt > 0.0 {
                            let inv = 1.0 / wt;
                            dst_pixels[base] = (acc_rgba[base] * inv).min(255.0) as u8;
                            dst_pixels[base + 1] = (acc_rgba[base + 1] * inv).min(255.0) as u8;
                            dst_pixels[base + 2] = (acc_rgba[base + 2] * inv).min(255.0) as u8;
                            dst_pixels[base + 3] = (acc_rgba[base + 3] * inv).min(255.0) as u8;
                        } else {
                            dst_pixels[base..base + 4]
                                .copy_from_slice(&src_center_row[base..base + 4]);
                        }
                    }
                }
            });
        });

    PCL_OK
}

#[allow(clippy::too_many_arguments)]
#[inline]
fn blur_quality_pixel(
    src: &[u8],
    src_stride: usize,
    width: usize,
    height: usize,
    x: usize,
    y: usize,
    taps: &[QualityTap],
    dst_row: &mut [u8],
) {
    let mut sum_b = 0.0_f32;
    let mut sum_g = 0.0_f32;
    let mut sum_r = 0.0_f32;
    let mut sum_a = 0.0_f32;
    let mut sum_w = 0.0_f32;

    for tap in taps {
        let sx = x as isize + tap.dx;
        let sy = y as isize + tap.dy;
        if sx >= 0 && sx < width as isize && sy >= 0 && sy < height as isize {
            let off = (sy as usize) * src_stride + (sx as usize) * 4;
            let w = tap.weight;
            sum_b += (src[off] as f32) * w;
            sum_g += (src[off + 1] as f32) * w;
            sum_r += (src[off + 2] as f32) * w;
            sum_a += (src[off + 3] as f32) * w;
            sum_w += w;
        }
    }

    let out_off = x * 4;
    if sum_w > 0.0 {
        let inv = 1.0 / sum_w;
        dst_row[out_off] = (sum_b * inv).min(255.0) as u8;
        dst_row[out_off + 1] = (sum_g * inv).min(255.0) as u8;
        dst_row[out_off + 2] = (sum_r * inv).min(255.0) as u8;
        dst_row[out_off + 3] = (sum_a * inv).min(255.0) as u8;
    } else {
        let off = y * src_stride + out_off;
        dst_row[out_off..out_off + 4].copy_from_slice(&src[off..off + 4]);
    }
}

#[allow(clippy::too_many_arguments)]
fn apply_performance_blur(
    src: &[u8],
    src_stride: usize,
    dst: &mut [u8],
    dst_stride: usize,
    width: usize,
    height: usize,
    radius: f64,
    sampling_rate: f64,
) -> i32 {
    let ceil_radius = (radius * sampling_rate).ceil();
    if ceil_radius > i32::MAX as f64 {
        return PCL_ERR_INVALID_ARG;
    }
    let r = ceil_radius as isize;
    if r <= 0 {
        copy_image_rows(src, src_stride, dst, dst_stride, width);
        return PCL_OK;
    }

    let sample_step = if sampling_rate >= 0.8 {
        1_isize
    } else {
        (2.0 - sampling_rate).ceil().max(1.0) as isize
    };

    let sigma = (r as f64) / 3.0;
    let two_sigma_sq = 2.0 * sigma * sigma;
    let mut raw_taps: Vec<(isize, f64)> = Vec::new();
    let mut sum_w = 0.0_f64;
    let mut k = -r;
    while k <= r {
        let x = k as f64;
        let w = (-(x * x) / two_sigma_sq).exp();
        raw_taps.push((k, w));
        sum_w += w;
        k += sample_step;
    }

    if sum_w <= 0.0 || raw_taps.is_empty() {
        copy_image_rows(src, src_stride, dst, dst_stride, width);
        return PCL_OK;
    }

    let inv_sum = 1.0 / sum_w;
    let taps: Vec<(isize, f32)> = raw_taps
        .into_iter()
        .map(|(offset, w)| (offset, (w * inv_sum) as f32))
        .collect();

    let row_bytes = width * 4;
    let Some(temp_bytes) = row_bytes.checked_mul(height) else {
        return PCL_ERR_INVALID_ARG;
    };
    let mut temp = vec![0u8; temp_bytes];

    let r_u = r as usize;
    let x_int_start = r_u.min(width);
    let x_int_end = width.saturating_sub(r_u).max(x_int_start);
    let max_x = (width - 1) as isize;

    // Pass 1: Horizontal separable blur (src -> temp)
    temp.par_chunks_mut(row_bytes)
        .enumerate()
        .for_each(|(y, temp_row)| {
            let src_row = &src[y * src_stride..y * src_stride + row_bytes];
            BLUR_ROW_SCRATCH.with(|cell| {
                let mut borrow = cell.borrow_mut();
                let (acc, _) = &mut *borrow;
                if acc.len() < row_bytes {
                    acc.resize(row_bytes, 0.0);
                }

                // Clamped left border
                for x in 0..x_int_start {
                    blur_clamped_horiz_pixel(src_row, max_x, x, &taps, temp_row);
                }

                // Interior sequential FMA pass
                if x_int_end > x_int_start {
                    let acc_slice = &mut acc[x_int_start * 4..x_int_end * 4];
                    let (dx0, w0) = taps[0];
                    let sx0_start = (x_int_start as isize + dx0) as usize;
                    let sx0_end = (x_int_end as isize + dx0) as usize;
                    let src0 = &src_row[sx0_start * 4..sx0_end * 4];
                    for (acc_v, &src_b) in acc_slice.iter_mut().zip(src0.iter()) {
                        *acc_v = (src_b as f32) * w0;
                    }

                    for &(dx, w) in &taps[1..] {
                        let sx_start = (x_int_start as isize + dx) as usize;
                        let sx_end = (x_int_end as isize + dx) as usize;
                        let src_span = &src_row[sx_start * 4..sx_end * 4];
                        for (acc_v, &src_b) in acc_slice.iter_mut().zip(src_span.iter()) {
                            *acc_v += (src_b as f32) * w;
                        }
                    }

                    let dst_int = &mut temp_row[x_int_start * 4..x_int_end * 4];
                    for (out_b, &val) in dst_int.iter_mut().zip(acc_slice.iter()) {
                        *out_b = (val + 1e-4_f32).min(255.0) as u8;
                    }
                }

                // Clamped right border
                for x in x_int_end..width {
                    blur_clamped_horiz_pixel(src_row, max_x, x, &taps, temp_row);
                }
            });
        });

    // Pass 2: Vertical separable blur (temp -> dst) processed row-wise for cache locality
    let max_y = (height - 1) as isize;
    dst.par_chunks_mut(dst_stride)
        .enumerate()
        .for_each(|(y, dst_row)| {
            let dst_pixels = &mut dst_row[..row_bytes];
            BLUR_ROW_SCRATCH.with(|cell| {
                let mut borrow = cell.borrow_mut();
                let (acc, _) = &mut *borrow;
                if acc.len() < row_bytes {
                    acc.resize(row_bytes, 0.0);
                }
                let acc_slice = &mut acc[..row_bytes];

                let (dy0, w0) = taps[0];
                let sy0 = (y as isize + dy0).clamp(0, max_y) as usize;
                let t_row0 = &temp[sy0 * row_bytes..(sy0 + 1) * row_bytes];
                for (acc_v, &src_b) in acc_slice.iter_mut().zip(t_row0.iter()) {
                    *acc_v = (src_b as f32) * w0;
                }

                for &(dy, w) in &taps[1..] {
                    let sy = (y as isize + dy).clamp(0, max_y) as usize;
                    let t_row = &temp[sy * row_bytes..(sy + 1) * row_bytes];
                    for (acc_v, &src_b) in acc_slice.iter_mut().zip(t_row.iter()) {
                        *acc_v += (src_b as f32) * w;
                    }
                }

                for (out_b, &val) in dst_pixels.iter_mut().zip(acc_slice.iter()) {
                    *out_b = (val + 1e-4_f32).min(255.0) as u8;
                }
            });
        });

    PCL_OK
}

#[inline]
fn blur_clamped_horiz_pixel(
    src_row: &[u8],
    max_x: isize,
    x: usize,
    taps: &[(isize, f32)],
    dst_row: &mut [u8],
) {
    let mut sum_b = 0.0_f32;
    let mut sum_g = 0.0_f32;
    let mut sum_r = 0.0_f32;
    let mut sum_a = 0.0_f32;
    let x_i = x as isize;

    for &(dx, w) in taps {
        let sx = (x_i + dx).clamp(0, max_x) as usize;
        let off = sx * 4;
        sum_b += (src_row[off] as f32) * w;
        sum_g += (src_row[off + 1] as f32) * w;
        sum_r += (src_row[off + 2] as f32) * w;
        sum_a += (src_row[off + 3] as f32) * w;
    }

    let out_off = x * 4;
    dst_row[out_off] = (sum_b + 1e-4_f32).min(255.0) as u8;
    dst_row[out_off + 1] = (sum_g + 1e-4_f32).min(255.0) as u8;
    dst_row[out_off + 2] = (sum_r + 1e-4_f32).min(255.0) as u8;
    dst_row[out_off + 3] = (sum_a + 1e-4_f32).min(255.0) as u8;
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Exact scalar reference matching `SamplingBlurProcessor.cs` in `f64` for parity checks.
    #[allow(clippy::too_many_arguments)]
    fn reference_blur_scalar(
        src: &[u8],
        src_stride: usize,
        width: usize,
        height: usize,
        radius: f64,
        sampling_rate: f64,
        rendering_bias: i32,
        kernel_type: i32,
    ) -> Vec<u8> {
        let mut dst = vec![0u8; width * height * 4];
        if rendering_bias == PCL_RENDERING_BIAS_QUALITY {
            let int_radius = radius.ceil() as i32;
            let sigma = radius / 3.0;
            let two_sigma_sq = 2.0 * sigma * sigma;
            let sample_count = if kernel_type == PCL_KERNEL_GAUSSIAN {
                ((32.0 * sampling_rate) as usize).min(POISSON_SAMPLES.len())
            } else {
                ((16.0 * sampling_rate) as usize).min(16)
            };

            for y in 0..height {
                for x in 0..width {
                    let (mut ta, mut tr, mut tg, mut tb, mut tw) = (0.0, 0.0, 0.0, 0.0, 0.0);
                    for i in 0..sample_count {
                        let (px, py) = POISSON_SAMPLES[i % POISSON_SAMPLES.len()];
                        let ox = px * (int_radius as f32);
                        let oy = py * (int_radius as f32);
                        let sx = x as isize + (ox as f64).round_ties_even() as isize;
                        let sy = y as isize + (oy as f64).round_ties_even() as isize;
                        if sx >= 0 && sx < width as isize && sy >= 0 && sy < height as isize {
                            let off = (sy as usize) * src_stride + (sx as usize) * 4;
                            let dist = (ox * ox + oy * oy).sqrt() as f64;
                            let w = if kernel_type == PCL_KERNEL_GAUSSIAN {
                                (-dist * dist / two_sigma_sq).exp()
                            } else {
                                (1.0 - dist / (int_radius as f64)).max(0.0)
                            };
                            tb += src[off] as f64 * w;
                            tg += src[off + 1] as f64 * w;
                            tr += src[off + 2] as f64 * w;
                            ta += src[off + 3] as f64 * w;
                            tw += w;
                        }
                    }
                    let out_off = (y * width + x) * 4;
                    if tw > 0.0 {
                        let inv = 1.0 / tw;
                        dst[out_off] = (tb * inv).min(255.0) as u8;
                        dst[out_off + 1] = (tg * inv).min(255.0) as u8;
                        dst[out_off + 2] = (tr * inv).min(255.0) as u8;
                        dst[out_off + 3] = (ta * inv).min(255.0) as u8;
                    } else {
                        let in_off = y * src_stride + x * 4;
                        dst[out_off..out_off + 4].copy_from_slice(&src[in_off..in_off + 4]);
                    }
                }
            }
        } else {
            let int_radius = (radius * sampling_rate).ceil() as isize;
            if int_radius <= 0 {
                for y in 0..height {
                    let s = &src[y * src_stride..y * src_stride + width * 4];
                    dst[y * width * 4..(y + 1) * width * 4].copy_from_slice(s);
                }
                return dst;
            }
            let size = (int_radius as usize) * 2 + 1;
            let mut weights = vec![0.0_f64; size];
            let sigma = (int_radius as f64) / 3.0;
            let two_sigma_sq = 2.0 * sigma * sigma;
            let norm = 1.0 / (std::f64::consts::PI * two_sigma_sq).sqrt();
            let mut total = 0.0;
            for (i, w_slot) in weights.iter_mut().enumerate() {
                let x = i as isize - int_radius;
                let w = norm * (-(x * x) as f64 / two_sigma_sq).exp();
                *w_slot = w;
                total += w;
            }
            for w in &mut weights {
                *w /= total;
            }
            let step = if sampling_rate >= 0.8 {
                1_isize
            } else {
                (2.0 - sampling_rate).ceil() as isize
            };

            let mut temp = vec![0u8; width * height * 4];
            for y in 0..height {
                for x in 0..width {
                    let (mut ta, mut tr, mut tg, mut tb, mut tw) = (0.0, 0.0, 0.0, 0.0, 0.0);
                    let mut k = -int_radius;
                    while k <= int_radius {
                        let sx = (x as isize + k).clamp(0, width as isize - 1) as usize;
                        let off = y * src_stride + sx * 4;
                        let w = weights[(k.abs() + int_radius) as usize];
                        tb += src[off] as f64 * w;
                        tg += src[off + 1] as f64 * w;
                        tr += src[off + 2] as f64 * w;
                        ta += src[off + 3] as f64 * w;
                        tw += w;
                        k += step;
                    }
                    let inv = 1.0 / tw;
                    let out_off = (y * width + x) * 4;
                    temp[out_off] = (tb * inv).min(255.0) as u8;
                    temp[out_off + 1] = (tg * inv).min(255.0) as u8;
                    temp[out_off + 2] = (tr * inv).min(255.0) as u8;
                    temp[out_off + 3] = (ta * inv).min(255.0) as u8;
                }
            }
            for x in 0..width {
                for y in 0..height {
                    let (mut ta, mut tr, mut tg, mut tb, mut tw) = (0.0, 0.0, 0.0, 0.0, 0.0);
                    let mut k = -int_radius;
                    while k <= int_radius {
                        let sy = (y as isize + k).clamp(0, height as isize - 1) as usize;
                        let off = (sy * width + x) * 4;
                        let w = weights[(k.abs() + int_radius) as usize];
                        tb += temp[off] as f64 * w;
                        tg += temp[off + 1] as f64 * w;
                        tr += temp[off + 2] as f64 * w;
                        ta += temp[off + 3] as f64 * w;
                        tw += w;
                        k += step;
                    }
                    let inv = 1.0 / tw;
                    let out_off = (y * width + x) * 4;
                    dst[out_off] = (tb * inv).min(255.0) as u8;
                    dst[out_off + 1] = (tg * inv).min(255.0) as u8;
                    dst[out_off + 2] = (tr * inv).min(255.0) as u8;
                    dst[out_off + 3] = (ta * inv).min(255.0) as u8;
                }
            }
        }
        dst
    }

    fn make_pattern(width: usize, height: usize, stride: usize) -> Vec<u8> {
        let mut buf = vec![0xAAu8; stride * height];
        for y in 0..height {
            for x in 0..width {
                let off = y * stride + x * 4;
                buf[off] = ((x * 13 + y * 7) & 0xFF) as u8;
                buf[off + 1] = (((x * 5) ^ (y * 11)) & 0xFF) as u8;
                buf[off + 2] = ((x + y * 19) & 0xFF) as u8;
                buf[off + 3] = 200u8.wrapping_add((x & 0x37) as u8);
            }
        }
        buf
    }

    #[test]
    fn blur_parity_quality_and_performance_with_padded_strides() {
        let width = 73;
        let height = 51;
        let src_stride = width * 4 + 12;
        let dst_stride = width * 4 + 20;
        let src = make_pattern(width, height, src_stride);

        let cases = [
            (16.0, 1.0, PCL_RENDERING_BIAS_QUALITY, PCL_KERNEL_GAUSSIAN),
            (16.0, 0.5, PCL_RENDERING_BIAS_QUALITY, PCL_KERNEL_BOX),
            (
                16.0,
                1.0,
                PCL_RENDERING_BIAS_PERFORMANCE,
                PCL_KERNEL_GAUSSIAN,
            ),
            (16.0, 0.5, PCL_RENDERING_BIAS_PERFORMANCE, PCL_KERNEL_BOX),
            (100.0, 1.0, PCL_RENDERING_BIAS_QUALITY, PCL_KERNEL_GAUSSIAN), // radius > width/height
            (4.0, 0.01, PCL_RENDERING_BIAS_QUALITY, PCL_KERNEL_GAUSSIAN),  // 0 samples fallback
        ];

        for (radius, rate, bias, kernel) in cases {
            let mut dst = vec![0x55u8; dst_stride * height];
            // SAFETY: Buffers allocated with valid sizes and non-overlapping.
            let code = unsafe {
                pcl_native_blur_bgra32(
                    src.as_ptr(),
                    src_stride as i32,
                    dst.as_mut_ptr(),
                    dst_stride as i32,
                    width as i32,
                    height as i32,
                    radius,
                    rate,
                    bias,
                    kernel,
                )
            };
            assert_eq!(code, PCL_OK);

            let expected =
                reference_blur_scalar(&src, src_stride, width, height, radius, rate, bias, kernel);
            for y in 0..height {
                // Verify padding bytes untouched
                assert!(
                    dst[y * dst_stride + width * 4..(y + 1) * dst_stride]
                        .iter()
                        .all(|&b| b == 0x55)
                );

                for x in 0..width * 4 {
                    let got = dst[y * dst_stride + x];
                    let exp = expected[y * width * 4 + x];
                    let diff = got.abs_diff(exp);
                    assert!(
                        diff <= 1,
                        "mismatch at ({x},{y}) for radius={radius} rate={rate} bias={bias} kernel={kernel}: got={got} exp={exp}"
                    );
                }
            }
        }
    }

    #[test]
    fn blur_constant_color_is_exact() {
        let width = 32;
        let height = 24;
        let mut src = vec![0u8; width * height * 4];
        for px in src.as_chunks_mut::<4>().0 {
            px.copy_from_slice(&[17, 89, 203, 255]);
        }
        for bias in [PCL_RENDERING_BIAS_PERFORMANCE, PCL_RENDERING_BIAS_QUALITY] {
            let mut dst = vec![0u8; width * height * 4];
            // SAFETY: Valid non-overlapping buffers.
            let code = unsafe {
                pcl_native_blur_bgra32(
                    src.as_ptr(),
                    (width * 4) as i32,
                    dst.as_mut_ptr(),
                    (width * 4) as i32,
                    width as i32,
                    height as i32,
                    8.0,
                    1.0,
                    bias,
                    PCL_KERNEL_GAUSSIAN,
                )
            };
            assert_eq!(code, PCL_OK);
            let expected = reference_blur_scalar(
                &src,
                width * 4,
                width,
                height,
                8.0,
                1.0,
                bias,
                PCL_KERNEL_GAUSSIAN,
            );
            for (idx, (px, exp)) in dst
                .as_chunks::<4>()
                .0
                .iter()
                .zip(expected.as_chunks::<4>().0.iter())
                .enumerate()
            {
                assert!(
                    px[0].abs_diff(17) <= 2 && px[0].abs_diff(exp[0]) <= 1,
                    "bias={bias} idx={idx} px={px:?} exp={exp:?}"
                );
                assert!(
                    px[1].abs_diff(89) <= 2 && px[1].abs_diff(exp[1]) <= 1,
                    "bias={bias} idx={idx} px={px:?} exp={exp:?}"
                );
                assert!(
                    px[2].abs_diff(203) <= 2 && px[2].abs_diff(exp[2]) <= 1,
                    "bias={bias} idx={idx} px={px:?} exp={exp:?}"
                );
                assert!(
                    px[3].abs_diff(255) <= 2 && px[3].abs_diff(exp[3]) <= 1,
                    "bias={bias} idx={idx} px={px:?} exp={exp:?}"
                );
            }
        }
    }

    #[test]
    fn blur_rejects_null_bad_args_and_overlap() {
        let mut buf = vec![0u8; 64 * 4];
        let mut dst = vec![0u8; 64 * 4];
        // SAFETY: Testing invalid/null/overlapping argument rejection in FFI guard.
        unsafe {
            assert_eq!(
                pcl_native_blur_bgra32(
                    std::ptr::null(),
                    32,
                    buf.as_mut_ptr(),
                    32,
                    8,
                    8,
                    4.0,
                    1.0,
                    0,
                    0
                ),
                PCL_ERR_NULL_PTR
            );
            assert_eq!(
                pcl_native_blur_bgra32(
                    buf.as_ptr(),
                    32,
                    std::ptr::null_mut(),
                    32,
                    8,
                    8,
                    4.0,
                    1.0,
                    0,
                    0
                ),
                PCL_ERR_NULL_PTR
            );
            assert_eq!(
                pcl_native_blur_bgra32(
                    buf.as_ptr(),
                    32,
                    buf.as_mut_ptr(),
                    32,
                    8,
                    8,
                    4.0,
                    1.0,
                    0,
                    0
                ),
                PCL_ERR_INVALID_ARG
            );
            assert_eq!(
                pcl_native_blur_bgra32(
                    buf.as_ptr(),
                    28,
                    dst.as_mut_ptr(),
                    32,
                    8,
                    8,
                    4.0,
                    1.0,
                    0,
                    0
                ),
                PCL_ERR_INVALID_ARG
            );
            assert_eq!(
                pcl_native_blur_bgra32(
                    buf.as_ptr(),
                    32,
                    dst.as_mut_ptr(),
                    32,
                    8,
                    8,
                    0.0,
                    1.0,
                    0,
                    0
                ),
                PCL_ERR_INVALID_ARG
            );
            assert_eq!(
                pcl_native_blur_bgra32(
                    buf.as_ptr(),
                    32,
                    dst.as_mut_ptr(),
                    32,
                    8,
                    8,
                    4.0,
                    1.0,
                    99,
                    0
                ),
                PCL_ERR_INVALID_ARG
            );
        }
    }

    #[test]
    fn blur_handles_1x1_zero_rate_and_small_radius() {
        let src = [10u8, 20, 30, 255];
        let mut dst = [0u8; 4];
        // SAFETY: Valid 1x1 non-overlapping buffers.
        unsafe {
            assert_eq!(
                pcl_native_blur_bgra32(
                    src.as_ptr(),
                    4,
                    dst.as_mut_ptr(),
                    4,
                    1,
                    1,
                    0.5,
                    0.0,
                    PCL_RENDERING_BIAS_PERFORMANCE,
                    PCL_KERNEL_BOX
                ),
                PCL_OK
            );
            assert_eq!(dst, src);

            assert_eq!(
                pcl_native_blur_bgra32(
                    src.as_ptr(),
                    4,
                    dst.as_mut_ptr(),
                    4,
                    1,
                    1,
                    0.5,
                    1.0,
                    PCL_RENDERING_BIAS_QUALITY,
                    PCL_KERNEL_BOX
                ),
                PCL_OK
            );
            assert_eq!(dst, src);
        }
    }
}
