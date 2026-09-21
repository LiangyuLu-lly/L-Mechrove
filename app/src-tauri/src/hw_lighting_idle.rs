//! C# LightingState idle/battery policy. Sleep is app-side; firmware CloseTimer is never sent.

use std::sync::atomic::{AtomicBool, Ordering};

use capabilities::ItemSupport;
use gcu_mqtt::client::MqttTransport;
use hid_kb::HidTransport;

use crate::hw_backend::{Backend, HostError};
use crate::hw_lighting_cfg::LightParams;

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

static REAL_LIGHTING_SUSPENDED: AtomicBool = AtomicBool::new(false);

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
pub async fn reconcile<T: MqttTransport>(
    transport: &mut T,
    item_support: &ItemSupport,
    on_battery: bool,
    idle_ms: u64,
    lighting_suspended: &mut bool,
    policy: LightingPolicy,
) -> Result<(), HostError> {
    match suspend_decision(idle_ms, policy.idle_seconds, *lighting_suspended) {
        Decision::None => {}
        Decision::Suspend => *lighting_suspended = true,
        Decision::Restore => *lighting_suspended = false,
    }
    let power_on =
        !(should_suspend_for_battery(policy.off_on_battery, on_battery) || *lighting_suspended);
    super::publish_offered_power(transport, item_support, power_on).await
}

pub(crate) fn take_real_suspended() -> bool {
    REAL_LIGHTING_SUSPENDED.load(Ordering::Relaxed)
}

pub(crate) fn store_real_suspended(suspended: bool) {
    REAL_LIGHTING_SUSPENDED.store(suspended, Ordering::Relaxed);
}

impl Backend {
    /// Hardware-free Real write: live ItemSupport + any `MqttTransport`.
    pub async fn apply_light_power_on<T: MqttTransport>(
        transport: &mut T,
        item_support: &ItemSupport,
        channel: &str,
        on: bool,
    ) -> Result<(), HostError> {
        super::apply_light_power(transport, item_support, None, channel, on).await
    }

    /// Hardware-free Real write: live ItemSupport + official SetEffectALL.
    pub async fn apply_light_effect_on<T: MqttTransport>(
        transport: &mut T,
        item_support: &ItemSupport,
        channel: &str,
        effect: &str,
        light: Option<&str>,
        speed: Option<&str>,
        color: Option<&str>,
    ) -> Result<(), HostError> {
        super::apply_light_effect(
            transport,
            item_support,
            None,
            channel,
            effect,
            LightParams {
                light,
                speed,
                color,
            },
        )
        .await
    }

    /// Hardware-free Real reconcile. Returns the updated suspended flag.
    pub async fn reconcile_lighting_on<T: MqttTransport>(
        transport: &mut T,
        item_support: &ItemSupport,
        off_on_battery: bool,
        idle_seconds: i32,
        on_battery: bool,
        idle_ms: u64,
        lighting_suspended: bool,
    ) -> Result<bool, HostError> {
        let mut suspended = lighting_suspended;
        reconcile(
            transport,
            item_support,
            on_battery,
            idle_ms,
            &mut suspended,
            LightingPolicy {
                off_on_battery,
                idle_seconds,
            },
        )
        .await?;
        Ok(suspended)
    }

    /// HID keyboard path. Device absent → fail closed and set `keyboardHidUnavailable`.
    pub fn apply_keyboard_hid<T: HidTransport>(
        &mut self,
        hid: Result<T, hid_kb::Error>,
    ) -> Result<(), HostError> {
        match hid {
            Ok(_) => {
                self.set_keyboard_hid_unavailable(false);
                Ok(())
            }
            Err(err) => {
                self.set_keyboard_hid_unavailable(true);
                Err(HostError::Hid(err))
            }
        }
    }

    fn set_keyboard_hid_unavailable(&mut self, unavailable: bool) {
        if let Self::Real { state } = self {
            state.keyboard_hid_unavailable = unavailable;
        }
    }
}
