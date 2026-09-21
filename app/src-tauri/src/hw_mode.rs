//! Performance-mode MQTT publish. Slot 4 transport only.

use gcu_mqtt::client::MqttTransport;
use gcu_mqtt::payloads::{
    custom_fan, custom_lchwoc, gaming_fan, gaming_lchwoc, office_fan, office_lchwoc,
    silent_turbo_fan, turbo_fan, turbo_lchwoc, ProfileIndex,
};
use gcu_mqtt::topics;
use serde::Serialize;

use std::path::Path;

use crate::hw_backend::HostError;
use crate::hw_mode_profile::ModeProfiles;

pub async fn apply_performance_mode<T: MqttTransport>(
    transport: &mut T,
    mode: &str,
    custom_slot: ProfileIndex,
) -> Result<(), HostError> {
    match mode {
        "office" => publish_pair(transport, office_fan(), office_lchwoc()).await,
        "gaming" => publish_pair(transport, gaming_fan(), gaming_lchwoc()).await,
        "turbo" => publish_pair(transport, turbo_fan(), turbo_lchwoc()).await,
        "custom" => publish_pair(transport, custom_fan(custom_slot), custom_lchwoc()).await,
        "silentTurbo" => publish_fan(transport, silent_turbo_fan()).await,
        other => Err(HostError::UnknownMode(other.to_owned())),
    }
}

pub(crate) fn selected_custom_slot(
    profile_dir: Option<&Path>,
    stored: &mut u8,
) -> Result<ProfileIndex, HostError> {
    if let Some(dir) = profile_dir {
        if let Some(index) = ModeProfiles::new(dir).read_custom_profile_index()? {
            *stored = index;
        }
    }
    ProfileIndex::new(*stored).map_err(Into::into)
}

async fn publish_fan<T: MqttTransport>(
    transport: &mut T,
    fan: impl Serialize,
) -> Result<(), HostError> {
    let bytes = serde_json::to_vec(&fan)?;
    transport.publish(topics::FAN_CONTROL, &bytes).await?;
    Ok(())
}

async fn publish_pair<T: MqttTransport>(
    transport: &mut T,
    fan: impl Serialize,
    lchwoc: impl Serialize,
) -> Result<(), HostError> {
    publish_fan(transport, fan).await?;
    let bytes = serde_json::to_vec(&lchwoc)?;
    transport.publish(topics::LCHWOC_CONTROL, &bytes).await?;
    Ok(())
}
