use tauri::{AppHandle, Manager, State};

use crate::hw_backend::AppState;
use crate::hw_gcu_coexistence::{self, GcuCoexistenceStatus};
use crate::hw_prefs;

#[tauri::command]
pub async fn app_quit(app: AppHandle) -> Result<(), String> {
    let state = app.state::<AppState>();
    {
        let mut backend = state.backend.lock().await;
        let _ = backend.shutdown().await;
    }
    app.exit(0);
    Ok(())
}

#[tauri::command]
pub fn set_theme_mode(mode: String, app: AppHandle) -> Result<(), String> {
    let dir = app.path().app_config_dir().map_err(|err| err.to_string())?;
    hw_prefs::set_theme_mode(&dir, &mode).map_err(|err| err.to_string())
}

#[tauri::command]
pub fn set_ui_language(code: String, app: AppHandle) -> Result<(), String> {
    let dir = app.path().app_config_dir().map_err(|err| err.to_string())?;
    hw_prefs::set_ui_language(&dir, &code).map_err(|err| err.to_string())?;
    Ok(())
}

#[tauri::command]
pub async fn set_lighting_policy(
    off_on_battery: bool,
    idle_seconds: i32,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_lighting_policy(off_on_battery, idle_seconds)
        .await
        .map_err(|err| err.to_string())
}

#[tauri::command]
pub fn gcu_coexistence_status() -> Result<GcuCoexistenceStatus, String> {
    Ok(hw_gcu_coexistence::probe_status())
}
