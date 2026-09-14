# Device Capability Resolution Design

**Status:** Approved for implementation from the three supplied local official console packages.

## Goal

Show only controls that the current Mechrevo laptop can safely use, while preserving controls that the official GCU confirms at runtime. The implementation must avoid treating the three package archives, a GPU generation, or a marketing model name as proof that a particular machine supports a write operation.

## Evidence Reviewed

The local packages are the only package evidence in scope:

| Package | Intended family | Version | `CustomizeTarget` | `UserFanTables` projects | RGB registry shape |
| --- | --- | --- | ---: | ---: | --- |
| `ControlCenter_5.17.49.19_Mechrevo` | 40-series regular | 5.17.49.19 | 17 | 24 | 19 keys, 489 values, 18 lighting-zone keys |
| `ControlCenter_5.17.51.34_Mechrevo` | 40-series dual-display/three-mode | 5.17.51.34 | 17 | 24 | 23 keys, 533 values, 22 lighting-zone keys |
| `ControlCenterX_5.56.60.26_Mechrevo` | 50-series | 5.56.60.26 | 56 | 24 | 23 keys, 600 values, 22 lighting-zone keys and seven logo-light values |

All three packages contain the same 24 project-family directories:

```text
PH4AQE3, PH4AQxx, PH4ARxx, PH4AUxf, PH4AUxx, PH4AXxx,
PH4PGx1, PH4PGx2, PH4PRxx, PH4PUxx, PH4TQx1, PH4TRX1,
PH4TUX1, PH6AQxx, PH6ARxx, PH6PG0x, PH6PG0x150W,
PH6PG3x, PH6PG3x150W, PH6PG7x, PH6PG7x150W, PH6PGEx,
PH6PRxx, PH6TRX1
```

The compared fan-table sample (`PH6PG0x/M1T1.json`) is byte-identical in all three packages. The archives do not contain a reliable map from a marketing SKU to a project family or per-feature support flag. Their package evidence therefore constrains parsing and lighting-layout detection; it cannot safely replace current-machine `ItemSupport` or GCU status.

The official GCU architecture remains:

```text
BIOS / current OEM registry
  -> GCUService ItemSupport and MQTT status
  -> GCUBridge broker
  -> L-Mechrevo MechrevoHw
  -> Settings and CustomModeForm
```

## Current Defects

`MechrevoDeviceCapabilities` reduces a missing feature field and an explicit false field to the same `false` value. `Settings.RefreshDeviceCapabilities()` then mixes individual static flags, observed status flags, and range checks inline. `CustomModeForm` creates most custom controls first and hides a subset only after status arrives. This causes three observable problems:

1. A stale registry can expose a control after GCU reports it unsupported.
2. A supported current device can hide a feature before its first MQTT status packet arrives.
3. The common page and custom-mode page use different rules for the same capability.

`MechrevoHw.SupportsGpuHotSwap` also requires the static `Capabilities.GpuHotSwap` property, so a future runtime report cannot independently promote the capability. That rule must remain conservative: it may only be promoted by all official hot-switch gates, never by a 50-series model assumption.

## Capability Model

Introduce one internal snapshot with explicit state and evidence:

```csharp
internal enum DeviceFeature
{
    KeyboardLighting,
    LightbarLighting,
    LogoLighting,
    DisplayRefresh,
    ColorCalibration,
    LocalDimming,
    LcdOverdrive,
    LiquidCooling,
    FanBoost,
    FanSettings,
    GpuOverclock,
    DgpuDirect,
    IgpuOnly,
    GpuHotSwap,
    TurboMode,
    TurboSubMode,
    CustomPerformance,
}

internal enum FeatureAvailability
{
    Unknown,
    Unsupported,
    Supported,
}

internal enum FeatureEvidence
{
    None,
    OfficialRegistry,
    RuntimeStatus,
    AdjustableRange,
    DirectDriver,
}

internal readonly record struct FeatureSupport(
    FeatureAvailability Availability,
    FeatureEvidence Evidence);
```

`DeviceCapabilitySnapshot` is immutable and maps each `DeviceFeature` to a `FeatureSupport`. It also exposes `IsSupported`, `IsKnownUnsupported`, and a stable layout fingerprint. It is calculated by `MechrevoHw`, which has both the static registry profile and current MQTT state. Forms must consume the snapshot rather than recalculate `static || runtime` rules.

## Precedence Rules

For a normal feature, resolve inputs in this order:

1. An explicit runtime support flag is authoritative. `false` becomes `Unsupported`; `true` becomes `Supported` with `RuntimeStatus` evidence.
2. A status packet that can only come from that command family becomes `Supported` with `RuntimeStatus` evidence, unless that packet itself carries an explicit false support flag.
3. A valid adjustable range becomes `Supported` with `AdjustableRange` evidence. This is applicable to fan values, TCC, TGP, Dynamic Boost, and GPU offsets.
4. A direct NVIDIA driver range becomes `Supported` with `DirectDriver` evidence for the matching GPU-offset field.
5. An explicitly present `ItemSupport` / `GpuConfig` flag becomes `Supported` or `Unsupported` with `OfficialRegistry` evidence.
6. A missing field remains `Unknown`.

The resolver must preserve whether a registry field was present. A profile that contains unrelated values does not turn every missing feature into `Unsupported`.

Special rules:

| Feature | Additional rule |
| --- | --- |
| GPU hot switch | `Supported` only when all five official gates are present and true: NVIDIA GPU, iGPU-only support, `APVersionCheck > 23`, `GpuHotSwapSwitchSupport`, and `lgpuHotSwapSwitchStatus`. Runtime mode status alone cannot promote it. |
| GPU overclock | A direct-driver range remains valid even when GCU `LCHWOC/Status` says its own channel is unsupported. An explicit GCU false only disables the GCU channel. |
| Liquid cooling | A `BT_LC/Status` packet proves the panel is relevant. It must not make manual controls writable when `LC_action` is explicitly unsupported or no connection exists. |
| Lighting | A keyboard/lightbar/logo status proves that endpoint exists even while its current power state is off. `powerStatus=off` is not an unsupported capability. |
| Custom performance | Any adjustable custom range or explicitly supported fan/CPU tuning capability enables custom mode. |

## Unknown-Feature UI Policy

The approved policy is:

- Hide unknown and unsupported write-risk controls: GPU mode changes, GPU overclocking, TGP/DB/TCC writes, fan-curve writes, liquid-cooling manual control, and unsupported calibration actions.
- Keep permanent low-risk structural panels such as performance selection and battery protection visible.
- Where a safe read-only area has a useful placeholder, render it disabled with `等待设备确认`; do not show a clickable control.
- Never change a saved configuration merely because a capability is `Unknown`. Existing cleanup of `gpu_auto` is allowed only after an explicit known-unsupported GPU result.
- `UiAuditMode` continues to reveal all controls for layout testing and does not alter normal-device capability decisions.

No new verbose end-user diagnostic panel is added. The application log records the snapshot's identity, per-feature availability, and evidence only when its fingerprint changes.

## Official Package Usage

The executable archives are development evidence and are not read at application runtime or copied into the release. The source tree stores a compact `OfficialConsoleCatalog` containing only non-proprietary facts required for regression validation:

- package version and `CustomizeTarget`;
- the 24 known fan-table project identifiers;
- RGB layout signatures: baseline, three-plus-one zone, and logo-capable layout.

The catalog does not map a project ID to an advertised model and does not assert that a package family supports GPU switching, overclocking, or liquid cooling. At runtime, RGB layout is detected from the installed OEM registry. The catalog prevents accidental regression in parsing those supplied formats and provides test fixtures for expected layout variants.

## UI Integration

`Settings.RefreshDeviceCapabilities()` obtains one snapshot at its start and uses it for:

- keyboard, lightbar, logo, refresh, display, calibration, liquid-cooling, and quick-switch visibility;
- turbo and custom performance buttons;
- iGPU/direct GPU buttons and hot-switch option visibility;
- capability layout fingerprinting and one-time reflow.

`CustomModeForm.OnCustomChanged()` obtains the same snapshot and uses it to decide row visibility before applying ranges. It must hide a row when no supported write path exists, not merely disable its slider after the form was built. It must keep core and memory overclock rows independently visible if only one has a valid range.

The hot-switch design at `docs/superpowers/specs/2026-08-22-gpu-hot-switch-lighting-design.md` remains authoritative for GPU-switch commands, process safety, confirmation, and reboot fallback. This work supplies one capability decision to that design; it does not change the switch protocol.

## Tests

Unit tests must cover real resolver behavior without a registry or hardware dependency:

1. Explicit registry false differs from missing registry input.
2. Runtime explicit false overrides stale static true.
3. Runtime endpoint observation promotes a missing static profile where that endpoint is safe to identify.
4. Adjustable range and direct-driver evidence promote only their own write path.
5. GPU hot switch is rejected when any official gate is missing, false, or AP version is 23 or lower.
6. A logo-capable RGB layout is recognized from its registry signature; a normal lightbar layout is not incorrectly promoted to logo support.
7. Catalog facts include all three supplied package variants and exactly the known 24 project IDs.
8. Snapshot fingerprint changes only when visible capability state/evidence changes.
9. Existing GCU parser tests and custom-mode visibility tests remain green.

Validation must run the focused unit tests, the full `MechrevoLite.slnx` Release test suite with `-p:GITHUB_ACTIONS=true`, and the Release project build with the same property. No claim of physical machine compatibility is made until manual testing occurs on representative 40-series regular, 40-series three-mode, and 50-series machines.

## Risks and Mitigations

| Risk | Concrete failure mode | Mitigation |
| --- | --- | --- |
| Incorrect static interpretation | A registry profile with an unrelated value causes a hidden feature to be treated as unsupported, or a stale key exposes a write control. | Preserve per-key presence, give explicit runtime results precedence, and leave missing fields `Unknown`. |
| Package overreach | A shared `PH6` fan-table directory is assumed to identify a marketing SKU and enables a control that its hardware rejects. | Store the package data only as parser/layout evidence; do not use it as a model-to-feature map. |
| UI churn | As several MQTT status topics arrive, each one reflows the dashboard and causes visible flicker. | Compare snapshot layout fingerprints and reflow only when visible state changes. |
| Hot-switch regression | Runtime GPU mode status is incorrectly interpreted as hot-switch support and sends an unsafe command. | Preserve all five official gates and retain the existing reboot fallback. |
| Overclock path conflation | A negative GCU HWOC status hides a valid direct-driver offset path. | Model GCU and direct-driver evidence independently, then expose the row only when at least one verified path remains. |

## Out of Scope

- Downloading, installing, or modifying additional official packages.
- Replacing GCU hardware communication with guessed ACPI or MQTT commands.
- Claiming universal support across every 40/50-series machine.
- Adding a marketing-model database without authoritative model-to-project evidence.
- Changing packaging, version number, licensing, official-console isolation, or unrelated UI layout.
