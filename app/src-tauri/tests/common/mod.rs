//! Shared Fake slot-4 protocol walk and canonical transcript format.
//! Never constructs `Backend::real` or opens TCP.
#![allow(dead_code)]

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use app_lib::Backend;
use capabilities::{DgpuGeneration, IGPU_ONLY_ON};
use serde_json::{Map, Value};

#[allow(dead_code)]
#[path = "../../src/overlay_state.rs"]
mod overlay_state;

use overlay_state::{overlay_update, OverlayStore};

static DIR_SEQ: AtomicU64 = AtomicU64::new(0);

const FULL_DEV: &str = include_str!("../../crates/_golden/item_support_full_dev.json");

pub fn transcript_path() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("target/slot4_transcript.tsv")
}

fn unique_dir(label: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "lmechrevo-transcript-{}-{}-{}",
        label,
        std::process::id(),
        DIR_SEQ.fetch_add(1, Ordering::Relaxed)
    ));
    std::fs::create_dir_all(&dir).expect("temp dir");
    dir
}

fn normalize_number(number: &serde_json::Number) -> Value {
    if let Some(value) = number.as_i64() {
        return Value::from(value);
    }
    if let Some(value) = number.as_u64() {
        return Value::from(value);
    }
    Value::Number(number.clone())
}

pub fn canonical_value(value: &Value) -> Value {
    match value {
        Value::Object(map) => {
            let mut out = Map::new();
            for (key, child) in map {
                out.insert(key.clone(), canonical_value(child));
            }
            Value::Object(out)
        }
        Value::Array(items) => Value::Array(items.iter().map(canonical_value).collect()),
        Value::Number(number) => normalize_number(number),
        other => other.clone(),
    }
}

pub fn canonical_json(value: &Value) -> String {
    serde_json::to_string(&canonical_value(value)).expect("canonical json")
}

pub fn format_transcript(publishes: &[(String, Value)]) -> String {
    let mut lines: Vec<String> = publishes
        .iter()
        .map(|(topic, payload)| format!("{topic}\t{}", canonical_json(payload)))
        .collect();
    lines.sort();
    let mut text = lines.join("\n");
    text.push('\n');
    text
}

pub fn write_transcript(path: &Path, text: &str) {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).expect("transcript parent");
    }
    std::fs::write(path, text).expect("write transcript");
}

pub async fn run_protocol_walk() -> Vec<(String, Value)> {
    let mut backend = Backend::fake_from_json(FULL_DEV)
        .expect("full-dev ItemSupport")
        .with_gpu(DgpuGeneration::Gen50, true)
        .with_dc_hz_seen(true)
        .with_profile_dir(unique_dir("profile"))
        .with_light_cfg_dir(unique_dir("light"))
        .with_write_allowed(true);
    backend.start().await.expect("fake handshake");

    backend
        .set_performance_mode("office")
        .await
        .expect("office mode");

    backend
        .set_performance_mode("custom")
        .await
        .expect("custom seed");
    backend
        .set_custom_detail("ProfileIndex", "2")
        .await
        .expect("custom slot 2");
    backend
        .set_performance_mode("custom")
        .await
        .expect("custom slot");

    backend
        .set_light_power("lightbar", false)
        .await
        .expect("lighting power");

    backend
        .set_quick_switch("darktheme", true)
        .await
        .expect("windows switch");
    backend
        .set_quick_switch("touchpad", true)
        .await
        .expect("touchpad switch");

    backend.set_charge_limit(80).expect("charge limit");

    let mut overlay = OverlayStore::memory();
    overlay_update(&mut overlay, &serde_json::json!({ "mode": "light" }))
        .expect("overlay toggle");

    backend
        .set_gpu_route(IGPU_ONLY_ON)
        .await
        .expect("n16 gpu");
    backend
        .set_auto_refresh_rate(true)
        .await
        .expect("auto-hz");

    backend.apply_inbound("Setting/Status", br#"{"DeepSleepSwitch":1}"#);
    backend
        .set_quick_switch("deepsleep", true)
        .await
        .expect("deep-sleep");

    backend.shutdown().await.expect("shutdown");
    backend.recorded_publishes()
}
