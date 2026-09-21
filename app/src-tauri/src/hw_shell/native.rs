use super::{taskbar_target_state, ShellError, ABS_AUTOHIDE, IMMERSIVE_COLOR_SET};

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
        let mut wide: Vec<u16> = IMMERSIVE_COLOR_SET.encode_utf16().collect();
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
