//! DisplayRouteMatrix port: Gen30/40/50 offer table. No Auto. Hot-swap 50-only.

use std::fs;
use std::path::PathBuf;

use capabilities::{
    from_device_id, from_marketing_name, resolve_dgpu, DgpuGeneration, FeatureMatrix, GpuAdapter,
    GpuRouteGate, ItemSupport, HOT_SWAP_OFF, HOT_SWAP_ON, IGPU_ONLY_AUTO, IGPU_ONLY_OFF,
    IGPU_ONLY_ON, RESTART, TOGGLE_IGPU, TOGGLE_OFF, TOGGLE_ON,
};
use serde::Deserialize;

fn golden_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("_golden")
        .join("gpu")
}

fn read_golden(name: &str) -> String {
    let path = golden_dir().join(name);
    fs::read_to_string(&path).unwrap_or_else(|err| {
        panic!("read golden {}: {err}", path.display());
    })
}

#[derive(Debug, Deserialize)]
struct Gen40Golden {
    generation: u16,
    tiers: Vec<Gen40Tier>,
}

#[derive(Debug, Deserialize)]
struct Gen40Tier {
    #[serde(rename = "threeMode")]
    three_mode: bool,
    actions: Vec<String>,
}

#[derive(Debug, Deserialize)]
struct Gen50Golden {
    generation: u16,
    #[serde(rename = "allowsHotSwap")]
    allows_hot_swap: bool,
    actions: Vec<String>,
}

fn gate(generation: DgpuGeneration, three_mode: bool, hot_swap: bool) -> GpuRouteGate {
    GpuRouteGate {
        generation,
        three_mode,
        hot_swap,
    }
}

fn offered(generation: DgpuGeneration, three_mode: bool, hot_swap: bool) -> Vec<&'static str> {
    gate(generation, three_mode, hot_swap)
        .offered_actions()
        .to_vec()
}

fn parse_item_support(json: &str) -> ItemSupport {
    ItemSupport::parse_json(json).unwrap_or_else(|err| panic!("parse ItemSupport: {err}"))
}

#[test]
fn unknown_and_no_dgpu_offer_no_actions() {
    for generation in [DgpuGeneration::Unknown, DgpuGeneration::NoDgpu] {
        let actions = offered(generation, true, true);
        assert!(
            actions.is_empty(),
            "{generation:?} must fail-closed even when caps look fully enabled"
        );
        assert!(!gate(generation, true, true).allows(TOGGLE_ON));
        assert!(!gate(generation, true, true).allows(HOT_SWAP_ON));
    }
}

#[test]
fn gen30_offered_actions_exclude_igpu_only_when_three_mode_true() {
    let actions = offered(DgpuGeneration::Gen30, true, true);
    assert_eq!(actions, vec![TOGGLE_ON, TOGGLE_OFF]);
    assert!(!actions.iter().any(|action| action.contains("IGPU_ONLY")));
    assert!(!actions.iter().any(|action| action.contains("HOTSWAP")));
    assert!(!actions.contains(&RESTART));
    assert!(!actions.contains(&TOGGLE_IGPU));
}

#[test]
fn gen40_offered_actions_match_golden_tiers() {
    let golden: Gen40Golden = serde_json::from_str(&read_golden("gen40_actions.json")).unwrap();
    assert_eq!(golden.generation, 40);
    for tier in golden.tiers {
        let actions = offered(DgpuGeneration::Gen40, tier.three_mode, true);
        assert_eq!(
            actions,
            tier.actions.iter().map(String::as_str).collect::<Vec<_>>(),
            "Gen40 three_mode={}",
            tier.three_mode
        );
        assert!(!actions.contains(&IGPU_ONLY_AUTO));
        assert!(!actions.iter().any(|action| action.contains("HOTSWAP")));
    }
}

#[test]
fn gen50_offered_actions_are_golden_without_auto_when_hot_swap_flags() {
    let golden: Gen50Golden = serde_json::from_str(&read_golden("gen50_hotswap.json")).unwrap();
    assert_eq!(golden.generation, 50);
    assert!(golden.allows_hot_swap);
    assert!(
        golden.actions.iter().any(|action| action == IGPU_ONLY_AUTO),
        "vendor Gen50 vocab includes AUTO; product must still refuse it"
    );

    let expected: Vec<&str> = golden
        .actions
        .iter()
        .map(String::as_str)
        .filter(|action| *action != IGPU_ONLY_AUTO)
        .collect();
    let actions = offered(DgpuGeneration::Gen50, true, true);
    assert_eq!(actions, expected);
    assert!(!actions.contains(&IGPU_ONLY_AUTO));
}

