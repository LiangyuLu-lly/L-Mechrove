//! BT_LC MQTT pump/fan/connect writes. `PumpCtrl`/`FanCtrl` are JSON strings. BLE only when MQTT LC is down.
//!
//! allow: SIZE_OK — one BT_LC protocol surface (route policy + connect family + pump/fan MQTT/BLE).

use capabilities::{FeatureBit, FeatureMatrix, ItemSupport};
use gcu_mqtt::client::MqttTransport;
use gcu_mqtt::topics;
use serde::Serialize;

use crate::hw_backend::Backend;
use crate::hw_ble::FakeBle;
use crate::hw_error::HostError;

const BLE_CMD_FAN: u8 = 0x1B;
const BLE_CMD_PUMP: u8 = 0x1C;
const BLE_PUMP_V7: u8 = 0x02;
const BLE_PUMP_V8: u8 = 0x03;
const BLE_PUMP_V11: u8 = 0x00;

/// C# `LiquidCoolingControlRoute`. Direct BLE wins, then GCU, then Windows-observed BT.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LcControlRoute {
    None,
    BluetoothObserved,
    Gcu,
    DirectBle,
}

/// C# `LiquidCoolingConnectionPolicy.ResolveRoute`.
pub const fn resolve_route(
    direct_ble_connected: bool,
    gcu_controllable: bool,
    bluetooth_observed: bool,
) -> LcControlRoute {
    if direct_ble_connected {
        LcControlRoute::DirectBle
    } else if gcu_controllable {
        LcControlRoute::Gcu
    } else if bluetooth_observed {
        LcControlRoute::BluetoothObserved
    } else {
        LcControlRoute::None
    }
}

/// C# `ShouldUseAutomaticDirectFallback`.
pub const fn should_use_automatic_direct_fallback(direct_ble_connected: bool) -> bool {
    !direct_ble_connected
}

/// C# `ShouldSkipDirectFallbackBecauseGcuHoldsRadio`.
pub fn should_skip_direct_fallback_because_gcu_holds_radio(gcu_connect: &str) -> bool {
    matches!(gcu_connect, "Scanning" | "Connecting" | "IsConnectable")
}

/// C# `ShouldRetryGcuConnection`.
pub const fn should_retry_gcu_connection(
    hardware_connected: bool,
    service_available: bool,
    route: LcControlRoute,
    action_support_reported: bool,
    action_supported: bool,
    attempts: i32,
    since_last_attempt_ms: i64,
) -> bool {
    hardware_connected
        && service_available
        && matches!(
            route,
            LcControlRoute::None | LcControlRoute::BluetoothObserved
        )
        && !(action_support_reported && !action_supported)
        && attempts < 5
        && since_last_attempt_ms >= 8000
}

/// C# `ShouldRestoreSavedGcuLighting`.
pub fn should_restore_saved_gcu_lighting(
    direct_ble_connected: bool,
    gcu_controllable: bool,
    gcu_status_fresh: bool,
    saved_profile: Option<&str>,
) -> bool {
    !direct_ble_connected
        && gcu_controllable
        && gcu_status_fresh
        && match saved_profile {
            Some(profile) => !profile.is_empty(),
            None => false,
        }
}

