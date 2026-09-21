//! GPU route gate on Fake host. Unknown gen is empty.
//! N16 named-key lock: C# `GpuSwitchPayloadPerActionN16Tests` per-action SetToWMIEC matrix.
//! C# Gen50 ConsoleActions offer AUTO; restart-route prelude/leave extras + 800ms RESTART.

use app_lib::Backend;
use capabilities::{
    DgpuGeneration, HOT_SWAP_OFF, HOT_SWAP_ON, IGPU_ONLY_AUTO, IGPU_ONLY_OFF, IGPU_ONLY_ON,
    RESTART, TOGGLE_IGPU, TOGGLE_OFF, TOGGLE_ON,
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

fn setting_control_since(
    publishes: &[(String, serde_json::Value)],
    from: usize,
) -> Vec<serde_json::Value> {
    publishes[from..]
        .iter()
        .filter(|(topic, _)| topic == "Setting/Control")
        .map(|(_, payload)| payload.clone())
        .collect()
}

fn action_only(action: &str) -> serde_json::Value {
    serde_json::json!({ "Action": action })
}

fn igpu_off_with_wmiec() -> serde_json::Value {
    serde_json::json!({ "Action": IGPU_ONLY_OFF, "SetToWMIEC": "OK" })
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
    assert_eq!(
        backend.snapshot().gpu_actions,
        vec![
            TOGGLE_ON.to_owned(),
            TOGGLE_OFF.to_owned(),
            TOGGLE_IGPU.to_owned(),
            IGPU_ONLY_ON.to_owned(),
            IGPU_ONLY_OFF.to_owned(),
            IGPU_ONLY_AUTO.to_owned(),
            RESTART.to_owned(),
            HOT_SWAP_ON.to_owned(),
            HOT_SWAP_OFF.to_owned(),
        ]
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
async fn snapshot_gpu_actions_hide_auto_when_gen40_three_mode() {
    // Given: C# DisplayRouteMatrix.cs:148 Gen40 three-mode ConsoleActions omit AUTO
    let backend = Backend::fake_from_json("{}")
        .expect("empty")
        .with_gpu(DgpuGeneration::Gen40, true);

    // When: snapshot offered actions
    let actions = backend.snapshot().gpu_actions;

    // Then: AUTO is hidden (C# would hide it)
    assert_eq!(
        actions,
        vec![
            TOGGLE_ON.to_owned(),
            TOGGLE_OFF.to_owned(),
            TOGGLE_IGPU.to_owned(),
            IGPU_ONLY_ON.to_owned(),
            IGPU_ONLY_OFF.to_owned(),
            RESTART.to_owned(),
        ]
    );
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
        payload
            .get("SetToWMIEC")
            .and_then(serde_json::Value::as_str),
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
        payload
            .get("SetToWMIEC")
            .and_then(serde_json::Value::as_str),
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

#[tokio::test]
async fn set_gpu_route_dgpu_direct_entry_emits_vendor_prelude_then_restart_when_gen50() {
    // Given: Gen50 dGPU-direct entry
    // C# GpuSwitchPayloadPerActionN16Tests.TheDirectConnectRestartRouteMatchesTheVendorSequence
    // + GpuRouteCommandLayer.BuildRestartCommands (GpuSwitchCommandTests.EnteringDirectKeepsTheOfficialThreeActionRouteThenRestart)
    let mut backend = Backend::fake_from_json("{}")
        .expect("empty")
        .with_gpu(DgpuGeneration::Gen50, true);
    backend.start().await.expect("handshake");
    let from = backend.recorded_publishes().len();

    // When: apply TOGGLE_ON
    backend
        .set_gpu_route(TOGGLE_ON)
        .await
        .expect("Gen50 allows TOGGLE_ON");

    // Then: TOGGLE_ON, IGPU_ONLY_OFF+SetToWMIEC, TOGGLE_ON, RESTART (CCUWinUI:85136-85168, 86511-86515)
    assert_eq!(
        setting_control_since(&backend.recorded_publishes(), from),
        vec![
            action_only(TOGGLE_ON),
            igpu_off_with_wmiec(),
            action_only(TOGGLE_ON),
            action_only(RESTART),
        ]
    );
}

#[tokio::test]
async fn set_gpu_route_leaving_direct_to_auto_emits_toggle_off_then_auto_then_restart_when_gen50() {
    // Given: Gen50 leave-direct to AUTO
    // C# MechrevoService.CreateGpuRestartTargetPayloads GpuAuto + supportsDgpuDirect (lines 1148-1152)
    // + GpuRouteCommandLayer.cs:44-48 RESTART after 800ms
    let mut backend = Backend::fake_from_json("{}")
        .expect("empty")
        .with_gpu(DgpuGeneration::Gen50, true);
    backend.start().await.expect("handshake");
    let from = backend.recorded_publishes().len();

    // When: apply AUTO (leave-direct extras)
    backend
        .set_gpu_route(IGPU_ONLY_AUTO)
        .await
        .expect("Gen50 allows IGPU_ONLY_AUTO");

    // Then: TOGGLE_OFF, AUTO, RESTART
    assert_eq!(
        setting_control_since(&backend.recorded_publishes(), from),
        vec![
            action_only(TOGGLE_OFF),
            action_only(IGPU_ONLY_AUTO),
            action_only(RESTART),
        ]
    );
}

#[tokio::test]
async fn set_gpu_route_leaving_direct_to_hybrid_emits_toggle_off_then_restart_when_gen50() {
    // Given: Gen50 leave-direct to hybrid
    // C# CreateGpuRestartTargetPayloads GpuStandard + supportsDgpuDirect (lines 1142-1143)
    let mut backend = Backend::fake_from_json("{}")
        .expect("empty")
        .with_gpu(DgpuGeneration::Gen50, true);
    backend.start().await.expect("handshake");
    let from = backend.recorded_publishes().len();

    // When: apply TOGGLE_OFF
    backend
        .set_gpu_route(TOGGLE_OFF)
        .await
        .expect("Gen50 allows TOGGLE_OFF");

    // Then: TOGGLE_OFF, RESTART
    assert_eq!(
        setting_control_since(&backend.recorded_publishes(), from),
        vec![action_only(TOGGLE_OFF), action_only(RESTART)]
    );
}
