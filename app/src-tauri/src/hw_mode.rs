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
use crate::hw_fake::FakeState;
use crate::hw_mode_profile::ModeProfiles;

pub async fn apply_performance_mode(state: &mut FakeState, mode: &str) -> Result<(), HostError> {
    match mode {
        "office" => publish_pair(&mut state.broker, office_fan(), office_lchwoc()).await,
        "gaming" => publish_pair(&mut state.broker, gaming_fan(), gaming_lchwoc()).await,
        "turbo" => publish_pair(&mut state.broker, turbo_fan(), turbo_lchwoc()).await,
        "custom" => {
            let slot = selected_custom_slot(state)?;
            publish_pair(&mut state.broker, custom_fan(slot), custom_lchwoc()).await
        }
        "silentTurbo" => publish_fan(&mut state.broker, silent_turbo_fan()).await,
        other => Err(HostError::UnknownMode(other.to_owned())),
    }
}

fn selected_custom_slot(state: &mut FakeState) -> Result<ProfileIndex, HostError> {
    if let Some(dir) = state.profile_dir.clone() {
        if let Some(stored) = ModeProfiles::new(&dir).read_custom_profile_index()? {
            state.custom_profile_index = stored;
        }
    }
    ProfileIndex::new(state.custom_profile_index).map_err(Into::into)
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
