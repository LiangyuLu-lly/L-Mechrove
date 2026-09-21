mod battery;
mod diagnostics;
mod display;
mod fan;
mod gpu;
mod host;
mod lc;
mod lighting;
mod mode;
mod overlay;
mod snapshot;
mod switches;
mod updates;

pub use battery::set_charge_limit;
pub use diagnostics::diagnostics_export;
pub use display::{
    set_auto_refresh_rate, set_brightness, set_calibration, set_display_hz, set_local_dimming,
    set_overdrive,
};
pub use fan::{set_custom_detail, set_fan_boost, set_fan_curve};
pub use gpu::set_gpu_route;
pub use host::{
    app_quit, set_lighting_policy, set_official_isolation, set_project_id, set_theme_mode,
    set_ui_language,
};
pub use lc::{set_lc_fan, set_lc_pump};
pub use lighting::{set_light_effect, set_light_power};
pub use mode::{close_custom_mode_window, open_custom_mode_window, set_performance_mode};
pub use overlay::{overlay_set, overlay_update};
pub use snapshot::hw_snapshot;
pub use switches::{set_monitor_off, set_quick_switch};
pub use updates::{updates_check, updates_install, updates_open_page};
