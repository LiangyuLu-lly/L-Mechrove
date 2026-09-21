//! Given a map fixture. When item_support_from_values. Then ItemSupport matches C# DWORD flags.

use std::collections::HashMap;

use app_lib::hw_item_support_win::{
    item_support_from_values, GPU_CONFIG_PATHS, ITEM_SUPPORT_PATHS,
};
use serde_json::json;

#[test]
fn item_support_from_values_reads_dword_truthy_keys() {
    // Given: a registry-shaped map fixture (never the live hive)
    let mut values = HashMap::new();
    values.insert("LightbarSupport".to_owned(), json!(1));
    values.insert("IsTurboSubModeSupport".to_owned(), json!(1));
    values.insert("LiquidCoolingSupport".to_owned(), json!(0));

    // When: the map is parsed
    let item = item_support_from_values(values);

    // Then: DWORD 1 is truthy and 0 is not
    assert!(item.is_truthy("LightbarSupport"));
    assert!(item.is_truthy("IsTurboSubModeSupport"));
    assert!(!item.is_truthy("LiquidCoolingSupport"));
}

#[test]
fn item_support_from_values_first_wins_on_duplicate_keys() {
    // Given: canonical GamingCenter2 then a legacy alias for the same value
    let values = [
        ("LightbarSupport".to_owned(), json!(1)),
        ("lightbarsupport".to_owned(), json!(0)),
    ];

    // When: layers are merged first-wins
    let item = item_support_from_values(values);

    // Then: the first DWORD is kept
    assert!(
        item.is_truthy("LightbarSupport"),
        "C# TryAdd keeps the canonical value"
    );
}

#[test]
fn item_support_paths_match_csharp_roots() {
    // Given: C# MechrevoDeviceCapabilities ItemSupportPaths + GpuConfigPaths
    // When: the Rust constants are read
    // Then: roots and aliases match
    assert_eq!(
        ITEM_SUPPORT_PATHS,
        &[
            r"SOFTWARE\OEM\GamingCenter2\ItemSupport",
            r"SOFTWARE\OEM\GamingCenter\ItemSupport",
            r"SOFTWARE\OEM\ControlCenter\ItemSupport",
        ]
    );
    assert_eq!(
        GPU_CONFIG_PATHS,
        &[
            r"SOFTWARE\OEM\GamingCenter2\MySetting\GpuConfig",
            r"SOFTWARE\OEM\GamingCenter\MySetting\GpuConfig",
            r"SOFTWARE\OEM\ControlCenter\MySetting\GpuConfig",
        ]
    );
}
