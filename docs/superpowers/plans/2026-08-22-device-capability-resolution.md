# Device Capability Resolution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve each device feature from explicit registry, runtime, range, and direct-driver evidence so L-Mechrevo only displays usable controls for the current machine.

**Architecture:** Add a small immutable capability snapshot in `Hardware`, retain raw registry-key presence in the existing capability profile, and calculate one snapshot from `MechrevoHw`. The two WinForms surfaces consume that snapshot instead of independently combining booleans. The supplied packages are represented only as compact evidence metadata and regression fixtures; live `ItemSupport` and GCU status remain authoritative.

**Tech Stack:** .NET 10, C# WinForms, xUnit 2.9, Newtonsoft.Json, existing MQTT/GCU integration.

**Spec:** `docs/superpowers/specs/2026-08-22-device-capability-resolution-design.md`

## Global Constraints

- Target framework remains `net10.0-windows10.0.19041.0`; add no NuGet package.
- Do not install, execute, modify, redistribute, or read an official console archive at application runtime.
- Treat current `ItemSupport`, `MySetting\\GpuConfig`, and GCU status as device authority; package metadata is validation evidence only.
- Unknown write-risk features remain hidden in normal UI; `UiAuditMode` retains full layout visibility.
- Preserve the five official GPU-hot-switch gates defined in `2026-08-22-gpu-hot-switch-lighting-design.md`.
- Use `Dictionary<string, object>` for MQTT payloads; do not add anonymous payload objects.
- Do not stage, commit, push, delete, or overwrite user release artifacts without explicit user approval.
- Verify with `dotnet test .\\MechrevoLite.slnx -c Release -p:GITHUB_ACTIONS=true` and `dotnet build .\\src\\MechrevoLiteWin\\MechrevoLite.csproj -c Release -p:GITHUB_ACTIONS=true`.

---

## File Structure

| File | Responsibility |
| --- | --- |
| `src/MechrevoLiteWin/Hardware/DeviceCapabilitySnapshot.cs` | Defines feature IDs, tri-state availability, evidence sources, immutable support values, snapshot fingerprinting, and resolver helpers. |
| `src/MechrevoLiteWin/Hardware/OfficialConsoleCatalog.cs` | Stores the three compact package fingerprints, project-family list, and RGB-layout signatures used by tests and registry layout recognition. |
| `src/MechrevoLiteWin/Hardware/MechrevoDeviceCapabilities.cs` | Retains canonical raw registry-key presence and exposes explicit static feature state without changing existing public booleans. |
| `src/MechrevoLiteWin/Hardware/MechrevoHw.cs` | Produces a snapshot from the static profile, MQTT state, adjustable ranges, and direct-driver availability; raises changes only when its fingerprint changes. |
| `src/MechrevoLiteWin/Settings.cs` | Uses the snapshot for dashboard and GPU/lights/display/liquid-cooling visibility. |
| `src/MechrevoLiteWin/CustomModeForm.cs` | Uses the snapshot to hide unsupported custom write rows before ranges are enabled. |
| `tests/MechrevoLite.Tests/DeviceCapabilitySnapshotTests.cs` | Unit tests for precedence, fingerprints, RGB signature facts, package catalog facts, and hot-switch safety. |
| `tests/MechrevoLite.Tests/DeviceCapabilityTests.cs` | Extends existing parser/hardware tests only where needed to assert snapshot integration. |

### Task 1: Define Feature States and Catalog Facts

**Files:**
- Create: `src/MechrevoLiteWin/Hardware/DeviceCapabilitySnapshot.cs`
- Create: `src/MechrevoLiteWin/Hardware/OfficialConsoleCatalog.cs`
- Create: `tests/MechrevoLite.Tests/DeviceCapabilitySnapshotTests.cs`

**Interfaces:**
- Produces: `DeviceFeature`, `FeatureAvailability`, `FeatureEvidence`, `FeatureSupport`, `DeviceCapabilitySnapshot`.
- Produces: `OfficialConsoleCatalog.Packages`, `OfficialConsoleCatalog.ProjectIds`, `OfficialConsoleCatalog.DetectRgbLayout(...)`.
- Consumes later: `DeviceCapabilitySnapshot.Get(DeviceFeature)`, `IsSupported(DeviceFeature)`, `LayoutFingerprint`.

