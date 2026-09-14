# GPU Overclock Physical Application Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply custom NVIDIA GPU offsets through a verified driver path instead of treating GCU MQTT state as physical success.

**Architecture:** The standard process remains unelevated and asks an elevated, per-user named-pipe helper to perform NVAPI writes only when local driver writes are denied. `MechrevoService` separates profile persistence from physical application and accepts a GPU offset only after driver readback equals the request.

**Tech Stack:** .NET 10 Windows Forms, `NvAPIWrapper.Net`, `System.IO.Pipes`, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-17-gpu-overclock-physical-application-design.md`

## Global Constraints

- Keep the application manifest `asInvoker`.
- Do not treat MQTT `Fan/Status` values as proof of physical GPU overclocking.
- Validate every offset against the NVIDIA driver range before writing.
- The helper pipe must use a random name and `PipeOptions.CurrentUserOnly`.
- No source-control commit is part of this workspace task.

---

### Task 1: Replace the false-success contract

**Files:**
- Modify: `tests/MechrevoLite.Tests/GpuOverclockFallbackTests.cs`
- Modify: `src/MechrevoLiteWin/Hardware/MechrevoService.cs`

**Interfaces:**
- Consumes: `MechrevoService.SetCustomDetail(Dictionary<string, string>)`
- Produces: GPU field confirmation that requires a driver readback.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task GcuStatusEcho_DoesNotConfirmPhysicalGpuOffset()
{
    // A rejected fake driver stays at zero while GCU echoes +500.
    Assert.False(await service.SetCustomDetail(new()
    {
        ["GpuCoreClockOffsetOC"] = "500"
    }));
}
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run: `dotnet test .\MechrevoLite.slnx -c Release --no-restore -p:GITHUB_ACTIONS=true --filter FullyQualifiedName~GpuOverclockFallbackTests`

Expected: the existing GCU fallback returns true, so the new assertion fails.

- [ ] **Step 3: Add physical-readback confirmation**

```csharp
bool gpuConfirmed = gpuEntries.All(field =>
    _hw.DriverGpuOverclockFieldMatches(field.Key, int.Parse(field.Value)));
confirmed = nonGpuConfirmed && gpuConfirmed;
```

GCU publication remains profile metadata only; failure must preserve actual
driver readback rather than selecting the GCU backend.

- [ ] **Step 4: Run the focused test and verify it passes**

Run: `dotnet test .\MechrevoLite.slnx -c Release --no-restore -p:GITHUB_ACTIONS=true --filter FullyQualifiedName~GpuOverclockFallbackTests`

Expected: status echoes without driver movement fail.

### Task 2: Add an elevated, bounded GPU helper

**Files:**
- Create: `src/MechrevoLiteWin/Gpu/NVidia/ElevatedGpuOverclockHelper.cs`
- Modify: `src/MechrevoLiteWin/Program.cs`
- Modify: `src/MechrevoLiteWin/Hardware/MechrevoHw.cs`
- Test: `tests/MechrevoLite.Tests/GpuOverclockFallbackTests.cs`

**Interfaces:**
- Consumes: `IGpuOverclockControl`, `ProcessHelper.IsUserAdministrator()`.
- Produces: `Task<GpuOverclockApplyResult> ApplyAsync(GpuOverclockApplyRequest, CancellationToken)`.

- [ ] **Step 1: Write the failing helper-result test**

```csharp
[Fact]
public async Task ElevatedResult_WithMatchingDriverReadback_ConfirmsGpuOffset()
{
    var result = await service.SetCustomDetail(new()
    {
        ["GpuCoreClockOffsetOC"] = "500"
    });
    Assert.True(result);
    Assert.Equal(500, driver.CoreOffset.Current);
}
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run: `dotnet test .\MechrevoLite.slnx -c Release --no-restore -p:GITHUB_ACTIONS=true --filter FullyQualifiedName~ElevatedResult`

