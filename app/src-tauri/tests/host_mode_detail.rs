//! AMD SET_OPERATING_MODE_DETAIL remap. No millivolt undervolt MQTT field.

use app_lib::{Backend, HostError};

async fn started(json: &str) -> Backend {
    let mut backend = Backend::fake_from_json(json).expect("ItemSupport");
    backend.start().await.expect("fake handshake");
    backend
}

fn last_detail(backend: &Backend) -> serde_json::Value {
    backend
        .recorded_publishes()
        .into_iter()
        .rev()
        .find(|(topic, value)| {
            topic == "Fan/Control" && value["Action"] == "SET_OPERATING_MODE_DETAIL"
        })
        .map(|(_, value)| value)
        .expect("SET_OPERATING_MODE_DETAIL")
}

#[tokio::test]
async fn amd_pl1_publishes_cpu_amd_spl_string_without_pl1_key() {
    let mut backend = started(r#"{"IsAMDPlatform":1}"#).await;
    backend
        .set_custom_detail("PL1", "45")
        .await
        .expect("AMD PL1");
    let payload = last_detail(&backend);
    assert!(
        payload["CpuAmdSPL"].is_string(),
        "CpuAmdSPL must be a JSON string: {payload:?}"
    );
    assert_eq!(payload["CpuAmdSPL"], "45");
    assert!(
        payload.get("PL1").is_none(),
        "AMD payload must not contain PL1: {payload:?}"
    );
}

#[tokio::test]
async fn empty_item_support_pl1_stays_intel_pl1_string() {
    let mut backend = started("{}").await;
    backend
        .set_custom_detail("PL1", "45")
        .await
        .expect("Intel PL1");
    let payload = last_detail(&backend);
    assert!(payload["PL1"].is_string(), "PL1 must be a JSON string: {payload:?}");
    assert_eq!(payload["PL1"], "45");
}

#[tokio::test]
async fn amd_cpu_tcc_offset_publishes_cpu_amd_tcc_target_string() {
    let mut backend = started(r#"{"IsAMDPlatform":1}"#).await;
    backend
        .set_custom_detail("CpuTccOffset", "10")
        .await
        .expect("AMD TCC");
    let payload = last_detail(&backend);
    assert!(
        payload["CpuAmdTccTarget"].is_string(),
        "CpuAmdTccTarget must be a JSON string: {payload:?}"
    );
    assert_eq!(payload["CpuAmdTccTarget"], "10");
}

#[tokio::test]
async fn millivolt_cpu_voltage_offset_is_unknown_mode() {
    let mut backend = started("{}").await;
    let err = backend
        .set_custom_detail("CpuVoltageOffset", "50")
        .await
        .expect_err("no millivolt undervolt MQTT field");
    assert!(
        matches!(err, HostError::UnknownMode(ref field) if field == "CpuVoltageOffset"),
        "CpuVoltageOffset must be UnknownMode, got {err}"
    );
}