/// Liquid-cooling write failures. Capability miss is fail-closed.
#[derive(Debug, thiserror::Error)]
#[non_exhaustive]
pub enum LcError {
    #[error("liquid cooling not offered")]
    NotSupported,
    #[error("pump index out of range: {0}")]
    PumpIndex(u8),
    #[error("fan index out of range: {0}")]
    FanIndex(u8),
    #[error("device mac empty")]
    DeviceMacEmpty,
    #[error(transparent)]
    Mqtt(#[from] gcu_mqtt::client::MqttError),
    #[error(transparent)]
    Json(#[from] serde_json::Error),
}

#[derive(Serialize)]
struct LcPumpPayload {
    #[serde(rename = "Action")]
    action: &'static str,
    #[serde(rename = "PumpCtrl")]
    pump_ctrl: &'static str,
}

#[derive(Serialize)]
struct LcFanPayload {
    #[serde(rename = "Action")]
    action: &'static str,
    #[serde(rename = "FanCtrl")]
    fan_ctrl: &'static str,
}

#[derive(Serialize)]
struct LcActionPayload {
    #[serde(rename = "Action")]
    action: &'static str,
}

#[derive(Serialize)]
struct LcDeviceMacPayload<'a> {
    #[serde(rename = "Action")]
    action: &'static str,
    #[serde(rename = "DeviceMac")]
    device_mac: &'a str,
}

/// Publish `LC_PumpCtrl` with `PumpCtrl` as a JSON string, or BLE-fallback when MQTT LC is down.
pub async fn apply_lc_pump<T: MqttTransport>(
    transport: &mut T,
    ble: &mut FakeBle,
    item_support: &ItemSupport,
    mqtt_lc_connected: bool,
    index: u8,
) -> Result<(), LcError> {
    require_support(item_support)?;
    let digit = pump_digit(index)?;
    if mqtt_lc_connected {
        publish(
            transport,
            &LcPumpPayload {
                action: "LC_PumpCtrl",
                pump_ctrl: digit,
            },
        )
        .await
    } else {
        ble.write(&pump_frame(index)?);
        Ok(())
    }
}

/// Publish `LC_FanCtrl` with `FanCtrl` as a JSON string (`"0"`..=`"3"`, `"4"` auto).
pub async fn apply_lc_fan<T: MqttTransport>(
    transport: &mut T,
    ble: &mut FakeBle,
    item_support: &ItemSupport,
    mqtt_lc_connected: bool,
    index: u8,
) -> Result<(), LcError> {
    require_support(item_support)?;
    let digit = fan_digit(index)?;
    if mqtt_lc_connected {
        publish(
            transport,
            &LcFanPayload {
                action: "LC_FanCtrl",
                fan_ctrl: digit,
            },
        )
        .await
    } else {
        ble.write(&fan_frame(index)?);
        Ok(())
    }
}

/// C# `LcConnect`: `BT_LC/Control` `{ Action: "Connect" }`. Discovery, no support gate.
pub async fn apply_lc_connect<T: MqttTransport>(
    transport: &mut T,
    _item_support: &ItemSupport,
) -> Result<(), LcError> {
    publish(transport, &LcActionPayload { action: "Connect" }).await
}

/// C# `LcDisconnect`: `{ Action: "Disconnect" }`.
pub async fn apply_lc_disconnect<T: MqttTransport>(
    transport: &mut T,
    item_support: &ItemSupport,
) -> Result<(), LcError> {
    require_support(item_support)?;
    publish(
        transport,
        &LcActionPayload {
            action: "Disconnect",
        },
    )
    .await
}

/// C# `DeviceMacSetting`: `{ Action: "DeviceMacSetting", DeviceMac: <string> }`.
pub async fn apply_lc_device_mac<T: MqttTransport>(
    transport: &mut T,
    item_support: &ItemSupport,
    mac: &str,
) -> Result<(), LcError> {
    require_support(item_support)?;
    if mac.trim().is_empty() {
        return Err(LcError::DeviceMacEmpty);
    }
    publish(
        transport,
        &LcDeviceMacPayload {
            action: "DeviceMacSetting",
            device_mac: mac,
        },
    )
    .await
}

/// C# `LcClearMac`: `{ Action: "ClearDevMAC" }`.
pub async fn apply_lc_clear_mac<T: MqttTransport>(
    transport: &mut T,
    item_support: &ItemSupport,
) -> Result<(), LcError> {
    require_support(item_support)?;
    publish(
        transport,
        &LcActionPayload {
            action: "ClearDevMAC",
        },
    )
    .await
}

/// C# `LcInputWater`: `{ Action: "InputWater" }`.
pub async fn apply_lc_input_water<T: MqttTransport>(
    transport: &mut T,
    item_support: &ItemSupport,
) -> Result<(), LcError> {
    require_support(item_support)?;
    publish(
        transport,
        &LcActionPayload {
            action: "InputWater",
        },
    )
    .await
}

fn require_support(item_support: &ItemSupport) -> Result<(), LcError> {
    if FeatureMatrix::from_values(item_support).is_supported(FeatureBit::LiquidCooling) {
        Ok(())
    } else {
        Err(LcError::NotSupported)
    }
}

const fn pump_digit(index: u8) -> Result<&'static str, LcError> {
    match index {
        0 => Ok("0"),
        1 => Ok("1"),
        2 => Ok("2"),
        other => Err(LcError::PumpIndex(other)),
    }
}

const fn fan_digit(index: u8) -> Result<&'static str, LcError> {
    match index {
        0 => Ok("0"),
        1 => Ok("1"),
        2 => Ok("2"),
        3 => Ok("3"),
        4 => Ok("4"),
        other => Err(LcError::FanIndex(other)),
    }
}

const fn pump_frame(index: u8) -> Result<[u8; 8], LcError> {
    let (duty, voltage) = match index {
        0 => (45_u8, BLE_PUMP_V7),
        1 => (60, BLE_PUMP_V8),
        2 => (90, BLE_PUMP_V11),
        other => return Err(LcError::PumpIndex(other)),
    };
    Ok([0xFE, BLE_CMD_PUMP, 1, duty, voltage, 0, 0, 0xEF])
}

const fn fan_frame(index: u8) -> Result<[u8; 8], LcError> {
    let duty = match index {
        0 => 40_u8,
        1 => 50,
        2 => 60,
        3 => 90,
        4 => 100,
        other => return Err(LcError::FanIndex(other)),
    };
    Ok([0xFE, BLE_CMD_FAN, 1, duty, 0, 0, 0, 0xEF])
}

async fn publish<T: MqttTransport>(
    transport: &mut T,
    payload: &impl Serialize,
) -> Result<(), LcError> {
    let bytes = serde_json::to_vec(payload)?;
    transport.publish(topics::BT_LC_CONTROL, &bytes).await?;
    Ok(())
}

impl From<LcError> for HostError {
    fn from(err: LcError) -> Self {
        match err {
            LcError::Mqtt(inner) => Self::Mqtt(inner),
            LcError::Json(inner) => Self::Json(inner),
            other => Self::LcDenied(other.to_string()),
        }
    }
}

impl Backend {
    pub async fn set_lc_pump(&mut self, index: u8) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                apply_lc_pump(
                    &mut state.broker,
                    &mut state.ble,
                    &state.item_support,
                    !state.lc_mqtt_disconnected,
                    index,
                )
                .await?;
                Ok(())
            }
            Self::Real { state } => {
                let mut ble = crate::hw_ble::FakeBle::new();
                apply_lc_pump(
                    &mut state.client,
                    &mut ble,
                    &state.item_support,
                    true,
                    index,
                )
                .await?;
                Ok(())
            }
        }
    }

    pub async fn set_lc_fan(&mut self, index: u8) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                apply_lc_fan(
                    &mut state.broker,
                    &mut state.ble,
                    &state.item_support,
                    !state.lc_mqtt_disconnected,
                    index,
                )
                .await?;
                Ok(())
            }
            Self::Real { state } => {
                let mut ble = crate::hw_ble::FakeBle::new();
                apply_lc_fan(
                    &mut state.client,
                    &mut ble,
                    &state.item_support,
                    true,
                    index,
                )
                .await?;
                Ok(())
            }
        }
    }
}
