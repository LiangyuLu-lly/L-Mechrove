use std::fs;
use std::path::{Path, PathBuf};

use gcu_mqtt::fake::{FakeBroker, Recorded};
use gcu_mqtt::handshake::{run_handshake, SUBSCRIBE_FILTERS};
use gcu_mqtt::topics;
use serde::Deserialize;
use serde_json::Value;

fn golden_dir() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../_golden/mqtt")
}

fn read_golden(name: &str) -> String {
    let path = golden_dir().join(name);
    fs::read_to_string(&path).unwrap_or_else(|err| panic!("read {}: {err}", path.display()))
}

#[derive(Debug, Deserialize)]
struct GoldenPublish {
    topic: String,
    payload: Value,
}

#[test]
fn topics_are_byte_identical_to_mqtt_topics_cs_when_compared_to_golden() {
    let golden: Value = serde_json::from_str(&read_golden("topics.json")).unwrap();
    let catalog = topics::catalog();
    let obj = golden.as_object().expect("topics.json object");
    assert_eq!(catalog.len(), obj.len());
    for (name, expected) in obj {
        let got = catalog
            .iter()
            .find(|(key, _)| *key == name.as_str())
            .unwrap_or_else(|| panic!("missing topic constant {name}"))
            .1;
        assert_eq!(got, expected.as_str().unwrap(), "{name}");
    }
    assert_eq!(topics::LOGO_LIGHT_FILTER, "HidLightbar_Logo/#");
}

#[tokio::test]
async fn handshake_publish_order_matches_csharp_when_run_against_fake_broker() {
    let golden: Vec<GoldenPublish> =
        serde_json::from_str(&read_golden("handshake_order.json")).unwrap();
    let mut broker = FakeBroker::new();

    run_handshake(&mut broker).await.unwrap();

    let recorded = broker.recorded();
    let first_publish = recorded
        .iter()
        .position(|item| matches!(item, Recorded::Publish { .. }))
        .expect("handshake must publish");
    assert!(
        recorded[..first_publish]
            .iter()
            .all(|item| matches!(item, Recorded::Subscribe(_))),
        "all SUBACKs must complete before System_ON"
    );
    assert!(recorded[first_publish..]
        .iter()
        .all(|item| matches!(item, Recorded::Publish { .. })));
    assert!(recorded[..first_publish].iter().any(|item| {
        matches!(item, Recorded::Subscribe(filter) if filter == "HidLightbar_Logo/#")
    }));
    assert_eq!(first_publish, SUBSCRIBE_FILTERS.len());

    let publishes: Vec<(String, Value)> = recorded[first_publish..]
        .iter()
        .map(|item| match item {
            Recorded::Publish { topic, payload } => (
                topic.clone(),
                serde_json::from_slice(payload).expect("publish payload json"),
            ),
            Recorded::Subscribe(_) => unreachable!("filtered to publishes"),
        })
        .collect();
    assert_eq!(publishes.len(), golden.len());
    for (got, expected) in publishes.iter().zip(golden.iter()) {
        assert_eq!(got.0, expected.topic);
        assert_eq!(got.1, expected.payload);
    }
    assert_eq!(publishes[0].0, "System/Control");
    assert_eq!(publishes[2].1["Action"], "GET_FAN_SPEED_CURVE_SETTING");
}
