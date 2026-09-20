//! Diagnostic pack: snapshot JSON, ItemSupport dump, logs. Never copies MQTT secrets.

use std::fs;
use std::path::{Path, PathBuf};

use crate::hw_backend::Backend;
use crate::hw_error::HostError;

const SECRET_NEEDLE: &[u8] = b"UWPClient_Pwd";
const SNAPSHOT_NAME: &str = "snapshot.json";
const ITEM_SUPPORT_NAME: &str = "item_support.json";
const LOGS_NAME: &str = "logs.txt";
const MANIFEST_NAME: &str = "manifest.txt";
const ZIP_NAME: &str = "diagnostics.zip";

struct ZipEntry {
    name: String,
    data: Vec<u8>,
}

impl Backend {
    pub fn export_diagnostics(&self, dest_dir: &Path) -> Result<PathBuf, HostError> {
        export_pack(self, dest_dir)
    }
}

fn export_pack(backend: &Backend, dest_dir: &Path) -> Result<PathBuf, HostError> {
    fs::create_dir_all(dest_dir)?;
    let entries = collect(backend)?;
    for entry in &entries {
        reject_secrets(&entry.name, &entry.data)?;
        fs::write(dest_dir.join(&entry.name), &entry.data)?;
    }
    let zip_path = dest_dir.join(ZIP_NAME);
    let zip_bytes = store_zip(&entries)?;
    reject_secrets(ZIP_NAME, &zip_bytes)?;
    fs::write(&zip_path, zip_bytes)?;
    Ok(zip_path)
}

fn collect(backend: &Backend) -> Result<Vec<ZipEntry>, HostError> {
    let snapshot = serde_json::to_vec_pretty(&backend.snapshot())?;
    let item = item_support_bytes(backend)?;
    let logs = logs_bytes(backend);
    let manifest = format!("{SNAPSHOT_NAME}\n{ITEM_SUPPORT_NAME}\n{LOGS_NAME}\n{MANIFEST_NAME}\n")
        .into_bytes();
    Ok(vec![
        ZipEntry {
            name: SNAPSHOT_NAME.to_owned(),
            data: snapshot,
        },
        ZipEntry {
            name: ITEM_SUPPORT_NAME.to_owned(),
            data: item,
        },
        ZipEntry {
            name: LOGS_NAME.to_owned(),
            data: logs,
        },
        ZipEntry {
            name: MANIFEST_NAME.to_owned(),
            data: manifest,
        },
    ])
}

fn item_support_bytes(backend: &Backend) -> Result<Vec<u8>, HostError> {
    match backend {
        Backend::Fake { state } => Ok(serde_json::to_vec_pretty(&state.item_support.dump_json())?),
        Backend::Real => Ok(b"{}".to_vec()),
    }
}

fn logs_bytes(backend: &Backend) -> Vec<u8> {
    let snap = backend.snapshot();
    format!(
        "mqtt={:?}\nwrite_allowed={}\ncharge_percent={}\n",
        snap.mqtt, snap.write_allowed, snap.charge_percent
    )
    .into_bytes()
}

fn reject_secrets(name: &str, data: &[u8]) -> Result<(), HostError> {
    if data
        .windows(SECRET_NEEDLE.len())
        .any(|window| window == SECRET_NEEDLE)
    {
        return Err(HostError::Io(std::io::Error::new(
            std::io::ErrorKind::InvalidData,
            format!("refusing to pack secret in {name}"),
        )));
    }
    Ok(())
}

fn crc32(data: &[u8]) -> u32 {
    let mut crc = 0xFFFF_FFFFu32;
    for &byte in data {
        crc ^= u32::from(byte);
        for _ in 0..8 {
            crc = if crc & 1 == 0 {
                crc >> 1
            } else {
                (crc >> 1) ^ 0xEDB8_8320
            };
        }
    }
    !crc
}

fn store_zip(entries: &[ZipEntry]) -> Result<Vec<u8>, HostError> {
    let mut local = Vec::new();
    let mut central = Vec::new();
    for entry in entries {
        let name = entry.name.as_bytes();
        let crc = crc32(&entry.data);
        let size = u32::try_from(entry.data.len()).map_err(|_| {
            HostError::Io(std::io::Error::new(
                std::io::ErrorKind::InvalidData,
                "diagnostic entry too large",
            ))
        })?;
        let name_len = u16::try_from(name.len()).map_err(|_| {
            HostError::Io(std::io::Error::new(
                std::io::ErrorKind::InvalidData,
                "diagnostic name too long",
            ))
        })?;
        let local_offset = u32::try_from(local.len()).map_err(|_| {
            HostError::Io(std::io::Error::new(
                std::io::ErrorKind::InvalidData,
                "diagnostic zip too large",
            ))
        })?;
        write_u32(&mut local, 0x0403_4b50);
        write_u16(&mut local, 20);
        write_u16(&mut local, 0);
        write_u16(&mut local, 0);
        write_u16(&mut local, 0);
        write_u16(&mut local, 0);
        write_u32(&mut local, crc);
        write_u32(&mut local, size);
        write_u32(&mut local, size);
        write_u16(&mut local, name_len);
        write_u16(&mut local, 0);
        local.extend_from_slice(name);
        local.extend_from_slice(&entry.data);

        write_u32(&mut central, 0x0201_4b50);
        write_u16(&mut central, 20);
        write_u16(&mut central, 20);
        write_u16(&mut central, 0);
        write_u16(&mut central, 0);
        write_u16(&mut central, 0);
        write_u16(&mut central, 0);
        write_u32(&mut central, crc);
        write_u32(&mut central, size);
        write_u32(&mut central, size);
        write_u16(&mut central, name_len);
        write_u16(&mut central, 0);
        write_u16(&mut central, 0);
        write_u16(&mut central, 0);
        write_u16(&mut central, 0);
        write_u32(&mut central, 0);
        write_u32(&mut central, local_offset);
        central.extend_from_slice(name);
    }
    let cd_offset = u32::try_from(local.len()).map_err(|_| {
        HostError::Io(std::io::Error::new(
            std::io::ErrorKind::InvalidData,
            "diagnostic zip too large",
        ))
    })?;
    let cd_size = u32::try_from(central.len()).map_err(|_| {
        HostError::Io(std::io::Error::new(
            std::io::ErrorKind::InvalidData,
            "diagnostic zip too large",
        ))
    })?;
    let count = u16::try_from(entries.len()).map_err(|_| {
        HostError::Io(std::io::Error::new(
            std::io::ErrorKind::InvalidData,
            "too many diagnostic entries",
        ))
    })?;
    let mut out = local;
    out.extend_from_slice(&central);
    write_u32(&mut out, 0x0605_4b50);
    write_u16(&mut out, 0);
    write_u16(&mut out, 0);
    write_u16(&mut out, count);
    write_u16(&mut out, count);
    write_u32(&mut out, cd_size);
    write_u32(&mut out, cd_offset);
    write_u16(&mut out, 0);
    Ok(out)
}

fn write_u16(buf: &mut Vec<u8>, value: u16) {
    buf.extend_from_slice(&value.to_le_bytes());
}

fn write_u32(buf: &mut Vec<u8>, value: u32) {
    buf.extend_from_slice(&value.to_le_bytes());
}
