//! Fan curve write. SET_FAN_SPEED_CURVE_SETTING T0–T15 are JSON strings.

use app_lib::hw_fan::{apply_fan_curve, FanCurveType};
use app_lib::Backend;
use gcu_mqtt::fake::{FakeBroker, Recorded};
use gcu_mqtt::topics::FAN_CONTROL;
use serde_json::Value;

const NAME: &str = "curve";
const DUTIES: [u8; 16] = [
    0, 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 100, 100, 100, 100,
];

fn set_curve_payload(broker: &FakeBroker) -> Value {
    broker
        .recorded()
        .iter()
        .find_map(|item| match item {
            Recorded::Publish { topic, payload } => {
                let value: Value = serde_json::from_slice(payload).ok()?;
                (topic.as_str() == FAN_CONTROL && value["Action"] == "SET_FAN_SPEED_CURVE_SETTING")
                    .then_some(value)
            }
            Recorded::Subscribe(_) => None,
        })
        .expect("Fan/Control SET_FAN_SPEED_CURVE_SETTING publish")
}

#[tokio::test]
async fn set_fan_curve_cpu_publishes_t0_t15_strings() {
    let mut broker = FakeBroker::new();
    apply_fan_curve(&mut broker, NAME, FanCurveType::Cpu, DUTIES)
        .await
        .expect("CPU curve publish");
    let payload = set_curve_payload(&broker);
    assert_eq!(payload["Action"], "SET_FAN_SPEED_CURVE_SETTING");
    assert_eq!(payload["Type"], "CPU");
    for i in 0..16 {
        let key = format!("T{i}");
        assert!(
            payload[&key].is_string(),
            "{key} must be a JSON string: {payload:?}"
        );
    }
}

#[tokio::test]
async fn t_values_are_strings_not_numbers() {
    let mut broker = FakeBroker::new();
    apply_fan_curve(&mut broker, NAME, FanCurveType::Cpu, DUTIES)
        .await
        .expect("CPU curve publish");
    let payload = set_curve_payload(&broker);
    for i in 0..16 {
        let key = format!("T{i}");
        assert!(
            payload[&key].is_string(),
            "{key} must be a JSON string: {payload:?}"
        );
        assert!(
            !payload[&key].is_number(),
            "{key} must not be a JSON number: {payload:?}"
        );
    }
}

#[tokio::test]
async fn backend_set_fan_curve_t0_t15_are_strings_after_start() {
    let mut backend = Backend::fake_from_json("{}").expect("parse");
    backend.start().await.expect("start");
    backend
        .set_fan_curve(NAME, "CPU", DUTIES.to_vec())
        .await
        .expect("Backend::set_fan_curve");
    let publishes = backend.recorded_publishes();
    let payload = publishes
        .iter()
        .find(|(topic, value)| {
            topic == FAN_CONTROL
                && value["Action"] == "SET_FAN_SPEED_CURVE_SETTING"
                && value["Action"] != "GETSTATUS"
        })
        .map(|(_, value)| value)
        .expect("Fan/Control SET_FAN_SPEED_CURVE_SETTING publish");
    for i in 0..16 {
        let key = format!("T{i}");
        assert!(
            payload[&key].is_string(),
            "{key} must be a JSON string: {payload:?}"
        );
    }
}

#[tokio::test]
async fn set_fan_boost_on_publishes_fan_boost_on() {
    let mut backend = Backend::fake_from_json("{}").expect("parse");
    backend.start().await.expect("start");
    backend
        .set_fan_boost(true)
        .await
        .expect("Backend::set_fan_boost");
    let publishes = backend.recorded_publishes();
    assert!(
        publishes.iter().any(|(topic, value)| {
            topic == FAN_CONTROL && value["Action"] == "FAN_BOOST_ON"
        }),
        "Fan/Control FAN_BOOST_ON missing: {publishes:?}"
    );
}

#[tokio::test]
async fn set_custom_detail_pl1_value_is_string() {
    let mut backend = Backend::fake_from_json("{}").expect("parse");
    backend.start().await.expect("start");
    backend
        .set_custom_detail("PL1", "45")
        .await
        .expect("Backend::set_custom_detail");
    let publishes = backend.recorded_publishes();
    let payload = publishes
        .iter()
        .find(|(topic, value)| {
            topic == FAN_CONTROL && value["Action"] == "SET_OPERATING_MODE_DETAIL"
        })
        .map(|(_, value)| value)
        .expect("SET_OPERATING_MODE_DETAIL");
    assert!(
        payload["PL1"].is_string(),
        "PL1 must be a JSON string: {payload:?}"
    );
    assert_eq!(payload["PL1"], "45");
}
