//! Parse GCU status reports into Seen flags, telemetry, hz, and charge.

use serde_json::Value;

use crate::hw_switches::SeenFlags;

/// Live fields filled from inbound MQTT. Snapshot lighting still ignores Seen.
#[derive(Debug, Clone)]
pub struct InboundLive {
    pub seen: SeenFlags,
    pub charge_percent: u8,
    pub hz_list: Vec<String>,
    pub dc_hz_seen: bool,
    pub cpu_temp_c: Option<f64>,
    pub gpu_temp_c: Option<f64>,
    pub cpu_rpm: Option<i64>,
    pub gpu_rpm: Option<i64>,
    pub custom_profile_index: Option<u8>,
    pub color_calibration_mode: Option<i32>,
}

impl Default for InboundLive {
    fn default() -> Self {
        Self {
            seen: SeenFlags::default(),
            charge_percent: 100,
            hz_list: Vec::new(),
            dc_hz_seen: false,
            cpu_temp_c: None,
            gpu_temp_c: None,
            cpu_rpm: None,
            gpu_rpm: None,
            custom_profile_index: None,
            color_calibration_mode: None,
        }
    }
}

/// Apply one topic payload. Returns whether snapshot-visible fields changed.
pub fn apply_inbound(live: &mut InboundLive, topic: &str, payload: &[u8]) -> bool {
    let Ok(value) = serde_json::from_slice::<Value>(payload) else {
        return false;
    };
    match topic {
        "System/CpuInfo" => set_f64(&mut live.cpu_temp_c, &value, "CpuTemperature"),
        "System/GpuInfo" => set_f64(&mut live.gpu_temp_c, &value, "GpuTemperature"),
        "System/FanInfo" => apply_fan_info(live, &value),
        "System/BatteryInfo" => apply_battery(live, &value),
        "GPUDevice/Status" => apply_gpu_device(live, &value),
        "Settings/DeviceSwitchItemStatus" => apply_device_switch(live, &value),
        "HidLightbar/Status" => set_seen_if_light_content(&mut live.seen.lightbar, &value),
        "HidLightbar_Logo/Status" => set_seen_if_light_content(&mut live.seen.logo_light, &value),
        "Setting/Status" => apply_setting_status(live, &value),
        "Fan/Status" => apply_fan_status(live, &value),
        _ => false,
    }
}

fn apply_fan_info(live: &mut InboundLive, value: &Value) -> bool {
    let cpu = set_i64(&mut live.cpu_rpm, value, "CpuFanRpm");
    let gpu = set_i64(&mut live.gpu_rpm, value, "GpuFanRpm");
    cpu || gpu
}

fn apply_battery(live: &mut InboundLive, value: &Value) -> bool {
    let Some(percent) = json_u8(value.get("BatteryLifePercent")) else {
        return false;
    };
    if live.charge_percent == percent {
        return false;
    }
    live.charge_percent = percent;
    true
}

fn apply_gpu_device(live: &mut InboundLive, value: &Value) -> bool {
    let mut changed = false;
    if let Some(list) = value.get("currentHZList") {
        let hz = parse_hz_list(list);
        if live.hz_list != hz {
            live.hz_list = hz;
            changed = true;
        }
    }
    if optional_bool(value.get("DC_HZ")).is_some() && !live.dc_hz_seen {
        live.dc_hz_seen = true;
        changed = true;
    }
    changed
}

fn apply_device_switch(live: &mut InboundLive, value: &Value) -> bool {
    let mut changed = false;
    if value.get("WIFIEnable").and_then(Value::as_bool).is_some() {
        changed |= set_flag(&mut live.seen.wifi, true);
    }
    if value.get("BTEnable").and_then(Value::as_bool).is_some() {
        changed |= set_flag(&mut live.seen.bluetooth, true);
    }
    if value.get("WebCamEnable").and_then(Value::as_bool).is_some() {
        changed |= set_flag(&mut live.seen.webcam, true);
    }
    changed
}

fn apply_setting_status(live: &mut InboundLive, value: &Value) -> bool {
    let obj = match value.as_object() {
        Some(obj) => obj,
        None => return false,
    };
    let mut changed = false;
    changed |= present(obj, "WinKey") && set_flag(&mut live.seen.win_key, true);
    changed |= present(obj, "FnKey") && set_flag(&mut live.seen.fn_key, true);
    changed |= present(obj, "NumPad") && set_flag(&mut live.seen.numpad, true);
    changed |= present(obj, "DeepSleepSwitch") && set_flag(&mut live.seen.deep_sleep, true);
    changed |= present(obj, "CopilotKey") && set_flag(&mut live.seen.copilot, true);
    changed |=
        present(obj, "AcRecoverySwitch_Status") && set_flag(&mut live.seen.ac_recovery, true);
    changed |=
        present(obj, "HighPerformancePowerModeSwitch") && set_flag(&mut live.seen.high_perf, true);
    changed |= present(obj, "TouchpadToggle") && set_flag(&mut live.seen.touchpad_toggle, true);
    changed |= present(obj, "SingleColorKBBL") && set_flag(&mut live.seen.single_color_kb, true);
    changed |= (present(obj, "UniSwitch") || present(obj, "OmniSwitch"))
        && set_flag(&mut live.seen.uni_omni, true);
    changed |= present(obj, "PowerLightSwitch") && set_flag(&mut live.seen.power_light, true);
    changed |= present(obj, "BatteryLogo_Status") && set_flag(&mut live.seen.battery_logo, true);
    // C# MechrevoHw.cs:1938-1944 FirstField then ParseColorCalibrationMode.
    if let Some(mode) = color_calibration_mode_from_status(value) {
        if live.color_calibration_mode != Some(mode) {
            live.color_calibration_mode = Some(mode);
            changed = true;
        }
    }
    changed
}

