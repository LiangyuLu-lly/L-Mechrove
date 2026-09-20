/// Three-state HID controller verdict. Unknown must never route to GCU.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FeatureAvailability {
    /// Not probed, or probe timed out / threw. Stay on the HID ladder.
    Unknown,
    /// Deterministic: no compatible ITE8291 interface.
    Unsupported,
    /// Probe connected and entered custom mode.
    Supported,
}

/// Inputs to [`should_use_gcu_keyboard_fallback`], matching C# `ShouldUseGcuKeyboardFallback`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct KeyboardLightPath {
    /// HID controller availability verdict.
    pub hid: FeatureAvailability,
    /// Whether the HID stream is open.
    pub hid_connected: bool,
    /// Whether the GCU MQTT service is connected.
    pub service_connected: bool,
    /// Outcome of a real effect-frame brightness write. Unobserved failure must be `true`.
    pub hid_brightness_took_effect: bool,
}

/// Whether to use the official GCU keyboard channel. Port of `KeyboardLightPathPolicy.cs`.
///
/// Unknown stays HID even when brightness failed. No service → never GCU.
pub const fn should_use_gcu_keyboard_fallback(path: KeyboardLightPath) -> bool {
    if !path.service_connected {
        return false;
    }
    match path.hid {
        FeatureAvailability::Unsupported if !path.hid_connected => true,
        FeatureAvailability::Supported => path.hid_connected && !path.hid_brightness_took_effect,
        FeatureAvailability::Unknown | FeatureAvailability::Unsupported => false,
    }
}
