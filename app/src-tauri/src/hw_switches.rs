//! Quick-switch CORE. SupportsQuickSwitch + SwitchQuick. No UI glue.
//! allow: SIZE_OK — per-key MQTT action map is one table; Backend glue cannot split it.

use capabilities::{ItemSupport, LightingVisibility};
use gcu_mqtt::client::MqttTransport;
use gcu_mqtt::topics;
use serde::{Deserialize, Serialize};

use crate::hw_backend::Backend;
use crate::hw_error::HostError;
use crate::hw_wmi::FakeWmi;

const CANDIDATES: &[&str] = &[
    "touchpad",
    "wifi",
    "bt",
    "webcam",
    "winkey",
    "fnkey",
    "numpad",
    "osd",
    "usb",
    "deepsleep",
    "copilot",
    "acrecovery",
    "highperf",
    "fanboost",
    "lightbar",
    "logolight",
    "touchpadtoggle",
    "singlecolorkb",
    "uni",
    "omni",
    "powerlight",
    "batterylogo",
    "gamewhitelist",
    "cpuadvperf",
    "startup",
    "taskbarautohide",
    "transparency",
    "darktheme",
    "monitoroff",
];

/// MQTT *Seen flags that gate SupportsQuickSwitch. Touchpad/OSD/USB ignore these.
#[derive(Debug, Clone, Copy, Default)]
pub struct SeenFlags {
    pub wifi: bool,
    pub bluetooth: bool,
    pub webcam: bool,
    pub win_key: bool,
    pub fn_key: bool,
    pub numpad: bool,
    pub deep_sleep: bool,
    pub copilot: bool,
    pub ac_recovery: bool,
    pub high_perf: bool,
    pub fan_boost: bool,
    pub lightbar: bool,
    pub logo_light: bool,
    pub touchpad_toggle: bool,
    pub single_color_kb: bool,
    pub uni_omni: bool,
    pub power_light: bool,
    pub battery_logo: bool,
    pub game_whitelist: bool,
    pub cpu_adv_perf: bool,
}

/// ItemSupport + Seen used by apply_quick_switch.
pub struct QuickSwitchGate<'a> {
    pub item_support: &'a ItemSupport,
    pub seen: SeenFlags,
}

/// Settings/DeviceSwitchItemStatus. Firmware key is `TochpadEnable` (missing u).
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Deserialize)]
pub struct DeviceSwitchStatus {
    #[serde(rename = "TochpadEnable")]
    pub touchpad: Option<bool>,
    #[serde(rename = "WIFIEnable")]
    pub wifi: Option<bool>,
    #[serde(rename = "BTEnable")]
    pub bt: Option<bool>,
    #[serde(rename = "WebCamEnable")]
    pub webcam: Option<bool>,
}

