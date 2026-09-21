//! Given offered switch keys. When tray_menu_spec. Then C# tray mode ids, gated silentTurbo, no touchpad.

use app_lib::tray::{hide_main_on_close, should_hide_on_close, tray_menu_spec};

#[test]
fn tray_menu_spec_contains_office_and_gaming_when_offered_empty() {
    // Given: no offered keys
    let offered: [String; 0] = [];

    // When: the pure tray menu spec is built
    let ids = tray_menu_spec(&offered);

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
    // Given: offered list without silentTurbo
    let offered = ["fanboost".to_owned()];

    // When: the pure tray menu spec is built
    let ids = tray_menu_spec(&offered);

    // Then: mode-silentTurbo is absent
    assert!(
        !ids.iter().any(|id| id == "mode-silentTurbo"),
        "mode-silentTurbo must be absent when not offered: {ids:?}"
    );
}

#[test]
fn tray_menu_spec_contains_silent_turbo_when_offered() {
    // Given: offered list includes silentTurbo
    let offered = ["silentTurbo".to_owned()];

    // When: the pure tray menu spec is built
    let ids = tray_menu_spec(&offered);

    // Then: mode-silentTurbo is present
    assert!(
        ids.iter().any(|id| id == "mode-silentTurbo"),
        "mode-silentTurbo must be present when offered: {ids:?}"
    );
}

#[test]
fn tray_menu_spec_never_contains_switch_touchpad() {
    // Given: offered list includes touchpad
    let offered = ["touchpad".to_owned(), "fanboost".to_owned()];

    // When: the pure tray menu spec is built
    let ids = tray_menu_spec(&offered);

    // Then: C# tray has no touchpad item
    assert!(
        !ids.iter().any(|id| id == "switch-touchpad"),
        "switch-touchpad must never appear: {ids:?}"
    );
}

#[test]
fn tray_menu_spec_omits_switch_fanboost_when_fanboost_not_offered() {
    // Given: offered list without fanboost
    let offered = ["touchpad".to_owned(), "wifi".to_owned()];

    // When: the pure tray menu spec is built
    let ids = tray_menu_spec(&offered);

    // Then: switch-fanboost is absent
    assert!(
        !ids.iter().any(|id| id == "switch-fanboost"),
        "fanboost must be absent when not offered: {ids:?}"
    );
}

#[test]
fn tray_menu_spec_contains_switch_fanboost_when_fanboost_offered() {
    // Given: offered list includes fanboost
    let offered = ["fanboost".to_owned()];

    // When: the pure tray menu spec is built
    let ids = tray_menu_spec(&offered);

    // Then: switch-fanboost is present
    assert!(
        ids.iter().any(|id| id == "switch-fanboost"),
        "fanboost must be present when offered: {ids:?}"
    );
}

#[test]
fn tray_menu_spec_never_contains_switch_lightbar_when_lightbar_offered() {
    // Given: offered list includes lighting keys that have no tray write path
    let offered = [
        "lightbar".to_owned(),
        "logolight".to_owned(),
        "fanboost".to_owned(),
    ];

    // When: the pure tray menu spec is built
    let ids = tray_menu_spec(&offered);

    // Then: lightbar is never a tray item
    assert!(
        !ids.iter().any(|id| id == "switch-lightbar"),
        "lightbar must never appear: {ids:?}"
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
