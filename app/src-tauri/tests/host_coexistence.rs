//! Given leftover GCUBridge / vendor UI detection inputs. When classified. Then prompt only if leftover.

use app_lib::hw_gcu_coexistence::{
    classify, is_leftover_vendor_service, requires_console_removal_prompt, status_from_detection,
    GcuCoexistenceKind,
};

#[test]
fn prompt_omitted_when_nothing_left_over() {
    let kind = classify(false, false);
    assert_eq!(kind, GcuCoexistenceKind::None);
    assert!(!requires_console_removal_prompt(kind));
    assert!(!status_from_detection(false, false).requires_prompt);
}

#[test]
fn prompt_required_when_leftover_bridge_present() {
    let kind = classify(true, false);
    assert_eq!(kind, GcuCoexistenceKind::LeftoverBridge);
    assert!(requires_console_removal_prompt(kind));
    assert!(status_from_detection(true, false).requires_prompt);
}

#[test]
fn prompt_required_when_vendor_ui_present() {
    let kind = classify(false, true);
    assert_eq!(kind, GcuCoexistenceKind::VendorUi);
    assert!(requires_console_removal_prompt(kind));
    assert!(status_from_detection(false, true).requires_prompt);
}

#[test]
fn prompt_required_when_both_left_over() {
    let kind = classify(true, true);
    assert_eq!(kind, GcuCoexistenceKind::Both);
    assert!(requires_console_removal_prompt(kind));
    assert!(status_from_detection(true, true).requires_prompt);
}

#[test]
fn leftover_vendor_service_when_oem_image() {
    assert!(is_leftover_vendor_service(Some(
        r"C:\Program Files\OEM\ControlCenter\GCUService.exe"
    )));
    assert!(is_leftover_vendor_service(Some(
        r#""C:\Program Files\OEM\GCUBridge.exe""#
    )));
}

#[test]
fn leftover_vendor_service_omitted_when_our_image_or_absent() {
    assert!(!is_leftover_vendor_service(None));
    assert!(!is_leftover_vendor_service(Some("")));
    assert!(!is_leftover_vendor_service(Some(
        r"C:\Program Files\L-Mechrevo\GCU\GCUService.exe"
    )));
}
