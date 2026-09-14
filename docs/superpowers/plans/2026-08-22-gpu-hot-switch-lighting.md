# GPU Hot Switching and Lighting Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Support verified GCU iGPU hot switching, make dGPU application cleanup explicit and safe, restore temporary battery-suspended lighting, and show Custom mode as selected.

**Architecture:** Capability detection reads the same official registry fields as the OEM console and feeds a pure switch-route policy. The UI obtains a fresh hardware state, uses the policy to select direct, hot-switch, or reboot flow, and delegates application inspection to a narrow NVIDIA-backed coordinator. Lighting tracks a temporary keyboard power-off separately from the user's persistent setting.

**Tech Stack:** .NET 10 WinForms, MQTTnet/GCU MQTT protocol, Microsoft.Win32 registry APIs, NvAPIWrapper.Net, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-22-gpu-hot-switch-lighting-design.md`

## Global Constraints

- Use only the established `IGPU_ONLY_CONNECT_RB_*`, `DGPU_DIRECT_CONNECT_TOGGLE_*`, and `DGPU_DIRECT_CONNECT_RESTART` GCU actions; do not send `GPU_HOTSWAP_ON` or `GPU_HOTSWAP_OFF`.
- Treat a missing, malformed, or conflicting capability field as hot-switch unsupported.
- Derive a restart decision from fresh GCU state, never from persisted `gpu_mode` alone.
- Never terminate a process without an explicit in-UI confirmation for that exact operation; automatic forced cleanup remains off until separately confirmed.
- Exclude the current process, session-zero processes, inaccessible processes, and critical system process names from managed dGPU application candidates.
- Preserve an explicit keyboard-lighting-off user setting during AC restore and wake restore.
- Run all build/test commands with `-p:GITHUB_ACTIONS=true`; use isolated output/intermediate paths and never signal or overwrite the user's running `L-Mechrevo.exe`.
- Do not stage, commit, push, or delete files. The user did not authorize irreversible repository actions.

---

### Task 1: Official Capability Gate and Pure GPU Route Policy

**Files:**
- Create: `src/MechrevoLiteWin/Hardware/GpuSwitchPolicy.cs`
- Modify: `src/MechrevoLiteWin/Hardware/MechrevoDeviceCapabilities.cs:12-105,109-200`
- Modify: `src/MechrevoLiteWin/Hardware/MechrevoHw.cs:167-240`
- Test: `tests/MechrevoLite.Tests/DeviceCapabilityTests.cs`
- Test: `tests/MechrevoLite.Tests/GpuSwitchPolicyTests.cs`

**Interfaces:**
- Produces `MechrevoDeviceCapabilities.GpuHotSwap : bool`.
- Produces `MechrevoHw.SupportsGpuHotSwap : bool` and `MechrevoHw.IgpuSwitchBlocked : bool?`.
- Produces `GpuSwitchRoute` with `NoChange`, `Direct`, `HotSwitch`, and `Restart` values.
- Produces `GpuSwitchPolicy.Resolve(int reportedMode, int automaticRuntime, int targetMode, bool supportsHotSwap) : GpuSwitchPlan`.
- Consumed by `SettingsForm.SwitchGpuModeFromUi` and `GPUModeControl` in Task 4.

- [ ] **Step 1: Write failing capability and route-policy tests**

Add these tests before production code. The test names must document the official requirements and the stale-config regression boundary.

```csharp
[Fact]
public void OfficialHotSwapGate_RequiresEveryOfficialRegistryValue()
{
    var enabled = MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>
    {
        ["IsNvGpu"] = 1,
        ["iGPUModeOnlySupport"] = 1,
        ["APVersionCheck"] = 24,
        ["GpuHotSwapSwitchSupport"] = 1,
        ["lgpuHotSwapSwitchStatus"] = 1,
    });
    var blocked = MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>
    {
        ["IsNvGpu"] = 1,
        ["iGPUModeOnlySupport"] = 1,
        ["APVersionCheck"] = 23,
        ["GpuHotSwapSwitchSupport"] = 1,
        ["lgpuHotSwapSwitchStatus"] = 1,
    });

    Assert.True(enabled.GpuHotSwap);
    Assert.False(blocked.GpuHotSwap);
}

[Theory]
[InlineData("IsNvGpu")]
[InlineData("iGPUModeOnlySupport")]
[InlineData("GpuHotSwapSwitchSupport")]
[InlineData("lgpuHotSwapSwitchStatus")]
public void OfficialHotSwapGate_MissingRequiredFlagIsUnsupported(string missingKey)
{
    var values = new Dictionary<string, object?>
    {
        ["IsNvGpu"] = 1,
        ["iGPUModeOnlySupport"] = 1,
        ["APVersionCheck"] = 24,
        ["GpuHotSwapSwitchSupport"] = 1,
        ["lgpuHotSwapSwitchStatus"] = 1,
    };
    values.Remove(missingKey);

    Assert.False(MechrevoDeviceCapabilities.FromValues(values).GpuHotSwap);
}

