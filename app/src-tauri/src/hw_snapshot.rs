//! Gated DTO for the webview. Lighting is ItemSupport, never MQTT Seen.

use capabilities::{DgpuGeneration, FeatureBit, FeatureMatrix, ItemSupport, LightingVisibility};
use serde::{Deserialize, Serialize};

use crate::hw_backend::Backend;
use crate::hw_model;
use crate::hw_switches::{offered_quick_switches, SeenFlags};

fn tcc_adjustable(item_support: &ItemSupport) -> bool {
    item_support.any_truthy(&[
        "IsAMDPlatform",
        "OcSettingsSupport",
        "HWOCSupport",
        "CPUPerformanceAndOverClockMenuSupport",
    ])
}

fn silent_turbo(item_support: &ItemSupport) -> bool {
    item_support.is_truthy("IsTurboSubModeSupport")
}

fn fan_curve_table_name(index: u8) -> String {
    format!("M4T{}", u16::from(index) + 1)
}

fn lc_connection_label(direct: bool, gcu: bool) -> String {
    if direct {
        "direct".to_owned()
    } else if gcu {
        "gcu".to_owned()
    } else {
        "none".to_owned()
    }
}

/// GCU connection pill. Serialized as a PascalCase string.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum MqttStatus {
    Connecting,
    Connected,
    Disconnected,
    Error,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct HwSnapshot {
    pub mqtt: MqttStatus,
    pub lighting: LightingVisibility,
    #[serde(rename = "chargePercent")]
    pub charge_percent: u8,
    #[serde(rename = "gpuActions")]
    pub gpu_actions: Vec<String>,
    #[serde(rename = "writeAllowed")]
    pub write_allowed: bool,
    #[serde(rename = "hzList")]
    pub hz_list: Vec<String>,
    #[serde(rename = "offeredSwitches")]
    pub offered_switches: Vec<String>,
    #[serde(rename = "liquidCooling")]
    pub liquid_cooling: bool,
    #[serde(rename = "hdrOn")]
    pub hdr_on: bool,
    #[serde(rename = "tccAdjustable")]
    pub tcc_adjustable: bool,
    #[serde(rename = "ocSettings")]
    pub oc_settings: bool,
    #[serde(rename = "silentTurbo")]
    pub silent_turbo: bool,
    #[serde(rename = "dcHzSeen", default)]
    pub dc_hz_seen: bool,
    #[serde(rename = "colorCalibration")]
    pub color_calibration: bool,
    #[serde(rename = "cpuTempC", skip_serializing_if = "Option::is_none")]
    pub cpu_temp_c: Option<f64>,
    #[serde(rename = "gpuTempC", skip_serializing_if = "Option::is_none")]
    pub gpu_temp_c: Option<f64>,
    #[serde(rename = "cpuRpm", skip_serializing_if = "Option::is_none")]
    pub cpu_rpm: Option<i64>,
    #[serde(rename = "gpuRpm", skip_serializing_if = "Option::is_none")]
    pub gpu_rpm: Option<i64>,
    #[serde(rename = "cpuWatt", skip_serializing_if = "Option::is_none")]
    pub cpu_watt: Option<f64>,
    #[serde(rename = "gpuWatt", skip_serializing_if = "Option::is_none")]
    pub gpu_watt: Option<f64>,
    #[serde(rename = "keyboardHidUnavailable")]
    pub keyboard_hid_unavailable: bool,
    #[serde(rename = "lightingOffOnBattery")]
    pub lighting_off_on_battery: bool,
    #[serde(rename = "lightingIdleSeconds")]
    pub lighting_idle_seconds: i32,
    #[serde(rename = "modelReason")]
    pub model_reason: String,
    #[serde(rename = "projectId")]
    pub project_id: String,
    #[serde(rename = "ocRequiresElevation")]
    pub oc_requires_elevation: bool,
    #[serde(rename = "themeMode")]
    pub theme_mode: String,
    #[serde(rename = "releaseLabel")]
    pub release_label: String,
    #[serde(rename = "batteryHealth")]
    pub battery_health: String,
    #[serde(rename = "chargeStatus")]
    pub charge_status: String,
    #[serde(rename = "chargeFullOffered")]
    pub charge_full_offered: bool,
    pub overdrive: bool,
    #[serde(rename = "localDimming")]
    pub local_dimming: bool,
    #[serde(rename = "customProfileOffered")]
    pub custom_profile_offered: bool,
    #[serde(rename = "lcConnection")]
    pub lc_connection: String,
    #[serde(rename = "fanCurveTableName")]
    pub fan_curve_table_name: String,
    #[serde(rename = "updateAvailable")]
    pub update_available: bool,
}

