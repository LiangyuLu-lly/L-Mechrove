//! Lighting write path. ItemSupport fail-closed. Keyboard/lightbar/logo MQTT SetEffectALL.

use std::path::Path;

use capabilities::{effect_allowed, ItemSupport, LightingVisibility};
use gcu_mqtt::client::MqttTransport;
use gcu_mqtt::fake::FakeBroker;
use gcu_mqtt::topics;
use serde::Serialize;

use crate::hw_backend::HostError;

const DEFAULT_LIGHT: &str = "4";
const DEFAULT_SPEED: &str = "1";

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

pub async fn apply_light_effect(
    broker: &mut FakeBroker,
    item_support: &ItemSupport,
    cfg_dir: Option<&Path>,
    channel: &str,
    effect: &str,
) -> Result<(), HostError> {
    let channel = LightChannel::parse(channel)?;
    let visibility = LightingVisibility::from_item_support(item_support);
    let offered = match channel {
        LightChannel::Keyboard => visibility.keyboard,
        LightChannel::Lightbar => visibility.lightbar,
        LightChannel::Logo => visibility.logo,
    };
    if !offered {
        return Err(HostError::LightingDenied(channel.id().to_owned()));
    }
    if !effect_allowed(channel.id(), visibility.keyboard_type, effect) {
        return Err(HostError::LightingEffectDenied(effect.to_owned()));
    }
    publish_effect(broker, channel.ctrl_topic(), effect).await?;
    if let Some(dir) = cfg_dir {
        persist_cfg(dir, channel, effect)?;
    }
    Ok(())
}

async fn publish_effect(
    broker: &mut FakeBroker,
    topic: &str,
    effect: &str,
) -> Result<(), HostError> {
    let payload = LightEffectPayload {
        function: "SetEffectALL",
        mode: "Lighting",
        speed: DEFAULT_SPEED,
        light: DEFAULT_LIGHT,
        effect,
        direction: "None",
        nv_save: "SAVE",
        color: single_white(),
    };
    let bytes = serde_json::to_vec(&payload)?;
    broker.publish(topic, &bytes).await?;
    Ok(())
}

fn persist_cfg(dir: &Path, channel: LightChannel, effect: &str) -> Result<(), HostError> {
    std::fs::create_dir_all(dir)?;
    let body = format!("effect={effect}\nlight=4\nspeed=1\ncolor=-1\npower=1\n");
    std::fs::write(dir.join(channel.cfg_name()), body)?;
    Ok(())
}

#[derive(Serialize)]
struct LightEffectPayload<'a> {
    function: &'static str,
    mode: &'static str,
    speed: &'a str,
    light: &'a str,
    effect: &'a str,
    direction: &'a str,
    nv_save: &'a str,
    color: LightColor,
}

#[derive(Serialize)]
struct LightColor {
    #[serde(rename = "isCircular")]
    is_circular: bool,
    #[serde(rename = "ColorBlocks")]
    color_blocks: i32,
    #[serde(rename = "ColorBuffer")]
    color_buffer: [Rgb; 1],
}

#[derive(Serialize)]
struct Rgb {
    #[serde(rename = "R")]
    r: i32,
    #[serde(rename = "G")]
    g: i32,
    #[serde(rename = "B")]
    b: i32,
}

const fn single_white() -> LightColor {
    LightColor {
        is_circular: true,
        color_blocks: 1,
        color_buffer: [Rgb {
            r: 255,
            g: 255,
            b: 255,
        }],
    }
}
