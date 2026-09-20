//! Official ItemSupport lighting catalogs. English MQTT effect IDs only.

use capabilities::{effect_allowed, keyboard_catalog, lightbar_catalog, logo_catalog};

const GCU11: [&str; 11] = [
    "Single",
    "Breathing",
    "Wave",
    "Reactive",
    "Rainbow",
    "Ripple",
    "Raindrop",
    "Marquee",
    "Spark",
    "Aurora",
    "Gaming",
];

#[test]
fn keyboard_catalog_len_2_when_type_1_or_2() {
    assert_eq!(keyboard_catalog(1), ["Single", "Breathing"]);
    assert_eq!(keyboard_catalog(2), ["Single", "Breathing"]);
    assert_eq!(keyboard_catalog(1).len(), 2);
    assert_eq!(keyboard_catalog(2).len(), 2);
}

#[test]
fn keyboard_catalog_len_11_when_type_0_or_at_least_3() {
    assert_eq!(keyboard_catalog(0), GCU11);
    assert_eq!(keyboard_catalog(3), GCU11);
    assert_eq!(keyboard_catalog(0).len(), 11);
    assert_eq!(keyboard_catalog(3).len(), 11);
}

#[test]
fn lightbar_catalog_is_five_english_ids() {
    assert_eq!(
        lightbar_catalog(),
        ["Single", "Breathing", "Wave", "Impact", "Raindrop"]
    );
}

#[test]
fn logo_catalog_is_three_english_ids() {
    assert_eq!(logo_catalog(), ["Single", "Breathing", "Mix"]);
}

#[test]
fn effect_allowed_matches_official_channel_catalogs() {
    assert!(!effect_allowed("keyboard", 2, "Wave"));
    assert!(effect_allowed("keyboard", 0, "Wave"));
    assert!(effect_allowed("keyboard", 3, "Wave"));
    assert!(!effect_allowed("lightbar", 0, "Mix"));
    assert!(effect_allowed("lightbar", 0, "Impact"));
    assert!(!effect_allowed("logo", 0, "Wave"));
    assert!(effect_allowed("logo", 0, "Mix"));
}
