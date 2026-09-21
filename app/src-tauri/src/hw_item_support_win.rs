//! ItemSupport DWORDs from the C# registry roots. Tests inject a map; never the live hive.

use std::collections::HashMap;

use capabilities::ItemSupport;
use serde_json::Value;

/// C# `MechrevoDeviceCapabilities.ItemSupportPaths`. Canonical first.
pub const ITEM_SUPPORT_PATHS: &[&str] = &[
    r"SOFTWARE\OEM\GamingCenter2\ItemSupport",
    r"SOFTWARE\OEM\GamingCenter\ItemSupport",
    r"SOFTWARE\OEM\ControlCenter\ItemSupport",
];

/// C# `MechrevoDeviceCapabilities.GpuConfigPaths`. Canonical first.
pub const GPU_CONFIG_PATHS: &[&str] = &[
    r"SOFTWARE\OEM\GamingCenter2\MySetting\GpuConfig",
    r"SOFTWARE\OEM\GamingCenter\MySetting\GpuConfig",
    r"SOFTWARE\OEM\ControlCenter\MySetting\GpuConfig",
];

/// Parse registry-shaped values. First key wins (C# `TryAdd`, ignore-case).
pub fn item_support_from_values<I>(values: I) -> ItemSupport
where
    I: IntoIterator<Item = (String, Value)>,
{
    let mut map = HashMap::new();
    for (key, value) in values {
        map.entry(key.to_ascii_lowercase()).or_insert(value);
    }
    ItemSupport::from_map(map)
}

/// Live HKLM read. Tests must not call this; they inject a map fixture.
pub fn read_live_item_support() -> ItemSupport {
    item_support_from_values(read_live_pairs())
}

fn read_live_pairs() -> Vec<(String, Value)> {
    #[cfg(windows)]
    {
        read_windows_pairs()
    }
    #[cfg(not(windows))]
    {
        Vec::new()
    }
}

#[cfg(windows)]
fn read_windows_pairs() -> Vec<(String, Value)> {
    let mut pairs = Vec::new();
    let mut seen = HashMap::<String, ()>::new();
    for path in ITEM_SUPPORT_PATHS.iter().chain(GPU_CONFIG_PATHS) {
        for wow64_64 in [true, false] {
            for (name, value) in read_key(path, wow64_64) {
                let nk = name.to_ascii_lowercase();
                if seen.contains_key(&nk) {
                    continue;
                }
                seen.insert(nk, ());
                pairs.push((name, value));
            }
        }
    }
    pairs
}

#[cfg(windows)]
fn read_key(path: &str, wow64_64: bool) -> Vec<(String, Value)> {
    use winreg::enums::{HKEY_LOCAL_MACHINE, KEY_READ, KEY_WOW64_32KEY, KEY_WOW64_64KEY};
    use winreg::RegKey;
    let flags = KEY_READ | if wow64_64 { KEY_WOW64_64KEY } else { KEY_WOW64_32KEY };
    let hklm = RegKey::predef(HKEY_LOCAL_MACHINE);
    let Ok(key) = hklm.open_subkey_with_flags(path, flags) else {
        return Vec::new();
    };
    let mut out = Vec::new();
    let Ok(names) = key.enum_values().collect::<Result<Vec<_>, _>>() else {
        return Vec::new();
    };
    for (name, _) in names {
        if let Some(value) = dword_or_string(&key, &name) {
            out.push((name, value));
        }
    }
    out
}

#[cfg(windows)]
fn dword_or_string(key: &winreg::RegKey, name: &str) -> Option<Value> {
    if let Ok(value) = key.get_value::<u32, _>(name) {
        return Some(Value::Number(value.into()));
    }
    if let Ok(value) = key.get_value::<String, _>(name) {
        return Some(Value::String(value));
    }
    None
}
