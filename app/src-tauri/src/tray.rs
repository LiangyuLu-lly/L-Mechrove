//! One core tray icon. Menu clicks reuse Backend commands, not a second MQTT path.

use tauri::menu::{CheckMenuItem, CheckMenuItemBuilder, MenuBuilder, MenuItemBuilder};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{App, AppHandle, Manager, Runtime};

use crate::hw_backend::AppState;

const ID_MODE_OFFICE: &str = "mode-office";
const ID_MODE_GAMING: &str = "mode-gaming";
const ID_MODE_SILENT_TURBO: &str = "mode-silentTurbo";
const ID_MODE_TURBO: &str = "mode-turbo";
const ID_MODE_CUSTOM: &str = "mode-custom";
const ID_SWITCH_FANBOOST: &str = "switch-fanboost";
const ID_SHOW: &str = "show";
const ID_QUIT: &str = "quit";

/// C# `SettingsForm_FormClosing`: `e.Cancel = true`; `HideAll()`.
/// Wired from `Builder::on_window_event` `CloseRequested` in `lib.rs`
/// (not `RunEvent::ExitRequested`, which would also swallow tray/footer `app.exit(0)`).
pub const fn should_hide_on_close() -> bool {
    true
}

/// Main window X hides to tray. HUD / overlay close is not intercepted.
pub fn hide_main_on_close(label: &str) -> bool {
    label == "main" && should_hide_on_close()
}

enum TrayCommand {
    Mode(&'static str),
    SwitchFanboost,
    Show,
    Quit,
}

struct TrayChecks<R: Runtime> {
    modes: Vec<CheckMenuItem<R>>,
    fanboost: Option<CheckMenuItem<R>>,
}

/// Pure menu ids for tests. silentTurbo and fanboost follow `offered`.
pub fn tray_menu_spec(offered: &[String]) -> Vec<String> {
    let mut ids = vec![ID_MODE_OFFICE.to_owned(), ID_MODE_GAMING.to_owned()];
    if offered.iter().any(|key| key == "silentTurbo") {
        ids.push(ID_MODE_SILENT_TURBO.to_owned());
    }
    ids.push(ID_MODE_TURBO.to_owned());
    ids.push(ID_MODE_CUSTOM.to_owned());
    if offered.iter().any(|key| key == "fanboost") {
        ids.push(ID_SWITCH_FANBOOST.to_owned());
    }
    ids.push(ID_SHOW.to_owned());
    ids.push(ID_QUIT.to_owned());
    ids
}

pub fn build_tray(app: &App) -> tauri::Result<()> {
    let offered = current_offered(app);
    let spec = tray_menu_spec(&offered);
    let include_silent_turbo = spec.iter().any(|id| id == ID_MODE_SILENT_TURBO);
    let include_fanboost = spec.iter().any(|id| id == ID_SWITCH_FANBOOST);

    let office = check_item(app, ID_MODE_OFFICE, "静音 (办公)", true)?;
    let gaming = check_item(app, ID_MODE_GAMING, "平衡 (游戏)", false)?;
    let silent_turbo = if include_silent_turbo {
        Some(check_item(app, ID_MODE_SILENT_TURBO, "静音狂暴", false)?)
    } else {
        None
    };
    let turbo = check_item(app, ID_MODE_TURBO, "狂暴 (增强)", false)?;
    let custom = check_item(app, ID_MODE_CUSTOM, "自定义", false)?;
    let fanboost = if include_fanboost {
        Some(check_item(app, ID_SWITCH_FANBOOST, "风扇增强", false)?)
    } else {
        None
    };
    let show = MenuItemBuilder::with_id(ID_SHOW, "显示").build(app)?;
    let quit = MenuItemBuilder::with_id(ID_QUIT, "退出").build(app)?;

    let mut menu = MenuBuilder::new(app).item(&office).item(&gaming);
    if let Some(ref silent) = silent_turbo {
        menu = menu.item(silent);
    }
    menu = menu.item(&turbo).item(&custom).separator();
    if let Some(ref fan) = fanboost {
        menu = menu.item(fan);
    }
    let menu = menu.separator().item(&show).item(&quit).build()?;

    let mut modes = vec![office, gaming];
    if let Some(silent) = silent_turbo {
        modes.push(silent);
    }
    modes.push(turbo);
    modes.push(custom);

    let checks = TrayChecks { modes, fanboost };
    let mut tray_builder = TrayIconBuilder::with_id("main")
        .menu(&menu)
        .show_menu_on_left_click(false)
        .tooltip("L-Mechrevo")
        .on_menu_event(move |app, event| {
            on_tray_command(app, event.id().as_ref(), &checks);
        })
        .on_tray_icon_event(|tray, event| match event {
            TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                ..
            } => show_main(tray.app_handle()),
            _ => {}
        });
    if let Some(icon) = app.default_window_icon().cloned() {
        tray_builder = tray_builder.icon(icon);
    }
    let tray = tray_builder.build(app)?;
    app.manage(tray);
    Ok(())
}

