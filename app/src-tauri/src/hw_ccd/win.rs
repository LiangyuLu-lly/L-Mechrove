//! Live user32 CCD. Same APIs as `ScreenCCD.GetHDRStatus` (ScreenCCD.cs:11-99).

use super::{
    hdr_acm_from_active_color_mode, hdr_acm_from_legacy_color_info, is_advanced_color_enabled,
    AdvancedColorQuery,
};
use std::mem::size_of;
use std::ptr;

const QDC_ONLY_ACTIVE_PATHS: u32 = 0x2;
const ERROR_INSUFFICIENT_BUFFER: i32 = 122;
const GET_TARGET_NAME: i32 = 2;
const GET_ADVANCED_COLOR_INFO: i32 = 9;
const GET_ADVANCED_COLOR_INFO_2: i32 = 15;
const OUTPUT_DP_EMBEDDED: i32 = 11;
const OUTPUT_INTERNAL: i32 = 0x8000_0000_u32 as i32;

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct Luid {
    low_part: u32,
    high_part: i32,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct DeviceInfoHeader {
    info_type: i32,
    size: u32,
    adapter_id: Luid,
    id: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct PathSourceInfo {
    adapter_id: Luid,
    id: u32,
    mode_info_idx: u32,
    status_flags: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct Rational {
    numerator: u32,
    denominator: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct PathTargetInfo {
    adapter_id: Luid,
    id: u32,
    mode_info_idx: u32,
    output_technology: i32,
    rotation: u32,
    scaling: u32,
    refresh_rate: Rational,
    scan_line_ordering: u32,
    target_available: i32,
    status_flags: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct PathInfo {
    _source_info: PathSourceInfo,
    target_info: PathTargetInfo,
    _flags: u32,
}

#[repr(C)]
#[derive(Clone, Copy)]
struct ModeInfo {
    _raw: [u8; 64],
}

impl Default for ModeInfo {
    fn default() -> Self {
        Self { _raw: [0; 64] }
    }
}

#[repr(C)]
struct TargetDeviceName {
    header: DeviceInfoHeader,
    flags: u32,
    output_technology: i32,
    edid_manufacture_id: u16,
    edid_product_code_id: u16,
    connector_instance: u32,
    monitor_friendly_device_name: [u16; 64],
    monitor_device_path: [u16; 128],
}

impl Default for TargetDeviceName {
    fn default() -> Self {
        Self {
            header: DeviceInfoHeader::default(),
            flags: 0,
            output_technology: 0,
            edid_manufacture_id: 0,
            edid_product_code_id: 0,
            connector_instance: 0,
            monitor_friendly_device_name: [0; 64],
            monitor_device_path: [0; 128],
        }
    }
}

#[repr(C)]
#[derive(Default)]
struct AdvancedColorInfo {
    header: DeviceInfoHeader,
    value: u32,
    color_encoding: u32,
    bits_per_color_channel: i32,
}

#[repr(C)]
#[derive(Default)]
struct AdvancedColorInfo2 {
    header: DeviceInfoHeader,
    value: u32,
    color_encoding: u32,
    bits_per_color_channel: i32,
    active_color_mode: i32,
}

#[link(name = "user32")]
extern "system" {
    fn GetDisplayConfigBufferSizes(flags: u32, num_path: *mut u32, num_mode: *mut u32) -> i32;
    fn QueryDisplayConfig(
        flags: u32,
        num_path: *mut u32,
        paths: *mut PathInfo,
        num_mode: *mut u32,
        modes: *mut ModeInfo,
        topology: *mut core::ffi::c_void,
    ) -> i32;
    fn DisplayConfigGetDeviceInfo(header: *mut DeviceInfoHeader) -> i32;
}

pub fn query() -> AdvancedColorQuery {
    let Some((paths, path_count)) = active_paths() else {
        return AdvancedColorQuery::Unavailable;
    };
    let mut hdr = false;
    let mut acm = false;
    for path in paths.iter().take(path_count) {
        let Some(tech) = target_output_technology(path) else {
            continue;
        };
        if !is_internal_output(tech) {
            continue;
        }
        let adapter = path.target_info.adapter_id;
        let id = path.target_info.id;
        if let Some((h, a)) = color_v2(adapter, id) {
            hdr |= h;
            acm |= a;
            continue;
        }
        if let Some((h, a)) = color_v1(adapter, id) {
            hdr |= h;
            acm |= a;
        }
    }
    if is_advanced_color_enabled(hdr, acm) {
        AdvancedColorQuery::Enabled
    } else {
        AdvancedColorQuery::Disabled
    }
}

fn is_internal_output(tech: i32) -> bool {
    tech == OUTPUT_INTERNAL || tech == OUTPUT_DP_EMBEDDED
}

fn active_paths() -> Option<(Vec<PathInfo>, usize)> {
    let mut path_count = 0u32;
    let mut mode_count = 0u32;
    let mut err = 0i32;
    let mut paths = Vec::new();
    let mut modes = Vec::new();
    for _ in 0..3 {
        // SAFETY: [Category 8 — FFI boundary]
        // Out-counts are stack locals. QDC_ONLY_ACTIVE_PATHS matches ScreenCCD.cs:21.
        err = unsafe {
            GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &mut path_count, &mut mode_count)
        };
        if err != 0 {
            break;
        }
        paths.resize_with(path_count as usize, PathInfo::default);
        modes.resize_with(mode_count as usize, ModeInfo::default);
        // SAFETY: [Category 8 — FFI boundary]
        // Buffers are sized from the previous call. topology is null as in ScreenCCD.cs:25.
        err = unsafe {
            QueryDisplayConfig(
                QDC_ONLY_ACTIVE_PATHS,
                &mut path_count,
                paths.as_mut_ptr(),
                &mut mode_count,
                modes.as_mut_ptr(),
                ptr::null_mut(),
            )
        };
        if err != ERROR_INSUFFICIENT_BUFFER {
            break;
        }
    }
    if err != 0 {
        return None;
    }
    Some((paths, path_count as usize))
}

fn target_output_technology(path: &PathInfo) -> Option<i32> {
    let mut info = TargetDeviceName::default();
    info.header.info_type = GET_TARGET_NAME;
    info.header.size = size_of::<TargetDeviceName>() as u32;
    info.header.adapter_id = path.target_info.adapter_id;
    info.header.id = path.target_info.id;
    // SAFETY: [Category 8 — FFI boundary]
    // Packet starts at `header`; size is the full TARGET_DEVICE_NAME (ScreenCCD.cs:42-47).
    let err = unsafe { DisplayConfigGetDeviceInfo(&mut info.header) };
    if err != 0 {
        return None;
    }
    Some(info.output_technology)
}

fn color_v2(adapter: Luid, id: u32) -> Option<(bool, bool)> {
    let mut info = AdvancedColorInfo2::default();
    info.header.info_type = GET_ADVANCED_COLOR_INFO_2;
    info.header.size = size_of::<AdvancedColorInfo2>() as u32;
    info.header.adapter_id = adapter;
    info.header.id = id;
    // SAFETY: [Category 8 — FFI boundary]
    // Packet type 15; unsupported systems reject it (ScreenCCD.cs:61-67).
    let err = unsafe { DisplayConfigGetDeviceInfo(&mut info.header) };
    if err != 0 {
        return None;
    }
    Some(hdr_acm_from_active_color_mode(info.active_color_mode))
}

fn color_v1(adapter: Luid, id: u32) -> Option<(bool, bool)> {
    let mut info = AdvancedColorInfo::default();
    info.header.info_type = GET_ADVANCED_COLOR_INFO;
    info.header.size = size_of::<AdvancedColorInfo>() as u32;
    info.header.adapter_id = adapter;
    info.header.id = id;
    // SAFETY: [Category 8 — FFI boundary]
    // Packet type 9 legacy path (ScreenCCD.cs:77-82).
    let err = unsafe { DisplayConfigGetDeviceInfo(&mut info.header) };
    if err != 0 {
        return None;
    }
    let enabled = info.value & 0x2 != 0;
    let wide = info.value & 0x4 != 0;
    Some(hdr_acm_from_legacy_color_info(
        enabled,
        wide,
        info.bits_per_color_channel,
    ))
}
