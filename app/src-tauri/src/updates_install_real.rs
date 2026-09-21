//! Real download + `.new`/`.old` replace + relaunch. Never called from unit tests.

use std::path::{Path, PathBuf};

use super::{DownloadReq, InstallError, InstallIo, Progress};

pub(super) struct RealIo;

impl InstallIo for RealIo {
    fn download(
        &self,
        req: &DownloadReq<'_>,
        progress: &mut dyn FnMut(Progress),
    ) -> Result<(), InstallError> {
        progress(Progress {
            received: 0,
            total: req.size,
        });
        let status = std::process::Command::new("curl")
            .args(["-fsSL", "--max-filesize", &req.size.to_string(), "-o"])
            .arg(req.dest)
            .arg(req.url)
            .status()?;
        if !status.success() {
            return Err(InstallError::Verify);
        }
        let received = std::fs::metadata(req.dest)?.len();
        progress(Progress {
            received,
            total: req.size,
        });
        Ok(())
    }

    fn replace(&self, downloaded: &Path) -> Result<(), InstallError> {
        let exe = std::env::current_exe()?;
        replace_with_rollback(&exe, downloaded)
    }

    fn relaunch(&self) -> Result<(), InstallError> {
        let exe = std::env::current_exe()?;
        let _child = std::process::Command::new(exe).spawn()?;
        Ok(())
    }
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
