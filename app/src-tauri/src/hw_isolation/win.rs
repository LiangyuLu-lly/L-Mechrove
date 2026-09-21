use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

use super::{
    is_official_reference, is_restorable_registry_entry, is_restorable_startup_file,
    planned_reg_add_string_args, planned_reg_delete_args, DISABLED_STARTUP_SUFFIX, RUN_KEY_PATHS,
};
use crate::hw_backend::HostError;

#[derive(Debug, Default, Serialize, Deserialize)]
struct IsolationSnapshot {
    version: u32,
    isolated: bool,
    registry: Vec<RegEntry>,
    startup_files: Vec<StartupEntry>,
}

#[derive(Debug, Serialize, Deserialize)]
struct RegEntry {
    hive: String,
    key_path: String,
    name: String,
    value: String,
}

#[derive(Debug, Serialize, Deserialize)]
struct StartupEntry {
    original: String,
    disabled: String,
}

pub fn apply(on: bool) -> Result<(), HostError> {
    if on {
        isolate()
    } else {
        restore()
    }
}

fn isolate() -> Result<(), HostError> {
    let mut snapshot = capture()?;
    save_snapshot(&snapshot)?;
    let mut first_err: Option<HostError> = None;
    for entry in &snapshot.registry {
        let key = format!(r"{}\{}", entry.hive, entry.key_path);
        if let Err(err) = run_reg(&planned_reg_delete_args(&key, &entry.name)) {
            if first_err.is_none() {
                first_err = Some(err);
            }
        }
    }
    for file in &snapshot.startup_files {
        if Path::new(&file.original).exists() && !Path::new(&file.disabled).exists() {
            if let Err(err) = std::fs::rename(&file.original, &file.disabled) {
                if first_err.is_none() {
                    first_err = Some(HostError::Io(err));
                }
            }
        }
    }
    snapshot.isolated = true;
    save_snapshot(&snapshot)?;
    match first_err {
        Some(err) => Err(err),
        None => Ok(()),
    }
}

fn restore() -> Result<(), HostError> {
    let Some(mut snapshot) = load_snapshot()? else {
        return Ok(());
    };
    let expected = snapshot.registry.len() + snapshot.startup_files.len();
    let mut restored = 0usize;
    let mut failed = 0usize;
    for entry in &snapshot.registry {
        if !is_restorable_registry_entry(&entry.hive, &entry.key_path, &entry.name) {
            continue;
        }
        let key = format!(r"{}\{}", entry.hive, entry.key_path);
        match value_exists(&key, &entry.name) {
            Ok(true) => {}
            Ok(false) => match run_reg(&planned_reg_add_string_args(
                &key,
                &entry.name,
                &entry.value,
            )) {
                Ok(()) => restored += 1,
                Err(_) => failed += 1,
            },
            Err(_) => failed += 1,
        }
    }
    let folders = startup_folder_strings();
    let folder_refs: Vec<&str> = folders.iter().map(String::as_str).collect();
    for file in &snapshot.startup_files {
        if !is_restorable_startup_file(&file.original, &file.disabled, &folder_refs) {
            continue;
        }
        if Path::new(&file.disabled).exists() && !Path::new(&file.original).exists() {
            match std::fs::rename(&file.disabled, &file.original) {
                Ok(()) => restored += 1,
                Err(_) => failed += 1,
            }
        }
    }
    if expected > 0 && restored == 0 {
        return Err(HostError::Io(std::io::Error::other(
            "isolation restore changed nothing",
        )));
    }
    if failed > 0 {
        return Err(HostError::Io(std::io::Error::other(format!(
            "isolation restore failed {failed} item(s)"
        ))));
    }
    snapshot.isolated = false;
    save_snapshot(&snapshot)?;
    Ok(())
}

fn capture() -> Result<IsolationSnapshot, HostError> {
    let mut snapshot = IsolationSnapshot {
        version: 1,
        isolated: false,
        registry: Vec::new(),
        startup_files: Vec::new(),
    };
    for path in RUN_KEY_PATHS {
        let key = format!(r"HKCU\{path}");
        for (name, value) in query_reg_values(&key)? {
            if !is_official_reference(&format!("{name} {value}")) {
                continue;
            }
            if !is_restorable_registry_entry("HKCU", path, &name) {
                continue;
            }
            snapshot.registry.push(RegEntry {
                hive: "HKCU".to_owned(),
                key_path: (*path).to_owned(),
                name,
                value,
            });
        }
    }
    snapshot.startup_files = capture_startup_files()?;
    Ok(snapshot)
}