[Theory]
[InlineData(MechrevoService.GpuStandard, 1, MechrevoService.GpuIGpu, true, GpuSwitchRoute.HotSwitch)]
[InlineData(MechrevoService.GpuStandard, 1, MechrevoService.GpuIGpu, false, GpuSwitchRoute.Restart)]
[InlineData(MechrevoService.GpuAuto, 1, MechrevoService.GpuIGpu, true, GpuSwitchRoute.HotSwitch)]
[InlineData(MechrevoService.GpuDgpu, -1, MechrevoService.GpuIGpu, true, GpuSwitchRoute.Restart)]
public void GpuRoute_UsesFreshRuntimeState(
    int reportedMode, int automaticRuntime, int targetMode, bool supported, GpuSwitchRoute expected)
{
    Assert.Equal(expected,
        GpuSwitchPolicy.Resolve(reportedMode, automaticRuntime, targetMode, supported).Route);
}
```

Add a `MechrevoHw` status-parser test with `IGpuCannotBeSwitchNowVisibility` values `"false"` and `"true"`, asserting `false` and `true` respectively when direct mode is off.

- [ ] **Step 2: Run the new tests and verify the red state**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release -p:GITHUB_ACTIONS=true '-p:OutputPath=artifacts\gpu-hot-switch-tests\$(MSBuildProjectName)\bin\' '-p:IntermediateOutputPath=artifacts\gpu-hot-switch-tests\obj\$(MSBuildProjectName)\' --filter 'FullyQualifiedName~OfficialHotSwapGate|FullyQualifiedName~GpuRoute_UsesFreshRuntimeState|FullyQualifiedName~IgpuSwitchBlocked'
```

Expected: compile failure because `GpuHotSwap`, `GpuSwitchPolicy`, `GpuSwitchRoute`, or `IgpuSwitchBlocked` does not yet exist.

- [ ] **Step 3: Implement the official registry gate and route policy**

Add matching `GpuConfigPaths` for all existing OEM roots and merge their values after `ItemSupportPaths`, retaining canonical-first `TryAdd` behavior. Add this property and initializer condition:

```csharp
public bool GpuHotSwap { get; init; }

GpuHotSwap = Flag("IsNvGpu", "IsNvidiaGpu") &&
    Flag("iGPUModeOnlySupport", "IGpuOnlyModeSupport", "IntegratedGpuOnlySupport") &&
    Number("APVersionCheck") > 23 &&
    Flag("GpuHotSwapSwitchSupport") &&
    Flag("lgpuHotSwapSwitchStatus"),
```

Add `SupportsGpuHotSwap` in `MechrevoHw` as `Capabilities.GpuHotSwap && SupportsIgpuOnly`. Parse `IGpuCannotBeSwitchNowVisibility` from `Setting/Status` exactly like the OEM console: it is blocked when direct mode is not active and the nonempty value does not contain `false` case-insensitively.

Create `GpuSwitchPolicy.cs` with this public-to-assembly contract:

```csharp
namespace MechrevoLite.Hardware;

internal enum GpuSwitchRoute { NoChange, Direct, HotSwitch, Restart }

internal readonly record struct GpuSwitchPlan(
    GpuSwitchRoute Route,
    bool RequiresDgpuProcessPreflight);

internal static class GpuSwitchPolicy
{
    internal static GpuSwitchPlan Resolve(
        int reportedMode, int automaticRuntime, int targetMode, bool supportsHotSwap)
    {
        int currentMode = reportedMode == MechrevoService.GpuAuto
            ? automaticRuntime == 2 ? MechrevoService.GpuIGpu
            : automaticRuntime == 1 ? MechrevoService.GpuStandard
            : -1
            : reportedMode;
        if (currentMode == targetMode)
            return new(GpuSwitchRoute.NoChange, false);
        if (currentMode < MechrevoService.GpuIGpu ||
            targetMode == MechrevoService.GpuDgpu ||
            currentMode == MechrevoService.GpuDgpu ||
            (currentMode == MechrevoService.GpuStandard &&
             targetMode == MechrevoService.GpuIGpu && !supportsHotSwap))
            return new(GpuSwitchRoute.Restart, false);
        bool hotSwitch = currentMode == MechrevoService.GpuStandard &&
            targetMode == MechrevoService.GpuIGpu && supportsHotSwap;
        return new(hotSwitch ? GpuSwitchRoute.HotSwitch : GpuSwitchRoute.Direct, hotSwitch);
    }
}
```

