//! Given FakeBroker + ItemSupport/Seen. When apply_quick_switch. Then official MQTT.

use std::fs;
use std::path::Path;

use app_lib::hw_switches::{
    apply_monitor_off, apply_quick_switch, offered_quick_switches, parse_device_switch_item_status,
    QuickSwitchGate, SeenFlags,
};
use app_lib::hw_wmi::FakeWmi;
use app_lib::Backend;
use capabilities::ItemSupport;
use gcu_mqtt::fake::{FakeBroker, Recorded};

fn empty_item() -> ItemSupport {
    ItemSupport::parse_json("{}").expect("empty ItemSupport")
}

fn publishes(broker: &FakeBroker) -> Vec<(String, serde_json::Value)> {
    broker
        .recorded()
        .iter()
        .filter_map(|item| match item {
            Recorded::Publish { topic, payload } => {
                let value = serde_json::from_slice(payload).ok()?;
                Some((topic.clone(), value))
            }
            Recorded::Subscribe(_) => None,
        })
        .collect()
}

#[test]
fn touchpad_always_offered_on_empty_itemsupport() {
    let item = empty_item();
    let seen = SeenFlags::default();
    let offered = offered_quick_switches(&item, &seen);
    assert!(
        offered.iter().any(|key| *key == "touchpad"),
        "touchpad must be offered with empty ItemSupport: {offered:?}"
    );
}

#[test]
fn parses_tochpadenable_not_touchpadenable() {
    let from_firmware =
        parse_device_switch_item_status(r#"{"TochpadEnable":true}"#).expect("firmware misspelling");
    assert_eq!(from_firmware.touchpad, Some(true));

    let correct_spelling = parse_device_switch_item_status(r#"{"TouchPadEnable":true}"#)
        .expect("correct spelling must not be a parse key");
    assert_eq!(
        correct_spelling.touchpad, None,
        "TouchPadEnable must not populate touchpad"
    );
}

#[test]
fn whisper_absent_from_offered_list() {
    let item = empty_item();
    let seen = SeenFlags {
        uni_omni: true,
        game_whitelist: true,
        ..SeenFlags::default()
    };
    let offered = offered_quick_switches(&item, &seen);
    assert!(
        !offered.iter().any(|key| *key == "whisper"),
        "whisper must never be offered: {offered:?}"
    );
}

#[tokio::test]
async fn gamewhitelist_switch_is_number_on_fan_control() {
    let item = empty_item();
    let seen = SeenFlags {
        game_whitelist: true,
        ..SeenFlags::default()
    };
    let gate = QuickSwitchGate {
        item_support: &item,
        seen,
    };
    let mut broker = FakeBroker::new();
    apply_quick_switch(&mut broker, &gate, "gamewhitelist", true)
        .await
        .expect("gamewhitelist offered");
    let publishes = publishes(&broker);
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "Fan/Control"
                && payload["Action"] == "OPERATING_GAME_WHITE_LIST"
                && payload["GameWhitelistSwitch"] == 1
                && payload["GameWhitelistSwitch"].is_number()
        }),
        "Fan/Control GameWhitelistSwitch must be JSON number 1: {publishes:?}"
    );
}

#[tokio::test]
async fn uni_on_publishes_omni_off() {
    let item = empty_item();
    let seen = SeenFlags {
        uni_omni: true,
        ..SeenFlags::default()
    };
    let gate = QuickSwitchGate {
        item_support: &item,
        seen,
    };
    let mut broker = FakeBroker::new();
    apply_quick_switch(&mut broker, &gate, "uni", true)
        .await
        .expect("uni offered");
    let publishes = publishes(&broker);
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "Setting/Control" && payload["Action"] == "Omni_OFF"
        }),
        "uni on must publish Omni_OFF first: {publishes:?}"
    );
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "Setting/Control" && payload["Action"] == "Uni_ON"
        }),
        "uni on must publish Uni_ON: {publishes:?}"
    );
    assert!(
        !publishes.iter().any(|(topic, payload)| {
            topic == "Setting/Control" && payload["Action"] == "Omni_ON"
        }),
        "uni and omni must not both be on: {publishes:?}"
    );
}

