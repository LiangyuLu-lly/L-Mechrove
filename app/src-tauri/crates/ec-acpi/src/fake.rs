//! In-memory IOCTL transport. Records `(code, in_bytes)`; never opens a device.

use std::collections::HashMap;

use crate::ioctl::{IoctlTransport, IOCTL_READ, IOCTL_WRITE, READ_IN_LEN, WRITE_IN_LEN};
use crate::{Error, LOWER, UPPER};

/// One recorded DeviceIoControl-shaped call.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct IoctlCall {
    /// IOCTL code (`IOCTL_READ` / `IOCTL_WRITE`).
    pub code: u32,
    /// Exact in-buffer bytes sent to the transport.
    pub in_bytes: Vec<u8>,
}

/// Fake ACPI driver. Tests assert write layout against `calls`.
#[derive(Debug)]
pub struct FakeIoctl {
    calls: Vec<IoctlCall>,
    regs: HashMap<u32, u8>,
    fail_addr: Option<u32>,
}

impl FakeIoctl {
    /// Seed upper/lower registers (factory unlimited is `0, 0`).
    #[must_use]
    pub fn with_pair(upper: u8, lower: u8) -> Self {
        let mut regs = HashMap::new();
        regs.insert(u32::from(UPPER), upper);
        regs.insert(u32::from(LOWER), lower);
        Self {
            calls: Vec::new(),
            regs,
            fail_addr: None,
        }
    }

    /// Subsequent writes to `addr` fail after being recorded.
    pub fn fail_writes_at(&mut self, addr: u16) {
        self.fail_addr = Some(u32::from(addr));
    }

    /// Recorded IOCTL calls in order.
    #[must_use]
    pub fn calls(&self) -> &[IoctlCall] {
        &self.calls
    }

    /// Current `(upper, lower)` register pair.
    #[must_use]
    pub fn pair(&self) -> (u8, u8) {
        let upper = self.regs.get(&u32::from(UPPER)).copied().unwrap_or(0);
        let lower = self.regs.get(&u32::from(LOWER)).copied().unwrap_or(0);
        (upper, lower)
    }
}

impl Default for FakeIoctl {
    fn default() -> Self {
        Self::with_pair(0, 0)
    }
}

impl IoctlTransport for FakeIoctl {
    fn device_io_control(
        &mut self,
        code: u32,
        in_bytes: &[u8],
        out: &mut [u8],
    ) -> Result<usize, Error> {
        self.calls.push(IoctlCall {
            code,
            in_bytes: in_bytes.to_vec(),
        });
        if out.is_empty() {
            return Err(Error::Ioctl { code });
        }
        if code == IOCTL_READ {
            return self.read_ec(in_bytes, out);
        }
        if code == IOCTL_WRITE {
            return self.write_ec(in_bytes, out);
        }
        Err(Error::Ioctl { code })
    }
}

impl FakeIoctl {
    fn read_ec(&mut self, in_bytes: &[u8], out: &mut [u8]) -> Result<usize, Error> {
        if in_bytes.len() != READ_IN_LEN {
            return Err(Error::Ioctl { code: IOCTL_READ });
        }
        let mut addr_bytes = [0_u8; 4];
        addr_bytes.copy_from_slice(in_bytes);
        let addr = u32::from_le_bytes(addr_bytes);
        let value = self
            .regs
            .get(&addr)
            .copied()
            .ok_or(Error::Ioctl { code: IOCTL_READ })?;
        match out.first_mut() {
            Some(slot) => *slot = value,
            None => return Err(Error::Ioctl { code: IOCTL_READ }),
        }
        Ok(out.len())
    }

    fn write_ec(&mut self, in_bytes: &[u8], out: &[u8]) -> Result<usize, Error> {
        if in_bytes.len() != WRITE_IN_LEN {
            return Err(Error::Ioctl { code: IOCTL_WRITE });
        }
        let mut buf = [0_u8; WRITE_IN_LEN];
        buf.copy_from_slice(in_bytes);
        let [b0, b1, b2, b3, value] = buf;
        let addr = u32::from_le_bytes([b0, b1, b2, b3]);
        if self.fail_addr == Some(addr) {
            return Err(Error::Ioctl { code: IOCTL_WRITE });
        }
        self.regs.insert(addr, value);
        Ok(out.len())
    }
}
