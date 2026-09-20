use crate::item_support::ItemSupport;

/// Service-value missing policy from `FeatureMatrix.cs`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub enum FeatureMissingPolicy {
    /// EC / NVRAM derived: omitted = unsupported.
    FailClosed,
    /// Vendor constant: omitted still reports supported.
    VendorConstantOn,
    /// Vendor constant that is on only for non-commercial models.
    VendorConstantNonCommercial,
}

/// One row of the model × feature matrix.
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub enum FeatureBit {
    AcRecoverySwitch,
    AcRecoveryBios,
    Keyboard,
    SystemMonitor,
    FanSettings,
    OverclockSettings,
    ColorCalibration,
    DgpuDirect,
    FanBoost,
    AmdPlatform,
    NvidiaGpu,
    Lightbar,
    RgbLightbar,
    Numpad,
    LiquidCooling,
    LiquidCoolingAutoMode,
    RamFan15,
    TurboMode,
    TypeC,
    Commercial,
    CommercialHave20Db,
    GpuHotSwapSwitch,
    HotSwapStatus,
}

/// Capability bit → service key → missing policy.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct FeatureBitDefinition {
    pub bit: FeatureBit,
    pub key: &'static str,
    pub missing: FeatureMissingPolicy,
}

/// Read-only ItemSupport matrix. Presence uses the written value; omission uses [`FeatureMissingPolicy`].
#[derive(Debug, Clone)]
pub struct FeatureMatrix {
    values: ItemSupport,
    is_commercial: bool,
    profile_available: bool,
}

impl FeatureMatrix {
    pub const fn definition(bit: FeatureBit) -> FeatureBitDefinition {
        match bit {
            FeatureBit::AcRecoverySwitch => {
                def(bit, "AcRecoverySwitchSupport", FeatureMissingPolicy::VendorConstantOn)
            }
            FeatureBit::Keyboard => {
                def(bit, "KeyboardSupport", FeatureMissingPolicy::VendorConstantOn)
            }
            FeatureBit::SystemMonitor => {
                def(bit, "SystemMonitorSupport", FeatureMissingPolicy::VendorConstantOn)
            }
            FeatureBit::FanSettings => def(
                bit,
                "FanSettingsSupport",
                FeatureMissingPolicy::VendorConstantNonCommercial,
            ),
            FeatureBit::OverclockSettings => def(
                bit,
                "OcSettingsSupport",
                FeatureMissingPolicy::VendorConstantNonCommercial,
            ),
            FeatureBit::AcRecoveryBios => {
                def(bit, "AcRecoverySwitchBiosSupport", FeatureMissingPolicy::FailClosed)
            }
            FeatureBit::ColorCalibration => {
                def(bit, "ColorCalibrationSupport", FeatureMissingPolicy::FailClosed)
            }
            FeatureBit::DgpuDirect => {
                def(bit, "DGpuDirectConnectionSupport", FeatureMissingPolicy::FailClosed)
            }
            FeatureBit::FanBoost => def(bit, "FanBoostBtnSupport", FeatureMissingPolicy::FailClosed),
            FeatureBit::AmdPlatform => def(bit, "IsAMDPlatform", FeatureMissingPolicy::FailClosed),
            FeatureBit::NvidiaGpu => def(bit, "IsNvGpu", FeatureMissingPolicy::FailClosed),
            FeatureBit::Lightbar => def(bit, "LightbarSupport", FeatureMissingPolicy::FailClosed),
            FeatureBit::RgbLightbar => {
                def(bit, "RGBLightbarSupport", FeatureMissingPolicy::FailClosed)
            }
            FeatureBit::Numpad => def(bit, "NumPadSupport", FeatureMissingPolicy::FailClosed),
            FeatureBit::LiquidCooling => {
                def(bit, "LiquidCoolingSupport", FeatureMissingPolicy::FailClosed)
            }
            FeatureBit::LiquidCoolingAutoMode => {
                def(bit, "LiquidCoolingAutoModeSupport", FeatureMissingPolicy::FailClosed)
            }
            FeatureBit::RamFan15 => def(bit, "RamFan1p5Support", FeatureMissingPolicy::FailClosed),
            FeatureBit::TurboMode => def(bit, "TurboModeSupport", FeatureMissingPolicy::FailClosed),
            FeatureBit::TypeC => def(bit, "TypeCSupport", FeatureMissingPolicy::FailClosed),
            FeatureBit::Commercial => {
                def(bit, "IsProjectIdCommercial", FeatureMissingPolicy::FailClosed)
            }
            FeatureBit::CommercialHave20Db => {
                def(bit, "IsProjectIdCommercialHAVE20DB", FeatureMissingPolicy::FailClosed)
            }
            FeatureBit::GpuHotSwapSwitch => {
                def(bit, "GpuHotSwapSwitchSupport", FeatureMissingPolicy::FailClosed)
            }
            FeatureBit::HotSwapStatus => {
                def(bit, "lgpuHotSwapSwitchStatus", FeatureMissingPolicy::FailClosed)
            }
        }
    }

    pub fn from_values(values: &ItemSupport) -> Self {
        Self {
            is_commercial: values.is_truthy("IsProjectIdCommercial"),
            profile_available: !values.is_empty(),
            values: values.clone(),
        }
    }

    pub fn is_commercial(&self) -> bool {
        self.is_commercial
    }

    pub fn profile_available(&self) -> bool {
        self.profile_available
    }

    pub fn is_present(&self, bit: FeatureBit) -> bool {
        self.values.is_present(Self::definition(bit).key)
    }

    pub fn is_supported(&self, bit: FeatureBit) -> bool {
        let definition = Self::definition(bit);
        if self.values.is_present(definition.key) {
            return self.values.is_truthy(definition.key);
        }
        match definition.missing {
            FeatureMissingPolicy::FailClosed => false,
            FeatureMissingPolicy::VendorConstantOn => true,
            FeatureMissingPolicy::VendorConstantNonCommercial => !self.is_commercial,
        }
    }

    pub fn gpu_hot_swap_available(&self) -> bool {
        self.is_present(FeatureBit::GpuHotSwapSwitch)
            && self.is_present(FeatureBit::HotSwapStatus)
            && self.is_supported(FeatureBit::GpuHotSwapSwitch)
            && self.is_supported(FeatureBit::HotSwapStatus)
    }
}

const fn def(bit: FeatureBit, key: &'static str, missing: FeatureMissingPolicy) -> FeatureBitDefinition {
    FeatureBitDefinition { bit, key, missing }
}
