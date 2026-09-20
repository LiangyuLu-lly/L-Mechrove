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
            },
        }
    }
}
