//! In-memory BLE NUS recorder. Never opens a radio.

/// Fake Nordic UART write sink used when MQTT LC is disconnected.
#[derive(Debug, Default)]
pub struct FakeBle {
    writes: Vec<Vec<u8>>,
}

impl FakeBle {
    /// Empty recorder.
    pub fn new() -> Self {
        Self::default()
    }

    /// Recorded NUS frames, in write order.
    pub fn writes(&self) -> &[Vec<u8>] {
        &self.writes
    }

    /// Record one frame. No radio I/O.
    pub fn write(&mut self, frame: &[u8]) {
        self.writes.push(frame.to_vec());
    }
}
