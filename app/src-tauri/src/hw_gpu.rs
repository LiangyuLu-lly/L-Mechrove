//! GPU route MQTT. Setting/Control only.

use std::time::Duration;

use capabilities::{
    DgpuGeneration, FeatureMatrix, GpuRouteGate, ItemSupport, IGPU_ONLY_AUTO, IGPU_ONLY_OFF,
    IGPU_ONLY_ON, RESTART, TOGGLE_IGPU, TOGGLE_OFF, TOGGLE_ON,
};
use gcu_mqtt::client::MqttTransport;
use gcu_mqtt::topics;
use serde::Serialize;

use crate::hw_backend::HostError;

/// C# `GpuRouteCommandLayer.RestartDelayMilliseconds` (CCUWinUI:86511-86515).
const RESTART_DELAY: Duration = Duration::from_millis(800);

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

/// C# `DisplayRouteMatrix` Gen50 ConsoleActions include AUTO; 30/40 hide it.
const fn offers_auto(generation: DgpuGeneration) -> bool {
    matches!(generation, DgpuGeneration::Gen50)
}

pub fn offered_actions(
    item_support: &ItemSupport,
    generation: DgpuGeneration,
    three_mode: bool,
) -> Vec<String> {
    let mut actions: Vec<String> = GpuRouteGate::from_matrix(
        generation,
        three_mode,
        &FeatureMatrix::from_values(item_support),
    )
    .offered_actions()
    .iter()
    .copied()
    .map(str::to_string)
    .collect();
    if offers_auto(generation) {
        // C# DisplayRouteMatrix.cs:201 — AUTO sits after IGPU_ONLY_OFF, before RESTART.
        match actions.iter().position(|action| action == IGPU_ONLY_OFF) {
            Some(index) => actions.insert(index + 1, IGPU_ONLY_AUTO.to_owned()),
            None => actions.push(IGPU_ONLY_AUTO.to_owned()),
        }
    }
    actions
}

fn action_allowed(gate: GpuRouteGate, generation: DgpuGeneration, action: &str) -> bool {
    if action == IGPU_ONLY_AUTO {
        return offers_auto(generation);
    }
    gate.allows(action)
}

/// C# `CreateGpuRestartTargetPayloads` + `BuildRestartCommands` when RESTART is in vocab.
fn route_frames<'a>(action: &'a str, restart_ok: bool) -> Vec<&'a str> {
    if !restart_ok {
        return vec![action];
    }
    match action {
        TOGGLE_ON => vec![TOGGLE_ON, IGPU_ONLY_OFF, TOGGLE_ON, RESTART],
        TOGGLE_OFF => vec![TOGGLE_OFF, RESTART],
        TOGGLE_IGPU => vec![TOGGLE_IGPU, RESTART],
        IGPU_ONLY_AUTO => vec![TOGGLE_OFF, IGPU_ONLY_AUTO, RESTART],
        _ => vec![action],
    }
}

async fn delay_before_restart() {
    // C# waits 800ms before RESTART (CCUWinUI:86511-86515). Timing is not a unit-test
    // claim: cfg!(test) is a no-op so host_gpu does not sleep.
    if cfg!(test) {
        return;
    }
    tokio::time::sleep(RESTART_DELAY).await;
}

pub async fn apply_gpu_route<T: MqttTransport>(
    transport: &mut T,
    item_support: &ItemSupport,
    generation: DgpuGeneration,
    three_mode: bool,
    action: &str,
) -> Result<(), HostError> {
    let gate = GpuRouteGate::from_matrix(
        generation,
        three_mode,
        &FeatureMatrix::from_values(item_support),
    );
    if !action_allowed(gate, generation, action) {
        return Err(HostError::GpuActionDenied(action.to_owned()));
    }
    let restart_ok = gate.allows(RESTART);
    for frame in route_frames(action, restart_ok) {
        if !action_allowed(gate, generation, frame) {
            continue;
        }
        if frame == RESTART {
            delay_before_restart().await;
        }
        let bytes = serde_json::to_vec(&payload_for(frame))?;
        transport.publish(topics::SETTING_CONTROL, &bytes).await?;
    }
    Ok(())
}
