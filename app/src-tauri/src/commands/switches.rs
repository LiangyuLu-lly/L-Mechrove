use tauri::State;

use crate::hw_backend::AppState;

#[tauri::command]
pub async fn set_quick_switch(
    key: String,
    on: bool,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_quick_switch(&key, on)
        .await
        .map_err(|err| err.to_string())
}

#[tauri::command]
pub async fn set_monitor_off(state: State<'_, AppState>) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend.set_monitor_off().map_err(|err| err.to_string())
}
