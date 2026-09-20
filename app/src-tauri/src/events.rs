//! Host → UI events. No MQTT secrets. Overlay HUD consumes SNAPSHOT only.

use capabilities::LightingVisibility;
use tauri::{AppHandle, Emitter};

use crate::hw_backend::MqttStatus;
use crate::hw_snapshot::HwSnapshot;

pub const MQTT_STATUS: &str = "mqtt_status";
pub const CAPABILITIES: &str = "capabilities";
pub const SNAPSHOT: &str = "hw_snapshot";

pub fn emit_mqtt_status(app: &AppHandle, status: MqttStatus) -> Result<(), tauri::Error> {
    app.emit(MQTT_STATUS, status)
}

pub fn emit_capabilities(
    app: &AppHandle,
    lighting: LightingVisibility,
) -> Result<(), tauri::Error> {
    app.emit(CAPABILITIES, lighting)
}

pub fn emit_snapshot(app: &AppHandle, snapshot: &HwSnapshot) -> Result<(), tauri::Error> {
    app.emit(SNAPSHOT, snapshot)
}
