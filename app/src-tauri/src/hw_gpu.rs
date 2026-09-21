//! GPU route MQTT. Setting/Control only. AUTO is never published.

use capabilities::{
    DgpuGeneration, FeatureMatrix, GpuRouteGate, ItemSupport, IGPU_ONLY_AUTO, IGPU_ONLY_OFF,
    IGPU_ONLY_ON,
};
use gcu_mqtt::client::MqttTransport;
use gcu_mqtt::topics;
use serde::Serialize;

use crate::hw_backend::HostError;

#[derive(Serialize)]
struct GpuActionPayload<'a> {
    #[serde(rename = "Action")]
    action: &'a str,
    #[serde(rename = "SetToWMIEC", skip_serializing_if = "Option::is_none")]
    set_to_wmiec: Option<&'a str>,
}

/// N16: only `IGPU_ONLY_CONNECT_RB_ON` / `_OFF` carry `SetToWMIEC="OK"`
/// (CCUWinUI:53543,53607). AUTO and every `DGPU_DIRECT_*` send the action alone
/// (CCUWinUI:53671,85136,85152,85159).
fn payload_for(action: &str) -> GpuActionPayload<'_> {
    match action {
        IGPU_ONLY_ON | IGPU_ONLY_OFF => GpuActionPayload {
            action,
            set_to_wmiec: Some("OK"),
        },
        _ => GpuActionPayload {
            action,
            set_to_wmiec: None,
        },
    }
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

pub async fn apply_gpu_route<T: MqttTransport>(
    transport: &mut T,
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
    let bytes = serde_json::to_vec(&payload_for(action))?;
    transport.publish(topics::SETTING_CONTROL, &bytes).await?;
    Ok(())
}
