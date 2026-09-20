//! Charge-limit pair: upper + recharge-lower. No arbitrary EC address writes.

use crate::ioctl::{
    IoctlTransport, IOCTL_READ, IOCTL_WRITE, READ_IN_LEN, READ_OUT_LEN, WRITE_IN_LEN,
};
use crate::Error;

/// `ADDR_BATTERY_CHARGE_LIMIT_UP`.
pub const UPPER: u16 = 0x7B9;
/// `ADDR_BATTERY_CHARGE_LIMIT_DOWN`.
pub const LOWER: u16 = 0x7D0;
/// Addresses this crate must never write (DBAP / CGLM). Guard for source tests.
pub const FORBIDDEN: [u16; 2] = [0x7A6, 0x78F];
/// Recharge hysteresis in percent points.
pub const HYSTERESIS: u8 = 5;
/// Inclusive slider minimum.
pub const MIN_PERCENT: u8 = 40;
/// Inclusive slider maximum (encoded as 0/0 unlimited).
pub const MAX_PERCENT: u8 = 100;

#[derive(Clone, Copy)]
enum ChargeReg {
    Upper,
    Lower,
}

impl ChargeReg {
    const fn addr(self) -> u16 {
        match self {
            Self::Upper => UPPER,
            Self::Lower => LOWER,
        }
    }
}

fn encode_write(reg: ChargeReg, value: u8) -> [u8; WRITE_IN_LEN] {
    let [b0, b1, b2, b3] = u32::from(reg.addr()).to_le_bytes();
    [b0, b1, b2, b3, value]
}

fn encode_read(reg: ChargeReg) -> [u8; READ_IN_LEN] {
    u32::from(reg.addr()).to_le_bytes()
}

/// Upper register encoding: 100% → 0 (firmware unlimited), else the percent.
#[must_use]
pub const fn value_for(percent: u8) -> u8 {
    if percent >= MAX_PERCENT {
        0
    } else {
        percent
    }
}

/// Lower register: 100% → 0, else percent − hysteresis.
#[must_use]
pub const fn lower_value_for(percent: u8) -> u8 {
    if percent >= MAX_PERCENT {
        0
    } else {
        percent.saturating_sub(HYSTERESIS)
    }
}

/// Register byte → user-visible percent (0 → 100, otherwise clamped to 40..=100).
#[must_use]
pub const fn percent_for(register: u8) -> u8 {
    if register == 0 {
        MAX_PERCENT
    } else if register < MIN_PERCENT {
        MIN_PERCENT
    } else if register > MAX_PERCENT {
        MAX_PERCENT
    } else {
        register
    }
}

/// Charge-limit controller. Public writes take a percent, never a raw EC address.
#[derive(Debug)]
pub struct ChargeLimit<T> {
    transport: T,
}

impl<T> ChargeLimit<T> {
    /// Wrap a transport (fake in tests, Windows device in production).
    #[must_use]
    pub const fn new(transport: T) -> Self {
        Self { transport }
    }

    /// Unwrap the transport (tests inspect recorded IOCTLs).
    #[must_use]
    pub fn into_transport(self) -> T {
        self.transport
    }

    /// Borrow the transport (tests inspect recorded IOCTLs without consuming).
    #[must_use]
    pub const fn transport(&self) -> &T {
        &self.transport
    }
}

impl<T: IoctlTransport> ChargeLimit<T> {
    /// Write the upper/lower pair for `percent` (40..=100). 100% writes 0/0.
    ///
    /// # Errors
    /// [`Error::PercentOutOfRange`], IOCTL failure, or read-back mismatch.
    /// If the lower write fails after the upper succeeded, the previous pair is restored.
    pub fn try_set(&mut self, percent: u8) -> Result<u8, Error> {
        if !(MIN_PERCENT..=MAX_PERCENT).contains(&percent) {
            return Err(Error::PercentOutOfRange(percent));
        }
        let wanted_upper = value_for(percent);
        let wanted_lower = lower_value_for(percent);
        let previous_upper = self.read_reg(ChargeReg::Upper)?;
        let previous_lower = self.read_reg(ChargeReg::Lower)?;
        self.write_reg(ChargeReg::Upper, wanted_upper)?;
        if self.read_reg(ChargeReg::Upper)? != wanted_upper {
            return Err(Error::VerifyMismatch);
        }
        if let Err(err) = self.write_reg(ChargeReg::Lower, wanted_lower) {
            self.restore_pair(previous_upper, previous_lower);
            return Err(err);
        }
        if self.read_reg(ChargeReg::Lower)? != wanted_lower {
            self.restore_pair(previous_upper, previous_lower);
            return Err(Error::VerifyMismatch);
        }
        let applied = percent_for(self.read_reg(ChargeReg::Upper)?);
        if applied != percent_for(wanted_upper) {
            return Err(Error::VerifyMismatch);
        }
        Ok(applied)
    }

    /// Read the current upper register as a user-visible percent.
    ///
    /// # Errors
    /// IOCTL read failure.
    pub fn read_percent(&mut self) -> Result<u8, Error> {
        Ok(percent_for(self.read_reg(ChargeReg::Upper)?))
    }

    fn read_reg(&mut self, reg: ChargeReg) -> Result<u8, Error> {
        let input = encode_read(reg);
        let mut out = [0_u8; READ_OUT_LEN];
        let n = self
            .transport
            .device_io_control(IOCTL_READ, &input, &mut out)?;
        if n == 0 {
            return Err(Error::Ioctl { code: IOCTL_READ });
        }
        let [value, ..] = out;
        Ok(value)
    }

    fn write_reg(&mut self, reg: ChargeReg, value: u8) -> Result<(), Error> {
        let input = encode_write(reg, value);
        let mut out = [0_u8; READ_OUT_LEN];
        let _n = self
            .transport
            .device_io_control(IOCTL_WRITE, &input, &mut out)?;
        Ok(())
    }

    fn restore_pair(&mut self, upper: u8, lower: u8) {
        if self.write_reg(ChargeReg::Upper, upper).is_err() {
            return;
        }
        let _restored = self.write_reg(ChargeReg::Lower, lower).is_ok();
    }
}
