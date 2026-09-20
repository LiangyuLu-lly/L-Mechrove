//! Given Fake backend + G16 ItemSupport. When hw_snapshot. Then lighting from ItemSupport.

use std::fs;
use std::path::Path;

use app_lib::{Backend, MqttStatus};

fn g16_json() -> String {
    let path = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("crates/_golden/item_support_g16_no_lightbar.json");
    fs::read_to_string(&path).unwrap_or_else(|err| panic!("read {}: {err}", path.display()))
}

#[tokio::test]
async fn snapshot_hides_g16_logo_and_lightbar_when_itemsupport_false() {
    let mut backend = Backend::fake_from_json(&g16_json()).expect("parse G16 ItemSupport");
    backend.start().await.expect("fake handshake");

    let snapshot = backend.snapshot();

    assert!(
        !snapshot.lighting.logo,
        "G16 LogoLightSupport=0 must hide Logo"
    );
    assert!(
        !snapshot.lighting.lightbar,
        "G16 lightbar bits 0 must hide lightbar (ItemSupport, never MQTT Seen)"
    );
}

#[tokio::test]
async fn snapshot_reports_mqtt_connected_after_fake_handshake() {
    let mut backend = Backend::fake_from_json(&g16_json()).expect("parse G16 ItemSupport");
    backend.start().await.expect("fake handshake");

    let snapshot = backend.snapshot();

    assert_eq!(snapshot.mqtt, MqttStatus::Connected);
}