- [ ] **Step 1: Write the failing catalog and snapshot tests**

```csharp
[Fact]
public void Catalog_ContainsExactlyTheThreeReviewedConsoleVariants()
{
    Assert.Equal(new[] { "5.17.49.19", "5.17.51.34", "5.56.60.26" },
        OfficialConsoleCatalog.Packages.Select(package => package.Version));
    Assert.Equal(24, OfficialConsoleCatalog.ProjectIds.Count);
}

[Fact]
public void SnapshotFingerprint_ChangesWhenAvailabilityChanges()
{
    var before = DeviceCapabilitySnapshot.Empty;
    var after = before.With(DeviceFeature.KeyboardLighting,
        new FeatureSupport(FeatureAvailability.Supported, FeatureEvidence.RuntimeStatus));

    Assert.NotEqual(before.LayoutFingerprint, after.LayoutFingerprint);
}
```

- [ ] **Step 2: Run the focused tests and verify they fail because the types do not exist**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release -p:GITHUB_ACTIONS=true --filter "FullyQualifiedName~DeviceCapabilitySnapshotTests"
```

Expected: compilation failure identifying missing `OfficialConsoleCatalog` and `DeviceCapabilitySnapshot`.

- [ ] **Step 3: Implement the minimal immutable types and catalog**

```csharp
internal readonly record struct FeatureSupport(
    FeatureAvailability Availability,
    FeatureEvidence Evidence)
{
    public bool IsSupported => Availability == FeatureAvailability.Supported;
}

internal sealed class DeviceCapabilitySnapshot
{
    public static DeviceCapabilitySnapshot Empty { get; } = new();
    public FeatureSupport Get(DeviceFeature feature) => _features.TryGetValue(feature, out var value) ? value : default;
    public DeviceCapabilitySnapshot With(DeviceFeature feature, FeatureSupport support) { /* copy map */ }
}
```

Populate `OfficialConsoleCatalog` with only the reviewed version strings, targets (`17`, `17`, `56`), the exact 24 project IDs from the spec, and RGB-layout signature identifiers. Do not embed registry values, EXE data, or full OEM package contents.

- [ ] **Step 4: Run the focused tests and verify they pass**

Run the command from Step 2.

Expected: focused tests pass with no new warning.

- [ ] **Step 5: Add RGB-layout signature regression coverage**

```csharp
[Fact]
public void ThreePlusOneRgbLayout_IsNotMistakenForLogoLayout()
{
    var layout = OfficialConsoleCatalog.DetectRgbLayout(
        new[] { "MEZone_3p1nd_101", "MEZone_3p1nd_102" }, Array.Empty<string>());

    Assert.Equal(OfficialRgbLayout.ThreePlusOne, layout);
}
```

- [ ] **Step 6: Run the focused tests again**

Run the command from Step 2.

Expected: all `DeviceCapabilitySnapshotTests` pass.

### Task 2: Preserve Explicit Registry Presence and Resolve Static States

**Files:**
- Modify: `src/MechrevoLiteWin/Hardware/MechrevoDeviceCapabilities.cs`
- Modify: `tests/MechrevoLite.Tests/DeviceCapabilityTests.cs`
- Test: `tests/MechrevoLite.Tests/DeviceCapabilitySnapshotTests.cs`

**Interfaces:**
- Consumes: `DeviceFeature`, `FeatureSupport` from Task 1.
- Produces: `MechrevoDeviceCapabilities.GetStaticSupport(DeviceFeature)`.
- Produces: static support as `Unknown` when a field was absent and `Unsupported` when the canonical feature field was explicitly false.

- [ ] **Step 1: Write a failing explicit-false-versus-missing test**

```csharp
[Fact]
public void StaticSupport_DistinguishesMissingFieldFromExplicitFalse()
{
    var missing = MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>());
    var disabled = MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>
    {
        ["LightbarSupport"] = 0,
    });

    Assert.Equal(FeatureAvailability.Unknown,
        missing.GetStaticSupport(DeviceFeature.LightbarLighting).Availability);
    Assert.Equal(FeatureAvailability.Unsupported,
        disabled.GetStaticSupport(DeviceFeature.LightbarLighting).Availability);
}
```

- [ ] **Step 2: Run the focused tests and verify they fail because `GetStaticSupport` does not exist**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release -p:GITHUB_ACTIONS=true --filter "FullyQualifiedName~StaticSupport_Distinguishes"
```