- [ ] **Step 4: Run the focused tests and verify green state**

Run the Step 2 command again.

Expected: all selected tests pass; AP version 23, each missing required flag, and disabled hot-switch status remain unsupported.

- [ ] **Step 5: Review the local change without committing**

Run:

```powershell
Get-Content -LiteralPath 'src\MechrevoLiteWin\Hardware\GpuSwitchPolicy.cs'
Select-String -LiteralPath 'src\MechrevoLiteWin\Hardware\MechrevoDeviceCapabilities.cs' -Pattern 'GpuHotSwap|GpuConfigPaths'
```

Expected: no model-name or RTX-generation conditional appears in the capability gate.

### Task 2: GCU Transition Confirmation and Vendor Reboot Route

**Files:**
- Modify: `src/MechrevoLiteWin/Hardware/MechrevoService.cs:625-778`
- Test: `tests/MechrevoLite.Tests/DeviceCapabilityTests.cs`

**Interfaces:**
- Consumes `MechrevoHw.SupportsGpuHotSwap`, `MechrevoHw.IgpuSwitchBlocked`, and `GpuSwitchPlan` from Task 1.
- Produces `MechrevoService.RequestGpuModeRestartAsync(int mode, CancellationToken cancellationToken = default) : Task<bool>`.
- Preserves `MechrevoService.SwitchGpuMode(int mode, bool autoRestart = false, bool? pluggedForAuto = null) : Task<bool>` for existing callers.
- Consumed by `SettingsForm` and `GPUModeControl` in Task 4.

- [ ] **Step 1: Write failing GCU transition tests**

Add a test that uses the existing `MechrevoHw` publish override to record actions. It must assert that restart preparation publishes the requested target action before `DGPU_DIRECT_CONNECT_RESTART` and never invokes an OS process restart.

```csharp
[Fact]
public async Task RestartGpuModeRoute_PublishesTargetThenVendorRestart()
{
    var actions = new List<string>();
    using var hardware = new MechrevoHw((_, payload) =>
    {
        actions.Add(((IDictionary<string, object>)payload)["Action"].ToString()!);
        return Task.CompletedTask;
    }, new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true, DgpuDirect = true });
    var service = new MechrevoService(hardware);

    Assert.True(await service.RequestGpuModeRestartAsync(MechrevoService.GpuIGpu));
    Assert.Equal("IGPU_ONLY_CONNECT_RB_ON", actions[0]);
    Assert.Equal("DGPU_DIRECT_CONNECT_RESTART", actions[^1]);
}
```

Add a hot-switch test whose fake GCU status reaches `CheckDGpuStatusforIGpuOnlyOnSuccess = "2"` after a retry and asserts that `SwitchGpuMode(GpuIGpu)` succeeds only after readback.

- [ ] **Step 2: Run the new transition tests and verify the red state**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release -p:GITHUB_ACTIONS=true '-p:OutputPath=artifacts\gpu-hot-switch-tests\$(MSBuildProjectName)\bin\' '-p:IntermediateOutputPath=artifacts\gpu-hot-switch-tests\obj\$(MSBuildProjectName)\' --filter 'FullyQualifiedName~RestartGpuModeRoute|FullyQualifiedName~HotSwitch'
```

Expected: compile failure because `RequestGpuModeRestartAsync` does not exist.

- [ ] **Step 3: Refactor GCU publishing and add bounded official polling**

Extract the common target-command sequence from `SwitchGpuMode` into a private method that preserves these order-sensitive operations:

```csharp
// Targeting direct mode first disables iGPU-only; leaving direct adjusts the MUX layer.
if (mode == GpuDgpu)
    await _hw.Publish("Setting/Control", CreateGpuModePayload(GpuStandard));
await _hw.Publish("Setting/Control", CreateGpuModePayload(mode));
if (leavingDirect)
    await _hw.Publish("Setting/Control", new Dictionary<string, object>
    {
        ["Action"] = mode == GpuIGpu
            ? "DGPU_DIRECT_CONNECT_TOGGLE_IGPU"
            : "DGPU_DIRECT_CONNECT_TOGGLE_OFF",
    });
