//! Given OfficialConsoleIsolation.cs. When set_official_isolation. Then record, no MQTT.

use app_lib::hw_isolation::{
    is_allowed_run_key_path, is_isolation_process_target, is_official_reference,
    planned_reg_delete_args, RUN_KEY_PATHS, VENDOR_UI_PROCESS_NAMES,
};
use app_lib::Backend;

#[tokio::test]
async fn set_official_isolation_true_records_without_mqtt_publish() {
    let mut backend = Backend::fake_from_json("{}").expect("parse");
    backend.start().await.expect("start");
    let before = backend.recorded_publishes().len();
    backend
        .set_official_isolation(true)
        .expect("isolate records");
    let publishes = backend.recorded_publishes();
    assert_eq!(
        publishes.len(),
        before,
        "isolation must not publish MQTT: {publishes:?}"
    );
    assert_eq!(backend.recorded_official_isolation(), Some(true));
}

#[tokio::test]
async fn set_official_isolation_false_records_restore() {
    let mut backend = Backend::fake_from_json("{}").expect("parse");
    backend.start().await.expect("start");
    let after_handshake = backend.recorded_publishes().len();
    backend.set_official_isolation(true).expect("isolate");
    backend.set_official_isolation(false).expect("restore");
    assert_eq!(backend.recorded_official_isolation(), Some(false));
    assert_eq!(
        backend.recorded_publishes().len(),
        after_handshake,
        "isolation itself must add no MQTT publish"
    );
}

#[test]
fn isolation_never_targets_l_mechrevo_process() {
    assert!(!is_isolation_process_target("L-Mechrevo", "app"));
    assert!(!is_isolation_process_target("L-Mechrevo.exe", "app"));
    assert!(!is_isolation_process_target("L-Mechrevo", "L-Mechrevo"));
    assert!(!VENDOR_UI_PROCESS_NAMES
        .iter()
        .any(|name| name.eq_ignore_ascii_case("L-Mechrevo")));
}

#[test]
fn isolation_never_targets_own_exe() {
    assert!(!is_isolation_process_target("app", "app"));
    assert!(!is_isolation_process_target("app.exe", "app"));
    assert!(!is_isolation_process_target("control-center-x", "control-center-x"));
}

#[test]
fn isolation_targets_vendor_ui_names_only() {
    for name in VENDOR_UI_PROCESS_NAMES {
        assert!(
            is_isolation_process_target(name, "app"),
            "vendor UI {name} must be a target"
        );
        assert!(
            !is_isolation_process_target(name, name),
            "own exe stem {name} must never be a target"
        );
    }
}

#[test]
fn isolation_run_keys_are_run_and_runonce() {
    assert_eq!(
        RUN_KEY_PATHS,
        &[
            r"Software\Microsoft\Windows\CurrentVersion\Run",
            r"Software\Microsoft\Windows\CurrentVersion\RunOnce",
        ]
    );
    assert!(is_allowed_run_key_path(RUN_KEY_PATHS[0]));
    assert!(!is_allowed_run_key_path(
        r"SYSTEM\CurrentControlSet\Services\Evil"
    ));
}

#[test]
fn isolation_markers_match_vendor_exe_not_l_mechrevo() {
    assert!(is_official_reference(r"C:\OEM\CCUWinUI.exe"));
    assert!(!is_official_reference(
        r"C:\Program Files\L-Mechrevo\L-Mechrevo.exe"
    ));
}

#[test]
fn planned_reg_delete_args_target_hkcu_run_value() {
    let key = format!(r"HKCU\{}", RUN_KEY_PATHS[0]);
    let args = planned_reg_delete_args(&key, "CCUWinUI");
    assert_eq!(args.first().map(String::as_str), Some("delete"));
    assert!(args.iter().any(|arg| arg == &key));
    assert!(args.iter().any(|arg| arg == "CCUWinUI"));
    assert!(args.iter().any(|arg| arg == "/f"));
}
