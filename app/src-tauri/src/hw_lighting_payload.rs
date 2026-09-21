//! Official SetPower / SetEffectALL JSON copied from MechrevoService.cs.

use gcu_mqtt::client::MqttTransport;
use serde::Serialize;

use crate::hw_backend::HostError;
use crate::hw_lighting_cfg::ChannelCfg;

pub(crate) async fn publish_power<T: MqttTransport>(
    transport: &mut T,
    topic: &str,
    on: bool,
) -> Result<(), HostError> {
    let payload = LightPowerPayload {
        function: "SetPower",
        powerstatus: i32::from(on),
    };
    let bytes = serde_json::to_vec(&payload)?;
    transport.publish(topic, &bytes).await?;
    Ok(())
}

pub(crate) async fn publish_effect<T: MqttTransport>(
    transport: &mut T,
    topic: &str,
    cfg: &ChannelCfg,
) -> Result<(), HostError> {
    let payload = LightEffectPayload {
        function: "SetEffectALL",
        mode: "Lighting",
        speed: &cfg.speed,
        light: &cfg.light,
        effect: &cfg.effect,
        direction: "None",
        nv_save: "SAVE",
        color: color_for_effect(&cfg.effect, cfg.color_argb),
    };
    let bytes = serde_json::to_vec(&payload)?;
    transport.publish(topic, &bytes).await?;
    Ok(())
}

fn effect_uses_single_color(effect: &str) -> bool {
    matches!(effect, "Single" | "Breathing" | "Impact" | "Mix")
}

fn rgb_from_argb(argb: i32) -> Rgb {
    let value = argb as u32;
    Rgb {
        r: ((value >> 16) & 0xFF) as i32,
        g: ((value >> 8) & 0xFF) as i32,
        b: (value & 0xFF) as i32,
    }
}

fn color_for_effect(effect: &str, color_argb: i32) -> LightColor {
    if effect_uses_single_color(effect) {
        LightColor {
            is_circular: true,
            color_blocks: 1,
            color_buffer: vec![rgb_from_argb(color_argb)],
        }
    } else {
        LightColor {
            is_circular: true,
            color_blocks: 7,
            color_buffer: RAINBOW.to_vec(),
        }
    }
}

#[derive(Serialize)]
struct LightPowerPayload {
    function: &'static str,
    powerstatus: i32,
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
    color_buffer: Vec<Rgb>,
}

#[derive(Clone, Copy, Serialize)]
struct Rgb {
    #[serde(rename = "R")]
    r: i32,
    #[serde(rename = "G")]
    g: i32,
    #[serde(rename = "B")]
    b: i32,
}

const RAINBOW: [Rgb; 7] = [
    Rgb { r: 255, g: 0, b: 0 },
    Rgb {
        r: 255,
        g: 165,
        b: 0,
    },
    Rgb {
        r: 255,
        g: 255,
        b: 0,
    },
    Rgb { r: 0, g: 255, b: 0 },
    Rgb {
        r: 0,
        g: 255,
        b: 255,
    },
    Rgb { r: 0, g: 0, b: 255 },
    Rgb {
        r: 255,
        g: 0,
        b: 255,
    },
];
