use gcu_mqtt::client::{GcuClient, QoS};

#[test]
fn publish_qos_is_exactly_once_when_matching_csharp_control_commands() {
    assert_eq!(GcuClient::publish_qos(), QoS::ExactlyOnce);
}

#[test]
fn subscribe_qos_is_exactly_once_when_matching_csharp_control_commands() {
    assert_eq!(GcuClient::subscribe_qos(), QoS::ExactlyOnce);
}
