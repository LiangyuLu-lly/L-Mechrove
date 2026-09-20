use std::fs;
use std::path::{Path, PathBuf};

use gcu_mqtt::payloads::{
    custom_fan, custom_lchwoc, fan_boost_off, fan_boost_on, gaming_fan, gaming_lchwoc, office_fan,
    office_lchwoc, silent_turbo_fan, turbo_fan, turbo_lchwoc, PayloadError, ProfileIndex,
};
use serde_json::Value;

fn golden_dir() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../_golden/mqtt")
}

fn read_golden(name: &str) -> Value {
    let path = golden_dir().join(name);
    let text =
        fs::read_to_string(&path).unwrap_or_else(|err| panic!("read {}: {err}", path.display()));
    serde_json::from_str(&text).unwrap()
}

fn assert_json_number(value: &Value, key: &str, expected: i64) {
    assert!(value[key].is_number(), "{key} must be a JSON number");
    assert!(!value[key].is_string(), "{key} must not be a JSON string");
    assert_eq!(value[key], expected);
}

#[test]
fn fan_operating_matches_golden_when_official_mode() {
    let cases: &[(&str, Value, &str, i64)] = &[
        (
            "operating_office.json",
            serde_json::to_value(office_fan()).unwrap(),
            "OPERATING_OFFICE_MODE",
            0,
        ),
        (
            "operating_gaming.json",
            serde_json::to_value(gaming_fan()).unwrap(),
            "OPERATING_GAMING_MODE",
            0,
        ),
        (
            "operating_turbo.json",
            serde_json::to_value(turbo_fan()).unwrap(),
            "OPERATING_TURBO_MODE",
            0,
        ),
        (
            "operating_custom.json",
            serde_json::to_value(custom_fan(ProfileIndex::new(2).unwrap())).unwrap(),
            "OPERATING_CUSTOM_MODE",
            2,
        ),
    ];
    for &(golden, ref encoded, action, index) in cases {
        assert_eq!(encoded, &read_golden(golden), "{golden}");
        assert_eq!(encoded["Action"], action);
        assert_json_number(encoded, "ProfileIndex", index);
    }
}

#[test]
fn lchwoc_matches_golden_when_official_mode() {
    let cases: &[(&str, Value, Option<i64>, Option<bool>)] = &[
        (
            "lchwoc_office.json",
            serde_json::to_value(office_lchwoc()).unwrap(),
            Some(0),
            None,
        ),
        (
            "lchwoc_gaming.json",
            serde_json::to_value(gaming_lchwoc()).unwrap(),
            Some(1),
            None,
        ),
        (
            "lchwoc_turbo.json",
            serde_json::to_value(turbo_lchwoc()).unwrap(),
            Some(2),
            None,
        ),
        (
            "lchwoc_custom.json",
            serde_json::to_value(custom_lchwoc()).unwrap(),
            None,
            Some(true),
        ),
    ];
    for &(golden, ref encoded, normal_run, custom_run) in cases {
        assert_eq!(encoded, &read_golden(golden), "{golden}");
        match (normal_run, custom_run) {
            (Some(value), None) => assert_json_number(encoded, "IsNormalRun", value),
            (None, Some(flag)) => {
                assert!(encoded["IsCustomRun"].is_boolean());
                assert!(!encoded["IsCustomRun"].is_string());
                assert_eq!(encoded["IsCustomRun"], flag);
                assert!(encoded.get("IsNormalRun").is_none());
            }
            _ => panic!("{golden}: expected exactly one of IsNormalRun / IsCustomRun"),
        }
    }
}

#[test]
fn custom_fan_encodes_profile_index_number_when_index_in_0_to_3() {
    for index in 0_u8..=3 {
        let encoded = serde_json::to_value(custom_fan(ProfileIndex::new(index).unwrap())).unwrap();
        assert_eq!(encoded["Action"], "OPERATING_CUSTOM_MODE");
        assert_json_number(&encoded, "ProfileIndex", i64::from(index));
    }
}

#[test]
fn profile_index_rejects_when_out_of_range() {
    match ProfileIndex::new(4) {
        Err(PayloadError::ProfileIndexOutOfRange { index: 4 }) => {}
        other => panic!("expected ProfileIndexOutOfRange {{ index: 4 }}, got {other:?}"),
    }
}

#[test]
fn silent_turbo_fan_encodes_silent_number_1_when_encoded() {
    let encoded = serde_json::to_value(silent_turbo_fan()).unwrap();
    assert_eq!(encoded, read_golden("silent_turbo.json"));
    assert_eq!(encoded["Action"], "SET_CPU_CORE_OFFSET_SILENT");
    assert_json_number(&encoded, "SILENT", 1);
}

#[test]
fn fan_boost_matches_golden_when_on_or_off() {
    let cases = [
        (
            "fan_boost_on.json",
            serde_json::to_value(fan_boost_on()).unwrap(),
            "FAN_BOOST_ON",
        ),
        (
            "fan_boost_off.json",
            serde_json::to_value(fan_boost_off()).unwrap(),
            "FAN_BOOST_OFF",
        ),
    ];
    for (golden, encoded, action) in cases {
        assert_eq!(encoded, read_golden(golden), "{golden}");
        assert_eq!(encoded["Action"], action);
    }
}
