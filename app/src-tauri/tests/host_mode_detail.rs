//! AMD SET_OPERATING_MODE_DETAIL remap. No millivolt undervolt MQTT field.

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use app_lib::{Backend, HostError};
use serde_json::Value;

static PROFILE_SEQ: AtomicU64 = AtomicU64::new(0);

async fn started(json: &str) -> Backend {
    let mut backend = Backend::fake_from_json(json).expect("ItemSupport");
    backend.start().await.expect("fake handshake");
    backend
}

async fn started_custom(json: &str) -> Backend {
    let mut backend = started(json).await;
    backend
        .set_performance_mode("custom")
        .await
        .expect("current_mode custom");
    backend
}

fn unique_profile_dir() -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "lmechrevo-mode-detail-{}-{}",
        std::process::id(),
        PROFILE_SEQ.fetch_add(1, Ordering::Relaxed)
    ));
    fs::create_dir_all(&dir).expect("temp mode profile dir");
    dir
}

async fn started_with_profiles() -> (Backend, PathBuf) {
    let dir = unique_profile_dir();
    let mut backend = Backend::fake_from_json("{}")
        .expect("ItemSupport")
        .with_profile_dir(dir.clone());
    backend.start().await.expect("fake handshake");
    (backend, dir)
}

fn read_profiles(dir: &Path) -> Value {
    let text = fs::read_to_string(dir.join("mode-profiles.json"))
        .unwrap_or_else(|err| panic!("mode-profiles.json: {err}"));
    serde_json::from_str(&text).expect("mode-profiles.json JSON")
}

fn mode_detail_after(backend: &Backend, start: usize) -> Vec<Value> {
    backend
        .recorded_publishes()
        .into_iter()
        .skip(start)
        .filter(|(topic, value)| {
            topic == "Fan/Control" && value["Action"] == "SET_OPERATING_MODE_DETAIL"
        })
        .map(|(_, value)| value)
        .collect()
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
    let mut backend = started_custom(r#"{"IsAMDPlatform":1}"#).await;
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
    let mut backend = started_custom("{}").await;
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
    let mut backend = started_custom(r#"{"IsAMDPlatform":1}"#).await;
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
    let mut backend = started_custom("{}").await;
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

#[tokio::test]
async fn persists_pl1_under_office_key_when_current_mode_is_office() {
    // Given: current mode is Office with a profile dir
    let (mut backend, dir) = started_with_profiles().await;
    backend
        .set_performance_mode("office")
        .await
        .expect("current_mode office");

    // When: a detail field is changed while still on Office
    backend
        .set_custom_detail("PL1", "45")
        .await
        .expect("save PL1");

    // Then: the value is filed under the Office key, not Custom
    let store = read_profiles(&dir);
    assert_eq!(store["office"]["pl1"], "45");
    assert!(
        store
            .get("custom")
            .and_then(|slot| slot.get("pl1"))
            .is_none(),
        "Office change must not be filed under custom: {store}"
    );
}

#[tokio::test]
async fn does_not_publish_operating_mode_detail_when_current_mode_is_office() {
    // Given: current mode is Office
    let (mut backend, _dir) = started_with_profiles().await;
    backend
        .set_performance_mode("office")
        .await
        .expect("current_mode office");
    let start = backend.recorded_publishes().len();

    // When: a detail field is changed while still on Office
    backend
        .set_custom_detail("PL1", "45")
        .await
        .expect("save PL1");

    // Then: vendor console only publishes SET_OPERATING_MODE_DETAIL in Custom
    let published = mode_detail_after(&backend, start);
    assert!(
        published.is_empty(),
        "Office must not publish SET_OPERATING_MODE_DETAIL by default: {published:?}"
    );
}

#[tokio::test]
async fn persists_pl1_under_custom_key_when_current_mode_is_custom() {
    // Given: current mode is Custom with a profile dir
    let (mut backend, dir) = started_with_profiles().await;
    backend
        .set_performance_mode("custom")
        .await
        .expect("current_mode custom");

    // When: a detail field is changed while still on Custom
    backend
        .set_custom_detail("PL1", "45")
        .await
        .expect("save PL1");

    // Then: the value is filed under the Custom key
    let store = read_profiles(&dir);
    assert_eq!(store["custom"]["pl1"], "45");
    assert!(
        store
            .get("office")
            .and_then(|slot| slot.get("pl1"))
            .is_none(),
        "Custom change must not be filed under office: {store}"
    );
}

#[tokio::test]
async fn publishes_operating_mode_detail_when_current_mode_is_custom() {
    // Given: current mode is Custom
    let (mut backend, _dir) = started_with_profiles().await;
    backend
        .set_performance_mode("custom")
        .await
        .expect("current_mode custom");
    let start = backend.recorded_publishes().len();

    // When: a detail field is changed while still on Custom
    backend
        .set_custom_detail("PL1", "45")
        .await
        .expect("save PL1");

    // Then: SET_OPERATING_MODE_DETAIL payload shape stays the vendor-matched string
    let published = mode_detail_after(&backend, start);
    assert_eq!(
        published.len(),
        1,
        "Custom must publish once: {published:?}"
    );
    assert!(
        published[0]["PL1"].is_string(),
        "PL1 must be a JSON string: {:?}",
        published[0]
    );
    assert_eq!(published[0]["PL1"], "45");
    assert_eq!(published[0]["Action"], "SET_OPERATING_MODE_DETAIL");
}
