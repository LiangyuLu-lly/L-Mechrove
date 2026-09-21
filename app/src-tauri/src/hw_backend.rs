//! In-process Fake GCU, or Real slot-4 `GcuClient` (handshake does not poll the event loop).

use std::path::PathBuf;

use capabilities::{DgpuGeneration, ItemSupport};
use gcu_mqtt::client::GcuClient;
use gcu_mqtt::fake::Recorded;
use gcu_mqtt::handshake::run_handshake;

use crate::hw_fake::FakeState;

#[path = "hw_model.rs"]
mod hw_model;

pub use crate::hw_error::HostError;
pub use crate::hw_snapshot::{HwSnapshot, MqttStatus};

pub struct AppState {
    pub(crate) backend: tokio::sync::Mutex<Backend>,
}

/// `Fake` never constructs rumqttc / NativeHid / Windows IOCTL.
pub enum Backend {
    Fake { state: FakeState },
    Real { client: GcuClient },
}

impl Backend {
    pub fn fake(item_support: ItemSupport) -> Self {
        let mut backend = Self::Fake {
            state: FakeState::new(item_support),
        };
        if let Self::Fake { state } = &mut backend {
            sync_fake_model_gate(state);
        }
        backend
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
            state.write_override = Some(allowed);
            state.write_allowed = allowed;
        }
        self
    }

    pub fn from_env() -> Self {
        if prefer_fake() {
            fake_from_env()
        } else {
            Self::real()
        }
    }

    pub fn real() -> Self {
        match crate::secrets::slot4_params() {
            Ok(params) => Self::Real {
                client: GcuClient::new(&params),
            },
            Err(err) => unreachable!("slot4_params uses UWPClient_4: {err}"),
        }
    }

    pub async fn start(&mut self) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.mqtt_status = MqttStatus::Connecting;
                run_handshake(&mut state.broker).await?;
                state.mqtt_status = MqttStatus::Connected;
                sync_fake_model_gate(state);
                Ok(())
            }
            Self::Real { client } => {
                let params = crate::secrets::slot4_params()?;
                *client = GcuClient::new(&params);
                run_handshake(client).await?;
                Ok(())
            }
        }
    }

    pub async fn set_performance_mode(&mut self, mode: &str) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                let slot = crate::hw_mode::selected_custom_slot(state)?;
                crate::hw_mode::apply_performance_mode(&mut state.broker, mode, slot).await?;
                if let Some(dir) = state.profile_dir.clone() {
                    crate::hw_mode_profile::ModeProfiles::new(&dir)
                        .apply(&mut state.broker, &state.item_support, mode)
                        .await?;
                }
                state.current_mode = Some(mode.to_owned());
                Ok(())
            }
            Self::Real { client } => {
                let slot = gcu_mqtt::payloads::ProfileIndex::new(0)?;
                crate::hw_mode::apply_performance_mode(client, mode, slot).await
            }
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
            Self::Real { client } => {
                crate::hw_gpu::apply_gpu_route(
                    client,
                    &ItemSupport::default(),
                    DgpuGeneration::Unknown,
                    false,
                    action,
                )
                .await
            }
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
            Self::Real { .. } => Err(HostError::RealUnavailable),
        }
    }

    pub async fn set_light_effect(&mut self, channel: &str, effect: &str) -> Result<(), HostError> {
        self.set_light_params(channel, effect, None, None, None)
            .await
    }

    pub async fn set_light_params(
        &mut self,
        channel: &str,
        effect: &str,
        light: Option<&str>,
        speed: Option<&str>,
        color: Option<&str>,
    ) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                crate::hw_lighting::apply_light_effect(
                    &mut state.broker,
                    &state.item_support,
                    state.light_cfg_dir.as_deref(),
                    channel,
                    effect,
                    crate::hw_lighting::LightParams {
                        light,
                        speed,
                        color,
                    },
                )
                .await
            }
            Self::Real { client } => {
                crate::hw_lighting::apply_light_effect(
                    client,
                    &ItemSupport::default(),
                    None,
                    channel,
                    effect,
                    crate::hw_lighting::LightParams {
                        light,
                        speed,
                        color,
                    },
                )
                .await
            }
        }
    }

    pub async fn set_light_power(&mut self, channel: &str, on: bool) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                crate::hw_lighting::apply_light_power(
                    &mut state.broker,
                    &state.item_support,
                    state.light_cfg_dir.as_deref(),
                    channel,
                    on,
                )
                .await
            }
            Self::Real { client } => {
                crate::hw_lighting::apply_light_power(
                    client,
                    &ItemSupport::default(),
                    None,
                    channel,
                    on,
                )
                .await
            }
        }
    }

    pub fn set_project_id(&mut self, id: &str) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                let mut store = hw_model::ProjectIdStore::new();
                store.set(id);
                state.project_id = store.as_str().to_owned();
                sync_fake_model_gate(state);
                Ok(())
            }
            Self::Real { .. } => Err(HostError::RealUnavailable),
        }
    }

    pub fn set_official_isolation(&mut self, on: bool) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.official_isolation = Some(on);
                Ok(())
            }
            Self::Real { .. } => crate::hw_isolation::apply_real(on),
        }
    }

    pub fn recorded_official_isolation(&self) -> Option<bool> {
        match self {
            Self::Fake { state } => state.official_isolation,
            Self::Real { .. } => None,
        }
    }

    pub fn recorded_hid_feature_reports(&self) -> Vec<Vec<u8>> {
        match self {
            Self::Fake { state } => state.hid.feature_reports().to_vec(),
            Self::Real { .. } => Vec::new(),
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
            Self::Real { .. } => Vec::new(),
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
            Self::Real { .. } => Vec::new(),
        }
    }
}

fn sync_fake_model_gate(state: &mut FakeState) {
    let mqtt_connected = state.mqtt_status == MqttStatus::Connected;
    if state.write_override.is_none() {
        state.write_allowed = hw_model::is_served(mqtt_connected, &state.item_support);
    }
    state.model_reason = hw_model::model_reason(mqtt_connected, &state.item_support).to_owned();
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
    cfg!(test) || fake_gcu_requested()
}

const FULL_DEV_ITEM_SUPPORT: &str =
    include_str!("../crates/_golden/item_support_full_dev.json");

fn fake_gcu_requested() -> bool {
    matches!(std::env::var("LMECHREVO_FAKE_GCU"), Ok(ref value) if value == "1")
}

fn fake_from_env() -> Backend {
    if !fake_gcu_requested() {
        return Backend::fake(ItemSupport::default());
    }
    match std::env::var("LMECHREVO_FAKE_PROFILE") {
        Ok(name) if !name.is_empty() => {
            let json = if name == "full" {
                FULL_DEV_ITEM_SUPPORT.to_owned()
            } else {
                read_named_golden(&name).unwrap_or_else(|| "{}".to_owned())
            };
            let item = ItemSupport::parse_json(&json).unwrap_or_default();
            Backend::fake(item).with_gpu(DgpuGeneration::Gen50, true)
        }
        // Empty stays the default: a seeded full-capability machine would flip the
        // fail-closed gates and let the UI claim rows a real G16 hides.
        _ => Backend::fake(ItemSupport::default()),
    }
}

fn read_named_golden(name: &str) -> Option<String> {
    if name.contains(['/', '\\', ':']) {
        return None;
    }
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("crates/_golden")
        .join(name);
    std::fs::read_to_string(path).ok()
}
