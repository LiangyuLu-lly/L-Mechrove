//! Win32 scheduled-task autostart. Same contract as Helpers/Startup.cs. Not MQTT.

use std::path::Path;

use crate::hw_backend::Backend;

pub const TASK_NAME_PREFIX: &str = "LMechrevo";
pub const STARTUP_ARGUMENT: &str = "startup";
pub const FAKE_SID: &str = "S-1-5-21-0";
const FAKE_EXE: &str = r"C:\Program Files\L-Mechrevo\L-Mechrevo.exe";

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StartupTaskPlan {
    pub task_name: String,
    pub exe_path: String,
    pub arguments: &'static str,
}

impl StartupTaskPlan {
    pub fn new(sid: &str, exe_path: &str) -> Self {
        Self {
            task_name: user_task_name(sid),
            exe_path: normalize_executable_path(exe_path),
            arguments: STARTUP_ARGUMENT,
        }
    }
}

pub fn user_task_name(sid: &str) -> String {
    format!("{TASK_NAME_PREFIX}_{sid}")
}

pub fn sanitize_task_name_fragment(value: &str) -> String {
    if value.trim().is_empty() {
        return "unknown".to_owned();
    }
    value
        .chars()
        .map(|c| {
            if c.is_ascii_alphanumeric() || c == '-' || c == '.' {
                c
            } else {
                '_'
            }
        })
        .collect()
}

pub fn normalize_executable_path(exe_path: &str) -> String {
    exe_path.trim().trim_matches('"').to_owned()
}

pub fn is_transient_executable_path(exe_path: &str, temp_root: &str) -> bool {
    let exe = normalize_executable_path(exe_path);
    if exe.is_empty() || temp_root.trim().is_empty() {
        return false;
    }
    let root = dir_prefix(temp_root);
    Path::new(&exe)
        .to_string_lossy()
        .replace('/', "\\")
        .to_ascii_lowercase()
        .starts_with(&root)
}

fn dir_prefix(root: &str) -> String {
    let mut prefix = root.trim().replace('/', "\\");
    while prefix.ends_with('\\') {
        prefix.pop();
    }
    prefix.push('\\');
    prefix.to_ascii_lowercase()
}

pub fn schtasks_create_args(plan: &StartupTaskPlan) -> Vec<String> {
    vec![
        "/Create".to_owned(),
        "/TN".to_owned(),
        plan.task_name.clone(),
        "/TR".to_owned(),
        format!("\"{}\" {}", plan.exe_path, plan.arguments),
        "/SC".to_owned(),
        "ONLOGON".to_owned(),
        "/DELAY".to_owned(),
        "0000:10".to_owned(),
        "/RL".to_owned(),
        "HIGHEST".to_owned(),
        "/F".to_owned(),
    ]
}

pub fn schtasks_delete_args(plan: &StartupTaskPlan) -> Vec<String> {
    vec![
        "/Delete".to_owned(),
        "/TN".to_owned(),
        plan.task_name.clone(),
        "/F".to_owned(),
    ]
}

#[derive(Debug, Default)]
pub struct RecordingScheduler {
    enabled: Option<bool>,
    last_plan: Option<StartupTaskPlan>,
}

impl RecordingScheduler {
    pub fn apply(&mut self, plan: StartupTaskPlan, on: bool) {
        if self.enabled == Some(on) {
            return;
        }
        self.enabled = Some(on);
        self.last_plan = Some(plan);
    }

    pub fn enabled(&self) -> Option<bool> {
        self.enabled
    }

    pub fn last_plan(&self) -> Option<&StartupTaskPlan> {
        self.last_plan.as_ref()
    }
}

#[derive(Debug, thiserror::Error)]
pub enum StartupError {
    #[error("refusing to register startup from a transient location: {0}")]
    Transient(String),
    #[error("startup task failed: {0}")]
    Task(String),
}

pub fn apply_fake(scheduler: &mut RecordingScheduler, on: bool) {
    scheduler.apply(StartupTaskPlan::new(FAKE_SID, FAKE_EXE), on);
}

pub fn system_apply(on: bool) -> Result<(), StartupError> {
    let exe = std::env::current_exe().map_err(|err| StartupError::Task(err.to_string()))?;
    let exe_str = exe.to_string_lossy();
    let temp = std::env::temp_dir();
    if is_transient_executable_path(&exe_str, &temp.to_string_lossy()) {
        return Err(StartupError::Transient(exe_str.into_owned()));
    }
    let sid = current_user_sid();
    let plan = StartupTaskPlan::new(&sid, &exe_str);
    if on {
        run_schtasks(&schtasks_create_args(&plan))
    } else {
        run_schtasks(&schtasks_delete_args(&plan))
    }
}

fn current_user_sid() -> String {
    let output = std::process::Command::new("whoami")
        .args(["/user", "/fo", "csv", "/nh"])
        .output();
    match output {
        Ok(out) if out.status.success() => parse_whoami_sid(&String::from_utf8_lossy(&out.stdout))
            .unwrap_or_else(|| "unknown".to_owned()),
        _ => "unknown".to_owned(),
    }
}

fn parse_whoami_sid(csv: &str) -> Option<String> {
    let line = csv.lines().next()?.trim();
    let sid = line.rsplit(',').next()?.trim().trim_matches('"');
    sid.starts_with("S-").then(|| sid.to_owned())
}

fn run_schtasks(args: &[String]) -> Result<(), StartupError> {
    let output = std::process::Command::new("schtasks")
        .args(args)
        .output()
        .map_err(|err| StartupError::Task(err.to_string()))?;
    if output.status.success() {
        return Ok(());
    }
    let detail = schtasks_failure_detail(&output);
    if args.first().map(String::as_str) == Some("/Delete") && is_missing_task(&detail) {
        return Ok(());
    }
    Err(StartupError::Task(detail))
}

fn schtasks_failure_detail(output: &std::process::Output) -> String {
    let stderr = String::from_utf8_lossy(&output.stderr);
    let stdout = String::from_utf8_lossy(&output.stdout);
    let err = stderr.trim();
    if !err.is_empty() {
        return err.to_owned();
    }
    let out = stdout.trim();
    if !out.is_empty() {
        return out.to_owned();
    }
    format!("schtasks exit {}", output.status)
}

fn is_missing_task(detail: &str) -> bool {
    let lower = detail.to_ascii_lowercase();
    lower.contains("cannot find") || lower.contains("does not exist")
}

impl Backend {
    pub fn recorded_startup_enabled(&self) -> Option<bool> {
        match self {
            Self::Fake { state } => state.startup.enabled(),
            Self::Real { .. } => None,
        }
    }

    pub fn recorded_startup_task_name(&self) -> Option<String> {
        match self {
            Self::Fake { state } => state.startup.last_plan().map(|plan| plan.task_name.clone()),
            Self::Real { .. } => None,
        }
    }

    pub fn recorded_startup_arguments(&self) -> Option<&'static str> {
        match self {
            Self::Fake { state } => state.startup.last_plan().map(|plan| plan.arguments),
            Self::Real { .. } => None,
        }
    }
}