```

For an iGPU hot-switch attempt on `SupportsGpuHotSwap`, use the official bounded policy: poll GCU state every two seconds, retry the target command every fourth poll, and stop after 61 polls. A confirmed iGPU target requires both the reported mode and `GpuSwitchResult == 2` when that result is present. Preserve the shorter existing confirmation loop for non-hot routes.

Implement `RequestGpuModeRestartAsync` with the same serialized switch lock and target-command sequence, then wait 800 milliseconds, call `AppConfig.Flush()`, and publish `DGPU_DIRECT_CONNECT_RESTART`. Do not call `shutdown.exe`; the GCU service owns this restart command. Return `false` on disconnected hardware, unsupported target, cancellation, or a publish failure.

- [ ] **Step 4: Run focused transition tests and existing GPU-mode tests**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release -p:GITHUB_ACTIONS=true '-p:OutputPath=artifacts\gpu-hot-switch-tests\$(MSBuildProjectName)\bin\' '-p:IntermediateOutputPath=artifacts\gpu-hot-switch-tests\obj\$(MSBuildProjectName)\' --filter 'FullyQualifiedName~GpuMode|FullyQualifiedName~RestartGpuModeRoute|FullyQualifiedName~RefreshGpuModeStatus'
```

Expected: direct, standard, iGPU, and automatic command tests still pass; the restart test records no OS restart path.

- [ ] **Step 5: Review restart behavior without committing**

Run:

```powershell
Select-String -LiteralPath 'src\MechrevoLiteWin\Hardware\MechrevoService.cs' -Pattern 'DGPU_DIRECT_CONNECT_RESTART|shutdown|GPU_HOTSWAP' -Context 2,3
```

Expected: the new service path uses the vendor restart action and contains no undocumented hot-swap action.

### Task 3: Safe dGPU Application Discovery and Cleanup Coordinator

**Files:**
- Create: `src/MechrevoLiteWin/Gpu/NVidia/DgpuApplicationCoordinator.cs`
- Modify: `src/MechrevoLiteWin/Gpu/NVidia/NvidiaGpuControl.cs:140-190,399-411`
- Test: `tests/MechrevoLite.Tests/DgpuApplicationCoordinatorTests.cs`

**Interfaces:**
- Produces `DgpuApplication(int ProcessId, string ProcessName, int SessionId, bool HasMainWindow)`.
- Produces `DgpuApplicationCoordinator.Snapshot() : IReadOnlyList<DgpuApplication>`.
- Produces `DgpuApplicationCoordinator.RequestGracefulCloseAsync(IReadOnlyList<DgpuApplication>) : Task<IReadOnlyList<DgpuApplication>>`.
- Produces `DgpuApplicationCoordinator.ForceCloseAsync(IReadOnlyList<DgpuApplication>) : Task<IReadOnlyList<DgpuApplication>>`.
- Consumed by manual and automatic iGPU switching in Task 4.

- [ ] **Step 1: Write failing process-safety tests**

Use only pure candidate data in tests; do not create or kill real processes. Define a safety predicate and test the exclusion boundary.

```csharp
[Theory]
[InlineData(1234, "game", 1, 999, true)]
[InlineData(999, "game", 1, 999, false)]
[InlineData(1234, "game", 0, 999, false)]
[InlineData(1234, "csrss", 1, 999, false)]
[InlineData(1234, "nvcontainer", 1, 999, false)]
public void CandidateSafety_ExcludesCurrentSystemAndServiceProcesses(
    int pid, string name, int session, int currentPid, bool expected)
{
    Assert.Equal(expected,
        DgpuApplicationCoordinator.IsSafeCandidate(new(pid, name, session, true), currentPid));
}
```

Add a test that `FilterSafeCandidates` retains only safe data and preserves each candidate's PID, name, session, and window flag.

- [ ] **Step 2: Run the new safety tests and verify the red state**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release -p:GITHUB_ACTIONS=true '-p:OutputPath=artifacts\gpu-hot-switch-tests\$(MSBuildProjectName)\bin\' '-p:IntermediateOutputPath=artifacts\gpu-hot-switch-tests\obj\$(MSBuildProjectName)\' --filter 'FullyQualifiedName~CandidateSafety|FullyQualifiedName~FilterSafeCandidates'
```

Expected: compile failure because `DgpuApplicationCoordinator` does not exist.

- [ ] **Step 3: Implement narrow discovery and two-stage cleanup**

Move the existing critical-process name set from `NvidiaGpuControl.KillGPUApps` into the coordinator as a case-insensitive blocklist. Add a read-only NVIDIA method that gets the laptop `PhysicalGPU`, calls `GetActiveApplications()`, snapshots only safe candidates, and disposes every returned `Process` after extracting its fields.

Implement the coordinator with these rules:

```csharp
internal static bool IsSafeCandidate(DgpuApplication app, int currentPid) =>
    app.ProcessId > 0 && app.ProcessId != currentPid && app.SessionId > 0 &&
    !CriticalProcessNames.Contains(app.ProcessName);
