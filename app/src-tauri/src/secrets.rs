//! MQTT slot-4 credentials. Never serialized to JS.

use gcu_mqtt::client::{ConnectError, ConnectParams};

const ENV_PASSWORD: &str = "LMECHREVO_MQTT_PASSWORD";
const SLOT4_PASSWORD: &str = "UWPClient_Pwd888881772688_4";

/// Product slot 4 only. Password from env or the C# `UWPClient_Pwd888881772688_4` const.
pub fn slot4_params() -> Result<ConnectParams, ConnectError> {
    let password = std::env::var(ENV_PASSWORD).unwrap_or_else(|_| SLOT4_PASSWORD.to_owned());
    ConnectParams::builder().password(password).build()
}
