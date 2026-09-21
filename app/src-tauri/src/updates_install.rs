//! Cached update offer + install/open-page plan. Never leaks into the 2-key JS DTO.

use std::path::{Path, PathBuf};
use std::sync::Mutex;

use crate::updates::{accept_download_url, DownloadUrlError};

const SHA256_HEX_LEN: usize = 64;

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
}

struct Host {
    cache: Option<CachedOffer>,
    fake_log: Vec<FakeAction>,
}

static HOST: Mutex<Host> = Mutex::new(Host {
    cache: None,
    fake_log: Vec::new(),
});

fn lock_host() -> std::sync::MutexGuard<'static, Host> {
    HOST.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

pub fn reset_host() {
    let mut host = lock_host();
    host.cache = None;
    host.fake_log.clear();
}

pub fn cache_offer(offer: CachedOffer) {
    lock_host().cache = Some(offer);
}

pub fn recorded_actions() -> Vec<FakeAction> {
    lock_host().fake_log.clone()
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

pub fn install_fake(confirm: bool) -> Result<(), InstallError> {
    let plan = plan_from_cache()?;
    if plan.confirm_required && !confirm {
        return Err(InstallError::ConfirmRequired);
    }
    let mut host = lock_host();
    host.fake_log.push(FakeAction::Install);
    host.fake_log.push(FakeAction::Relaunch);
    Ok(())
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

pub fn install_real(confirm: bool) -> Result<(), InstallError> {
    let plan = plan_from_cache()?;
    if plan.confirm_required && !confirm {
        return Err(InstallError::ConfirmRequired);
    }
    let exe = std::env::current_exe()?;
    let tmp = std::env::temp_dir().join("lmechrevo-update.bin");
    download(&plan.download_url, &tmp, plan.size)?;
    verify_sha256_and_size(&tmp, &plan.sha256, plan.size)?;
    replace_with_rollback(&exe, &tmp)?;
    let _child = std::process::Command::new(&exe).spawn()?;
    Ok(())
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

fn download(url: &str, dest: &Path, size: u64) -> Result<(), InstallError> {
    let status = std::process::Command::new("curl")
        .args(["-fsSL", "--max-filesize", &size.to_string(), "-o"])
        .arg(dest)
        .arg(url)
        .status()?;
    if status.success() {
        Ok(())
    } else {
        Err(InstallError::Verify)
    }
}

fn verify_sha256_and_size(
    path: &Path,
    expected: &str,
    size: u64,
) -> Result<(), InstallError> {
    let meta = std::fs::metadata(path)?;
    if meta.len() != size {
        return Err(InstallError::Verify);
    }
    let output = std::process::Command::new("certutil")
        .args(["-hashfile"])
        .arg(path)
        .arg("SHA256")
        .output()?;
    if !output.status.success() {
        return Err(InstallError::Verify);
    }
    let text = String::from_utf8_lossy(&output.stdout);
    let actual = parse_certutil_sha256(&text).ok_or(InstallError::Verify)?;
    if actual.eq_ignore_ascii_case(expected) {
        Ok(())
    } else {
        Err(InstallError::Verify)
    }
}

fn parse_certutil_sha256(stdout: &str) -> Option<String> {
    stdout.lines().find_map(|line| {
        let hex: String = line.chars().filter(|c| !c.is_whitespace()).collect();
        (hex.len() == SHA256_HEX_LEN && is_valid_sha256(&hex)).then_some(hex)
    })
}

fn replace_with_rollback(current: &Path, downloaded: &Path) -> Result<(), InstallError> {
    let new_path = sibling(current, ".new");
    let old_path = sibling(current, ".old");
    std::fs::copy(downloaded, &new_path)?;
    if current.exists() {
        let _ = std::fs::remove_file(&old_path);
        std::fs::rename(current, &old_path)?;
    }
    match std::fs::rename(&new_path, current) {
        Ok(()) => {
            let _ = std::fs::remove_file(&old_path);
            Ok(())
        }
        Err(err) => {
            if old_path.exists() {
                let _ = std::fs::rename(&old_path, current);
            }
            Err(err.into())
        }
    }
}

fn sibling(path: &Path, suffix: &str) -> PathBuf {
    let mut raw = path.as_os_str().to_os_string();
    raw.push(suffix);
    PathBuf::from(raw)
}
