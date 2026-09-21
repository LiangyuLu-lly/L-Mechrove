//! Display write path. Hz/calibration/OD/LD on Setting/Control. Brightness is WMI, not MQTT.

use std::time::Duration;

use gcu_mqtt::client::MqttTransport;
use gcu_mqtt::topics;
use serde::Serialize;

use crate::hw_backend::Backend;
use crate::hw_error::HostError;
use crate::hw_wmi::FakeWmi;

const WMI_BRIGHTNESS_TIMEOUT: u32 = 1;

#[derive(Serialize)]
struct ActionPayload<'a> {
    #[serde(rename = "Action")]
    action: &'a str,
}

#[derive(Serialize)]
struct HzPayload {
    #[serde(rename = "Action")]
    action: &'static str,
    #[serde(rename = "Hz")]
    hz: String,
}

/// Latest-wins brightness coalescer. Production debounce is 120 ms; tests inject `Duration::ZERO`.
pub struct BrightnessQueue {
    debounce: Duration,
    latest: Option<u8>,
}

impl BrightnessQueue {
    pub const fn new(debounce: Duration) -> Self {
        Self {
            debounce,
            latest: None,
        }
    }

    pub fn submit(&mut self, brightness: u8) {
        self.latest = Some(brightness);
    }
}

#[derive(Serialize)]
struct DcHzPayload {
    #[serde(rename = "Action")]
    action: &'static str,
    #[serde(rename = "Enable")]
    enable: bool,
}

#[derive(Serialize)]
struct CalibrationOffPayload<'a> {
    #[serde(rename = "Action")]
    action: &'static str,
    #[serde(rename = "FileName")]
    file_name: &'a str,
}

/// C# `MechrevoService.ColorCalibrationFileName` (MechrevoService.cs:986-992).
pub const fn color_calibration_file_name(mode: i32) -> &'static str {
    match mode {
        2 => "sRGB",
        3 => "P3",
        4 => "AdobeRGB",
        _ => "Default",
    }
}

pub async fn apply_display_hz<T: MqttTransport>(
    transport: &mut T,
    hz: u32,
) -> Result<(), HostError> {
    let payload = HzPayload {
        action: "GPU_HZSETTING",
        hz: hz.to_string(),
    };
    let bytes = serde_json::to_vec(&payload)?;
    transport.publish(topics::SETTING_CONTROL, &bytes).await?;
    Ok(())
}

pub async fn apply_auto_refresh_rate<T: MqttTransport>(
    transport: &mut T,
    dc_hz_seen: bool,
    on: bool,
) -> Result<(), HostError> {
    if !dc_hz_seen {
        return Err(HostError::DisplayDenied("GPU_DC_HZ".to_owned()));
    }
    let payload = DcHzPayload {
        action: "GPU_DC_HZ",
        enable: on,
    };
    let bytes = serde_json::to_vec(&payload)?;
    transport.publish(topics::SETTING_CONTROL, &bytes).await?;
    Ok(())
}

pub async fn apply_brightness<T: MqttTransport>(
    _transport: &mut T,
    wmi: &mut FakeWmi,
    queue: &mut BrightnessQueue,
) -> Result<(), HostError> {
    let Some(brightness) = queue.latest.take() else {
        return Ok(());
    };
    if !queue.debounce.is_zero() {
        tokio::time::sleep(queue.debounce).await;
    }
    wmi.set_brightness(WMI_BRIGHTNESS_TIMEOUT, brightness);
    Ok(())
}

pub async fn apply_calibration<T: MqttTransport>(
    transport: &mut T,
    action: &str,
    hdr_on: bool,
    file_name: &str,
) -> Result<(), HostError> {
    if hdr_on {
        return Err(HostError::DisplayDenied(action.to_owned()));
    }
    if action == "COLOR_CALIBRATION_OFF" {
        let payload = CalibrationOffPayload {
            action: "COLOR_CALIBRATION_OFF",
            file_name,
        };
        let bytes = serde_json::to_vec(&payload)?;
        transport.publish(topics::SETTING_CONTROL, &bytes).await?;
        return Ok(());
    }
    publish_action(transport, action).await
}

