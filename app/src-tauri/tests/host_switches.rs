//! Given FakeBroker + ItemSupport/Seen. When apply_quick_switch. Then official MQTT.

use std::fs;
use std::path::Path;
use std::sync::{Arc, Mutex};

use app_lib::hw_exec_state::{BLANK_EXECUTION_STATE, ES_CONTINUOUS};
use app_lib::hw_screen_blank::{inject, ScreenBlankExec, ScreenBlankWmi};
use app_lib::hw_switches::{
    apply_monitor_off, apply_power_light_brightness, apply_quick_switch, offered_quick_switches,
    parse_device_switch_item_status, QuickSwitchGate, SeenFlags,
};
use app_lib::hw_wmi::FakeWmi;
use app_lib::{Backend, HostError};
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

#[test]
fn monitoroff_offered_on_empty_itemsupport() {
    let item = empty_item();
    let seen = SeenFlags::default();
    let offered = offered_quick_switches(&item, &seen);
    assert!(
        offered.iter().any(|key| *key == "monitoroff"),
        "monitoroff must be offered with empty ItemSupport like the Windows switches: {offered:?}"
    );
}

#[tokio::test]
async fn backend_set_quick_switch_monitoroff_is_non_mqtt() {
    let mut backend = Backend::fake_from_json("{}").expect("parse");
    backend.start().await.expect("start");
    let before = backend.recorded_publishes().len();
    backend
        .set_quick_switch("monitoroff", true)
        .await
        .expect("monitoroff is a non-MQTT Windows action");
    let after = backend.recorded_publishes();
    assert_eq!(
        after.len(),
        before,
        "monitoroff must not publish MQTT: {after:?}"
    );
}

#[tokio::test]
async fn powerlight_on_carries_brightness_as_number() {
    let item = empty_item();
    let gate = QuickSwitchGate {
        item_support: &item,
        seen: SeenFlags {
            power_light: true,
            ..SeenFlags::default()
        },
    };
    let mut broker = FakeBroker::new();
    apply_quick_switch(&mut broker, &gate, "powerlight", true)
        .await
        .expect("powerlight offered");
    let pubs = publishes(&broker);
    let payload = pubs
        .iter()
        .find(|(topic, value)| topic == "Setting/Control" && value["Action"] == "PowerLight_ON")
        .map(|(_, value)| value)
        .expect("PowerLight_ON");
    assert!(
        payload["Brightness"].is_number(),
        "Brightness must be a JSON number: {payload:?}"
    );
    assert!(
        pubs.iter().any(|(topic, value)| {
            topic == "Setting/Control" && value["Action"] == "PowerLight_Brightness"
        }),
        "powerlight must emit PowerLight_Brightness: {pubs:?}"
    );
}

#[tokio::test]
async fn power_light_brightness_action_carries_number() {
    let mut broker = FakeBroker::new();
    apply_power_light_brightness(&mut broker, 80)
        .await
        .expect("PowerLight_Brightness");
    let pubs = publishes(&broker);
    let payload = pubs
        .iter()
        .find(|(topic, value)| {
            topic == "Setting/Control" && value["Action"] == "PowerLight_Brightness"
        })
        .map(|(_, value)| value)
        .expect("PowerLight_Brightness");
    assert!(
        payload["Brightness"].is_number(),
        "Brightness must be a JSON number: {payload:?}"
    );
    assert_eq!(
        payload["Brightness"], 80,
        "PowerLight_Brightness must carry the requested number: {payload:?}"
    );
}

#[tokio::test]
async fn deepsleep_on_secs_matches_csharp_900() {
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
    assert_eq!(
        payload["Secs"], "900",
        "Secs must match C# SwitchDeepSleep string 900: {payload:?}"
    );
}

#[tokio::test]
async fn touchpad_records_setting_getstatus_confirmation() {
    let item = empty_item();
    let gate = QuickSwitchGate {
        item_support: &item,
        seen: SeenFlags::default(),
    };
    let mut broker = FakeBroker::new();
    apply_quick_switch(&mut broker, &gate, "touchpad", true)
        .await
        .expect("touchpad offered");
    let pubs = publishes(&broker);
    let touchpad = pubs.iter().position(|(topic, payload)| {
        topic == "Setting/Control" && payload["Action"] == "TOUCHPAD_ON"
    });
    let getstatus = pubs.iter().position(|(topic, payload)| {
        topic == "Setting/Control" && payload["Action"] == "GETSTATUS"
    });
    assert!(
        matches!((touchpad, getstatus), (Some(command), Some(confirm)) if confirm > command),
        "Setting/Control GETSTATUS must follow TOUCHPAD_ON: {pubs:?}"
    );
}

