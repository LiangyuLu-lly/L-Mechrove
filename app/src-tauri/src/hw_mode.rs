//! Performance-mode MQTT publish. Slot 4 FakeBroker only.

use gcu_mqtt::client::MqttTransport;
use gcu_mqtt::fake::FakeBroker;
use gcu_mqtt::payloads::{
    custom_fan, custom_lchwoc, gaming_fan, gaming_lchwoc, office_fan, office_lchwoc,
    silent_turbo_fan, turbo_fan, turbo_lchwoc, ProfileIndex,
};
use gcu_mqtt::topics;
use serde::Serialize;

use crate::hw_backend::HostError;

pub async fn apply_performance_mode(broker: &mut FakeBroker, mode: &str) -> Result<(), HostError> {
    match mode {
        "office" => publish_pair(broker, office_fan(), office_lchwoc()).await,
        "gaming" => publish_pair(broker, gaming_fan(), gaming_lchwoc()).await,
        "turbo" => publish_pair(broker, turbo_fan(), turbo_lchwoc()).await,
        "custom" => publish_pair(broker, custom_fan(ProfileIndex::new(0)?), custom_lchwoc()).await,
        "silentTurbo" => publish_fan(broker, silent_turbo_fan()).await,
        other => Err(HostError::UnknownMode(other.to_owned())),
    }
}

async fn publish_fan(broker: &mut FakeBroker, fan: impl Serialize) -> Result<(), HostError> {
    let bytes = serde_json::to_vec(&fan)?;
    broker.publish(topics::FAN_CONTROL, &bytes).await?;
    Ok(())
}

async fn publish_pair(
    broker: &mut FakeBroker,
    fan: impl Serialize,
    lchwoc: impl Serialize,
) -> Result<(), HostError> {
    publish_fan(broker, fan).await?;
    let bytes = serde_json::to_vec(&lchwoc)?;
    broker.publish(topics::LCHWOC_CONTROL, &bytes).await?;
    Ok(())
}
