use hid_kb::{
    score_candidate, should_use_gcu_keyboard_fallback, FakeHid, FeatureAvailability, HidCandidate,
    Ite8291, KeyboardLightPath, PRODUCT_ID, STEP1, STEP2, USAGE_PAGE, VENDOR_ID,
};
#[cfg(windows)]
use hid_kb::{Error, NativeHid};
use serde::Deserialize;

const GOLDEN: &str = include_str!("../../_golden/kb/path_policy.json");

#[derive(Deserialize)]
struct GoldenFile {
    table: Vec<Case>,
    matrix: Vec<Case>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Case {
    hid: String,
    hid_connected: bool,
    service_connected: bool,
    hid_brightness_took_effect: bool,
    use_gcu: bool,
}

fn parse_hid(name: &str) -> FeatureAvailability {
    match name {
        "Unknown" => FeatureAvailability::Unknown,
        "Unsupported" => FeatureAvailability::Unsupported,
        "Supported" => FeatureAvailability::Supported,
        other => panic!("unexpected FeatureAvailability {other}"),
    }
}

fn path_from(case: &Case) -> KeyboardLightPath {
    KeyboardLightPath {
        hid: parse_hid(&case.hid),
        hid_connected: case.hid_connected,
        service_connected: case.service_connected,
        hid_brightness_took_effect: case.hid_brightness_took_effect,
    }
}

fn assert_cases(cases: &[Case]) {
    for case in cases {
        let got = should_use_gcu_keyboard_fallback(path_from(case));
        assert_eq!(
            got,
            case.use_gcu,
            "hid={} connected={} service={} brightness_ok={} expected use_gcu={}",
            case.hid,
            case.hid_connected,
            case.service_connected,
            case.hid_brightness_took_effect,
            case.use_gcu
        );
    }
}

#[test]
fn should_use_gcu_keyboard_fallback_matches_csharp_golden_when_table_driven() {
    let golden: GoldenFile = serde_json::from_str(GOLDEN).expect("golden JSON");
    assert_cases(&golden.table);
    assert_cases(&golden.matrix);
}

#[test]
fn should_use_gcu_when_unsupported_disconnected_and_service_up() {
    let path = KeyboardLightPath {
        hid: FeatureAvailability::Unsupported,
        hid_connected: false,
        service_connected: true,
        hid_brightness_took_effect: true,
    };
    assert!(should_use_gcu_keyboard_fallback(path));
}

#[test]
fn should_use_gcu_when_supported_connected_and_brightness_failed() {
    let path = KeyboardLightPath {
        hid: FeatureAvailability::Supported,
        hid_connected: true,
        service_connected: true,
        hid_brightness_took_effect: false,
    };
    assert!(should_use_gcu_keyboard_fallback(path));
}

#[test]
fn should_stay_on_hid_when_supported_connected_and_brightness_ok() {
    let path = KeyboardLightPath {
        hid: FeatureAvailability::Supported,
        hid_connected: true,
        service_connected: true,
        hid_brightness_took_effect: true,
    };
    assert!(!should_use_gcu_keyboard_fallback(path));
}

#[test]
fn should_never_use_gcu_when_service_is_down() {
    let path = KeyboardLightPath {
        hid: FeatureAvailability::Supported,
        hid_connected: true,
        service_connected: false,
        hid_brightness_took_effect: false,
    };
    assert!(!should_use_gcu_keyboard_fallback(path));
}

#[test]
fn unknown_stays_on_hid_when_brightness_write_failed() {
    let path = KeyboardLightPath {
        hid: FeatureAvailability::Unknown,
        hid_connected: true,
        service_connected: true,
        hid_brightness_took_effect: false,
    };
    assert!(!should_use_gcu_keyboard_fallback(path));
}

#[test]
fn fake_hid_records_feature_reports_when_entering_custom_mode() {
    let mut kb = Ite8291::new(FakeHid::new());
    kb.enter_custom_mode().expect("fake enter");
    let hid = kb.transport();
    assert_eq!(hid.feature_reports(), &[STEP1.to_vec(), STEP2.to_vec()]);
    assert_eq!(hid.output_reports().len(), 1);
    let frame = hid.output_reports().first().expect("clear frame");
    assert_eq!(frame.len(), 65);
    assert!(frame.iter().all(|b| *b == 0));
}

#[cfg(windows)]
#[test]
fn native_open_is_refused_during_crate_tests() {
    assert!(matches!(NativeHid::open(), Err(Error::RefusedInTests)));
}

#[test]
fn score_candidate_prefers_ff03_usage_page_over_mi01() {
    let ff03 = score_candidate(HidCandidate {
        product_id: PRODUCT_ID,
        usage_page: USAGE_PAGE,
        interface_number: 0,
        output_report_length: 65,
        feature_report_length: 9,
    });
    let mi01 = score_candidate(HidCandidate {
        product_id: PRODUCT_ID,
        usage_page: 0,
        interface_number: 1,
        output_report_length: 65,
        feature_report_length: 9,
    });
    assert!(ff03 > mi01);
    assert_eq!(VENDOR_ID, 0x048D);
    assert_eq!(USAGE_PAGE, 0xFF03);
}
