//! GPU route gate on Fake host. Unknown gen is empty. AUTO is never published.
//! N16 named-key lock: C# `GpuSwitchPayloadPerActionN16Tests` per-action SetToWMIEC matrix.

use app_lib::Backend;
use capabilities::{
    DgpuGeneration, HOT_SWAP_ON, IGPU_ONLY_AUTO, IGPU_ONLY_OFF, IGPU_ONLY_ON, TOGGLE_IGPU,
    TOGGLE_ON,
};

fn setting_control<'a>(
    publishes: &'a [(String, serde_json::Value)],
    action: &str,
) -> &'a serde_json::Value {
    publishes
        .iter()
        .find(|(topic, payload)| topic == "Setting/Control" && payload["Action"] == action)
        .map(|(_, payload)| payload)
        .unwrap_or_else(|| panic!("Setting/Control {action} missing: {publishes:?}"))
}

fn payload_object(payload: &serde_json::Value) -> &serde_json::Map<String, serde_json::Value> {
    payload
        .as_object()
        .unwrap_or_else(|| panic!("payload must be a JSON object: {payload}"))
}

#[tokio::test]
async fn snapshot_gpu_actions_empty_when_generation_unknown() {
    let backend = Backend::fake_from_json("{}").expect("empty ItemSupport");
    let snapshot = backend.snapshot();
    assert!(
        snapshot.gpu_actions.is_empty(),
        "Unknown gen must fail-closed: {snapshot:?}"
    );
    assert!(!snapshot.gpu_actions.iter().any(|a| a == IGPU_ONLY_AUTO));
}

#[tokio::test]
async fn snapshot_gpu_actions_offer_hot_swap_when_gen50_and_both_flags() {
    let json = r#"{"GpuHotSwapSwitchSupport":1,"lgpuHotSwapSwitchStatus":1}"#;
    let backend = Backend::fake_from_json(json)
        .expect("parse")
        .with_gpu(DgpuGeneration::Gen50, true);
    let actions = backend.snapshot().gpu_actions;
    assert!(
        actions.iter().any(|a| a == HOT_SWAP_ON),
        "Gen50 + both flags must offer hot-swap: {actions:?}"
    );
    assert!(
        !actions.iter().any(|a| a == IGPU_ONLY_AUTO),
        "AUTO must never be offered: {actions:?}"
    );
}

#[tokio::test]
async fn set_gpu_route_rejects_when_generation_unknown() {
    let mut backend = Backend::fake_from_json("{}").expect("empty");
    backend.start().await.expect("handshake");
    let err = backend
        .set_gpu_route(TOGGLE_ON)
        .await
        .expect_err("Unknown gen must deny TOGGLE_ON");
    assert!(
        err.to_string().contains("gpu action"),
        "denied error, got {err}"
    );
}

#[tokio::test]
async fn set_gpu_route_rejects_auto_when_gen50() {
    let json = r#"{"GpuHotSwapSwitchSupport":1,"lgpuHotSwapSwitchStatus":1}"#;
    let mut backend = Backend::fake_from_json(json)
        .expect("parse")
        .with_gpu(DgpuGeneration::Gen50, true);
    backend
        .set_gpu_route(IGPU_ONLY_AUTO)
        .await
        .expect_err("AUTO must be denied even on Gen50");
}

#[tokio::test]
async fn set_gpu_route_publishes_setting_control_when_gen30_toggle_on() {
    let mut backend = Backend::fake_from_json("{}")
        .expect("empty")
        .with_gpu(DgpuGeneration::Gen30, false);
    backend.start().await.expect("handshake");
    backend
        .set_gpu_route(TOGGLE_ON)
        .await
        .expect("Gen30 allows TOGGLE_ON");
    let publishes = backend.recorded_publishes();
    let payload = setting_control(&publishes, TOGGLE_ON);
    assert_eq!(payload["Action"], TOGGLE_ON);
    assert!(
        !payload_object(payload).contains_key("SetToWMIEC"),
        "Gen30 TOGGLE_ON must not carry SetToWMIEC (N16 CCUWinUI:85136): {payload}"
    );
}

#[tokio::test]
async fn set_gpu_route_igpu_only_on_carries_set_to_wmiec_ok_when_gen50() {
    let mut backend = Backend::fake_from_json("{}")
        .expect("empty")
        .with_gpu(DgpuGeneration::Gen50, true);
    backend.start().await.expect("handshake");
    backend
        .set_gpu_route(IGPU_ONLY_ON)
        .await
        .expect("Gen50 allows IGPU_ONLY_ON");
    let publishes = backend.recorded_publishes();
    let payload = setting_control(&publishes, IGPU_ONLY_ON);
    assert_eq!(payload["Action"], IGPU_ONLY_ON);
    assert_eq!(
        payload.get("SetToWMIEC").and_then(serde_json::Value::as_str),
        Some("OK"),
        "IGPU_ONLY_ON must carry SetToWMIEC=OK (N16 IgpuOnlyOnCarriesTheWmiecField): {payload}"
    );
}

#[tokio::test]
async fn set_gpu_route_igpu_only_off_carries_set_to_wmiec_ok_when_gen50() {
    let mut backend = Backend::fake_from_json("{}")
        .expect("empty")
        .with_gpu(DgpuGeneration::Gen50, true);
    backend.start().await.expect("handshake");
    backend
        .set_gpu_route(IGPU_ONLY_OFF)
        .await
        .expect("Gen50 allows IGPU_ONLY_OFF");
    let publishes = backend.recorded_publishes();
    let payload = setting_control(&publishes, IGPU_ONLY_OFF);
    assert_eq!(payload["Action"], IGPU_ONLY_OFF);
    assert_eq!(
        payload.get("SetToWMIEC").and_then(serde_json::Value::as_str),
        Some("OK"),
        "IGPU_ONLY_OFF must carry SetToWMIEC=OK (N16 IgpuOnlyOffCarriesTheWmiecField): {payload}"
    );
}

#[tokio::test]
async fn set_gpu_route_toggle_igpu_omits_set_to_wmiec_when_gen50() {
    let mut backend = Backend::fake_from_json("{}")
        .expect("empty")
        .with_gpu(DgpuGeneration::Gen50, true);
    backend.start().await.expect("handshake");
    backend
        .set_gpu_route(TOGGLE_IGPU)
        .await
        .expect("Gen50 allows TOGGLE_IGPU");
    let publishes = backend.recorded_publishes();
    let payload = setting_control(&publishes, TOGGLE_IGPU);
    assert_eq!(payload["Action"], TOGGLE_IGPU);
    assert!(
        !payload_object(payload).contains_key("SetToWMIEC"),
        "TOGGLE_IGPU must not carry SetToWMIEC (N16 TheDirectConnectIgpuToggleDoesNotCarryTheWmiecField): {payload}"
    );
}
