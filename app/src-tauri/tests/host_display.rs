//! Display CORE. Wave 2: `Backend::set_display_hz` wraps `apply_display_hz` after `start()`.

use std::sync::{Arc, Mutex};
use std::thread::ThreadId;
use std::time::Duration;

use app_lib::hw_display::{
    apply_auto_refresh_rate, apply_brightness, apply_calibration, apply_direct_connect_restart,
    apply_display_hz, apply_local_dimming, apply_overdrive, color_calibration_file_name,
    hdr_acm_from_active_color_mode, hdr_acm_from_legacy_color_info, is_advanced_color_enabled,
    AdvancedColorProbe, AdvancedColorQuery, BrightnessQueue, BrightnessSink,
};
use app_lib::hw_wmi::FakeWmi;
use app_lib::{Backend, HostError};
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
        actions
            .iter()
            .all(|action| !action.contains("NV_CTRL_PANEL")),
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
    apply_calibration(
        &mut broker,
        "COLOR_CALIBRATION_ON_SRGB",
        true,
        color_calibration_file_name(2),
    )
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
async fn real_inbound_does_not_invent_mqtt_hdr_and_calibration_is_fail_closed() {
    // Given: C# HDR is ScreenCCD.GetHDRStatus (ScreenCCD.cs:11) via
    // MechrevoService.GetAdvancedColorState/IsHdrEnabled (MechrevoService.cs:956-970),
    // wired as _readHdrEnabled = IsAdvancedColorEnabled (MechrevoService.cs:118).
    // There is no MQTT topic or field. An inbound Setting/Status must not
    // silently pass the S4-style guard on the Real path.
    let _seam = CCD_SEAM.lock().expect("ccd seam");
    let mut backend = Backend::real();
    backend.apply_inbound(
        "Setting/Status",
        br#"{"WinKey":"WINKEY_LOCK","ColorCalibrationMode":2}"#,
    );

    // When: calibration is requested with HDR never observed from CCD
    let err = backend
        .set_calibration("COLOR_CALIBRATION_ON_SRGB")
        .await
        .expect_err("unobserved HDR must refuse calibration");

    // Then: fail-closed with the unavailable reason, no silent pass, no invented MQTT field
    assert!(
        matches!(
            err,
            HostError::DisplayDenied(ref msg)
                if msg.contains("COLOR_CALIBRATION_ON_SRGB") && msg.contains("unavailable")
        ),
        "expected DisplayDenied with unavailable reason, got {err}"
    );
}

#[tokio::test]
async fn set_calibration_off_sends_file_name_of_current_mode() {
    // Given: Fake backend after handshake. C# SetColorCalibration(off) sends
    // ColorCalibrationFileName(currentMode) (MechrevoService.cs:778-779, 986-992).
    let mut backend = Backend::fake_from_json("{}").expect("parse");
    backend.start().await.expect("start");

    // When: sRGB is selected, then calibration is turned off
    backend
        .set_calibration("COLOR_CALIBRATION_ON_SRGB")
        .await
        .expect("sRGB");
    backend
        .set_calibration("COLOR_CALIBRATION_OFF")
        .await
        .expect("off after sRGB");

    // Then: FileName is the C# sRGB literal
    let publishes = backend.recorded_publishes();
    let off = publishes.iter().rev().find(|(topic, payload)| {
        topic == "Setting/Control" && payload["Action"] == "COLOR_CALIBRATION_OFF"
    });
    let (_, payload) = off.expect("COLOR_CALIBRATION_OFF after sRGB missing");
    assert_eq!(payload["FileName"], "sRGB");

    // When: returning to the default, then off
    backend
        .set_calibration("COLOR_CALIBRATION_ON_DEFAULT")
        .await
        .expect("default");
    backend
        .set_calibration("COLOR_CALIBRATION_OFF")
        .await
        .expect("off after default");

    // Then: FileName is the C# Default literal
    let publishes = backend.recorded_publishes();
    let off = publishes.iter().rev().find(|(topic, payload)| {
        topic == "Setting/Control" && payload["Action"] == "COLOR_CALIBRATION_OFF"
    });
    let (_, payload) = off.expect("COLOR_CALIBRATION_OFF after default missing");
    assert_eq!(payload["FileName"], "Default");
}