fn check_item<R: Runtime>(
    app: &App<R>,
    id: &str,
    text: &str,
    checked: bool,
) -> tauri::Result<CheckMenuItem<R>> {
    CheckMenuItemBuilder::with_id(id, text)
        .checked(checked)
        .build(app)
}

fn current_offered(app: &App) -> Vec<String> {
    let state = app.state::<AppState>();
    let Ok(backend) = state.backend.try_lock() else {
        return Vec::new();
    };
    let snapshot = backend.snapshot();
    let mut offered = snapshot.offered_switches;
    if snapshot.silent_turbo {
        offered.push("silentTurbo".to_owned());
    }
    offered
}

fn tray_command(id: &str) -> Option<TrayCommand> {
    match id {
        ID_MODE_OFFICE => Some(TrayCommand::Mode("office")),
        ID_MODE_GAMING => Some(TrayCommand::Mode("gaming")),
        ID_MODE_SILENT_TURBO => Some(TrayCommand::Mode("silentTurbo")),
        ID_MODE_TURBO => Some(TrayCommand::Mode("turbo")),
        ID_MODE_CUSTOM => Some(TrayCommand::Mode("custom")),
        ID_SWITCH_FANBOOST => Some(TrayCommand::SwitchFanboost),
        ID_SHOW => Some(TrayCommand::Show),
        ID_QUIT => Some(TrayCommand::Quit),
        _ => None,
    }
}

fn on_tray_command<R: Runtime>(app: &AppHandle<R>, id: &str, checks: &TrayChecks<R>) {
    let Some(command) = tray_command(id) else {
        return;
    };
    match command {
        TrayCommand::Mode(mode) => {
            let handle = app.clone();
            let items = checks.modes.clone();
            let selected = id.to_owned();
            tauri::async_runtime::spawn(async move {
                {
                    let state = handle.state::<AppState>();
                    let mut backend = state.backend.lock().await;
                    let _ = backend.set_performance_mode(mode).await;
                }
                for item in items.iter() {
                    let _ = item.set_checked(item.id().as_ref() == selected);
                }
            });
        }
        TrayCommand::SwitchFanboost => {
            if let Some(item) = checks.fanboost.as_ref() {
                spawn_switch(app, "fanboost", item);
            }
        }
        TrayCommand::Show => show_main(app),
        TrayCommand::Quit => app.exit(0),
    }
}

fn spawn_switch<R: Runtime>(app: &AppHandle<R>, key: &'static str, item: &CheckMenuItem<R>) {
    let Ok(on) = item.is_checked() else {
        return;
    };
    let handle = app.clone();
    tauri::async_runtime::spawn(async move {
        let state = handle.state::<AppState>();
        let mut backend = state.backend.lock().await;
        let _ = backend.set_quick_switch(key, on).await;
    });
}

fn show_main<R: Runtime>(app: &AppHandle<R>) {
    let Some(window) = app.get_webview_window("main") else {
        return;
    };
    let _ = window.unminimize();
    let _ = window.show();
    let _ = window.set_focus();
}
