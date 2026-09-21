//! Given FakeBroker. When set_performance_mode. Then official Fan + LCHWOC packets.

use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

use app_lib::Backend;

static PROFILE_SEQ: AtomicU64 = AtomicU64::new(0);

async fn started() -> Backend {
    let mut backend = Backend::fake_from_json("{}").expect("empty ItemSupport");
    backend.start().await.expect("fake handshake");
    backend
}

fn unique_profile_dir() -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "lmechrevo-host-modes-{}-{}",
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

fn has_fan(publishes: &[(String, serde_json::Value)], action: &str, profile: i64) -> bool {
    publishes.iter().any(|(topic, payload)| {
        topic == "Fan/Control"
            && payload["Action"] == action
            && payload["ProfileIndex"] == profile
            && payload["ProfileIndex"].is_number()
    })
}

fn has_lchwoc_normal(publishes: &[(String, serde_json::Value)], run: i64) -> bool {
    publishes.iter().any(|(topic, payload)| {
        topic == "LCHWOC/Control"
            && payload["IsNormalRun"] == run
            && payload["IsNormalRun"].is_number()
    })
}

#[tokio::test]
async fn set_performance_mode_gaming_publishes_fan_and_lchwoc_when_fake_broker() {
    let mut backend = started().await;
    backend
        .set_performance_mode("gaming")
        .await
        .expect("gaming mode");
    let publishes = backend.recorded_publishes();
    assert!(
        has_fan(&publishes, "OPERATING_GAMING_MODE", 0),
        "Fan OPERATING_GAMING_MODE missing: {publishes:?}"
    );
    assert!(
        has_lchwoc_normal(&publishes, 1),
        "LCHWOC IsNormalRun=1 missing: {publishes:?}"
    );
}

#[tokio::test]
async fn set_performance_mode_turbo_publishes_fan_and_lchwoc_when_fake_broker() {
    let mut backend = started().await;
    backend
        .set_performance_mode("turbo")
        .await
        .expect("turbo mode");
    let publishes = backend.recorded_publishes();
    assert!(
        has_fan(&publishes, "OPERATING_TURBO_MODE", 0),
        "Fan OPERATING_TURBO_MODE missing: {publishes:?}"
    );
    assert!(
        has_lchwoc_normal(&publishes, 2),
        "LCHWOC IsNormalRun=2 missing: {publishes:?}"
    );
}

fn last_custom_fan(publishes: &[(String, serde_json::Value)]) -> &serde_json::Value {
    publishes
        .iter()
        .rev()
        .find(|(topic, payload)| {
            topic == "Fan/Control" && payload["Action"] == "OPERATING_CUSTOM_MODE"
        })
        .map(|(_, payload)| payload)
        .expect("OPERATING_CUSTOM_MODE")
}

#[tokio::test]
async fn set_performance_mode_custom_publishes_selected_profile_index_when_fake_broker() {
    for slot in 0..=3_i64 {
        let mut backend = started_with_profiles().await;
        backend
            .set_performance_mode("custom")
            .await
            .expect("current_mode custom");
        backend
            .set_custom_detail("ProfileIndex", &slot.to_string())
            .await
            .expect("store selected custom slot");
        backend
            .set_performance_mode("custom")
            .await
            .expect("custom mode");
        let publishes = backend.recorded_publishes();
        let payload = last_custom_fan(&publishes);
        assert!(
            payload["ProfileIndex"].is_number(),
            "ProfileIndex must be a JSON number for slot {slot}: {payload:?}"
        );
        assert_eq!(
            payload["ProfileIndex"], slot,
            "Fan OPERATING_CUSTOM_MODE ProfileIndex must equal selected slot {slot}: {publishes:?}"
        );
        assert!(
            publishes.iter().any(|(topic, payload)| {
                topic == "LCHWOC/Control"
                    && payload["IsCustomRun"] == true
                    && payload["IsCustomRun"].is_boolean()
            }),
            "LCHWOC IsCustomRun=true missing: {publishes:?}"
        );
    }
}

#[tokio::test]
async fn set_performance_mode_silent_turbo_publishes_silent_offset_when_fake_broker() {
    let mut backend = started().await;
    backend
        .set_performance_mode("silentTurbo")
        .await
        .expect("silent turbo");
    let publishes = backend.recorded_publishes();
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "Fan/Control"
                && payload["Action"] == "SET_CPU_CORE_OFFSET_SILENT"
                && payload["SILENT"] == 1
                && payload["SILENT"].is_number()
        }),
        "Fan SET_CPU_CORE_OFFSET_SILENT SILENT=1 missing: {publishes:?}"
    );
}

#[tokio::test]
async fn set_performance_mode_extreme_publishes_extreme_offset_when_fake_broker() {
    // Given: Fake broker after handshake
    let mut backend = started().await;

    // When: extreme turbo sub-mode is selected (C# SwitchTurboSubMode(silent: false))
    backend
        .set_performance_mode("extreme")
        .await
        .expect("extreme turbo");

    // Then: Fan/Control SET_CPU_CORE_OFFSET_EXTREME EXTREME=1 (JSON number)
    let publishes = backend.recorded_publishes();
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "Fan/Control"
                && payload["Action"] == "SET_CPU_CORE_OFFSET_EXTREME"
                && payload["EXTREME"] == 1
                && payload["EXTREME"].is_number()
        }),
        "Fan SET_CPU_CORE_OFFSET_EXTREME EXTREME=1 missing: {publishes:?}"
    );
}

#[tokio::test]
async fn set_performance_mode_custom_publishes_stored_slot_from_profile_file_when_fake_broker() {
    for slot in 0..=3_i64 {
        // Given: mode-profiles.json already holds customProfileIndex
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

        // Then: OPERATING_CUSTOM_MODE ProfileIndex is that stored JSON number
        let publishes = backend.recorded_publishes();
        let payload = last_custom_fan(&publishes);
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
async fn stored_custom_profile_index_is_slot_0_to_3_when_real() {
    for slot in 0..=3_u8 {
        // Given: Real backend with a stored custom slot on disk
        let dir = unique_profile_dir();
        fs::write(
            dir.join("mode-profiles.json"),
            format!(r#"{{"customProfileIndex":"{slot}"}}"#),
        )
        .expect("write stored custom slot");
        let mut backend = Backend::real();
        backend.set_profile_dir(dir);

        // When: the Real path reads the stored slot
        let index = backend.real_custom_profile_index();

        // Then: it is the stored 0..=3 value, not hardcoded 0
        assert_eq!(index, slot, "Real stored custom slot must be {slot}");
    }
}
