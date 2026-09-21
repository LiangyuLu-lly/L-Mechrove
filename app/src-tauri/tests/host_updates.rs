//! Given update policy + JS DTO. When URL/serde run. Then HTTPS stats host only.

use std::io::{Read, Write};
use std::net::TcpListener;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::thread;
use std::time::{SystemTime, UNIX_EPOCH};

use app_lib::updates::updates_install::{
    cache_offer, install_with, recorded_actions, recorded_progress, replace_with_rollback,
    reset_host, sha256_hex, CachedOffer, DownloadReq, FakeAction, InstallError, InstallIo,
    Progress,
};
use app_lib::updates::{
    accept_download_url, check_url, http_get_bytes, DownloadUrlError, UpdateDto,
};
use app_lib::Backend;

static GATE: Mutex<()> = Mutex::new(());

const PAYLOAD: &[u8] = b"ok";
const VALID_SHA256: &str = "2689367b205c16ce32ed4200942b8b8b1e262dfc70d9bc9fbc77c49699a4f1df";
const VALID_URL: &str = "https://stats.l-mechrevo.cn/pkg.exe";
const VALID_SIZE: u64 = 2;

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

const CHECK_URL_ENV: &str = "LMECHREVO_UPDATE_CHECK_URL";

struct EnvGuard {
    key: &'static str,
    prev: Option<String>,
}

impl EnvGuard {
    fn set(key: &'static str, value: &str) -> Self {
        let prev = std::env::var(key).ok();
        std::env::set_var(key, value);
        Self { key, prev }
    }
}

impl Drop for EnvGuard {
    fn drop(&mut self) {
        match &self.prev {
            Some(value) => std::env::set_var(self.key, value),
            None => std::env::remove_var(self.key),
        }
    }
}

struct LocalUpdate {
    check_url: String,
    download_url: String,
}

fn start_local_update(payload: Vec<u8>, latest: &str, sha256: &str) -> LocalUpdate {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind loopback");
    let port = listener.local_addr().expect("local addr").port();
    let download_url = format!("http://127.0.0.1:{port}/pkg.exe");
    let check_url = format!("http://127.0.0.1:{port}/check");
    let json = format!(
        "{{\"ok\":true,\"data\":{{\"latest_version\":\"{latest}\",\"update_available\":true,\"download_url\":\"{download_url}\",\"sha256\":\"{sha256}\",\"size\":{}}}}}",
        payload.len()
    );
    thread::spawn(move || {
        for stream in listener.incoming() {
            let Ok(mut stream) = stream else {
                continue;
            };
            let mut buf = [0u8; 2048];
            let n = stream.read(&mut buf).unwrap_or(0);
            let req = String::from_utf8_lossy(&buf[..n]);
            let (body, ctype) = if req.starts_with("GET /check") {
                (json.as_bytes().to_vec(), "application/json")
            } else if req.starts_with("GET /pkg.exe") {
                (payload.clone(), "application/octet-stream")
            } else {
                (Vec::new(), "text/plain")
            };
            let _ = write!(
                stream,
                "HTTP/1.1 200 OK\r\nContent-Type: {ctype}\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
                body.len()
            );
            let _ = stream.write_all(&body);
        }
    });
    LocalUpdate {
        check_url,
        download_url,
    }
}

