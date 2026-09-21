//! One core tray icon. Menu clicks reuse Backend commands, not a second MQTT path.
//! allow: SIZE_OK — C# tray surface builder; T11 ownership is tray.rs only.

use tauri::menu::{
    CheckMenuItem, CheckMenuItemBuilder, MenuBuilder, MenuItemBuilder, SubmenuBuilder,
};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{App, AppHandle, Manager, Runtime};

use crate::hw_backend::AppState;

const ID_MODE_OFFICE: &str = "mode-office";
const ID_MODE_GAMING: &str = "mode-gaming";
const ID_MODE_SILENT_TURBO: &str = "mode-silentTurbo";
const ID_MODE_TURBO: &str = "mode-turbo";
const ID_SWITCH_FANBOOST: &str = "switch-fanboost";
const ID_GPU_IGPU: &str = "gpu-igpu";
const ID_GPU_STANDARD: &str = "gpu-standard";
const ID_GPU_DGPU: &str = "gpu-dgpu";
const ID_LIGHTING_KEYBOARD: &str = "lighting-keyboard";
const ID_OVERLAY: &str = "overlay";
const ID_SHOW: &str = "show";
const ID_DIAGNOSTICS: &str = "diagnostics";
const ID_QUIT: &str = "quit";
const CUSTOM_SLOT_IDS: [&str; 4] = ["custom-0", "custom-1", "custom-2", "custom-3"];
const CUSTOM_SLOT_LABELS: [&str; 4] = ["自定义 1", "自定义 2", "自定义 3", "自定义 4"];

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

/// C# tray gates: silentTurbo / fanboost / custom / gpu_actions / keyboard.
#[derive(Clone, Copy, Debug)]
pub struct TrayMenuGates<'a> {
    pub silent_turbo: bool,
    pub fanboost: bool,
    pub custom: bool,
    pub gpu_actions: &'a [String],
    pub keyboard: bool,
}

impl Default for TrayMenuGates<'static> {
    fn default() -> Self {
        Self {
            silent_turbo: false,
            fanboost: false,
            custom: false,
            gpu_actions: &[],
            keyboard: false,
        }
    }
}

/// Snapshot fields the tray menu reads.
#[derive(Clone, Copy, Debug)]
pub struct TraySnapshotView<'a> {
    pub silent_turbo: bool,
    pub offered_switches: &'a [String],
    pub gpu_actions: &'a [String],
    pub keyboard: bool,
    pub tcc_adjustable: bool,
    pub oc_settings: bool,
}

/// C# `Settings.cs:3784` `CpuPerformanceTuning || FanSettings || HasAnyCustomRange`
/// → snapshot `tcc_adjustable || oc_settings`. C# rebuilds on every right-click; we build once.
pub fn gates_from_snapshot<'a>(snapshot: &'a TraySnapshotView<'a>) -> TrayMenuGates<'a> {
    TrayMenuGates {
        silent_turbo: snapshot.silent_turbo,
        fanboost: snapshot
            .offered_switches
            .iter()
            .any(|key| key == "fanboost"),
        custom: snapshot.tcc_adjustable || snapshot.oc_settings,
        gpu_actions: snapshot.gpu_actions,
        keyboard: snapshot.keyboard,
    }
}

/// C# `自定义 1`..`自定义 4` → firmware slots 0..3. Slot 5 does not exist.
pub fn tray_custom_slot(id: &str) -> Option<u8> {
    CUSTOM_SLOT_IDS
        .iter()
        .position(|slot_id| *slot_id == id)
        .and_then(|index| u8::try_from(index).ok())
}

/// Label for firmware slot 0..3 (`自定义 1`..`自定义 4`).
pub fn tray_custom_label(slot: u8) -> Option<&'static str> {
    CUSTOM_SLOT_LABELS.get(usize::from(slot)).copied()
}

