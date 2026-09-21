//! N8 model gate: the vendor service serves this machine, or the host stays read-only.
//!
//! Served = MQTT connected OR non-empty ItemSupport. A manual project id is a stored
//! label for the banner; it does not pin, does not 24-code-validate, and does not
//! unlock lighting writes.

use capabilities::ItemSupport;

const UNSERVED: &str = "unserved";

pub fn is_served(mqtt_connected: bool, item_support: &ItemSupport) -> bool {
    mqtt_connected || !item_support.is_empty()
}

/// Empty when served; a stable token when the vendor service is not serving this machine.
pub fn model_reason(mqtt_connected: bool, item_support: &ItemSupport) -> &'static str {
    if is_served(mqtt_connected, item_support) {
        ""
    } else {
        UNSERVED
    }
}

/// Trimmed label store. No 24-char set, no permanent pin.
#[derive(Debug, Clone, Default)]
pub struct ProjectIdStore {
    id: String,
}

impl ProjectIdStore {
    pub const fn new() -> Self {
        Self { id: String::new() }
    }

    pub fn set(&mut self, id: &str) {
        self.id = id.trim().to_owned();
    }

    pub fn as_str(&self) -> &str {
        &self.id
    }
}
