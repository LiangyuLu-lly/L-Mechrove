//! Lighting write path. ItemSupport fail-closed. Keyboard/lightbar/logo MQTT SetEffectALL.

use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

use app_lib::Backend;
use hid_kb::STEP1;

static CFG_SEQ: AtomicU64 = AtomicU64::new(0);

fn unique_cfg_dir() -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "lmechrevo-light-{}-{}",
        std::process::id(),
        CFG_SEQ.fetch_add(1, Ordering::Relaxed)
    ));
    fs::create_dir_all(&dir).expect("temp lighting cfg dir");
    dir
}

#[tokio::test]
async fn set_light_effect_rejects_lightbar_when_g16_itemsupport_hides_it() {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("crates/_golden/item_support_g16_no_lightbar.json");
    let json = fs::read_to_string(&path).unwrap_or_else(|err| panic!("{}: {err}", path.display()));
    let mut backend = Backend::fake_from_json(&json).expect("g16 golden");
    backend.start().await.expect("handshake");
    let err = backend
        .set_light_effect("lightbar", "Single")
        .await
        .expect_err("G16 must deny lightbar write");
    assert!(
        err.to_string().contains("lighting"),
        "denied error, got {err}"
    );
    let publishes = backend.recorded_publishes();
    assert!(
        !publishes.iter().any(|(topic, payload)| {
            topic == "HidLightbar/Ctrl" && payload["function"] == "SetEffectALL"
        }),
        "G16 must not publish HidLightbar/Ctrl SetEffectALL: {publishes:?}"
    );
}

#[tokio::test]
async fn set_light_effect_publishes_hidlightbar_ctrl_when_lightbar_supported() {
    let mut backend = Backend::fake_from_json(r#"{"LightbarSupport":1}"#).expect("parse");
    backend.start().await.expect("handshake");
    backend
        .set_light_effect("lightbar", "Single")
        .await
        .expect("LightbarSupport must allow write");
    let publishes = backend.recorded_publishes();
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "HidLightbar/Ctrl"
                && payload["function"] == "SetEffectALL"
                && payload["effect"] == "Single"
        }),
        "HidLightbar/Ctrl SetEffectALL Single missing: {publishes:?}"
    );
}

#[tokio::test]
async fn set_light_effect_publishes_logo_ctrl_when_logo_supported() {
    let mut backend = Backend::fake_from_json(r#"{"LogoLightSupport":1}"#).expect("parse");
    backend.start().await.expect("handshake");
    backend
        .set_light_effect("logo", "Single")
        .await
        .expect("LogoLightSupport must allow write");
    let publishes = backend.recorded_publishes();
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "HidLightbar_Logo/Ctrl"
                && payload["function"] == "SetEffectALL"
                && payload["effect"] == "Single"
        }),
        "HidLightbar_Logo/Ctrl SetEffectALL missing: {publishes:?}"
    );
}

#[tokio::test]
async fn set_light_effect_keyboard_writes_hid_step1_not_keyboard_ctrl() {
    let mut backend = Backend::fake_from_json("{}").expect("empty");
    backend.start().await.expect("handshake");
    backend
        .set_light_effect("keyboard", "Single")
        .await
        .expect("keyboard VendorConstantOn must allow MQTT write");
    let features = backend.recorded_hid_feature_reports();
    assert!(
        !features
            .iter()
            .any(|report| report.as_slice() == STEP1.as_slice()),
        "keyboard effect write must not send HID STEP1: {features:?}"
    );
    let publishes = backend.recorded_publishes();
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "Keyboard/Ctrl"
                && payload["function"] == "SetEffectALL"
                && payload["effect"] == "Single"
        }),
        "Keyboard/Ctrl SetEffectALL Single missing: {publishes:?}"
    );
}

