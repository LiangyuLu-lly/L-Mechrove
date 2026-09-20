//! Display write path. Hz/calibration/OD/LD on Setting/Control. Brightness is WMI, not MQTT.

use std::time::Duration;

use gcu_mqtt::client::MqttTransport;
use gcu_mqtt::fake::FakeBroker;
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

pub async fn apply_display_hz(broker: &mut FakeBroker, hz: u32) -> Result<(), HostError> {
    let payload = HzPayload {
        action: "GPU_HZSETTING",
        hz: hz.to_string(),
    };
    let bytes = serde_json::to_vec(&payload)?;
    broker.publish(topics::SETTING_CONTROL, &bytes).await?;
    Ok(())
}

pub async fn apply_brightness(
    _broker: &mut FakeBroker,
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

pub async fn apply_calibration(
    broker: &mut FakeBroker,
    action: &str,
    hdr_on: bool,
) -> Result<(), HostError> {
    if hdr_on {
        return Err(HostError::DisplayDenied(action.to_owned()));
    }
    publish_action(broker, action).await
}

pub async fn apply_overdrive(broker: &mut FakeBroker, on: bool) -> Result<(), HostError> {
    let action = if on {
        "LCDOverdrive_ON"
    } else {
        "LCDOverdrive_OFF"
    };
    publish_action(broker, action).await
}

pub async fn apply_local_dimming(broker: &mut FakeBroker, on: bool) -> Result<(), HostError> {
    let action = if on {
        "LOCALDIMMING_ON"
    } else {
        "LOCALDIMMING_OFF"
    };
    publish_action(broker, action).await
}

async fn publish_action(broker: &mut FakeBroker, action: &str) -> Result<(), HostError> {
    let bytes = serde_json::to_vec(&ActionPayload { action })?;
    broker.publish(topics::SETTING_CONTROL, &bytes).await?;
    Ok(())
}

impl Backend {
    pub fn with_brightness_debounce(mut self, debounce: Duration) -> Self {
        if let Self::Fake { state } = &mut self {
            state.brightness_queue = BrightnessQueue::new(debounce);
        }
        self
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
            Self::Real => Err(HostError::RealUnavailable),
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
            Self::Real => Err(HostError::RealUnavailable),
        }
    }

    pub async fn set_calibration(&mut self, mode: &str) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                apply_calibration(&mut state.broker, mode, state.hdr_on).await
            }
            Self::Real => Err(HostError::RealUnavailable),
        }
    }

    pub async fn set_overdrive(&mut self, on: bool) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                apply_overdrive(&mut state.broker, on).await
            }
            Self::Real => Err(HostError::RealUnavailable),
        }
    }

    pub async fn set_local_dimming(&mut self, on: bool) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                apply_local_dimming(&mut state.broker, on).await
            }
            Self::Real => Err(HostError::RealUnavailable),
        }
    }
}
