//! Overlay HUD window. Snapshot events only — no MQTT client, no slot-5 client.
//!
//! Findings: tray skipped — `tauri` is built without `tray-icon`; enabling it
//! would pull extra native deps. Footer 退出 uses `app_quit` instead.

use tauri::{AppHandle, Manager};

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
