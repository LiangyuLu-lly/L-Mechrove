//! ItemSupport capability matrix. Lighting rows are fail-closed on ItemSupport, never MQTT Seen.

mod gpu_gen;
mod gpu_probe;
mod item_support;
mod lighting_catalog;
mod lighting_gate;
mod matrix;

pub use gpu_gen::{
    DgpuGeneration, GpuRouteGate, HOT_SWAP_OFF, HOT_SWAP_ON, IGPU_ONLY_AUTO, IGPU_ONLY_OFF,
    IGPU_ONLY_ON, RESTART, TOGGLE_IGPU, TOGGLE_OFF, TOGGLE_ON,
};
pub use gpu_probe::{
    from_device_id, from_marketing_name, resolve_dgpu, DgpuIdentity, DgpuProbeSource, GpuAdapter,
};
pub use item_support::ItemSupport;
pub use lighting_catalog::{effect_allowed, keyboard_catalog, lightbar_catalog, logo_catalog};
pub use lighting_gate::LightingVisibility;
pub use matrix::{FeatureBit, FeatureBitDefinition, FeatureMatrix, FeatureMissingPolicy};

/// Parse or shape error for ItemSupport JSON.
#[derive(Debug, thiserror::Error)]
#[non_exhaustive]
pub enum Error {
    /// JSON text was not valid.
    #[error("invalid ItemSupport JSON")]
    Json(#[from] serde_json::Error),
    /// Root value was an array, number, or other non-object.
    #[error("ItemSupport root must be a JSON object")]
    RootNotObject,
}