```

`RequestGracefulCloseAsync` must reacquire every PID, verify process name and session still match its snapshot, call `CloseMainWindow()` only when `HasMainWindow` is true, wait 800 milliseconds, then return a fresh safe snapshot of still-active dGPU applications. `ForceCloseAsync` must repeat the identity check before `Kill(entireProcessTree: true)`, wait for exit up to 1500 milliseconds, and return the remaining snapshot. Access failures must be logged and returned as remaining candidates; no catch block may convert an unknown candidate into a successful close.

Keep the legacy `NvidiaGpuControl.KillGPUApps` untouched for non-Mechrevo legacy callers. New Mechrevo hot switching must use the coordinator, not `HardwareControl.KillGPUApps()`.

- [ ] **Step 4: Run process-safety tests and compile the project**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release -p:GITHUB_ACTIONS=true '-p:OutputPath=artifacts\gpu-hot-switch-tests\$(MSBuildProjectName)\bin\' '-p:IntermediateOutputPath=artifacts\gpu-hot-switch-tests\obj\$(MSBuildProjectName)\' --filter 'FullyQualifiedName~DgpuApplicationCoordinator'
dotnet build .\src\MechrevoLiteWin\MechrevoLite.csproj -c Release -p:GITHUB_ACTIONS=true '-p:OutputPath=artifacts\gpu-hot-switch-build\$(MSBuildProjectName)\bin\' '-p:IntermediateOutputPath=artifacts\gpu-hot-switch-build\obj\$(MSBuildProjectName)\'
```

Expected: safety tests pass and the production project compiles without terminating any live process.

- [ ] **Step 5: Review the force-close boundary without committing**

Run:

```powershell
Select-String -LiteralPath 'src\MechrevoLiteWin\Gpu\NVidia\DgpuApplicationCoordinator.cs' -Pattern 'Kill\(|CloseMainWindow|SessionId|CriticalProcessNames' -Context 2,4
```

Expected: each kill site is preceded by a safe-candidate and snapshot-identity check.

### Task 4: Manual and Automatic Hot-Switch UI Integration

**Files:**
- Modify: `src/MechrevoLiteWin/Settings.cs:1022-1250,1520-1665,3382-3512,3735-3845`
- Modify: `src/MechrevoLiteWin/Gpu/GPUModeControl.cs:293-350`
- Test: `tests/MechrevoLite.Tests/GpuSwitchPolicyTests.cs`
- Test: `tests/MechrevoLite.Tests/DeviceCapabilityTests.cs`

**Interfaces:**
- Consumes `GpuSwitchPolicy.Resolve`, `MechrevoService.RequestGpuModeRestartAsync`, and `DgpuApplicationCoordinator` from Tasks 1-3.
- Produces `GPUModeControl.TryApplyAutomaticHotSwitchAsync(bool plugged) : Task<bool>`.
- Persists `gpu_auto_hot_switch` and `gpu_auto_force_dgpu_close` as `0` by default when unset.
- Retains `gpu_auto` as the user-visible Auto mode selection.

- [ ] **Step 1: Write failing UI-policy tests**

Add pure policy coverage for app-managed automatic switching. The predicate must only select app-managed switching when both user opt-in and hardware capability are true.

```csharp
[Theory]
[InlineData(false, true, false)]
[InlineData(true, false, false)]
[InlineData(true, true, true)]
public void AutomaticHotSwitch_RequiresExplicitOptInAndCapability(
    bool optionEnabled, bool capabilityEnabled, bool expected)
{
    Assert.Equal(expected,
        GpuSwitchPolicy.ShouldUseAppManagedAutomaticSwitch(optionEnabled, capabilityEnabled));
}
```

Add a route test proving that a stale configured direct-GPU value cannot turn a fresh mixed-to-iGPU hot-switch route into `Restart`; the policy API must not accept `gpu_mode` configuration as an input.

