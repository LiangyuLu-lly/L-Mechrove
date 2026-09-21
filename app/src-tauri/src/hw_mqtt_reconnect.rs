//! C# `MqttReconnectCoordinator` + `ReconnectLoopAsync` backoff. One client, never slot 5.

/// C# `retryDelayMs = 250`.
pub const INITIAL_RETRY_DELAY_MS: u64 = 250;
/// C# `Math.Min(retryDelayMs * 2, 2000)`.
pub const MAX_RETRY_DELAY_MS: u64 = 2000;

/// Single-flight reconnect gate. A second disconnect only bumps the generation.
#[derive(Debug, Default)]
pub struct ReconnectCoordinator {
    in_loop: bool,
    pending_generation: u32,
    last_handled_generation: u32,
}

impl ReconnectCoordinator {
    pub const fn new() -> Self {
        Self {
            in_loop: false,
            pending_generation: 0,
            last_handled_generation: 0,
        }
    }

    pub fn mark_disconnect_requested(&mut self) {
        self.pending_generation = self.pending_generation.saturating_add(1);
    }

    pub fn try_enter_loop(&mut self) -> bool {
        if self.in_loop {
            return false;
        }
        self.in_loop = true;
        self.last_handled_generation = self.pending_generation;
        true
    }

    pub fn exit_loop(&mut self) -> bool {
        self.in_loop = false;
        self.pending_generation > self.last_handled_generation
    }

    pub const fn should_continue(&self, session_ready: bool, disposed: bool) -> bool {
        !disposed && !session_ready
    }
}

pub fn next_retry_delay_ms(current: u64) -> u64 {
    current.saturating_mul(2).min(MAX_RETRY_DELAY_MS)
}
