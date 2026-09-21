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

#[tokio::test]
async fn snapshot_silent_turbo_true_when_is_turbo_sub_mode_support() {
    let mut backend =
        Backend::fake_from_json(r#"{"IsTurboSubModeSupport":1}"#).expect("parse ItemSupport");
    backend.start().await.expect("fake handshake");

    assert!(
        backend.snapshot().silent_turbo,
        "IsTurboSubModeSupport=1 is C# SilentTurboAvailability::Supported"
    );
}

#[tokio::test]
async fn snapshot_silent_turbo_false_when_itemsupport_omits_key() {
    let mut backend = Backend::fake_from_json("{}").expect("empty ItemSupport");
    backend.start().await.expect("fake handshake");

    assert!(
        !backend.snapshot().silent_turbo,
        "missing IsTurboSubModeSupport is C# Unsupported/Unknown"
    );
}

#[tokio::test]
async fn snapshot_fake_includes_default_overlay_telemetry() {
    // Given: Fake backend (no LHM)
    let mut backend = Backend::fake_from_json("{}").expect("empty ItemSupport");
    backend.start().await.expect("fake handshake");

    // When: a snapshot is taken
    let snapshot = backend.snapshot();

    // Then: Default overlay fields are stable fixtures
    assert_eq!(snapshot.cpu_temp_c, Some(78.0));
    assert_eq!(snapshot.gpu_temp_c, Some(82.0));
    assert_eq!(snapshot.cpu_rpm, Some(2100));
    assert_eq!(snapshot.gpu_rpm, Some(2100));
    assert_eq!(snapshot.cpu_watt, Some(45.0));
    assert_eq!(snapshot.gpu_watt, Some(80.0));
}

#[test]
fn snapshot_real_omits_overlay_telemetry() {
    // Given: Real backend without LHM
    let backend = Backend::real();

    // When: a snapshot is taken
    let snapshot = backend.snapshot();

    // Then: telemetry is absent
    assert_eq!(snapshot.cpu_temp_c, None);
    assert_eq!(snapshot.gpu_temp_c, None);
    assert_eq!(snapshot.cpu_rpm, None);
    assert_eq!(snapshot.gpu_rpm, None);
    assert_eq!(snapshot.cpu_watt, None);
    assert_eq!(snapshot.gpu_watt, None);
}

#[test]
fn snapshot_from_matches_for_fake_and_real_when_item_support_equal() {
    // Given: the same ItemSupport blob on Fake and Real
    let json = r#"{"LightbarSupport":1,"IsTurboSubModeSupport":1,"LiquidCoolingSupport":1}"#;
    let fake = Backend::fake_from_json(json).expect("fake");
    let mut real = Backend::real();
    real.apply_item_support(capabilities::ItemSupport::parse_json(json).expect("item"));

    // When: both snapshots are taken
    let fake_snap = fake.snapshot();
    let real_snap = real.snapshot();

    // Then: lighting / silent_turbo / liquid_cooling cannot diverge
    assert_eq!(fake_snap.lighting, real_snap.lighting);
    assert_eq!(fake_snap.silent_turbo, real_snap.silent_turbo);
    assert_eq!(fake_snap.liquid_cooling, real_snap.liquid_cooling);
    assert!(
        real_snap.write_allowed,
        "N8: non-empty ItemSupport is served"
    );
    assert!(real_snap.lighting.lightbar);
    assert!(real_snap.silent_turbo);
    assert!(real_snap.liquid_cooling);
}

#[test]
fn snapshot_real_hides_g16_lightbar_even_when_mqtt_seen() {
    // Given: G16 ItemSupport (lightbar/logo off) plus an inbound lightbar report
    let mut real = Backend::real();
    real.apply_item_support(capabilities::ItemSupport::parse_json(&g16_json()).expect("g16"));
    real.apply_inbound(
        "HidLightbar/Status",
        br#"{"type":"rainbow","powerStatus":"1"}"#,
    );

    // When: a snapshot is taken
    let snapshot = real.snapshot();

    // Then: lighting stays ItemSupport-only (never Seen-OR)
    assert!(
        !snapshot.lighting.lightbar,
        "G16 must hide lightbar even if MQTT Seen would have been true"
    );
    assert!(!snapshot.lighting.logo);
}

#[test]
fn snapshot_includes_parity_dto_fields() {
    // Given: Fake backend snapshot serialized to JSON
    let backend = Backend::fake_from_json("{}").expect("empty ItemSupport");
    let value = serde_json::to_value(backend.snapshot()).expect("serialize");
    let obj = value.as_object().expect("object");

    // When/Then: C# parity DTO keys are present on the wire
    for key in [
        "keyboardHidUnavailable",
        "lightingOffOnBattery",
        "lightingIdleSeconds",
        "modelReason",
        "projectId",
        "ocRequiresElevation",
        "themeMode",
        "releaseLabel",
    ] {
        assert!(obj.contains_key(key), "missing serde key {key}");
    }
}

#[test]
fn snapshot_includes_hub_fields_the_ui_reads() {
    // Given: Fake backend with empty ItemSupport (fail-closed catalog)
    let backend = Backend::fake_from_json("{}").expect("empty ItemSupport");
    let snapshot = backend.snapshot();
    let value = serde_json::to_value(&snapshot).expect("serialize");
    let obj = value.as_object().expect("object");

    // When/Then: every field a landed UI surface reads is on the wire
    for key in [
        "batteryHealth",
        "chargeStatus",
        "chargeFullOffered",
        "overdrive",
        "localDimming",
        "customProfileOffered",
        "lcConnection",
        "fanCurveTableName",
        "updateAvailable",
    ] {
        assert!(obj.contains_key(key), "missing serde key {key}");
    }
    assert_eq!(snapshot.battery_health, "");
    assert_eq!(snapshot.charge_status, "");
    assert!(snapshot.charge_full_offered);
    assert!(!snapshot.overdrive);
    assert!(!snapshot.local_dimming);
    assert!(
        snapshot.custom_profile_offered,
        "FanSettings is vendor-constant-on for non-commercial"
    );
    assert_eq!(snapshot.lc_connection, "none");
    assert_eq!(snapshot.fan_curve_table_name, "M4T1");
    assert!(!snapshot.update_available);
}

#[test]
fn snapshot_offers_overdrive_and_local_dimming_from_item_support() {
    // Given: ItemSupport bits the Screen row reads
    let backend = Backend::fake_from_json(
        r#"{"LcdOverDriveSupport":1,"LocalDimmingSupport":1,"LiquidCoolingSupport":1}"#,
    )
    .expect("parse");
    let snapshot = backend.snapshot();

    // When/Then: offered flags follow ItemSupport, LC chip is GCU when MQTT LC is up
    assert!(snapshot.overdrive);
    assert!(snapshot.local_dimming);
    assert_eq!(snapshot.lc_connection, "gcu");
}