- [ ] **Step 2: Run the UI-policy tests and verify the red state**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release -p:GITHUB_ACTIONS=true '-p:OutputPath=artifacts\gpu-hot-switch-tests\$(MSBuildProjectName)\bin\' '-p:IntermediateOutputPath=artifacts\gpu-hot-switch-tests\obj\$(MSBuildProjectName)\' --filter 'FullyQualifiedName~AutomaticHotSwitch|FullyQualifiedName~GpuRoute'
```

Expected: compile failure because `ShouldUseAppManagedAutomaticSwitch` does not exist.

- [ ] **Step 3: Add compact, capability-gated controls and manual flow**

In `BuildDashboardLayout`, create one `FlowLayoutPanel` named `gpuHotSwitchOptions` with two `RCheckBox` controls:

```csharp
// Visible only when Program.hw?.SupportsGpuHotSwap is true.
autoHotSwitchBox.Text = "自动模式离电切核显";
forceDgpuCloseBox.Text = "允许关闭独显程序";
```

Place it below `tableGPU`, use `AutoSize = true`, `WrapContents = true`, and increase `panelGPU.Height` only while the option panel is visible. Attach tooltips explaining that the first option applies only in Auto mode and the second can lose unsaved work. Persist both choices with `AppConfig.Set`; enabling the force-close option requires a `MessageBoxButtons.YesNo` warning and reverts the checkbox/configuration on `No`.

Refactor `SwitchGpuModeFromUi` into these explicit terminal paths:

1. Call `RefreshGpuModeStatus()` and calculate `GpuSwitchPlan` from `Program.service.CurrentGpuMode`, `Program.hw.GpuSwitchResult`, target, and `Program.hw.SupportsGpuHotSwap`. If refresh returns `false`, show a readback-unavailable message and return without calculating a plan from stale state.
2. On `NoChange`, refresh visible state and context menu without writing speculative state.
3. On `HotSwitch`, call `DgpuApplicationCoordinator.Snapshot()`. With no candidates, call `SwitchGpuMode(target)`. With candidates, show their names and PID values in a three-choice `YesNoCancel` prompt: Yes requests graceful close, No selects reboot switch, Cancel refreshes state and returns.
4. After graceful close, if candidates remain, show the remaining list in a second `YesNo` prompt. Yes calls `ForceCloseAsync`; No refreshes and returns. If candidates remain after force-close, show a blocker message and offer reboot switch rather than pretending the hot switch completed.
5. If `SwitchGpuMode(target)` returns `false` for a `HotSwitch` route, refresh status, show the GCU blocked state when reported, and offer the same reboot prompt. Do not mark the target as active until hardware readback confirms it.
6. On `Restart`, call `ShowGpuRestartPromptAsync(target)` before writing a confirmed target visual state. On Yes, save the requested `gpu_mode`, set `gpu_auto` to `0`, mark restart pending, flush, then call `RequestGpuModeRestartAsync(target)`. If that call fails, restore the prior configuration and refresh hardware state. On No, call `RefreshGpuModeAfterRestartPromptAsync()`. Remove its direct `shutdown.exe` invocation.
7. On successful direct or hot route, update `gpu_mode`/`gpu_auto`, then call `RefreshGpuModeStatus()`, `VisualiseGPUMode`, and `SetContextMenu`. On every cancellation, failure, timeout, or blocked GCU status, call the same refresh functions before leaving the method.

Add `GpuSwitchPolicy.ShouldUseAppManagedAutomaticSwitch(bool optionEnabled, bool capabilityEnabled) => optionEnabled && capabilityEnabled`.

In `GPUModeControl`, add this method:

```csharp
internal async Task<bool> TryApplyAutomaticHotSwitchAsync(bool plugged)
{
    if (Program.hw is not { IsConnected: true } || Program.service is null)
        return false;
    if (!GpuSwitchPolicy.ShouldUseAppManagedAutomaticSwitch(
            AppConfig.Is("gpu_auto_hot_switch"), Program.hw.SupportsGpuHotSwap))
        return await Program.service.SwitchAutomaticGpuMode(plugged);
    if (plugged)
        return await Program.service.SwitchGpuMode(MechrevoService.GpuStandard);
    IReadOnlyList<DgpuApplication> apps = DgpuApplicationCoordinator.Snapshot();
    if (apps.Count > 0 && !AppConfig.Is("gpu_auto_force_dgpu_close"))
    {
        // Notify through the form/tray and leave hardware unchanged.
        return false;
    }
    if (apps.Count > 0)
        apps = await DgpuApplicationCoordinator.ForceCloseAsync(apps);
    return apps.Count == 0 && await Program.service.SwitchGpuMode(MechrevoService.GpuIGpu);
}
```

Use `TryApplyAutomaticHotSwitchAsync` in the power-change branch only when `gpu_auto` is selected. When the option is off, retain the existing GCU Auto path unchanged.

- [ ] **Step 4: Run focused tests and compile the WinForms UI**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release -p:GITHUB_ACTIONS=true '-p:OutputPath=artifacts\gpu-hot-switch-tests\$(MSBuildProjectName)\bin\' '-p:IntermediateOutputPath=artifacts\gpu-hot-switch-tests\obj\$(MSBuildProjectName)\' --filter 'FullyQualifiedName~GpuSwitchPolicy|FullyQualifiedName~GpuMode'
dotnet build .\src\MechrevoLiteWin\MechrevoLite.csproj -c Release -p:GITHUB_ACTIONS=true '-p:OutputPath=artifacts\gpu-hot-switch-build\$(MSBuildProjectName)\bin\' '-p:IntermediateOutputPath=artifacts\gpu-hot-switch-build\obj\$(MSBuildProjectName)\'
```

