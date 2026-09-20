use tauri::State;

use crate::hw_backend::AppState;

#[tauri::command]
pub async fn set_lc_pump(index: u8, state: State<'_, AppState>) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_lc_pump(index)
        .await
        .map_err(|err| err.to_string())
}

#[tauri::command]
pub async fn set_lc_fan(index: u8, state: State<'_, AppState>) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_lc_fan(index)
        .await
        .map_err(|err| err.to_string())
}
