//! Lighting write path. ItemSupport fail-closed. Official MQTT SetEffectALL / SetPower.

use std::path::Path;

use capabilities::{effect_allowed, ItemSupport, LightingVisibility};
use gcu_mqtt::fake::FakeBroker;
use gcu_mqtt::topics;

use crate::hw_backend::HostError;
use crate::hw_lighting_cfg::{load_cfg, merge_cfg, persist_cfg};
use crate::hw_lighting_payload::{publish_effect, publish_power};

pub use crate::hw_lighting_cfg::LightParams;

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

pub async fn apply_light_power(
    broker: &mut FakeBroker,
    item_support: &ItemSupport,
    cfg_dir: Option<&Path>,
    channel: &str,
    on: bool,
) -> Result<(), HostError> {
    let channel = LightChannel::parse(channel)?;
    ensure_channel_offered(item_support, channel)?;
    publish_power(broker, channel.ctrl_topic(), on).await?;
    if let Some(dir) = cfg_dir {
        let mut cfg = load_cfg(dir, channel.cfg_name());
        cfg.power = on;
        persist_cfg(dir, channel.cfg_name(), &cfg)?;
    }
    Ok(())
}

pub async fn apply_light_effect(
    broker: &mut FakeBroker,
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
        publish_effect(broker, channel.ctrl_topic(), &cfg).await?;
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
