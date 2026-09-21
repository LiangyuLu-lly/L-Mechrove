//! Given Startup.cs contract. When task name / apply. Then LMechrevo_{SID} and no MQTT.

use app_lib::hw_startup::{
    is_transient_executable_path, normalize_executable_path, sanitize_task_name_fragment,
    schtasks_create_args, schtasks_delete_args, user_task_name, StartupTaskPlan, STARTUP_ARGUMENT,
};
use app_lib::hw_switches::offered_quick_switches;
use app_lib::Backend;
use capabilities::ItemSupport;

fn empty_item() -> ItemSupport {
    ItemSupport::parse_json("{}").expect("empty ItemSupport")
}

#[test]
fn user_task_name_is_lmechrevo_underscore_sid() {
    let sid = "S-1-5-21-3623811015-3361044348-30300820-1013";
    assert_eq!(user_task_name(sid), format!("LMechrevo_{sid}"));
}

#[test]
fn startup_action_argument_is_startup() {
    assert_eq!(STARTUP_ARGUMENT, "startup");
}

#[test]
fn sanitize_replaces_path_separators_in_account_name() {
    assert_eq!(sanitize_task_name_fragment(r"DOMAIN\User"), "DOMAIN_User");
}

#[test]
fn normalize_executable_path_strips_paired_quotes() {
    assert_eq!(
        normalize_executable_path(r#""C:\Program Files\L-Mechrevo\L-Mechrevo.exe""#),
        r"C:\Program Files\L-Mechrevo\L-Mechrevo.exe"
    );
}

#[test]
fn transient_path_under_temp_is_rejected() {
    assert!(is_transient_executable_path(
        r"C:\Users\me\AppData\Local\Temp\L-Mechrevo.exe",
        r"C:\Users\me\AppData\Local\Temp",
    ));
    assert!(!is_transient_executable_path(
        r"C:\Program Files\L-Mechrevo\L-Mechrevo.exe",
        r"C:\Users\me\AppData\Local\Temp",
    ));
}

#[test]
fn schtasks_create_args_carry_task_name_startup_and_highest() {
    let plan = StartupTaskPlan::new("S-1-5-21-1", r"C:\Program Files\L-Mechrevo\L-Mechrevo.exe");
    let args = schtasks_create_args(&plan);
    assert!(
        args.iter().any(|arg| arg == "LMechrevo_S-1-5-21-1"),
        "task name missing: {args:?}"
    );
    assert!(
        args.iter().any(|arg| arg.contains("startup")),
        "startup argument missing: {args:?}"
    );
    assert!(
        args.iter().any(|arg| arg == "HIGHEST"),
        "RunLevel Highest missing: {args:?}"
    );
}

#[test]
fn schtasks_create_tr_quotes_exe_and_startup_argument() {
    let plan = StartupTaskPlan::new("S-1-5-21-9", r"C:\Program Files\L-Mechrevo\L-Mechrevo.exe");
    let args = schtasks_create_args(&plan);
    let tr = args
        .windows(2)
        .find(|pair| pair[0] == "/TR")
        .map(|pair| pair[1].as_str())
        .expect("missing /TR");
    assert!(
        tr.starts_with('"') && tr.contains("\" startup"),
        "Real /TR must quote the exe and pass startup: {tr}"
    );
    assert!(
        tr.contains(r"C:\Program Files\L-Mechrevo\L-Mechrevo.exe"),
        "exe path missing: {tr}"
    );
}

#[test]
fn schtasks_delete_args_target_lmechrevo_sid_task() {
    let plan = StartupTaskPlan::new("S-1-5-21-1", r"C:\Program Files\L-Mechrevo\L-Mechrevo.exe");
    let args = schtasks_delete_args(&plan);
    assert_eq!(args.first().map(String::as_str), Some("/Delete"));
    assert!(
        args.iter().any(|arg| arg == "LMechrevo_S-1-5-21-1"),
        "task name missing: {args:?}"
    );
    assert!(
        args.iter().any(|arg| arg == "/F"),
        "force flag missing: {args:?}"
    );
}

#[test]
fn startup_always_offered_on_empty_itemsupport() {
    let offered = offered_quick_switches(&empty_item(), &Default::default());
    assert!(
        offered.iter().any(|key| *key == "startup"),
        "startup must be offered (StaticQuick): {offered:?}"
    );
}

#[tokio::test]
async fn set_quick_switch_startup_does_not_publish_mqtt() {
    let mut backend = Backend::fake_from_json("{}").expect("parse");
    backend.start().await.expect("start");
    let before = backend.recorded_publishes().len();
    backend
        .set_quick_switch("startup", true)
        .await
        .expect("startup Win32");
    let publishes = backend.recorded_publishes();
    assert_eq!(
        publishes.len(),
        before,
        "startup must not publish MQTT: {publishes:?}"
    );
    assert_eq!(backend.recorded_startup_enabled(), Some(true));
    let name = backend.recorded_startup_task_name().expect("task name");
    assert!(
        name.starts_with("LMechrevo_"),
        "task name must be LMechrevo_{{SID}}, got {name}"
    );
    assert_eq!(
        backend.recorded_startup_arguments().as_deref(),
        Some("startup")
    );
}
