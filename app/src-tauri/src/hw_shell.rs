//! Taskbar autohide / transparency / dark theme. Same Win32 as ShellPersonalization.cs. Not MQTT.

use crate::hw_backend::Backend;

pub const ABS_AUTOHIDE: i32 = 0x1;
pub const ABS_ALWAYSONTOP: i32 = 0x2;
const PERSONALIZE: &str = r"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

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
        "transparency" => {
            registry_set("EnableTransparency", u32::from(on))?;
            native::broadcast_immersive_color_set();
            Ok(())
        }
        "darktheme" => {
            let light = light_theme_dword(on);
            registry_set("AppsUseLightTheme", light)?;
            registry_set("SystemUsesLightTheme", light)?;
            native::broadcast_immersive_color_set();
            Ok(())
        }
        _ => Err(ShellError::UnknownKey(key.to_owned())),
    }
}

fn registry_set(name: &str, value: u32) -> Result<(), ShellError> {
    let status = std::process::Command::new("reg")
        .args([
            "add",
            PERSONALIZE,
            "/v",
            name,
            "/t",
            "REG_DWORD",
            "/d",
            &value.to_string(),
            "/f",
        ])
        .status()
        .map_err(|err| ShellError::Native(err.to_string()))?;
    if status.success() {
        Ok(())
    } else {
        Err(ShellError::Native(format!("reg exit {status}")))
    }
}

mod native {
    use super::{taskbar_target_state, ShellError, ABS_AUTOHIDE};

    const ABM_GETSTATE: u32 = 0x0000_0004;
    const ABM_SETSTATE: u32 = 0x0000_000A;
    const HWND_BROADCAST: isize = 0xFFFF;
    const WM_SETTINGCHANGE: u32 = 0x001A;
    const SMTO_ABORTIFHUNG: u32 = 0x0002;

    #[repr(C)]
    struct Rect {
        left: i32,
        top: i32,
        right: i32,
        bottom: i32,
    }

    #[repr(C)]
    struct AppBarData {
        cb_size: u32,
        hwnd: isize,
        callback_message: u32,
        edge: u32,
        rc: Rect,
        l_param: isize,
    }

    #[cfg(all(windows, not(miri)))]
    #[link(name = "shell32")]
    extern "system" {
        fn SHAppBarMessage(msg: u32, data: *mut AppBarData) -> usize;
    }

    #[cfg(all(windows, not(miri)))]
    #[link(name = "user32")]
    extern "system" {
        fn SendMessageTimeoutW(
            hwnd: isize,
            msg: u32,
            wparam: isize,
            lparam: *const u16,
            flags: u32,
            timeout: u32,
            result: *mut isize,
        ) -> isize;
    }

    fn empty_app_bar() -> AppBarData {
        AppBarData {
            cb_size: std::mem::size_of::<AppBarData>() as u32,
            hwnd: 0,
            callback_message: 0,
            edge: 0,
            rc: Rect {
                left: 0,
                top: 0,
                right: 0,
                bottom: 0,
            },
            l_param: 0,
        }
    }

    pub fn set_taskbar_auto_hide(auto_hide: bool) -> Result<(), ShellError> {
        #[cfg(all(windows, not(miri)))]
        {
            let mut data = empty_app_bar();
            // SAFETY: [Category 8 — FFI boundary]
            // ABM_GETSTATE reads only cbSize; remaining fields may be zero.
            // `AppBarData` is `repr(C)` matching shell32 APPBARDATA. The pointer
            // is a stack local that lives for the call.
            let state = unsafe { SHAppBarMessage(ABM_GETSTATE, &mut data) } as i32;
            let target = taskbar_target_state(state, auto_hide);
            if target == state {
                return Ok(());
            }
            data.l_param = target as isize;
            // SAFETY: [Category 8 — FFI boundary]
            // ABM_SETSTATE takes the full state word in lParam. Same layout
            // contract as GETSTATE; we only flip ABS_AUTOHIDE.
            unsafe { SHAppBarMessage(ABM_SETSTATE, &mut data) };
            let applied = unsafe { SHAppBarMessage(ABM_GETSTATE, &mut data) } as i32;
            if (applied & ABS_AUTOHIDE != 0) == auto_hide {
                Ok(())
            } else {
                Err(ShellError::Native(
                    "taskbar auto-hide not confirmed".to_owned(),
                ))
            }
        }
        #[cfg(not(all(windows, not(miri))))]
        {
            let _ = auto_hide;
            Err(ShellError::Native("SHAppBarMessage unavailable".to_owned()))
        }
    }

    pub fn broadcast_immersive_color_set() {
        #[cfg(all(windows, not(miri)))]
        {
            let mut wide: Vec<u16> = "ImmersiveColorSet".encode_utf16().collect();
            wide.push(0);
            let mut result = 0isize;
            // SAFETY: [Category 8 — FFI boundary]
            // HWND_BROADCAST is the documented WM_SETTINGCHANGE target.
            // lParam is a NUL-terminated UTF-16 string that lives for the call.
            // SMTO_ABORTIFHUNG + 100ms matches ShellPersonalization.cs.
            unsafe {
                SendMessageTimeoutW(
                    HWND_BROADCAST,
                    WM_SETTINGCHANGE,
                    0,
                    wide.as_ptr(),
                    SMTO_ABORTIFHUNG,
                    100,
                    &mut result,
                );
            }
        }
    }
}

impl Backend {
    pub fn recorded_shell_applies(&self) -> Vec<(String, bool)> {
        match self {
            Self::Fake { state } => state.shell.applies().to_vec(),
            Self::Real => Vec::new(),
        }
    }
}