/// Failures from apply_quick_switch.
#[derive(Debug, thiserror::Error)]
#[non_exhaustive]
pub enum SwitchError {
    #[error("quick switch not offered: {0}")]
    NotOffered(String),
    #[error(transparent)]
    Mqtt(#[from] gcu_mqtt::client::MqttError),
    #[error(transparent)]
    Json(#[from] serde_json::Error),
}

#[derive(Serialize)]
struct ActionPayload {
    #[serde(rename = "Action")]
    action: &'static str,
}

#[derive(Serialize)]
struct GameWhitelistPayload {
    #[serde(rename = "Action")]
    action: &'static str,
    #[serde(rename = "GameWhitelistSwitch")]
    game_whitelist_switch: i32,
}

#[derive(Serialize)]
struct PowerLightPayload {
    #[serde(rename = "Action")]
    action: &'static str,
    #[serde(rename = "Brightness")]
    brightness: i32,
}

/// GCU treats a missing Brightness as 0 and turns the light off.
const POWER_LIGHT_BRIGHTNESS: i32 = 100;

/// Parse DeviceSwitchItemStatus. `TouchPadEnable` is not a key.
pub fn parse_device_switch_item_status(
    json: &str,
) -> Result<DeviceSwitchStatus, serde_json::Error> {
    serde_json::from_str(json)
}

/// Keys offered like C# `SupportsQuickSwitch`. Whisper is never in this list.
pub fn offered_quick_switches(item_support: &ItemSupport, seen: &SeenFlags) -> Vec<&'static str> {
    CANDIDATES
        .iter()
        .copied()
        .filter(|key| supports_quick_switch(key, item_support, seen))
        .collect()
}

fn supports_quick_switch(key: &str, item_support: &ItemSupport, seen: &SeenFlags) -> bool {
    match key {
        "touchpad" | "osd" | "usb" => true,
        "wifi" => seen.wifi,
        "bt" => seen.bluetooth,
        "webcam" => seen.webcam,
        "winkey" => seen.win_key,
        "fnkey" => seen.fn_key,
        "numpad" => seen.numpad,
        "deepsleep" => seen.deep_sleep,
        "copilot" => seen.copilot,
        "acrecovery" => seen.ac_recovery || item_support.is_truthy("AcRecoverySwitchSupport"),
        "highperf" => seen.high_perf,
        "fanboost" => seen.fan_boost,
        "lightbar" => LightingVisibility::from_item_support(item_support).lightbar,
        "logolight" => LightingVisibility::from_item_support(item_support).logo,
        "touchpadtoggle" => seen.touchpad_toggle,
        "singlecolorkb" => seen.single_color_kb,
        "uni" | "omni" => seen.uni_omni,
        "powerlight" => seen.power_light,
        "batterylogo" => seen.battery_logo,
        "gamewhitelist" => seen.game_whitelist,
        "cpuadvperf" => {
            seen.cpu_adv_perf
                && item_support.any_truthy(&[
                    "CPUPerformanceAndOverClockMenuSupport",
                    "OcSettingsSupport",
                    "HWOCSupport",
                ])
        }
        "startup" | "taskbarautohide" | "transparency" | "darktheme" | "monitoroff" => true,
        "whisper" => false,
        _ => false,
    }
}

/// Publish one quick switch. Uni on also publishes Omni_OFF (mutually exclusive).
pub async fn apply_quick_switch<T: MqttTransport>(
    transport: &mut T,
    gate: &QuickSwitchGate<'_>,
    key: &str,
    on: bool,
) -> Result<(), SwitchError> {
    if !supports_quick_switch(key, gate.item_support, &gate.seen) {
        return Err(SwitchError::NotOffered(key.to_owned()));
    }
    if key == "monitoroff" {
        return Ok(());
    }
    let confirm_topic = match key {
        "gamewhitelist" | "cpuadvperf" | "fanboost" => topics::FAN_CONTROL,
        _ => topics::SETTING_CONTROL,
    };
    match key {
        "gamewhitelist" => {
            publish_json(
                transport,
                topics::FAN_CONTROL,
                &GameWhitelistPayload {
                    action: "OPERATING_GAME_WHITE_LIST",
                    game_whitelist_switch: i32::from(on),
                },
            )
            .await?;
        }
        "cpuadvperf" => publish_cpu_adv_perf(transport, on).await?,
        "fanboost" => {
            let action = if on { "FAN_BOOST_ON" } else { "FAN_BOOST_OFF" };
            publish_json(transport, topics::FAN_CONTROL, &ActionPayload { action }).await?;
        }
        "deepsleep" => publish_deepsleep(transport, on).await?,
        "powerlight" => publish_power_light(transport, on).await?,
        "uni" if on => publish_uni_omni_on(transport, "Omni_OFF", "Uni_ON").await?,
        "omni" if on => publish_uni_omni_on(transport, "Uni_OFF", "Omni_ON").await?,
        _ => {
            let action =
                setting_action(key, on).ok_or_else(|| SwitchError::NotOffered(key.to_owned()))?;
            publish_action(transport, action).await?;
        }
    }
    confirm_getstatus(transport, confirm_topic).await
}

fn setting_action(key: &str, on: bool) -> Option<&'static str> {
    let (on_action, off_action) = match key {
        "touchpad" => ("TOUCHPAD_ON", "TOUCHPAD_OFF"),
        "wifi" => ("WIFI_ON", "WIFI_OFF"),
        "bt" => ("BT_ON", "BT_OFF"),
        "webcam" => ("WEBCAM_ON", "WEBCAM_OFF"),
        "winkey" => ("WINKEY_LOCK", "WINKEY_UNLOCK"),
        "fnkey" => ("FNKEY_LOCK", "FNKEY_UNLOCK"),
        "osd" => ("OSD_HIDDEN_OFF", "OSD_HIDDEN_ON"),
        "numpad" => ("NUMPAD_LOCK", "NUMPAD_UNLOCK"),
        "copilot" => ("COPILOTKEY_LOCK", "COPILOTKEY_UNLOCK"),
        "acrecovery" => ("ACRECOVERY_TOGGLE_ON", "ACRECOVERY_TOGGLE_OFF"),
        "batterylogo" => ("BATTERYLOGO_TOGGLE_ON", "BATTERYLOGO_TOGGLE_OFF"),
        "highperf" => (
            "HIGHPERFORMANCEPOWERMODE_ON",
            "HIGHPERFORMANCEPOWERMODE_OFF",
        ),
        "touchpadtoggle" => ("TOUCHPAD_TOGGLE_ON", "TOUCHPAD_TOGGLE_OFF"),
        "singlecolorkb" => (
            "SINGLE_COLOR_KBBL_STATUS_ON",
            "SINGLE_COLOR_KBBL_STATUS_OFF",
        ),
        "uni" => ("Uni_ON", "Uni_OFF"),
        "omni" => ("Omni_ON", "Omni_OFF"),
        "usb" => ("USB_CHARGER_ON", "USB_CHARGER_OFF"),
        _ => return None,
    };
    Some(if on { on_action } else { off_action })
}

