//! Charge-limit encode + IOCTL write-layout goldens.
//!
//! Given / When / Then per test. Transport is `FakeIoctl` — never `\\.\ACPIDriver`.

use std::fs;
use std::path::PathBuf;

use ec_acpi::{
    lower_value_for, percent_for, value_for, ChargeLimit, Error, FakeIoctl, IOCTL_WRITE, LOWER,
    MAX_PERCENT, MIN_PERCENT, UPPER, WRITE_IN_LEN,
};

fn golden_path(name: &str) -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("_golden")
        .join("ec")
        .join(name)
}

fn parse_write(in_bytes: &[u8]) -> (u32, u8) {
    assert_eq!(
        in_bytes.len(),
        WRITE_IN_LEN,
        "write in-buffer must be exactly 5 bytes"
    );
    let addr_bytes: [u8; 4] = in_bytes
        .get(..4)
        .and_then(|s| s.try_into().ok())
        .expect("5-byte write buffer has a u32 address prefix");
    let value = *in_bytes
        .get(4)
        .expect("5-byte write buffer has a value byte");
    (u32::from_le_bytes(addr_bytes), value)
}

fn write_pairs(fake: &FakeIoctl) -> Vec<(u32, u8)> {
    fake.calls()
        .iter()
        .filter(|c| c.code == IOCTL_WRITE)
        .map(|c| parse_write(&c.in_bytes))
        .collect()
}

#[test]
fn value_for_maps_100_to_zero_when_unlimited() {
    // Given: firmware "no limit" sentinel
    // When: encoding 100%
    // Then: register value is 0
    assert_eq!(value_for(100), 0);
}

#[test]
fn lower_value_for_applies_hysteresis_when_80() {
    // Given: 80% upper with 5-point recharge hysteresis
    // When: encoding the lower register
    // Then: 75
    assert_eq!(lower_value_for(80), 75);
}

#[test]
fn percent_for_maps_zero_to_100_when_unlimited() {
    // Given: register 0 (factory unlimited)
    // When: mapping back to a user-visible percent
    // Then: 100
    assert_eq!(percent_for(0), 100);
}

#[test]
fn try_set_80_records_golden_five_byte_writes() {
    // Given: golden S3 vector (80% → 0x7B9=80, 0x7D0=75)
    let path = golden_path("charge_limit_80.json");
    let golden: serde_json::Value =
        serde_json::from_str(&fs::read_to_string(&path).expect("read golden")).expect("json");
    assert_eq!(golden["percent"], 80);
    assert_eq!(golden["hysteresis"], 5);
    assert_eq!(golden["writes"][0]["addr"], 1977);
    assert_eq!(golden["writes"][0]["value"], 80);
    assert_eq!(golden["writes"][1]["addr"], 2000);
    assert_eq!(golden["writes"][1]["value"], 75);

    let fake = FakeIoctl::default();
    let mut limit = ChargeLimit::new(fake);

    // When: setting 80%
    let applied = limit.try_set(80).expect("set 80");

    // Then: applied percent is 80, writes are the golden pair, each 5 bytes
    assert_eq!(applied, 80);
    let fake = limit.into_transport();
    for call in fake.calls().iter().filter(|c| c.code == IOCTL_WRITE) {
        assert_eq!(call.in_bytes.len(), WRITE_IN_LEN);
    }
    let writes = write_pairs(&fake);
    assert_eq!(
        writes,
        vec![(u32::from(UPPER), 80), (u32::from(LOWER), 75),]
    );
    assert_eq!(u32::from(UPPER), 0x7B9);
    assert_eq!(u32::from(LOWER), 0x7D0);
}

#[test]
fn try_set_100_writes_zero_zero_pair() {
    // Given: unlimited / factory pair
    let fake = FakeIoctl::default();
    let mut limit = ChargeLimit::new(fake);

    // When: setting 100%
    let applied = limit.try_set(100).expect("set 100");

    // Then: 100% maps to writes (0x7B9, 0) and (0x7D0, 0)
    assert_eq!(applied, 100);
    let fake = limit.into_transport();
    for call in fake.calls().iter().filter(|c| c.code == IOCTL_WRITE) {
        assert_eq!(call.in_bytes.len(), WRITE_IN_LEN);
    }
    assert_eq!(
        write_pairs(&fake),
        vec![(u32::from(UPPER), 0), (u32::from(LOWER), 0)]
    );
}

#[test]
fn try_set_rejects_percent_outside_40_to_100() {
    // Given: a fresh fake
    let mut limit = ChargeLimit::new(FakeIoctl::default());

    // When: 39 and 101
    // Then: range error, no IOCTL writes
    match limit.try_set(MIN_PERCENT.saturating_sub(1)) {
        Err(Error::PercentOutOfRange(39)) => {}
        other => panic!("expected PercentOutOfRange(39), got {other:?}"),
    }
    match limit.try_set(MAX_PERCENT.saturating_add(1)) {
        Err(Error::PercentOutOfRange(101)) => {}
        other => panic!("expected PercentOutOfRange(101), got {other:?}"),
    }
    assert!(limit
        .transport()
        .calls()
        .iter()
        .all(|c| c.code != IOCTL_WRITE));
}

#[test]
fn try_set_restores_previous_pair_when_lower_write_fails() {
    // Given: previous pair 60/55, lower writes will fail
    let mut fake = FakeIoctl::with_pair(60, 55);
    fake.fail_writes_at(LOWER);
    let mut limit = ChargeLimit::new(fake);

    // When: setting 80% (upper succeeds, lower fails)
    let err = limit.try_set(80).expect_err("lower write must fail");
    match err {
        Error::Ioctl { code } => assert_eq!(code, IOCTL_WRITE),
        other => panic!("expected Ioctl write error, got {other:?}"),
    }

    // Then: hardware pair is restored to 60/55
    let fake = limit.into_transport();
    assert_eq!(fake.pair(), (60, 55));
    let writes = write_pairs(&fake);
    assert!(
        writes.contains(&(u32::from(UPPER), 80)),
        "upper 80 must have been attempted: {writes:?}"
    );
    assert!(
        writes.contains(&(u32::from(UPPER), 60)),
        "upper must be restored to 60: {writes:?}"
    );
}

#[test]
fn every_write_buffer_is_exactly_five_bytes() {
    // Given: several in-range percents
    let fake = FakeIoctl::default();
    let mut limit = ChargeLimit::new(fake);

    // When: setting 40, 73, 80, 100
    for percent in [40_u8, 73, 80, 100] {
        limit.try_set(percent).expect("in-range set");
    }

    // Then: every write in-buffer is 5 bytes [u32 LE addr][u8 value]
    let fake = limit.into_transport();
    let writes: Vec<_> = fake
        .calls()
        .iter()
        .filter(|c| c.code == IOCTL_WRITE)
        .collect();
    assert!(!writes.is_empty());
    for call in writes {
        assert_eq!(call.in_bytes.len(), 5);
        assert_eq!(call.in_bytes.len(), WRITE_IN_LEN);
    }
}
