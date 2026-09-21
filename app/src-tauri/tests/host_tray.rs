//! Given tray gates. When tray_menu_spec. Then C# ids in C# order; no touchpad; custom 1..4 → slots 0..3.

use app_lib::tray::{
    gates_from_snapshot, hide_main_on_close, should_hide_on_close, tray_custom_label,
    tray_custom_slot, tray_menu_spec, TrayMenuGates, TraySnapshotView,
};

fn spec(gates: TrayMenuGates<'_>) -> Vec<String> {
    tray_menu_spec(&gates)
}

fn empty_gates() -> TrayMenuGates<'static> {
    TrayMenuGates::default()
}

#[test]
fn tray_menu_spec_contains_office_and_gaming_when_offered_empty() {
    // Given: no gates
    let gates = empty_gates();

    // When: the pure tray menu spec is built
    let ids = spec(gates);

    // Then: office and gaming are always present
    assert!(
        ids.iter().any(|id| id == "mode-office"),
        "missing mode-office: {ids:?}"
    );
    assert!(
        ids.iter().any(|id| id == "mode-gaming"),
        "missing mode-gaming: {ids:?}"
    );
    assert!(ids.iter().any(|id| id == "quit"), "missing quit: {ids:?}");
}

#[test]
fn tray_menu_spec_omits_silent_turbo_when_not_offered() {
    // Given: fanboost only
    let gates = TrayMenuGates {
        fanboost: true,
        ..empty_gates()
    };

    // When: the pure tray menu spec is built
    let ids = spec(gates);

    // Then: mode-silentTurbo is absent
    assert!(
        !ids.iter().any(|id| id == "mode-silentTurbo"),
        "mode-silentTurbo must be absent when not offered: {ids:?}"
    );
}

#[test]
fn tray_menu_spec_contains_silent_turbo_when_offered() {
    // Given: silentTurbo gate
    let gates = TrayMenuGates {
        silent_turbo: true,
        ..empty_gates()
    };

    // When: the pure tray menu spec is built
    let ids = spec(gates);

    // Then: mode-silentTurbo is present
    assert!(
        ids.iter().any(|id| id == "mode-silentTurbo"),
        "mode-silentTurbo must be present when offered: {ids:?}"
    );
}

#[test]
fn tray_menu_spec_never_contains_switch_touchpad() {
    // Given: every C# tray gate on
    let gpu = ["DGPU_DIRECT_CONNECT_TOGGLE_ON".to_owned()];
    let gates = TrayMenuGates {
        silent_turbo: true,
        fanboost: true,
        custom: true,
        gpu_actions: &gpu,
        keyboard: true,
    };

    // When: the pure tray menu spec is built
    let ids = spec(gates);

    // Then: C# tray has no touchpad item
    assert!(
        !ids.iter().any(|id| id == "switch-touchpad"),
        "switch-touchpad must never appear: {ids:?}"
    );
}

#[test]
fn tray_menu_spec_omits_switch_fanboost_when_fanboost_not_offered() {
    // Given: no fanboost gate
    let gates = empty_gates();

    // When: the pure tray menu spec is built
    let ids = spec(gates);

    // Then: switch-fanboost is absent
    assert!(
        !ids.iter().any(|id| id == "switch-fanboost"),
        "fanboost must be absent when not offered: {ids:?}"
    );
}

#[test]
fn tray_menu_spec_contains_switch_fanboost_when_fanboost_offered() {
    // Given: fanboost gate
    let gates = TrayMenuGates {
        fanboost: true,
        ..empty_gates()
    };

    // When: the pure tray menu spec is built
    let ids = spec(gates);

    // Then: switch-fanboost is present
    assert!(
        ids.iter().any(|id| id == "switch-fanboost"),
        "fanboost must be present when offered: {ids:?}"
    );
}

#[test]
fn tray_menu_spec_never_contains_switch_lightbar_when_lightbar_offered() {
    // Given: all gates on — lightbar still has no tray write path
    let gpu = ["DGPU_DIRECT_CONNECT_TOGGLE_ON".to_owned()];
    let gates = TrayMenuGates {
        silent_turbo: true,
        fanboost: true,
        custom: true,
        gpu_actions: &gpu,
        keyboard: true,
    };

    // When: the pure tray menu spec is built
    let ids = spec(gates);

    // Then: lightbar is never a tray item
    assert!(
        !ids.iter().any(|id| id == "switch-lightbar"),
        "lightbar must never appear: {ids:?}"
    );
}

