//! Fan curve write. SET_FAN_SPEED_CURVE_SETTING T0–T15 are JSON strings.

use app_lib::hw_fan::{apply_fan_curve, normalize_fan_curve, FanCurveType};
use app_lib::Backend;
use gcu_mqtt::fake::{FakeBroker, Recorded};
use gcu_mqtt::topics::FAN_CONTROL;
use serde_json::Value;

const NAME: &str = "curve";
const DUTIES: [u8; 16] = [
    0, 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 100, 100, 100, 100,
];
/// C# `NormalizeFanCurve` with Gaming default UpT sentinel at index 11
/// (`MechrevoHw.cs:2712`, `DefaultCurve_Gaming.json` CPU/GPU UpT[11]=255).
/// T11..T15 are filled with T10, not the user-drawn 100s.
const CSHARP_GAMING_TRAILING: [&str; 16] = [
    "0", "0", "10", "20", "30", "40", "50", "60", "70", "80", "90", "90", "90", "90", "90", "90",
];
/// C# `MechrevoHardwareTests.NormalizeFanCurve_ClampsAndEnforcesMonotonicCurve`
/// recast onto the Gaming 11-point table: dip at T3 stays 55, 200 clamps to 100.
const CSHARP_MONOTONIC_CLAMP: [u8; 16] =
    [0, 10, 55, 40, 200, 20, 60, 70, 80, 90, 95, 1, 2, 3, 4, 5];
const CSHARP_MONOTONIC_CLAMP_T: [&str; 16] = [
    "0", "10", "55", "55", "100", "100", "100", "100", "100", "100", "100", "100", "100", "100",
    "100", "100",
];
/// C# `NormalizeFanCurve_AllowsLastEffectiveTemperatureBelowFullDuty` on 11-point table.
const CSHARP_LAST_EFFECTIVE: [u8; 16] = [
    20, 30, 40, 50, 60, 65, 70, 80, 85, 90, 92, 100, 100, 100, 100, 100,
];
const CSHARP_LAST_EFFECTIVE_T: [&str; 16] = [
    "20", "30", "40", "50", "60", "65", "70", "80", "85", "90", "92", "92", "92", "92", "92", "92",
];

fn t0_t15_strings(payload: &Value) -> [String; 16] {
    core::array::from_fn(|i| {
        let key = format!("T{i}");
        payload[&key]
            .as_str()
            .unwrap_or_else(|| panic!("{key} must be a JSON string: {payload:?}"))
            .to_owned()
    })
}

fn assert_published_t0_t15(payload: &Value, expected: [&str; 16]) {
    let actual = t0_t15_strings(payload);
    for i in 0..16 {
        assert_eq!(
            actual[i], expected[i],
            "T{i}: published {} != C# {}",
            actual[i], expected[i]
        );
    }
}

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
async fn published_cpu_t0_t15_match_csharp_gaming_table_trailing_fill() {
    let mut broker = FakeBroker::new();
    apply_fan_curve(&mut broker, NAME, FanCurveType::Cpu, DUTIES)
        .await
        .expect("CPU curve publish");
    let payload = set_curve_payload(&broker);
    assert_eq!(payload["Type"], "CPU");
    assert_published_t0_t15(&payload, CSHARP_GAMING_TRAILING);
}

#[tokio::test]
async fn published_gpu_t0_t15_match_csharp_gaming_table_trailing_fill() {
    let mut broker = FakeBroker::new();
    apply_fan_curve(&mut broker, NAME, FanCurveType::Gpu, DUTIES)
        .await
        .expect("GPU curve publish");
    let payload = set_curve_payload(&broker);
    assert_eq!(payload["Type"], "GPU");
    assert_published_t0_t15(&payload, CSHARP_GAMING_TRAILING);
}

#[tokio::test]
async fn published_t0_t15_match_csharp_monotonic_step_and_clamp() {
    let mut broker = FakeBroker::new();
    apply_fan_curve(&mut broker, NAME, FanCurveType::Cpu, CSHARP_MONOTONIC_CLAMP)
        .await
        .expect("CPU curve publish");
    assert_published_t0_t15(&set_curve_payload(&broker), CSHARP_MONOTONIC_CLAMP_T);
}

