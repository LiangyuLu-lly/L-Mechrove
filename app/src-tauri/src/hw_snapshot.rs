//! Gated DTO for the webview. Lighting is ItemSupport, never MQTT Seen.

use capabilities::{FeatureBit, FeatureMatrix, ItemSupport, LightingVisibility};
use serde::{Deserialize, Serialize};

use crate::hw_backend::Backend;
use crate::hw_switches::offered_quick_switches;

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
}

impl Backend {
    pub fn snapshot(&self) -> HwSnapshot {
        match self {
            Self::Fake { state } => HwSnapshot {
                mqtt: state.mqtt_status,
                lighting: LightingVisibility::from_item_support(&state.item_support),
                charge_percent: state.charge_percent,
                gpu_actions: crate::hw_gpu::offered_actions(
                    &state.item_support,
                    state.gpu_generation,
                    state.gpu_three_mode,
                ),
                write_allowed: state.write_allowed,
                hz_list: state.hz_list.clone(),
                offered_switches: offered_quick_switches(&state.item_support, &state.seen)
                    .into_iter()
                    .map(str::to_string)
                    .collect(),
                liquid_cooling: FeatureMatrix::from_values(&state.item_support)
                    .is_supported(FeatureBit::LiquidCooling),
                hdr_on: state.hdr_on,
                tcc_adjustable: tcc_adjustable(&state.item_support),
                oc_settings: state.item_support.is_truthy("OcSettingsSupport"),
                silent_turbo: silent_turbo(&state.item_support),
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
                model_reason: String::new(),
                project_id: String::new(),
                oc_requires_elevation: false,
                theme_mode: "night".to_owned(),
                release_label: env!("CARGO_PKG_VERSION").to_owned(),
            },
            Self::Real => HwSnapshot {
                mqtt: MqttStatus::Error,
                lighting: LightingVisibility::from_item_support(&ItemSupport::default()),
                charge_percent: 100,
                gpu_actions: Vec::new(),
                write_allowed: false,
                hz_list: Vec::new(),
                offered_switches: Vec::new(),
                liquid_cooling: false,
                hdr_on: false,
                tcc_adjustable: false,
                oc_settings: false,
                silent_turbo: false,
                dc_hz_seen: false,
                cpu_temp_c: None,
                gpu_temp_c: None,
                cpu_rpm: None,
                gpu_rpm: None,
                cpu_watt: None,
                gpu_watt: None,
                keyboard_hid_unavailable: false,
                lighting_off_on_battery: false,
                lighting_idle_seconds: 0,
                model_reason: String::new(),
                project_id: String::new(),
                oc_requires_elevation: false,
                theme_mode: "night".to_owned(),
                release_label: env!("CARGO_PKG_VERSION").to_owned(),
            },
        }
    }
}
