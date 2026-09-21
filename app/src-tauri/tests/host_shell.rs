//! Given ShellPersonalization.cs. When set_quick_switch. Then Win32, not MQTT.

use app_lib::hw_shell::{
    light_theme_dword, planned_dark_theme, planned_reg_add_args, planned_transparency,
    taskbar_target_state, ABS_ALWAYSONTOP, ABS_AUTOHIDE, IMMERSIVE_COLOR_SET, PERSONALIZE_KEY,
};
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
fn transparency_plan_writes_enable_transparency_on_personalize_key() {
    let plan = planned_transparency(true);
    assert_eq!(plan.broadcast, Some(IMMERSIVE_COLOR_SET));
    assert_eq!(plan.writes.len(), 1);
    assert_eq!(plan.writes[0].key, PERSONALIZE_KEY);
    assert_eq!(
        PERSONALIZE_KEY,
        r"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize"
    );
    assert_eq!(plan.writes[0].name, "EnableTransparency");
    assert_eq!(plan.writes[0].value, 1);
    let off = planned_transparency(false);
    assert_eq!(off.writes[0].value, 0);
}

#[test]
fn dark_theme_plan_writes_both_light_theme_values_on_personalize_key() {
    let plan = planned_dark_theme(true);
    assert_eq!(plan.broadcast, Some(IMMERSIVE_COLOR_SET));
    assert_eq!(plan.writes.len(), 2);
    assert!(plan.writes.iter().all(|write| write.key == PERSONALIZE_KEY));
    assert!(plan
        .writes
        .iter()
        .any(|write| write.name == "AppsUseLightTheme" && write.value == 0));
    assert!(plan
        .writes
        .iter()
        .any(|write| write.name == "SystemUsesLightTheme" && write.value == 0));
    let light = planned_dark_theme(false);
    assert!(light
        .writes
        .iter()
        .any(|write| write.name == "AppsUseLightTheme" && write.value == 1));
}

#[test]
fn planned_reg_add_args_are_hkcu_dword_without_executing_reg() {
    let plan = planned_transparency(true);
    let args = planned_reg_add_args(&plan.writes[0]);
    assert_eq!(args.first().map(String::as_str), Some("add"));
    assert!(args.iter().any(|arg| arg == PERSONALIZE_KEY));
    assert!(args.iter().any(|arg| arg == "EnableTransparency"));
    assert!(args.iter().any(|arg| arg == "REG_DWORD"));
    assert!(args.iter().any(|arg| arg == "1"));
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