#[test]
fn monitor_off_sets_fake_wmi_brightness_0() {
    let mut wmi = FakeWmi::new();
    apply_monitor_off(&mut wmi);
    assert!(
        wmi.writes().iter().any(|write| write.brightness == 0),
        "monitor off must set FakeWmi brightness 0: {:?}",
        wmi.writes()
    );
}

#[tokio::test]
async fn backend_set_quick_switch_touchpad_after_start() {
    let mut backend = Backend::fake_from_json("{}").expect("parse");
    backend.start().await.expect("start");
    backend
        .set_quick_switch("touchpad", true)
        .await
        .expect("Backend::set_quick_switch");
    let publishes = backend.recorded_publishes();
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "Setting/Control"
                && payload["Action"] == "TOUCHPAD_ON"
                && payload["Action"] != "GETSTATUS"
        }),
        "Setting/Control TOUCHPAD_ON missing (GETSTATUS filtered): {publishes:?}"
    );
}

#[tokio::test]
async fn fanboost_publishes_fan_control_fan_boost_on() {
    let mut broker = FakeBroker::new();
    let item = empty_item();
    let gate = QuickSwitchGate {
        item_support: &item,
        seen: SeenFlags {
            fan_boost: true,
            ..SeenFlags::default()
        },
    };
    apply_quick_switch(&mut broker, &gate, "fanboost", true)
        .await
        .expect("fanboost");
    let pubs = publishes(&broker);
    assert!(
        pubs.iter().any(|(topic, payload)| {
            topic == "Fan/Control" && payload["Action"] == "FAN_BOOST_ON"
        }),
        "FAN_BOOST_ON missing: {pubs:?}"
    );
}

#[tokio::test]
async fn deepsleep_on_secs_is_string() {
    let mut broker = FakeBroker::new();
    let item = empty_item();
    let gate = QuickSwitchGate {
        item_support: &item,
        seen: SeenFlags {
            deep_sleep: true,
            ..SeenFlags::default()
        },
    };
    apply_quick_switch(&mut broker, &gate, "deepsleep", true)
        .await
        .expect("deepsleep");
    let pubs = publishes(&broker);
    let payload = pubs
        .iter()
        .find(|(topic, value)| topic == "Setting/Control" && value["Action"] == "DEEPSLEEP_ON")
        .map(|(_, value)| value)
        .expect("DEEPSLEEP_ON");
    assert!(
        payload["Secs"].is_string(),
        "Secs must be a JSON string: {payload:?}"
    );
}

#[test]
fn lightbar_seen_without_itemsupport_is_not_offered() {
    let item = empty_item();
    let seen = SeenFlags {
        lightbar: true,
        ..SeenFlags::default()
    };
    let offered = offered_quick_switches(&item, &seen);
    assert!(
        !offered.iter().any(|key| *key == "lightbar"),
        "lightbar must not be offered from MQTT Seen alone: {offered:?}"
    );
}

#[test]
fn logolight_offered_from_itemsupport_when_seen_false() {
    let item = ItemSupport::parse_json(r#"{"LogoLightSupport":1}"#).expect("LogoLightSupport");
    let seen = SeenFlags::default();
    let offered = offered_quick_switches(&item, &seen);
    assert!(
        offered.iter().any(|key| *key == "logolight"),
        "logolight must be offered from LogoLightSupport=1: {offered:?}"
    );
}

#[test]
fn g16_golden_offers_neither_lightbar_nor_logolight_even_if_seen() {
    let path = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("crates/_golden/item_support_g16_no_lightbar.json");
    let json = fs::read_to_string(&path).unwrap_or_else(|err| panic!("{}: {err}", path.display()));
    let item = ItemSupport::parse_json(&json).expect("g16 golden");
    let seen = SeenFlags {
        lightbar: true,
        logo_light: true,
        ..SeenFlags::default()
    };
    let offered = offered_quick_switches(&item, &seen);
    assert!(
        !offered.iter().any(|key| *key == "lightbar"),
        "G16 must not offer lightbar: {offered:?}"
    );
    assert!(
        !offered.iter().any(|key| *key == "logolight"),
        "G16 must not offer logolight: {offered:?}"
    );
}
