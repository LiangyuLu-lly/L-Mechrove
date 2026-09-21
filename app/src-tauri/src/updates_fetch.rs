//! Env-driven update check. The vendor production URL is unused until credentials exist.

use std::process::Command;

use serde::Deserialize;

use super::updates_install::{cache_offer, is_valid_sha256, CachedOffer};
use super::{accept_download_url, UpdateDto};
use crate::hw_error::HostError;

pub const CHECK_URL_ENV: &str = "LMECHREVO_UPDATE_CHECK_URL";
pub const USERNAME_ENV: &str = "LMECHREVO_UPDATE_USERNAME";
pub const PASSWORD_ENV: &str = "LMECHREVO_UPDATE_PASSWORD";

#[derive(Deserialize)]
struct CheckRoot {
    ok: Option<bool>,
    data: Option<CheckData>,
}

#[derive(Deserialize)]
struct CheckData {
    latest_version: Option<String>,
    update_available: Option<bool>,
    download_url: Option<String>,
    sha256: Option<String>,
    size: Option<u64>,
}

pub fn check_from_env() -> Result<UpdateDto, HostError> {
    let empty = UpdateDto {
        update_available: false,
        latest_version: String::new(),
    };
    let Ok(url) = std::env::var(CHECK_URL_ENV) else {
        return Ok(empty);
    };
    if url.is_empty() || accept_download_url(&url).is_err() {
        return Ok(empty);
    }
    let body = http_get_bytes(&url)?;
    apply_check_json(&body)
}

fn apply_check_json(body: &[u8]) -> Result<UpdateDto, HostError> {
    let root: CheckRoot = serde_json::from_slice(body)?;
    if root.ok == Some(false) {
        return Ok(UpdateDto {
            update_available: false,
            latest_version: String::new(),
        });
    }
    let Some(data) = root.data else {
        return Ok(UpdateDto {
            update_available: false,
            latest_version: String::new(),
        });
    };
    let latest = data.latest_version.unwrap_or_default();
    let available = data.update_available.unwrap_or(false) && !latest.is_empty();
    if available {
        maybe_cache_offer(data.download_url, data.sha256, data.size);
    }
    Ok(UpdateDto {
        update_available: available,
        latest_version: latest,
    })
}

fn maybe_cache_offer(download_url: Option<String>, sha256: Option<String>, size: Option<u64>) {
    let (Some(download_url), Some(sha256), Some(size)) = (download_url, sha256, size) else {
        return;
    };
    if !is_valid_sha256(&sha256) || size == 0 || accept_download_url(&download_url).is_err() {
        return;
    }
    cache_offer(CachedOffer {
        download_url,
        sha256,
        size,
    });
}

pub fn http_get_bytes(url: &str) -> Result<Vec<u8>, std::io::Error> {
    accept_download_url(url)
        .map_err(|err| std::io::Error::new(std::io::ErrorKind::InvalidInput, err))?;
    let mut cmd = Command::new("curl");
    cmd.args([
        "-fsS",
        "--max-time",
        "6",
        "--max-redirs",
        "0",
        "--noproxy",
        "*",
    ]);
    if let Some(pair) = user_pass() {
        cmd.arg("-u").arg(pair);
    }
    cmd.arg("--").arg(url);
    let output = cmd.output()?;
    if !output.status.success() {
        return Err(std::io::Error::new(
            std::io::ErrorKind::Other,
            "update check request failed",
        ));
    }
    Ok(output.stdout)
}

fn user_pass() -> Option<String> {
    let user = std::env::var(USERNAME_ENV).ok().filter(|s| !s.is_empty())?;
    let pass = std::env::var(PASSWORD_ENV).ok().unwrap_or_default();
    Some(format!("{user}:{pass}"))
}