/// Inputs shared by Fake and Real so the catalog cannot diverge.
pub struct SnapshotParts<'a> {
    pub mqtt: MqttStatus,
    pub item_support: &'a ItemSupport,
    pub seen: &'a SeenFlags,
    pub charge_percent: u8,
    pub gpu_generation: DgpuGeneration,
    pub gpu_three_mode: bool,
    pub write_allowed: bool,
    pub hz_list: &'a [String],
    pub hdr_on: bool,
    pub dc_hz_seen: bool,
    pub cpu_temp_c: Option<f64>,
    pub gpu_temp_c: Option<f64>,
    pub cpu_rpm: Option<i64>,
    pub gpu_rpm: Option<i64>,
    pub cpu_watt: Option<f64>,
    pub gpu_watt: Option<f64>,
    pub keyboard_hid_unavailable: bool,
    pub lighting_off_on_battery: bool,
    pub lighting_idle_seconds: i32,
    pub model_reason: String,
    pub project_id: String,
    pub oc_requires_elevation: bool,
    pub theme_mode: String,
    pub battery_health: String,
    pub charge_status: String,
    pub charge_full_offered: bool,
    pub lc_direct: bool,
    pub lc_mqtt_connected: bool,
    pub custom_profile_index: u8,
    pub update_available: bool,
}

pub fn snapshot_from(parts: SnapshotParts<'_>) -> HwSnapshot {
    HwSnapshot {
        mqtt: parts.mqtt,
        lighting: LightingVisibility::from_item_support(parts.item_support),
        charge_percent: parts.charge_percent,
        gpu_actions: crate::hw_gpu::offered_actions(
            parts.item_support,
            parts.gpu_generation,
            parts.gpu_three_mode,
        ),
        write_allowed: parts.write_allowed,
        hz_list: parts.hz_list.to_vec(),
        offered_switches: offered_quick_switches(parts.item_support, parts.seen)
            .into_iter()
            .map(str::to_string)
            .collect(),
        liquid_cooling: FeatureMatrix::from_values(parts.item_support)
            .is_supported(FeatureBit::LiquidCooling),
        hdr_on: parts.hdr_on,
        tcc_adjustable: tcc_adjustable(parts.item_support),
        oc_settings: parts.item_support.is_truthy("OcSettingsSupport"),
        silent_turbo: silent_turbo(parts.item_support),
        dc_hz_seen: parts.dc_hz_seen,
        color_calibration: FeatureMatrix::from_values(parts.item_support)
            .is_supported(FeatureBit::ColorCalibration),
        cpu_temp_c: parts.cpu_temp_c,
        gpu_temp_c: parts.gpu_temp_c,
        cpu_rpm: parts.cpu_rpm,
        gpu_rpm: parts.gpu_rpm,
        cpu_watt: parts.cpu_watt,
        gpu_watt: parts.gpu_watt,
        keyboard_hid_unavailable: parts.keyboard_hid_unavailable,
        lighting_off_on_battery: parts.lighting_off_on_battery,
        lighting_idle_seconds: parts.lighting_idle_seconds,
        model_reason: parts.model_reason,
        project_id: parts.project_id,
        oc_requires_elevation: parts.oc_requires_elevation,
        theme_mode: parts.theme_mode,
        release_label: env!("CARGO_PKG_VERSION").to_owned(),
        battery_health: parts.battery_health,
        charge_status: parts.charge_status,
        charge_full_offered: parts.charge_full_offered,
        overdrive: parts.item_support.is_truthy("LcdOverDriveSupport"),
        local_dimming: parts.item_support.is_truthy("LocalDimmingSupport"),
        custom_profile_offered: FeatureMatrix::from_values(parts.item_support)
            .is_supported(FeatureBit::FanSettings),
        lc_connection: lc_connection_label(
            parts.lc_direct,
            parts.lc_mqtt_connected
                && FeatureMatrix::from_values(parts.item_support)
                    .is_supported(FeatureBit::LiquidCooling),
        ),
        fan_curve_table_name: fan_curve_table_name(parts.custom_profile_index),
        update_available: parts.update_available,
    }
}

