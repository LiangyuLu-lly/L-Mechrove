//! Liquid cooling BT_LC MQTT. PumpCtrl/FanCtrl are JSON strings. Fail-closed on LiquidCoolingSupport.

use app_lib::hw_ble::FakeBle;
use app_lib::hw_lc::{
    apply_lc_clear_mac, apply_lc_connect, apply_lc_device_mac, apply_lc_disconnect, apply_lc_fan,
    apply_lc_input_water, apply_lc_pump, resolve_route, should_restore_saved_gcu_lighting,
    should_retry_gcu_connection, should_skip_direct_fallback_because_gcu_holds_radio,
    should_use_automatic_direct_fallback, LcControlRoute,
};
use app_lib::Backend;
use capabilities::ItemSupport;
use gcu_mqtt::fake::{FakeBroker, Recorded};
use gcu_mqtt::handshake::run_handshake;
use gcu_mqtt::topics::BT_LC_CONTROL;
use serde_json::Value;

fn publish_json(item: &Recorded) -> Option<(String, Value)> {
    match item {
        Recorded::Publish { topic, payload } => {
            let value = serde_json::from_slice(payload).ok()?;
            Some((topic.clone(), value))
        }
        Recorded::Subscribe(_) => None,
    }
}

fn lc_action_writes(broker: &FakeBroker, action: &str) -> Vec<Value> {
    broker
        .recorded()
        .iter()
        .filter_map(publish_json)
        .filter(|(topic, payload)| topic == BT_LC_CONTROL && payload["Action"] == action)
        .map(|(_, payload)| payload)
        .collect()
}

#[tokio::test]
async fn liquid_cooling_support_false_no_bt_lc_write() {
    for json in ["{}", r#"{"LiquidCoolingSupport":0}"#] {
        let item_support = ItemSupport::parse_json(json).expect("parse");
        let mut broker = FakeBroker::new();
        run_handshake(&mut broker).await.expect("handshake");
        let mut ble = FakeBle::new();
        apply_lc_pump(&mut broker, &mut ble, &item_support, true, 1)
            .await
            .expect_err("LiquidCoolingSupport false must deny write");
        assert!(
            lc_action_writes(&broker, "LC_PumpCtrl").is_empty(),
            "must not publish LC_PumpCtrl (GETSTATUS handshake ignored): {:?}",
            broker.recorded()
        );
        assert!(
            ble.writes().is_empty(),
            "must not write BLE when support is false: {:?}",
            ble.writes()
        );
    }
}

