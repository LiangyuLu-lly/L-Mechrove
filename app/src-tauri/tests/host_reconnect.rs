//! Given C# MqttReconnectCoordinator. When disconnects overlap. Then single-flight + 250ms×2 cap 2s.

use app_lib::hw_mqtt_reconnect::{
    next_retry_delay_ms, ReconnectCoordinator, INITIAL_RETRY_DELAY_MS, MAX_RETRY_DELAY_MS,
};

#[test]
fn try_enter_loop_is_single_flight() {
    // Given: a coordinator not in the loop
    let mut coord = ReconnectCoordinator::new();
    coord.mark_disconnect_requested();

    // When: two enters are attempted
    let first = coord.try_enter_loop();
    let second = coord.try_enter_loop();

    // Then: only the first owns the loop
    assert!(first);
    assert!(!second);
}

#[test]
fn exit_loop_reenters_when_disconnect_arrived_in_flight() {
    // Given: a running loop that saw another disconnect
    let mut coord = ReconnectCoordinator::new();
    coord.mark_disconnect_requested();
    assert!(coord.try_enter_loop());
    coord.mark_disconnect_requested();

    // When: the loop exits
    let reenter = coord.exit_loop();

    // Then: a new generation is pending
    assert!(reenter);
}

#[test]
fn retry_delay_doubles_from_250ms_and_caps_at_2s() {
    // Given: C# retryDelayMs = 250, then *2, cap 2000
    let mut delay = INITIAL_RETRY_DELAY_MS;

    // When: four backoffs run
    delay = next_retry_delay_ms(delay);
    assert_eq!(delay, 500);
    delay = next_retry_delay_ms(delay);
    assert_eq!(delay, 1000);
    delay = next_retry_delay_ms(delay);
    assert_eq!(delay, 2000);
    delay = next_retry_delay_ms(delay);

    // Then: the cap holds
    assert_eq!(delay, MAX_RETRY_DELAY_MS);
    assert_eq!(INITIAL_RETRY_DELAY_MS, 250);
    assert_eq!(MAX_RETRY_DELAY_MS, 2000);
}

#[test]
fn should_continue_stops_when_session_ready_or_disposed() {
    // Given: a coordinator
    let coord = ReconnectCoordinator::new();

    // When/Then: C# ShouldContinue(!disposed && !sessionReady)
    assert!(coord.should_continue(false, false));
    assert!(!coord.should_continue(true, false));
    assert!(!coord.should_continue(false, true));
}
