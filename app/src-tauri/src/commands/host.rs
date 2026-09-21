use tauri::{AppHandle, State};

use crate::hw_backend::AppState;

#[tauri::command]
pub fn app_quit(app: AppHandle) {
    app.exit(0);
}

#[tauri::command]
pub async fn set_official_isolation(
    isolate: bool,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_official_isolation(isolate)
        .map_err(|err| err.to_string())
}

#[tauri::command]
pub fn set_theme_mode(mode: String) -> Result<(), String> {
    let _ = mode;
    Err("not implemented".to_string())
}

#[tauri::command]
pub fn set_ui_language(code: String) -> Result<(), String> {
    let _ = code;
    Err("not implemented".to_string())
}

#[tauri::command]
pub fn set_project_id(id: String) -> Result<(), String> {
    let _ = id;
    Err("not implemented".to_string())
}

#[tauri::command]
pub fn set_lighting_policy(off_on_battery: bool, idle_seconds: i32) -> Result<(), String> {
    let _ = (off_on_battery, idle_seconds);
    Err("not implemented".to_string())
}
