//! In-process Fake GCU, or Real slot-4 `GcuClient` with live ItemSupport + inbound MQTT.

use std::cell::RefCell;
use std::path::PathBuf;
use std::sync::atomic::Ordering;

use capabilities::{DgpuGeneration, ItemSupport};
use ec_acpi::{ChargeLimit, FakeIoctl};
use gcu_mqtt::client::{GcuClient, MqttTransport};
use gcu_mqtt::fake::Recorded;
use gcu_mqtt::handshake::run_handshake;
use gcu_mqtt::topics;

use crate::hw_fake::FakeState;
use crate::hw_model;
use crate::hw_mqtt_inbound::apply_inbound;
use crate::hw_real::{MqttLoopParts, RealState};

pub use crate::hw_error::HostError;
pub use crate::hw_snapshot::{HwSnapshot, MqttStatus};

thread_local! {
    static INJECTED_EC: RefCell<Option<ChargeLimit<FakeIoctl>>> = const { RefCell::new(None) };
}

/// Clears a test-only FakeIoctl so CI never opens `\\.\ACPIDriver`.
pub struct InjectedEcGuard;

impl Drop for InjectedEcGuard {
    fn drop(&mut self) {
        INJECTED_EC.with(|slot| {
            slot.replace(None);
        });
    }
}

pub struct AppState {
    pub(crate) backend: tokio::sync::Mutex<Backend>,
}

