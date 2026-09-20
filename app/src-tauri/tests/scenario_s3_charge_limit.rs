//! S3: set_charge_limit(80) writes only 0x7B9/0x7D0 as 5-byte IOCTLs. Never 0x7A6/0x78F.

use app_lib::Backend;
use capabilities::ItemSupport;

fn write_addr(in_bytes: &[u8]) -> u32 {
    assert_eq!(in_bytes.len(), 5, "EC write in-buffer must be 5 bytes");
    u32::from_le_bytes([in_bytes[0], in_bytes[1], in_bytes[2], in_bytes[3]])
}

#[test]
fn s3_charge_limit_80_writes_only_7b9_and_7d0() {
    let mut backend = Backend::fake(ItemSupport::default());
    let applied = backend.set_charge_limit(80).expect("set 80");
    assert_eq!(applied, 80);

    let writes = backend.recorded_ec_writes();
    assert!(!writes.is_empty(), "expected EC writes");
    let addrs: Vec<u32> = writes.iter().map(|(_, bytes)| write_addr(bytes)).collect();
    assert!(addrs.contains(&0x7B9), "missing upper 0x7B9: {addrs:?}");
    assert!(addrs.contains(&0x7D0), "missing lower 0x7D0: {addrs:?}");
    assert!(
        addrs.iter().all(|addr| *addr == 0x7B9 || *addr == 0x7D0),
        "forbidden EC addr in writes: {addrs:?}"
    );
    assert!(!addrs.contains(&0x7A6));
    assert!(!addrs.contains(&0x78F));

    let upper = writes.iter().find(|(_, b)| write_addr(b) == 0x7B9).unwrap();
    let lower = writes.iter().find(|(_, b)| write_addr(b) == 0x7D0).unwrap();
    assert_eq!(upper.1[4], 80);
    assert_eq!(lower.1[4], 75);
    assert_eq!(upper.0, ec_acpi::IOCTL_WRITE);
}
