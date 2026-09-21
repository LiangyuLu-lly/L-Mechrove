//! Cached update offer + install/open-page plan. Never leaks into the 2-key JS DTO.
//! Authenticode is NOT implemented — integrity is sha256 + size only.
//! Real replace+relaunch cannot be proven in unit tests; CI uses [`install_with`].

use std::io::Read;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::updates::{accept_download_url, DownloadUrlError};

#[path = "updates_install_real.rs"]
mod real;

pub use real::replace_with_rollback;

const SHA256_HEX_LEN: usize = 64;
const HEX: &[u8; 16] = b"0123456789abcdef";
const FAKE_PAYLOAD: &[u8] = b"ok";

/// Last check payload kept on the host. Not serialized to JS.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CachedOffer {
    pub download_url: String,
    pub sha256: String,
    pub size: u64,
}

/// Install payload. `confirm_required` is always set — silent replace is forbidden.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InstallPlan {
    pub download_url: String,
    pub sha256: String,
    pub size: u64,
    pub confirm_required: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FakeAction {
    Install,
    Relaunch,
    OpenPage(String),
}

/// UI progress event. Exactly `{received, total}`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct Progress {
    pub received: u64,
    pub total: u64,
}

/// Download request passed to an [`InstallIo`] double.
pub struct DownloadReq<'a> {
    pub url: &'a str,
    pub dest: &'a Path,
    pub size: u64,
}

/// Injected download + replace + relaunch. Real I/O stays behind this seam.
pub trait InstallIo {
    fn download(
        &self,
        req: &DownloadReq<'_>,
        progress: &mut dyn FnMut(Progress),
    ) -> Result<(), InstallError>;
    fn replace(&self, downloaded: &Path) -> Result<(), InstallError>;
    fn relaunch(&self) -> Result<(), InstallError>;
}

#[derive(Debug, thiserror::Error)]
#[non_exhaustive]
pub enum InstallError {
    #[error("no cached update offer")]
    NoCache,
    #[error("cached offer has no valid sha256")]
    InvalidSha256,
    #[error("download url rejected: {0}")]
    Url(#[from] DownloadUrlError),
    #[error("install requires explicit confirm")]
    ConfirmRequired,
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error("update package verification failed")]
    Verify,
    #[error("downloaded size does not match cached offer")]
    SizeMismatch,
}

struct Host {
    cache: Option<CachedOffer>,
    fake_log: Vec<FakeAction>,
    progress: Vec<Progress>,
}

static HOST: Mutex<Host> = Mutex::new(Host {
    cache: None,
    fake_log: Vec::new(),
    progress: Vec::new(),
});

fn lock_host() -> std::sync::MutexGuard<'static, Host> {
    HOST.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

pub fn reset_host() {
    let mut host = lock_host();
    host.cache = None;
    host.fake_log.clear();
    host.progress.clear();
}

pub fn cache_offer(offer: CachedOffer) {
    lock_host().cache = Some(offer);
}

pub fn recorded_actions() -> Vec<FakeAction> {
    lock_host().fake_log.clone()
}

pub fn recorded_progress() -> Vec<Progress> {
    lock_host().progress.clone()
}

pub const fn is_valid_sha256(value: &str) -> bool {
    let bytes = value.as_bytes();
    if bytes.len() != SHA256_HEX_LEN {
        return false;
    }
    let mut i = 0;
    while i < SHA256_HEX_LEN {
        if !bytes[i].is_ascii_hexdigit() {
            return false;
        }
        i += 1;
    }
    true
}

pub fn plan_install(offer: &CachedOffer) -> Result<InstallPlan, InstallError> {
    if !is_valid_sha256(&offer.sha256) {
        return Err(InstallError::InvalidSha256);
    }
    accept_download_url(&offer.download_url)?;
    Ok(InstallPlan {
        download_url: offer.download_url.clone(),
        sha256: offer.sha256.clone(),
        size: offer.size,
        confirm_required: true,
    })
}

pub fn open_page(offer: &CachedOffer) -> &str {
    &offer.download_url
}