#[tokio::test]
async fn pumpctrl_is_string() {
    let item_support = ItemSupport::parse_json(r#"{"LiquidCoolingSupport":1}"#).expect("parse");
    let mut broker = FakeBroker::new();
    let mut ble = FakeBle::new();
    apply_lc_pump(&mut broker, &mut ble, &item_support, true, 1)
        .await
        .expect("LiquidCoolingSupport 1 must allow pump write");
    let writes = lc_action_writes(&broker, "LC_PumpCtrl");
    assert_eq!(writes.len(), 1, "one LC_PumpCtrl, got {writes:?}");
    assert!(
        writes[0]["PumpCtrl"].is_string(),
        "PumpCtrl must be a JSON string: {}",
        writes[0]
    );
    assert_eq!(writes[0]["PumpCtrl"], "1");
    assert!(
        ble.writes().is_empty(),
        "MQTT connected must not fall back to BLE: {:?}",
        ble.writes()
    );
}

#[tokio::test]
async fn fanctrl_is_string() {
    let item_support = ItemSupport::parse_json(r#"{"LiquidCoolingSupport":1}"#).expect("parse");
    for index in 0..=4_u8 {
        let mut broker = FakeBroker::new();
        let mut ble = FakeBle::new();
        apply_lc_fan(&mut broker, &mut ble, &item_support, true, index)
            .await
            .unwrap_or_else(|err| panic!("FanCtrl {index} must publish, got {err}"));
        let writes = lc_action_writes(&broker, "LC_FanCtrl");
        assert_eq!(
            writes.len(),
            1,
            "one LC_FanCtrl for {index}, got {writes:?}"
        );
        assert!(
            writes[0]["FanCtrl"].is_string(),
            "FanCtrl must be a JSON string: {}",
            writes[0]
        );
        assert_eq!(writes[0]["FanCtrl"], index.to_string());
    }
}

#[tokio::test]
async fn ble_records_when_capability_true_and_mqtt_lc_disconnected() {
    let item_support = ItemSupport::parse_json(r#"{"LiquidCoolingSupport":1}"#).expect("parse");
    let mut broker = FakeBroker::new();
    let mut ble = FakeBle::new();
    apply_lc_pump(&mut broker, &mut ble, &item_support, false, 1)
        .await
        .expect("BLE fallback when support true and MQTT LC disconnected");
    assert!(
        lc_action_writes(&broker, "LC_PumpCtrl").is_empty(),
        "disconnected MQTT must not publish LC_PumpCtrl: {:?}",
        broker.recorded()
    );
    assert!(
        !ble.writes().is_empty(),
        "FakeBle must record a write when MQTT LC is disconnected"
    );
}

#[tokio::test]
async fn backend_set_lc_pump_after_start() {
    let mut backend = Backend::fake_from_json(r#"{"LiquidCoolingSupport":1}"#).expect("parse");
    backend.start().await.expect("start");
    backend.set_lc_pump(1).await.expect("Backend::set_lc_pump");
    let publishes = backend.recorded_publishes();
    let pump = publishes.iter().find(|(topic, payload)| {
        topic == BT_LC_CONTROL
            && payload["Action"] == "LC_PumpCtrl"
            && payload["Action"] != "GETSTATUS"
    });
    let (_, payload) = pump.expect("BT_LC LC_PumpCtrl missing");
    assert!(
        payload["PumpCtrl"].is_string(),
        "PumpCtrl must be a JSON string: {payload}"
    );
    assert_eq!(payload["PumpCtrl"], "1");
}

fn support_on() -> ItemSupport {
    ItemSupport::parse_json(r#"{"LiquidCoolingSupport":1}"#).expect("parse")
}

fn support_off() -> ItemSupport {
    ItemSupport::parse_json(r#"{"LiquidCoolingSupport":0}"#).expect("parse")
}

#[tokio::test]
async fn connect_action_is_connect_with_no_extra_fields() {
    let mut broker = FakeBroker::new();
    apply_lc_connect(&mut broker, &support_off())
        .await
        .expect("Connect is runtime discovery and must not require LiquidCoolingSupport");
    let writes = lc_action_writes(&broker, "Connect");
    assert_eq!(writes.len(), 1, "one Connect, got {writes:?}");
    let object = writes[0]
        .as_object()
        .unwrap_or_else(|| panic!("Connect payload must be an object: {}", writes[0]));
    assert_eq!(
        object.get("Action").map(Value::as_str),
        Some(Some("Connect"))
    );
    assert_eq!(object.len(), 1, "Connect is Action-only: {}", writes[0]);
}

#[tokio::test]
async fn disconnect_action_is_disconnect_with_no_extra_fields() {
    let mut broker = FakeBroker::new();
    apply_lc_disconnect(&mut broker, &support_on())
        .await
        .expect("Disconnect must publish when support is true");
    let writes = lc_action_writes(&broker, "Disconnect");
    assert_eq!(writes.len(), 1, "one Disconnect, got {writes:?}");
    let object = writes[0]
        .as_object()
        .unwrap_or_else(|| panic!("Disconnect payload must be an object: {}", writes[0]));
    assert_eq!(
        object.get("Action").map(Value::as_str),
        Some(Some("Disconnect"))
    );
    assert_eq!(object.len(), 1, "Disconnect is Action-only: {}", writes[0]);
}

#[tokio::test]
async fn device_mac_setting_mac_is_string() {
    let mut broker = FakeBroker::new();
    let mac = "BluetoothLE#BluetoothLE00:11:22:33:44:55-00:11:22:33:44:55";
    apply_lc_device_mac(&mut broker, &support_on(), mac)
        .await
        .expect("DeviceMacSetting must publish when support is true");
    let writes = lc_action_writes(&broker, "DeviceMacSetting");
    assert_eq!(writes.len(), 1, "one DeviceMacSetting, got {writes:?}");
    assert!(
        writes[0]["DeviceMac"].is_string(),
        "DeviceMac must be a JSON string: {}",
        writes[0]
    );
    assert_eq!(writes[0]["DeviceMac"], mac);
}

#[tokio::test]
async fn clear_dev_mac_action_is_clear_dev_mac_with_no_extra_fields() {
    let mut broker = FakeBroker::new();
    apply_lc_clear_mac(&mut broker, &support_on())
        .await
        .expect("ClearDevMAC must publish when support is true");
    let writes = lc_action_writes(&broker, "ClearDevMAC");
    assert_eq!(writes.len(), 1, "one ClearDevMAC, got {writes:?}");
    let object = writes[0]
        .as_object()
        .unwrap_or_else(|| panic!("ClearDevMAC payload must be an object: {}", writes[0]));
    assert_eq!(
        object.get("Action").map(Value::as_str),
        Some(Some("ClearDevMAC"))
    );
    assert_eq!(object.len(), 1, "ClearDevMAC is Action-only: {}", writes[0]);
}

#[tokio::test]
async fn input_water_action_is_input_water_with_no_extra_fields() {
    let mut broker = FakeBroker::new();
    apply_lc_input_water(&mut broker, &support_on())
        .await
        .expect("InputWater must publish when support is true");
    let writes = lc_action_writes(&broker, "InputWater");
    assert_eq!(writes.len(), 1, "one InputWater, got {writes:?}");
    let object = writes[0]
        .as_object()
        .unwrap_or_else(|| panic!("InputWater payload must be an object: {}", writes[0]));
    assert_eq!(
        object.get("Action").map(Value::as_str),
        Some(Some("InputWater"))
    );
    assert_eq!(object.len(), 1, "InputWater is Action-only: {}", writes[0]);
}

#[tokio::test]
async fn disconnect_family_denied_when_support_false() {
    let item_support = support_off();
    let mut broker = FakeBroker::new();
    apply_lc_disconnect(&mut broker, &item_support)
        .await
        .expect_err("Disconnect must deny when support is false");
    apply_lc_clear_mac(&mut broker, &item_support)
        .await
        .expect_err("ClearDevMAC must deny when support is false");
    apply_lc_input_water(&mut broker, &item_support)
        .await
        .expect_err("InputWater must deny when support is false");
    apply_lc_device_mac(&mut broker, &item_support, "aa:bb")
        .await
        .expect_err("DeviceMacSetting must deny when support is false");
    assert!(
        lc_action_writes(&broker, "Disconnect").is_empty()
            && lc_action_writes(&broker, "ClearDevMAC").is_empty()
            && lc_action_writes(&broker, "InputWater").is_empty()
            && lc_action_writes(&broker, "DeviceMacSetting").is_empty(),
        "support false must not publish the connect family: {:?}",
        broker.recorded()
    );
}

#[test]
fn resolve_route_prefers_direct_ble_then_gcu_then_observed() {
    assert_eq!(resolve_route(true, true, true), LcControlRoute::DirectBle);
    assert_eq!(resolve_route(false, true, true), LcControlRoute::Gcu);
    assert_eq!(
        resolve_route(false, false, true),
        LcControlRoute::BluetoothObserved
    );
    assert_eq!(resolve_route(false, false, false), LcControlRoute::None);
}

#[test]
fn automatic_direct_fallback_only_when_not_already_direct() {
    assert!(should_use_automatic_direct_fallback(false));
    assert!(!should_use_automatic_direct_fallback(true));
}

#[test]
fn gcu_holds_radio_during_scan_connect_is_connectable() {
    assert!(should_skip_direct_fallback_because_gcu_holds_radio(
        "Scanning"
    ));
    assert!(should_skip_direct_fallback_because_gcu_holds_radio(
        "Connecting"
    ));
    assert!(should_skip_direct_fallback_because_gcu_holds_radio(
        "IsConnectable"
    ));
    assert!(!should_skip_direct_fallback_because_gcu_holds_radio(
        "Connected"
    ));
    assert!(!should_skip_direct_fallback_because_gcu_holds_radio(""));
}

#[test]
fn retry_gcu_connection_matches_csharp_policy() {
    assert!(should_retry_gcu_connection(
        true,
        true,
        LcControlRoute::None,
        false,
        false,
        0,
        8000,
    ));
    assert!(!should_retry_gcu_connection(
        true,
        true,
        LcControlRoute::Gcu,
        false,
        false,
        0,
        8000,
    ));
    assert!(!should_retry_gcu_connection(
        true,
        true,
        LcControlRoute::None,
        true,
        false,
        0,
        8000,
    ));
}

#[test]
fn restore_saved_gcu_lighting_requires_gcu_fresh_profile() {
    assert!(should_restore_saved_gcu_lighting(
        false,
        true,
        true,
        Some("cyan_static")
    ));
    assert!(!should_restore_saved_gcu_lighting(
        true,
        true,
        true,
        Some("cyan_static")
    ));
    assert!(!should_restore_saved_gcu_lighting(
        false,
        true,
        true,
        Some("")
    ));
    assert!(!should_restore_saved_gcu_lighting(false, true, true, None));
}
