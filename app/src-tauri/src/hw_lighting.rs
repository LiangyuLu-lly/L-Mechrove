//! Lighting write path. ItemSupport fail-closed. Official MQTT SetEffectALL / SetPower.

use std::path::Path;

use capabilities::{effect_allowed, ItemSupport, LightingVisibility};
use gcu_mqtt::client::MqttTransport;
use gcu_mqtt::topics;

use crate::hw_backend::{Backend, HostError};
use crate::hw_fake::FakeState;
use crate::hw_lighting_cfg::{load_cfg, merge_cfg, persist_cfg};
use crate::hw_lighting_payload::{publish_effect, publish_power};

pub use crate::hw_lighting_cfg::LightParams;

#[path = "hw_lighting_idle.rs"]
mod hw_lighting_idle;

use hw_lighting_idle::{reconcile, suspend_decision, Decision, LightingPolicy};

#[derive(Clone, Copy)]
enum LightChannel {
    Keyboard,
    Lightbar,
    Logo,
}

impl LightChannel {
    fn parse(channel: &str) -> Result<Self, HostError> {
        match channel {
            "keyboard" => Ok(Self::Keyboard),
            "lightbar" => Ok(Self::Lightbar),
            "logo" => Ok(Self::Logo),
            other => Err(HostError::LightingDenied(other.to_owned())),
        }
    }

    const fn id(self) -> &'static str {
        match self {
            Self::Keyboard => "keyboard",
            Self::Lightbar => "lightbar",
            Self::Logo => "logo",
        }
    }

    const fn cfg_name(self) -> &'static str {
        match self {
            Self::Keyboard => "keyboard.cfg",
            Self::Lightbar => "lightbar.cfg",
            Self::Logo => "logolight.cfg",
        }
    }

    const fn ctrl_topic(self) -> &'static str {
        match self {
            Self::Keyboard => topics::KEYBOARD_CTRL,
            Self::Lightbar => topics::LIGHTBAR_CTRL,
            Self::Logo => topics::LOGO_LIGHT_CTRL,
        }
    }
}

pub async fn apply_light_power<T: MqttTransport>(
    transport: &mut T,
    item_support: &ItemSupport,
    cfg_dir: Option<&Path>,
    channel: &str,
    on: bool,
) -> Result<(), HostError> {
    let channel = LightChannel::parse(channel)?;
    ensure_channel_offered(item_support, channel)?;
    publish_power(transport, channel.ctrl_topic(), on).await?;
    if let Some(dir) = cfg_dir {
        let mut cfg = load_cfg(dir, channel.cfg_name());
        cfg.power = on;
        persist_cfg(dir, channel.cfg_name(), &cfg)?;
    }
    Ok(())
}

pub async fn apply_light_effect<T: MqttTransport>(
    transport: &mut T,
    item_support: &ItemSupport,
    cfg_dir: Option<&Path>,
    channel: &str,
    effect: &str,
    params: LightParams<'_>,
) -> Result<(), HostError> {
    let channel = LightChannel::parse(channel)?;
    let visibility = LightingVisibility::from_item_support(item_support);
    if !channel_offered(visibility, channel) {
        return Err(HostError::LightingDenied(channel.id().to_owned()));
    }
    if !effect_allowed(channel.id(), visibility.keyboard_type, effect) {
        return Err(HostError::LightingEffectDenied(effect.to_owned()));
    }
    let saved = cfg_dir.map(|dir| load_cfg(dir, channel.cfg_name()));
    let cfg = merge_cfg(saved.as_ref(), effect, params);
    if cfg.power {
        publish_effect(transport, channel.ctrl_topic(), &cfg).await?;
    }
    if let Some(dir) = cfg_dir {
        persist_cfg(dir, channel.cfg_name(), &cfg)?;
    }
    Ok(())
}

fn ensure_channel_offered(
    item_support: &ItemSupport,
    channel: LightChannel,
) -> Result<(), HostError> {
    let visibility = LightingVisibility::from_item_support(item_support);
    if channel_offered(visibility, channel) {
        Ok(())
    } else {
        Err(HostError::LightingDenied(channel.id().to_owned()))
    }
}

const fn channel_offered(visibility: LightingVisibility, channel: LightChannel) -> bool {
    match channel {
        LightChannel::Keyboard => visibility.keyboard,
        LightChannel::Lightbar => visibility.lightbar,
        LightChannel::Logo => visibility.logo,
    }
}

pub(crate) async fn publish_offered_power<T: MqttTransport>(
    transport: &mut T,
    item_support: &ItemSupport,
    on: bool,
) -> Result<(), HostError> {
    let visibility = LightingVisibility::from_item_support(item_support);
    for channel in [
        LightChannel::Keyboard,
        LightChannel::Lightbar,
        LightChannel::Logo,
    ] {
        if channel_offered(visibility, channel) {
            publish_power(transport, channel.ctrl_topic(), on).await?;
        }
    }
    Ok(())
}

/// Driver entry: C# `ReconcileLightingPowerAsync` over FakeState.
pub async fn apply_lighting_policy(
    state: &mut FakeState,
    off_on_battery: bool,
    idle_seconds: i32,
) -> Result<(), HostError> {
    reconcile(
        &mut state.broker,
        &state.item_support,
        state.on_battery,
        state.idle_ms,
        &mut state.lighting_suspended,
        LightingPolicy {
            off_on_battery,
            idle_seconds,
        },
    )
    .await
}

impl Backend {
    pub fn with_on_battery(mut self, on_battery: bool) -> Self {
        if let Self::Fake { state } = &mut self {
            state.on_battery = on_battery;
        }
        self
    }

    pub fn with_idle_ms(mut self, idle_ms: u64) -> Self {
        if let Self::Fake { state } = &mut self {
            state.idle_ms = idle_ms;
        }
        self
    }

    pub fn set_idle_ms(&mut self, idle_ms: u64) {
        if let Self::Fake { state } = self {
            state.idle_ms = idle_ms;
        }
    }

    /// C# `LightingState.ResolveIdleAction`: 0=None, 1=Suspend, 2=Restore.
    pub const fn lighting_idle_action(idle_ms: u64, timeout_secs: i32, suspended: bool) -> u8 {
        match suspend_decision(idle_ms, timeout_secs, suspended) {
            Decision::None => 0,
            Decision::Suspend => 1,
            Decision::Restore => 2,
        }
    }

    pub async fn reconcile_lighting_power(
        &mut self,
        off_on_battery: bool,
        idle_seconds: i32,
    ) -> Result<(), HostError> {
        match self {
            Self::Fake { state } => {
                state.ensure_writable()?;
                apply_lighting_policy(state, off_on_battery, idle_seconds).await
            }
            Self::Real { state } => {
                let mut suspended = hw_lighting_idle::take_real_suspended();
                let result = reconcile(
                    &mut state.client,
                    &state.item_support,
                    false,
                    0,
                    &mut suspended,
                    LightingPolicy {
                        off_on_battery,
                        idle_seconds,
                    },
                )
                .await;
                hw_lighting_idle::store_real_suspended(suspended);
                result
            }
        }
    }
}