fn color_calibration_mode_from_status(value: &Value) -> Option<i32> {
    let token = [
        "CurrentColorCalibration",
        "ColorCalibrationMode",
        "ColorCalibration",
    ]
    .iter()
    .find_map(|key| value.get(*key).filter(|item| !item.is_null()))?;
    let parsed = json_i64(Some(token))
        .and_then(|n| i32::try_from(n).ok())
        .unwrap_or_else(|| match token.as_str().unwrap_or("").to_ascii_lowercase() {
            ref lower if lower.contains("srgb") => 2,
            ref lower if lower.contains("p3") => 3,
            ref lower if lower.contains("adobe") => 4,
            ref lower if lower.contains("default") => 1,
            _ => 0,
        });
    (1..=4).contains(&parsed).then_some(parsed)
}

fn apply_fan_status(live: &mut InboundLive, value: &Value) -> bool {
    let mut changed = false;
    if value.get("FanBoostEnable").is_some() {
        changed |= set_flag(&mut live.seen.fan_boost, true);
    }
    if value.get("GameWhitelistSwitch").is_some() {
        changed |= set_flag(&mut live.seen.game_whitelist, true);
    }
    if value.get("CPU_PerformanceAndOverClockMenuSwitch").is_some() {
        changed |= set_flag(&mut live.seen.cpu_adv_perf, true);
    }
    if let Some(index) = json_u8(value.get("CustomProfileIndex")) {
        if index <= 3 && live.custom_profile_index != Some(index) {
            live.custom_profile_index = Some(index);
            changed = true;
        }
    }
    changed
}

fn set_seen_if_light_content(flag: &mut bool, value: &Value) -> bool {
    if !has_lightbar_content(value) {
        return false;
    }
    set_flag(flag, true)
}

fn has_lightbar_content(value: &Value) -> bool {
    nonempty_field(value, "type") || nonempty_field(value, "powerStatus")
}

fn nonempty_field(value: &Value, key: &str) -> bool {
    value
        .get(key)
        .and_then(Value::as_str)
        .is_some_and(|text| !text.trim().is_empty())
}

fn present(obj: &serde_json::Map<String, Value>, key: &str) -> bool {
    obj.contains_key(key)
}

fn set_flag(slot: &mut bool, value: bool) -> bool {
    if *slot == value {
        return false;
    }
    *slot = value;
    true
}

fn set_f64(slot: &mut Option<f64>, value: &Value, key: &str) -> bool {
    let Some(parsed) = json_f64(value.get(key)) else {
        return false;
    };
    if *slot == Some(parsed) {
        return false;
    }
    *slot = Some(parsed);
    true
}

fn set_i64(slot: &mut Option<i64>, value: &Value, key: &str) -> bool {
    let Some(parsed) = json_i64(value.get(key)) else {
        return false;
    };
    if *slot == Some(parsed) {
        return false;
    }
    *slot = Some(parsed);
    true
}

fn parse_hz_list(value: &Value) -> Vec<String> {
    let Some(arr) = value.as_array() else {
        return Vec::new();
    };
    let mut hz: Vec<i64> = arr
        .iter()
        .filter_map(|item| json_i64(Some(item)))
        .filter(|v| *v > 0)
        .collect();
    hz.sort_unstable_by(|a, b| b.cmp(a));
    hz.dedup();
    hz.into_iter().map(|v| v.to_string()).collect()
}

fn optional_bool(value: Option<&Value>) -> Option<bool> {
    let value = value?;
    if let Some(flag) = value.as_bool() {
        return Some(flag);
    }
    match value.as_str()?.trim() {
        "true" | "1" | "TRUE" => Some(true),
        "false" | "0" | "FALSE" => Some(false),
        _ => None,
    }
}

fn json_f64(value: Option<&Value>) -> Option<f64> {
    let value = value?;
    value
        .as_f64()
        .or_else(|| value.as_i64().map(|n| n as f64))
        .or_else(|| value.as_str()?.trim().parse().ok())
}

fn json_i64(value: Option<&Value>) -> Option<i64> {
    let value = value?;
    value
        .as_i64()
        .or_else(|| value.as_u64().and_then(|n| i64::try_from(n).ok()))
        .or_else(|| value.as_str()?.trim().parse().ok())
}

fn json_u8(value: Option<&Value>) -> Option<u8> {
    json_i64(value).and_then(|n| u8::try_from(n).ok())
}