#[tokio::test]
async fn set_calibration_off_uses_inbound_color_calibration_mode() {
    // Given: Setting/Status reports the current mode the way C# FirstField does
    // (MechrevoHw.cs:1938-1944).
    let mut backend = Backend::fake_from_json("{}").expect("parse");
    backend.start().await.expect("start");
    backend.apply_inbound("Setting/Status", br#"{"ColorCalibrationMode":2}"#);

    // When: calibration is turned off
    backend
        .set_calibration("COLOR_CALIBRATION_OFF")
        .await
        .expect("off from inbound sRGB");

    // Then: FileName matches ColorCalibrationFileName(2) == "sRGB"
    let publishes = backend.recorded_publishes();
    let off = publishes.iter().find(|(topic, payload)| {
        topic == "Setting/Control" && payload["Action"] == "COLOR_CALIBRATION_OFF"
    });
    let (_, payload) = off.expect("COLOR_CALIBRATION_OFF missing");
    assert_eq!(payload["FileName"], "sRGB");
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

#[tokio::test]
async fn auto_refresh_rate_on_publishes_gpu_dc_hz_enable_bool() {
    let mut broker = FakeBroker::new();
    apply_auto_refresh_rate(&mut broker, true, true)
        .await
        .expect("DcHzSeen");
    let publishes = recorded_publishes(&broker);
    let found = publishes
        .iter()
        .find(|(topic, payload)| topic == "Setting/Control" && payload["Action"] == "GPU_DC_HZ");
    let (_, payload) = found.expect("Setting/Control GPU_DC_HZ missing");
    assert!(
        payload["Enable"].is_boolean(),
        "Enable must be JSON bool, got {payload}"
    );
    assert_eq!(payload["Enable"], true);
    let golden_path = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("crates/_golden/mqtt/gpu_dc_hz_on.json");
    let golden = std::fs::read_to_string(&golden_path)
        .unwrap_or_else(|err| panic!("{}: {err}", golden_path.display()));
    let expected: serde_json::Value = serde_json::from_str(&golden).expect("golden json");
    assert_eq!(payload, &expected);
}

#[tokio::test]
async fn auto_refresh_rate_denied_when_dc_hz_seen_false() {
    let mut broker = FakeBroker::new();
    apply_auto_refresh_rate(&mut broker, false, true)
        .await
        .expect_err("fail-closed without DcHzSeen");
    let actions = write_actions(&broker);
    assert!(
        actions.iter().all(|action| action != "GPU_DC_HZ"),
        "must not publish GPU_DC_HZ when DcHzSeen is false: {actions:?}"
    );
}

#[tokio::test]
async fn backend_set_auto_refresh_rate_requires_dc_hz_seen() {
    let mut backend = Backend::fake_from_json("{}").expect("parse");
    backend.start().await.expect("start");
    backend
        .set_auto_refresh_rate(true)
        .await
        .expect_err("default Fake has no DcHzSeen");
    let mut backend = Backend::fake_from_json("{}")
        .expect("parse")
        .with_dc_hz_seen(true);
    backend.start().await.expect("start");
    backend.set_auto_refresh_rate(true).await.expect("DcHzSeen");
    let publishes = backend.recorded_publishes();
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "Setting/Control"
                && payload["Action"] == "GPU_DC_HZ"
                && payload["Enable"] == true
        }),
        "GPU_DC_HZ Enable true missing: {publishes:?}"
    );
}

#[test]
fn color_calibration_file_name_matches_csharp() {
    assert_eq!(color_calibration_file_name(2), "sRGB");
    assert_eq!(color_calibration_file_name(3), "P3");
    assert_eq!(color_calibration_file_name(4), "AdobeRGB");
    assert_eq!(color_calibration_file_name(1), "Default");
    assert_eq!(color_calibration_file_name(0), "Default");
}

#[tokio::test]
async fn color_calibration_off_golden_includes_filename_string() {
    let mut broker = FakeBroker::new();
    apply_calibration(
        &mut broker,
        "COLOR_CALIBRATION_OFF",
        false,
        color_calibration_file_name(2),
    )
    .await
    .expect("COLOR_CALIBRATION_OFF");
    let publishes = recorded_publishes(&broker);
    let found = publishes.iter().find(|(topic, payload)| {
        topic == "Setting/Control" && payload["Action"] == "COLOR_CALIBRATION_OFF"
    });
    let (_, payload) = found.expect("Setting/Control COLOR_CALIBRATION_OFF missing");
    assert!(
        payload["FileName"].is_string(),
        "FileName must be a JSON string, got {payload}"
    );
    let expected = serde_json::json!({
        "Action": "COLOR_CALIBRATION_OFF",
        "FileName": "sRGB",
    });
    assert_eq!(payload, &expected);
}