#[tokio::test]
async fn set_light_effect_keyboard_wave_denied_when_keyboard_type_2() {
    let mut backend = Backend::fake_from_json(r#"{"KeyboardType":2}"#).expect("parse");
    backend.start().await.expect("handshake");
    let err = backend
        .set_light_effect("keyboard", "Wave")
        .await
        .expect_err("KeyboardType 2 must reject Wave");
    assert!(
        err.to_string().contains("lighting"),
        "denied error, got {err}"
    );
    let publishes = backend.recorded_publishes();
    assert!(
        !publishes.iter().any(|(topic, payload)| {
            topic == "Keyboard/Ctrl" && payload["function"] == "SetEffectALL"
        }),
        "KeyboardType 2 must not publish Keyboard/Ctrl SetEffectALL: {publishes:?}"
    );
}

#[tokio::test]
async fn set_light_effect_keyboard_wave_publishes_when_keyboard_type_0() {
    let mut backend = Backend::fake_from_json(r#"{"KeyboardType":0}"#).expect("parse");
    backend.start().await.expect("handshake");
    backend
        .set_light_effect("keyboard", "Wave")
        .await
        .expect("KeyboardType 0 uses GCU 11");
    let publishes = backend.recorded_publishes();
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "Keyboard/Ctrl"
                && payload["function"] == "SetEffectALL"
                && payload["effect"] == "Wave"
        }),
        "Keyboard/Ctrl SetEffectALL Wave missing: {publishes:?}"
    );
}

#[tokio::test]
async fn set_light_effect_keyboard_wave_publishes_when_keyboard_type_3() {
    let mut backend = Backend::fake_from_json(r#"{"KeyboardType":3}"#).expect("parse");
    backend.start().await.expect("handshake");
    backend
        .set_light_effect("keyboard", "Wave")
        .await
        .expect("KeyboardType 3 uses GCU 11");
    let publishes = backend.recorded_publishes();
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "Keyboard/Ctrl"
                && payload["function"] == "SetEffectALL"
                && payload["effect"] == "Wave"
        }),
        "Keyboard/Ctrl SetEffectALL Wave missing: {publishes:?}"
    );
}

#[tokio::test]
async fn set_light_effect_lightbar_mix_denied_impact_allowed() {
    let mut backend = Backend::fake_from_json(r#"{"LightbarSupport":1}"#).expect("parse");
    backend.start().await.expect("handshake");
    let err = backend
        .set_light_effect("lightbar", "Mix")
        .await
        .expect_err("lightbar Mix is not in catalog");
    assert!(
        err.to_string().contains("lighting"),
        "denied error, got {err}"
    );
    backend
        .set_light_effect("lightbar", "Impact")
        .await
        .expect("lightbar Impact is in catalog");
    let publishes = backend.recorded_publishes();
    assert!(
        !publishes.iter().any(|(topic, payload)| {
            topic == "HidLightbar/Ctrl"
                && payload["function"] == "SetEffectALL"
                && payload["effect"] == "Mix"
        }),
        "lightbar Mix must not publish: {publishes:?}"
    );
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "HidLightbar/Ctrl"
                && payload["function"] == "SetEffectALL"
                && payload["effect"] == "Impact"
        }),
        "HidLightbar/Ctrl SetEffectALL Impact missing: {publishes:?}"
    );
}

#[tokio::test]
async fn set_light_effect_logo_wave_denied_mix_allowed() {
    let mut backend = Backend::fake_from_json(r#"{"LogoLightSupport":1}"#).expect("parse");
    backend.start().await.expect("handshake");
    let err = backend
        .set_light_effect("logo", "Wave")
        .await
        .expect_err("logo Wave is not in catalog");
    assert!(
        err.to_string().contains("lighting"),
        "denied error, got {err}"
    );
    backend
        .set_light_effect("logo", "Mix")
        .await
        .expect("logo Mix is in catalog");
    let publishes = backend.recorded_publishes();
    assert!(
        !publishes.iter().any(|(topic, payload)| {
            topic == "HidLightbar_Logo/Ctrl"
                && payload["function"] == "SetEffectALL"
                && payload["effect"] == "Wave"
        }),
        "logo Wave must not publish: {publishes:?}"
    );
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "HidLightbar_Logo/Ctrl"
                && payload["function"] == "SetEffectALL"
                && payload["effect"] == "Mix"
        }),
        "HidLightbar_Logo/Ctrl SetEffectALL Mix missing: {publishes:?}"
    );
}

#[tokio::test]
async fn set_light_effect_persists_lightbar_cfg_under_override_dir() {
    let dir = unique_cfg_dir();
    let mut backend = Backend::fake_from_json(r#"{"LightbarSupport":1}"#)
        .expect("parse")
        .with_light_cfg_dir(dir.clone());
    backend.start().await.expect("handshake");
    backend
        .set_light_effect("lightbar", "Single")
        .await
        .expect("write");
    let cfg = fs::read_to_string(dir.join("lightbar.cfg")).expect("lightbar.cfg");
    assert!(
        cfg.contains("effect=Single"),
        "persisted effect, got {cfg:?}"
    );
}