#[tokio::test]
async fn fanboost_records_fan_getstatus_confirmation() {
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
    let command = pubs
        .iter()
        .position(|(topic, payload)| topic == "Fan/Control" && payload["Action"] == "FAN_BOOST_ON");
    let getstatus = pubs
        .iter()
        .position(|(topic, payload)| topic == "Fan/Control" && payload["Action"] == "GETSTATUS");
    assert!(
        matches!((command, getstatus), (Some(cmd), Some(confirm)) if confirm > cmd),
        "Fan/Control GETSTATUS must follow FAN_BOOST_ON: {pubs:?}"
    );
}

static SCREEN_BLANK_SEAM: Mutex<()> = Mutex::new(());

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum BlankStep {
    Write(u8),
    Exec(u32),
}

struct ScreenBlankSpy {
    current: Mutex<Option<u8>>,
    steps: Mutex<Vec<BlankStep>>,
}

impl ScreenBlankSpy {
    fn unreadable() -> Arc<Self> {
        Arc::new(Self {
            current: Mutex::new(None),
            steps: Mutex::new(Vec::new()),
        })
    }

    fn readable(brightness: u8) -> Arc<Self> {
        Arc::new(Self {
            current: Mutex::new(Some(brightness)),
            steps: Mutex::new(Vec::new()),
        })
    }

    fn steps(&self) -> Vec<BlankStep> {
        self.steps.lock().expect("steps").clone()
    }
}

impl ScreenBlankWmi for ScreenBlankSpy {
    fn get_brightness(&self) -> Option<u8> {
        *self.current.lock().expect("current")
    }

    fn set_brightness(&self, brightness: u8) -> Result<(), HostError> {
        self.steps
            .lock()
            .expect("steps")
            .push(BlankStep::Write(brightness));
        *self.current.lock().expect("current") = Some(brightness);
        Ok(())
    }
}

impl ScreenBlankExec for ScreenBlankSpy {
    fn apply(&self, flags: u32) {
        self.steps
            .lock()
            .expect("steps")
            .push(BlankStep::Exec(flags));
    }
}

#[test]
fn real_screen_blank_read_failure_is_error_and_writes_nothing() {
    // Given: Real arm + WMI spy that cannot read CurrentBrightness
    let _seam = SCREEN_BLANK_SEAM.lock().expect("screen blank seam");
    let spy = ScreenBlankSpy::unreadable();
    let _guard = inject(
        Arc::clone(&spy) as Arc<dyn ScreenBlankWmi>,
        Arc::clone(&spy) as Arc<dyn ScreenBlankExec>,
    );
    let mut backend = Backend::real();

    // When: 息屏 is requested
    let err = backend
        .set_monitor_off()
        .expect_err("WMI read failure must not claim success");

    // Then: error to the UI, no brightness write, no execution-state hold
    assert!(
        matches!(
            err,
            HostError::DisplayDenied(ref msg) if msg.contains("brightness read failed")
        ),
        "expected DisplayDenied brightness read failed, got {err}"
    );
    assert!(
        spy.steps().is_empty(),
        "read failure must not write brightness or execution state: {:?}",
        spy.steps()
    );
}

#[test]
fn real_screen_blank_writes_brightness_0_then_execution_state() {
    // Given: Real arm + readable WMI at 40% and an execution-state spy
    let _seam = SCREEN_BLANK_SEAM.lock().expect("screen blank seam");
    let spy = ScreenBlankSpy::readable(40);
    let _guard = inject(
        Arc::clone(&spy) as Arc<dyn ScreenBlankWmi>,
        Arc::clone(&spy) as Arc<dyn ScreenBlankExec>,
    );
    let mut backend = Backend::real();

    // When: 息屏 is requested
    backend.set_monitor_off().expect("readable WMI must dim");

    // Then: brightness 0 first, then the C# blank execution-state flags
    assert_eq!(
        spy.steps(),
        [BlankStep::Write(0), BlankStep::Exec(BLANK_EXECUTION_STATE),]
    );
}

#[tokio::test]
async fn real_screen_blank_restore_restores_previous_brightness() {
    // Given: Real arm that has already dimmed from 40%
    let _seam = SCREEN_BLANK_SEAM.lock().expect("screen blank seam");
    let spy = ScreenBlankSpy::readable(40);
    let _guard = inject(
        Arc::clone(&spy) as Arc<dyn ScreenBlankWmi>,
        Arc::clone(&spy) as Arc<dyn ScreenBlankExec>,
    );
    let mut backend = Backend::real();
    backend.set_monitor_off().expect("readable WMI must dim");

    // When: monitoroff is turned off (restore)
    backend
        .set_quick_switch("monitoroff", false)
        .await
        .expect("restore");

    // Then: previous brightness is written back and execution state is cleared
    assert_eq!(
        spy.steps(),
        [
            BlankStep::Write(0),
            BlankStep::Exec(BLANK_EXECUTION_STATE),
            BlankStep::Write(40),
            BlankStep::Exec(ES_CONTINUOUS),
        ]
    );
}
