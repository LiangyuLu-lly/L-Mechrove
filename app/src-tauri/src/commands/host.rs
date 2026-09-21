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
