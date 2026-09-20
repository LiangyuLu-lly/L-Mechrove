//! Guard: this crate must never write DBAP (0x7A6) or CGLM (0x78F).

use std::fs;
use std::path::{Path, PathBuf};

use ec_acpi::{FORBIDDEN, LOWER, UPPER};

fn golden_forbidden() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("_golden")
        .join("ec")
        .join("forbidden_addrs.json")
}

fn src_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("src")
}

fn is_guard_constant_line(line: &str) -> bool {
    let trimmed = line.trim_start();
    trimmed.starts_with("pub const FORBIDDEN") || trimmed.starts_with("const FORBIDDEN")
}

fn contains_forbidden_literal(text: &str) -> bool {
    let lower = text.to_ascii_lowercase();
    lower.contains("0x7a6")
        || lower.contains("0x07a6")
        || lower.contains("0x78f")
        || lower.contains("0x078f")
        || text.contains("1958")
        || text.contains("1935")
}

fn scan_src_file(path: &Path) -> Vec<(usize, String)> {
    let text = fs::read_to_string(path).expect("read src");
    text.lines()
        .enumerate()
        .filter(|(_, line)| !is_guard_constant_line(line) && contains_forbidden_literal(line))
        .map(|(i, line)| (i + 1, line.to_owned()))
        .collect()
}

#[test]
fn forbidden_addrs_match_golden() {
    // Given: the extracted golden list [DBAP, CGLM]
    let expected: [u16; 2] =
        serde_json::from_str(&fs::read_to_string(golden_forbidden()).expect("golden"))
            .expect("json");

    // When: comparing the crate guard constant
    // Then: FORBIDDEN is exactly 0x7A6 / 0x78F
    assert_eq!(expected, [1958, 1935]);
    assert_eq!(FORBIDDEN, expected);
    assert_eq!(FORBIDDEN, [0x7A6, 0x78F]);
}

#[test]
fn src_has_no_forbidden_write_targets_except_guard_constant() {
    // Given: every file under src/
    let mut hits = Vec::new();
    let entries = fs::read_dir(src_dir()).expect("src dir");
    for entry in entries {
        let entry = entry.expect("dirent");
        let path = entry.path();
        if path.extension().and_then(|e| e.to_str()) != Some("rs") {
            continue;
        }
        for (line, text) in scan_src_file(&path) {
            hits.push(format!("{}:{line}: {text}", path.display()));
        }
    }

    // When: scanning for 0x7A6 / 0x78F outside FORBIDDEN
    // Then: no hits — those addresses are not write targets
    assert!(
        hits.is_empty(),
        "forbidden EC addresses appear outside FORBIDDEN: {hits:?}"
    );
}

#[test]
fn public_charge_api_does_not_accept_arbitrary_ec_address() {
    // Given: charge_limit.rs public surface
    let src = fs::read_to_string(src_dir().join("charge_limit.rs")).expect("charge_limit.rs");

    // When: looking for a public write that takes an address
    // Then: only try_set(percent) / read_percent exist — no write(addr, …)
    assert!(src.contains("pub fn try_set"));
    assert!(src.contains("pub fn read_percent"));
    assert!(!src.contains("pub fn write("));
    assert!(!src.contains("pub fn write_ec"));
    assert!(!src.contains("pub fn write_reg"));
    assert!(src.contains("pub fn try_set(&mut self, percent: u8)"));
    assert_ne!(u32::from(UPPER), u32::from(FORBIDDEN[0]));
    assert_ne!(u32::from(LOWER), u32::from(FORBIDDEN[1]));
    assert_ne!(UPPER, FORBIDDEN[0]);
    assert_ne!(LOWER, FORBIDDEN[1]);
}
