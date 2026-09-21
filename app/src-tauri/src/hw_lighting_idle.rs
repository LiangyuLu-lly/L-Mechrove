//! C# LightingState idle/battery policy. Sleep is app-side; firmware CloseTimer is never sent.

use crate::hw_backend::HostError;
use crate::hw_fake::FakeState;

/// C# `LightingIdleAction`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Decision {
    None,
    Suspend,
    Restore,
}

pub struct LightingPolicy {
    pub off_on_battery: bool,
    pub idle_seconds: i32,
}

const RESTORE_IDLE_MS: u64 = 2_000;

/// C# `LightingState.ResolveIdleAction` (LightingState.cs:12-19).
pub const fn suspend_decision(idle_ms: u64, timeout_secs: i32, suspended: bool) -> Decision {
    if timeout_secs <= 0 {
        return Decision::None;
    }
    let timeout_ms = (timeout_secs as u64).saturating_mul(1000);
    if !suspended && idle_ms >= timeout_ms {
        return Decision::Suspend;
    }
    if suspended && idle_ms < RESTORE_IDLE_MS {
        return Decision::Restore;
    }
    Decision::None
}

/// C# `LightingState.ShouldSuspendForBattery`.
pub const fn should_suspend_for_battery(option_enabled: bool, on_battery: bool) -> bool {
    option_enabled && on_battery
}

/// Apply idle + battery policy and publish SetPower on ItemSupport-offered channels.
pub async fn reconcile(state: &mut FakeState, policy: LightingPolicy) -> Result<(), HostError> {
    match suspend_decision(state.idle_ms, policy.idle_seconds, state.lighting_suspended) {
        Decision::None => {}
        Decision::Suspend => state.lighting_suspended = true,
        Decision::Restore => state.lighting_suspended = false,
    }
    let power_on = !(should_suspend_for_battery(policy.off_on_battery, state.on_battery)
        || state.lighting_suspended);
    super::publish_offered_power(&mut state.broker, &state.item_support, power_on).await
}