/// `Fake` never constructs rumqttc / NativeHid / Windows IOCTL.
pub enum Backend {
    Fake { state: FakeState },
    Real { state: RealState },
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
        match self {
            Self::Fake { state } => state.profile_dir = Some(dir),
            Self::Real { state } => state.profile_dir = Some(dir),
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
                state: RealState::new(GcuClient::new(&params)),
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
            Self::Real { state } => {
                let params = crate::secrets::slot4_params()?;
                state.client = GcuClient::new(&params);
                state.mqtt_status = MqttStatus::Connecting;
                run_handshake(&mut state.client).await?;
                Ok(())
            }
        }
    }

    pub async fn set_performance_mode(&mut self, mode: &str) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                let slot = crate::hw_mode::selected_custom_slot(
                    state.profile_dir.as_deref(),
                    &mut state.custom_profile_index,
                )?;
                crate::hw_mode::apply_performance_mode(&mut state.broker, mode, slot).await?;
                if let Some(dir) = state.profile_dir.clone() {
                    crate::hw_mode_profile::ModeProfiles::new(&dir)
                        .apply(&mut state.broker, &state.item_support, mode)
                        .await?;
                }
                state.current_mode = Some(mode.to_owned());
                Ok(())
            }
            Self::Real { state } => {
                let slot = crate::hw_mode::selected_custom_slot(
                    state.profile_dir.as_deref(),
                    &mut state.custom_profile_index,
                )?;
                crate::hw_mode::apply_performance_mode(&mut state.client, mode, slot).await
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
            Self::Real { state } => {
                crate::hw_gpu::apply_gpu_route(
                    &mut state.client,
                    &state.item_support,
                    state.gpu_generation,
                    state.gpu_three_mode,
                    action,
                )
                .await
            }
        }
    }

    pub fn set_charge_limit(&mut self, percent: u8) -> Result<u8, HostError> {
        let percent = percent.clamp(ec_acpi::MIN_PERCENT, ec_acpi::MAX_PERCENT);
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                let applied = state.charge.try_set(percent)?;
                state.charge_percent = applied;
                Ok(applied)
            }
            Self::Real { state } => {
                let applied = set_real_charge_limit(percent)?;
                state.charge_percent = applied;
                Ok(applied)
            }
        }
    }

    /// C# `BatteryControl.SetBatteryLimitFull` → `MaximumPercent` (100 encodes as 0/0).
    pub fn set_charge_full(&mut self) -> Result<u8, HostError> {
        self.set_charge_limit(ec_acpi::MAX_PERCENT)
    }

    /// Install a FakeIoctl for the Real charge path. Never opens the live device.
    pub fn inject_ec_transport(transport: FakeIoctl) -> InjectedEcGuard {
        INJECTED_EC.with(|slot| {
            slot.replace(Some(ChargeLimit::new(transport)));
        });
        InjectedEcGuard
    }

    /// C# `MechrevoHw.SetBatteryProtection` Action goldens (modes 0/1/2).
    pub async fn set_battery_protection(&mut self, mode: u8) -> Result<(), HostError> {
        let action = battery_protection_action(mode)
            .ok_or_else(|| HostError::UnknownMode(mode.to_string()))?;
        let bytes = serde_json::to_vec(&serde_json::json!({ "Action": action }))?;
        self.publish_battery_protection(&bytes).await
    }

    /// C# `MechrevoHw` `{Report:"GET"}` on `BatteryProtection/Control`.
    pub async fn report_battery_protection(&mut self) -> Result<(), HostError> {
        let bytes = serde_json::to_vec(&serde_json::json!({ "Report": "GET" }))?;
        self.publish_battery_protection(&bytes).await
    }

    async fn publish_battery_protection(&mut self, bytes: &[u8]) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state
                    .broker
                    .publish(topics::BATTERY_PROTECTION_CONTROL, bytes)
                    .await?;
                Ok(())
            }
            Self::Real { state } => {
                state
                    .client
                    .publish(topics::BATTERY_PROTECTION_CONTROL, bytes)
                    .await?;
                Ok(())
            }
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
            Self::Real { state } => {
                crate::hw_lighting::apply_light_effect(
                    &mut state.client,
                    &state.item_support,
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
            Self::Real { state } => {
                crate::hw_lighting::apply_light_power(
                    &mut state.client,
                    &state.item_support,
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
            Self::Real { state } => {
                let mut store = hw_model::ProjectIdStore::new();
                store.set(id);
                state.project_id = store.as_str().to_owned();
                Ok(())
            }
        }
    }

    pub fn apply_item_support(&mut self, item: ItemSupport) {
        match self {
            Self::Fake { state } => {
                state.item_support = item;
                sync_fake_model_gate(state);
            }
            Self::Real { state } => state.item_support = item,
        }
    }

    pub fn apply_gpu_generation(&mut self, generation: DgpuGeneration) {
        match self {
            Self::Fake { state } => state.gpu_generation = generation,
            Self::Real { state } => state.gpu_generation = generation,
        }
    }

    pub fn apply_inbound(&mut self, topic: &str, payload: &[u8]) {
        match self {
            Self::Fake { state } => {
                let mut live = crate::hw_mqtt_inbound::InboundLive {
                    seen: state.seen,
                    charge_percent: state.charge_percent,
                    hz_list: state.hz_list.clone(),
                    dc_hz_seen: state.dc_hz_seen,
                    cpu_temp_c: state.cpu_temp_c,
                    gpu_temp_c: state.gpu_temp_c,
                    cpu_rpm: state.cpu_rpm,
                    gpu_rpm: state.gpu_rpm,
                    custom_profile_index: Some(state.custom_profile_index),
                    color_calibration_mode: (state.color_calibration_mode >= 1)
                        .then_some(state.color_calibration_mode),
                };
                if apply_inbound(&mut live, topic, payload) {
                    state.seen = live.seen;
                    state.charge_percent = live.charge_percent;
                    state.hz_list = live.hz_list;
                    state.dc_hz_seen = live.dc_hz_seen;
                    state.cpu_temp_c = live.cpu_temp_c;
                    state.gpu_temp_c = live.gpu_temp_c;
                    state.cpu_rpm = live.cpu_rpm;
                    state.gpu_rpm = live.gpu_rpm;
                    if let Some(index) = live.custom_profile_index {
                        state.custom_profile_index = index;
                    }
                    if let Some(mode) = live.color_calibration_mode {
                        state.color_calibration_mode = mode;
                    }
                }
            }
            Self::Real { state } => {
                let mut live = state.inbound_live();
                if apply_inbound(&mut live, topic, payload) {
                    state.apply_inbound_live(&live);
                }
            }
        }
    }

    pub fn take_mqtt_loop(&mut self) -> Option<MqttLoopParts> {
        match self {
            Self::Fake { .. } => None,
            Self::Real { state } => state.take_mqtt_loop(),
        }
    }

    pub fn set_mqtt_status(&mut self, status: MqttStatus) {
        match self {
            Self::Fake { state } => state.mqtt_status = status,
            Self::Real { state } => state.mqtt_status = status,
        }
    }

    pub fn set_theme_mode_cached(&mut self, mode: &str) {
        if let Self::Real { state } = self {
            state.theme_mode = mode.to_owned();
        }
    }

    pub fn real_custom_profile_index(&mut self) -> u8 {
        match self {
            Self::Fake { state } => {
                let _ = crate::hw_mode::selected_custom_slot(
                    state.profile_dir.as_deref(),
                    &mut state.custom_profile_index,
                );
                state.custom_profile_index
            }
            Self::Real { state } => {
                let _ = crate::hw_mode::selected_custom_slot(
                    state.profile_dir.as_deref(),
                    &mut state.custom_profile_index,
                );
                state.custom_profile_index
            }
        }
    }

    pub async fn shutdown(&mut self) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state
                    .broker
                    .publish(topics::SYSTEM_CONTROL, gcu_mqtt::client::SYSTEM_OFF_JSON)
                    .await?;
                Ok(())
            }
            Self::Real { state } => {
                state.stop.store(true, Ordering::Release);
                let publisher = state.client.publisher();
                publisher.publish_system_off().await?;
                publisher.disconnect().await?;
                Ok(())
            }
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
            Self::Fake { state } => recorded_writes(&state.charge),
            Self::Real { .. } => INJECTED_EC.with(|slot| {
                slot.borrow()
                    .as_ref()
                    .map(recorded_writes)
                    .unwrap_or_default()
            }),
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

const fn battery_protection_action(mode: u8) -> Option<&'static str> {
    match mode {
        0 => Some("PERFORMANCEDMODE"),
        1 => Some("BALANCEDMODE"),
        2 => Some("HEALTHYMODE"),
        _ => None,
    }
}

fn recorded_writes(charge: &ChargeLimit<FakeIoctl>) -> Vec<(u32, Vec<u8>)> {
    charge
        .transport()
        .calls()
        .iter()
        .filter(|call| call.code == ec_acpi::IOCTL_WRITE)
        .map(|call| (call.code, call.in_bytes.clone()))
        .collect()
}

fn set_real_charge_limit(percent: u8) -> Result<u8, HostError> {
    let injected = INJECTED_EC.with(|slot| {
        slot.borrow_mut()
            .as_mut()
            .map(|charge| charge.try_set(percent))
    });
    match injected {
        Some(result) => Ok(result?),
        None => live_ec_try_set(percent),
    }
}

fn live_ec_try_set(percent: u8) -> Result<u8, HostError> {
    #[cfg(windows)]
    {
        let mut charge = ChargeLimit::new(ec_acpi::WindowsIoctl::open()?);
        return Ok(charge.try_set(percent)?);
    }
    #[cfg(not(windows))]
    {
        let _ = percent;
        Err(HostError::RealUnavailable)
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
    cfg!(test) || fake_gcu_requested()
}

const FULL_DEV_ITEM_SUPPORT: &str = include_str!("../crates/_golden/item_support_full_dev.json");

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
