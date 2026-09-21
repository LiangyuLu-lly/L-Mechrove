//! Diff a Fake protocol-walk transcript against C# MQTT goldens.
//! Keys and value types are strict. Machine-state values are presence-only.

mod common;

use common::{canonical_json, run_protocol_walk};
use serde_json::Value;

const MACHINE_STATE_KEYS: &[&str] = &[
    "BatteryLifePercent",
    "CpuFanRpm",
    "CpuTemperature",
    "GpuFanRpm",
    "GpuTemperature",
    "Temperature",
    "Rpm",
];

async fn walked() -> Vec<(String, Value)> {
    run_protocol_walk().await
}

fn json_kind(value: &Value) -> &'static str {
    match value {
        Value::Null => "null",
        Value::Bool(_) => "bool",
        Value::Number(_) => "number",
        Value::String(_) => "string",
        Value::Array(_) => "array",
        Value::Object(_) => "object",
    }
}

fn is_machine_state_key(key: &str) -> bool {
    MACHINE_STATE_KEYS.iter().any(|name| *name == key)
}

fn payload_matches_golden(actual: &Value, golden: &Value) -> Result<(), String> {
    match (actual, golden) {
        (Value::Object(actual_map), Value::Object(golden_map)) => {
            for (key, expected) in golden_map {
                let got = actual_map.get(key).ok_or_else(|| {
                    format!("missing key {key}: actual={}", canonical_json(actual))
                })?;
                if json_kind(got) != json_kind(expected) {
                    return Err(format!(
                        "key {key} type {} != {}: actual={}",
                        json_kind(got),
                        json_kind(expected),
                        canonical_json(actual)
                    ));
                }
                if is_machine_state_key(key) {
                    continue;
                }
                payload_matches_golden(got, expected)?;
            }
            Ok(())
        }
        (Value::Array(actual_items), Value::Array(golden_items)) => {
            if actual_items.len() != golden_items.len() {
                return Err(format!(
                    "array length {} != {}",
                    actual_items.len(),
                    golden_items.len()
                ));
            }
            for (got, expected) in actual_items.iter().zip(golden_items) {
                payload_matches_golden(got, expected)?;
            }
            Ok(())
        }
        (got, expected) if json_kind(got) == json_kind(expected) && got == expected => Ok(()),
        (got, expected) if json_kind(got) == json_kind(expected) => Err(format!(
            "value mismatch: {} != {}",
            canonical_json(got),
            canonical_json(expected)
        )),
        (got, expected) => Err(format!(
            "type {} != {}",
            json_kind(got),
            json_kind(expected)
        )),
    }
}

fn transcript_contains_golden(
    publishes: &[(String, Value)],
    golden: &Value,
) -> Result<(), String> {
    let mut errors = Vec::new();
    for (topic, payload) in publishes {
        match payload_matches_golden(payload, golden) {
            Ok(()) => return Ok(()),
            Err(err) => errors.push(format!("{topic}: {err}")),
        }
    }
    Err(format!(
        "golden {} not contained: {}",
        canonical_json(golden),
        errors.join(" | ")
    ))
}

fn load_golden(name: &str) -> Value {
    let path = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("crates/_golden/mqtt")
        .join(name);
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|err| panic!("{}: {err}", path.display()));
    serde_json::from_str(&text).unwrap_or_else(|err| panic!("{}: {err}", path.display()))
}

fn has_action(pubs: &[(String, Value)], topic: &str, action: &str) -> bool {
    pubs.iter().any(|(got_topic, payload)| {
        got_topic == topic && payload.get("Action").and_then(Value::as_str) == Some(action)
    })
}

fn payload_with_action<'a>(
    pubs: &'a [(String, Value)],
    topic: &str,
    action: &str,
) -> &'a Value {
    pubs.iter()
        .find(|(got_topic, payload)| {
            got_topic == topic && payload.get("Action").and_then(Value::as_str) == Some(action)
        })
        .map(|(_, payload)| payload)
        .unwrap_or_else(|| panic!("{topic} Action={action} missing: {pubs:?}"))
}

