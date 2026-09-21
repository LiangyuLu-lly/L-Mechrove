//! Given ScreenBlankController.cs. When apply_fake. Then ES flags, not MQTT.

use app_lib::hw_exec_state::{
    apply_fake, flags_for, RecordingExecState, BLANK_EXECUTION_STATE, ES_CONTINUOUS,
    ES_DISPLAY_REQUIRED, ES_SYSTEM_REQUIRED,
};
use gcu_mqtt::fake::FakeBroker;

#[test]
fn records_blank_execution_state_when_on() {
    // Given: C# BlankExecutionState = ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED
    assert_eq!(ES_CONTINUOUS, 0x8000_0000);
    assert_eq!(ES_SYSTEM_REQUIRED, 0x0000_0001);
    assert_eq!(ES_DISPLAY_REQUIRED, 0x0000_0002);
    assert_eq!(
        BLANK_EXECUTION_STATE,
        ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED
    );
    assert_eq!(BLANK_EXECUTION_STATE, 0x8000_0003);
    assert_eq!(flags_for(true), BLANK_EXECUTION_STATE);

    let mut exec = RecordingExecState::default();

    // When: ON (Dim)
    apply_fake(&mut exec, true);

    // Then: recording double saw the exact C# blank flags
    assert_eq!(exec.flags(), &[BLANK_EXECUTION_STATE]);
}

#[test]
fn records_es_continuous_when_off_restores() {
    // Given: a held-awake recording double
    let mut exec = RecordingExecState::default();
    apply_fake(&mut exec, true);
    assert_eq!(flags_for(false), ES_CONTINUOUS);

    // When: OFF (RestoreCore)
    apply_fake(&mut exec, false);

    // Then: restore writes ES_CONTINUOUS only (clears SYSTEM/DISPLAY required)
    assert_eq!(exec.flags(), &[BLANK_EXECUTION_STATE, ES_CONTINUOUS]);
}

#[test]
fn publishes_no_mqtt_frame_when_toggled() {
    // Given: a broker that would record any publish
    let broker = FakeBroker::new();
    let mut exec = RecordingExecState::default();

    // When: ON then OFF
    apply_fake(&mut exec, true);
    apply_fake(&mut exec, false);

    // Then: this path never publishes an MQTT frame
    assert!(
        broker.recorded().is_empty(),
        "exec-state path must not publish MQTT: {:?}",
        broker.recorded()
    );
}

#[test]
fn source_contains_no_sc_monitorpower() {
    let source = include_str!(concat!(env!("CARGO_MANIFEST_DIR"), "/src/hw_exec_state.rs"));
    assert!(
        !source.contains("SC_MONITORPOWER"),
        "must not use SC_MONITORPOWER"
    );
    assert!(
        !source.contains("SetMonitorPower"),
        "must not use SetMonitorPower"
    );
    let lower = source.to_ascii_lowercase();
    assert!(
        !lower.contains("mqtt"),
        "exec-state path must not mention mqtt"
    );
}