async fn publish_uni_omni_on<T: MqttTransport>(
    transport: &mut T,
    partner_off: &'static str,
    self_on: &'static str,
) -> Result<(), SwitchError> {
    publish_action(transport, partner_off).await?;
    publish_action(transport, self_on).await
}

async fn publish_power_light<T: MqttTransport>(
    transport: &mut T,
    on: bool,
) -> Result<(), SwitchError> {
    let action = if on {
        "PowerLight_ON"
    } else {
        "PowerLight_OFF"
    };
    publish_json(
        transport,
        topics::SETTING_CONTROL,
        &PowerLightPayload {
            action,
            brightness: POWER_LIGHT_BRIGHTNESS,
        },
    )
    .await?;
    publish_json(
        transport,
        topics::SETTING_CONTROL,
        &PowerLightPayload {
            action: "PowerLight_Brightness",
            brightness: POWER_LIGHT_BRIGHTNESS,
        },
    )
    .await
}

/// Setting/Control PowerLight_Brightness. Brightness is a JSON number.
pub async fn apply_power_light_brightness<T: MqttTransport>(
    transport: &mut T,
    brightness: i32,
) -> Result<(), SwitchError> {
    let brightness = brightness.clamp(0, 100);
    publish_json(
        transport,
        topics::SETTING_CONTROL,
        &PowerLightPayload {
            action: "PowerLight_Brightness",
            brightness,
        },
    )
    .await?;
    confirm_getstatus(transport, topics::SETTING_CONTROL).await
}

async fn confirm_getstatus<T: MqttTransport>(
    transport: &mut T,
    topic: &str,
) -> Result<(), SwitchError> {
    publish_json(
        transport,
        topic,
        &ActionPayload {
            action: "GETSTATUS",
        },
    )
    .await
}

