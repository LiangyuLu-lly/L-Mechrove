//! Host-side failures. Commands map this to a string at the Tauri boundary.

#[derive(Debug, thiserror::Error)]
#[non_exhaustive]
pub enum HostError {
    #[error(transparent)]
    Mqtt(#[from] gcu_mqtt::client::MqttError),
    #[error(transparent)]
    Connect(#[from] gcu_mqtt::client::ConnectError),
    #[error(transparent)]
    Capabilities(#[from] capabilities::Error),
    #[error(transparent)]
    Json(#[from] serde_json::Error),
    #[error("real GCU backend is not available in Plan A")]
    RealUnavailable,
    #[error("unknown performance mode: {0}")]
    UnknownMode(String),
    #[error(transparent)]
    Ec(#[from] ec_acpi::Error),
    #[error(transparent)]
    Payload(#[from] gcu_mqtt::payloads::PayloadError),
    #[error("gpu action not offered: {0}")]
    GpuActionDenied(String),
    #[error("lighting channel not offered: {0}")]
    LightingDenied(String),
    #[error("lighting effect not offered: {0}")]
    LightingEffectDenied(String),
    #[error("display denied: {0}")]
    DisplayDenied(String),
    #[error("quick switch not offered: {0}")]
    SwitchDenied(String),
    #[error("liquid cooling denied: {0}")]
    LcDenied(String),
    #[error("writes disabled")]
    WritesDisabled,
    #[error(transparent)]
    Hid(#[from] hid_kb::Error),
    #[error(transparent)]
    Io(#[from] std::io::Error),
}