/// Pure menu ids for tests. Gates follow C# Settings.cs:3735-3871 order.
pub fn tray_menu_spec(gates: &TrayMenuGates<'_>) -> Vec<String> {
    let mut ids = vec![ID_MODE_OFFICE.to_owned(), ID_MODE_GAMING.to_owned()];
    if gates.silent_turbo {
        ids.push(ID_MODE_SILENT_TURBO.to_owned());
    }
    ids.push(ID_MODE_TURBO.to_owned());
    if gates.fanboost {
        ids.push(ID_SWITCH_FANBOOST.to_owned());
    }
    if gates.custom {
        ids.extend(CUSTOM_SLOT_IDS.iter().map(|id| (*id).to_owned()));
    }
    if !gates.gpu_actions.is_empty() {
        ids.extend(
            [ID_GPU_IGPU, ID_GPU_STANDARD, ID_GPU_DGPU]
                .iter()
                .map(|id| (*id).to_owned()),
        );
    }
    if gates.keyboard {
        ids.push(ID_LIGHTING_KEYBOARD.to_owned());
    }
    ids.push(ID_OVERLAY.to_owned());
    ids.push(ID_SHOW.to_owned());
    ids.push(ID_DIAGNOSTICS.to_owned());
    ids.push(ID_QUIT.to_owned());
    ids
}

