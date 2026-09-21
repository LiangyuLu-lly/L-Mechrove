//! Given update policy + JS DTO. When URL/serde run. Then HTTPS stats host only.

use std::sync::Mutex;

use app_lib::updates::updates_install::{
    cache_offer, recorded_actions, reset_host, CachedOffer, FakeAction, InstallError,
};
use app_lib::updates::{accept_download_url, check_url, DownloadUrlError, UpdateDto};
use app_lib::Backend;

static GATE: Mutex<()> = Mutex::new(());

const VALID_SHA256: &str = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const VALID_URL: &str = "https://stats.l-mechrevo.cn/pkg.exe";
const VALID_SIZE: u64 = 1024;

fn seeded(offer: CachedOffer) -> (std::sync::MutexGuard<'static, ()>, Backend) {
    let gate = GATE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    reset_host();
    cache_offer(offer);
    let backend = Backend::fake_from_json("{}").expect("parse");
    (gate, backend)
}

fn valid_offer() -> CachedOffer {
    CachedOffer {
        download_url: VALID_URL.to_owned(),
        sha256: VALID_SHA256.to_owned(),
        size: VALID_SIZE,
    }
}

#[test]
fn rejects_http_non_loopback_download_url() {
    let err = accept_download_url("http://evil.example/pkg.exe");
    assert!(
        err.is_err(),
        "http non-loopback download_url must be rejected: {err:?}"
    );
}

#[test]
fn accepts_https_stats_host() {
    assert_eq!(
        check_url("", ""),
        "https://stats.l-mechrevo.cn/api/update_check.php?version=&channel="
    );
    accept_download_url("https://stats.l-mechrevo.cn/pkg.exe")
        .expect("https stats.l-mechrevo.cn download_url must be accepted");
}

#[test]
fn dto_serde_exactly_update_available_and_latest_version() {
    let dto = UpdateDto {
        update_available: true,
        latest_version: "5.56.60.26".to_owned(),
    };

    let value = serde_json::to_value(&dto).expect("serialize UpdateDto");
    let obj = value.as_object().expect("UpdateDto JSON object");
    assert_eq!(obj.len(), 2, "JS DTO must have exactly two keys: {obj:?}");
    assert_eq!(obj.get("updateAvailable"), Some(&serde_json::json!(true)));
    assert_eq!(
        obj.get("latestVersion"),
        Some(&serde_json::json!("5.56.60.26"))
    );

    let round_trip: UpdateDto = serde_json::from_value(value).expect("camelCase deserialize");
    assert_eq!(round_trip, dto);
}

#[test]
fn updates_install_refuses_when_cached_sha256_invalid() {
    let (_gate, backend) = seeded(CachedOffer {
        download_url: VALID_URL.to_owned(),
        sha256: "not-64-hex".to_owned(),
        size: VALID_SIZE,
    });

    let err = backend
        .updates_install(true)
        .expect_err("install must refuse without a valid sha256");

    assert!(
        matches!(err, InstallError::InvalidSha256),
        "expected InvalidSha256, got {err:?}"
    );
    assert!(
        recorded_actions().is_empty(),
        "refused install must not record: {:?}",
        recorded_actions()
    );
}

#[test]
fn updates_install_refuses_when_download_url_http_non_loopback() {
    let (_gate, backend) = seeded(CachedOffer {
        download_url: "http://evil.example/pkg.exe".to_owned(),
        sha256: VALID_SHA256.to_owned(),
        size: VALID_SIZE,
    });

    let err = backend
        .updates_install(true)
        .expect_err("http non-loopback download_url must be rejected");

    assert!(
        matches!(err, InstallError::Url(DownloadUrlError::Insecure)),
        "expected Url(Insecure), got {err:?}"
    );
    assert!(
        recorded_actions().is_empty(),
        "refused install must not record: {:?}",
        recorded_actions()
    );
}

#[test]
fn updates_install_fake_records_install_then_relaunch_without_network() {
    let (_gate, backend) = seeded(valid_offer());

    backend
        .updates_install(true)
        .expect("valid cached offer must install on Fake");

    assert_eq!(
        recorded_actions(),
        vec![FakeAction::Install, FakeAction::Relaunch],
        "Fake must record install then relaunch"
    );
    assert!(
        backend.recorded_publishes().is_empty(),
        "Fake install must not publish MQTT / touch the broker"
    );
}

#[test]
fn updates_open_page_records_cached_url_on_fake_without_network() {
    let (_gate, backend) = seeded(valid_offer());

    backend
        .updates_open_page()
        .expect("open_page must record the cached URL on Fake");

    assert_eq!(
        recorded_actions(),
        vec![FakeAction::OpenPage(VALID_URL.to_owned())],
        "Fake open_page must record the cached URL"
    );
    assert!(
        backend.recorded_publishes().is_empty(),
        "Fake open_page must not publish MQTT / touch the broker"
    );
}
