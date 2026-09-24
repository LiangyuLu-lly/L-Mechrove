mod commands;
mod events;
mod hw_backend;
pub mod hw_ble;
mod hw_ccd;
mod hw_diagnostics;
pub mod hw_display;
mod hw_error;
pub mod hw_exec_state;
mod hw_fake;
pub mod hw_fan;
pub mod hw_gcu_coexistence;
mod hw_gpu;
pub mod hw_gpu_gen;
pub mod hw_item_support_win;
pub mod hw_lc;
mod hw_lighting;
mod hw_lighting_cfg;
mod hw_lighting_inputs;
mod hw_lighting_payload;
mod hw_mode;
mod hw_mode_detail;
mod hw_mode_profile;
mod hw_model;
pub mod hw_mqtt_inbound;
mod hw_mqtt_loop;
pub mod hw_mqtt_reconnect;
mod hw_prefs;
mod hw_real;
pub mod hw_screen_blank;
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
pub use hw_lighting_inputs::LightingInputGuard;
pub use hw_snapshot::{HwSnapshot, MqttStatus};
pub use secrets::slot4_params;

use commands::{
    app_quit, close_custom_mode_window, diagnostics_export, gcu_coexistence_status, hw_snapshot,
    open_custom_mode_window,
    overlay_set, overlay_update, set_auto_refresh_rate, set_brightness, set_calibration,
    set_charge_full, set_charge_limit, set_custom_detail, set_display_hz, set_fan_boost,
    set_fan_curve, set_gpu_route, set_lc_connect, set_lc_disconnect, set_lc_fan, set_lc_pump,
    set_light_effect, set_light_power, set_lighting_policy, set_local_dimming, set_monitor_off,
    set_overdrive, set_performance_mode, set_quick_switch, set_theme_mode,
    set_ui_language, updates_check, updates_install, updates_open_page,
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
                let loop_parts = {
                    let mut backend = state.backend.lock().await;
                    if let Ok(dir) = handle.path().app_config_dir() {
                        backend.set_profile_dir(dir.clone());
                        if let Ok(prefs) = hw_prefs::load(&dir, "") {
                            backend.set_theme_mode_cached(prefs.theme_mode.as_str());
                        }
                    }
                    let _started = backend.start().await;
                    backend.take_mqtt_loop()
                };
                if loop_parts.is_some() {
                    let item =
                        tokio::task::spawn_blocking(hw_item_support_win::read_live_item_support)
                            .await
                            .unwrap_or_else(|_| capabilities::ItemSupport::default());
                    let mut backend = state.backend.lock().await;
                    backend.apply_item_support(item);
                    let generation = tokio::task::spawn_blocking(hw_gpu_gen::read_live_generation)
                        .await
                        .unwrap_or(capabilities::DgpuGeneration::Unknown);
                    backend.apply_gpu_generation(generation);
                }
                if let Some(parts) = loop_parts {
                    let poll_handle = handle.clone();
                    tauri::async_runtime::spawn(async move {
                        hw_mqtt_loop::run_mqtt_loop(parts, poll_handle).await;
                    });
                }
                let snap = {
                    let backend = state.backend.lock().await;
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
            set_charge_full,
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
            set_lc_connect,
            set_lc_disconnect,
            updates_check,
            overlay_set,
            diagnostics_export,
            app_quit,
            gcu_coexistence_status,
            updates_install,
            updates_open_page,
            overlay_update,
            set_theme_mode,
            set_ui_language,
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