fn capture_startup_files() -> Result<Vec<StartupEntry>, HostError> {
    let mut out = Vec::new();
    let Some(folder) = user_startup_folder() else {
        return Ok(out);
    };
    let folder_str = folder.to_string_lossy().into_owned();
    let entries = match std::fs::read_dir(&folder) {
        Ok(rd) => rd,
        Err(_) => return Ok(out),
    };
    for entry in entries.filter_map(Result::ok) {
        let path = entry.path();
        let name = path.file_name().and_then(|n| n.to_str()).unwrap_or("");
        if !is_official_reference(name) {
            continue;
        }
        let original = path.to_string_lossy().into_owned();
        let disabled = format!("{original}{DISABLED_STARTUP_SUFFIX}");
        if !is_restorable_startup_file(&original, &disabled, &[&folder_str]) {
            continue;
        }
        out.push(StartupEntry { original, disabled });
    }
    Ok(out)
}

fn snapshot_path() -> PathBuf {
    let base = std::env::var_os("LOCALAPPDATA")
        .or_else(|| std::env::var_os("ProgramData"))
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from(r"C:\ProgramData"));
    base.join("L-Mechrevo").join("OfficialConsoleState.json")
}

fn save_snapshot(snapshot: &IsolationSnapshot) -> Result<(), HostError> {
    let path = snapshot_path();
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let json = serde_json::to_string_pretty(snapshot)?;
    std::fs::write(path, json)?;
    Ok(())
}

fn load_snapshot() -> Result<Option<IsolationSnapshot>, HostError> {
    let path = snapshot_path();
    if !path.exists() {
        return Ok(None);
    }
    let json = std::fs::read_to_string(path)?;
    let snapshot: IsolationSnapshot = serde_json::from_str(&json)?;
    if snapshot.version != 1 {
        return Err(HostError::Io(std::io::Error::other(
            "unsupported isolation snapshot version",
        )));
    }
    Ok(Some(snapshot))
}

fn user_startup_folder() -> Option<PathBuf> {
    std::env::var_os("APPDATA").map(|appdata| {
        PathBuf::from(appdata).join(r"Microsoft\Windows\Start Menu\Programs\Startup")
    })
}

fn startup_folder_strings() -> Vec<String> {
    user_startup_folder()
        .map(|path| path.to_string_lossy().into_owned())
        .into_iter()
        .collect()
}

fn query_reg_values(key: &str) -> Result<Vec<(String, String)>, HostError> {
    let output = std::process::Command::new("reg")
        .args(["query", key])
        .output()
        .map_err(HostError::Io)?;
    if !output.status.success() {
        return Ok(Vec::new());
    }
    let stdout = String::from_utf8_lossy(&output.stdout);
    Ok(stdout.lines().filter_map(parse_reg_query_line).collect())
}

fn parse_reg_query_line(line: &str) -> Option<(String, String)> {
    let line = line.trim();
    if line.is_empty() || line.starts_with("HKEY_") {
        return None;
    }
    let (name, rest) = line
        .split_once("REG_SZ")
        .or_else(|| line.split_once("REG_EXPAND_SZ"))?;
    Some((name.trim().to_owned(), rest.trim().to_owned()))
}

fn value_exists(key: &str, name: &str) -> Result<bool, HostError> {
    Ok(query_reg_values(key)?.iter().any(|(n, _)| n == name))
}

fn run_reg(args: &[String]) -> Result<(), HostError> {
    let output = std::process::Command::new("reg")
        .args(args)
        .output()
        .map_err(HostError::Io)?;
    if output.status.success() {
        return Ok(());
    }
    let stderr = String::from_utf8_lossy(&output.stderr);
    let detail = stderr.trim();
    if detail.is_empty() {
        Err(HostError::Io(std::io::Error::other(format!(
            "reg exit {}",
            output.status
        ))))
    } else {
        Err(HostError::Io(std::io::Error::other(detail.to_owned())))
    }
}
