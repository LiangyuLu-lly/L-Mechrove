use tauri::State;

use crate::hw_backend::AppState;
use crate::updates::UpdateDto;

#[tauri::command]
pub async fn updates_check(state: State<'_, AppState>) -> Result<UpdateDto, String> {
    let backend = state.backend.lock().await;
    backend.updates_check().map_err(|err| err.to_string())
}

#[tauri::command]
pub fn updates_install() -> Result<(), String> {
    Err("not implemented".to_string())
}

#[tauri::command]
pub fn updates_open_page() -> Result<(), String> {
    Err("not implemented".to_string())
}
