//! Overlay host state. Persist keys match HardwareOverlay.cs; no MQTT.

use std::collections::BTreeMap;
use std::path::Path;

use serde_json::Value;

pub const MIN_SCALE_PERCENT: i32 = 35;
pub const MAX_SCALE_PERCENT: i32 = 300;
pub const SCALE_STEP_PERCENT: i32 = 10;
pub const MARGIN_FROM_EDGE: i32 = 10;
const RESTORE_CLAMP: i32 = 5;
const FILE_NAME: &str = "overlay.json";

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(i32)]
pub enum OverlayMode {
    Default = 0,
    Light = 1,
    Full = 2,
    Complete = 3,
}

impl OverlayMode {
    pub const fn from_stored(value: i32) -> Self {
        match value {
            1 => Self::Light,
            2 => Self::Full,
            3 => Self::Complete,
            _ => Self::Default,
        }
    }

    pub const fn cycle(self) -> Self {
        match self {
            Self::Light => Self::Default,
            Self::Default => Self::Full,
            Self::Full => Self::Complete,
            Self::Complete => Self::Light,
        }
    }

    pub const fn to_i32(self) -> i32 {
        match self {
            Self::Default => 0,
            Self::Light => 1,
            Self::Full => 2,
            Self::Complete => 3,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Point {
    pub x: i32,
    pub y: i32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ScreenRect {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Size {
    pub width: i32,
    pub height: i32,
}

pub const fn clamp_scale(raw: i32) -> i32 {
    if raw <= MIN_SCALE_PERCENT {
        return MIN_SCALE_PERCENT;
    }
    if raw >= MAX_SCALE_PERCENT {
        return MAX_SCALE_PERCENT;
    }
    let rounded = ((raw + SCALE_STEP_PERCENT / 2) / SCALE_STEP_PERCENT) * SCALE_STEP_PERCENT;
    clamp_i32(rounded, MIN_SCALE_PERCENT, MAX_SCALE_PERCENT)
}

pub const fn anchor_from_center(center: Point, screen: ScreenRect) -> i32 {
    let is_right = center.x > screen.x + screen.width / 2;
    let is_bottom = center.y > screen.y + screen.height / 2;
    (if is_bottom { 2 } else { 0 }) | (if is_right { 1 } else { 0 })
}

const fn clamp_i32(value: i32, lo: i32, hi: i32) -> i32 {
    if value < lo {
        lo
    } else if value > hi {
        hi
    } else {
        value
    }
}

pub const fn restore_position(
    anchor: i32,
    offset_x: i32,
    offset_y: i32,
    screen: ScreenRect,
    size: Size,
) -> Point {
    if anchor < 0 {
        return Point {
            x: screen.x + MARGIN_FROM_EDGE,
            y: screen.y + MARGIN_FROM_EDGE,
        };
    }
    let is_right = (anchor & 1) != 0;
    let is_bottom = (anchor & 2) != 0;
    let x = if is_right {
        screen.x + screen.width - size.width - offset_x
    } else {
        screen.x + offset_x
    };
    let y = if is_bottom {
        screen.y + screen.height - size.height - offset_y
    } else {
        screen.y + offset_y
    };
    Point {
        x: clamp_i32(
            x,
            screen.x + RESTORE_CLAMP,
            screen.x + screen.width - size.width - RESTORE_CLAMP,
        ),
        y: clamp_i32(
            y,
            screen.y + RESTORE_CLAMP,
            screen.y + screen.height - size.height - RESTORE_CLAMP,
        ),
    }
}

fn exe_stem(name: &str) -> &str {
    let file = name.rsplit(['/', '\\']).next().unwrap_or(name);
    let bytes = file.as_bytes();
    if bytes.len() >= 4 && bytes[bytes.len() - 4..].eq_ignore_ascii_case(b".exe") {
        &file[..file.len() - 4]
    } else {
        file
    }
}

pub fn is_game_foreground(exe: &str, allowlist: &[&str]) -> bool {
    let needle = exe_stem(exe);
    allowlist
        .iter()
        .any(|item| exe_stem(item).eq_ignore_ascii_case(needle))
}

#[derive(Debug, thiserror::Error)]
pub enum OverlayError {
    #[error(transparent)]
    Json(#[from] serde_json::Error),
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error("overlay prefs must be a JSON object")]
    NotObject,
}

pub struct OverlayStore {
    values: BTreeMap<String, Value>,
}

impl OverlayStore {
    pub fn memory() -> Self {
        Self {
            values: BTreeMap::new(),
        }
    }

    pub fn load(dir: &Path) -> Result<Self, OverlayError> {
        match std::fs::read_to_string(dir.join(FILE_NAME)) {
            Ok(text) => {
                let mut store = Self {
                    values: serde_json::from_str(&text)?,
                };
                store.migrate_light_mode();
                Ok(store)
            }
            Err(err) if err.kind() == std::io::ErrorKind::NotFound => Ok(Self::memory()),
            Err(err) => Err(err.into()),
        }
    }

    pub fn save(&self, dir: &Path) -> Result<(), OverlayError> {
        std::fs::create_dir_all(dir)?;
        std::fs::write(
            dir.join(FILE_NAME),
            serde_json::to_string_pretty(&self.values)?,
        )?;
        Ok(())
    }

    pub fn seed_legacy_light_mode(&mut self, value: i32) {
        self.values
            .insert("overlay_light_mode".to_owned(), Value::from(value));
        self.migrate_light_mode();
    }

    fn migrate_light_mode(&mut self) {
        if self.values.contains_key("overlay_mode") {
            return;
        }
        if let Some(legacy) = self.values.get("overlay_light_mode").cloned() {
            self.values.insert("overlay_mode".to_owned(), legacy);
        }
    }

    pub fn get_i32(&self, key: &str) -> Option<i32> {
        self.values.get(key).and_then(json_i32)
    }
    pub fn get_str(&self, key: &str) -> Option<&str> {
        self.values.get(key).and_then(Value::as_str)
    }
    fn set_i32(&mut self, key: &str, value: i32) {
        self.values.insert(key.to_owned(), Value::from(value));
    }
    fn set_str(&mut self, key: &str, value: &str) {
        self.values.insert(key.to_owned(), Value::from(value));
    }
    fn set_flag(&mut self, key: &str, on: bool) {
        self.set_i32(key, i32::from(on));
    }
    pub fn mode(&self) -> OverlayMode {
        OverlayMode::from_stored(self.get_i32("overlay_mode").unwrap_or(0))
    }

    pub fn click_cycle(&mut self) -> OverlayMode {
        self.migrate_light_mode();
        let next = self.mode().cycle();
        self.set_i32("overlay_mode", next.to_i32());
        next
    }
}

fn json_i32(value: &Value) -> Option<i32> {
    match value {
        Value::Number(number) => number.as_i64().and_then(|n| i32::try_from(n).ok()),
        Value::String(text) => text.parse().ok(),
        _ => None,
    }
}

fn json_bool(value: &Value) -> Option<bool> {
    value.as_bool().or_else(|| json_i32(value).map(|n| n != 0))
}

fn parse_mode(value: &Value) -> Option<OverlayMode> {
    if let Some(name) = value.as_str() {
        return Some(match name {
            "light" => OverlayMode::Light,
            "default" => OverlayMode::Default,
            "full" => OverlayMode::Full,
            "complete" => OverlayMode::Complete,
            _ => return None,
        });
    }
    json_i32(value).map(OverlayMode::from_stored)
}

pub fn overlay_update(store: &mut OverlayStore, prefs: &Value) -> Result<(), OverlayError> {
    let obj = prefs.as_object().ok_or(OverlayError::NotObject)?;
    store.migrate_light_mode();
    if obj.get("cycleMode").and_then(json_bool) == Some(true) {
        store.click_cycle();
    }
    if let Some(mode) = obj.get("mode").and_then(parse_mode) {
        store.set_i32("overlay_mode", mode.to_i32());
    }
    if let Some(scale) = obj.get("scalePercent").and_then(json_i32) {
        store.set_i32("overlay_scale_percent", clamp_scale(scale));
    }
    if let Some(anchor) = obj.get("anchor").and_then(json_i32) {
        store.set_i32("overlay_anchor", anchor);
    }
    if let Some(offset_x) = obj.get("offsetX").and_then(json_i32) {
        store.set_i32("overlay_offset_x", offset_x);
    }
    if let Some(offset_y) = obj.get("offsetY").and_then(json_i32) {
        store.set_i32("overlay_offset_y", offset_y);
    }
    if let Some(screen) = obj.get("screen").and_then(Value::as_str) {
        store.set_str("overlay_screen", screen);
    }
    for (json_key, persist_key) in [
        ("gameOnly", "overlay_game_only"),
        ("showTemp", "overlay_show_temp"),
        ("showFans", "overlay_show_fans"),
        ("showPower", "overlay_show_power"),
        ("showUsage", "overlay_show_usage"),
        ("showRam", "overlay_show_ram"),
        ("showBattery", "overlay_show_battery"),
        ("names", "overlay_names"),
    ] {
        if let Some(on) = obj.get(json_key).and_then(json_bool) {
            store.set_flag(persist_key, on);
        }
    }
    if let Some(alpha) = obj.get("alpha").and_then(json_i32) {
        store.set_i32("overlay_alpha", clamp_i32(alpha, 0, 255));
    }
    if let Some(colors) = obj.get("colors").and_then(Value::as_object) {
        for (metric, hex) in colors {
            if let Some(hex) = hex.as_str() {
                store.set_str(&format!("overlay_color_{metric}"), hex);
            }
        }
    }
    Ok(())
}