Expected: compilation failure for missing `GetStaticSupport`.

- [ ] **Step 3: Implement canonical field-presence tracking**

Store canonical raw names/values when `FromValues` receives a registry dictionary. Use the same aliases already parsed by the class and evaluate them as `bool?`, not a collapsed boolean:

```csharp
internal FeatureSupport GetStaticSupport(DeviceFeature feature)
{
    bool? value = GetCanonicalFlag(feature);
    return value switch
    {
        true => new(FeatureAvailability.Supported, FeatureEvidence.OfficialRegistry),
        false => new(FeatureAvailability.Unsupported, FeatureEvidence.OfficialRegistry),
        _ => default,
    };
}
```

Keep existing public boolean properties as compatibility projections of the same raw fields. An object created with an initializer and no raw registry dictionary remains `Unknown` unless its existing boolean is true.

- [ ] **Step 4: Run focused parser and resolver tests**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release -p:GITHUB_ACTIONS=true --filter "FullyQualifiedName~DeviceCapability"
```

Expected: existing parser tests and the new presence test pass.

### Task 3: Resolve a Snapshot in MechrevoHw

**Files:**
- Modify: `src/MechrevoLiteWin/Hardware/MechrevoHw.cs`
- Modify: `tests/MechrevoLite.Tests/DeviceCapabilitySnapshotTests.cs`
- Modify: `tests/MechrevoLite.Tests/DeviceCapabilityTests.cs`

**Interfaces:**
- Consumes: `MechrevoDeviceCapabilities.GetStaticSupport(DeviceFeature)` from Task 2.
- Produces: `MechrevoHw.DeviceCapabilities` and `MechrevoHw.GetFeatureSupport(DeviceFeature)`.
- Produces: `CapabilitiesChanged` only after a snapshot fingerprint changes.

- [ ] **Step 1: Write failing precedence tests**

```csharp
[Fact]
public void RuntimeFalse_OverridesStaleStaticTrue()
{
    using var hw = new MechrevoHw(null,
        new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true });
    hw.HandleMessage("Setting/Status", "{\"IGPU_ONLY_SUPPORT\":false}");

    Assert.Equal(FeatureAvailability.Unsupported,
        hw.GetFeatureSupport(DeviceFeature.IgpuOnly).Availability);
}

[Fact]
public void ObservedKeyboardEndpoint_PromotesMissingRegistryProfile()
{
    using var hw = new MechrevoHw(null, new MechrevoDeviceCapabilities());
    hw.HandleMessage("Keyboard/Status", "{\"powerStatus\":\"OFF\"}");

    Assert.True(hw.GetFeatureSupport(DeviceFeature.KeyboardLighting).IsSupported);
}
```

- [ ] **Step 2: Run the focused tests and verify they fail because the snapshot API is absent**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release -p:GITHUB_ACTIONS=true --filter "FullyQualifiedName~RuntimeFalse_OverridesStaleStaticTrue|FullyQualifiedName~ObservedKeyboardEndpoint"
```

Expected: compilation failure for missing `GetFeatureSupport`.

- [ ] **Step 3: Implement resolver inputs with explicit precedence**

Add a cached snapshot property and recompute it after each existing capability-bearing topic. Use narrow helpers instead of broad reflection:

```csharp
public DeviceCapabilitySnapshot DeviceCapabilities { get; private set; }

public FeatureSupport GetFeatureSupport(DeviceFeature feature) => DeviceCapabilities.Get(feature);

FeatureSupport Resolve(DeviceFeature feature, bool observed, bool? runtimeSupport = null)
{
    if (runtimeSupport is bool value)
        return new(value ? FeatureAvailability.Supported : FeatureAvailability.Unsupported,
            FeatureEvidence.RuntimeStatus);
    if (observed)
        return new(FeatureAvailability.Supported, FeatureEvidence.RuntimeStatus);
    return Capabilities.GetStaticSupport(feature);
}
```

