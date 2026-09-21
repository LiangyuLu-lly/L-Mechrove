use tauri::State;

use crate::hw_backend::AppState;

#[tauri::command]
pub async fn set_charge_limit(percent: u8, state: State<'_, AppState>) -> Result<u8, String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_charge_limit(percent)
        .map_err(|err| err.to_string())
}

#[tauri::command]
pub async fn set_charge_full(state: State<'_, AppState>) -> Result<u8, String> {
    let mut backend = state.backend.lock().await;
    backend.set_charge_full().map_err(|err| err.to_string())
}
