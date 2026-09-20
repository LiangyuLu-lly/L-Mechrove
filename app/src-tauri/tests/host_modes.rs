//! Given FakeBroker. When set_performance_mode. Then official Fan + LCHWOC packets.

use app_lib::Backend;

async fn started() -> Backend {
    let mut backend = Backend::fake_from_json("{}").expect("empty ItemSupport");
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

#[tokio::test]
async fn set_performance_mode_custom_publishes_fan_and_is_custom_run_when_fake_broker() {
    let mut backend = started().await;
    backend
        .set_performance_mode("custom")
        .await
        .expect("custom mode");
    let publishes = backend.recorded_publishes();
    assert!(
        has_fan(&publishes, "OPERATING_CUSTOM_MODE", 0),
        "Fan OPERATING_CUSTOM_MODE ProfileIndex=0 missing: {publishes:?}"
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
