//! Display CORE. Wave 2: `Backend::set_display_hz` wraps `apply_display_hz` after `start()`.

use std::time::Duration;

use app_lib::hw_display::{
    apply_brightness, apply_calibration, apply_display_hz, BrightnessQueue,
};
use app_lib::hw_wmi::FakeWmi;
use app_lib::Backend;
use gcu_mqtt::fake::{FakeBroker, Recorded};
use gcu_mqtt::handshake::run_handshake;

fn recorded_publishes(broker: &FakeBroker) -> Vec<(String, serde_json::Value)> {
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

fn write_actions(broker: &FakeBroker) -> Vec<String> {
    recorded_publishes(broker)
        .into_iter()
        .filter_map(|(_, payload)| {
            let action = payload.get("Action")?.as_str()?;
            if action == "GETSTATUS" {
                return None;
            }
            Some(action.to_owned())
        })
        .collect()
}

#[tokio::test]
async fn set_display_hz_publishes_gpu_hzsetting_with_hz_string() {
    let mut broker = FakeBroker::new();
    run_handshake(&mut broker).await.expect("start handshake");
    apply_display_hz(&mut broker, 165)
        .await
        .expect("apply GPU_HZSETTING");
    let publishes = recorded_publishes(&broker);
    let hz = publishes.iter().find(|(topic, payload)| {
        topic == "Setting/Control" && payload["Action"] == "GPU_HZSETTING"
    });
    let (_, payload) = hz.expect("Setting/Control GPU_HZSETTING missing");
    assert!(
        payload["Hz"].is_string(),
        "Hz must be a JSON string, got {payload}"
    );
    assert_eq!(payload["Hz"], "165");
    let golden_path = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("crates/_golden/mqtt/gpu_hz_165.json");
    let golden = std::fs::read_to_string(&golden_path)
        .unwrap_or_else(|err| panic!("{}: {err}", golden_path.display()));
    let expected: serde_json::Value = serde_json::from_str(&golden).expect("golden json");
    assert_eq!(payload, &expected);
}

#[tokio::test]
async fn set_display_hz_never_publishes_display_mode_or_nv_ctrl_panel() {
    let mut broker = FakeBroker::new();
    run_handshake(&mut broker).await.expect("start handshake");
    apply_display_hz(&mut broker, 165)
        .await
        .expect("apply GPU_HZSETTING");
    let actions = write_actions(&broker);
    assert!(
        actions.iter().all(|action| !action.contains("DISPLAY_")),
        "zero Action containing DISPLAY_, got {actions:?}"
    );
    assert!(
        actions.iter().all(|action| !action.contains("NV_CTRL_PANEL")),
        "zero NV_CTRL_PANEL Action, got {actions:?}"
    );
}

#[tokio::test]
async fn brightness_queue_latest_wins_on_fake_wmi() {
    let mut broker = FakeBroker::new();
    let mut wmi = FakeWmi::new();
    let mut queue = BrightnessQueue::new(Duration::ZERO);
    queue.submit(40);
    queue.submit(55);
    queue.submit(70);
    apply_brightness(&mut broker, &mut wmi, &mut queue)
        .await
        .expect("WMI brightness");
    let writes = wmi.writes();
    assert_eq!(
        writes.len(),
        1,
        "latest-wins must write once, got {writes:?}"
    );
    assert_eq!(writes[0].brightness, 70);
}

#[tokio::test]
async fn brightness_never_publishes_setscreenbrightness() {
    let mut broker = FakeBroker::new();
    run_handshake(&mut broker).await.expect("start handshake");
    let mut wmi = FakeWmi::new();
    let mut queue = BrightnessQueue::new(Duration::ZERO);
    queue.submit(70);
    apply_brightness(&mut broker, &mut wmi, &mut queue)
        .await
        .expect("WMI brightness");
    let actions = write_actions(&broker);
    assert!(
        actions
            .iter()
            .all(|action| !action.contains("SETSCREENBRIGHTNESS")),
        "brightness must not publish SETSCREENBRIGHTNESS, got {actions:?}"
    );
}

#[tokio::test]
async fn hdr_on_blocks_color_calibration_without_publish() {
    let mut broker = FakeBroker::new();
    run_handshake(&mut broker).await.expect("start handshake");
    apply_calibration(&mut broker, "COLOR_CALIBRATION_ON_SRGB", true)
        .await
        .expect_err("HDR on must deny calibration");
    let actions = write_actions(&broker);
    assert!(
        actions
            .iter()
            .all(|action| !action.contains("COLOR_CALIBRATION")),
        "HDR block must not publish COLOR_CALIBRATION_*, got {actions:?}"
    );
}

#[tokio::test]
async fn backend_set_display_hz_publishes_hz_string_after_start() {
    let mut backend = Backend::fake_from_json("{}").expect("parse");
    backend.start().await.expect("start");
    backend
        .set_display_hz("165")
        .await
        .expect("Backend::set_display_hz");
    let publishes = backend.recorded_publishes();
    let hz = publishes.iter().find(|(topic, payload)| {
        topic == "Setting/Control" && payload["Action"] == "GPU_HZSETTING"
    });
    let (_, payload) = hz.expect("Setting/Control GPU_HZSETTING missing");
    assert!(
        payload["Hz"].is_string(),
        "Hz must be a JSON string, got {payload}"
    );
    assert_eq!(payload["Hz"], "165");
    assert!(
        publishes.iter().all(|(topic, payload)| {
            payload.get("Action").and_then(|value| value.as_str()) != Some("GETSTATUS")
                || topic != "Setting/Control"
                || payload.get("Hz").is_none()
        }),
        "write Action must not be handshake GETSTATUS: {publishes:?}"
    );
}
