# P1 Reliability Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore full solution builds and make GCU liquid-cooling pump/fan changes require fresh status confirmation.

**Architecture:** The service layer remains responsible for protocol writes and state confirmation. UI code consumes the service result and rolls back existing selections on failure. Generated diagnostic artifacts remain on disk but are excluded at the `Probe` project boundary.

**Tech Stack:** .NET 10, WinForms, MQTTnet, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-25-p1-reliability-remediation-design.md`

## Global Constraints

- Do not issue live hardware control messages during automated verification.
- Do not change GCU services, AppX packages, startup tasks, or firewall state.
- Preserve the existing GCU protocol payloads.
- A status query may be retried; a pump or fan write may not be retried automatically.

---

### Task 1: Exclude Probe Diagnostic Artifacts

**Files:**
- Modify: `src/Probe/Probe.csproj`

**Interfaces:**
- Produces: a solution project that does not compile `src/Probe/artifacts/**`.

- [ ] **Step 1: Reproduce the full-solution build failure**

Run:

```powershell
dotnet build .\MechrevoLite.slnx -c Release -p:GITHUB_ACTIONS=true --no-restore
```

Expected: duplicate assembly-attribute errors sourced from `src\Probe\artifacts`.

- [ ] **Step 2: Exclude generated artifacts**

Add this item group to `src/Probe/Probe.csproj`:

```xml
<ItemGroup>
  <Compile Remove="artifacts\**" />
  <EmbeddedResource Remove="artifacts\**" />
  <None Remove="artifacts\**" />
</ItemGroup>
```

- [ ] **Step 3: Verify the full solution now builds**

Run:

```powershell
dotnet build .\MechrevoLite.slnx -c Release -p:GITHUB_ACTIONS=true --no-restore
```

Expected: no duplicate assembly-attribute errors and exit code zero.

### Task 2: Require Fresh Pump Status Confirmation

**Files:**
- Modify: `tests/MechrevoLite.Tests/LiquidCoolingIntegrationTests.cs`
- Modify: `src/MechrevoLiteWin/Hardware/MechrevoService.cs`

**Interfaces:**
- Consumes: `MechrevoHw.LcStatusVersion`, `MechrevoHw.LcPumpControl`, `MechrevoHw.WaitForStateAsync`, and `BT_LC/Control`.
- Produces: `SwitchLcPump(int)` returns `true` only after a fresh matching status report.

- [ ] **Step 1: Write the failing pump tests**

Add tests equivalent to:

```csharp
[Fact]
public async Task SwitchLcPump_ConfirmsOnlyAfterFreshMatchingStatus()
{
    // The publisher supplies LC_PumpCtrl=1 only when the service asks GETSTATUS.
    // Assert the method returns true and a GETSTATUS request was emitted.
}

[Fact]
public async Task SwitchLcPump_RejectsFreshMismatchedStatus()
{
    // The publisher supplies LC_PumpCtrl=0 for a requested profile of 1.
    // Assert the method returns false.
}
```

- [ ] **Step 2: Run the two tests and confirm RED**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SwitchLcPump"
```

Expected: the current implementation incorrectly returns true without `GETSTATUS` or without rejecting the mismatched report.

- [ ] **Step 3: Implement a private fresh-status confirmation helper**

In `MechrevoService`, add a private helper with this behavioral contract:

```csharp
Task<bool> ConfirmLiquidCoolingStateAsync(
    long statusVersionBeforeWrite,
    Func<bool> targetReached,
    string operation)
```

The helper waits for a status version greater than `statusVersionBeforeWrite`; if none arrives promptly, it publishes `GETSTATUS` and waits again. A first fresh mismatched report triggers one more `GETSTATUS` query to exclude an in-flight pre-command state. It returns true only when a fresh report satisfies `targetReached`, and it never repeats the physical control write.

- [ ] **Step 4: Route `SwitchLcPump` through the helper**

Capture `LcStatusVersion` before publishing `LC_PumpCtrl`; pass `() => _hw.LcPumpControl == index` to the helper. Keep the control payload exactly unchanged.

- [ ] **Step 5: Verify GREEN**

Run the command from Step 2. Expected: both pump tests pass.

### Task 3: Require Fresh Fan Status Confirmation

**Files:**
- Modify: `tests/MechrevoLite.Tests/LiquidCoolingIntegrationTests.cs`
- Modify: `src/MechrevoLiteWin/Hardware/MechrevoService.cs`

**Interfaces:**
- Consumes: the helper from Task 2 and `MechrevoHw.LcFanControl`.
- Produces: `SwitchLcFan(int)` returns `true` only after a fresh matching status report.

- [ ] **Step 1: Write a failing fan confirmation test**

```csharp
[Fact]
public async Task SwitchLcFan_ConfirmsOnlyAfterFreshMatchingStatus()
{
    // Supply LC_FanCtrl=3 in response to GETSTATUS and assert true.
}
```

- [ ] **Step 2: Run the fan test and confirm RED**

Run:

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SwitchLcFan"
```

Expected: failure because the existing implementation does not request or validate status.

- [ ] **Step 3: Route `SwitchLcFan` through the Task 2 helper**

Capture `LcStatusVersion` before publishing `LC_FanCtrl`; pass `() => _hw.LcFanControl == index` to the helper.

- [ ] **Step 4: Verify GREEN**

Run the command from Step 2. Expected: the fan test passes.

### Task 4: Align UI Copy With Confirmation Semantics

**Files:**
- Modify: `src/MechrevoLiteWin/Settings.cs`

**Interfaces:**
- Consumes: the confirmed boolean result from `SwitchLcPump` and `SwitchLcFan`.
- Produces: Chinese UI feedback that differentiates confirmed application from rollback.

- [ ] **Step 1: Change only successful GCU status text**

Replace the GCU success messages that say “命令已发送（等待 GCU 回读）” after awaited service success with “已确认”. Do not change Direct BLE messages.

- [ ] **Step 2: Build the main application**

Run:

```powershell
dotnet build .\src\MechrevoLiteWin\MechrevoLite.csproj -c Release -p:GITHUB_ACTIONS=true --no-restore
```

Expected: exit code zero.

### Task 5: Regression Verification

**Files:**
- Test: `tests/MechrevoLite.Tests/LiquidCoolingIntegrationTests.cs`

- [ ] **Step 1: Run the complete test project**

```powershell
dotnet test .\tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj -c Release --no-restore
```

Expected: all non-hardware tests pass; the opt-in real-GCU integration test remains skipped unless explicitly enabled.

- [ ] **Step 2: Run full solution build**

```powershell
dotnet build .\MechrevoLite.slnx -c Release -p:GITHUB_ACTIONS=true --no-restore
```

Expected: exit code zero.

- [ ] **Step 3: Inspect the change set**

Run:

```powershell
Get-ChildItem .\src\Probe\Probe.csproj, .\src\MechrevoLiteWin\Hardware\MechrevoService.cs, .\src\MechrevoLiteWin\Settings.cs, .\tests\MechrevoLite.Tests\LiquidCoolingIntegrationTests.cs
```

Expected: only the planned files plus this design and plan change.
