use crate::{Error, HidTransport};

/// In-memory HID double. Records feature and output reports. Never opens a real device.
#[derive(Debug, Default)]
pub struct FakeHid {
    feature_reports: Vec<Vec<u8>>,
    output_reports: Vec<Vec<u8>>,
}

impl FakeHid {
    /// Empty recorder.
    pub fn new() -> Self {
        Self::default()
    }

    /// Recorded feature reports, in send order.
    pub fn feature_reports(&self) -> &[Vec<u8>] {
        &self.feature_reports
    }

    /// Recorded output reports, in send order.
    pub fn output_reports(&self) -> &[Vec<u8>] {
        &self.output_reports
    }
}

impl HidTransport for FakeHid {
    fn send_feature_report(&mut self, report: &[u8]) -> Result<(), Error> {
        self.feature_reports.push(report.to_vec());
        Ok(())
    }

    fn write_output_report(&mut self, report: &[u8]) -> Result<(), Error> {
        self.output_reports.push(report.to_vec());
        Ok(())
    }
}
