//! ITE8291 keyboard HID crate. Tests use [`FakeHid`] only; they never open real HID.

mod fake;
mod ite8291;
mod policy;

pub use fake::FakeHid;
#[cfg(windows)]
pub use ite8291::NativeHid;
pub use ite8291::{
    score_candidate, HidCandidate, Ite8291, PRODUCT_ID, STEP1, STEP2, STEP3, USAGE_PAGE, VENDOR_ID,
};
pub use policy::{should_use_gcu_keyboard_fallback, FeatureAvailability, KeyboardLightPath};

/// HID I/O seam. Production uses hidapi 2.6 `windows-native`; tests use [`FakeHid`].
pub trait HidTransport {
    /// Send a feature report. `report[0]` is the report ID.
    ///
    /// # Errors
    /// Returns [`Error::FeatureRejected`] or [`Error::Io`] when the transport refuses the write.
    fn send_feature_report(&mut self, report: &[u8]) -> Result<(), Error>;

    /// Send an output report. `report[0]` is the report ID.
    ///
    /// # Errors
    /// Returns [`Error::Io`] when the write fails or is short.
    fn write_output_report(&mut self, report: &[u8]) -> Result<(), Error>;
}

/// Typed HID failures. No stringly errors, no `unwrap` in this crate.
#[derive(Debug, thiserror::Error)]
#[non_exhaustive]
pub enum Error {
    /// Underlying HID I/O failed.
    #[error("HID I/O failed")]
    Io,
    /// Feature report was rejected by the device or fake.
    #[error("HID feature report rejected")]
    FeatureRejected,
    /// No VID 048D candidate scored as FF03 or MI_01.
    #[error("no compatible ITE8291 interface (VID 048D, FF03/MI_01)")]
    NoDevice,
    /// [`NativeHid::open`] must not enumerate hardware while `hid-kb` itself is under test.
    #[error("native HID open is refused while testing hid-kb")]
    RefusedInTests,
}
