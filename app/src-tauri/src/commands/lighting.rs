use tauri::State;

use crate::hw_backend::AppState;

#[tauri::command]
pub async fn set_light_effect(
    channel: String,
    effect: String,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_light_effect(&channel, &effect)
        .await
        .map_err(|err| err.to_string())
}
