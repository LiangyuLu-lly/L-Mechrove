//! Liquid cooling BT_LC MQTT. PumpCtrl/FanCtrl are JSON strings. Fail-closed on LiquidCoolingSupport.

use app_lib::hw_ble::FakeBle;
use app_lib::hw_lc::{apply_lc_fan, apply_lc_pump};
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
        assert_eq!(writes.len(), 1, "one LC_FanCtrl for {index}, got {writes:?}");
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
    backend
        .set_lc_pump(1)
        .await
        .expect("Backend::set_lc_pump");
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
