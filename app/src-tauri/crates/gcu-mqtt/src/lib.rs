//! Product GCU MQTT client. Slot 4 only (never `UWPClient_5`).

pub mod client;
pub mod eventloop;
pub mod fake;
pub mod handshake;
pub mod payloads;
pub mod topics;

/// MQTT 3.1.1, matching C# `MqttProtocolVersion.V311`.
pub const MQTT_PROTOCOL: &str = "3.1.1";