#[tokio::test]
async fn published_t0_t15_match_csharp_last_effective_below_full_duty() {
    let mut broker = FakeBroker::new();
    apply_fan_curve(&mut broker, NAME, FanCurveType::Cpu, CSHARP_LAST_EFFECTIVE)
        .await
        .expect("CPU curve publish");
    assert_published_t0_t15(&set_curve_payload(&broker), CSHARP_LAST_EFFECTIVE_T);
}

#[test]
fn normalize_matches_csharp_sentinel_8_monotonic_clamp() {
    let temperatures = [
        30, 40, 50, 60, 70, 80, 90, 100, 255, 255, 255, 255, 255, 255, 255, 255,
    ];
    let duties = [0, 10, 55, 40, 200, 20, 60, 70, 80, 90, 95, 1, 2, 3, 4, 5];
    let actual = normalize_fan_curve(duties, temperatures);
    let expected: [u8; 16] = [
        0, 10, 55, 55, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100,
    ];
    assert_eq!(actual, expected);
}

#[test]
fn normalize_matches_csharp_sentinel_8_last_effective_below_100() {
    let temperatures = [
        30, 40, 50, 60, 65, 70, 75, 81, 255, 255, 255, 255, 255, 255, 255, 255,
    ];
    let mut duties = [0_u8; 16];
    duties[..8].copy_from_slice(&[20, 30, 40, 50, 60, 65, 70, 80]);
    let actual = normalize_fan_curve(duties, temperatures);
    assert_eq!(actual[7], 80);
    assert!(actual[8..].iter().all(|&d| d == 80));
}

#[test]
fn gpu_table_earlier_sentinel_fills_sooner_than_cpu() {
    let duties = [
        10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 100, 100, 100, 100, 100, 100,
    ];
    let cpu_up_t = [
        0, 46, 51, 54, 57, 60, 63, 66, 68, 70, 72, 255, 255, 255, 255, 255,
    ];
    let gpu_up_t = [
        0, 45, 48, 51, 53, 55, 57, 59, 255, 255, 255, 255, 255, 255, 255, 255,
    ];
    let cpu = normalize_fan_curve(duties, cpu_up_t);
    let gpu = normalize_fan_curve(duties, gpu_up_t);
    assert_eq!(
        cpu,
        [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 100, 100, 100, 100, 100, 100]
    );
    assert_eq!(
        gpu,
        [10, 20, 30, 40, 50, 60, 70, 80, 80, 80, 80, 80, 80, 80, 80, 80]
    );
}

#[test]
fn gpu_earlier_sentinel_t0_t15_strings_match_csharp() {
    let duties = [
        10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 100, 100, 100, 100, 100, 100,
    ];
    let gpu_up_t = [
        0, 45, 48, 51, 53, 55, 57, 59, 255, 255, 255, 255, 255, 255, 255, 255,
    ];
    let actual = normalize_fan_curve(duties, gpu_up_t);
    let expected = [
        "10", "20", "30", "40", "50", "60", "70", "80", "80", "80", "80", "80", "80", "80", "80",
        "80",
    ];
    for i in 0..16 {
        assert_eq!(actual[i].to_string(), expected[i]);
    }
}

#[test]
fn normalize_matches_csharp_zero_duty_at_48c() {
    let temperatures = [
        0, 48, 52, 56, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
    ];
    let mut duties = [0_u8; 16];
    duties[..4].copy_from_slice(&[0, 0, 35, 40]);
    let actual = normalize_fan_curve(duties, temperatures);
    assert_eq!(actual[1], 0);
    assert_eq!(actual[2], 35);
    assert_eq!(actual[3], 40);
    assert!(actual[4..].iter().all(|&d| d == 40));
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
        publishes
            .iter()
            .any(|(topic, value)| { topic == FAN_CONTROL && value["Action"] == "FAN_BOOST_ON" }),
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
