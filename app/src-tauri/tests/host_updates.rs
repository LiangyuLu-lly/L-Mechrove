//! Given update policy + JS DTO. When URL/serde run. Then HTTPS stats host only.

use app_lib::updates::{accept_download_url, check_url, UpdateDto};

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
