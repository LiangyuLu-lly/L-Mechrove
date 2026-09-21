//! Given OfficialConsoleIsolation.cs. Isolating the vendor console disables its
//! startup entry / tray surface — it never kills L-Mechrevo and never publishes MQTT.

use crate::hw_backend::HostError;

/// Real path. `OfficialConsoleIsolation.cs` toggles the vendor console's own
/// startup/tray registration; this host does not own that surface yet, so Real
/// reports unavailable rather than pretending the switch took effect.
pub fn apply_real(_on: bool) -> Result<(), HostError> {
    Err(HostError::RealUnavailable)
}
