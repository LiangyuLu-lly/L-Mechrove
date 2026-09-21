//! Per-mode PL1 + T0–T15 persist. Missing profile leaves official Fan+LCHWOC.

use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

use app_lib::Backend;
use serde_json::Value;

static PROFILE_SEQ: AtomicU64 = AtomicU64::new(0);

const DUTIES: [u8; 16] = [
    0, 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 100, 100, 100, 100,
];

fn unique_profile_dir() -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "lmechrevo-mode-{}-{}",
        std::process::id(),
        PROFILE_SEQ.fetch_add(1, Ordering::Relaxed)
    ));
    fs::create_dir_all(&dir).expect("temp mode profile dir");
    dir
}

async fn started_with_profiles() -> Backend {
    let mut backend = Backend::fake_from_json("{}")
        .expect("empty ItemSupport")
        .with_profile_dir(unique_profile_dir());
    backend.start().await.expect("fake handshake");
    backend
}

fn last_action_idx(publishes: &[(String, Value)], action: &str) -> usize {
    publishes
        .iter()
        .rposition(|(topic, payload)| topic == "Fan/Control" && payload["Action"] == action)
        .unwrap_or_else(|| panic!("{action}"))
}

fn last_office_idx(publishes: &[(String, Value)]) -> usize {
    last_action_idx(publishes, "OPERATING_OFFICE_MODE")
}

#[tokio::test]
async fn set_performance_mode_office_without_profile_skips_stored_pl1_and_curve() {
    let mut backend = started_with_profiles().await;
    backend
        .set_performance_mode("office")
        .await
        .expect("office mode");
    let publishes = backend.recorded_publishes();
    assert!(
        !publishes.iter().any(|(topic, payload)| {
            topic == "Fan/Control"
                && (payload["Action"] == "SET_OPERATING_MODE_DETAIL"
                    || payload["Action"] == "SET_FAN_SPEED_CURVE_SETTING")
        }),
        "no profile → official office Fan+LCHWOC only: {publishes:?}"
    );
}

#[tokio::test]
async fn saved_office_pl1_republishes_string_after_mode_packet() {
    let mut backend = started_with_profiles().await;
    backend
        .set_performance_mode("office")
        .await
        .expect("current_mode office");
    backend
        .set_custom_detail("PL1", "45")
        .await
        .expect("save PL1");
    backend
        .set_performance_mode("office")
        .await
        .expect("re-enter office");
    let publishes = backend.recorded_publishes();
    let after = &publishes[last_office_idx(&publishes) + 1..];
    assert!(
        after.iter().any(|(topic, payload)| {
            topic == "Fan/Control"
                && payload["Action"] == "SET_OPERATING_MODE_DETAIL"
                && payload["PL1"].is_string()
                && payload["PL1"] == "45"
        }),
        "stored PL1 string 45 after mode packet: {after:?}"
    );
}

#[tokio::test]
async fn saved_office_cpu_curve_republishes_t0_t15_strings_after_mode_roundtrip() {
    let mut backend = started_with_profiles().await;
    backend
        .set_performance_mode("office")
        .await
        .expect("current_mode office");
    backend
        .set_fan_curve("curve", "CPU", DUTIES.to_vec())
        .await
        .expect("save CPU curve");
    backend
        .set_performance_mode("turbo")
        .await
        .expect("leave office");
    backend
        .set_performance_mode("office")
        .await
        .expect("return office");
    let publishes = backend.recorded_publishes();
    let after = &publishes[last_office_idx(&publishes) + 1..];
    let payload = after
        .iter()
        .find(|(topic, value)| {
            topic == "Fan/Control"
                && value["Action"] == "SET_FAN_SPEED_CURVE_SETTING"
                && value["Type"] == "CPU"
                && value["Name"] == "curve"
        })
        .map(|(_, value)| value)
        .expect("CPU curve after returning to office");
    // C# asserts only the first `valid` points (FanCurveForm.cs:347-350). With the
    // all-sentinel temperatures this path synthesizes, `valid` is min(11, 16) per
    // MechrevoHw.cs:2712, and the tail follows the C# zero-fill rather than the
    // requested values, so it stays outside the asserted window.
    const VALID: usize = 11;
    for i in 0..VALID {
        let key = format!("T{i}");
        assert!(
            payload[&key].is_string(),
            "{key} must be a JSON string: {payload:?}"
        );
        assert_eq!(payload[&key], DUTIES[i].to_string());
    }
    for i in VALID..16 {
        let key = format!("T{i}");
        assert!(
            payload[&key].is_string(),
            "{key} must be a JSON string: {payload:?}"
        );
    }
}

#[tokio::test]
async fn stored_custom_profile_index_publishes_json_number_when_custom_mode() {
    for slot in 0..=3_i64 {
        // Given: customProfileIndex already persisted in mode-profiles.json
        let dir = unique_profile_dir();
        fs::write(
            dir.join("mode-profiles.json"),
            format!(r#"{{"customProfileIndex":"{slot}"}}"#),
        )
        .expect("write stored custom slot");
        let mut backend = Backend::fake_from_json("{}")
            .expect("empty ItemSupport")
            .with_profile_dir(dir);
        backend.start().await.expect("fake handshake");

        // When: custom mode is selected
        backend
            .set_performance_mode("custom")
            .await
            .expect("custom mode from stored slot");

        // Then: Fan OPERATING_CUSTOM_MODE ProfileIndex is that stored JSON number
        let publishes = backend.recorded_publishes();
        let payload = publishes
            .iter()
            .rev()
            .find(|(topic, value)| {
                topic == "Fan/Control" && value["Action"] == "OPERATING_CUSTOM_MODE"
            })
            .map(|(_, value)| value)
            .expect("OPERATING_CUSTOM_MODE");
        assert!(
            payload["ProfileIndex"].is_number(),
            "ProfileIndex must be a JSON number for stored slot {slot}: {payload:?}"
        );
        assert_eq!(
            payload["ProfileIndex"], slot,
            "stored slot {slot} must be published as ProfileIndex: {payload:?}"
        );
    }
}

#[tokio::test]
async fn saved_custom_pl2_republishes_string_after_mode_roundtrip() {
    let mut backend = started_with_profiles().await;
    backend
        .set_performance_mode("custom")
        .await
        .expect("current_mode custom");
    backend
        .set_custom_detail("PL2", "45")
        .await
        .expect("save PL2");
    backend
        .set_performance_mode("office")
        .await
        .expect("leave custom");
    backend
        .set_performance_mode("custom")
        .await
        .expect("return custom");
    let publishes = backend.recorded_publishes();
    let after = &publishes[last_action_idx(&publishes, "OPERATING_CUSTOM_MODE") + 1..];
    assert!(
        after.iter().any(|(topic, payload)| {
            topic == "Fan/Control"
                && payload["Action"] == "SET_OPERATING_MODE_DETAIL"
                && payload["PL2"].is_string()
                && payload["PL2"] == "45"
        }),
        "stored PL2 string 45 after mode packet: {after:?}"
    );
}
