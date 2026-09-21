use gcu_mqtt::client::{GcuClient, QoS};

#[test]
fn publish_qos_is_exactly_once_when_matching_csharp_control_commands() {
    assert_eq!(GcuClient::publish_qos(), QoS::ExactlyOnce);
}

#[test]
fn subscribe_qos_is_exactly_once_when_matching_csharp_control_commands() {
    assert_eq!(GcuClient::subscribe_qos(), QoS::ExactlyOnce);
}

#[test]
fn system_off_qos_is_at_most_once_because_qos2_dies_on_clean_session_disconnect() {
    // Given: C# StopTelemetryBeforeExit
    // When: System_OFF is published
    // Then: QoS0 — a QoS2 four-step handshake dies on a clean-session disconnect
    assert_eq!(GcuClient::system_off_qos(), QoS::AtMostOnce);
}