fn plan_from_cache() -> Result<InstallPlan, InstallError> {
    let offer = lock_host().cache.clone().ok_or(InstallError::NoCache)?;
    plan_install(&offer)
}

struct DeleteOnDrop<'a>(&'a Path);

impl Drop for DeleteOnDrop<'_> {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(self.0);
    }
}

fn temp_download_path() -> PathBuf {
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    std::env::temp_dir().join(format!("lmechrevo-update-{}-{nanos}", std::process::id()))
}

/// Confirm, download, sha256+size verify, replace, relaunch. Never silent.
pub fn install_with(confirm: bool, io: &dyn InstallIo) -> Result<(), InstallError> {
    let plan = plan_from_cache()?;
    if plan.confirm_required && !confirm {
        return Err(InstallError::ConfirmRequired);
    }
    let dest = temp_download_path();
    let _cleanup = DeleteOnDrop(&dest);
    io.download(
        &DownloadReq {
            url: &plan.download_url,
            dest: &dest,
            size: plan.size,
        },
        &mut |progress| lock_host().progress.push(progress),
    )?;
    verify_sha256_and_size(&dest, &plan.sha256, plan.size)?;
    io.replace(&dest)?;
    io.relaunch()?;
    let mut host = lock_host();
    host.fake_log.push(FakeAction::Install);
    host.fake_log.push(FakeAction::Relaunch);
    Ok(())
}

struct FakeIo;

impl InstallIo for FakeIo {
    fn download(
        &self,
        req: &DownloadReq<'_>,
        progress: &mut dyn FnMut(Progress),
    ) -> Result<(), InstallError> {
        progress(Progress {
            received: 0,
            total: req.size,
        });
        std::fs::write(req.dest, FAKE_PAYLOAD)?;
        progress(Progress {
            received: FAKE_PAYLOAD.len() as u64,
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

pub fn install_fake(confirm: bool) -> Result<(), InstallError> {
    install_with(confirm, &FakeIo)
}

pub fn open_page_fake() -> Result<(), InstallError> {
    let url = {
        let host = lock_host();
        let offer = host.cache.as_ref().ok_or(InstallError::NoCache)?;
        open_page(offer).to_owned()
    };
    lock_host().fake_log.push(FakeAction::OpenPage(url));
    Ok(())
}

/// Downloads to a temp file, verifies sha256+size, writes `.new`, `.old` rollback, relaunches.
///
/// Unit tests must never call this: no network, no replacing the test binary.
pub fn install_real(confirm: bool) -> Result<(), InstallError> {
    #[cfg(test)]
    {
        let _ = confirm;
        panic!("Real install path must not run in unit tests");
    }
    #[cfg(not(test))]
    install_with(confirm, &real::RealIo)
}

pub fn open_page_real() -> Result<(), InstallError> {
    let url = {
        let host = lock_host();
        let offer = host.cache.as_ref().ok_or(InstallError::NoCache)?;
        open_page(offer).to_owned()
    };
    let _child = std::process::Command::new("cmd")
        .args(["/C", "start", "", &url])
        .spawn()?;
    Ok(())
}

pub fn sha256_hex(bytes: &[u8]) -> String {
    hex_encode(&Sha256::digest(bytes))
}

fn hex_encode(digest: &[u8]) -> String {
    let mut actual = String::with_capacity(digest.len() * 2);
    for byte in digest {
        actual.push(char::from(HEX[(byte >> 4) as usize]));
        actual.push(char::from(HEX[(byte & 0x0F) as usize]));
    }
    actual
}

fn verify_sha256_and_size(path: &Path, expected: &str, size: u64) -> Result<(), InstallError> {
    let meta = std::fs::metadata(path)?;
    if meta.len() != size {
        return Err(InstallError::SizeMismatch);
    }
    let mut file = std::fs::File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buf = [0u8; 8192];
    loop {
        let n = file.read(&mut buf)?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
    }
    if hex_encode(&hasher.finalize()).eq_ignore_ascii_case(expected) {
        Ok(())
    } else {
        Err(InstallError::Verify)
    }
}
