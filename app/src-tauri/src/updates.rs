//! Host-only update check. JS sees `{updateAvailable, latestVersion}` only.

#[path = "updates_install.rs"]
pub mod updates_install;

use serde::{Deserialize, Serialize};

use crate::hw_backend::Backend;
use crate::hw_error::HostError;

const CHECK_PREFIX: &str = "https://stats.l-mechrevo.cn/api/update_check.php?version=";
const CHANNEL_QUERY: &str = "&channel=";
const ALLOWED_DOWNLOAD_HOSTS: &[&str] = &["stats.l-mechrevo.cn", "l-mechrevo.cn"];
const HEX: &[u8; 16] = b"0123456789ABCDEF";

/// Fail-closed download URL verdict.
#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
#[non_exhaustive]
pub enum DownloadUrlError {
    #[error("download url is not a valid absolute URL")]
    Invalid,
    #[error("download url must use HTTPS")]
    Insecure,
    #[error("download host is not allowed")]
    HostDenied,
}

/// Webview DTO. Exactly two camelCase keys.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateDto {
    pub update_available: bool,
    pub latest_version: String,
}

/// `https://stats.l-mechrevo.cn/api/update_check.php?version=&channel=`.
pub fn check_url(version: &str, channel: &str) -> String {
    let mut url = String::with_capacity(
        CHECK_PREFIX.len() + version.len() * 3 + CHANNEL_QUERY.len() + channel.len() * 3,
    );
    url.push_str(CHECK_PREFIX);
    append_query_value(&mut url, version);
    url.push_str(CHANNEL_QUERY);
    append_query_value(&mut url, channel);
    url
}

/// HTTPS, or HTTP only on loopback. Host must be stats/l-mechrevo.cn or loopback.
pub fn accept_download_url(url: &str) -> Result<(), DownloadUrlError> {
    let Some((scheme, host)) = scheme_and_host(url) else {
        return Err(DownloadUrlError::Invalid);
    };
    let https = scheme.eq_ignore_ascii_case("https");
    let http = scheme.eq_ignore_ascii_case("http");
    let loopback = is_loopback(host);
    if !(https || (http && loopback)) {
        return Err(DownloadUrlError::Insecure);
    }
    if !is_allowed_host(host) {
        return Err(DownloadUrlError::HostDenied);
    }
    Ok(())
}

fn append_query_value(out: &mut String, value: &str) {
    for byte in value.bytes() {
        match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'.' | b'_' | b'~' => {
                out.push(char::from(byte));
            }
            _ => {
                out.push('%');
                out.push(char::from(HEX[(byte >> 4) as usize]));
                out.push(char::from(HEX[(byte & 0x0F) as usize]));
            }
        }
    }
}

fn scheme_and_host(url: &str) -> Option<(&str, &str)> {
    let (scheme, rest) = url.split_once("://")?;
    if scheme.is_empty() {
        return None;
    }
    let authority = match rest.split(['/', '?', '#']).next() {
        Some(part) if !part.is_empty() => part,
        Some(_) | None => return None,
    };
    let hostport = authority
        .rsplit_once('@')
        .map_or(authority, |(_, host)| host);
    let host = if let Some(inner) = hostport.strip_prefix('[') {
        inner.split_once(']')?.0
    } else {
        match hostport.split(':').next() {
            Some(part) if !part.is_empty() => part,
            Some(_) | None => return None,
        }
    };
    if host.is_empty() {
        return None;
    }
    Some((scheme, host))
}

fn is_loopback(host: &str) -> bool {
    host.eq_ignore_ascii_case("localhost")
        || host == "::1"
        || host == "127.0.0.1"
        || host.starts_with("127.")
}

fn is_allowed_host(host: &str) -> bool {
    is_loopback(host)
        || ALLOWED_DOWNLOAD_HOSTS
            .iter()
            .any(|allowed| host.eq_ignore_ascii_case(allowed))
}

impl Backend {
    pub fn updates_check(&self) -> Result<UpdateDto, HostError> {
        match self {
            Self::Fake { .. } => Ok(UpdateDto {
                update_available: false,
                latest_version: String::new(),
            }),
            Self::Real { .. } => Err(HostError::RealUnavailable),
        }
    }

    pub fn updates_install(&self, confirm: bool) -> Result<(), updates_install::InstallError> {
        match self {
            Self::Fake { .. } => updates_install::install_fake(confirm),
            Self::Real { .. } => updates_install::install_real(confirm),
        }
    }

    pub fn updates_open_page(&self) -> Result<(), updates_install::InstallError> {
        match self {
            Self::Fake { .. } => updates_install::open_page_fake(),
            Self::Real { .. } => updates_install::open_page_real(),
        }
    }
}