Expected: route tests pass, the UI compiles, and no process cleanup occurs during tests.

- [ ] **Step 5: Review layout and cancellation paths without committing**

Run:

```powershell
Select-String -LiteralPath 'src\MechrevoLiteWin\Settings.cs' -Pattern 'gpuHotSwitchOptions|gpu_auto_hot_switch|gpu_auto_force_dgpu_close|RequestGpuModeRestartAsync|shutdown' -Context 2,5
```

Expected: optional controls are capability-gated, cancel paths refresh hardware state, and no `shutdown` call remains in the GCU restart dialog.

### Task 5: Custom Selected State and Temporary Lighting Restoration

**Files:**
- Modify: `src/MechrevoLiteWin/Settings.cs:3645-3673`
- Modify: `src/MechrevoLiteWin/Hardware/LightingState.cs`
- Modify: `src/MechrevoLiteWin/Program.cs:953-1115`
- Test: `tests/MechrevoLite.Tests/RuntimeRegressionTests.cs`
- Test: `tests/MechrevoLite.Tests/LightingStateTests.cs`

**Interfaces:**
- Produces `SettingsForm.IsCustomVisualMode(int mode) : bool` for visual-state testing.
- Produces `LightingState.ShouldRestoreKeyboardPower(bool userWantsPower, bool temporaryPowerOff, bool cachedPowerOn) : bool`.
- Adds a private temporary keyboard-power marker in `Program` that is set only after a successful temporary `SetKeyboardPower(false)` write and cleared only after a successful restoration.

- [ ] **Step 1: Write failing custom-mode and lighting tests**

Add the following tests:

```csharp
[Theory]
[InlineData(AsusACPI.PerformanceManual, true)]
[InlineData(AsusACPI.PerformanceTurbo, false)]
[InlineData(MechrevoService.ModeCustom, false)]
public void CustomVisualState_UsesTheVisualManualEnum(int mode, bool expected) =>
    Assert.Equal(expected, SettingsForm.IsCustomVisualMode(mode));

[Theory]
[InlineData(true, true, true, true)]
[InlineData(true, false, false, true)]
[InlineData(true, false, true, false)]
[InlineData(false, true, false, false)]
public void TemporaryKeyboardPowerRestore_RespectsUserIntentAndStaleReadback(
    bool userWantsPower, bool temporaryPowerOff, bool cachedPowerOn, bool expected) =>
    Assert.Equal(expected, LightingState.ShouldRestoreKeyboardPower(
        userWantsPower, temporaryPowerOff, cachedPowerOn));
```

The `ModeCustom` assertion intentionally proves that service enum `3` is not the visual enum used by the button state; `ToVisualMode(ModeCustom)` is `PerformanceManual`.

- [ ] **Step 2: Run the tests and verify the red state**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release -p:GITHUB_ACTIONS=true '-p:OutputPath=artifacts\gpu-hot-switch-tests\$(MSBuildProjectName)\bin\' '-p:IntermediateOutputPath=artifacts\gpu-hot-switch-tests\obj\$(MSBuildProjectName)\' --filter 'FullyQualifiedName~CustomVisualState|FullyQualifiedName~TemporaryKeyboardPowerRestore'
```

Expected: compile failure because the two helpers do not yet exist.

- [ ] **Step 3: Implement the minimal state corrections**

In `SettingsForm.VisualiseMode`, replace the incorrect `case MechrevoService.ModeCustom` selected-state branch with a check against `AsusACPI.PerformanceManual` through this helper:

```csharp
internal static bool IsCustomVisualMode(int mode) => mode == AsusACPI.PerformanceManual;
```

In `LightingState`, add:

```csharp
internal static bool ShouldRestoreKeyboardPower(
    bool userWantsPower, bool temporaryPowerOff, bool cachedPowerOn) =>
    userWantsPower && (temporaryPowerOff || !cachedPowerOn);
```

In `Program`, add an interlocked private marker such as `_keyboardPowerTemporarilySuspended`. Set it only when the temporary suspend path successfully sends `SetKeyboardPower(false)` while `rgb.KbPowerOn` is true. In `RestoreKeyboardLightingAsync`, load and retain the marker until all necessary steps succeed. If `ShouldRestoreKeyboardPower` returns true, send `SetKeyboardPower(true)` and wait 120 milliseconds before firmware-effect or HID-effect restore. Force HID `StartMode` after temporary suspension. Clear the marker only after the keyboard restore succeeds.

In `ReconcileLightingPowerAsync`, do not clear external-light snapshots unconditionally. Clear each temporary snapshot only after its corresponding restoration reports success, so a transient reconnect error remains eligible for retry.

- [ ] **Step 4: Run focused lighting and runtime regression tests**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release -p:GITHUB_ACTIONS=true '-p:OutputPath=artifacts\gpu-hot-switch-tests\$(MSBuildProjectName)\bin\' '-p:IntermediateOutputPath=artifacts\gpu-hot-switch-tests\obj\$(MSBuildProjectName)\' --filter 'FullyQualifiedName~RuntimeRegressionTests|FullyQualifiedName~LightingStateTests|FullyQualifiedName~KeyboardRgbCompatibilityTests'
```

