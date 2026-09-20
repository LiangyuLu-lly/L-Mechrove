//! Golden-backed lighting gate: ItemSupport fail-closed, never MQTT Seen.

use std::fs;
use std::path::{Path, PathBuf};

use capabilities::{
    FeatureBit, FeatureMatrix, FeatureMissingPolicy, ItemSupport, LightingVisibility,
};
use serde::Deserialize;

fn golden_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("..").join("_golden")
}

fn read_golden(name: &str) -> String {
    let path = golden_dir().join(name);
    fs::read_to_string(&path).unwrap_or_else(|err| {
        panic!("read golden {}: {err}", path.display());
    })
}

fn load_item_support(name: &str) -> ItemSupport {
    ItemSupport::parse_json(&read_golden(name)).unwrap_or_else(|err| {
        panic!("parse ItemSupport golden {name}: {err}");
    })
}

fn visibility_from_golden(name: &str) -> LightingVisibility {
    LightingVisibility::from_item_support(&load_item_support(name))
}

#[derive(Debug, Deserialize)]
struct FailClosedGolden {
    missing_policy: FeatureMissingPolicy,
    bits: Vec<FailClosedBit>,
}

#[derive(Debug, Deserialize)]
struct FailClosedBit {
    bit: FeatureBit,
    key: String,
}

fn load_fail_closed_golden() -> FailClosedGolden {
    serde_json::from_str(&read_golden("feature_matrix_fail_closed.json")).unwrap_or_else(|err| {
        panic!("parse feature_matrix_fail_closed.json: {err}");
    })
}

#[test]
fn lighting_visibility_reads_keyboard_type_from_itemsupport() {
    let map = ItemSupport::parse_json(r#"{"KeyboardType":2}"#).expect("KeyboardType json");
    let visibility = LightingVisibility::from_item_support(&map);
    assert_eq!(visibility.keyboard_type, 2);
}

#[test]
fn lighting_visibility_keyboard_type_is_zero_when_missing() {
    let visibility = LightingVisibility::from_item_support(&ItemSupport::default());
    assert_eq!(visibility.keyboard_type, 0);
}

#[test]
fn lighting_visibility_hides_logo_when_itemsupport_false() {
    let visibility = visibility_from_golden("item_support_g16_no_lightbar.json");
    assert!(!visibility.logo, "G16 LogoLightSupport=0 must hide Logo");
    assert!(
        !visibility.lightbar,
        "G16 Lightbar/RGB all 0 must hide lightbar even if MQTT Seen would be true"
    );
}

#[test]
fn lighting_visibility_shows_logo_when_logo_lightbar_alias_true() {
    let visibility = visibility_from_golden("item_support_logo_true.json");
    assert!(visibility.logo, "LogoLightbarSupport=1 is a LogoLight alias");
}

#[test]
fn feature_matrix_missing_lightbar_support_is_unsupported() {
    let matrix = FeatureMatrix::from_values(&ItemSupport::default());
    assert!(
        !matrix.is_present(FeatureBit::Lightbar),
        "empty profile does not contain LightbarSupport"
    );
    assert!(
        !matrix.is_supported(FeatureBit::Lightbar),
        "missing LightbarSupport is FailClosed"
    );
}

#[test]
fn vendor_constant_on_bits_default_true_when_missing() {
    let matrix = FeatureMatrix::from_values(&ItemSupport::default());
    assert!(matrix.is_supported(FeatureBit::Keyboard));
    assert!(matrix.is_supported(FeatureBit::SystemMonitor));
    assert!(matrix.is_supported(FeatureBit::AcRecoverySwitch));
}

#[test]
fn feature_matrix_fail_closed_golden_bits_are_unsupported_when_omitted() {
    let golden = load_fail_closed_golden();
    assert_eq!(golden.missing_policy, FeatureMissingPolicy::FailClosed);
    let matrix = FeatureMatrix::from_values(&ItemSupport::default());
    for entry in golden.bits {
        let definition = FeatureMatrix::definition(entry.bit);
        assert_eq!(definition.key, entry.key.as_str());
        assert_eq!(definition.missing, FeatureMissingPolicy::FailClosed);
        assert!(
            !matrix.is_supported(entry.bit),
            "{:?} must be unsupported when omitted",
            entry.bit
        );
    }
}

#[test]
fn goldens_live_beside_crate_not_inside_it() {
    assert!(Path::new(&golden_dir()).join("item_support_g16_no_lightbar.json").is_file());
    assert!(Path::new(&golden_dir()).join("item_support_logo_true.json").is_file());
    assert!(Path::new(&golden_dir()).join("feature_matrix_fail_closed.json").is_file());
}