impl Backend {
    pub fn snapshot(&self) -> HwSnapshot {
        match self {
            Self::Fake { state } => snapshot_from(SnapshotParts {
                mqtt: state.mqtt_status,
                item_support: &state.item_support,
                seen: &state.seen,
                charge_percent: state.charge_percent,
                gpu_generation: state.gpu_generation,
                gpu_three_mode: state.gpu_three_mode,
                write_allowed: state.write_allowed,
                hz_list: &state.hz_list,
                hdr_on: state.hdr_on,
                dc_hz_seen: state.dc_hz_seen,
                cpu_temp_c: state.cpu_temp_c,
                gpu_temp_c: state.gpu_temp_c,
                cpu_rpm: state.cpu_rpm,
                gpu_rpm: state.gpu_rpm,
                cpu_watt: state.cpu_watt,
                gpu_watt: state.gpu_watt,
                keyboard_hid_unavailable: false,
                lighting_off_on_battery: false,
                lighting_idle_seconds: 0,
                model_reason: state.model_reason.clone(),
                project_id: state.project_id.clone(),
                oc_requires_elevation: false,
                theme_mode: "night".to_owned(),
                battery_health: state.battery_health.clone(),
                charge_status: state.charge_status.clone(),
                charge_full_offered: true,
                lc_direct: false,
                lc_mqtt_connected: !state.lc_mqtt_disconnected,
                custom_profile_index: state.custom_profile_index,
                update_available: state.update_available,
            }),
            Self::Real { state } => {
                let mqtt_connected = state.mqtt_status == MqttStatus::Connected;
                snapshot_from(SnapshotParts {
                    mqtt: state.mqtt_status,
                    item_support: &state.item_support,
                    seen: &state.seen,
                    charge_percent: state.charge_percent,
                    gpu_generation: state.gpu_generation,
                    gpu_three_mode: state.gpu_three_mode,
                    write_allowed: hw_model::is_served(mqtt_connected, &state.item_support),
                    hz_list: &state.hz_list,
                    hdr_on: state.hdr_on,
                    dc_hz_seen: state.dc_hz_seen,
                    cpu_temp_c: state.cpu_temp_c,
                    gpu_temp_c: state.gpu_temp_c,
                    cpu_rpm: state.cpu_rpm,
                    gpu_rpm: state.gpu_rpm,
                    cpu_watt: state.cpu_watt,
                    gpu_watt: state.gpu_watt,
                    keyboard_hid_unavailable: state.keyboard_hid_unavailable,
                    lighting_off_on_battery: state.lighting_off_on_battery,
                    lighting_idle_seconds: state.lighting_idle_seconds,
                    model_reason: hw_model::model_reason(mqtt_connected, &state.item_support)
                        .to_owned(),
                    project_id: state.project_id.clone(),
                    oc_requires_elevation: false,
                    theme_mode: state.theme_mode.clone(),
                    battery_health: String::new(),
                    charge_status: String::new(),
                    charge_full_offered: true,
                    lc_direct: false,
                    lc_mqtt_connected: mqtt_connected,
                    custom_profile_index: state.custom_profile_index,
                    update_available: false,
                })
            }
        }
    }
}
