//! Mode-switch payloads. `ProfileIndex` / `IsNormalRun` / `SILENT` are JSON numbers.
//! `IsCustomRun` is a JSON bool.

use serde::Serialize;

/// Invalid custom-mode profile slot.
#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
#[non_exhaustive]
pub enum PayloadError {
    /// Official custom slots are `0..=3`.
    #[error("ProfileIndex {index} is outside 0..=3")]
    ProfileIndexOutOfRange { index: u8 },
}

/// Custom-mode slot. JSON number `0..=3`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(transparent)]
pub struct ProfileIndex(u8);

impl ProfileIndex {
    /// Parse a custom-mode slot.
    ///
    /// # Errors
    /// Returns [`PayloadError::ProfileIndexOutOfRange`] when `index` is not `0..=3`.
    pub const fn new(index: u8) -> Result<Self, PayloadError> {
        match index {
            0..=3 => Ok(Self(index)),
            _ => Err(PayloadError::ProfileIndexOutOfRange { index }),
        }
    }

    /// Slot as `u8`.
    pub const fn get(self) -> u8 {
        self.0
    }
}

/// Fan/Control operating-mode command (`OPERATING_*_MODE`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub struct OperatingOffice {
    /// Official action token.
    #[serde(rename = "Action")]
    pub action: &'static str,
    /// JSON number, never a string.
    #[serde(rename = "ProfileIndex")]
    pub profile_index: i32,
}

/// LCHWOC/Control run flag for office/gaming/turbo.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub struct LchwocOffice {
    /// Office = 0, Gaming = 1, Turbo = 2. JSON number.
    #[serde(rename = "IsNormalRun")]
    pub is_normal_run: i32,
}

/// LCHWOC/Control custom-run flag.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub struct LchwocCustom {
    /// Official custom-run marker. JSON bool, never 1/0.
    #[serde(rename = "IsCustomRun")]
    pub is_custom_run: bool,
}

/// Fan/Control silent-turbo sub-mode (`SET_CPU_CORE_OFFSET_SILENT`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub struct SilentTurbo {
    /// Official action token.
    #[serde(rename = "Action")]
    pub action: &'static str,
    /// JSON number 1.
    #[serde(rename = "SILENT")]
    pub silent: i32,
}

/// Fan/Control fan-boost command (`FAN_BOOST_ON` / `FAN_BOOST_OFF`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub struct FanBoost {
    /// Official action token.
    #[serde(rename = "Action")]
    pub action: &'static str,
}

const fn operating(action: &'static str, profile_index: i32) -> OperatingOffice {
    OperatingOffice {
        action,
        profile_index,
    }
}

const fn lchwoc_normal(is_normal_run: i32) -> LchwocOffice {
    LchwocOffice { is_normal_run }
}

/// Fan payload for office mode: `ProfileIndex` is the number 0.
pub const fn office_fan() -> OperatingOffice {
    operating("OPERATING_OFFICE_MODE", 0)
}

/// LCHWOC payload for office mode: `IsNormalRun` is the number 0.
pub const fn office_lchwoc() -> LchwocOffice {
    lchwoc_normal(0)
}

/// Fan payload for gaming mode: `ProfileIndex` is the number 0.
pub const fn gaming_fan() -> OperatingOffice {
    operating("OPERATING_GAMING_MODE", 0)
}

/// LCHWOC payload for gaming mode: `IsNormalRun` is the number 1.
pub const fn gaming_lchwoc() -> LchwocOffice {
    lchwoc_normal(1)
}

/// Fan payload for turbo mode: `ProfileIndex` is the number 0.
pub const fn turbo_fan() -> OperatingOffice {
    operating("OPERATING_TURBO_MODE", 0)
}

/// LCHWOC payload for turbo mode: `IsNormalRun` is the number 2.
pub const fn turbo_lchwoc() -> LchwocOffice {
    lchwoc_normal(2)
}

/// Fan payload for custom mode: `ProfileIndex` is JSON number `n` (`0..=3`).
pub fn custom_fan(profile: ProfileIndex) -> OperatingOffice {
    operating("OPERATING_CUSTOM_MODE", i32::from(profile.get()))
}

/// LCHWOC payload for custom mode: `IsCustomRun` is JSON bool `true`.
pub const fn custom_lchwoc() -> LchwocCustom {
    LchwocCustom {
        is_custom_run: true,
    }
}

/// Fan payload for silent turbo: `{Action: SET_CPU_CORE_OFFSET_SILENT, SILENT: 1}`.
pub const fn silent_turbo_fan() -> SilentTurbo {
    SilentTurbo {
        action: "SET_CPU_CORE_OFFSET_SILENT",
        silent: 1,
    }
}

/// Fan payload for fan boost on.
pub const fn fan_boost_on() -> FanBoost {
    FanBoost {
        action: "FAN_BOOST_ON",
    }
}

/// Fan payload for fan boost off.
pub const fn fan_boost_off() -> FanBoost {
    FanBoost {
        action: "FAN_BOOST_OFF",
    }
}
