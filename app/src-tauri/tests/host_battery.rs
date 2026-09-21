//! BatteryProtection MQTT goldens + Real EC charge-limit + 充满电.
//!
//! C# writers live in `MechrevoHw.SetBatteryProtection` (not MechrevoService) and
//! `BatteryControl.SetBatteryLimitFull`. Tests never open `\\.\ACPIDriver`.

use app_lib::Backend;
use ec_acpi::{FakeIoctl, MAX_PERCENT};
use gcu_mqtt::topics::BATTERY_PROTECTION_CONTROL;
use serde_json::{json, Value};

fn write_addr(in_bytes: &[u8]) -> u32 {
    assert_eq!(in_bytes.len(), 5, "EC write in-buffer must be 5 bytes");
    u32::from_le_bytes([in_bytes[0], in_bytes[1], in_bytes[2], in_bytes[3]])
}

fn protection_payloads(backend: &Backend) -> Vec<Value> {
    backend
        .recorded_publishes()
        .into_iter()
        .filter(|(topic, _)| topic == BATTERY_PROTECTION_CONTROL)
        .map(|(_, payload)| payload)
        .collect()
}

fn assert_only_charge_pair(writes: &[(u32, Vec<u8>)]) {
    assert!(!writes.is_empty(), "expected EC writes");
    let addrs: Vec<u32> = writes.iter().map(|(_, bytes)| write_addr(bytes)).collect();
    assert!(
        addrs.iter().all(|addr| *addr == 0x7B9 || *addr == 0x7D0),
        "forbidden EC addr in writes: {addrs:?}"
    );
    assert!(!addrs.contains(&0x7A6));
    assert!(!addrs.contains(&0x78F));
}

#[tokio::test]
async fn set_battery_protection_publishes_performancedmode_when_mode_0() {
    // Given: Fake GCU. C# MechrevoHw.cs:2612 mode 0 → PERFORMANCEDMODE
    let mut backend = Backend::fake_from_json("{}").expect("fixture");

    // When: battery protection performance / 满充
    backend
        .set_battery_protection(0)
        .await
        .expect("mode 0");

    // Then: BatteryProtection/Control {"Action":"PERFORMANCEDMODE"}
    let payloads = protection_payloads(&backend);
    assert!(
        payloads
            .iter()
            .any(|payload| payload == &json!({ "Action": "PERFORMANCEDMODE" })),
        "PERFORMANCEDMODE missing: {payloads:?}"
    );
}

#[tokio::test]
async fn set_battery_protection_publishes_balancedmode_when_mode_1() {
    // Given: Fake GCU. C# MechrevoHw.cs:2613 mode 1 → BALANCEDMODE
    let mut backend = Backend::fake_from_json("{}").expect("fixture");

    // When: battery protection balanced
    backend
        .set_battery_protection(1)
        .await
        .expect("mode 1");

    // Then: BatteryProtection/Control {"Action":"BALANCEDMODE"}
    let payloads = protection_payloads(&backend);
    assert!(
        payloads
            .iter()
            .any(|payload| payload == &json!({ "Action": "BALANCEDMODE" })),
        "BALANCEDMODE missing: {payloads:?}"
    );
}

#[tokio::test]
async fn set_battery_protection_publishes_healthymode_when_mode_2() {
    // Given: Fake GCU. C# MechrevoHw.cs:2614 mode 2 → HEALTHYMODE
    let mut backend = Backend::fake_from_json("{}").expect("fixture");

    // When: battery protection healthy
    backend
        .set_battery_protection(2)
        .await
        .expect("mode 2");

    // Then: BatteryProtection/Control {"Action":"HEALTHYMODE"}
    let payloads = protection_payloads(&backend);
    assert!(
        payloads
            .iter()
            .any(|payload| payload == &json!({ "Action": "HEALTHYMODE" })),
        "HEALTHYMODE missing: {payloads:?}"
    );
}

#[tokio::test]
async fn report_battery_protection_publishes_report_get() {
    // Given: Fake GCU. C# MechrevoHw.cs:1255 / 2626 {Report:"GET"}
    let mut backend = Backend::fake_from_json("{}").expect("fixture");

    // When: battery protection report
    backend
        .report_battery_protection()
        .await
        .expect("Report GET");

    // Then: BatteryProtection/Control {"Report":"GET"}
    let payloads = protection_payloads(&backend);
    assert!(
        payloads
            .iter()
            .any(|payload| payload == &json!({ "Report": "GET" })),
        "Report GET missing: {payloads:?}"
    );
}

#[test]
fn real_set_charge_limit_writes_7b9_7d0_when_item_support_is_default() {
    // Given: Real-shaped backend (ItemSupport::default()) + injected FakeIoctl
    let _guard = Backend::inject_ec_transport(FakeIoctl::default());
    let mut backend = Backend::real();
    assert!(
        !backend.snapshot().write_allowed,
        "default ItemSupport must not look served: {:?}",
        backend.snapshot()
    );

    // When: set_charge_limit(80) on the Real arm
    let applied = backend
        .set_charge_limit(80)
        .expect("Real EC path must not return RealUnavailable");

    // Then: live EC pair 0x7B9=80 / 0x7D0=75, never 0x7A6/0x78F
    assert_eq!(applied, 80);
    let writes = backend.recorded_ec_writes();
    assert_only_charge_pair(&writes);
    let upper = writes
        .iter()
        .find(|(_, bytes)| write_addr(bytes) == 0x7B9)
        .expect("missing upper 0x7B9");
    let lower = writes
        .iter()
        .find(|(_, bytes)| write_addr(bytes) == 0x7D0)
        .expect("missing lower 0x7D0");
    assert_eq!(upper.1[4], 80);
    assert_eq!(lower.1[4], 75);
}

#[test]
fn set_charge_full_writes_zero_zero_pair_when_called() {
    // Given: Fake with ChargeLimitSupport so the write path is reachable
    // C# BatteryControl.cs:66-70 SetBatteryLimitFull → MaximumPercent (100 → 0/0)
    let mut backend = Backend::fake_from_json(r#"{"ChargeLimitSupport":1}"#).expect("fixture");

    // When: 充满电
    let applied = backend.set_charge_full().expect("full charge");

    // Then: 100% encodes as 0/0 on 0x7B9/0x7D0
    assert_eq!(applied, MAX_PERCENT);
    let writes = backend.recorded_ec_writes();
    assert_only_charge_pair(&writes);
    let upper = writes
        .iter()
        .find(|(_, bytes)| write_addr(bytes) == 0x7B9)
        .expect("missing upper 0x7B9");
    let lower = writes
        .iter()
        .find(|(_, bytes)| write_addr(bytes) == 0x7D0)
        .expect("missing lower 0x7D0");
    assert_eq!(upper.1[4], 0);
    assert_eq!(lower.1[4], 0);
}
