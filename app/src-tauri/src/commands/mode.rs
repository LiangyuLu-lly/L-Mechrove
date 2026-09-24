use tauri::{AppHandle, Manager, State};

use crate::hw_backend::AppState;

const EDITOR_MODES: &[&str] = &["office", "gaming", "turbo", "silentTurbo", "custom"];

#[tauri::command]
pub async fn set_performance_mode(mode: String, state: State<'_, AppState>) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_performance_mode(&mode)
        .await
        .map_err(|err| err.to_string())
}

#[tauri::command]
pub fn open_custom_mode_window(app: AppHandle, mode: Option<String>) -> Result<(), String> {
    let window = app
        .get_webview_window("custom-mode")
        .ok_or_else(|| "custom-mode window missing".to_string())?;
    if let Some(mode) = mode {
        if !EDITOR_MODES.contains(&mode.as_str()) {
            return Err(format!("unknown editor mode: {mode}"));
        }
        let base = window
            .url()
            .map_err(|err| format!("editor base url: {err}"))?;
        let url = base
            .join(&format!("?window=custom&mode={mode}"))
            .map_err(|err| format!("editor url: {err}"))?;
        window.navigate(url).map_err(|err| err.to_string())?;
    }
    window.show().map_err(|err| err.to_string())
}

#[tauri::command]
pub fn close_custom_mode_window(app: AppHandle) -> Result<(), String> {
    let window = app
        .get_webview_window("custom-mode")
        .ok_or_else(|| "custom-mode window missing".to_string())?;
    window.hide().map_err(|err| err.to_string())
}
