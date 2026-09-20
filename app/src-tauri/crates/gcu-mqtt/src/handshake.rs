//! Connect handshake: await all SUBACKs, then `RequestInitialStateAsync` order.

use serde_json::{json, Value};

use crate::client::{MqttError, MqttTransport};
use crate::topics;

/// C# `MechrevoHw.SubscribedTopicFilters`. Logo is a separate filter.
pub const SUBSCRIBE_FILTERS: &[&str] = &[
    topics::SYSTEM_FILTER,
    topics::FAN_FILTER,
    topics::SETTING_FILTER,
    topics::SETTINGS_FILTER,
    topics::CUSTOMIZE_FILTER,
    topics::GPU_DEVICE_FILTER,
    topics::BT_LC_FILTER,
    topics::KEYBOARD_FILTER,
    topics::LIGHTBAR_FILTER,
    topics::LOGO_LIGHT_FILTER,
    topics::LCHWOC_FILTER,
];

/// One GETSTATUS / System_ON / Report=GET publish.
#[derive(Debug, Clone, PartialEq)]
pub struct HandshakePublish {
    /// Control topic.
    pub topic: &'static str,
    /// JSON object payload.
    pub payload: Value,
}

fn action(topic: &'static str, action: &str) -> HandshakePublish {
    HandshakePublish {
        topic,
        payload: json!({ "Action": action }),
    }
}

/// C# `RequestInitialStateAsync` publish list (C# wins over stale goldens).
pub fn handshake_publishes() -> [HandshakePublish; 10] {
    [
        action(topics::SYSTEM_CONTROL, "System_ON"),
        action(topics::FAN_CONTROL, "GETSTATUS"),
        action(topics::FAN_CONTROL, "GET_FAN_SPEED_CURVE_SETTING"),
        action(topics::SETTING_CONTROL, "GETSTATUS"),
        action(topics::LCHWOC_CONTROL, "GETSTATUS"),
        action(topics::KEYBOARD_CTRL, "GETSTATUS"),
        action(topics::LIGHTBAR_CTRL, "GETSTATUS"),
        action(topics::LOGO_LIGHT_CTRL, "GETSTATUS"),
        action(topics::BT_LC_CONTROL, "GETSTATUS"),
        HandshakePublish {
            topic: topics::BATTERY_PROTECTION_CONTROL,
            payload: json!({ "Report": "GET" }),
        },
    ]
}

/// Subscribe every filter (wait SUBACK), then publish the initial-state sequence.
pub async fn run_handshake<T: MqttTransport>(transport: &mut T) -> Result<(), MqttError> {
    for filter in SUBSCRIBE_FILTERS {
        transport.subscribe(filter).await?;
    }
    for step in handshake_publishes() {
        let bytes = serde_json::to_vec(&step.payload)?;
        transport.publish(step.topic, &bytes).await?;
    }
    Ok(())
}
