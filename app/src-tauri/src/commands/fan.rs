use tauri::State;

use crate::hw_backend::AppState;

#[tauri::command]
pub async fn set_fan_curve(
    name: String,
    ty: String,
    duties: Vec<u8>,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_fan_curve(&name, &ty, duties)
        .await
        .map_err(|err| err.to_string())
}

#[tauri::command]
pub async fn set_fan_boost(on: bool, state: State<'_, AppState>) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_fan_boost(on)
        .await
        .map_err(|err| err.to_string())
}

#[tauri::command]
pub async fn set_custom_detail(
    field: String,
    value: String,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let mut backend = state.backend.lock().await;
    backend
        .set_custom_detail(&field, &value)
        .await
        .map_err(|err| err.to_string())
}
