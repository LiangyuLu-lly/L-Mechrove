//! Given ShellPersonalization.cs. When set_quick_switch. Then Win32, not MQTT.

use app_lib::hw_shell::{light_theme_dword, taskbar_target_state, ABS_ALWAYSONTOP, ABS_AUTOHIDE};
use app_lib::hw_switches::offered_quick_switches;
use app_lib::Backend;
use capabilities::ItemSupport;

fn empty_item() -> ItemSupport {
    ItemSupport::parse_json("{}").expect("empty ItemSupport")
}

#[test]
fn taskbar_target_state_preserves_always_on_top() {
    let current = ABS_ALWAYSONTOP;
    let hidden = taskbar_target_state(current, true);
    assert_eq!(hidden & ABS_AUTOHIDE, ABS_AUTOHIDE);
    assert_eq!(hidden & ABS_ALWAYSONTOP, ABS_ALWAYSONTOP);
    let shown = taskbar_target_state(hidden, false);
    assert_eq!(shown & ABS_AUTOHIDE, 0);
    assert_eq!(shown & ABS_ALWAYSONTOP, ABS_ALWAYSONTOP);
}

#[test]
fn dark_theme_writes_light_dword_zero() {
    assert_eq!(light_theme_dword(true), 0);
    assert_eq!(light_theme_dword(false), 1);
}

#[test]
fn shell_keys_always_offered_on_empty_itemsupport() {
    let offered = offered_quick_switches(&empty_item(), &Default::default());
    for key in ["taskbarautohide", "transparency", "darktheme"] {
        assert!(
            offered.iter().any(|offered_key| *offered_key == key),
            "{key} must be offered (StaticQuick): {offered:?}"
        );
    }
}

#[tokio::test]
async fn set_quick_switch_shell_keys_do_not_publish_mqtt() {
    let mut backend = Backend::fake_from_json("{}").expect("parse");
    backend.start().await.expect("start");
    let before = backend.recorded_publishes().len();
    for key in ["taskbarautohide", "transparency", "darktheme"] {
        backend
            .set_quick_switch(key, true)
            .await
            .unwrap_or_else(|err| panic!("{key}: {err}"));
    }
    let publishes = backend.recorded_publishes();
    assert_eq!(
        publishes.len(),
        before,
        "shell keys must not publish MQTT: {publishes:?}"
    );
    let recorded = backend.recorded_shell_applies();
    assert!(
        recorded
            .iter()
            .any(|(key, on)| key == "taskbarautohide" && *on),
        "taskbarautohide missing: {recorded:?}"
    );
    assert!(
        recorded
            .iter()
            .any(|(key, on)| key == "transparency" && *on),
        "transparency missing: {recorded:?}"
    );
    assert!(
        recorded.iter().any(|(key, on)| key == "darktheme" && *on),
        "darktheme missing: {recorded:?}"
    );
}
