//! Screen blank (息屏不睡眠). Port of Display/ScreenBlankController.cs.
//! Dim writes WMI brightness 0 then holds thread execution state. Restore writes the saved brightness.

use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use crate::hw_error::HostError;
use crate::hw_exec_state::{self, BLANK_EXECUTION_STATE, ES_CONTINUOUS};

pub const BLANK_LEVEL: u8 = 0;
const MAX_BLANK: Duration = Duration::from_secs(30 * 60);
const POLL_INTERVAL: Duration = Duration::from_millis(300);
const MIN_BLANK_BEFORE_RESTORE: Duration = Duration::from_millis(800);
const RESTORE_IDLE_WINDOW: Duration = Duration::from_millis(400);

pub trait ScreenBlankWmi: Send + Sync {
    fn get_brightness(&self) -> Option<u8>;
    fn set_brightness(&self, brightness: u8) -> Result<(), HostError>;
}

pub trait ScreenBlankExec: Send + Sync {
    fn apply(&self, flags: u32);
}

pub struct InjectedScreenBlankGuard;

#[derive(Clone, Copy)]
struct Controller {
    dimmed: bool,
    saved: Option<u8>,
    started: Option<Instant>,
}

const EMPTY: Controller = Controller {
    dimmed: false,
    saved: None,
    started: None,
};

static INJECTED_WMI: Mutex<Option<Arc<dyn ScreenBlankWmi>>> = Mutex::new(None);
static INJECTED_EXEC: Mutex<Option<Arc<dyn ScreenBlankExec>>> = Mutex::new(None);
static STATE: Mutex<Controller> = Mutex::new(EMPTY);

fn lock_mutex<T>(mutex: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|err| err.into_inner())
}

impl Drop for InjectedScreenBlankGuard {
    fn drop(&mut self) {
        *lock_mutex(&INJECTED_WMI) = None;
        *lock_mutex(&INJECTED_EXEC) = None;
        *lock_mutex(&STATE) = EMPTY;
    }
}

pub fn inject(
    wmi: Arc<dyn ScreenBlankWmi>,
    exec: Arc<dyn ScreenBlankExec>,
) -> InjectedScreenBlankGuard {
    *lock_mutex(&STATE) = EMPTY;
    *lock_mutex(&INJECTED_WMI) = Some(wmi);
    *lock_mutex(&INJECTED_EXEC) = Some(exec);
    InjectedScreenBlankGuard
}

fn read_failed() -> HostError {
    HostError::DisplayDenied("brightness read failed".to_owned())
}

fn current_wmi() -> Result<Arc<dyn ScreenBlankWmi>, HostError> {
    if let Some(wmi) = lock_mutex(&INJECTED_WMI).clone() {
        return Ok(wmi);
    }
    if crate::hw_display::running_as_rust_test_binary() {
        return Err(HostError::RealUnavailable);
    }
    Ok(Arc::new(LiveWmi))
}

fn current_exec() -> Result<Arc<dyn ScreenBlankExec>, HostError> {
    if let Some(exec) = lock_mutex(&INJECTED_EXEC).clone() {
        return Ok(exec);
    }
    if crate::hw_display::running_as_rust_test_binary() {
        return Err(HostError::RealUnavailable);
    }
    Ok(Arc::new(LiveExec))
}

pub fn dim() -> Result<(), HostError> {
    let wmi = current_wmi()?;
    let exec = current_exec()?;
    let injected = lock_mutex(&INJECTED_WMI).is_some();
    let mut state = lock_mutex(&STATE);
    if state.dimmed {
        return Ok(());
    }
    let current = wmi.get_brightness().ok_or_else(read_failed)?;
    wmi.set_brightness(BLANK_LEVEL)?;
    state.saved = Some(current);
    state.started = Some(Instant::now());
    state.dimmed = true;
    exec.apply(BLANK_EXECUTION_STATE);
    drop(state);
    if !injected {
        start_polling();
    }
    Ok(())
}

pub fn restore() {
    let mut state = lock_mutex(&STATE);
    if !state.dimmed {
        return;
    }
    state.dimmed = false;
    let saved = state.saved.take();
    state.started = None;
    drop(state);
    if let Some(level) = saved {
        if let Ok(wmi) = current_wmi() {
            let _ = wmi.set_brightness(level);
        }
    }
    if let Ok(exec) = current_exec() {
        exec.apply(ES_CONTINUOUS);
    }
}

fn start_polling() {
    let _ = std::thread::Builder::new()
        .name("screen-blank-poll".into())
        .spawn(|| loop {
            std::thread::sleep(POLL_INTERVAL);
            let (dimmed, started) = {
                let state = lock_mutex(&STATE);
                (state.dimmed, state.started)
            };
            if !dimmed {
                break;
            }
            let Some(started) = started else { break };
            let elapsed = Instant::now().saturating_duration_since(started);
            if elapsed >= MAX_BLANK
                || (elapsed > MIN_BLANK_BEFORE_RESTORE && idle_time() <= RESTORE_IDLE_WINDOW)
            {
                restore();
                break;
            }
        });
}

fn idle_time() -> Duration {
    #[cfg(all(windows, not(miri)))]
    {
        live_idle::idle().unwrap_or(Duration::MAX)
    }
    #[cfg(not(all(windows, not(miri))))]
    {
        Duration::MAX
    }
}

struct LiveWmi;

impl ScreenBlankWmi for LiveWmi {
    fn get_brightness(&self) -> Option<u8> {
        crate::hw_display::read_brightness_blocking().ok()
    }

    fn set_brightness(&self, brightness: u8) -> Result<(), HostError> {
        crate::hw_display::write_brightness_blocking(brightness)
    }
}

struct LiveExec;

impl ScreenBlankExec for LiveExec {
    fn apply(&self, flags: u32) {
        let on = flags == BLANK_EXECUTION_STATE;
        let _ = hw_exec_state::system_apply(on);
    }
}

#[cfg(all(windows, not(miri)))]
mod live_idle {
    use std::time::Duration;

    #[repr(C)]
    struct LastInputInfo {
        cb_size: u32,
        dw_time: u32,
    }

    #[link(name = "user32")]
    extern "system" {
        fn GetLastInputInfo(plii: *mut LastInputInfo) -> i32;
    }

    #[link(name = "kernel32")]
    extern "system" {
        fn GetTickCount() -> u32;
    }

    pub fn idle() -> Option<Duration> {
        let mut info = LastInputInfo {
            cb_size: u32::try_from(std::mem::size_of::<LastInputInfo>()).ok()?,
            dw_time: 0,
        };
        // SAFETY: [Category 8 — FFI boundary]
        // GetLastInputInfo writes cbSize+dwTime into this stack struct.
        // cbSize is sizeof(LASTINPUTINFO). No other pointers.
        let ok = unsafe { GetLastInputInfo(&mut info) };
        if ok == 0 {
            return None;
        }
        // SAFETY: [Category 8 — FFI boundary]
        // GetTickCount takes no pointers and returns a DWORD tick count.
        let now = unsafe { GetTickCount() };
        Some(Duration::from_millis(u64::from(
            now.wrapping_sub(info.dw_time),
        )))
    }
}
