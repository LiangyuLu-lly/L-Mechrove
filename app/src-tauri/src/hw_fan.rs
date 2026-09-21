//! Fan curve MQTT. SET_FAN_SPEED_CURVE_SETTING T0–T15 are JSON strings.

use gcu_mqtt::client::MqttTransport;
use gcu_mqtt::payloads::{fan_boost_off, fan_boost_on};
use gcu_mqtt::topics::FAN_CONTROL;
use serde_json::{json, Map, Value};

use crate::hw_backend::Backend;
use crate::hw_error::HostError;
use crate::hw_mode_profile::ModeProfiles;

const ACTION: &str = "SET_FAN_SPEED_CURVE_SETTING";
const T_KEYS: [&str; 16] = [
    "T0", "T1", "T2", "T3", "T4", "T5", "T6", "T7", "T8", "T9", "T10", "T11", "T12", "T13", "T14",
    "T15",
];
const TEMP_SENTINEL: u8 = 255;
const FALLBACK_POINTS: usize = 11;
/// Gaming default CPU UpT (`DefaultCurve_Gaming.json`). Sentinel 255 at index 11.
const CPU_UP_T: [u8; 16] = [
    0, 46, 51, 54, 57, 60, 63, 66, 68, 70, 72, 255, 255, 255, 255, 255,
];
/// Gaming default GPU UpT. Same sentinel index, cooler steps than CPU.
const GPU_UP_T: [u8; 16] = [
    0, 45, 48, 51, 53, 55, 57, 59, 61, 63, 65, 255, 255, 255, 255, 255,
];

/// Wire `Type` for SET_FAN_SPEED_CURVE_SETTING. CPU and GPU only.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FanCurveType {
    Cpu,
    Gpu,
}

impl FanCurveType {
    const fn as_wire(self) -> &'static str {
        match self {
            Self::Cpu => "CPU",
            Self::Gpu => "GPU",
        }
    }

    fn from_wire(ty: &str) -> Option<Self> {
        match ty {
            "CPU" => Some(Self::Cpu),
            "GPU" => Some(Self::Gpu),
            _ => None,
        }
    }
}

fn duties_16(duties: &[u8]) -> [u8; 16] {
    let mut out = [0_u8; 16];
    let n = duties.len().min(16);
    out[..n].copy_from_slice(&duties[..n]);
    out
}

const fn clamp_duty(duty: u8) -> u8 {
    if duty > 100 {
        100
    } else {
        duty
    }
}

const fn default_up_t(ty: FanCurveType) -> [u8; 16] {
    match ty {
        FanCurveType::Cpu => CPU_UP_T,
        FanCurveType::Gpu => GPU_UP_T,
    }
}

/// C# `MechrevoHw.NormalizeFanCurve` (`MechrevoHw.cs:2712`): first UpT==255 is
/// `validCount`; if that index is <2, fall back to min(11, max(2, duty_count)).
const fn valid_count(temperatures: [u8; 16], duty_count: usize) -> usize {
    let mut i = 0;
    while i < 16 {
        if temperatures[i] == TEMP_SENTINEL {
            if i >= 2 {
                return i;
            }
            break;
        }
        i += 1;
    }
    let n = if duty_count > 2 { duty_count } else { 2 };
    if n < FALLBACK_POINTS {
        n
    } else {
        FALLBACK_POINTS
    }
}

/// Port of C# `MechrevoHw.NormalizeFanCurve` (`MechrevoHw.cs:2712-2733`).
pub const fn normalize_fan_curve(duties: [u8; 16], temperatures: [u8; 16]) -> [u8; 16] {
    let valid = valid_count(temperatures, 16);
    let mut result = [0_u8; 16];
    result[0] = clamp_duty(duties[0]);
    let mut i = 1;
    while i < valid {
        let clamped = clamp_duty(duties[i]);
        result[i] = if clamped > result[i - 1] {
            clamped
        } else {
            result[i - 1]
        };
        i += 1;
    }
    let trailing = result[valid - 1];
    while i < 16 {
        result[i] = trailing;
        i += 1;
    }
    result
}

/// Gate CPU|GPU, normalize duties, publish Fan/Control SET_FAN_SPEED_CURVE_SETTING.
pub async fn apply_fan_curve<T: MqttTransport>(
    transport: &mut T,
    name: &str,
    ty: FanCurveType,
    duties: [u8; 16],
) -> Result<(), HostError> {
    let duties = normalize_fan_curve(duties, default_up_t(ty));
    let mut payload = Map::new();
    payload.insert("Action".to_owned(), json!(ACTION));
    payload.insert("Name".to_owned(), json!(name));
    payload.insert("Type".to_owned(), json!(ty.as_wire()));
    for (key, duty) in T_KEYS.iter().zip(duties) {
        payload.insert((*key).to_owned(), Value::String(duty.to_string()));
    }
    let bytes = serde_json::to_vec(&payload)?;
    transport.publish(FAN_CONTROL, &bytes).await?;
    Ok(())
}

impl Backend {
    pub async fn set_fan_curve(
        &mut self,
        name: &str,
        ty: &str,
        duties: Vec<u8>,
    ) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                let ty = FanCurveType::from_wire(ty)
                    .ok_or_else(|| HostError::UnknownMode(ty.to_owned()))?;
                let duties = duties_16(&duties);
                apply_fan_curve(&mut state.broker, name, ty, duties).await?;
                let dir = state.profile_dir.clone();
                let mode = state.current_mode.clone();
                if let (Some(dir), Some(mode)) = (dir, mode) {
                    ModeProfiles::new(&dir).put_curve(&mode, ty, duties)?;
                }
                Ok(())
            }
            Self::Real { state } => {
                let ty = FanCurveType::from_wire(ty)
                    .ok_or_else(|| HostError::UnknownMode(ty.to_owned()))?;
                apply_fan_curve(&mut state.client, name, ty, duties_16(&duties)).await
            }
        }
    }

    pub async fn set_fan_boost(&mut self, on: bool) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                apply_fan_boost(&mut state.broker, on).await
            }
            Self::Real { state } => apply_fan_boost(&mut state.client, on).await,
        }
    }

    pub async fn set_custom_detail(&mut self, field: &str, value: &str) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                crate::hw_mode_detail::apply_custom_detail(
                    &mut state.broker,
                    &state.item_support,
                    field,
                    value,
                )
                .await?;
                let dir = state.profile_dir.clone();
                let mode = state.current_mode.clone();
                if let (Some(dir), Some(mode)) = (dir, mode) {
                    ModeProfiles::new(&dir).put_detail(&mode, field, value)?;
                }
                Ok(())
            }
            Self::Real { state } => {
                crate::hw_mode_detail::apply_custom_detail(
                    &mut state.client,
                    &state.item_support,
                    field,
                    value,
                )
                .await?;
                if let Some(dir) = state.profile_dir.clone() {
                    ModeProfiles::new(&dir).put_detail("custom", field, value)?;
                }
                if field == "ProfileIndex" {
                    if let Ok(index) = value.parse::<u8>() {
                        if index <= 3 {
                            state.custom_profile_index = index;
                        }
                    }
                }
                Ok(())
            }
        }
    }
}

pub async fn apply_fan_boost<T: MqttTransport>(
    transport: &mut T,
    on: bool,
) -> Result<(), HostError> {
    let payload = if on { fan_boost_on() } else { fan_boost_off() };
    let bytes = serde_json::to_vec(&payload)?;
    transport.publish(FAN_CONTROL, &bytes).await?;
    Ok(())
}
