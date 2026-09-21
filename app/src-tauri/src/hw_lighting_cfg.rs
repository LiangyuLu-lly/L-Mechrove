//! Persisted Official lighting cfg (`effect/light/speed/color/power`).

use std::path::Path;

use crate::hw_backend::HostError;

pub(crate) const DEFAULT_LIGHT: &str = "4";
pub(crate) const DEFAULT_SPEED: &str = "1";
/// `Color.White.ToArgb()` in C# LightingSettingsStore.
pub(crate) const DEFAULT_COLOR_ARGB: i32 = -1;

pub(crate) struct ChannelCfg {
    pub(crate) effect: String,
    pub(crate) light: String,
    pub(crate) speed: String,
    pub(crate) color_argb: i32,
    pub(crate) power: bool,
}

impl ChannelCfg {
    pub(crate) fn default_cfg() -> Self {
        Self {
            effect: "Single".to_owned(),
            light: DEFAULT_LIGHT.to_owned(),
            speed: DEFAULT_SPEED.to_owned(),
            color_argb: DEFAULT_COLOR_ARGB,
            power: true,
        }
    }
}

pub struct LightParams<'a> {
    pub light: Option<&'a str>,
    pub speed: Option<&'a str>,
    pub color: Option<&'a str>,
}

pub(crate) fn merge_cfg(
    saved: Option<&ChannelCfg>,
    effect: &str,
    params: LightParams<'_>,
) -> ChannelCfg {
    let fallback = ChannelCfg::default_cfg();
    let base = saved.unwrap_or(&fallback);
    ChannelCfg {
        effect: effect.to_owned(),
        light: official_light(params.light).unwrap_or_else(|| base.light.clone()),
        speed: official_speed(params.speed).unwrap_or_else(|| base.speed.clone()),
        color_argb: params.color.map(parse_hex_argb).unwrap_or(base.color_argb),
        power: base.power,
    }
}

fn official_light(value: Option<&str>) -> Option<String> {
    match value {
        Some(light) if matches!(light, "0" | "1" | "2" | "3" | "4") => Some(light.to_owned()),
        _ => None,
    }
}

fn official_speed(value: Option<&str>) -> Option<String> {
    match value {
        Some(speed) if matches!(speed, "1" | "2" | "3") => Some(speed.to_owned()),
        _ => None,
    }
}

pub(crate) fn load_cfg(dir: &Path, file_name: &str) -> ChannelCfg {
    let Ok(text) = std::fs::read_to_string(dir.join(file_name)) else {
        return ChannelCfg::default_cfg();
    };
    parse_cfg(&text)
}

fn parse_cfg(text: &str) -> ChannelCfg {
    let mut cfg = ChannelCfg::default_cfg();
    for line in text.lines() {
        let Some((key, value)) = line.split_once('=') else {
            continue;
        };
        match key {
            "effect" if !value.is_empty() => cfg.effect = value.to_owned(),
            "light" if matches!(value, "0" | "1" | "2" | "3" | "4") => {
                cfg.light = value.to_owned();
            }
            "speed" if matches!(value, "1" | "2" | "3") => cfg.speed = value.to_owned(),
            "color" => {
                if let Ok(parsed) = value.parse::<i32>() {
                    cfg.color_argb = parsed;
                }
            }
            "power" => cfg.power = value != "0",
            _ => {}
        }
    }
    cfg
}

pub(crate) fn persist_cfg(dir: &Path, file_name: &str, cfg: &ChannelCfg) -> Result<(), HostError> {
    std::fs::create_dir_all(dir)?;
    let power = if cfg.power { "1" } else { "0" };
    let body = format!(
        "effect={}\nlight={}\nspeed={}\ncolor={}\npower={power}\n",
        cfg.effect, cfg.light, cfg.speed, cfg.color_argb
    );
    std::fs::write(dir.join(file_name), body)?;
    Ok(())
}

fn parse_hex_argb(color: &str) -> i32 {
    let hex = color.strip_prefix('#').unwrap_or(color);
    if hex.len() != 6 {
        return DEFAULT_COLOR_ARGB;
    }
    match u32::from_str_radix(hex, 16) {
        Ok(rgb) => (0xFF00_0000 | rgb) as i32,
        Err(_) => DEFAULT_COLOR_ARGB,
    }
}
