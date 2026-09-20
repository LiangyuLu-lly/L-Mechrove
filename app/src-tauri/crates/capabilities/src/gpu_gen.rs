use crate::matrix::FeatureMatrix;

pub const TOGGLE_ON: &str = "DGPU_DIRECT_CONNECT_TOGGLE_ON";
pub const TOGGLE_OFF: &str = "DGPU_DIRECT_CONNECT_TOGGLE_OFF";
pub const TOGGLE_IGPU: &str = "DGPU_DIRECT_CONNECT_TOGGLE_IGPU";
pub const RESTART: &str = "DGPU_DIRECT_CONNECT_RESTART";
pub const IGPU_ONLY_ON: &str = "IGPU_ONLY_CONNECT_RB_ON";
pub const IGPU_ONLY_OFF: &str = "IGPU_ONLY_CONNECT_RB_OFF";
pub const IGPU_ONLY_AUTO: &str = "IGPU_ONLY_CONNECT_RB_AUTO";
pub const HOT_SWAP_ON: &str = "GPU_HOTSWAP_ON";
pub const HOT_SWAP_OFF: &str = "GPU_HOTSWAP_OFF";

const GEN30: &[&str] = &[TOGGLE_ON, TOGGLE_OFF];
const GEN40: &[&str] = &[TOGGLE_ON, TOGGLE_OFF, TOGGLE_IGPU, RESTART];
const GEN40_THREE: &[&str] = &[TOGGLE_ON, TOGGLE_OFF, TOGGLE_IGPU, IGPU_ONLY_ON, IGPU_ONLY_OFF, RESTART];
const GEN50: &[&str] = &[TOGGLE_ON, TOGGLE_OFF, TOGGLE_IGPU, IGPU_ONLY_ON, IGPU_ONLY_OFF, RESTART];
const GEN50_HOT: &[&str] = &[
    TOGGLE_ON,
    TOGGLE_OFF,
    TOGGLE_IGPU,
    IGPU_ONLY_ON,
    IGPU_ONLY_OFF,
    RESTART,
    HOT_SWAP_ON,
    HOT_SWAP_OFF,
];

/// dGPU generation (axis 2). Unknown and NoDgpu are distinct; never defaulted to a generation.
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub enum DgpuGeneration {
    Unknown,
    NoDgpu,
    Gen30,
    Gen40,
    Gen50,
}

/// Product GPU route gate. AUTO is never offered. Hot-swap is Gen50 plus both ItemSupport flags.
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct GpuRouteGate {
    pub generation: DgpuGeneration,
    pub three_mode: bool,
    pub hot_swap: bool,
}

impl GpuRouteGate {
    pub fn from_matrix(generation: DgpuGeneration, three_mode: bool, matrix: &FeatureMatrix) -> Self {
        Self {
            generation,
            three_mode,
            hot_swap: matrix.gpu_hot_swap_available(),
        }
    }

    pub const fn offered_actions(self) -> &'static [&'static str] {
        match self.generation {
            DgpuGeneration::Unknown | DgpuGeneration::NoDgpu => &[],
            DgpuGeneration::Gen30 => GEN30,
            DgpuGeneration::Gen40 => match self.three_mode {
                true => GEN40_THREE,
                false => GEN40,
            },
            DgpuGeneration::Gen50 => match self.hot_swap {
                true => GEN50_HOT,
                false => GEN50,
            },
        }
    }

    pub fn allows(self, action: &str) -> bool {
        self.offered_actions().contains(&action)
    }
}
