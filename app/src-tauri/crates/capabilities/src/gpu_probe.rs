//! C# `DgpuGenerationProbe` port (`DgpuGeneration.cs`). Pure; no registry.

use crate::gpu_gen::DgpuGeneration;

/// C# `DgpuProbeSource` (`DgpuGeneration.cs:25-34`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DgpuProbeSource {
    None,
    MarketingName,
    PciDeviceId,
}

/// C# `GpuAdapter` (`DgpuGeneration.cs:40-45`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GpuAdapter {
    pub name: String,
    pub vendor_id: Option<String>,
    pub device_id: Option<String>,
}

impl GpuAdapter {
    pub const NVIDIA_VENDOR_ID: &'static str = "10DE";

    pub fn new(name: impl Into<String>, vendor_id: Option<&str>, device_id: Option<&str>) -> Self {
        Self {
            name: name.into(),
            vendor_id: vendor_id.map(str::to_owned),
            device_id: device_id.map(str::to_owned),
        }
    }

    /// C# `GpuAdapter.IsNvidia` (`DgpuGeneration.cs:44`).
    pub fn is_nvidia(&self) -> bool {
        self.vendor_id
            .as_deref()
            .is_some_and(|id| id.eq_ignore_ascii_case(Self::NVIDIA_VENDOR_ID))
    }
}

/// C# `DgpuIdentity` (`DgpuGeneration.cs:52-70`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DgpuIdentity {
    pub generation: DgpuGeneration,
    pub source: DgpuProbeSource,
    pub has_dgpu: bool,
    pub marketing_name: Option<String>,
    pub device_id: Option<String>,
}

/// C# `DgpuGenerationProbe.Resolve` (`DgpuGeneration.cs:87-111`).
pub fn resolve_dgpu(adapters: &[GpuAdapter], enumeration_available: bool) -> DgpuIdentity {
    if !enumeration_available {
        return DgpuIdentity {
            generation: DgpuGeneration::Unknown,
            source: DgpuProbeSource::None,
            has_dgpu: false,
            marketing_name: None,
            device_id: None,
        };
    }

    let mut first_nvidia = None;
    for adapter in adapters.iter().filter(|adapter| adapter.is_nvidia()) {
        if first_nvidia.is_none() {
            first_nvidia = Some(adapter);
        }
        if let Some(generation) = from_marketing_name(&adapter.name) {
            return identity(adapter, generation, DgpuProbeSource::MarketingName);
        }
    }
    let Some(first) = first_nvidia else {
        return DgpuIdentity {
            generation: DgpuGeneration::NoDgpu,
            source: DgpuProbeSource::None,
            has_dgpu: false,
            marketing_name: None,
            device_id: None,
        };
    };
    for adapter in adapters.iter().filter(|adapter| adapter.is_nvidia()) {
        if let Some(device_id) = adapter.device_id.as_deref() {
            if let Some(generation) = from_device_id(device_id) {
                return identity(adapter, generation, DgpuProbeSource::PciDeviceId);
            }
        }
    }
    DgpuIdentity {
        generation: DgpuGeneration::Unknown,
        source: DgpuProbeSource::None,
        has_dgpu: true,
        marketing_name: Some(first.name.clone()),
        device_id: first.device_id.clone(),
    }
}

fn identity(
    adapter: &GpuAdapter,
    generation: DgpuGeneration,
    source: DgpuProbeSource,
) -> DgpuIdentity {
    DgpuIdentity {
        generation,
        source,
        has_dgpu: true,
        marketing_name: Some(adapter.name.clone()),
        device_id: adapter.device_id.clone(),
    }
}

/// C# `DgpuGenerationProbe.FromMarketingName` (`DgpuGeneration.cs:114-129`).
pub fn from_marketing_name(name: &str) -> Option<DgpuGeneration> {
    if name.trim().is_empty() {
        return None;
    }
    let bytes = name.as_bytes();
    let n = bytes.len();
    let mut i = 0;
    while i + 3 <= n {
        if rtx_at(bytes, i) && boundary_before(bytes, i) {
            let mut j = i + 3;
            while j < n && bytes[j].is_ascii_whitespace() {
                j += 1;
            }
            if j + 4 <= n
                && bytes[j..j + 4].iter().all(u8::is_ascii_digit)
                && boundary_after(bytes, j + 4)
            {
                return generation_from_model_prefix(&bytes[j..j + 4]);
            }
        }
        i += 1;
    }
    None
}

/// C# `DgpuGenerationProbe.FromDeviceId` (`DgpuGeneration.cs:132-146`).
pub fn from_device_id(device_id: &str) -> Option<DgpuGeneration> {
    let value = try_parse_hex16(device_id)?;
    let [high, _] = value.to_be_bytes();
    generation_from_pci_high_byte(high)
}

/// C# `DgpuGeneration.cs:141-143` INFERRED PCI high-byte bands.
const fn generation_from_pci_high_byte(high: u8) -> Option<DgpuGeneration> {
    match high {
        0x20..=0x25 => Some(DgpuGeneration::Gen30),
        0x26..=0x28 => Some(DgpuGeneration::Gen40),
        0x2B..=0x30 => Some(DgpuGeneration::Gen50),
        _ => None,
    }
}

fn generation_from_model_prefix(digits: &[u8]) -> Option<DgpuGeneration> {
    match digits {
        [b'3', b'0', _, _] => Some(DgpuGeneration::Gen30),
        [b'4', b'0', _, _] => Some(DgpuGeneration::Gen40),
        [b'5', b'0', _, _] => Some(DgpuGeneration::Gen50),
        _ => None,
    }
}

fn rtx_at(bytes: &[u8], i: usize) -> bool {
    bytes[i].eq_ignore_ascii_case(&b'r')
        && bytes[i + 1].eq_ignore_ascii_case(&b't')
        && bytes[i + 2].eq_ignore_ascii_case(&b'x')
}

fn is_word_byte(b: u8) -> bool {
    b.is_ascii_alphanumeric() || b == b'_'
}

fn boundary_before(bytes: &[u8], i: usize) -> bool {
    is_word_byte(bytes[i]) && (i == 0 || !is_word_byte(bytes[i - 1]))
}

fn boundary_after(bytes: &[u8], end: usize) -> bool {
    end > 0 && is_word_byte(bytes[end - 1]) && (end == bytes.len() || !is_word_byte(bytes[end]))
}

/// C# `TryParseHex16` (`DgpuGeneration.cs:149-162`).
fn try_parse_hex16(text: &str) -> Option<u16> {
    let trimmed = text.trim();
    if trimmed.is_empty() {
        return None;
    }
    let after_dev = match find_ignore_ascii_case(trimmed, "DEV_") {
        Some(at) => &trimmed[at + 4..],
        None => trimmed,
    };
    let after_prefix = if after_dev.len() >= 2 && after_dev[..2].eq_ignore_ascii_case("0x") {
        &after_dev[2..]
    } else {
        after_dev
    };
    let token = after_prefix.trim();
    if token.len() != 4 {
        return None;
    }
    u16::from_str_radix(token, 16).ok()
}

fn find_ignore_ascii_case(haystack: &str, needle: &str) -> Option<usize> {
    haystack
        .as_bytes()
        .windows(needle.len())
        .position(|window| window.eq_ignore_ascii_case(needle.as_bytes()))
}
