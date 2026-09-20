//! Fake WMI brightness recorder. No COM.

/// One `WmiSetBrightness(timeout, brightness)` call.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct WmiWrite {
    pub timeout: u32,
    pub brightness: u8,
}

/// In-memory WMI double. Records writes. Never opens CIM/COM.
#[derive(Debug, Default)]
pub struct FakeWmi {
    writes: Vec<WmiWrite>,
}

impl FakeWmi {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn writes(&self) -> &[WmiWrite] {
        &self.writes
    }

    pub fn set_brightness(&mut self, timeout: u32, brightness: u8) {
        self.writes.push(WmiWrite {
            timeout,
            brightness,
        });
    }
}
