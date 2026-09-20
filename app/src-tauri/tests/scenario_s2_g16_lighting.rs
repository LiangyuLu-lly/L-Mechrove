//! S2: G16 ItemSupport without lightbar/logo hides those channels even if MQTT would have Seen.

use std::fs;
use std::path::Path;

use app_lib::Backend;

#[test]
fn s2_g16_itemsupport_hides_lightbar_and_logo() {
    let path = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("crates/_golden/item_support_g16_no_lightbar.json");
    let json = fs::read_to_string(&path).unwrap_or_else(|err| panic!("{}: {err}", path.display()));
    let backend = Backend::fake_from_json(&json).expect("g16 golden");
    let snap = backend.snapshot();
    assert!(!snap.lighting.lightbar, "G16 must hide lightbar");
    assert!(!snap.lighting.logo, "G16 must hide Logo");
}
