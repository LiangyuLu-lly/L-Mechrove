mod commands;
mod events;
mod hw_backend;
pub mod hw_ble;
mod hw_diagnostics;
pub mod hw_display;
mod hw_error;
mod hw_fake;
pub mod hw_fan;
mod hw_gpu;
pub mod hw_isolation;
pub mod hw_lc;
mod hw_lighting;
mod hw_lighting_cfg;
mod hw_lighting_payload;
mod hw_mode;
mod hw_mode_detail;
mod hw_mode_profile;
pub mod hw_shell;
mod hw_snapshot;
pub mod hw_startup;
pub mod hw_switches;
pub mod hw_wmi;
mod secrets;
pub mod telemetry;
pub mod tray;
pub mod updates;

pub use hw_backend::Backend;
pub use hw_error::HostError;
pub use hw_gpu::apply_gpu_route;
pub use hw_snapshot::{HwSnapshot, MqttStatus};
pub use secrets::slot4_params;

use commands::{
    app_quit, close_custom_mode_window, diagnostics_export, hw_snapshot, open_custom_mode_window,
    overlay_set, overlay_update, set_auto_refresh_rate, set_brightness, set_calibration,
    set_charge_limit, set_custom_detail, set_display_hz, set_fan_boost, set_fan_curve, set_gpu_route,
    set_lc_fan, set_lc_pump, set_light_effect, set_light_power, set_lighting_policy,
    set_local_dimming, set_monitor_off, set_official_isolation, set_overdrive, set_performance_mode,
    set_project_id, set_quick_switch, set_theme_mode, set_ui_language, updates_check,
    updates_install, updates_open_page,
};
use hw_backend::{AppState, Backend as HwBackend};
use tauri::Manager;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let backend = HwBackend::from_env();
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_log::Builder::new().build())
        .manage(AppState {
            backend: tokio::sync::Mutex::new(backend),
        })
        .setup(|app| {
            tray::build_tray(app)?;
            let handle = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                let state = handle.state::<AppState>();
                let snap = {
                    let mut backend = state.backend.lock().await;
                    if let Ok(dir) = handle.path().app_config_dir() {
                        backend.set_profile_dir(dir);
                    }
                    let _started = backend.start().await;
                    backend.snapshot()
                };
                let _mqtt = events::emit_mqtt_status(&handle, snap.mqtt);
                let _caps = events::emit_capabilities(&handle, snap.lighting);
                let _snapshot = events::emit_snapshot(&handle, &snap);
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            hw_snapshot,
            set_performance_mode,
            set_charge_limit,
            set_gpu_route,
            set_light_effect,
            set_light_power,
            set_display_hz,
            set_auto_refresh_rate,
            set_brightness,
            set_calibration,
            set_overdrive,
            set_local_dimming,
            set_fan_curve,
            set_fan_boost,
            set_custom_detail,
            set_quick_switch,
            set_monitor_off,
            set_lc_pump,
            set_lc_fan,
            updates_check,
            overlay_set,
            diagnostics_export,
            app_quit,
            set_official_isolation,
            updates_install,
            updates_open_page,
            overlay_update,
            set_theme_mode,
            set_ui_language,
            set_project_id,
            set_lighting_policy,
            open_custom_mode_window,
            close_custom_mode_window
        ])
        .on_window_event(|window, event| {
            if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                if tray::hide_main_on_close(window.label()) {
                    api.prevent_close();
                    let _ = window.hide();
                }
            }
        })
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