Expected: no helper-backed apply path exists.

- [ ] **Step 3: Implement the helper protocol and action**

```csharp
if (action == "--gpu-oc-helper")
{
    Environment.ExitCode = ElevatedGpuOverclockHelper.Run(args);
    Logger.Close();
    return;
}
```

Use `NamedPipeServerStream` with `PipeOptions.CurrentUserOnly`; validate
driver range, write with `NvidiaGpuControl`, refresh, and return only the
actual offsets. The normal client starts it with `Verb = "runas"` only after
an access-denied direct write.

- [ ] **Step 4: Run the focused tests and verify they pass**

Run: `dotnet test .\MechrevoLite.slnx -c Release --no-restore -p:GITHUB_ACTIONS=true --filter FullyQualifiedName~GpuOverclockFallbackTests`

Expected: helper readback confirms matching values; mismatched values fail.

### Task 3: Preserve readback state and expose genuine driver limits

**Files:**
- Modify: `src/MechrevoLiteWin/Gpu/NVidia/NvidiaGpuControl.cs`
- Modify: `src/MechrevoLiteWin/Hardware/MechrevoHw.cs`
- Modify: `src/MechrevoLiteWin/CustomModeForm.cs`
- Test: `tests/MechrevoLite.Tests/GpuOverclockFallbackTests.cs`

**Interfaces:**
- Consumes: `GpuClockOffsetRange` from an NVAPI refresh.
- Produces: physical readback for display and memory-offset ranges bounded by the actual driver range.

- [ ] **Step 1: Write the failing range/readback tests**

```csharp
Assert.Equal(1000, hardware.GpuMemoryOffsetUserMaximum);
Assert.Equal(500, hardware.EffectiveGpuCoreClockOffset);
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run: `dotnet test .\MechrevoLite.slnx -c Release --no-restore -p:GITHUB_ACTIONS=true --filter FullyQualifiedName~GpuOverclockFallbackTests`

Expected: memory is capped at 500 and the current effective state can come from GCU cache.

- [ ] **Step 3: Implement read-only refresh and range selection**

```csharp
public bool IsWritable => !_writeAccessDenied && IsValid && HasEditableRange;
public bool Refresh() => TryReadOffsets(out CoreOffset, out MemoryOffset);
```

Keep read capability after a write denial so the custom page displays the
actual driver values. Limit core to +/-500 MHz and memory only to the
driver-reported minimum/maximum.

- [ ] **Step 4: Run the focused tests and verify they pass**

Run: `dotnet test .\MechrevoLite.slnx -c Release --no-restore -p:GITHUB_ACTIONS=true --filter FullyQualifiedName~GpuOverclockFallbackTests`

Expected: a +1000 MHz driver-supported memory range is visible; readback
controls the display.

### Task 4: Validate the end-to-end contract and release metadata

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `更新日志.txt`
- Modify: `src/MechrevoLiteWin/MechrevoLite.csproj`

- [ ] **Step 1: Add the beta7.7 physical-application release note**

```text
- GPU custom overclocking now reports success only after NVIDIA driver readback confirms the requested offset.
```

- [ ] **Step 2: Build and run all tests**

Run: `dotnet test .\MechrevoLite.slnx -c Release --no-restore -p:GITHUB_ACTIONS=true`

Expected: all tests pass.

- [ ] **Step 3: Run the strict analyzer build**

Run: `dotnet build .\MechrevoLite.slnx -c Release --no-restore -p:GITHUB_ACTIONS=true -p:EnableNETAnalyzers=true -p:AnalysisLevel=latest -p:TreatWarningsAsErrors=true`

Expected: zero warnings and zero errors.

- [ ] **Step 4: Perform a supported-device manual check**

Apply a bounded core and memory offset, then inspect the application log for
the requested and driver-read values. UAC cancellation or a driver rejection
must produce an unapplied status, never a successful confirmation.