enum TrayCommand {
    Mode(&'static str),
    Custom(u8),
    SwitchFanboost,
    GpuIgpu,
    GpuStandard,
    GpuDgpu,
    Keyboard,
    Overlay,
    Show,
    Diagnostics,
    Quit,
}

struct TrayChecks<R: Runtime> {
    modes: Vec<CheckMenuItem<R>>,
    fanboost: Option<CheckMenuItem<R>>,
    overlay: CheckMenuItem<R>,
    gpu_actions: Vec<String>,
}

pub fn build_tray(app: &App) -> tauri::Result<()> {
    let (silent_turbo, fanboost, custom, gpu_actions, keyboard) = live_gates(app);
    let gates = TrayMenuGates {
        silent_turbo,
        fanboost,
        custom,
        gpu_actions: &gpu_actions,
        keyboard,
    };
    let spec = tray_menu_spec(&gates);
    let has = |id: &str| spec.iter().any(|item| item == id);

    let header = MenuItemBuilder::with_id("header-modes", "性能模式")
        .enabled(false)
        .build(app)?;
    let office = check_item(app, ID_MODE_OFFICE, "静音 (办公)", true)?;
    let gaming = check_item(app, ID_MODE_GAMING, "平衡 (游戏)", false)?;
    let silent_turbo = if has(ID_MODE_SILENT_TURBO) {
        Some(check_item(app, ID_MODE_SILENT_TURBO, "静音狂暴", false)?)
    } else {
        None
    };
    let turbo = check_item(app, ID_MODE_TURBO, "狂暴 (增强)", false)?;
    let fanboost = if has(ID_SWITCH_FANBOOST) {
        Some(check_item(app, ID_SWITCH_FANBOOST, "风扇增强", false)?)
    } else {
        None
    };
    let custom_items = if has(CUSTOM_SLOT_IDS[0]) {
        Some([
            check_item(app, CUSTOM_SLOT_IDS[0], CUSTOM_SLOT_LABELS[0], false)?,
            check_item(app, CUSTOM_SLOT_IDS[1], CUSTOM_SLOT_LABELS[1], false)?,
            check_item(app, CUSTOM_SLOT_IDS[2], CUSTOM_SLOT_LABELS[2], false)?,
            check_item(app, CUSTOM_SLOT_IDS[3], CUSTOM_SLOT_LABELS[3], false)?,
        ])
    } else {
        None
    };
    let custom_menu = match custom_items.as_ref() {
        Some(items) => Some(
            SubmenuBuilder::new(app, "自定义模式")
                .item(&items[0])
                .item(&items[1])
                .item(&items[2])
                .item(&items[3])
                .build()?,
        ),
        None => None,
    };
    let gpu_igpu = maybe_item(app, has(ID_GPU_IGPU), ID_GPU_IGPU, "集显模式")?;
    let gpu_standard = maybe_item(app, has(ID_GPU_STANDARD), ID_GPU_STANDARD, "标准模式")?;
    let gpu_dgpu = maybe_item(app, has(ID_GPU_DGPU), ID_GPU_DGPU, "独显直连")?;
    let keyboard = maybe_item(
        app,
        has(ID_LIGHTING_KEYBOARD),
        ID_LIGHTING_KEYBOARD,
        "键盘灯效",
    )?;
    let overlay = check_item(app, ID_OVERLAY, "悬浮监控", false)?;
    let show = MenuItemBuilder::with_id(ID_SHOW, "打开主界面").build(app)?;
    let diagnostics = MenuItemBuilder::with_id(ID_DIAGNOSTICS, "导出诊断包").build(app)?;
    let quit = MenuItemBuilder::with_id(ID_QUIT, "退出").build(app)?;

    let mut menu = MenuBuilder::new(app)
        .item(&header)
        .item(&office)
        .item(&gaming);
    if let Some(ref silent) = silent_turbo {
        menu = menu.item(silent);
    }
    menu = menu.item(&turbo);
    if let Some(ref fan) = fanboost {
        menu = menu.item(fan);
    }
    if let Some(ref custom) = custom_menu {
        menu = menu.item(custom);
    }
    menu = menu.separator();
    if let Some(ref item) = gpu_igpu {
        menu = menu.item(item);
    }
    if let Some(ref item) = gpu_standard {
        menu = menu.item(item);
    }
    if let Some(ref item) = gpu_dgpu {
        menu = menu.item(item);
    }
    if let Some(ref item) = keyboard {
        menu = menu.item(item);
    }
    let menu = menu
        .item(&overlay)
        .item(&show)
        .item(&diagnostics)
        .separator()
        .item(&quit)
        .build()?;

    let mut modes = vec![office, gaming];
    if let Some(silent) = silent_turbo {
        modes.push(silent);
    }
    modes.push(turbo);
    if let Some(items) = custom_items {
        modes.extend(items);
    }

    let checks = TrayChecks {
        modes,
        fanboost,
        overlay,
        gpu_actions,
    };
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

fn maybe_item<R: Runtime>(
    app: &App<R>,
    include: bool,
    id: &str,
    text: &str,
) -> tauri::Result<Option<tauri::menu::MenuItem<R>>> {
    if include {
        Ok(Some(MenuItemBuilder::with_id(id, text).build(app)?))
    } else {
        Ok(None)
    }
}

fn live_gates(app: &App) -> (bool, bool, bool, Vec<String>, bool) {
    let state = app.state::<AppState>();
    let Ok(backend) = state.backend.try_lock() else {
        return (false, false, false, Vec::new(), false);
    };
    let snapshot = backend.snapshot();
    let view = TraySnapshotView {
        silent_turbo: snapshot.silent_turbo,
        offered_switches: &snapshot.offered_switches,
        gpu_actions: &snapshot.gpu_actions,
        keyboard: snapshot.lighting.keyboard,
        tcc_adjustable: snapshot.tcc_adjustable,
        oc_settings: snapshot.oc_settings,
    };
    let gates = gates_from_snapshot(&view);
    let silent_turbo = gates.silent_turbo;
    let fanboost = gates.fanboost;
    let custom = gates.custom;
    let keyboard = gates.keyboard;
    (
        silent_turbo,
        fanboost,
        custom,
        snapshot.gpu_actions,
        keyboard,
    )
}

fn tray_command(id: &str) -> Option<TrayCommand> {
    if let Some(slot) = tray_custom_slot(id) {
        return Some(TrayCommand::Custom(slot));
    }
    match id {
        ID_MODE_OFFICE => Some(TrayCommand::Mode("office")),
        ID_MODE_GAMING => Some(TrayCommand::Mode("gaming")),
        ID_MODE_SILENT_TURBO => Some(TrayCommand::Mode("silentTurbo")),
        ID_MODE_TURBO => Some(TrayCommand::Mode("turbo")),
        ID_SWITCH_FANBOOST => Some(TrayCommand::SwitchFanboost),
        ID_GPU_IGPU => Some(TrayCommand::GpuIgpu),
        ID_GPU_STANDARD => Some(TrayCommand::GpuStandard),
        ID_GPU_DGPU => Some(TrayCommand::GpuDgpu),
        ID_LIGHTING_KEYBOARD => Some(TrayCommand::Keyboard),
        ID_OVERLAY => Some(TrayCommand::Overlay),
        ID_SHOW => Some(TrayCommand::Show),
        ID_DIAGNOSTICS => Some(TrayCommand::Diagnostics),
        ID_QUIT => Some(TrayCommand::Quit),
        _ => None,
    }
}

fn gpu_route(kind: TrayCommand, gpu_actions: &[String]) -> &'static str {
    let (candidates, fallback) = match kind {
        TrayCommand::GpuIgpu => (
            ["IGPU_ONLY_CONNECT_RB_ON", "DGPU_DIRECT_CONNECT_TOGGLE_IGPU"].as_slice(),
            "DGPU_DIRECT_CONNECT_TOGGLE_IGPU",
        ),
        TrayCommand::GpuStandard => (
            ["IGPU_ONLY_CONNECT_RB_OFF", "DGPU_DIRECT_CONNECT_TOGGLE_OFF"].as_slice(),
            "DGPU_DIRECT_CONNECT_TOGGLE_OFF",
        ),
        TrayCommand::GpuDgpu => (
            ["DGPU_DIRECT_CONNECT_TOGGLE_ON"].as_slice(),
            "DGPU_DIRECT_CONNECT_TOGGLE_ON",
        ),
        TrayCommand::Mode(_)
        | TrayCommand::Custom(_)
        | TrayCommand::SwitchFanboost
        | TrayCommand::Keyboard
        | TrayCommand::Overlay
        | TrayCommand::Show
        | TrayCommand::Diagnostics
        | TrayCommand::Quit => return "",
    };
    candidates
        .iter()
        .copied()
        .find(|action| gpu_actions.iter().any(|offered| offered == action))
        .unwrap_or(fallback)
}

fn on_tray_command<R: Runtime>(app: &AppHandle<R>, id: &str, checks: &TrayChecks<R>) {
    let Some(command) = tray_command(id) else {
        return;
    };
    match command {
        TrayCommand::Mode(mode) => spawn_mode(app, mode, id, &checks.modes),
        TrayCommand::Custom(slot) => spawn_custom(app, slot, &checks.modes),
        TrayCommand::SwitchFanboost => {
            if let Some(item) = checks.fanboost.as_ref() {
                spawn_switch(app, "fanboost", item);
            }
        }
        TrayCommand::GpuIgpu | TrayCommand::GpuStandard | TrayCommand::GpuDgpu => {
            spawn_gpu(app, gpu_route(command, &checks.gpu_actions));
        }
        TrayCommand::Keyboard | TrayCommand::Show => show_main(app),
        TrayCommand::Overlay => {
            let Ok(on) = checks.overlay.is_checked() else {
                return;
            };
            set_overlay(app, on);
        }
        TrayCommand::Diagnostics => spawn_diagnostics(app),
        TrayCommand::Quit => app.exit(0),
    }
}

fn spawn_mode<R: Runtime>(
    app: &AppHandle<R>,
    mode: &'static str,
    selected: &str,
    items: &[CheckMenuItem<R>],
) {
    let handle = app.clone();
    let items = items.to_vec();
    let selected = selected.to_owned();
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

fn spawn_custom<R: Runtime>(app: &AppHandle<R>, slot: u8, items: &[CheckMenuItem<R>]) {
    let handle = app.clone();
    let items = items.to_vec();
    let selected = format!("custom-{slot}");
    let index = slot.to_string();
    tauri::async_runtime::spawn(async move {
        {
            let state = handle.state::<AppState>();
            let mut backend = state.backend.lock().await;
            let _ = backend.set_custom_detail("ProfileIndex", &index).await;
            let _ = backend.set_performance_mode("custom").await;
        }
        for item in items.iter() {
            let _ = item.set_checked(item.id().as_ref() == selected);
        }
    });
}

fn spawn_gpu<R: Runtime>(app: &AppHandle<R>, action: &'static str) {
    if action.is_empty() {
        return;
    }
    let handle = app.clone();
    tauri::async_runtime::spawn(async move {
        let state = handle.state::<AppState>();
        let mut backend = state.backend.lock().await;
        let _ = backend.set_gpu_route(action).await;
    });
}

fn spawn_diagnostics<R: Runtime>(app: &AppHandle<R>) {
    let handle = app.clone();
    tauri::async_runtime::spawn(async move {
        let state = handle.state::<AppState>();
        let backend = state.backend.lock().await;
        let dir = std::env::temp_dir().join(format!("lmechrevo-diag-{}", std::process::id()));
        let _ = backend.export_diagnostics(&dir);
    });
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

fn set_overlay<R: Runtime>(app: &AppHandle<R>, on: bool) {
    let Some(hud) = app.get_webview_window("hud") else {
        return;
    };
    if on {
        let _ = hud.show();
    } else {
        let _ = hud.hide();
    }
}

fn show_main<R: Runtime>(app: &AppHandle<R>) {
    let Some(window) = app.get_webview_window("main") else {
        return;
    };
    let _ = window.unminimize();
    let _ = window.show();
    let _ = window.set_focus();
}
