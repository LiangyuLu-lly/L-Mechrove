//! N8 model gate: MQTT connected OR non-empty ItemSupport. Manual id is a label, not a pin.

use std::fs;
use std::path::Path;

use app_lib::{Backend, HostError};

fn g16_json() -> String {
    let path = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("crates/_golden/item_support_g16_no_lightbar.json");
    fs::read_to_string(&path).unwrap_or_else(|err| panic!("read {}: {err}", path.display()))
}

#[test]
fn empty_itemsupport_without_handshake_is_read_only_with_reason() {
    // Given: Fake with empty ItemSupport, not started
    let backend = Backend::fake_from_json("{}").expect("empty ItemSupport");

    // When: a snapshot is taken
    let snapshot = backend.snapshot();

    // Then: not served → writes locked and the banner has a reason
    assert!(
        !snapshot.write_allowed,
        "empty ItemSupport + no MQTT must not allow writes"
    );
    assert!(
        !snapshot.model_reason.is_empty(),
        "unsupported banner needs a non-empty model_reason"
    );
}

#[tokio::test]
async fn handshake_marks_supported_and_clears_reason() {
    // Given: Fake with empty ItemSupport
    let mut backend = Backend::fake_from_json("{}").expect("empty ItemSupport");

    // When: the fake GCU handshake completes
    backend.start().await.expect("fake handshake");
    let snapshot = backend.snapshot();

    // Then: MQTT connected counts as served
    assert!(
        snapshot.write_allowed,
        "successful handshake must allow writes"
    );
    assert!(
        snapshot.model_reason.is_empty(),
        "supported machine must not show a model_reason"
    );
}

#[test]
fn nonempty_itemsupport_without_handshake_is_usable() {
    // Given: Fake with a non-empty ItemSupport blob, no handshake
    let backend =
        Backend::fake_from_json(r#"{"LightbarSupport":1}"#).expect("non-empty ItemSupport");

    // When: a snapshot is taken
    let snapshot = backend.snapshot();

    // Then: N8 — non-empty ItemSupport counts as served
    assert!(
        snapshot.write_allowed,
        "non-empty ItemSupport must be usable without MQTT"
    );
    assert!(
        snapshot.model_reason.is_empty(),
        "served machine must not show a model_reason"
    );
}

#[test]
fn set_project_id_stores_g16_and_does_not_bypass_itemsupport_lighting() {
    // Given: G16 ItemSupport that hides lightbar/logo
    let mut backend = Backend::fake_from_json(&g16_json()).expect("G16 ItemSupport");

    // When: a manual project id is stored
    backend.set_project_id("G16").expect("store project id");
    let snapshot = backend.snapshot();

    // Then: id is kept, S6 lighting gate is unchanged
    assert_eq!(snapshot.project_id, "G16");
    assert!(
        !snapshot.lighting.lightbar,
        "manual G16 must not reveal lightbar"
    );
    assert!(!snapshot.lighting.logo, "manual G16 must not reveal Logo");
}

#[tokio::test]
async fn unparsable_identity_degrades_to_read_only_never_silent_write() {
    // Given: Fake with empty ItemSupport, not started (identity unknown)
    let mut backend = Backend::fake_from_json("{}").expect("empty ItemSupport");

    // When: an unparsable / unknown id is stored
    backend
        .set_project_id("   ")
        .expect("unparsable id is stored as a label");
    let snapshot = backend.snapshot();

    // Then: still read-only with a reason, and a write is refused
    assert!(
        !snapshot.write_allowed,
        "unparsable identity must not silently allow writes"
    );
    assert!(
        !snapshot.model_reason.is_empty(),
        "read-only degrade must carry a model_reason"
    );
    let err = backend
        .set_performance_mode("gaming")
        .await
        .expect_err("write must fail closed");
    assert!(
        matches!(err, HostError::WritesDisabled),
        "WritesDisabled, got {err}"
    );
}

#[test]
fn unknown_manual_id_does_not_unlock_writes() {
    // Given: not served
    let mut backend = Backend::fake_from_json("{}").expect("empty ItemSupport");

    // When: a manual id that is not a 24-code (and must not be validated as one)
    backend
        .set_project_id("GK7NXXR")
        .expect("store unknown id without 24-code reject");
    let snapshot = backend.snapshot();

    // Then: label stored, writes still locked — no ManualPinned unlock
    assert_eq!(snapshot.project_id, "GK7NXXR");
    assert!(
        !snapshot.write_allowed,
        "manual id must not bypass the N8 served gate"
    );
    assert!(!snapshot.model_reason.is_empty());
}
