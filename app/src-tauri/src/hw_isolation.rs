//! Official console autostart isolation. Same surfaces as OfficialConsoleIsolation.cs.

use crate::hw_backend::HostError;

mod win;

pub const DISABLED_STARTUP_SUFFIX: &str = ".lmechrevo-disabled";
pub const PROTECTED_PROCESS: &str = "L-Mechrevo";
pub const VENDOR_UI_PROCESS_NAMES: &[&str] = &[
    "CCUWinUI",
    "SystrayComponent",
    "ControlCenterU",
    "GamingCenterU",
    "GCUUI",
];
pub const OFFICIAL_MARKERS: &[&str] = &[
    "CCUWinUI.exe",
    "SystrayComponent.exe",
    "ControlCenterU.exe",
    "GamingCenterU.exe",
    "GCUUI.exe",
    "CCU.WinUI_",
    "CCU.WinUI_wrbgcf7aesyd8",
];
pub const RUN_KEY_PATHS: &[&str] = &[
    r"Software\Microsoft\Windows\CurrentVersion\Run",
    r"Software\Microsoft\Windows\CurrentVersion\RunOnce",
];

pub fn process_stem(name: &str) -> &str {
    let trimmed = name.trim();
    if trimmed.len() >= 4 && trimmed[trimmed.len() - 4..].eq_ignore_ascii_case(".exe") {
        &trimmed[..trimmed.len() - 4]
    } else {
        trimmed
    }
}

pub fn is_isolation_process_target(process_name: &str, own_exe_stem: &str) -> bool {
    let name = process_stem(process_name);
    let own = process_stem(own_exe_stem);
    if name.is_empty() || name.eq_ignore_ascii_case(PROTECTED_PROCESS) {
        return false;
    }
    if !own.is_empty() && name.eq_ignore_ascii_case(own) {
        return false;
    }
    VENDOR_UI_PROCESS_NAMES
        .iter()
        .any(|vendor| name.eq_ignore_ascii_case(vendor))
}

pub fn is_official_reference(value: &str) -> bool {
    let lower = value.to_ascii_lowercase();
    OFFICIAL_MARKERS
        .iter()
        .any(|marker| lower.contains(&marker.to_ascii_lowercase()))
}

pub fn is_allowed_run_key_path(key_path: &str) -> bool {
    RUN_KEY_PATHS
        .iter()
        .any(|allowed| allowed.eq_ignore_ascii_case(key_path))
}

pub fn is_allowed_registry_hive(hive: &str) -> bool {
    hive == "HKLM" || hive == "HKCU"
}

pub fn is_allowed_registry_value_name(name: &str) -> bool {
    let trimmed = name.trim();
    !trimmed.is_empty() && !trimmed.contains('\\') && !trimmed.contains('/')
}

pub fn is_restorable_registry_entry(hive: &str, key_path: &str, name: &str) -> bool {
    is_allowed_registry_hive(hive)
        && is_allowed_run_key_path(key_path)
        && is_allowed_registry_value_name(name)
}

pub fn is_restorable_startup_file(original: &str, disabled: &str, folders: &[&str]) -> bool {
    if original.is_empty() || disabled.is_empty() {
        return false;
    }
    if disabled != format!("{original}{DISABLED_STARTUP_SUFFIX}") {
        return false;
    }
    let Some(parent) = std::path::Path::new(original).parent() else {
        return false;
    };
    folders.iter().any(|folder| {
        let folder = folder.trim_end_matches(['\\', '/']);
        parent.to_string_lossy().eq_ignore_ascii_case(folder)
    })
}

pub fn planned_reg_delete_args(key: &str, name: &str) -> Vec<String> {
    vec![
        "delete".to_owned(),
        key.to_owned(),
        "/v".to_owned(),
        name.to_owned(),
        "/f".to_owned(),
    ]
}

pub fn planned_reg_add_string_args(key: &str, name: &str, value: &str) -> Vec<String> {
    vec![
        "add".to_owned(),
        key.to_owned(),
        "/v".to_owned(),
        name.to_owned(),
        "/t".to_owned(),
        "REG_SZ".to_owned(),
        "/d".to_owned(),
        value.to_owned(),
        "/f".to_owned(),
    ]
}

/// Real path disables/restores vendor Run/RunOnce and Startup-folder entries.
/// It never enumerates or stops processes, so L-Mechrevo and our own exe cannot
/// be killed here.
pub fn apply_real(on: bool) -> Result<(), HostError> {
    win::apply(on)
}