#[tokio::test]
async fn transcript_contains_office_goldens_when_walked() {
    let pubs = walked().await;
    transcript_contains_golden(&pubs, &load_golden("operating_office.json"))
        .expect("office Fan golden");
    transcript_contains_golden(&pubs, &load_golden("lchwoc_office.json"))
        .expect("office LCHWOC golden");
}

#[tokio::test]
async fn transcript_contains_custom_slot_goldens_when_walked() {
    let pubs = walked().await;
    transcript_contains_golden(&pubs, &load_golden("operating_custom.json"))
        .expect("custom Fan golden");
    transcript_contains_golden(&pubs, &load_golden("lchwoc_custom.json"))
        .expect("custom LCHWOC golden");
}

#[tokio::test]
async fn transcript_contains_handshake_entries_when_walked() {
    let pubs = walked().await;
    let handshake = load_golden("handshake_order.json");
    let steps = handshake.as_array().expect("handshake_order array");
    for step in steps {
        let topic = step["topic"].as_str().expect("topic");
        let payload = &step["payload"];
        assert!(
            pubs.iter()
                .any(|(got, body)| got == topic && transcript_contains_golden(&[(got.clone(), body.clone())], payload).is_ok()),
            "handshake {topic} {} missing: {pubs:?}",
            payload
        );
    }
}

#[tokio::test]
async fn transcript_contains_set_power_shape_when_walked() {
    let pubs = walked().await;
    let payload = pubs
        .iter()
        .find(|(topic, body)| {
            topic.ends_with("/Ctrl")
                && body.get("function").and_then(Value::as_str) == Some("SetPower")
        })
        .map(|(_, body)| body)
        .expect("SetPower publish");
    assert_eq!(json_kind(&payload["function"]), "string");
    assert_eq!(json_kind(&payload["powerstatus"]), "number");
}

#[tokio::test]
async fn transcript_contains_n16_wmiec_when_walked() {
    let pubs = walked().await;
    let payload = payload_with_action(&pubs, "Setting/Control", "IGPU_ONLY_CONNECT_RB_ON");
    assert_eq!(json_kind(&payload["Action"]), "string");
    assert_eq!(json_kind(&payload["SetToWMIEC"]), "string");
    assert_eq!(payload["SetToWMIEC"], "OK");
}

#[tokio::test]
async fn transcript_contains_auto_hz_bool_when_walked() {
    let pubs = walked().await;
    transcript_contains_golden(&pubs, &load_golden("gpu_dc_hz_on.json")).expect("GPU_DC_HZ");
    let payload = payload_with_action(&pubs, "Setting/Control", "GPU_DC_HZ");
    assert_eq!(json_kind(&payload["Enable"]), "bool");
}

#[tokio::test]
async fn transcript_contains_deepsleep_secs_string_when_walked() {
    let pubs = walked().await;
    let payload = payload_with_action(&pubs, "Setting/Control", "DEEPSLEEP_ON");
    assert_eq!(json_kind(&payload["Secs"]), "string");
}

#[tokio::test]
async fn transcript_contains_shutdown_system_off_when_walked() {
    let pubs = walked().await;
    assert!(
        has_action(&pubs, "System/Control", "System_OFF"),
        "System_OFF missing: {pubs:?}"
    );
}

#[tokio::test]
async fn transcript_contains_touchpad_switch_when_walked() {
    let pubs = walked().await;
    assert!(
        has_action(&pubs, "Setting/Control", "TOUCHPAD_ON"),
        "TOUCHPAD_ON missing: {pubs:?}"
    );
}

#[test]
fn machine_state_fields_are_presence_only_when_compared() {
    let actual = serde_json::json!({
        "Action": "OPERATING_OFFICE_MODE",
        "ProfileIndex": 0,
        "CpuTemperature": 41.2
    });
    let golden = serde_json::json!({
        "Action": "OPERATING_OFFICE_MODE",
        "ProfileIndex": 0,
        "CpuTemperature": 999
    });
    payload_matches_golden(&actual, &golden)
        .expect("CpuTemperature value must not be compared");
}