#[tokio::test]
async fn auto_refresh_rate_publishes_only_when_live_dc_hz_seen() {
    let mut backend = Backend::fake_from_json("{}").expect("parse");
    backend.start().await.expect("start");
    backend.apply_inbound("GPUDevice/Status", br#"{"currentHZList":["60"]}"#);
    backend
        .set_auto_refresh_rate(true)
        .await
        .expect_err("refuse when live dc_hz_seen is absent");
    let denied = backend.recorded_publishes();
    assert!(
        denied.iter().all(|(topic, payload)| {
            topic != "Setting/Control" || payload["Action"] != "GPU_DC_HZ"
        }),
        "must not publish GPU_DC_HZ when live dc_hz_seen is absent: {denied:?}"
    );

    backend.apply_inbound(
        "GPUDevice/Status",
        br#"{"currentHZList":["60"],"DC_HZ":true}"#,
    );
    backend
        .set_auto_refresh_rate(true)
        .await
        .expect("live dc_hz_seen");
    let publishes = backend.recorded_publishes();
    let found = publishes
        .iter()
        .find(|(topic, payload)| topic == "Setting/Control" && payload["Action"] == "GPU_DC_HZ");
    let (_, payload) = found.expect("Setting/Control GPU_DC_HZ missing");
    let expected = serde_json::json!({
        "Action": "GPU_DC_HZ",
        "Enable": true,
    });
    assert_eq!(payload, &expected);
}

#[tokio::test]
async fn overdrive_on_publishes_lcdoverdrive_on() {
    let mut broker = FakeBroker::new();
    apply_overdrive(&mut broker, true)
        .await
        .expect("LCDOverdrive_ON");
    let actions = write_actions(&broker);
    assert_eq!(actions, vec!["LCDOverdrive_ON".to_owned()]);
}

#[tokio::test]
async fn overdrive_off_publishes_lcdoverdrive_off() {
    let mut broker = FakeBroker::new();
    apply_overdrive(&mut broker, false)
        .await
        .expect("LCDOverdrive_OFF");
    let actions = write_actions(&broker);
    assert_eq!(actions, vec!["LCDOverdrive_OFF".to_owned()]);
}

#[tokio::test]
async fn local_dimming_on_publishes_localdimming_on() {
    let mut broker = FakeBroker::new();
    apply_local_dimming(&mut broker, true)
        .await
        .expect("LOCALDIMMING_ON");
    let actions = write_actions(&broker);
    assert_eq!(actions, vec!["LOCALDIMMING_ON".to_owned()]);
}

#[tokio::test]
async fn local_dimming_off_publishes_localdimming_off() {
    let mut broker = FakeBroker::new();
    apply_local_dimming(&mut broker, false)
        .await
        .expect("LOCALDIMMING_OFF");
    let actions = write_actions(&broker);
    assert_eq!(actions, vec!["LOCALDIMMING_OFF".to_owned()]);
}

#[tokio::test]
async fn backend_set_overdrive_publishes_after_start() {
    let mut backend = Backend::fake_from_json("{}").expect("parse");
    backend.start().await.expect("start");
    backend.set_overdrive(true).await.expect("overdrive");
    let publishes = backend.recorded_publishes();
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "Setting/Control" && payload["Action"] == "LCDOverdrive_ON"
        }),
        "LCDOverdrive_ON missing: {publishes:?}"
    );
}

#[tokio::test]
async fn backend_set_local_dimming_publishes_after_start() {
    let mut backend = Backend::fake_from_json("{}").expect("parse");
    backend.start().await.expect("start");
    backend
        .set_local_dimming(false)
        .await
        .expect("local dimming");
    let publishes = backend.recorded_publishes();
    assert!(
        publishes.iter().any(|(topic, payload)| {
            topic == "Setting/Control" && payload["Action"] == "LOCALDIMMING_OFF"
        }),
        "LOCALDIMMING_OFF missing: {publishes:?}"
    );
}