#[test]
fn rejects_http_non_loopback() {
    let err = accept_download_url("http://evil.example/pkg.exe");
    assert!(
        err.is_err(),
        "http non-loopback download_url must be rejected: {err:?}"
    );
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
fn accepts_http_loopback() {
    accept_download_url("http://127.0.0.1:9/pkg.exe")
        .expect("http loopback download_url must be accepted");
    accept_download_url("http://localhost/pkg.exe").expect("http localhost must be accepted");
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

struct ScriptedIo {
    payload: Vec<u8>,
}

impl InstallIo for ScriptedIo {
    fn download(
        &self,
        req: &DownloadReq<'_>,
        progress: &mut dyn FnMut(Progress),
    ) -> Result<(), InstallError> {
        progress(Progress {
            received: 0,
            total: req.size,
        });
        std::fs::write(req.dest, &self.payload)?;
        progress(Progress {
            received: self.payload.len() as u64,
            total: req.size,
        });
        Ok(())
    }

    fn replace(&self, _: &Path) -> Result<(), InstallError> {
        Ok(())
    }

    fn relaunch(&self) -> Result<(), InstallError> {
        Ok(())
    }
}

struct RefuseReplaceIo {
    payload: Vec<u8>,
}

impl InstallIo for RefuseReplaceIo {
    fn download(
        &self,
        req: &DownloadReq<'_>,
        progress: &mut dyn FnMut(Progress),
    ) -> Result<(), InstallError> {
        progress(Progress {
            received: self.payload.len() as u64,
            total: req.size,
        });
        std::fs::write(req.dest, &self.payload)?;
        Ok(())
    }

    fn replace(&self, _: &Path) -> Result<(), InstallError> {
        panic!("replace must not run when verification refuses");
    }

    fn relaunch(&self) -> Result<(), InstallError> {
        panic!("relaunch must not run when verification refuses");
    }
}

#[test]
fn updates_install_with_injected_io_records_install_then_relaunch() {
    let (_gate, backend) = seeded(valid_offer());
    let io = ScriptedIo {
        payload: PAYLOAD.to_vec(),
    };

    install_with(true, &io).expect("valid cached offer must install through injected io");

    assert_eq!(
        recorded_actions(),
        vec![FakeAction::Install, FakeAction::Relaunch],
        "injected install must record install then relaunch"
    );
    assert_eq!(
        recorded_progress(),
        vec![
            Progress {
                received: 0,
                total: VALID_SIZE,
            },
            Progress {
                received: VALID_SIZE,
                total: VALID_SIZE,
            },
        ],
        "injected download must emit {{received,total}} progress"
    );
    assert!(
        backend.recorded_publishes().is_empty(),
        "injected install must not publish MQTT / touch the broker"
    );
}

#[test]
fn updates_install_refuses_when_downloaded_size_mismatches() {
    let (_gate, _) = seeded(valid_offer());
    let io = RefuseReplaceIo {
        payload: b"x".to_vec(),
    };

    let err = install_with(true, &io).expect_err("size mismatch must refuse");

    assert!(
        matches!(err, InstallError::SizeMismatch),
        "expected SizeMismatch, got {err:?}"
    );
    assert!(
        recorded_actions().is_empty(),
        "refused install must not record: {:?}",
        recorded_actions()
    );
}

#[test]
fn updates_install_refuses_when_downloaded_sha256_mismatches() {
    let (_gate, _) = seeded(valid_offer());
    let io = RefuseReplaceIo {
        payload: b"no".to_vec(),
    };

    let err = install_with(true, &io).expect_err("sha256 mismatch must refuse");

    assert!(
        matches!(err, InstallError::Verify),
        "expected Verify, got {err:?}"
    );
    assert!(
        recorded_actions().is_empty(),
        "refused install must not record: {:?}",
        recorded_actions()
    );
}

#[test]
fn install_refuses_without_confirm() {
    let (_gate, backend) = seeded(valid_offer());

    let err = backend
        .updates_install(false)
        .expect_err("silent replace is forbidden");

    assert!(
        matches!(err, InstallError::ConfirmRequired),
        "expected ConfirmRequired, got {err:?}"
    );
    assert!(
        recorded_actions().is_empty(),
        "unconfirmed install must not record: {:?}",
        recorded_actions()
    );
}

#[test]
fn updates_install_refuses_without_confirm() {
    let (_gate, backend) = seeded(valid_offer());

    let err = backend
        .updates_install(false)
        .expect_err("silent replace is forbidden");

    assert!(
        matches!(err, InstallError::ConfirmRequired),
        "expected ConfirmRequired, got {err:?}"
    );
    assert!(
        recorded_actions().is_empty(),
        "unconfirmed install must not record: {:?}",
        recorded_actions()
    );
}

#[test]
fn progress_event_json_is_received_and_total() {
    let value = serde_json::to_value(Progress {
        received: 3,
        total: 9,
    })
    .expect("serialize Progress");
    let obj = value.as_object().expect("Progress JSON object");
    assert_eq!(
        obj.len(),
        2,
        "progress event must have exactly two keys: {obj:?}"
    );
    assert_eq!(obj.get("received"), Some(&serde_json::json!(3)));
    assert_eq!(obj.get("total"), Some(&serde_json::json!(9)));
}

#[test]
fn updates_check_loopback_offer_populates_cache_consumed_by_install() {
    let gate = GATE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    reset_host();
    let local = start_local_update(PAYLOAD.to_vec(), "9.9.9", VALID_SHA256);
    let _env = EnvGuard::set(CHECK_URL_ENV, &local.check_url);
    let backend = Backend::fake_from_json("{}").expect("parse");

    let dto = backend
        .updates_check()
        .expect("loopback check URL must return an offer");

    assert!(dto.update_available, "check must report an update: {dto:?}");
    assert_eq!(dto.latest_version, "9.9.9");

    backend
        .updates_install(true)
        .expect("install must consume the offer cached by check");
    assert_eq!(
        recorded_actions(),
        vec![FakeAction::Install, FakeAction::Relaunch],
        "cached offer from check must drive install then relaunch"
    );
    drop(gate);
}

fn temp_target(tag: &str) -> PathBuf {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    std::env::temp_dir().join(format!(
        "lmechrevo-e2e-{tag}-{}-{nanos}.exe",
        std::process::id()
    ))
}

struct E2eIo {
    target: PathBuf,
}

impl InstallIo for E2eIo {
    fn download(
        &self,
        req: &DownloadReq<'_>,
        progress: &mut dyn FnMut(Progress),
    ) -> Result<(), InstallError> {
        progress(Progress {
            received: 0,
            total: req.size,
        });
        let bytes = http_get_bytes(req.url).map_err(InstallError::from)?;
        std::fs::write(req.dest, &bytes)?;
        progress(Progress {
            received: bytes.len() as u64,
            total: req.size,
        });
        Ok(())
    }

    fn replace(&self, downloaded: &Path) -> Result<(), InstallError> {
        replace_with_rollback(&self.target, downloaded)
    }

    fn relaunch(&self) -> Result<(), InstallError> {
        // Boundary: RealIo::relaunch would spawn current_exe. Tests stop here.
        Ok(())
    }
}

#[test]
fn updates_check_install_e2e_replaces_temp_target_stops_before_relaunch() {
    let gate = GATE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    reset_host();
    let dummy = b"MZ-dummy-update-payload".to_vec();
    let sha = sha256_hex(&dummy);
    let local = start_local_update(dummy.clone(), "9.9.9", &sha);
    let _env = EnvGuard::set(CHECK_URL_ENV, &local.check_url);
    let backend = Backend::fake_from_json("{}").expect("parse");
    let target = temp_target("replace");
    std::fs::write(&target, b"old-bytes").expect("seed temp target");
    let io = E2eIo {
        target: target.clone(),
    };

    let dto = backend
        .updates_check()
        .expect("loopback check must parse the JSON offer");
    assert!(
        dto.update_available,
        "check must report the loopback offer: {dto:?}"
    );
    assert!(
        !local.download_url.is_empty(),
        "server must expose a download url"
    );

    install_with(true, &io).expect("e2e install must verify hash/size and replace");
    assert_eq!(
        std::fs::read(&target).expect("read replaced target"),
        dummy,
        "replace step must write the downloaded dummy onto the temp target"
    );
    let _ = std::fs::remove_file(&target);
    let _ = std::fs::remove_file(format!("{}{}", target.display(), ".old"));
    let _ = std::fs::remove_file(format!("{}{}", target.display(), ".new"));
    drop(gate);
}
