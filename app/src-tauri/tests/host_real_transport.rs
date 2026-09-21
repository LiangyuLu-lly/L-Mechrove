//! Hardware-free Real transport: apply_* against a non-Fake MQTT double. No TCP.

use std::future::Future;

use app_lib::{apply_gpu_route, slot4_params};
use capabilities::{DgpuGeneration, ItemSupport, IGPU_ONLY_ON};
use gcu_mqtt::client::{MqttError, MqttTransport};
use gcu_mqtt::fake::Recorded;
use gcu_mqtt::topics::SETTING_CONTROL;

struct RecordingTransport {
    recorded: Vec<Recorded>,
}

impl RecordingTransport {
    fn new() -> Self {
        Self {
            recorded: Vec::new(),
        }
    }
}

impl MqttTransport for RecordingTransport {
    fn subscribe(&mut self, filter: &str) -> impl Future<Output = Result<(), MqttError>> + Send {
        self.recorded.push(Recorded::Subscribe(filter.to_owned()));
        async { Ok(()) }
    }

    fn publish(
        &mut self,
        topic: &str,
        payload: &[u8],
    ) -> impl Future<Output = Result<(), MqttError>> + Send {
        self.recorded.push(Recorded::Publish {
            topic: topic.to_owned(),
            payload: payload.to_vec(),
        });
        async { Ok(()) }
    }
}

#[tokio::test]
async fn apply_gpu_route_publishes_n16_igpu_payload_when_transport_is_not_fake() {
    // Given: a non-Fake MqttTransport and N16 IGPU_ONLY_ON
    let mut transport = RecordingTransport::new();
    let item_support = ItemSupport::default();

    // When: apply_gpu_route runs against that transport
    apply_gpu_route(
        &mut transport,
        &item_support,
        DgpuGeneration::Gen50,
        true,
        IGPU_ONLY_ON,
    )
    .await
    .expect("Gen50 IGPU_ONLY_ON");

    // Then: Setting/Control carries Action + SetToWMIEC=OK
    let payload = transport
        .recorded
        .iter()
        .find_map(|item| match item {
            Recorded::Publish { topic, payload } if topic == SETTING_CONTROL => {
                serde_json::from_slice::<serde_json::Value>(payload).ok()
            }
            _ => None,
        })
        .expect("Setting/Control publish");
    assert_eq!(payload["Action"], IGPU_ONLY_ON);
    assert_eq!(
        payload.get("SetToWMIEC").and_then(serde_json::Value::as_str),
        Some("OK"),
        "N16 IGPU_ONLY_ON must carry SetToWMIEC=OK: {payload}"
    );
}

#[test]
fn slot4_params_uses_loopback_13688_and_slot4_client_id() {
    // Given: product slot-4 secrets
    // When: slot4_params is built
    let params = slot4_params().expect("slot4");

    // Then: loopback 13688, client id contains _4, never _5
    assert_eq!(params.host(), "127.0.0.1");
    assert_eq!(params.port(), 13688);
    assert!(
        params.client_id().contains("_4"),
        "client id must contain _4: {}",
        params.client_id()
    );
    assert!(
        !params.client_id().contains("_5"),
        "client id must never contain _5: {}",
        params.client_id()
    );
    assert_ne!(params.client_id(), "UWPClient_5");
}

#[test]
fn app_never_constructs_uwpclient_5_client_id() {
    // Given: app crate sources
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("src");

    // When: every .rs file is scanned for UWPClient_5
    let mut hits = Vec::new();
    scan_rs(&root, &mut hits);

    // Then: the app never constructs that client id
    assert!(
        hits.is_empty(),
        "app must never construct UWPClient_5: {hits:?}"
    );
    let params = slot4_params().expect("slot4");
    assert!(
        !params.client_id().contains("UWPClient_5"),
        "slot4_params client id: {}",
        params.client_id()
    );
}

fn scan_rs(dir: &std::path::Path, hits: &mut Vec<String>) {
    let entries = std::fs::read_dir(dir)
        .unwrap_or_else(|err| panic!("read {}: {err}", dir.display()));
    for entry in entries {
        let entry = entry.expect("dirent");
        let path = entry.path();
        if path.is_dir() {
            scan_rs(&path, hits);
            continue;
        }
        if path.extension().and_then(|ext| ext.to_str()) != Some("rs") {
            continue;
        }
        let text = std::fs::read_to_string(&path)
            .unwrap_or_else(|err| panic!("read {}: {err}", path.display()));
        if text.contains("UWPClient_5") {
            hits.push(path.display().to_string());
        }
    }
}