#[tokio::test]
async fn direct_connect_restart_frame_recorded_with_delay_skipped_in_tests() {
    let mut broker = FakeBroker::new();
    apply_direct_connect_restart(&mut broker, Duration::ZERO)
        .await
        .expect("DGPU_DIRECT_CONNECT_RESTART");
    let publishes = recorded_publishes(&broker);
    let found = publishes.iter().find(|(topic, payload)| {
        topic == "Setting/Control" && payload["Action"] == "DGPU_DIRECT_CONNECT_RESTART"
    });
    let (_, payload) = found.expect("Setting/Control DGPU_DIRECT_CONNECT_RESTART missing");
    let expected = serde_json::json!({
        "Action": "DGPU_DIRECT_CONNECT_RESTART",
    });
    assert_eq!(payload, &expected);
}

static BRIGHTNESS_SEAM: Mutex<()> = Mutex::new(());

struct RecordingWmi {
    writes: Mutex<Vec<(u32, u8)>>,
    thread_id: Mutex<Option<ThreadId>>,
    on_tokio_worker: Mutex<Option<bool>>,
}

impl RecordingWmi {
    fn new() -> Arc<Self> {
        Arc::new(Self {
            writes: Mutex::new(Vec::new()),
            thread_id: Mutex::new(None),
            on_tokio_worker: Mutex::new(None),
        })
    }
}

impl BrightnessSink for RecordingWmi {
    fn set_brightness(&self, timeout: u32, brightness: u8) {
        *self.thread_id.lock().expect("thread_id") = Some(std::thread::current().id());
        *self.on_tokio_worker.lock().expect("tokio flag") =
            Some(tokio::runtime::Handle::try_current().is_ok());
        self.writes
            .lock()
            .expect("writes")
            .push((timeout, brightness));
    }
}

#[cfg(not(windows))]
#[tokio::test]
async fn real_set_brightness_without_sink_is_unavailable_off_windows() {
    let _seam = BRIGHTNESS_SEAM.lock().expect("brightness seam");
    let mut backend = Backend::real();
    let err = backend
        .set_brightness(40)
        .await
        .expect_err("live WMI does not exist off Windows");
    assert!(
        matches!(err, HostError::RealUnavailable),
        "expected RealUnavailable, got {err}"
    );
}

#[tokio::test]
async fn real_brightness_refuses_unless_injected_sink_is_installed() {
    // Integration tests compile the library without cfg(test). A Real brightness
    // write that does not install the injected sink must not open live WMI on a
    // Windows runner. The sink is the seam; missing it is RealUnavailable.
    let _seam = BRIGHTNESS_SEAM.lock().expect("brightness seam");
    let mut backend = Backend::real();
    let err = backend
        .set_brightness(40)
        .await
        .expect_err("live WMI is forbidden unless the brightness sink is installed");
    assert!(
        matches!(err, HostError::RealUnavailable),
        "expected RealUnavailable, got {err}"
    );
}

#[tokio::test]
async fn real_set_brightness_runs_wmi_on_sta_thread_not_tokio_worker() {
    // Given: Real arm + injected WMI double that records the calling thread
    let _seam = BRIGHTNESS_SEAM.lock().expect("brightness seam");
    let sink = RecordingWmi::new();
    let _guard = Backend::inject_brightness_sink(Arc::clone(&sink) as Arc<dyn BrightnessSink>);
    let mut backend = Backend::real();

    // When: brightness write on the Real arm
    backend
        .set_brightness(70)
        .await
        .expect("Real WMI path must not return RealUnavailable");

    // Then: WmiSetBrightness(1, 70) ran off the Tokio worker; no MQTT
    let writes = sink.writes.lock().expect("writes").clone();
    assert_eq!(writes, vec![(1, 70)]);
    assert_eq!(
        *sink.on_tokio_worker.lock().expect("tokio flag"),
        Some(false),
        "WMI must not run on a Tokio worker"
    );
    assert_ne!(
        *sink.thread_id.lock().expect("thread_id"),
        Some(std::thread::current().id()),
        "WMI must run on the dedicated STA thread"
    );
    let publishes = backend.recorded_publishes();
    assert!(
        publishes.iter().all(|(_, payload)| {
            payload.get("Action").and_then(|value| value.as_str()) != Some("SETSCREENBRIGHTNESS")
        }),
        "brightness must not publish SETSCREENBRIGHTNESS, got {publishes:?}"
    );
}

static CCD_SEAM: Mutex<()> = Mutex::new(());

struct RecordingCcd {
    result: AdvancedColorQuery,
    calls: Mutex<u32>,
}

impl RecordingCcd {
    fn new(result: AdvancedColorQuery) -> Arc<Self> {
        Arc::new(Self {
            result,
            calls: Mutex::new(0),
        })
    }
}

