use tauri::AppHandle;

#[tauri::command]
pub fn app_quit(app: AppHandle) {
    app.exit(0);
}
