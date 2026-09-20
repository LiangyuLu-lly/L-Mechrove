use tauri::State;

use crate::hw_backend::AppState;

#[tauri::command]
pub async fn diagnostics_export(state: State<'_, AppState>) -> Result<String, String> {
    let backend = state.backend.lock().await;
    let dir = std::env::temp_dir().join(format!(
        "lmechrevo-diag-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_millis())
            .unwrap_or(0)
    ));
    backend
        .export_diagnostics(&dir)
        .map(|path| path.to_string_lossy().into_owned())
        .map_err(|err| err.to_string())
}