impl AdvancedColorProbe for RecordingCcd {
    fn query(&self) -> AdvancedColorQuery {
        *self.calls.lock().expect("calls") += 1;
        self.result
    }
}

#[test]
fn advanced_color_enabled_matches_csharp_is_advanced_color_enabled() {
    // MechrevoService.cs:972, 974-975: IsAdvancedColorEnabled = state != Off.
    assert!(is_advanced_color_enabled(true, false));
    assert!(is_advanced_color_enabled(false, true));
    assert!(is_advanced_color_enabled(true, true));
    assert!(!is_advanced_color_enabled(false, false));
}

#[test]
fn advanced_color_v2_mode_matches_screenccd() {
    // ScreenCCD.cs:69-70: HDR=2, WCG/ACM=1.
    assert_eq!(hdr_acm_from_active_color_mode(2), (true, false));
    assert_eq!(hdr_acm_from_active_color_mode(1), (false, true));
    assert_eq!(hdr_acm_from_active_color_mode(0), (false, false));
}

#[test]
fn advanced_color_v1_bits_match_screenccd() {
    // ScreenCCD.cs:89-90.
    assert_eq!(
        hdr_acm_from_legacy_color_info(true, false, 8),
        (true, false)
    );
    assert_eq!(
        hdr_acm_from_legacy_color_info(true, true, 10),
        (false, true)
    );
    assert_eq!(
        hdr_acm_from_legacy_color_info(true, true, 8),
        (false, false)
    );
    assert_eq!(
        hdr_acm_from_legacy_color_info(false, false, 10),
        (false, false)
    );
}

#[tokio::test]
async fn real_calibration_refuses_when_advanced_color_on() {
    // Given: Real path + injected CCD probe reporting advanced colour on
    // (C# `_readHdrEnabled = IsAdvancedColorEnabled`, MechrevoService.cs:118, 972).
    let _seam = CCD_SEAM.lock().expect("ccd seam");
    let probe = RecordingCcd::new(AdvancedColorQuery::Enabled);
    let _guard =
        Backend::inject_advanced_color_probe(Arc::clone(&probe) as Arc<dyn AdvancedColorProbe>);
    let mut backend = Backend::real();

    // When: calibration ON is requested
    let err = backend
        .set_calibration("COLOR_CALIBRATION_ON_SRGB")
        .await
        .expect_err("advanced colour on must refuse calibration");

    // Then: DisplayDenied the action (not the unavailable reason); probe was used
    assert_eq!(*probe.calls.lock().expect("calls"), 1);
    assert!(
        matches!(err, HostError::DisplayDenied(ref action) if action == "COLOR_CALIBRATION_ON_SRGB"),
        "expected DisplayDenied(COLOR_CALIBRATION_ON_SRGB), got {err}"
    );
}

#[tokio::test]
async fn real_calibration_proceeds_when_advanced_color_off() {
    // Given: Real path + injected CCD probe reporting advanced colour off
    let _seam = CCD_SEAM.lock().expect("ccd seam");
    let probe = RecordingCcd::new(AdvancedColorQuery::Disabled);
    let _guard =
        Backend::inject_advanced_color_probe(Arc::clone(&probe) as Arc<dyn AdvancedColorProbe>);
    let mut backend = Backend::real();

    // When: calibration ON is requested
    backend
        .set_calibration("COLOR_CALIBRATION_ON_SRGB")
        .await
        .expect("advanced colour off must proceed");

    // Then: the CCD probe was consulted (live device never opened)
    assert_eq!(*probe.calls.lock().expect("calls"), 1);
}

#[tokio::test]
async fn real_calibration_refuses_when_advanced_color_query_unavailable() {
    // Given: Real path with no injected CCD probe (non-Windows / API fail / test binary)
    let _seam = CCD_SEAM.lock().expect("ccd seam");
    let mut backend = Backend::real();

    // When: calibration ON is requested
    let err = backend
        .set_calibration("COLOR_CALIBRATION_ON_SRGB")
        .await
        .expect_err("unavailable query must fail-closed");

    // Then: refuse and say the query could not run
    assert!(
        matches!(
            err,
            HostError::DisplayDenied(ref msg)
                if msg.contains("COLOR_CALIBRATION_ON_SRGB") && msg.contains("unavailable")
        ),
        "expected DisplayDenied with unavailable reason, got {err}"
    );
}
