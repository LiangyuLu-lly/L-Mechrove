use tauri::State;

use crate::hw_backend::AppState;

#[tauri::command]
pub async fn set_performance_mode(mode: String, state: State<'_, AppState>) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_performance_mode(&mode)
        .await
        .map_err(|err| err.to_string())
}

#[tauri::command]
pub fn open_custom_mode_window() -> Result<(), String> {
    Err("not implemented".to_string())
}

#[tauri::command]
pub fn close_custom_mode_window() -> Result<(), String> {
    Err("not implemented".to_string())
}
