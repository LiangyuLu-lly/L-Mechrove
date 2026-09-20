//! In-process Fake GCU, or a Real stub that does not open 13688.

use std::path::PathBuf;

use capabilities::{DgpuGeneration, ItemSupport};
use gcu_mqtt::fake::Recorded;
use gcu_mqtt::handshake::run_handshake;

use crate::hw_fake::FakeState;

pub use crate::hw_error::HostError;
pub use crate::hw_snapshot::{HwSnapshot, MqttStatus};

pub struct AppState {
    pub(crate) backend: tokio::sync::Mutex<Backend>,
}

/// `Fake` never constructs rumqttc / NativeHid / Windows IOCTL.
pub enum Backend {
    Fake { state: FakeState },
    Real,
}

impl Backend {
    pub fn fake(item_support: ItemSupport) -> Self {
        Self::Fake {
            state: FakeState::new(item_support),
        }
    }

    pub fn fake_from_json(json: &str) -> Result<Self, HostError> {
        Ok(Self::fake(ItemSupport::parse_json(json)?))
    }

    pub fn with_gpu(mut self, generation: DgpuGeneration, three_mode: bool) -> Self {
        if let Self::Fake { state } = &mut self {
            state.gpu_generation = generation;
            state.gpu_three_mode = three_mode;
        }
        self
    }

    pub fn with_light_cfg_dir(mut self, dir: PathBuf) -> Self {
        if let Self::Fake { state } = &mut self {
            state.light_cfg_dir = Some(dir);
        }
        self
    }

    pub fn with_profile_dir(mut self, dir: PathBuf) -> Self {
        self.set_profile_dir(dir);
        self
    }

    pub fn set_profile_dir(&mut self, dir: PathBuf) {
        if let Self::Fake { state } = self {
            state.profile_dir = Some(dir);
        }
    }

    pub fn with_write_allowed(mut self, allowed: bool) -> Self {
        if let Self::Fake { state } = &mut self {
            state.write_allowed = allowed;
        }
        self
    }

    pub fn from_env() -> Self {
        if prefer_fake() {
            Self::fake(ItemSupport::default())
        } else {
            Self::Real
        }
    }

    pub async fn start(&mut self) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.mqtt_status = MqttStatus::Connecting;
                run_handshake(&mut state.broker).await?;
                state.mqtt_status = MqttStatus::Connected;
                Ok(())
            }
            Self::Real => {
                let _params = crate::secrets::slot4_params()?;
                Err(HostError::RealUnavailable)
            }
        }
    }

    pub async fn set_performance_mode(&mut self, mode: &str) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                crate::hw_mode::apply_performance_mode(&mut state.broker, mode).await?;
                if let Some(dir) = state.profile_dir.clone() {
                    crate::hw_mode_profile::ModeProfiles::new(&dir)
                        .apply(&mut state.broker, &state.item_support, mode)
                        .await?;
                }
                state.current_mode = Some(mode.to_owned());
                Ok(())
            }
            Self::Real => Err(HostError::RealUnavailable),
        }
    }

    pub async fn set_gpu_route(&mut self, action: &str) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                crate::hw_gpu::apply_gpu_route(
                    &mut state.broker,
                    &state.item_support,
                    state.gpu_generation,
                    state.gpu_three_mode,
                    action,
                )
                .await
            }
            Self::Real => Err(HostError::RealUnavailable),
        }
    }

    pub fn set_charge_limit(&mut self, percent: u8) -> Result<u8, HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                let applied = state.charge.try_set(percent)?;
                state.charge_percent = applied;
                Ok(applied)
            }
            Self::Real => Err(HostError::RealUnavailable),
        }
    }

    pub async fn set_light_effect(&mut self, channel: &str, effect: &str) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                crate::hw_lighting::apply_light_effect(
                    &mut state.broker,
                    &state.item_support,
                    state.light_cfg_dir.as_deref(),
                    channel,
                    effect,
                )
                .await
            }
            Self::Real => Err(HostError::RealUnavailable),
        }
    }

    pub fn recorded_hid_feature_reports(&self) -> Vec<Vec<u8>> {
        match self {
            Self::Fake { state } => state.hid.feature_reports().to_vec(),
            Self::Real => Vec::new(),
        }
    }

    pub fn recorded_ec_writes(&self) -> Vec<(u32, Vec<u8>)> {
        match self {
            Self::Fake { state } => state
                .charge
                .transport()
                .calls()
                .iter()
                .filter(|call| call.code == ec_acpi::IOCTL_WRITE)
                .map(|call| (call.code, call.in_bytes.clone()))
                .collect(),
            Self::Real => Vec::new(),
        }
    }

    pub fn recorded_publishes(&self) -> Vec<(String, serde_json::Value)> {
        match self {
            Self::Fake { state } => state
                .broker
                .recorded()
                .iter()
                .filter_map(publish_json)
                .collect(),
            Self::Real => Vec::new(),
        }
    }
}

fn publish_json(item: &Recorded) -> Option<(String, serde_json::Value)> {
    match item {
        Recorded::Publish { topic, payload } => {
            let value = serde_json::from_slice(payload).ok()?;
            Some((topic.clone(), value))
        }
        Recorded::Subscribe(_) => None,
    }
}

fn prefer_fake() -> bool {
    cfg!(test) || matches!(std::env::var("LMECHREVO_FAKE_GCU"), Ok(ref value) if value == "1")
}
