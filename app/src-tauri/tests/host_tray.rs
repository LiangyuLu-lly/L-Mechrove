//! Given offered switch keys. When tray_menu_spec. Then mode ids, quit, gated fanboost, no lightbar.

use app_lib::tray::tray_menu_spec;

const MODE_IDS: [&str; 4] = [
    "mode-silentTurbo",
    "mode-office",
    "mode-turbo",
    "mode-custom",
];

#[test]
fn tray_menu_spec_contains_four_mode_ids_and_quit_when_offered_empty() {
    // Given: no offered quick switches
    let offered: [String; 0] = [];

    // When: the pure tray menu spec is built
    let ids = tray_menu_spec(&offered);

    // Then: the four mode ids and quit are present
    for mode in MODE_IDS {
        assert!(
            ids.iter().any(|id| id == mode),
            "missing {mode}: {ids:?}"
        );
    }
    assert!(ids.iter().any(|id| id == "quit"), "missing quit: {ids:?}");
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
