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
    assert!(
        payload["PL1"].is_string(),
        "PL1 must be a JSON string: {payload:?}"
    );
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
async fn empty_item_support_pl2_publishes_pl2_json_string() {
    let mut backend = started("{}").await;
    backend
        .set_custom_detail("PL2", "45")
        .await
        .expect("Intel PL2");
    let payload = last_detail(&backend);
    assert!(
        payload["PL2"].is_string(),
        "PL2 must be a JSON string: {payload:?}"
    );
    assert_eq!(payload["PL2"], "45");
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

#[tokio::test]
async fn profile_name_publishes_osd_string_when_fake_broker() {
    // Given: Fake broker after handshake
    let mut backend = started("{}").await;

    // When: the custom profile OSD name is set (C# SetCustomProfileName)
    backend
        .set_custom_detail("ProfileName", "自定义 1")
        .await
        .expect("OSD profile name");

    // Then: Fan/Control SET_CUSTOM_PROFILE_OSD_STRING ProfileName is that string
    let payload = backend
        .recorded_publishes()
        .into_iter()
        .rev()
        .find(|(topic, value)| {
            topic == "Fan/Control" && value["Action"] == "SET_CUSTOM_PROFILE_OSD_STRING"
        })
        .map(|(_, value)| value)
        .expect("SET_CUSTOM_PROFILE_OSD_STRING");
    assert!(
        payload["ProfileName"].is_string(),
        "ProfileName must be a JSON string: {payload:?}"
    );
    assert_eq!(payload["ProfileName"], "自定义 1");
}

#[tokio::test]
async fn restore_publishes_both_restore_actions_with_table_name_when_fake_broker() {
    // Given: Fake broker after handshake; C# Name = Program.hw.TableName
    let mut backend = started("{}").await;
    const TABLE_NAME: &str = "M4T1";

    // When: restore-current-profile-defaults is requested
    backend
        .set_custom_detail("RESTORE_OPERATING_MODE_DETAIL", TABLE_NAME)
        .await
        .expect("restore current profile defaults");

    // Then: both C# restore Fan/Control actions, curve Name = TableName
    let publishes = backend.recorded_publishes();
    let fan: Vec<&serde_json::Value> = publishes
        .iter()
        .filter(|(topic, _)| topic == "Fan/Control")
        .map(|(_, payload)| payload)
        .collect();
    let detail_idx = fan
        .iter()
        .position(|payload| payload["Action"] == "RESTORE_OPERATING_MODE_DETAIL")
        .unwrap_or_else(|| panic!("RESTORE_OPERATING_MODE_DETAIL missing: {publishes:?}"));
    let curve_idx = fan
        .iter()
        .position(|payload| payload["Action"] == "RESTORE_FAN_SPEED_CURVE_SETTING")
        .unwrap_or_else(|| panic!("RESTORE_FAN_SPEED_CURVE_SETTING missing: {publishes:?}"));
    assert!(
        detail_idx < curve_idx,
        "C# publishes RESTORE_OPERATING_MODE_DETAIL before RESTORE_FAN_SPEED_CURVE_SETTING: {publishes:?}"
    );
    let curve = fan[curve_idx];
    assert!(
        curve["Name"].is_string(),
        "Name must be a JSON string: {curve:?}"
    );
    assert_eq!(curve["Name"], TABLE_NAME);
}
