use std::fs;
use std::path::{Path, PathBuf};

use gcu_mqtt::payloads::{office_fan, office_lchwoc};
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

#[test]
fn office_fan_encodes_profile_index_number_0_when_office_mode() {
    let golden = read_golden("operating_office.json");
    let encoded = serde_json::to_value(office_fan()).unwrap();
    assert_eq!(encoded, golden);
    assert_eq!(encoded["Action"], "OPERATING_OFFICE_MODE");
    assert!(encoded["ProfileIndex"].is_number());
    assert!(!encoded["ProfileIndex"].is_string());
    assert_eq!(encoded["ProfileIndex"], 0);
}

#[test]
fn office_lchwoc_encodes_is_normal_run_number_0_when_office_mode() {
    let golden = read_golden("lchwoc_office.json");
    let encoded = serde_json::to_value(office_lchwoc()).unwrap();
    assert_eq!(encoded, golden);
    assert!(encoded["IsNormalRun"].is_number());
    assert!(!encoded["IsNormalRun"].is_string());
    assert_eq!(encoded["IsNormalRun"], 0);
}
