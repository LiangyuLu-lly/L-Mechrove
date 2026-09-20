//! Given FakeBroker. When set_performance_mode("office"). Then both office packets recorded.

use app_lib::Backend;

#[tokio::test]
async fn set_performance_mode_office_publishes_fan_and_lchwoc_when_fake_broker() {
    let mut backend = Backend::fake_from_json("{}").expect("empty ItemSupport");
    backend.start().await.expect("fake handshake");

    backend
        .set_performance_mode("office")
        .await
        .expect("office mode");

    let publishes = backend.recorded_publishes();
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "Fan/Control"
                && payload["Action"] == "OPERATING_OFFICE_MODE"
                && payload["ProfileIndex"] == 0
                && payload["ProfileIndex"].is_number()
        }),
        "Fan OPERATING_OFFICE_MODE ProfileIndex=0 missing: {publishes:?}"
    );
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "LCHWOC/Control"
                && payload["IsNormalRun"] == 0
                && payload["IsNormalRun"].is_number()
        }),
        "LCHWOC IsNormalRun=0 missing: {publishes:?}"
    );
    assert!(
        !publishes.iter().any(|(topic, payload)| {
            topic == "Fan/Control"
                && (payload["Action"] == "SET_OPERATING_MODE_DETAIL"
                    || payload["Action"] == "SET_FAN_SPEED_CURVE_SETTING")
        }),
        "missing profile must not publish extra PL1/curve: {publishes:?}"
    );
}

#[tokio::test]
async fn fake_handshake_records_system_on_in_memory_without_tcp_13688() {
    let mut backend = Backend::fake_from_json("{}").expect("empty ItemSupport");
    backend
        .start()
        .await
        .expect("in-memory handshake must not open 127.0.0.1:13688");

    let publishes = backend.recorded_publishes();
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "System/Control" && payload["Action"] == "System_ON"
        }),
        "in-process FakeBroker must record System_ON without TCP: {publishes:?}"
    );
}
