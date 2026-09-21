//! SET_OPERATING_MODE_DETAIL. AMD remaps Intel PL keys.
//! No millivolt undervolt MQTT field exists.

use capabilities::ItemSupport;
use gcu_mqtt::client::MqttTransport;
use gcu_mqtt::topics::FAN_CONTROL;
use serde_json::{json, Map, Value};

use crate::hw_error::HostError;

const CUSTOM_DETAIL_FIELDS: &[&str] = &[
    "PL1",
    "PL2",
    "PL4",
    "TGP",
    "CpuTccOffset",
    "CpuTccOffsetSwitch",
    "GpuConfigurableTGPTarget",
    "GpuDynamicBoost",
    "GpuDynamicBoostSwitch",
    "GpuCoreClockOffsetOC",
    "GpuMemoryClockOffsetOC",
    "FanSwitchSpeed",
    "FanSwitchSpeedEnabled",
    "OverClockingSwitch",
];

pub fn is_custom_detail_field(field: &str) -> bool {
    CUSTOM_DETAIL_FIELDS.contains(&field)
}

fn remap_detail_field<'a>(item_support: &ItemSupport, field: &'a str) -> &'a str {
    if !item_support.is_truthy("IsAMDPlatform") {
        return field;
    }
    match field {
        "PL1" => "CpuAmdSPL",
        "PL2" => "CpuAmdSPPT",
        "PL4" => "CpuAmdFPPT",
        "CpuTccOffset" => "CpuAmdTccTarget",
        other => other,
    }
}

pub async fn apply_custom_detail<T: MqttTransport>(
    transport: &mut T,
    item_support: &ItemSupport,
    field: &str,
    value: &str,
) -> Result<(), HostError> {
    match field {
        "ProfileIndex" => match value {
            "0" | "1" | "2" | "3" => Ok(()),
            _ => Err(HostError::UnknownMode(field.to_owned())),
        },
        "ProfileName" => {
            publish_fan(
                transport,
                json!({
                    "Action": "SET_CUSTOM_PROFILE_OSD_STRING",
                    "ProfileName": value,
                }),
            )
            .await
        }
        "RESTORE_OPERATING_MODE_DETAIL" => {
            publish_fan(
                transport,
                json!({ "Action": "RESTORE_OPERATING_MODE_DETAIL" }),
            )
            .await?;
            publish_fan(
                transport,
                json!({
                    "Action": "RESTORE_FAN_SPEED_CURVE_SETTING",
                    "Name": value,
                }),
            )
            .await
        }
        other if CUSTOM_DETAIL_FIELDS.contains(&other) => {
            let wire = remap_detail_field(item_support, other);
            let mut payload = Map::new();
            payload.insert("Action".to_owned(), json!("SET_OPERATING_MODE_DETAIL"));
            payload.insert(wire.to_owned(), Value::String(value.to_owned()));
            publish_fan(transport, Value::Object(payload)).await
        }
        other => Err(HostError::UnknownMode(other.to_owned())),
    }
}

async fn publish_fan<T: MqttTransport>(transport: &mut T, payload: Value) -> Result<(), HostError> {
    let bytes = serde_json::to_vec(&payload)?;
    transport.publish(FAN_CONTROL, &bytes).await?;
    Ok(())
}
