//! CCD advanced-colour query. C# `ScreenCCD.GetHDRStatus`, not MQTT.

use std::sync::{Arc, Mutex};

/// C# `IsAdvancedColorEnabled` (MechrevoService.cs:972): state != Off.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AdvancedColorQuery {
    Enabled,
    Disabled,
    Unavailable,
}

/// Test double. Production keeps the inject slot empty and calls user32 CCD.
pub trait AdvancedColorProbe: Send + Sync {
    fn query(&self) -> AdvancedColorQuery;
}

impl AdvancedColorProbe for AdvancedColorQuery {
    fn query(&self) -> AdvancedColorQuery {
        *self
    }
}

/// Clears a test-only CCD probe so CI never opens live display config.
pub struct InjectedAdvancedColorGuard;

impl Drop for InjectedAdvancedColorGuard {
    fn drop(&mut self) {
        *lock_mutex(&INJECTED) = None;
    }
}

static INJECTED: Mutex<Option<Arc<dyn AdvancedColorProbe>>> = Mutex::new(None);

fn lock_mutex<T>(mutex: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|err| err.into_inner())
}

pub fn inject(probe: Arc<dyn AdvancedColorProbe>) -> InjectedAdvancedColorGuard {
    *lock_mutex(&INJECTED) = Some(probe);
    InjectedAdvancedColorGuard
}

fn snapshot_injected() -> Option<Arc<dyn AdvancedColorProbe>> {
    lock_mutex(&INJECTED).clone()
}

fn running_as_rust_test_binary() -> bool {
    let Ok(exe) = std::env::current_exe() else {
        return false;
    };
    let path = exe.to_string_lossy();
    path.contains("deps") && path.contains('-')
}

/// C# `MechrevoService.IsAdvancedColorEnabled` (cs:972) via `ResolveAdvancedColorState` (cs:974-975).
pub const fn is_advanced_color_enabled(hdr: bool, acm: bool) -> bool {
    hdr || acm
}

/// C# `ScreenCCD.cs:69-70` v2 `activeColorMode` (HDR=2, WCG=1).
pub const fn hdr_acm_from_active_color_mode(mode: i32) -> (bool, bool) {
    (mode == 2, mode == 1)
}

/// C# `ScreenCCD.cs:89-90` v1 bitfield.
pub const fn hdr_acm_from_legacy_color_info(
    advanced_color_enabled: bool,
    wide_color_enforced: bool,
    bits_per_color_channel: i32,
) -> (bool, bool) {
    let hdr = advanced_color_enabled && !wide_color_enforced;
    let acm = advanced_color_enabled && wide_color_enforced && bits_per_color_channel > 8;
    (hdr, acm)
}

pub fn query_advanced_color() -> AdvancedColorQuery {
    if let Some(probe) = snapshot_injected() {
        return probe.query();
    }
    if running_as_rust_test_binary() {
        return AdvancedColorQuery::Unavailable;
    }
    live_query()
}

fn live_query() -> AdvancedColorQuery {
    #[cfg(all(windows, not(miri)))]
    {
        win::query()
    }
    #[cfg(not(all(windows, not(miri))))]
    {
        AdvancedColorQuery::Unavailable
    }
}

#[cfg(all(windows, not(miri)))]
mod win;
