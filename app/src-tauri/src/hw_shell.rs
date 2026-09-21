//! Taskbar autohide / transparency / dark theme. Same Win32 as ShellPersonalization.cs. Not MQTT.

use crate::hw_backend::Backend;

mod native;

pub const ABS_AUTOHIDE: i32 = 0x1;
pub const ABS_ALWAYSONTOP: i32 = 0x2;
pub const PERSONALIZE_KEY: &str =
    r"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
pub const IMMERSIVE_COLOR_SET: &str = "ImmersiveColorSet";

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RegistryDwordWrite {
    pub key: &'static str,
    pub name: &'static str,
    pub value: u32,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ShellApplyPlan {
    pub writes: Vec<RegistryDwordWrite>,
    pub broadcast: Option<&'static str>,
}

pub fn planned_transparency(on: bool) -> ShellApplyPlan {
    ShellApplyPlan {
        writes: vec![RegistryDwordWrite {
            key: PERSONALIZE_KEY,
            name: "EnableTransparency",
            value: u32::from(on),
        }],
        broadcast: Some(IMMERSIVE_COLOR_SET),
    }
}

pub fn planned_dark_theme(dark: bool) -> ShellApplyPlan {
    let light = light_theme_dword(dark);
    ShellApplyPlan {
        writes: vec![
            RegistryDwordWrite {
                key: PERSONALIZE_KEY,
                name: "AppsUseLightTheme",
                value: light,
            },
            RegistryDwordWrite {
                key: PERSONALIZE_KEY,
                name: "SystemUsesLightTheme",
                value: light,
            },
        ],
        broadcast: Some(IMMERSIVE_COLOR_SET),
    }
}

pub fn planned_reg_add_args(write: &RegistryDwordWrite) -> Vec<String> {
    vec![
        "add".to_owned(),
        write.key.to_owned(),
        "/v".to_owned(),
        write.name.to_owned(),
        "/t".to_owned(),
        "REG_DWORD".to_owned(),
        "/d".to_owned(),
        write.value.to_string(),
        "/f".to_owned(),
    ]
}

pub const fn taskbar_target_state(current: i32, auto_hide: bool) -> i32 {
    if auto_hide {
        current | ABS_AUTOHIDE
    } else {
        current & !ABS_AUTOHIDE
    }
}

pub const fn light_theme_dword(dark: bool) -> u32 {
    if dark {
        0
    } else {
        1
    }
}

pub fn is_shell_key(key: &str) -> bool {
    matches!(key, "taskbarautohide" | "transparency" | "darktheme")
}

#[derive(Debug, Default)]
pub struct RecordingShell {
    applies: Vec<(String, bool)>,
}

impl RecordingShell {
    pub fn apply(&mut self, key: &str, on: bool) {
        self.applies.push((key.to_owned(), on));
    }

    pub fn applies(&self) -> &[(String, bool)] {
        &self.applies
    }
}

#[derive(Debug, thiserror::Error)]
pub enum ShellError {
    #[error("unknown shell key: {0}")]
    UnknownKey(String),
    #[error("shell personalization failed: {0}")]
    Native(String),
}

pub fn apply_fake(shell: &mut RecordingShell, key: &str, on: bool) -> bool {
    if !is_shell_key(key) {
        return false;
    }
    shell.apply(key, on);
    true
}

pub fn system_apply(key: &str, on: bool) -> Result<(), ShellError> {
    match key {
        "taskbarautohide" => native::set_taskbar_auto_hide(on),
        "transparency" => apply_plan(&planned_transparency(on)),
        "darktheme" => apply_plan(&planned_dark_theme(on)),
        _ => Err(ShellError::UnknownKey(key.to_owned())),
    }
}

fn apply_plan(plan: &ShellApplyPlan) -> Result<(), ShellError> {
    for write in &plan.writes {
        registry_set(write)?;
    }
    if plan.broadcast == Some(IMMERSIVE_COLOR_SET) {
        native::broadcast_immersive_color_set();
    }
    Ok(())
}

fn registry_set(write: &RegistryDwordWrite) -> Result<(), ShellError> {
    let args = planned_reg_add_args(write);
    let output = std::process::Command::new("reg")
        .args(&args)
        .output()
        .map_err(|err| ShellError::Native(err.to_string()))?;
    if output.status.success() {
        Ok(())
    } else {
        let stderr = String::from_utf8_lossy(&output.stderr);
        let detail = stderr.trim();
        if detail.is_empty() {
            Err(ShellError::Native(format!("reg exit {}", output.status)))
        } else {
            Err(ShellError::Native(detail.to_owned()))
        }
    }
}

impl Backend {
    pub fn recorded_shell_applies(&self) -> Vec<(String, bool)> {
        match self {
            Self::Fake { state } => state.shell.applies().to_vec(),
            Self::Real { .. } => Vec::new(),
        }
    }
}