async fn publish_deepsleep<T: MqttTransport>(
    transport: &mut T,
    on: bool,
) -> Result<(), SwitchError> {
    let payload = if on {
        serde_json::json!({
            "Action": "DEEPSLEEP_ON",
            "Secs": "900",
        })
    } else {
        serde_json::json!({ "Action": "DEEPSLEEP_OFF" })
    };
    publish_json(transport, topics::SETTING_CONTROL, &payload).await
}

async fn publish_cpu_adv_perf<T: MqttTransport>(
    transport: &mut T,
    on: bool,
) -> Result<(), SwitchError> {
    let payload = if on {
        serde_json::json!({
            "Action": "SET_OPERATING_MODE_DETAIL",
            "CPUPerformanceAndOverClockMenuSwitch_ON": "1",
        })
    } else {
        serde_json::json!({
            "Action": "SET_OPERATING_MODE_DETAIL",
            "CPUPerformanceAndOverClockMenuSwitch_OFF": "0",
        })
    };
    publish_json(transport, topics::FAN_CONTROL, &payload).await
}

async fn publish_action<T: MqttTransport>(
    transport: &mut T,
    action: &'static str,
) -> Result<(), SwitchError> {
    publish_json(
        transport,
        topics::SETTING_CONTROL,
        &ActionPayload { action },
    )
    .await
}

async fn publish_json<T: MqttTransport>(
    transport: &mut T,
    topic: &str,
    payload: &impl Serialize,
) -> Result<(), SwitchError> {
    let bytes = serde_json::to_vec(payload)?;
    transport.publish(topic, &bytes).await?;
    Ok(())
}

/// Monitor off is WMI brightness 0, not MQTT.
pub fn apply_monitor_off(wmi: &mut FakeWmi) {
    wmi.set_brightness(1, 0);
}

impl Backend {
    pub async fn set_quick_switch(&mut self, key: &str, on: bool) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                if key == "startup" {
                    crate::hw_startup::apply_fake(&mut state.startup, on);
                    return Ok(());
                }
                if key == "monitoroff" {
                    apply_monitor_off(&mut state.wmi);
                    return Ok(());
                }
                if crate::hw_shell::apply_fake(&mut state.shell, key, on) {
                    return Ok(());
                }
                let seen = state.seen;
                apply_quick_switch(
                    &mut state.broker,
                    &QuickSwitchGate {
                        item_support: &state.item_support,
                        seen,
                    },
                    key,
                    on,
                )
                .await
                .map_err(|err| match err {
                    SwitchError::NotOffered(denied) => HostError::SwitchDenied(denied),
                    SwitchError::Mqtt(inner) => HostError::Mqtt(inner),
                    SwitchError::Json(inner) => HostError::Json(inner),
                })
            }
            Self::Real { state } => {
                if key == "startup" {
                    return crate::hw_startup::system_apply(on)
                        .map_err(|err| HostError::Io(std::io::Error::other(err.to_string())));
                }
                if key == "monitoroff" {
                    if on {
                        return crate::hw_screen_blank::dim();
                    }
                    crate::hw_screen_blank::restore();
                    return Ok(());
                }
                if crate::hw_shell::is_shell_key(key) {
                    return crate::hw_shell::system_apply(key, on)
                        .map_err(|err| HostError::Io(std::io::Error::other(err.to_string())));
                }
                let seen = state.seen;
                apply_quick_switch(
                    &mut state.client,
                    &QuickSwitchGate {
                        item_support: &state.item_support,
                        seen,
                    },
                    key,
                    on,
                )
                .await
                .map_err(|err| match err {
                    SwitchError::NotOffered(denied) => HostError::SwitchDenied(denied),
                    SwitchError::Mqtt(inner) => HostError::Mqtt(inner),
                    SwitchError::Json(inner) => HostError::Json(inner),
                })
            }
        }
    }

    pub fn set_monitor_off(&mut self) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                apply_monitor_off(&mut state.wmi);
                Ok(())
            }
            Self::Real { .. } => crate::hw_screen_blank::dim(),
        }
    }
}
