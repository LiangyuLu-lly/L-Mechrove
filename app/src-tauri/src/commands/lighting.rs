use tauri::State;

use crate::hw_backend::AppState;

#[tauri::command]
pub async fn set_light_effect(
    channel: String,
    effect: String,
    light: Option<String>,
    speed: Option<String>,
    color: Option<String>,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_light_params(
            &channel,
            &effect,
            light.as_deref(),
            speed.as_deref(),
            color.as_deref(),
        )
        .await
        .map_err(|err| err.to_string())
}

#[tauri::command]
pub async fn set_light_power(
    channel: String,
    on: bool,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_light_power(&channel, on)
        .await
        .map_err(|err| err.to_string())
}
