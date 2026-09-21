//! Read-only leftover vendor GCU / official-console detection. Prompt only; never delete.

use serde::Serialize;

pub const VENDOR_SERVICE_NAME: &str = "GCUBridge";
const OUR_IMAGE_MARKER: &str = "l-mechrevo";
const VENDOR_UI_PROCESSES: &[&str] = &[
    "CCUWinUI",
    "SystrayComponent",
    "ControlCenterU",
    "GamingCenterU",
    "GCUUI",
];
const VENDOR_PACKAGE_PREFIX: &str = "CCU.WinUI_";
const VENDOR_LEGACY_EXE: &str = "ControlCenterU.exe";
const SERVICE_KEY_PREFIX: &str = r"SYSTEM\CurrentControlSet\Services\";
const OEM_WALK_DEPTH: usize = 8;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GcuCoexistenceKind {
    None,
    LeftoverBridge,
    VendorUi,
    Both,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GcuCoexistenceStatus {
    pub leftover_bridge: bool,
    pub vendor_ui: bool,
    pub requires_prompt: bool,
}

pub const fn classify(leftover_bridge: bool, vendor_ui: bool) -> GcuCoexistenceKind {
    match (leftover_bridge, vendor_ui) {
        (true, true) => GcuCoexistenceKind::Both,
        (true, false) => GcuCoexistenceKind::LeftoverBridge,
        (false, true) => GcuCoexistenceKind::VendorUi,
        (false, false) => GcuCoexistenceKind::None,
    }
}

pub const fn requires_console_removal_prompt(kind: GcuCoexistenceKind) -> bool {
    match kind {
        GcuCoexistenceKind::None => false,
        GcuCoexistenceKind::LeftoverBridge
        | GcuCoexistenceKind::VendorUi
        | GcuCoexistenceKind::Both => true,
    }
}

pub const fn status_from_detection(leftover_bridge: bool, vendor_ui: bool) -> GcuCoexistenceStatus {
    GcuCoexistenceStatus {
        leftover_bridge,
        vendor_ui,
        requires_prompt: requires_console_removal_prompt(classify(leftover_bridge, vendor_ui)),
    }
}

pub fn is_our_image_path(path: &str) -> bool {
    path.to_ascii_lowercase().contains(OUR_IMAGE_MARKER)
}

pub fn is_leftover_vendor_service(image_path: Option<&str>) -> bool {
    match image_path {
        Some(path) if !path.trim().is_empty() => !is_our_image_path(path),
        _ => false,
    }
}

pub fn probe_status() -> GcuCoexistenceStatus {
    status_from_detection(detect_leftover_bridge(), detect_vendor_ui())
}

fn detect_leftover_bridge() -> bool {
    is_leftover_vendor_service(read_service_image_path().as_deref())
}

fn detect_vendor_ui() -> bool {
    vendor_ui_installed() || vendor_ui_running()
}

fn read_service_image_path() -> Option<String> {
    #[cfg(windows)]
    {
        read_service_image_path_windows()
    }
    #[cfg(not(windows))]
    {
        None
    }
}

#[cfg(windows)]
fn read_service_image_path_windows() -> Option<String> {
    use winreg::enums::{HKEY_LOCAL_MACHINE, KEY_READ};
    use winreg::RegKey;
    let hklm = RegKey::predef(HKEY_LOCAL_MACHINE);
    let key_path = format!("{SERVICE_KEY_PREFIX}{VENDOR_SERVICE_NAME}");
    let key = hklm.open_subkey_with_flags(&key_path, KEY_READ).ok()?;
    key.get_value::<String, _>("ImagePath").ok()
}

fn vendor_ui_installed() -> bool {
    let program_files = std::env::var("ProgramFiles")
        .ok()
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| r"C:\Program Files".to_owned());
    let windows_apps = std::path::Path::new(&program_files).join("WindowsApps");
    if package_dir_present(&windows_apps) {
        return true;
    }
    find_file_named(
        &std::path::Path::new(&program_files).join("OEM"),
        VENDOR_LEGACY_EXE,
        OEM_WALK_DEPTH,
    )
}

fn package_dir_present(windows_apps: &std::path::Path) -> bool {
    let Ok(entries) = std::fs::read_dir(windows_apps) else {
        return false;
    };
    entries.flatten().any(|entry| {
        entry
            .file_name()
            .to_string_lossy()
            .starts_with(VENDOR_PACKAGE_PREFIX)
    })
}

fn find_file_named(root: &std::path::Path, file_name: &str, remaining: usize) -> bool {
    if remaining == 0 {
        return false;
    }
    let Ok(entries) = std::fs::read_dir(root) else {
        return false;
    };
    for entry in entries.flatten() {
        let Ok(file_type) = entry.file_type() else {
            continue;
        };
        if file_type.is_dir() {
            if find_file_named(&entry.path(), file_name, remaining - 1) {
                return true;
            }
            continue;
        }
        if entry.file_name().eq_ignore_ascii_case(file_name) {
            return true;
        }
    }
    false
}

fn vendor_ui_running() -> bool {
    #[cfg(windows)]
    {
        VENDOR_UI_PROCESSES
            .iter()
            .any(|stem| process_image_running(stem))
    }
    #[cfg(not(windows))]
    {
        false
    }
}

#[cfg(windows)]
fn process_image_running(stem: &str) -> bool {
    use std::os::windows::process::CommandExt;
    use std::process::Command;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    let image = format!("{stem}.exe");
    let filter = format!("IMAGENAME eq {image}");
    let Ok(output) = Command::new("tasklist")
        .args(["/FI", &filter, "/NH", "/FO", "CSV"])
        .creation_flags(CREATE_NO_WINDOW)
        .output()
    else {
        return false;
    };
    if !output.status.success() {
        return false;
    }
    String::from_utf8_lossy(&output.stdout)
        .to_ascii_lowercase()
        .contains(&image.to_ascii_lowercase())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn serialized_status_exposes_first_run_requires_prompt() {
        let json = serde_json::to_value(status_from_detection(true, false)).expect("serialize");
        assert_eq!(json["requiresPrompt"], true);
        assert_eq!(json.get("requires_prompt"), None);
    }
}