Implement feature-specific ranges and direct-driver rules from the spec. Keep `SupportsGpuHotSwap` dependent on the existing five static official gates; do not infer it from a general status packet.

- [ ] **Step 4: Add a no-churn event test**

```csharp
[Fact]
public void RepeatedEquivalentStatus_DoesNotRaiseCapabilityChangeTwice()
{
    using var hw = new MechrevoHw(null, new MechrevoDeviceCapabilities());
    int changes = 0;
    hw.CapabilitiesChanged += () => changes++;

    hw.HandleMessage("Keyboard/Status", "{\"powerStatus\":\"ON\"}");
    hw.HandleMessage("Keyboard/Status", "{\"powerStatus\":\"OFF\"}");

    Assert.Equal(1, changes);
}
```

- [ ] **Step 5: Run focused capability tests and fix regressions**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release -p:GITHUB_ACTIONS=true --filter "FullyQualifiedName~DeviceCapability"
```

Expected: all focused tests pass, including existing GPU hot-switch tests.

### Task 4: Make the Common Page Consume the Snapshot

**Files:**
- Modify: `src/MechrevoLiteWin/Settings.cs:2303-2409`
- Modify: `tests/MechrevoLite.Tests/SettingsLayoutTests.cs`

**Interfaces:**
- Consumes: `MechrevoHw.DeviceCapabilities` from Task 3.
- Produces: dashboard visibility decisions from `snapshot.IsSupported(feature)` only, except `UiAuditMode`.

- [ ] **Step 1: Write a failing visibility-policy test**

Extract the pure visibility decision to an internal helper so it can be tested without constructing a full WinForms form:

```csharp
[Fact]
public void UnknownWriteRiskFeature_IsHiddenOutsideAuditMode()
{
    var visibility = Settings.ResolveFeatureVisibility(
        new FeatureSupport(FeatureAvailability.Unknown, FeatureEvidence.None),
        writeRisk: true, auditMode: false);

    Assert.False(visibility.Visible);
    Assert.False(visibility.Enabled);
}
```

- [ ] **Step 2: Run the focused test and verify it fails because the helper does not exist**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release -p:GITHUB_ACTIONS=true --filter "FullyQualifiedName~UnknownWriteRiskFeature_IsHidden"
```

Expected: compilation failure for missing `Settings.ResolveFeatureVisibility`.

- [ ] **Step 3: Implement the minimal pure visibility helper and replace inline OR rules**

```csharp
internal static (bool Visible, bool Enabled) ResolveFeatureVisibility(
    FeatureSupport support, bool writeRisk, bool auditMode) =>
    auditMode ? (true, true) :
    support.Availability == FeatureAvailability.Supported ? (true, true) :
    writeRisk ? (false, false) : (true, false);
```

Use the returned values for every panel/control that currently uses `caps.X || hw?.SupportsX == true`. Keep the permanent sections and existing responsive reflow. Replace the hand-built layout fingerprint with `snapshot.LayoutFingerprint` plus safe page-local flags, so repeated power-state updates do not rearrange the dashboard.

- [ ] **Step 4: Run focused settings tests**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release -p:GITHUB_ACTIONS=true --filter "FullyQualifiedName~SettingsLayoutTests|FullyQualifiedName~UnknownWriteRiskFeature"
```

Expected: all selected tests pass.

### Task 5: Make Custom Mode Consume the Snapshot

**Files:**
- Modify: `src/MechrevoLiteWin/CustomModeForm.cs:542-590`
- Modify: `tests/MechrevoLite.Tests/CustomModeFormTests.cs` if it exists; otherwise create it.

**Interfaces:**
- Consumes: `MechrevoHw.GetFeatureSupport(DeviceFeature)` and existing range properties.
- Produces: independent visibility for core and memory GPU offset rows; hidden controls for unsupported write paths.

- [ ] **Step 1: Write failing custom-row policy tests**

```csharp
[Fact]
public void CoreOffsetCanRemainVisibleWhenMemoryOffsetIsUnsupported()
{
    var rows = CustomModeForm.ResolveOverclockRowVisibility(
        coreAdjustable: true, memoryAdjustable: false,
        support: new FeatureSupport(FeatureAvailability.Supported, FeatureEvidence.DirectDriver));

    Assert.True(rows.CoreVisible);
    Assert.False(rows.MemoryVisible);
}
```

- [ ] **Step 2: Run the focused test and verify it fails because the helper does not exist**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release -p:GITHUB_ACTIONS=true --filter "FullyQualifiedName~CoreOffsetCanRemainVisible"
```

