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

#[tokio::test]
async fn set_light_power_publishes_hidlightbar_ctrl_when_lightbar_supported() {
    let mut backend = Backend::fake_from_json(r#"{"LightbarSupport":1}"#).expect("parse");
    backend.start().await.expect("handshake");
    backend
        .set_light_power("lightbar", true)
        .await
        .expect("LightbarSupport must allow power");
    let publishes = backend.recorded_publishes();
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "HidLightbar/Ctrl"
                && payload["function"] == "SetPower"
                && payload["powerstatus"] == 1
        }),
        "HidLightbar/Ctrl SetPower powerstatus=1 missing: {publishes:?}"
    );
}

#[tokio::test]
async fn set_light_power_rejects_lightbar_when_g16_itemsupport_hides_it() {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("crates/_golden/item_support_g16_no_lightbar.json");
    let json = fs::read_to_string(&path).unwrap_or_else(|err| panic!("{}: {err}", path.display()));
    let mut backend = Backend::fake_from_json(&json).expect("g16 golden");
    backend.start().await.expect("handshake");
    let err = backend
        .set_light_power("lightbar", true)
        .await
        .expect_err("G16 must deny lightbar power");
    assert!(
        err.to_string().contains("lighting"),
        "denied error, got {err}"
    );
    let publishes = backend.recorded_publishes();
    assert!(
        !publishes.iter().any(|(topic, payload)| {
            topic == "HidLightbar/Ctrl" && payload["function"] == "SetPower"
        }),
        "G16 must not publish HidLightbar/Ctrl SetPower: {publishes:?}"
    );
}

