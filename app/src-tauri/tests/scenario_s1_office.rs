//! S1: Fake broker handshake then Office mode emits Fan + LCHWOC packets.

use app_lib::Backend;
use capabilities::ItemSupport;

#[tokio::test]
async fn s1_office_publishes_fan_and_lchwoc_after_handshake() {
    let mut backend = Backend::fake(ItemSupport::default());
    backend.start().await.expect("fake handshake");
    backend
        .set_performance_mode("office")
        .await
        .expect("office mode");

    let pubs = backend.recorded_publishes();
    assert!(
        pubs.iter().any(|(topic, payload)| {
            topic == "Fan/Control"
                && payload["Action"] == "OPERATING_OFFICE_MODE"
                && payload["ProfileIndex"] == 0
        }),
        "missing office Fan packet: {pubs:?}"
    );
    assert!(
        pubs.iter()
            .any(|(topic, payload)| { topic == "LCHWOC/Control" && payload["IsNormalRun"] == 0 }),
        "missing office LCHWOC packet: {pubs:?}"
    );
    assert!(
        pubs.iter().any(|(topic, payload)| {
            topic == "System/Control" && payload["Action"] == "System_ON"
        }),
        "handshake must publish System_ON first among control pubs"
    );
}
