//! Given MQTT status payloads C# parses. When apply_inbound. Then Seen / telemetry / hz / charge update.

use app_lib::hw_mqtt_inbound::{apply_inbound, InboundLive};
use app_lib::hw_switches::SeenFlags;

fn live() -> InboundLive {
    InboundLive::default()
}

#[test]
fn apply_inbound_cpu_info_sets_temp() {
    // Given: System/CpuInfo
    let mut live = live();

    // When: CpuTemperature is parsed
    let changed = apply_inbound(&mut live, "System/CpuInfo", br#"{"CpuTemperature":71}"#);

    // Then: telemetry is Some
    assert!(changed);
    assert_eq!(live.cpu_temp_c, Some(71.0));
}

#[test]
fn apply_inbound_fan_info_sets_rpm() {
    // Given: System/FanInfo
    let mut live = live();

    // When: rpm fields are parsed
    apply_inbound(
        &mut live,
        "System/FanInfo",
        br#"{"CpuFanRpm":2100,"GpuFanRpm":1800}"#,
    );

    // Then: rpm Options are integers
    assert_eq!(live.cpu_rpm, Some(2100));
    assert_eq!(live.gpu_rpm, Some(1800));
}

#[test]
fn apply_inbound_battery_info_sets_charge_percent() {
    // Given: System/BatteryInfo
    let mut live = live();

    // When: BatteryLifePercent is parsed
    apply_inbound(
        &mut live,
        "System/BatteryInfo",
        br#"{"BatteryLifePercent":64}"#,
    );

    // Then: charge_percent is 64
    assert_eq!(live.charge_percent, 64);
}

#[test]
fn apply_inbound_gpu_device_status_sets_hz_list_and_dc_hz_seen() {
    // Given: GPUDevice/Status
    let mut live = live();

    // When: currentHZList and DC_HZ parse
    apply_inbound(
        &mut live,
        "GPUDevice/Status",
        br#"{"currentHZList":["165","60"],"currentHZ":165,"DC_HZ":true}"#,
    );

    // Then: hz strings and dc_hz_seen
    assert_eq!(live.hz_list, vec!["165".to_owned(), "60".to_owned()]);
    assert!(live.dc_hz_seen);
}

#[test]
fn apply_inbound_empty_dc_hz_does_not_set_seen() {
    // Given: GPUDevice/Status without a parsable DC_HZ
    let mut live = live();

    // When: DC_HZ is omitted
    apply_inbound(&mut live, "GPUDevice/Status", br#"{"currentHZList":[60]}"#);

    // Then: DcHzSeen stays false
    assert!(!live.dc_hz_seen);
}

#[test]
fn apply_inbound_device_switch_sets_wifi_seen() {
    // Given: Settings/DeviceSwitchItemStatus
    let mut live = live();

    // When: WIFIEnable parses
    apply_inbound(
        &mut live,
        "Settings/DeviceSwitchItemStatus",
        br#"{"WIFIEnable":true,"TochpadEnable":true}"#,
    );

    // Then: wifi Seen is set
    assert!(live.seen.wifi);
}

#[test]
fn apply_inbound_empty_lightbar_does_not_set_seen() {
    // Given: HidLightbar/Status with empty type/powerStatus
    let mut live = live();

    // When: HasLightbarContent is false
    apply_inbound(&mut live, "HidLightbar/Status", br#"{"effect":"1"}"#);

    // Then: lightbar Seen stays false
    assert!(!live.seen.lightbar);
}

#[test]
fn apply_inbound_lightbar_content_sets_seen() {
    // Given: HidLightbar/Status with type
    let mut live = live();

    // When: type is non-empty
    apply_inbound(
        &mut live,
        "HidLightbar/Status",
        br#"{"type":"rainbow","powerStatus":"1"}"#,
    );

    // Then: lightbar Seen is true (snapshot lighting still ignores this)
    assert!(live.seen.lightbar);
}

#[test]
fn apply_inbound_fan_status_stores_custom_profile_index() {
    // Given: Fan/Status
    let mut live = live();

    // When: CustomProfileIndex is a JSON number 2
    apply_inbound(&mut live, "Fan/Status", br#"{"CustomProfileIndex":2}"#);

    // Then: stored slot is 2
    assert_eq!(live.custom_profile_index, Some(2));
}

#[test]
fn apply_inbound_setting_status_sets_win_key_seen() {
    // Given: Setting/Status
    let mut live = live();

    // When: WinKey is present
    apply_inbound(&mut live, "Setting/Status", br#"{"WinKey":"WINKEY_LOCK"}"#);

    // Then: win_key Seen is true
    assert!(live.seen.win_key);
    let _flags = SeenFlags::default();
}

#[test]
fn apply_inbound_setting_status_sets_color_calibration_mode() {
    // Given: Setting/Status carrying C# FirstField ColorCalibrationMode
    // (MechrevoHw.cs:1938-1944)
    let mut live = live();

    // When: ColorCalibrationMode is a JSON number 2 (sRGB)
    let changed = apply_inbound(
        &mut live,
        "Setting/Status",
        br#"{"ColorCalibrationMode":2}"#,
    );

    // Then: current mode is 2 so OFF can send FileName=sRGB
    assert!(changed);
    assert_eq!(live.color_calibration_mode, Some(2));
}