#[test]
fn tray_menu_spec_omits_custom_slots_when_custom_not_offered() {
    // Given: custom gate off
    let gates = empty_gates();

    // When: the pure tray menu spec is built
    let ids = spec(gates);

    // Then: custom-0..3 are absent
    assert!(
        !ids.iter().any(|id| id.starts_with("custom-")),
        "custom slots must be absent when not offered: {ids:?}"
    );
}

#[test]
fn tray_menu_spec_contains_custom_0_through_3_when_custom_offered() {
    // Given: custom gate on
    let gates = TrayMenuGates {
        custom: true,
        ..empty_gates()
    };

    // When: the pure tray menu spec is built
    let ids = spec(gates);

    // Then: custom-0..3 appear in slot order
    let custom: Vec<&String> = ids.iter().filter(|id| id.starts_with("custom-")).collect();
    assert_eq!(
        custom,
        [&"custom-0", &"custom-1", &"custom-2", &"custom-3"],
        "custom ids must be slots 0..3: {ids:?}"
    );
}

#[test]
fn tray_custom_slot_maps_custom_1_through_4_to_indices_0_through_3() {
    // Given: C# 自定义 1..4
    // When: ids are mapped to firmware slots
    // Then: 自定义 N → index N-1; slot 5 does not exist
    assert_eq!(tray_custom_label(0), Some("自定义 1"));
    assert_eq!(tray_custom_label(1), Some("自定义 2"));
    assert_eq!(tray_custom_label(2), Some("自定义 3"));
    assert_eq!(tray_custom_label(3), Some("自定义 4"));
    assert_eq!(tray_custom_label(4), None);
    assert_eq!(tray_custom_slot("custom-0"), Some(0));
    assert_eq!(tray_custom_slot("custom-1"), Some(1));
    assert_eq!(tray_custom_slot("custom-2"), Some(2));
    assert_eq!(tray_custom_slot("custom-3"), Some(3));
    assert_eq!(tray_custom_slot("custom-4"), None);
}

#[test]
fn tray_menu_spec_omits_gpu_ids_when_gpu_actions_empty() {
    // Given: empty gpu_actions
    let gates = TrayMenuGates {
        custom: true,
        gpu_actions: &[],
        ..empty_gates()
    };

    // When: the pure tray menu spec is built
    let ids = spec(gates);

    // Then: GPU group is absent
    assert!(
        !ids.iter().any(|id| id.starts_with("gpu-")),
        "gpu ids must be absent when gpu_actions is empty: {ids:?}"
    );
}

#[test]
fn tray_menu_spec_contains_gpu_ids_in_csharp_order_when_gpu_actions_non_empty() {
    // Given: non-empty gpu_actions
    let gpu = ["DGPU_DIRECT_CONNECT_TOGGLE_ON".to_owned()];
    let gates = TrayMenuGates {
        gpu_actions: &gpu,
        ..empty_gates()
    };

    // When: the pure tray menu spec is built
    let ids = spec(gates);

    // Then: 集显 / 标准 / 独显 ids in C# order
    let gpu_ids: Vec<&String> = ids.iter().filter(|id| id.starts_with("gpu-")).collect();
    assert_eq!(
        gpu_ids,
        [&"gpu-igpu", &"gpu-standard", &"gpu-dgpu"],
        "gpu ids must be 集显/标准/独显 order: {ids:?}"
    );
}

#[test]
fn tray_menu_spec_omits_keyboard_when_not_offered() {
    // Given: keyboard gate off
    let gates = empty_gates();

    // When: the pure tray menu spec is built
    let ids = spec(gates);

    // Then: lighting-keyboard is absent
    assert!(
        !ids.iter().any(|id| id == "lighting-keyboard"),
        "keyboard must be absent when lighting.keyboard is false: {ids:?}"
    );
}

#[test]
fn tray_menu_spec_contains_keyboard_when_offered() {
    // Given: keyboard gate on
    let gates = TrayMenuGates {
        keyboard: true,
        ..empty_gates()
    };

    // When: the pure tray menu spec is built
    let ids = spec(gates);

    // Then: lighting-keyboard is present
    assert!(
        ids.iter().any(|id| id == "lighting-keyboard"),
        "keyboard must be present when lighting.keyboard: {ids:?}"
    );
}

