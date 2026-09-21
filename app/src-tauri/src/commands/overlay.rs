//! Overlay HUD window. Snapshot events only — no MQTT client, no slot-5 client.

use tauri::{AppHandle, Manager};

#[path = "../overlay_state.rs"]
mod overlay_state;

use overlay_state::{overlay_update as apply_overlay_update, OverlayStore};

#[tauri::command]
pub fn overlay_set(on: bool, app: AppHandle) -> Result<(), String> {
    let hud = app
        .get_webview_window("hud")
        .ok_or_else(|| "hud window missing".to_string())?;
    if on {
        hud.show().map_err(|err| err.to_string())
    } else {
        hud.hide().map_err(|err| err.to_string())
    }
}

#[tauri::command]
pub fn overlay_update(prefs: serde_json::Value, app: AppHandle) -> Result<(), String> {
    let dir = app.path().app_config_dir().map_err(|err| err.to_string())?;
    let mut store = OverlayStore::load(&dir).map_err(|err| err.to_string())?;
    apply_overlay_update(&mut store, &prefs).map_err(|err| err.to_string())?;
    store.save(&dir).map_err(|err| err.to_string())
}
