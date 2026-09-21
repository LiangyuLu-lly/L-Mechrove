//! C# `GpuAdapterReader` + `DgpuGenerationProbe` port.
//!
//! Registry: `GpuGenerationProvider.cs:81-147`. Probe: `DgpuGeneration.cs:87-146`.
//! Tests inject adapter lists and must not call [`read_live_generation`].

use capabilities::{resolve_dgpu, DgpuGeneration, DgpuIdentity, GpuAdapter};

/// C# `GpuAdapterReader.DisplayClassPath` (`GpuGenerationProvider.cs:84-85`).
pub const DISPLAY_CLASS_PATH: &str =
    r"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

/// Pure resolver. C# `DgpuGenerationProbe.Resolve` (`DgpuGeneration.cs:87-111`).
pub fn resolve(adapters: &[GpuAdapter], enumeration_available: bool) -> DgpuIdentity {
    resolve_dgpu(adapters, enumeration_available)
}

/// Live HKLM display-class read. Tests inject adapters; they must not call this.
pub fn read_live_generation() -> DgpuGeneration {
    let (adapters, available) = read_adapters();
    resolve(&adapters, available).generation
}

fn read_adapters() -> (Vec<GpuAdapter>, bool) {
    #[cfg(windows)]
    {
        read_windows_adapters()
    }
    #[cfg(not(windows))]
    {
        (Vec::new(), false)
    }
}

/// C# `GpuAdapterReader.ReadFromRegistry` (`GpuGenerationProvider.cs:87-118`).
#[cfg(windows)]
fn read_windows_adapters() -> (Vec<GpuAdapter>, bool) {
    use winreg::enums::{HKEY_LOCAL_MACHINE, KEY_READ};
    use winreg::RegKey;

    let hklm = RegKey::predef(HKEY_LOCAL_MACHINE);
    let Ok(class_key) = hklm.open_subkey_with_flags(DISPLAY_CLASS_PATH, KEY_READ) else {
        return (Vec::new(), false);
    };
    let Ok(names) = class_key.enum_keys().collect::<Result<Vec<_>, _>>() else {
        return (Vec::new(), false);
    };

    let mut adapters = Vec::new();
    for sub_key_name in names {
        if !sub_key_name.as_bytes().iter().all(u8::is_ascii_digit) {
            continue;
        }
        let Ok(adapter_key) = class_key.open_subkey(&sub_key_name) else {
            continue;
        };
        let name = adapter_key
            .get_value::<String, _>("DriverDesc")
            .unwrap_or_else(|_| String::new());
        let hardware_id = read_hardware_id(&adapter_key);
        let (vendor_id, device_id) = parse_pci_ids(hardware_id.as_deref());
        if name.is_empty() && hardware_id.is_none() {
            continue;
        }
        adapters.push(GpuAdapter::new(
            name,
            vendor_id.as_deref(),
            device_id.as_deref(),
        ));
    }
    (adapters, true)
}

/// C# `ReadHardwareId` (`GpuGenerationProvider.cs:120-125`).
#[cfg(windows)]
fn read_hardware_id(key: &winreg::RegKey) -> Option<String> {
    if let Ok(matching) = key.get_value::<String, _>("MatchingDeviceId") {
        if !matching.is_empty() {
            return Some(matching);
        }
    }
    if let Ok(ids) = key.get_value::<Vec<String>, _>("HardwareID") {
        return ids.into_iter().next();
    }
    None
}

/// C# `TryParsePciIds` + `ExtractToken` (`GpuGenerationProvider.cs:128-146`).
#[cfg(windows)]
fn parse_pci_ids(hardware_id: Option<&str>) -> (Option<String>, Option<String>) {
    (
        extract_token(hardware_id, "VEN_"),
        extract_token(hardware_id, "DEV_"),
    )
}

#[cfg(windows)]
fn extract_token(text: Option<&str>, prefix: &str) -> Option<String> {
    let text = text.filter(|value| !value.trim().is_empty())?;
    let bytes = text.as_bytes();
    let prefix_bytes = prefix.as_bytes();
    let at = bytes
        .windows(prefix_bytes.len())
        .position(|window| window.eq_ignore_ascii_case(prefix_bytes))?;
    let start = at + prefix_bytes.len();
    let mut end = start;
    while end < bytes.len() && bytes[end].is_ascii_hexdigit() {
        end += 1;
    }
    if end - start < 4 {
        return None;
    }
    Some(text[start..start + 4].to_ascii_uppercase())
}
