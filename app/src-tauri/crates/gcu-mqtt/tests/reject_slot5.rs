use std::fs;
use std::path::Path;
use std::time::Duration;

use gcu_mqtt::client::{mqtt_options, ConnectError, ConnectParams};
use serde_json::Value;

fn slot4_golden() -> Value {
    let path = Path::new(env!("CARGO_MANIFEST_DIR")).join("../_golden/connect_params_slot4.json");
    let text =
        fs::read_to_string(&path).unwrap_or_else(|err| panic!("read {}: {err}", path.display()));
    serde_json::from_str(&text).unwrap()
}

#[test]
fn builder_rejects_uwpclient_5_when_client_id_is_probe_slot() {
    let err = ConnectParams::builder()
        .client_id("UWPClient_5")
        .password("x")
        .build()
        .expect_err("slot 5 must be rejected");
    assert!(matches!(err, ConnectError::ForbiddenSlot5 { .. }));
}

#[test]
fn builder_rejects_client_id_when_it_ends_with_slot5_suffix() {
    let err = ConnectParams::builder()
        .client_id("other_5")
        .password("x")
        .build()
        .expect_err("client_id ending in _5 must be rejected");
    assert!(matches!(err, ConnectError::ForbiddenSlot5 { client_id } if client_id == "other_5"));
}

#[test]
fn builder_accepts_product_slot4_when_defaults_used() {
    let golden = slot4_golden();
    let password = golden["password"].as_str().unwrap();
    let params = ConnectParams::builder().password(password).build().unwrap();
    assert_eq!(params.host(), golden["host"].as_str().unwrap());
    assert_eq!(params.port(), golden["port"].as_u64().unwrap() as u16);
    assert_eq!(params.client_id(), golden["client_id"].as_str().unwrap());
    assert_eq!(params.username(), golden["username"].as_str().unwrap());
    assert_eq!(params.password(), password);
    assert_eq!(
        params.clean_session(),
        golden["clean_session"].as_bool().unwrap()
    );
    assert_eq!(
        params.keepalive_secs(),
        golden["keepalive_secs"].as_u64().unwrap() as u16
    );
    assert_eq!(
        gcu_mqtt::MQTT_PROTOCOL,
        golden["protocol"].as_str().unwrap()
    );
    assert_ne!(params.client_id(), "UWPClient_5");
    assert!(!params.client_id().ends_with("_5"));
}

#[test]
fn mqtt_options_use_keepalive_3s_and_clean_session_when_slot4() {
    let params = ConnectParams::builder()
        .password("host-supplied")
        .build()
        .unwrap();
    let opts = mqtt_options(&params);
    assert_eq!(opts.keep_alive(), Duration::from_secs(3));
    assert!(opts.clean_session());
    assert_eq!(opts.client_id(), "UWPClient_4");
    let (host, port) = opts.broker_address();
    assert_eq!(host, "127.0.0.1");
    assert_eq!(port, 13688);
}