#[test]
fn tray_menu_spec_matches_csharp_id_order_when_all_gates_on() {
    // Given: silentTurbo, fanboost, custom, gpu, keyboard
    let gpu = ["DGPU_DIRECT_CONNECT_TOGGLE_ON".to_owned()];
    let gates = TrayMenuGates {
        silent_turbo: true,
        fanboost: true,
        custom: true,
        gpu_actions: &gpu,
        keyboard: true,
    };

    // When: the pure tray menu spec is built
    let ids = spec(gates);

    // Then: C# Settings.cs:3735-3871 id order
    assert_eq!(
        ids,
        [
            "mode-office",
            "mode-gaming",
            "mode-silentTurbo",
            "mode-turbo",
            "switch-fanboost",
            "custom-0",
            "custom-1",
            "custom-2",
            "custom-3",
            "gpu-igpu",
            "gpu-standard",
            "gpu-dgpu",
            "lighting-keyboard",
            "overlay",
            "show",
            "diagnostics",
            "quit",
        ]
    );
}

#[test]
fn tray_menu_spec_always_contains_overlay_show_diagnostics_quit() {
    // Given: no optional gates
    let gates = empty_gates();

    // When: the pure tray menu spec is built
    let ids = spec(gates);

    // Then: overlay, 打开主界面, 导出诊断包, 退出 are always present
    assert_eq!(
        ids[ids.len().saturating_sub(4)..],
        ["overlay", "show", "diagnostics", "quit"]
    );
}

#[test]
fn should_hide_on_close_is_true() {
    // Given: C# SettingsForm_FormClosing cancels close and HideAll
    // When: the pure close policy is queried
    let hide = should_hide_on_close();

    // Then: main window close hides to tray instead of exiting
    assert!(hide);
}

#[test]
fn hide_main_on_close_when_label_is_main() {
    // Given: the main window CloseRequested
    // When: hide policy is applied to label "main"
    let hide = hide_main_on_close("main");

    // Then: close is converted to hide
    assert!(hide);
}

#[test]
fn hide_main_on_close_skips_hud_window() {
    // Given: the overlay HUD window CloseRequested
    // When: hide policy is applied to label "hud"
    let hide = hide_main_on_close("hud");

    // Then: overlay_set / HUD close is unchanged
    assert!(!hide);
}

fn empty_snapshot_view() -> TraySnapshotView<'static> {
    TraySnapshotView {
        silent_turbo: false,
        offered_switches: &[],
        gpu_actions: &[],
        keyboard: false,
        tcc_adjustable: false,
        oc_settings: false,
    }
}

#[test]
fn gates_from_snapshot_offers_custom_only_when_csharp_gate_holds() {
    // Given: snapshot with neither tcc_adjustable nor oc_settings
    // (C# Settings.cs:3784 CpuPerformanceTuning || FanSettings || HasAnyCustomRange is false)
    let unsatisfied = empty_snapshot_view();

    // When: gates are derived from the snapshot
    let hidden = gates_from_snapshot(&unsatisfied);

    // Then: custom 1–4 is not offered (the live `custom: true` literal must fail here)
    assert!(
        !hidden.custom,
        "custom must be hidden when the C# tray gate is not satisfied"
    );

    // Given: tcc_adjustable (CPUPerformanceAndOverClockMenuSupport / HasAnyCustomRange stand-in)
    let tuning = TraySnapshotView {
        tcc_adjustable: true,
        ..empty_snapshot_view()
    };

    // When: gates are derived from the snapshot
    let offered_tuning = gates_from_snapshot(&tuning);

    // Then: custom is offered
    assert!(
        offered_tuning.custom,
        "custom must be offered when tcc_adjustable satisfies the C# gate"
    );

    // Given: oc_settings (OcSettingsSupport)
    let oc = TraySnapshotView {
        oc_settings: true,
        ..empty_snapshot_view()
    };

    // When: gates are derived from the snapshot
    let offered_oc = gates_from_snapshot(&oc);

    // Then: custom is offered
    assert!(
        offered_oc.custom,
        "custom must be offered when oc_settings satisfies the C# gate"
    );
}
