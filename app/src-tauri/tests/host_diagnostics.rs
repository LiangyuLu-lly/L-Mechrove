//! Given Fake GCU. When diagnostics export / write-deny. Then no MQTT secrets and no OPERATING_* writes.

use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

use app_lib::Backend;

static DIAG_SEQ: AtomicU64 = AtomicU64::new(0);

fn unique_diag_dir() -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "lmechrevo-diag-{}-{}",
        std::process::id(),
        DIAG_SEQ.fetch_add(1, Ordering::Relaxed)
    ));
    fs::create_dir_all(&dir).expect("temp diagnostics dir");
    dir
}

fn write_actions<'a>(publishes: &'a [(String, serde_json::Value)]) -> Vec<(&'a str, &'a str)> {
    publishes
        .iter()
        .filter_map(|(topic, payload)| {
            let action = payload.get("Action")?.as_str()?;
            if action == "GETSTATUS" {
                return None;
            }
            Some((topic.as_str(), action))
        })
        .collect()
}

#[tokio::test]
async fn diagnostic_zip_excludes_mqtt_password() {
    let backend = Backend::fake_from_json("{}").expect("empty ItemSupport");
    let dir = unique_diag_dir();
    let zip_path = backend
        .export_diagnostics(&dir)
        .expect("diagnostics export");
    let bytes = fs::read(&zip_path).unwrap_or_else(|err| panic!("{}: {err}", zip_path.display()));
    let haystack = String::from_utf8_lossy(&bytes);
    assert!(
        !haystack.contains("UWPClient_Pwd"),
        "zip bytes must not contain UWPClient_Pwd: {}",
        zip_path.display()
    );
    let names = haystack.to_ascii_lowercase();
    assert!(
        !names.contains("uwpclient_pwd"),
        "zip file list must not contain UWPClient_Pwd: {}",
        zip_path.display()
    );
}

#[tokio::test]
async fn unsupported_set_performance_mode_does_not_publish() {
    let mut backend = Backend::fake_from_json("{}")
        .expect("empty ItemSupport")
        .with_write_allowed(false);
    backend.start().await.expect("fake handshake");
    let err = backend
        .set_performance_mode("office")
        .await
        .expect_err("write_allowed=false must deny set_performance_mode");
    assert!(
        err.to_string().contains("writes disabled"),
        "WritesDisabled, got {err}"
    );
    let publishes = backend.recorded_publishes();
    let actions = write_actions(&publishes);
    assert!(
        actions.iter().all(|(topic, action)| {
            *topic != "Fan/Control" || !action.starts_with("OPERATING_")
        }),
        "no OPERATING_* Fan/Control write when denied, got {actions:?}"
    );
}