Expected: the Custom visual mapping and lighting policy pass alongside existing keyboard renderer restart tests.

- [ ] **Step 5: Review lighting state ownership without committing**

Run:

```powershell
Select-String -LiteralPath 'src\MechrevoLiteWin\Program.cs' -Pattern 'keyboardPowerTemporarily|SetKeyboardPower\(true\)|SetKeyboardPower\(false\)|lightbarPowerBeforeTemporary|logoPowerBeforeTemporary' -Context 2,4
```

Expected: a temporary marker cannot override `rgb.KbPowerOn == false` and persists through a failed restore.

### Task 6: End-to-End Verification and Hardware Test Handoff

**Files:**
- Modify: no production files unless a verification failure identifies a scoped regression.
- Test: `tests/MechrevoLite.Tests/DeviceCapabilityTests.cs`
- Test: `tests/MechrevoLite.Tests/GpuSwitchPolicyTests.cs`
- Test: `tests/MechrevoLite.Tests/DgpuApplicationCoordinatorTests.cs`
- Test: `tests/MechrevoLite.Tests/RuntimeRegressionTests.cs`
- Test: `tests/MechrevoLite.Tests/LightingStateTests.cs`

**Interfaces:**
- Verifies the public behavior created in Tasks 1-5 without changing protocol scope.

- [ ] **Step 1: Run the complete automated suite in isolated outputs**

Run:

```powershell
dotnet test .\MechrevoLite.slnx -c Release -p:GITHUB_ACTIONS=true '-p:OutputPath=artifacts\gpu-hot-switch-tests\$(MSBuildProjectName)\bin\' '-p:IntermediateOutputPath=artifacts\gpu-hot-switch-tests\obj\$(MSBuildProjectName)\'
```

Expected: all tests pass. Any failure is investigated before further changes; do not weaken assertions to make the run pass.

- [ ] **Step 2: Build the production application to an isolated directory**

Run:

```powershell
dotnet build .\src\MechrevoLiteWin\MechrevoLite.csproj -c Release -p:GITHUB_ACTIONS=true '-p:OutputPath=artifacts\gpu-hot-switch-build\$(MSBuildProjectName)\bin\' '-p:IntermediateOutputPath=artifacts\gpu-hot-switch-build\obj\$(MSBuildProjectName)\'
```

Expected: zero compilation errors. Warnings are recorded but not hidden.

- [ ] **Step 3: Perform non-destructive manual hardware scenarios**

Use a supported hot-switch machine with no important unsaved work and record GCU logs/state after each scenario:

```text
1. Mixed/standard -> iGPU with no dGPU application: no reboot, readback shows iGPU.
2. Mixed/standard -> iGPU with a known dGPU application: Cancel leaves mode unchanged.
3. Same application case: graceful-close prompt shows remaining list before force-close.
4. Hot-switch timeout or blocked status: actual state refreshes and reboot option is offered.
5. Unsupported capability profile: no hot-switch option/control appears; restart flow remains.
6. Auto mode + hot-switch option on battery: active dGPU app blocks automatic action unless force option was explicitly enabled.
7. Battery lighting suspension then AC/wake: saved animated effect restores.
8. Explicitly disabled keyboard lighting then AC/wake: lighting remains off.
9. Custom performance mode: Custom button shows the activated marker immediately and after GCU mode readback.
```

- [ ] **Step 4: Inspect the final code for prohibited behavior**

Run:

```powershell
Select-String -Path 'src\MechrevoLiteWin\*.cs','src\MechrevoLiteWin\**\*.cs' -Pattern 'GPU_HOTSWAP_ON|GPU_HOTSWAP_OFF|shutdown", "/r|KillGPUApps\(\)' -CaseSensitive:$false -ErrorAction SilentlyContinue
```

Expected: no new undocumented hot-swap command, no new unconditional process cleanup in the Mechrevo hot-switch path, and no duplicate OS restart in the GCU restart route.

- [ ] **Step 5: Report verification boundaries without committing**

Report automated results, build result, modified files, and which manual hardware scenarios still require physical-machine confirmation. Do not claim universal machine compatibility from test doubles or a single device.