Expected: compilation failure for missing `ResolveOverclockRowVisibility`.

- [ ] **Step 3: Implement the visibility helper and apply it before slider enablement**

```csharp
internal static (bool CoreVisible, bool MemoryVisible) ResolveOverclockRowVisibility(
    bool coreAdjustable, bool memoryAdjustable, FeatureSupport support) =>
    support.IsSupported
        ? (coreAdjustable, memoryAdjustable)
        : (false, false);
```

Use matching helpers for TCC, TGP, Dynamic Boost, and fan curve writes. Keep the current `ApplyRange` behavior for valid controls; do not reset user values or send writes during refresh.

- [ ] **Step 4: Run custom-mode and capability tests**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release -p:GITHUB_ACTIONS=true --filter "FullyQualifiedName~CustomMode|FullyQualifiedName~DeviceCapability"
```

Expected: all selected tests pass.

### Task 6: Full Regression and Review

**Files:**
- Modify only if a regression test exposes a scoped defect in the files above.

**Interfaces:**
- Consumes: all prior tasks.
- Produces: verified source-level behavior and an explicit manual hardware test matrix.

- [ ] **Step 1: Run the full Release test suite**

Run:

```powershell
dotnet test .\MechrevoLite.slnx -c Release -p:GITHUB_ACTIONS=true
```

Expected: zero failures. Report any intentionally skipped hardware integration test separately.

- [ ] **Step 2: Build the shipping project without signaling a running application**

Run:

```powershell
dotnet build .\src\MechrevoLiteWin\MechrevoLite.csproj -c Release -p:GITHUB_ACTIONS=true
```

Expected: zero errors and no new warnings.

- [ ] **Step 3: Inspect the final diff for scope and unsafe behavior**

Check that no official executable/registry archive is copied into `src`, no broad model-name mapping is introduced, and no new command is published from an `Unknown` capability.

- [ ] **Step 4: Perform manual hardware validation when machines are available**

| Representative machine | Verify |
| --- | --- |
| 40-series regular | Unsupported three-mode/GPU controls stay absent; keyboard/fan controls appear after GCU status. |
| 40-series three-mode | iGPU/direct GPU controls follow GCU status; no capability-panel flicker during refresh. |
| 50-series | RGB logo layout and supported GPU controls appear only after evidence; no unsupported OC/liquid-cooling write control is clickable. |

State clearly that these hardware checks remain pending until physical devices are used.

## Self-Review

### Spec coverage

- Explicit static presence, runtime precedence, range/direct-driver evidence: Tasks 2 and 3.
- Three supplied package facts and RGB layout evidence: Task 1.
- Common-page feature visibility and Unknown policy: Task 4.
- Custom-mode independent row visibility: Task 5.
- No-churn behavior, tests, build, and manual boundaries: Tasks 3 and 6.
- Existing GPU hot-switch safety gates: Tasks 2, 3, and 6.

### Placeholder scan

The plan contains no `TODO`, `TBD`, "implement later", or generic test placeholders. Every test task includes a concrete intended assertion and command.

### Type consistency

All later tasks consume `DeviceFeature`, `FeatureAvailability`, `FeatureEvidence`, `FeatureSupport`, and `DeviceCapabilitySnapshot` declared in Task 1. `GetStaticSupport` originates in Task 2; `GetFeatureSupport` and `DeviceCapabilities` originate in Task 3; form helpers are declared in their own tasks before use.
