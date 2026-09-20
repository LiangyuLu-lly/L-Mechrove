//! GPU route MQTT. Setting/Control only. AUTO is never published.

use capabilities::{DgpuGeneration, FeatureMatrix, GpuRouteGate, ItemSupport, IGPU_ONLY_AUTO};
use gcu_mqtt::client::MqttTransport;
use gcu_mqtt::fake::FakeBroker;
use gcu_mqtt::topics;
use serde::Serialize;

use crate::hw_backend::HostError;

#[derive(Serialize)]
struct GpuActionPayload<'a> {
    #[serde(rename = "Action")]
    action: &'a str,
}

pub fn offered_actions(
    item_support: &ItemSupport,
    generation: DgpuGeneration,
    three_mode: bool,
) -> Vec<String> {
    GpuRouteGate::from_matrix(
        generation,
        three_mode,
        &FeatureMatrix::from_values(item_support),
    )
    .offered_actions()
    .iter()
    .copied()
    .filter(|action| *action != IGPU_ONLY_AUTO)
    .map(str::to_string)
    .collect()
}

pub async fn apply_gpu_route(
    broker: &mut FakeBroker,
    item_support: &ItemSupport,
    generation: DgpuGeneration,
    three_mode: bool,
    action: &str,
) -> Result<(), HostError> {
    if action == IGPU_ONLY_AUTO {
        return Err(HostError::GpuActionDenied(action.to_owned()));
    }
    let gate = GpuRouteGate::from_matrix(
        generation,
        three_mode,
        &FeatureMatrix::from_values(item_support),
    );
    if !gate.allows(action) {
        return Err(HostError::GpuActionDenied(action.to_owned()));
    }
    let bytes = serde_json::to_vec(&GpuActionPayload { action })?;
    broker.publish(topics::SETTING_CONTROL, &bytes).await?;
    Ok(())
}