#[test]
fn igpu_only_auto_never_offered() {
    let generations = [
        DgpuGeneration::Unknown,
        DgpuGeneration::NoDgpu,
        DgpuGeneration::Gen30,
        DgpuGeneration::Gen40,
        DgpuGeneration::Gen50,
    ];
    for generation in generations {
        for three_mode in [false, true] {
            for hot_swap in [false, true] {
                let route = gate(generation, three_mode, hot_swap);
                assert!(
                    !route.allows(IGPU_ONLY_AUTO),
                    "{generation:?} three_mode={three_mode} hot_swap={hot_swap} offered AUTO"
                );
                assert!(!route.offered_actions().contains(&IGPU_ONLY_AUTO));
            }
        }
    }
}

#[test]
fn gen50_keeps_igpu_only_when_three_mode_false() {
    let actions = offered(DgpuGeneration::Gen50, false, false);
    assert!(actions.contains(&IGPU_ONLY_ON));
    assert!(actions.contains(&IGPU_ONLY_OFF));
    assert!(actions.contains(&TOGGLE_IGPU));
    assert!(actions.contains(&RESTART));
    assert!(!actions.contains(&IGPU_ONLY_AUTO));
    assert!(!actions.contains(&HOT_SWAP_ON));
    assert!(!actions.contains(&HOT_SWAP_OFF));
}

