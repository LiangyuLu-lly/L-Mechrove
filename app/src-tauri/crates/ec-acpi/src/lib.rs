//! EC charge-limit via vendor `ACPIDriver` IOCTL. Writes only the 0x7B9/0x7D0 pair.

mod charge_limit;
mod fake;
mod ioctl;

pub use charge_limit::{
    lower_value_for, percent_for, value_for, ChargeLimit, FORBIDDEN, HYSTERESIS, LOWER,
    MAX_PERCENT, MIN_PERCENT, UPPER,
};
pub use fake::{FakeIoctl, IoctlCall};
pub use ioctl::{IoctlTransport, IOCTL_READ, IOCTL_WRITE, READ_IN_LEN, READ_OUT_LEN, WRITE_IN_LEN};

#[cfg(all(windows, feature = "real-ioctl"))]
pub use ioctl::WindowsIoctl;

/// Typed failures for percent checks and ACPI IOCTL.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
#[non_exhaustive]
pub enum Error {
    /// Percent is outside the supported slider range.
    #[error("charge percent {0} is outside 40..=100")]
    PercentOutOfRange(u8),
    /// `DeviceIoControl` (or the fake) rejected this IOCTL code.
    #[error("acpi ioctl 0x{code:08X} failed")]
    Ioctl { code: u32 },
    /// Write succeeded but the register did not read back the written value.
    #[error("charge-limit write did not read back")]
    VerifyMismatch,
    /// `CreateFile` could not open the ACPI device.
    #[error("acpi device unavailable")]
    DeviceUnavailable,
}
