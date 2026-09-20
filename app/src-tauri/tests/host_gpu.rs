//! GPU route gate on Fake host. Unknown gen is empty. AUTO is never published.

use app_lib::Backend;
use capabilities::{DgpuGeneration, HOT_SWAP_ON, IGPU_ONLY_AUTO, TOGGLE_ON};

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
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "Setting/Control" && payload["Action"] == TOGGLE_ON
        }),
        "Setting/Control TOGGLE_ON missing: {publishes:?}"
    );
}
