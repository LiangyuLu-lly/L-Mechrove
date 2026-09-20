//! Usage telemetry. Host-only HTTP. Off until the user opts in.

/// Opt-in flag. Default is off.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Telemetry {
    opted_in: bool,
}

impl Telemetry {
    pub const fn off() -> Self {
        Self { opted_in: false }
    }

    pub const fn opt_in() -> Self {
        Self { opted_in: true }
    }

    pub const fn is_enabled(self) -> bool {
        self.opted_in
    }
}
