use tauri::State;

use crate::hw_backend::{AppState, HwSnapshot};

#[tauri::command]
pub async fn hw_snapshot(state: State<'_, AppState>) -> Result<HwSnapshot, String> {
    let backend = state.backend.lock().await;
    Ok(backend.snapshot())
}