pub async fn apply_direct_connect_restart<T: MqttTransport>(
    transport: &mut T,
    delay: Duration,
) -> Result<(), HostError> {
    // Vendor waits 800 ms before DGPU_DIRECT_CONNECT_RESTART (MechrevoService.cs:1113, CCUWinUI:86511-86515).
    // Timing is not a unit-test claim; the delay is a no-op under cfg!(test).
    let delay = if cfg!(test) { Duration::ZERO } else { delay };
    if !delay.is_zero() {
        tokio::time::sleep(delay).await;
    }
    publish_action(transport, "DGPU_DIRECT_CONNECT_RESTART").await
}

pub async fn apply_overdrive<T: MqttTransport>(
    transport: &mut T,
    on: bool,
) -> Result<(), HostError> {
    let action = if on {
        "LCDOverdrive_ON"
    } else {
        "LCDOverdrive_OFF"
    };
    publish_action(transport, action).await
}

pub async fn apply_local_dimming<T: MqttTransport>(
    transport: &mut T,
    on: bool,
) -> Result<(), HostError> {
    let action = if on {
        "LOCALDIMMING_ON"
    } else {
        "LOCALDIMMING_OFF"
    };
    publish_action(transport, action).await
}

async fn publish_action<T: MqttTransport>(
    transport: &mut T,
    action: &str,
) -> Result<(), HostError> {
    let bytes = serde_json::to_vec(&ActionPayload { action })?;
    transport.publish(topics::SETTING_CONTROL, &bytes).await?;
    Ok(())
}

impl Backend {
    pub fn with_brightness_debounce(mut self, debounce: Duration) -> Self {
        if let Self::Fake { state } = &mut self {
            state.brightness_queue = BrightnessQueue::new(debounce);
        }
        self
    }

    pub fn with_dc_hz_seen(mut self, seen: bool) -> Self {
        if let Self::Fake { state } = &mut self {
            state.dc_hz_seen = seen;
        }
        self
    }

    pub async fn set_auto_refresh_rate(&mut self, on: bool) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                let seen = state.dc_hz_seen;
                apply_auto_refresh_rate(&mut state.broker, seen, on).await
            }
            Self::Real { state } => {
                apply_auto_refresh_rate(&mut state.client, state.dc_hz_seen, on).await
            }
        }
    }

    pub async fn set_display_hz(&mut self, hz: &str) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                let parsed = hz
                    .parse::<u32>()
                    .map_err(|_| HostError::DisplayDenied(hz.to_owned()))?;
                apply_display_hz(&mut state.broker, parsed).await
            }
            Self::Real { state } => {
                let parsed = hz
                    .parse::<u32>()
                    .map_err(|_| HostError::DisplayDenied(hz.to_owned()))?;
                apply_display_hz(&mut state.client, parsed).await
            }
        }
    }

    pub async fn set_brightness(&mut self, percent: u8) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                state.brightness_queue.submit(percent);
                apply_brightness(
                    &mut state.broker,
                    &mut state.wmi,
                    &mut state.brightness_queue,
                )
                .await
            }
            Self::Real { .. } => Err(HostError::RealUnavailable),
        }
    }

    pub async fn set_calibration(&mut self, mode: &str) -> Result<(), HostError> {
        let file_name = color_calibration_file_name(0);
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                apply_calibration(&mut state.broker, mode, state.hdr_on, file_name).await
            }
            Self::Real { state } => {
                apply_calibration(&mut state.client, mode, state.hdr_on, file_name).await
            }
        }
    }

    pub async fn set_overdrive(&mut self, on: bool) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                apply_overdrive(&mut state.broker, on).await
            }
            Self::Real { state } => apply_overdrive(&mut state.client, on).await,
        }
    }

    pub async fn set_local_dimming(&mut self, on: bool) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                apply_local_dimming(&mut state.broker, on).await
            }
            Self::Real { state } => apply_local_dimming(&mut state.client, on).await,
        }
    }
}
