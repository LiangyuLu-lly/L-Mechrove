//! Official GCU lighting effect catalogs. English MQTT IDs only.

const SINGLE_ZONE: &[&str] = &["Single", "Breathing"];

const GCU11: &[&str] = &[
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

const LIGHTBAR: &[&str] = &["Single", "Breathing", "Wave", "Impact", "Raindrop"];

const LOGO: &[&str] = &["Single", "Breathing", "Mix"];

/// KeyboardType 1|2 → single-zone pair. 0 (unknown) or ≥3 → GCU 11.
pub const fn keyboard_catalog(keyboard_type: i64) -> &'static [&'static str] {
    match keyboard_type {
        1 | 2 => SINGLE_ZONE,
        _ => GCU11,
    }
}

pub const fn lightbar_catalog() -> &'static [&'static str] {
    LIGHTBAR
}

pub const fn logo_catalog() -> &'static [&'static str] {
    LOGO
}

pub fn effect_allowed(channel: &str, keyboard_type: i64, effect: &str) -> bool {
    let catalog = match channel {
        "keyboard" => keyboard_catalog(keyboard_type),
        "lightbar" => lightbar_catalog(),
        "logo" => logo_catalog(),
        _ => return false,
    };
    catalog.contains(&effect)
}
