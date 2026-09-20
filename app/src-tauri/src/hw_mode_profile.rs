//! Per-mode PL1 + CPU/GPU T0–T15 store. JSON under with_profile_dir.

use std::path::{Path, PathBuf};
use std::time::Duration;

use capabilities::ItemSupport;
use gcu_mqtt::fake::FakeBroker;
use serde::{Deserialize, Serialize};

use crate::hw_error::HostError;
use crate::hw_fan::{apply_fan_curve, FanCurveType};
use crate::hw_mode_detail::apply_custom_detail;

const FILE_NAME: &str = "mode-profiles.json";
const DETAIL_GAP: Duration = Duration::from_millis(120);

#[derive(Debug, Default, Clone, Serialize, Deserialize)]
struct ModeProfile {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pl1: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    cpu: Option<Vec<u8>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    gpu: Option<Vec<u8>>,
    #[serde(
        rename = "cpuTccOffset",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    cpu_tcc_offset: Option<String>,
}

#[derive(Debug, Default, Clone, Serialize, Deserialize)]
struct ModeProfileFile {
    #[serde(
        rename = "silentTurbo",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    silent_turbo: Option<ModeProfile>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    office: Option<ModeProfile>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    turbo: Option<ModeProfile>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    custom: Option<ModeProfile>,
}

pub struct ModeProfiles<'a> {
    dir: &'a Path,
}

impl<'a> ModeProfiles<'a> {
    pub const fn new(dir: &'a Path) -> Self {
        Self { dir }
    }

    fn path(&self) -> PathBuf {
        self.dir.join(FILE_NAME)
    }

    fn load(&self) -> Result<ModeProfileFile, HostError> {
        match std::fs::read_to_string(self.path()) {
            Ok(text) => Ok(serde_json::from_str(&text)?),
            Err(err) if err.kind() == std::io::ErrorKind::NotFound => Ok(ModeProfileFile::default()),
            Err(err) => Err(err.into()),
        }
    }

    fn save(&self, store: &ModeProfileFile) -> Result<(), HostError> {
        std::fs::create_dir_all(self.dir)?;
        std::fs::write(self.path(), serde_json::to_string_pretty(store)?)?;
        Ok(())
    }

    pub fn put_pl1(&self, mode: &str, pl1: &str) -> Result<(), HostError> {
        self.patch(mode, |profile| {
            profile.pl1 = Some(pl1.to_owned());
        })
    }

    pub fn put_tcc(&self, mode: &str, tcc: &str) -> Result<(), HostError> {
        self.patch(mode, |profile| {
            profile.cpu_tcc_offset = Some(tcc.to_owned());
        })
    }

    pub fn put_curve(
        &self,
        mode: &str,
        ty: FanCurveType,
        duties: [u8; 16],
    ) -> Result<(), HostError> {
        self.patch(mode, |profile| match ty {
            FanCurveType::Cpu => profile.cpu = Some(duties.to_vec()),
            FanCurveType::Gpu => profile.gpu = Some(duties.to_vec()),
        })
    }

    pub fn put_detail(&self, mode: &str, field: &str, value: &str) -> Result<(), HostError> {
        match field {
            "PL1" => self.put_pl1(mode, value),
            "CpuTccOffset" => self.put_tcc(mode, value),
            _ => Ok(()),
        }
    }

    pub async fn apply(
        &self,
        broker: &mut FakeBroker,
        item_support: &ItemSupport,
        mode: &str,
    ) -> Result<(), HostError> {
        let store = self.load()?;
        let Some(profile) = slot_ref(&store, mode) else {
            return Ok(());
        };
        let mut published_detail = false;
        if let Some(pl1) = profile.pl1.as_deref() {
            apply_custom_detail(broker, item_support, "PL1", pl1).await?;
            published_detail = true;
        }
        if let Some(cpu) = profile.cpu.as_deref() {
            apply_fan_curve(broker, "curve", FanCurveType::Cpu, duties_16(cpu)).await?;
        }
        if let Some(gpu) = profile.gpu.as_deref() {
            apply_fan_curve(broker, "curve", FanCurveType::Gpu, duties_16(gpu)).await?;
        }
        if let Some(tcc) = profile.cpu_tcc_offset.as_deref() {
            if published_detail {
                tokio::time::sleep(DETAIL_GAP).await;
            }
            apply_custom_detail(broker, item_support, "CpuTccOffset", tcc).await?;
        }
        Ok(())
    }

    fn patch(
        &self,
        mode: &str,
        update: impl FnOnce(&mut ModeProfile),
    ) -> Result<(), HostError> {
        let mut store = self.load()?;
        let Some(profile) = slot_mut(&mut store, mode) else {
            return Ok(());
        };
        update(profile);
        self.save(&store)
    }
}

fn slot_mut<'a>(store: &'a mut ModeProfileFile, mode: &str) -> Option<&'a mut ModeProfile> {
    let slot = match mode {
        "silentTurbo" => &mut store.silent_turbo,
        "office" => &mut store.office,
        "turbo" => &mut store.turbo,
        "custom" => &mut store.custom,
        _ => return None,
    };
    Some(slot.get_or_insert_with(ModeProfile::default))
}

fn slot_ref<'a>(store: &'a ModeProfileFile, mode: &str) -> Option<&'a ModeProfile> {
    match mode {
        "silentTurbo" => store.silent_turbo.as_ref(),
        "office" => store.office.as_ref(),
        "turbo" => store.turbo.as_ref(),
        "custom" => store.custom.as_ref(),
        _ => None,
    }
}

fn duties_16(duties: &[u8]) -> [u8; 16] {
    let mut out = [0_u8; 16];
    let n = duties.len().min(16);
    out[..n].copy_from_slice(&duties[..n]);
    out
}
