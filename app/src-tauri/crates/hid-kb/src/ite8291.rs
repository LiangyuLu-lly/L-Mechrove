use crate::{Error, HidTransport};

/// ITE8291 vendor ID (`KeyboardRgb.cs`).
pub const VENDOR_ID: u16 = 0x048D;
/// Canglong 16 Pro bonus PID; scoring only, not a hard filter.
pub const PRODUCT_ID: u16 = 0x600B;
/// Preferred RGB usage page (`FF03`).
pub const USAGE_PAGE: u16 = 0xFF03;
/// Enter-custom-mode step 1 feature report (report ID 0).
pub const STEP1: [u8; 9] = [0x00, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00];
/// Enter-custom-mode step 2 feature report.
pub const STEP2: [u8; 9] = [0x00, 0x08, 0x02, 0x33, 0x00, 0x32, 0x00, 0x00, 0x00];
/// Per-frame heartbeat feature report.
pub const STEP3: [u8; 9] = [0x00, 0x12, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00];
const CLEAR_FRAME_LEN: usize = 65;

/// HID enumerate fields used by BetterRGB scoring.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct HidCandidate {
    /// USB product ID.
    pub product_id: u16,
    /// HID usage page. `0xFF03` is preferred.
    pub usage_page: u16,
    /// USB interface number. `1` (`MI_01`) is the fallback.
    pub interface_number: i32,
    /// Output report byte length. `65` is a tie-breaker.
    pub output_report_length: u16,
    /// Feature report byte length. `9` is a tie-breaker.
    pub feature_report_length: u16,
}

/// BetterRGB candidate score. Zero means not an RGB interface.
pub const fn score_candidate(candidate: HidCandidate) -> i32 {
    let mut score = if candidate.usage_page == USAGE_PAGE {
        30
    } else if candidate.interface_number == 1 {
        20
    } else {
        0
    };
    if score == 0 {
        return 0;
    }
    if candidate.product_id == PRODUCT_ID {
        score += 4;
    }
    if candidate.output_report_length == 65 {
        score += 2;
    }
    if candidate.feature_report_length == 9 {
        score += 2;
    }
    score
}

/// ITE8291 protocol helper over any [`HidTransport`].
#[derive(Debug)]
pub struct Ite8291<T> {
    transport: T,
}

impl<T: HidTransport> Ite8291<T> {
    /// Wrap an already-open transport. Does not enumerate hardware.
    pub const fn new(transport: T) -> Self {
        Self { transport }
    }

    /// Borrow the inner transport (FakeHid tests read recorded reports here).
    pub const fn transport(&self) -> &T {
        &self.transport
    }

    /// `SetFeature(step1)` → clear 65-byte frame → `SetFeature(step2)`.
    ///
    /// # Errors
    /// Propagates transport failures. Never opens a device.
    pub fn enter_custom_mode(&mut self) -> Result<(), Error> {
        self.transport.send_feature_report(&STEP1)?;
        self.transport
            .write_output_report(&[0_u8; CLEAR_FRAME_LEN])?;
        self.transport.send_feature_report(&STEP2)?;
        Ok(())
    }
}

/// Production hidapi 2.6 `windows-native` device. Tests must not construct this via [`Self::open`].
#[cfg(windows)]
#[derive(Debug)]
pub struct NativeHid {
    device: hidapi::HidDevice,
}

#[cfg(windows)]
impl NativeHid {
    /// Enumerate VID 048D, pick the best FF03/MI_01 score, open that path.
    ///
    /// # Errors
    /// [`Error::RefusedInTests`] when this crate is under test (no real HID).
    /// [`Error::NoDevice`] when no candidate scores. [`Error::Io`] on hidapi failure.
    pub fn open() -> Result<Self, Error> {
        // Integration tests in tests/ compile this lib without cfg(test).
        // Gate real enumerate behind feature `native-open` (host enables it).
        #[cfg(not(feature = "native-open"))]
        {
            return Err(Error::RefusedInTests);
        }
        #[cfg(feature = "native-open")]
        {
            Self::from_hidapi()
        }
    }

    #[cfg(feature = "native-open")]
    fn from_hidapi() -> Result<Self, Error> {
        let api = hidapi::HidApi::new().map_err(|_| Error::Io)?;
        let mut best_score = 0;
        let mut best: Option<&hidapi::DeviceInfo> = None;
        for info in api.device_list() {
            if info.vendor_id() != VENDOR_ID {
                continue;
            }
            let score = score_candidate(HidCandidate {
                product_id: info.product_id(),
                usage_page: info.usage_page(),
                interface_number: info.interface_number(),
                output_report_length: 0,
                feature_report_length: 0,
            });
            if score > best_score {
                best_score = score;
                best = Some(info);
            }
        }
        let info = best.ok_or(Error::NoDevice)?;
        let device = info.open_device(&api).map_err(|_| Error::Io)?;
        Ok(Self { device })
    }
}

#[cfg(windows)]
impl HidTransport for NativeHid {
    fn send_feature_report(&mut self, report: &[u8]) -> Result<(), Error> {
        self.device
            .send_feature_report(report)
            .map_err(|_| Error::Io)
    }

    fn write_output_report(&mut self, report: &[u8]) -> Result<(), Error> {
        let written = self.device.write(report).map_err(|_| Error::Io)?;
        if written == report.len() {
            Ok(())
        } else {
            Err(Error::Io)
        }
    }
}
