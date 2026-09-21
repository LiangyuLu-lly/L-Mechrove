//! Given OfficialConsoleIsolation.cs. When set_official_isolation. Then record, no MQTT.

use app_lib::Backend;

#[tokio::test]
async fn set_official_isolation_true_records_without_mqtt_publish() {
    let mut backend = Backend::fake_from_json("{}").expect("parse");
    backend.start().await.expect("start");
    let before = backend.recorded_publishes().len();
    backend
        .set_official_isolation(true)
        .expect("isolate records");
    let publishes = backend.recorded_publishes();
    assert_eq!(
        publishes.len(),
        before,
        "isolation must not publish MQTT: {publishes:?}"
    );
    assert_eq!(backend.recorded_official_isolation(), Some(true));
}

#[tokio::test]
async fn set_official_isolation_false_records_restore() {
    let mut backend = Backend::fake_from_json("{}").expect("parse");
    backend.start().await.expect("start");
    let after_handshake = backend.recorded_publishes().len();
    backend.set_official_isolation(true).expect("isolate");
    backend.set_official_isolation(false).expect("restore");
    assert_eq!(backend.recorded_official_isolation(), Some(false));
    assert_eq!(
        backend.recorded_publishes().len(),
        after_handshake,
        "isolation itself must add no MQTT publish"
    );
}