#[tokio::test]
async fn set_light_effect_publishes_official_light_speed_strings() {
    let mut backend = Backend::fake_from_json(r#"{"LightbarSupport":1}"#).expect("parse");
    backend.start().await.expect("handshake");
    backend
        .set_light_effect("lightbar", "Single")
        .await
        .expect("LightbarSupport must allow write");
    let publishes = backend.recorded_publishes();
    let payload = publishes
        .iter()
        .find(|(topic, body)| topic == "HidLightbar/Ctrl" && body["function"] == "SetEffectALL")
        .map(|(_, body)| body)
        .expect("HidLightbar/Ctrl SetEffectALL missing");
    assert!(
        payload["light"].is_string(),
        "Official light must be a JSON string: {payload}"
    );
    assert!(
        payload["speed"].is_string(),
        "Official speed must be a JSON string: {payload}"
    );
    assert_eq!(
        payload["light"], "4",
        "Official light is a string: {payload}"
    );
    assert_eq!(
        payload["speed"], "1",
        "Official speed is a string: {payload}"
    );
    assert_no_close_timer(&publishes);
}

#[tokio::test]
async fn set_light_effect_uses_caller_light_speed_color_strings() {
    let mut backend = Backend::fake_from_json(r#"{"LightbarSupport":1}"#).expect("parse");
    backend.start().await.expect("handshake");
    backend
        .set_light_params("lightbar", "Single", Some("2"), Some("3"), Some("#FF0000"))
        .await
        .expect("LightbarSupport must allow write");
    let publishes = backend.recorded_publishes();
    let payload = publishes
        .iter()
        .find(|(topic, body)| topic == "HidLightbar/Ctrl" && body["function"] == "SetEffectALL")
        .map(|(_, body)| body)
        .expect("HidLightbar/Ctrl SetEffectALL missing");
    assert_eq!(payload["light"], "2", "caller light string: {payload}");
    assert_eq!(payload["speed"], "3", "caller speed string: {payload}");
    assert_eq!(
        payload["color"]["ColorBlocks"], 1,
        "Single uses one block: {payload}"
    );
    assert_eq!(payload["color"]["ColorBuffer"][0]["R"], 255);
    assert_eq!(payload["color"]["ColorBuffer"][0]["G"], 0);
    assert_eq!(payload["color"]["ColorBuffer"][0]["B"], 0);
    assert!(
        payload["light"].is_string(),
        "caller light must be a JSON string: {payload}"
    );
    assert!(
        payload["speed"].is_string(),
        "caller speed must be a JSON string: {payload}"
    );
    assert_no_close_timer(&publishes);
}

const IDLE_NONE: u8 = 0;
const IDLE_SUSPEND: u8 = 1;
const IDLE_RESTORE: u8 = 2;

const OFFERED_CTRL: [&str; 3] = [
    "Keyboard/Ctrl",
    "HidLightbar/Ctrl",
    "HidLightbar_Logo/Ctrl",
];

fn g16_item_support() -> String {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("crates/_golden/item_support_g16_no_lightbar.json");
    fs::read_to_string(&path).unwrap_or_else(|err| panic!("{}: {err}", path.display()))
}

fn assert_no_close_timer(publishes: &[(String, serde_json::Value)]) {
    for (topic, payload) in publishes {
        let Some(obj) = payload.as_object() else {
            continue;
        };
        assert!(
            !obj.contains_key("CloseTimer"),
            "firmware CloseTimer must never be sent: {topic} {payload}"
        );
    }
}

fn set_power_on_topic(
    publishes: &[(String, serde_json::Value)],
    topic: &str,
    powerstatus: i64,
) -> bool {
    publishes.iter().any(|(published_topic, payload)| {
        published_topic == topic
            && payload["function"] == "SetPower"
            && payload["powerstatus"] == powerstatus
    })
}

#[test]
fn lighting_idle_decision_mirrors_csharp_lighting_state() {
    // LightingState.cs:12-19 / LightingStateTests IdleLightingPolicy
    assert_eq!(Backend::lighting_idle_action(60_000, 0, false), IDLE_NONE);
    assert_eq!(Backend::lighting_idle_action(9_999, 10, false), IDLE_NONE);
    assert_eq!(
        Backend::lighting_idle_action(10_000, 10, false),
        IDLE_SUSPEND
    );
    assert_eq!(Backend::lighting_idle_action(10_000, 10, true), IDLE_NONE);
    assert_eq!(
        Backend::lighting_idle_action(1_999, 10, true),
        IDLE_RESTORE
    );
    assert_eq!(Backend::lighting_idle_action(2_000, 10, true), IDLE_NONE);
}

#[tokio::test]
async fn lighting_battery_policy_sets_power_off_on_all_offered_channels() {
    // Given: lightingOffOnBattery=true and on_battery=true, all three channels offered
    let mut backend = Backend::fake_from_json(r#"{"LightbarSupport":1,"LogoLightSupport":1}"#)
        .expect("parse")
        .with_on_battery(true);
    backend.start().await.expect("handshake");

    // When: power policy is reconciled
    backend
        .reconcile_lighting_power(true, 0)
        .await
        .expect("battery policy");

    // Then: SetPower powerstatus=0 on every offered channel, never CloseTimer
    let publishes = backend.recorded_publishes();
    for topic in OFFERED_CTRL {
        assert!(
            set_power_on_topic(&publishes, topic, 0),
            "battery suspend must SetPower 0 on {topic}: {publishes:?}"
        );
    }
    assert_no_close_timer(&publishes);
}

#[tokio::test]
async fn lighting_battery_policy_skips_g16_hidden_lightbar_and_logo() {
    // Given: G16 ItemSupport hides lightbar/logo; battery policy is on
    let mut backend = Backend::fake_from_json(&g16_item_support())
        .expect("g16 golden")
        .with_on_battery(true);
    backend.start().await.expect("handshake");

    // When: power policy is reconciled
    backend
        .reconcile_lighting_power(true, 0)
        .await
        .expect("battery policy");

    // Then: hidden channels stay unpublished (S6 fail-closed)
    let publishes = backend.recorded_publishes();
    assert!(
        !publishes.iter().any(|(topic, payload)| {
            topic == "HidLightbar/Ctrl" && payload["function"] == "SetPower"
        }),
        "G16 must not SetPower lightbar: {publishes:?}"
    );
    assert!(
        !publishes.iter().any(|(topic, payload)| {
            topic == "HidLightbar_Logo/Ctrl" && payload["function"] == "SetPower"
        }),
        "G16 must not SetPower logo: {publishes:?}"
    );
    assert!(
        set_power_on_topic(&publishes, "Keyboard/Ctrl", 0),
        "G16 keyboard is still offered: {publishes:?}"
    );
    assert_no_close_timer(&publishes);
}

#[tokio::test]
async fn lighting_idle_policy_suspends_then_restores_on_fresh_input() {
    // Given: lightingIdleSeconds=1 and idleMs>=1000
    let mut backend = Backend::fake_from_json(r#"{"LightbarSupport":1,"LogoLightSupport":1}"#)
        .expect("parse")
        .with_idle_ms(1000);
    backend.start().await.expect("handshake");

    // When: idle reaches timeout
    backend
        .reconcile_lighting_power(false, 1)
        .await
        .expect("idle suspend");

    // Then: all offered channels suspend
    let after_suspend = backend.recorded_publishes();
    for topic in OFFERED_CTRL {
        assert!(
            set_power_on_topic(&after_suspend, topic, 0),
            "idle suspend must SetPower 0 on {topic}: {after_suspend:?}"
        );
    }
    assert_no_close_timer(&after_suspend);

    // When: idleMs<2000 after suspend (LightingState.cs restore hysteresis)
    backend.set_idle_ms(500);
    backend
        .reconcile_lighting_power(false, 1)
        .await
        .expect("idle restore");

    // Then: all offered channels restore, still no CloseTimer
    let after_restore = backend.recorded_publishes();
    for topic in OFFERED_CTRL {
        assert!(
            set_power_on_topic(&after_restore, topic, 1),
            "idle restore must SetPower 1 on {topic}: {after_restore:?}"
        );
    }
    assert_no_close_timer(&after_restore);
}

#[tokio::test]
async fn lighting_idle_disabled_does_not_suspend() {
    // Given: timeout<=0 ⇒ None even if idle is long
    let mut backend = Backend::fake_from_json(r#"{"LightbarSupport":1}"#)
        .expect("parse")
        .with_idle_ms(60_000);
    backend.start().await.expect("handshake");

    // When: policy is reconciled with idle timer off
    backend
        .reconcile_lighting_power(false, 0)
        .await
        .expect("idle disabled");

    // Then: no temporary SetPower 0
    let publishes = backend.recorded_publishes();
    assert!(
        !publishes.iter().any(|(topic, payload)| {
            topic == "HidLightbar/Ctrl"
                && payload["function"] == "SetPower"
                && payload["powerstatus"] == 0
        }),
        "timeout<=0 must not suspend: {publishes:?}"
    );
    assert_no_close_timer(&publishes);
}
