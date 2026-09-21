//! Live AC-line and idle inputs for lighting policy. Injection is the test seam.
//!
//! Inbound `System/BatteryInfo` is charge percent only — not AC vs battery.
//! AC comes from Win32 `GetSystemPowerStatus`. Idle is `GetLastInputInfo`
//! (same API as screen-blank; that module stays untouched).

use std::sync::{Mutex, MutexGuard};

use crate::hw_backend::Backend;

struct Injected {
    on_battery: bool,
    idle_ms: u64,
}

static GATE: Mutex<()> = Mutex::new(());
static INJECTED: Mutex<Option<Injected>> = Mutex::new(None);

fn lock_mutex<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|err| err.into_inner())
}

/// Holds the lighting-input injection until drop. Serializes inject tests.
pub struct LightingInputGuard {
    _exclusive: MutexGuard<'static, ()>,
}

impl Drop for LightingInputGuard {
    fn drop(&mut self) {
        *lock_mutex(&INJECTED) = None;
    }
}

pub fn inject(on_battery: bool, idle_ms: u64) -> LightingInputGuard {
    let exclusive = lock_mutex(&GATE);
    *lock_mutex(&INJECTED) = Some(Injected {
        on_battery,
        idle_ms,
    });
    LightingInputGuard {
        _exclusive: exclusive,
    }
}

pub(crate) fn on_battery() -> bool {
    if let Some(injected) = lock_mutex(&INJECTED).as_ref() {
        return injected.on_battery;
    }
    probe_on_battery()
}

pub(crate) fn idle_ms() -> u64 {
    if let Some(injected) = lock_mutex(&INJECTED).as_ref() {
        return injected.idle_ms;
    }
    probe_idle_ms()
}

impl Backend {
    pub fn inject_lighting_inputs(on_battery: bool, idle_ms: u64) -> LightingInputGuard {
        inject(on_battery, idle_ms)
    }

    pub fn live_on_battery() -> bool {
        on_battery()
    }

    pub fn live_idle_ms() -> u64 {
        idle_ms()
    }
}

#[cfg(all(windows, not(miri)))]
fn probe_on_battery() -> bool {
    live_ac::on_battery()
}

#[cfg(not(all(windows, not(miri))))]
fn probe_on_battery() -> bool {
    false
}

#[cfg(all(windows, not(miri)))]
fn probe_idle_ms() -> u64 {
    live_idle::idle_ms()
}

#[cfg(not(all(windows, not(miri))))]
fn probe_idle_ms() -> u64 {
    0
}

#[cfg(all(windows, not(miri)))]
mod live_ac {
    #[repr(C)]
    struct SystemPowerStatus {
        ac_line_status: u8,
        battery_flag: u8,
        battery_life_percent: u8,
        system_status_flag: u8,
        battery_life_time: u32,
        battery_full_life_time: u32,
    }

    const AC_LINE_OFFLINE: u8 = 0;

    #[link(name = "kernel32")]
    extern "system" {
        fn GetSystemPowerStatus(status: *mut SystemPowerStatus) -> i32;
    }

    pub fn on_battery() -> bool {
        let mut status = SystemPowerStatus {
            ac_line_status: 255,
            battery_flag: 0,
            battery_life_percent: 0,
            system_status_flag: 0,
            battery_life_time: 0,
            battery_full_life_time: 0,
        };
        // SAFETY: [Category 8 — FFI boundary]
        // GetSystemPowerStatus writes the six SYSTEM_POWER_STATUS fields into
        // this stack struct. No other pointers. Failure or ACLineStatus!=0
        // is not on-battery (unknown stays inert).
        let ok = unsafe { GetSystemPowerStatus(&mut status) };
        ok != 0 && status.ac_line_status == AC_LINE_OFFLINE
    }
}

#[cfg(all(windows, not(miri)))]
mod live_idle {
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

    pub fn idle_ms() -> u64 {
        let mut info = LastInputInfo {
            cb_size: u32::try_from(std::mem::size_of::<LastInputInfo>()).unwrap_or(0),
            dw_time: 0,
        };
        if info.cb_size == 0 {
            return 0;
        }
        // SAFETY: [Category 8 — FFI boundary]
        // GetLastInputInfo writes cbSize+dwTime into this stack struct.
        // cbSize is sizeof(LASTINPUTINFO). No other pointers.
        let ok = unsafe { GetLastInputInfo(&mut info) };
        if ok == 0 {
            return 0;
        }
        // SAFETY: [Category 8 — FFI boundary]
        // GetTickCount takes no pointers and returns a DWORD tick count.
        let now = unsafe { GetTickCount() };
        u64::from(now.wrapping_sub(info.dw_time))
    }
}
