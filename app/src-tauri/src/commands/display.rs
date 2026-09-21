use tauri::State;

use crate::hw_backend::AppState;

#[tauri::command]
pub async fn set_auto_refresh_rate(on: bool, state: State<'_, AppState>) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_auto_refresh_rate(on)
        .await
        .map_err(|err| err.to_string())
}

#[tauri::command]
pub async fn set_display_hz(hz: String, state: State<'_, AppState>) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_display_hz(&hz)
        .await
        .map_err(|err| err.to_string())
}

#[tauri::command]
pub async fn set_brightness(percent: u8, state: State<'_, AppState>) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_brightness(percent)
        .await
        .map_err(|err| err.to_string())
}

#[tauri::command]
pub async fn set_calibration(mode: String, state: State<'_, AppState>) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_calibration(&mode)
        .await
        .map_err(|err| err.to_string())
}

#[tauri::command]
pub async fn set_overdrive(on: bool, state: State<'_, AppState>) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_overdrive(on)
        .await
        .map_err(|err| err.to_string())
}

#[tauri::command]
pub async fn set_local_dimming(on: bool, state: State<'_, AppState>) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_local_dimming(on)
        .await
        .map_err(|err| err.to_string())
}