#[test]
fn hot_swap_offered_only_when_gen50_and_both_itemsupport_flags() {
    let both = parse_item_support(r#"{"GpuHotSwapSwitchSupport":1,"lgpuHotSwapSwitchStatus":1}"#);
    let switch_only = parse_item_support(r#"{"GpuHotSwapSwitchSupport":1}"#);
    let status_only = parse_item_support(r#"{"lgpuHotSwapSwitchStatus":1}"#);
    let both_false =
        parse_item_support(r#"{"GpuHotSwapSwitchSupport":0,"lgpuHotSwapSwitchStatus":0}"#);

    let offered_both = GpuRouteGate::from_matrix(
        DgpuGeneration::Gen50,
        true,
        &FeatureMatrix::from_values(&both),
    );
    assert!(offered_both.allows(HOT_SWAP_ON));
    assert!(offered_both.allows(HOT_SWAP_OFF));

    for map in [&switch_only, &status_only, &both_false] {
        let route = GpuRouteGate::from_matrix(
            DgpuGeneration::Gen50,
            true,
            &FeatureMatrix::from_values(map),
        );
        assert!(
            !route.allows(HOT_SWAP_ON),
            "partial/false flags must not offer hot-swap"
        );
        assert!(!route.allows(HOT_SWAP_OFF));
    }
}

#[test]
fn gen40_never_offers_hot_swap_when_itemsupport_flags_true() {
    let both = parse_item_support(r#"{"GpuHotSwapSwitchSupport":1,"lgpuHotSwapSwitchStatus":1}"#);
    let route = GpuRouteGate::from_matrix(
        DgpuGeneration::Gen40,
        true,
        &FeatureMatrix::from_values(&both),
    );
    assert!(!route.allows(HOT_SWAP_ON));
    assert!(!route.allows(HOT_SWAP_OFF));
    assert!(route.allows(IGPU_ONLY_ON));
    assert!(route.allows(IGPU_ONLY_OFF));
    assert!(!route.allows(IGPU_ONLY_AUTO));
}

fn nvidia(name: &str, device_id: &str) -> GpuAdapter {
    GpuAdapter::new(name, Some("10DE"), Some(device_id))
}

#[test]
fn marketing_name_resolves_gen40_when_rtx_4060_laptop() {
    // Given: C# DgpuGeneration.cs:114-129 `\bRTX\s*(\d{4})\b` on a 40-series marketing name
    let name = "NVIDIA GeForce RTX 4060 Laptop GPU";

    // When: parse the marketing name and resolve an NVIDIA adapter
    let by_name = from_marketing_name(name);
    let identity = resolve_dgpu(&[nvidia(name, "0000")], true);

    // Then: Gen40, has_dgpu
    assert_eq!(by_name, Some(DgpuGeneration::Gen40));
    assert_eq!(identity.generation, DgpuGeneration::Gen40);
    assert!(identity.has_dgpu);
}

#[test]
fn marketing_name_resolves_gen30_when_rtx_3070() {
    // Given: C# DgpuGeneration.cs:124 "30" prefix
    let name = "GeForce RTX 3070";

    // When
    let by_name = from_marketing_name(name);
    let identity = resolve_dgpu(&[nvidia(name, "0000")], true);

    // Then
    assert_eq!(by_name, Some(DgpuGeneration::Gen30));
    assert_eq!(identity.generation, DgpuGeneration::Gen30);
    assert!(identity.has_dgpu);
}

#[test]
fn marketing_name_resolves_gen50_when_rtx_5080_laptop() {
    // Given: C# DgpuGeneration.cs:126 "50" prefix
    let name = "GeForce RTX 5080 Laptop GPU";

    // When
    let by_name = from_marketing_name(name);
    let identity = resolve_dgpu(&[nvidia(name, "0000")], true);

    // Then
    assert_eq!(by_name, Some(DgpuGeneration::Gen50));
    assert_eq!(identity.generation, DgpuGeneration::Gen50);
    assert!(identity.has_dgpu);
}

#[test]
fn pci_high_byte_resolves_gen30_when_band_20_to_25() {
    // Given: C# DgpuGeneration.cs:141 Ampere GA10x 0x20..=0x25
    for high in 0x20u8..=0x25 {
        let device = format!("{high:02X}00");

        // When: empty marketing name, NVIDIA device-id only
        let by_id = from_device_id(&device);
        let identity = resolve_dgpu(&[nvidia("", &device)], true);

        // Then
        assert_eq!(by_id, Some(DgpuGeneration::Gen30), "high=0x{high:02X}");
        assert_eq!(
            identity.generation,
            DgpuGeneration::Gen30,
            "high=0x{high:02X}"
        );
        assert!(identity.has_dgpu);
    }
}

#[test]
fn pci_high_byte_resolves_gen40_when_band_26_to_28() {
    // Given: C# DgpuGeneration.cs:142 Ada AD10x 0x26..=0x28
    for high in 0x26u8..=0x28 {
        let device = format!("{high:02X}00");

        // When
        let by_id = from_device_id(&device);
        let identity = resolve_dgpu(&[nvidia("", &device)], true);

        // Then
        assert_eq!(by_id, Some(DgpuGeneration::Gen40), "high=0x{high:02X}");
        assert_eq!(
            identity.generation,
            DgpuGeneration::Gen40,
            "high=0x{high:02X}"
        );
        assert!(identity.has_dgpu);
    }
}

#[test]
fn pci_high_byte_resolves_gen50_when_band_2b_to_30() {
    // Given: C# DgpuGeneration.cs:143 Blackwell GB20x 0x2B..=0x30
    for high in 0x2Bu8..=0x30 {
        let device = format!("{high:02X}00");

        // When
        let by_id = from_device_id(&device);
        let identity = resolve_dgpu(&[nvidia("", &device)], true);

        // Then
        assert_eq!(by_id, Some(DgpuGeneration::Gen50), "high=0x{high:02X}");
        assert_eq!(
            identity.generation,
            DgpuGeneration::Gen50,
            "high=0x{high:02X}"
        );
        assert!(identity.has_dgpu);
    }
}

#[test]
fn resolve_is_unknown_when_enumeration_unavailable() {
    // Given: C# DgpuGeneration.cs:91 — enumeration itself failed
    let adapters = [nvidia("NVIDIA GeForce RTX 4060 Laptop GPU", "2882")];

    // When
    let identity = resolve_dgpu(&adapters, false);

    // Then: Unknown, never NoDgpu, has_dgpu false (C# DgpuIdentity.Unknown)
    assert_eq!(identity.generation, DgpuGeneration::Unknown);
    assert_ne!(identity.generation, DgpuGeneration::NoDgpu);
    assert!(!identity.has_dgpu);
}

#[test]
fn resolve_is_no_dgpu_when_nvidia_list_empty_and_enumeration_works() {
    // Given: C# DgpuGeneration.cs:93-94 — enum available, no NVIDIA
    let adapters = [
        GpuAdapter::new("Intel(R) UHD Graphics", Some("8086"), Some("46A6")),
        GpuAdapter::new("AMD Radeon 780M", Some("1002"), Some("15BF")),
    ];

    // When
    let identity = resolve_dgpu(&adapters, true);
    let empty = resolve_dgpu(&[], true);

    // Then
    assert_eq!(identity.generation, DgpuGeneration::NoDgpu);
    assert!(!identity.has_dgpu);
    assert_eq!(empty.generation, DgpuGeneration::NoDgpu);
    assert!(!empty.has_dgpu);
}

#[test]
fn resolve_is_unknown_with_has_dgpu_when_nvidia_unparseable() {
    // Given: C# DgpuGeneration.cs:108-110 — NVIDIA present, name and device-id miss the domain
    let adapters = [GpuAdapter::new(
        "NVIDIA RTX A5000 Laptop GPU",
        Some("10DE"),
        Some("1FB8"),
    )];

    // When
    let identity = resolve_dgpu(&adapters, true);

    // Then
    assert_eq!(identity.generation, DgpuGeneration::Unknown);
    assert!(identity.has_dgpu);
}
