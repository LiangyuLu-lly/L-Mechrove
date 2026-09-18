# First real-machine verification runbook

This is the **single ordered first-machine procedure** for every hardware-dependent behaviour in
the GCU 30/40/50 model-adaptation work. Take a real machine, run the sections top to bottom, and
you will know at the exact step where it failed.

`docs\hardware\runbooks\tNN-*.md` hold the per-task detail; each section below is complete on its
own and names the matching per-task file where one exists. Where a section depends on hardware the
CI box does not have, the unit chains are proven under fakes and the real-machine half is the
`BLOCKED-HW` item recorded in `.omo\evidence\f3-generation-status.json`.

**Binding rules enforced by this runbook** (any deviation is a FAIL, not a warning):

1. Display route is **MQTT-only**. The console must never write the `OemDisplayMode` firmware
   variable, and no firmware-variable write seam may exist.
2. **EC direct writes are forbidden** except the battery charge-limit path. No new firmware/NVRAM
   write anywhere.
3. Axis 1 (platform code) never determines axis 2 (dGPU generation). 30-series and 50-series
   **service-side write paths** stay **UNKNOWN**; only the 40-series write path is proven.
   Console-side marks: 40/50-series `PROVEN` (decompiled code), 30-series `INFERRED` (symbol-level
   only — .NET Native, no IL).
4. Never kill the process `L-Mechrevo`. Close it normally (`Stop-Process -Name 'L-Mechrevo'` only
   when the section says so, never `taskkill /F`).
5. Do not execute any extracted vendor binary. Read-only inspection only.

---

## How to use this document

- Run every step in **Windows PowerShell 5.1** on the target machine.
- One-time setup for every section:

  ```powershell
  $log = "$env:AppData\MechrevoLite\log.txt"
  New-Item -ItemType Directory -Force .omo\evidence | Out-Null
  git rev-parse HEAD | Tee-Object -Append .omo\evidence\first-machine.log
  ```

- For each observation, append the command output to `.omo\evidence\first-machine.log` so the whole
  run is one artifact:

  ```powershell
  function Note($section, $text) { "[$section] $text" | Tee-Object -Append .omo\evidence\first-machine.log }
  ```

- **PASS / FAIL legend.** Each section ends with an overall `PASS` definition and the *most likely
  failure signature*. A step that does not produce its stated expected observation is a failure at
  that step — record the section, the step number, and the raw output, then stop that section
  (continue with the next independent section only if it does not depend on the failed one).
- BLOCKED-HW is a legitimate outcome **only** when the required hardware is physically absent; it
  must be written to `.omo\evidence\f3-generation-status.json` with `probe` + `raw` + `hash`.

Global preconditions (run once, before section 1):

```powershell
Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer, Model
Get-CimInstance Win32_BIOS | Select-Object SMBIOSBIOSVersion
Get-CimInstance Win32_Processor | Select-Object Name, Manufacturer
Get-CimInstance Win32_VideoController | Select-Object Name, PNPDeviceID | Format-List
Get-Service GCUBridge -ErrorAction SilentlyContinue | Select-Object Status, StartType
Test-Path '\\.\ACPIDriver'
```

Expected: the machine identifies as a Mechrevo chassis, one NVIDIA adapter of the target
generation is present, and (for the EC sections) `\\.\ACPIDriver` exists. If `GCUBridge` is absent
or stopped, install/repair the GCU service first — most sections below assume it is `Running`.

---

## Section 1 — T9 registry-trace precondition (hot-swap capability values are written by the service)

Task: `refactor(gating): remove hardcoded per-model assumptions`.
Why hardware-dependent: the two replacement capability values
(`GpuHotSwapSwitchSupport`, `lgpuHotSwapSwitchStatus`) must be proven to be **written by the vendor
service**, not by us and not present before the service runs. Until this is proven, hot swap is not
relied upon and the gate is fail-closed.

### Step 1.1 — Prove the installer's own process writes no capability value

```powershell
Select-String -LiteralPath installer\Install-Gcu.ps1 -Pattern 'GpuHotSwapSwitchSupport|lgpuHotSwapSwitchStatus' |
  Measure-Object | Select-Object -ExpandProperty Count
```

Expected: `0`. Our installer never writes these keys.

### Step 1.2 — Snapshot both registry locations with the service stopped

Stop the service only through its own control path, never by killing `L-Mechrevo`:

```powershell
Stop-Service GCUBridge -Force
Start-Sleep -Seconds 3
$keys = @(
  'HKLM:\SOFTWARE\OEM\GamingCenter2\ItemSupport',
  'HKLM:\SOFTWARE\OEM\GamingCenter2\MySetting\GpuConfig'
)
foreach ($k in $keys) {
  "KEY $k"
  if (Test-Path $k) {
    (Get-ItemProperty $k).PSObject.Properties |
      Where-Object { $_.Name -in @('GpuHotSwapSwitchSupport','lgpuHotSwapSwitchStatus','APVersionCheck') } |
      ForEach-Object { "  $($_.Name)=$($_.Value)" }
  } else { "  (absent)" }
}
```

Expected: the two hot-swap values are **absent** (or the whole key absent). `APVersionCheck` may
be present; it is only corroborating evidence and is never the gate.

Record this output (raw text) before continuing.

### Step 1.3 — Start the service and snapshot again

```powershell
Start-Service GCUBridge
Start-Sleep -Seconds 20
foreach ($k in $keys) {
  "KEY $k"
  if (Test-Path $k) {
    (Get-ItemProperty $k).PSObject.Properties |
      Where-Object { $_.Name -in @('GpuHotSwapSwitchSupport','lgpuHotSwapSwitchStatus','APVersionCheck') } |
      ForEach-Object { "  $($_.Name)=$($_.Value)" }
  } else { "  (absent)" }
}
```

Expected: both `GpuHotSwapSwitchSupport` and `lgpuHotSwapSwitchStatus` are now **present** (the
vendor service wrote them on start). This absent→present transition is the precondition.

### Step 1.4 — Hash and persist the evidence

```powershell
$raw = (1.2)+(1.3) output joined as text
$bytes = [Text.Encoding]::UTF8.GetBytes($raw)
$hash = [BitConverter]::ToString([Security.Cryptography.SHA256]::HashData($bytes)).Replace('-','').ToLower()
$hash
```

Then write `.omo\evidence\t9-hotswap-values.json` with `status`, `probe`, `raw`, `hash`,
`hash_algorithm`, `replacement`, `consequence` (mirror the existing schema). The runbook path is
recorded in this file's `runbook` field.

**PASS (section 1):** with the service stopped the two values are absent; with it running both are
present; our installer writes neither; the evidence file carries `probe` + `raw` + a 64-hex `hash`
that matches the recorded `raw`.

**Most likely failure signature:** both values already present with the service stopped — the
values are provisioned by the BIOS/OS image rather than written by the GCU service, so the "service
writes them" claim is unproven and the hot-swap gate must stay disabled (record BLOCKED-HW).

---

## Section 2 — T16 / T17 / T18 display-route switching (dGPU-direct, iGPU-only, restart, per generation)

Tasks: `feat(gpu): record per-generation display-route matrix with evidence marks` (T16),
`feat(gpu): add per-generation display-route switching over MQTT` (T17),
`fix(gpu): match vendor iGPU-only retry and rollback semantics` (T18).
Detail: `docs\hardware\runbooks\t16-dgpu-generation.md`, `t17-display-route-mqtt.md`,
`t18-igpu-only-retry.md`.
Why hardware-dependent: the console only speaks MQTT; whether the vendor service actually applies
the route is observable only on a real machine.

Run this section once per available generation (30, 40, 50). 30-series and 50-series service-side
write paths remain UNKNOWN — only the 40-series route is proven.

### Step 2.1 — Preconditions

```powershell
Get-Service GCUBridge | Select-Object Status, Name
Get-NetTCPConnection -LocalPort 13688 -State Listen -ErrorAction SilentlyContinue |
  Select-Object LocalAddress, LocalPort, OwningProcess
```

Expected: service `Running`; exactly one listener on 13688.

### Step 2.2 — Record the detected generation

```powershell
(Get-CimInstance Win32_VideoController | Where-Object { $_.Name -match 'NVIDIA' }).Name
(Get-CimInstance Win32_VideoController | Where-Object { $_.Name -match 'NVIDIA' }).PNPDeviceID
Select-String -LiteralPath $log -Pattern 'dGPU generation resolved' | Select-Object -Last 1 | ForEach-Object { $_.Line }
```

Expected: a log line `dGPU generation resolved: Gen<NN> source=<MarketingName|PciDeviceId> hasDgpu=True`
whose `Gen<NN>` matches the model number (`30xx`→Gen30, `40xx`→Gen40, `50xx`→Gen50) and whose source
is `MarketingName` when the name parsed. `Generation=Unknown` while an RTX 30/40/50 name is visible
is a FAIL: record the exact `Name` string.

### Step 2.3 — Persist and reuse the resolved generation

```powershell
$cfg = "$env:AppData\MechrevoLite\config.json"
if (-not (Test-Path $cfg)) { $cfg = "$env:ProgramData\MechrevoLite\config.json" }
Get-Content -LiteralPath $cfg -Raw |
  Select-String -Pattern 'dgpu_generation"|dgpu_generation_fingerprint"|dgpu_has_dgpu"' -AllMatches |
  ForEach-Object { $_.Line }
```

Expected: `"dgpu_generation":"Gen<NN>"`, `"dgpu_has_dgpu":"1"`, non-empty `dgpu_generation_fingerprint`.
Then restart the app: a second start must produce **0** new `dGPU generation resolved` lines
(fingerprint unchanged → cached). A hardware change changes the fingerprint and produces exactly one.

### Step 2.4 — dGPU-direct ON / OFF round-trip (all generations)

In the app: Settings → GPU mode. Select **direct connect (独显直连)**, then back to the previous
mode. After each action:

```powershell
Select-String -LiteralPath $log -Pattern 'DGPU_DIRECT_CONNECT_TOGGLE' | Select-Object -Last 6 | ForEach-Object { $_.Line }
```

Expected: the action is published on MQTT topic `Setting/Control`, the machine ends up in the
requested route, and the **round-trip back to the prior mode succeeds** (the state before step 2.4
is restored). On 40/50 series a restart is expected as the last action; on 30 series no
`*_RESTART` may appear (see 2.7).

### Step 2.5 — iGPU-only ON / OFF round-trip (40/50 only) with vendor retry semantics

Switch to iGPU-only on AC power, then back. Then:

```powershell
Select-String -LiteralPath $log -Pattern 'IGPU_ONLY_CONNECT_RB_ON|IGPU_ONLY_CONNECT_RB_OFF|CheckDGpuStatus|confirmed=True|elapsed=' |
  Select-Object -Last 12 | ForEach-Object { $_.Line }
```

Expected, per T18:
- `IGPU_ONLY_CONNECT_RB_ON` (payload `SetToWMIEC=OK`) is resent every **2 s**, additionally every
  **4th** poll, and gives up after `count > 60` (~122 s);
- success for ON is `CheckDGpuStatusforIGpuOnlyOnSuccess == 2`, for OFF `== 1`, with
  `confirmed=True`;
- on timeout: `IGPUONLYCONNECTIONSWITCH_STATUS` with `Status` = the **pre-switch** value
  (`RB_OFF`=0 / `RB_ON`=1 / `RB_AUTO`=2) followed by a `GETSTATUS` refresh, and the UI shows the
  old mode (the official rollback);
- AUTO depends on AC (runtime 1 on AC, 2 on battery).

Then switch back to the prior mode — the round-trip must complete with **zero** rollback lines.

### Step 2.6 — Direct-connect restart and the 800 ms delay

```powershell
Select-String -LiteralPath $log -Pattern 'DGPU_DIRECT_CONNECT_TOGGLE|DGPU_DIRECT_CONNECT_RESTART' |
  Select-Object -Last 6 | ForEach-Object { $_.Line }
```

Expected: the toggle(s) first, then `DGPU_DIRECT_CONNECT_RESTART` **last**; the machine reboots and
boots into the requested route. On 50-series the restart is sent after `Task.Delay(800)`; on
40-series it maps to `shutdown /r /t 0`.

### Step 2.7 — Generation vocabulary is enforced (run on a 30-series machine)

On a **Gen30** machine request iGPU-only and direct-connect. Then:

```powershell
Select-String -LiteralPath $log -Pattern 'rejected: action not in dGPU generation|refusing to publish|IGPU_ONLY|DGPU_DIRECT_CONNECT_RESTART' |
  Select-Object -Last 8 | ForEach-Object { $_.Line }
```

Expected: a rejection line; **zero** `IGPU_ONLY_CONNECT_RB_*` and **zero**
`DGPU_DIRECT_CONNECT_RESTART`; the machine does **not** reboot. 30-series iGPU-only and RESTART are
`ProvenAbsent` in the matrix.

### Step 2.7b — 30-series console-side protocol (actions differ from 40/50)

The 30-series vendor console (`ControlCenter_4.17.47.13`, published .NET Native) is assessed
(**INFERRED** — symbol-level only) to send **only** two display-route actions on `Setting/Control`:
`DGPU_DIRECT_CONNECT_TOGGLE_ON` and `DGPU_DIRECT_CONNECT_TOGGLE_OFF`. The assembly has no IL, so the
basis is the metadata identifier heap + full PDB symbol table, not decompiled code
(`.omo\evidence\g30-console-decompile.md`). It has **no** `..._TOGGLE_IGPU`, `..._RESTART`,
`IGPU_ONLY_*`, `GPU_HOTSWAP_*`, and no `SetToWMIEC` payload field (all `PROVEN_ABSENT`: 0 hits).

On a **Gen30** machine switch to direct connect, then revert, and check:

```powershell
Select-String -LiteralPath $log -Pattern 'DGPU_DIRECT_CONNECT_TOGGLE_ON|DGPU_DIRECT_CONNECT_TOGGLE_OFF' |
  Select-Object -Last 4 | ForEach-Object { $_.Line }
Select-String -LiteralPath $log -Pattern 'IGPU_ONLY|_RESTART|GPU_HOTSWAP|TOGGLE_IGPU' |
  Measure-Object | Select-Object -ExpandProperty Count
```

Expected: the in-direction publishes exactly one `..._TOGGLE_ON` and the revert exactly one
`..._TOGGLE_OFF`, both on `Setting/Control`, with **no client-side retry** (the 30-series console has
none); the second command returns **0**; the machine does **not** reboot. The panel route may or may
not change — the 30-series **service-side** write path is UNKNOWN, so any change is recorded as
UNKNOWN, never as proven.

### Step 2.7c — 30-series UI offers no dead control (capability gate at the control layer)

The capability gate is composed in `MechrevoHw.CanOfferGpuModeSwitch` / `CanOfferIgpuOnly` /
`CanOfferGpuHotSwap` (`src\MechrevoLiteWin\Hardware\MechrevoHw.cs:468-482`) and consumed at the
control layer in `SettingsForm.RefreshDeviceCapabilities` (`src\MechrevoLiteWin\Settings.cs:2937`)
and `GPUModeControl.InitGPUMode` (`src\MechrevoLiteWin\Gpu\GPUModeControl.cs:28`). On a Gen30
machine `RESTART` / `IGPU_ONLY_*` / `GPU_HOTSWAP_*` are `ProvenAbsent` in the `DisplayRouteMatrix`
Gen30 row, so the manual mode switch has no usable route (the product's manual switch always
applies the target then reboots). The whole GPU-mode section must therefore be withheld.

On a **Gen30** machine open the app's main dashboard and check:

1. **No GPU-mode section at all.** `panelGPU` must be absent from the dashboard stack — no
   direct-connect / standard / ultimate / iGPU-only buttons and no GPU-mode row. A visible GPU
   control that can only fail is the exact defect this step detects.
2. **The section stays absent even if the model profile claims support.** `ItemSupport` may report
   `DGpuDirectConnectionSupport` / `iGPUModeOnlySupport` = 1: the profile is axis 1, the per-
   generation facts are axis 2, and axis 1 must never override axis 2.
3. **No offer of hot-swap or restart.** After using the app normally for a few minutes there is no
   `GPU_HOTSWAP_*`, `IGPU_ONLY_CONNECT_RB_*` or `DGPU_DIRECT_CONNECT_RESTART` publish in the log
   (commands in 2.7 / 2.7b).

Expected: the GPU-mode section is absent (if a machine still presents some other capability bit,
any remaining GPU control is disabled with a visible reason — never an enabled no-op button). The
30-series official console exposes only `DGPU_DIRECT_CONNECT_TOGGLE_ON/_OFF`; our UI must not offer
more. The unit proof for this gate is `GpuCapabilityGatingFailTests`
(`Gen30HidesTheGpuModeSectionWhileGen40KeepsIt`, plus the capability-level Gen30 cases in
`tests\MechrevoLite.Tests\GpuCapabilityGatingFailTests.cs`).

### Step 2.8 — Guardrails: no firmware write, no EC write

```powershell
Select-String -LiteralPath $log -Pattern 'OemDisplayMode|SetFwVars' | Measure-Object | Select-Object -ExpandProperty Count
Select-String -LiteralPath $log -Pattern 'ECWRITE|0x9C40A48C|WritePort' | Measure-Object | Select-Object -ExpandProperty Count
```

Expected: `0` and `0`. All route traffic went out on MQTT `Setting/Control`; the console never
wrote `OemDisplayMode` and never wrote EC.

**PASS (section 2):** generation resolves correctly and is cached; dGPU-direct and iGPU-only both
complete and round-trip back; iGPU-only timing/rollback matches T18; 30-series refuses
iGPU-only/RESTART; zero firmware and EC writes in the log.

**Most likely failure signature:** the switch reports success while the panel/route never changes,
or (worst case) a wrong `OemDisplayMode` byte on a platform mis-detection — on a 30/50 machine the
service-side write path is UNKNOWN, so a route that changes but cannot be attributed is recorded as
UNKNOWN, never as proven.

---

## Section 3 — T19 / T20 mode layer and EC-sourced PL/temperature defaults (Intel-vs-AMD discriminator)

Tasks: `feat(modes): separate vendor and console mode enums, send absolute detail` (T19),
`feat(modes): source per-SKU PL defaults from EC, fail-closed` (T20).
Detail: `docs\hardware\runbooks\t20-pl-defaults-ec.md`.
Why hardware-dependent: the PL/Tcc defaults live in EC and are per-SKU; the numeric values cannot be
unit-tested.

### Step 3.1 — Confirm the EC read channel exists

```powershell
Test-Path '\\.\ACPIDriver'
Get-Service -Name '*ACPIDriver*' -ErrorAction SilentlyContinue | Select-Object Name, Status
```

Expected: `True` and a running driver service. If either is false/absent, PL defaults are
unavailable → the correct behaviour is **PL editing disabled** (step 3.4), and this machine is
recorded as BLOCKED-HW in `f3-generation-status.json`.

### Step 3.2 — Discriminator check: Intel vs AMD must not be guessed

Field evidence: an **Intel 14650HX** machine (旷世16pro) read AMD PL values. Establish the true
platform from independent sources and compare with what the console believes:

```powershell
$cpu = (Get-CimInstance Win32_Processor).Manufacturer
$igpu = (Get-CimInstance Win32_VideoController | Where-Object { $_.Name -match 'Intel|AMD|Radeon' }).Name
$svc = (Get-ItemProperty 'HKLM:\SOFTWARE\OEM\GamingCenter2\ItemSupport' -ErrorAction SilentlyContinue).IsAMDPlatform
"cpu vendors : $cpu"
"igpu        : $igpu"
"ItemSupport.IsAMDPlatform : $svc"
Select-String -LiteralPath $log -Pattern 'AMD 平台|AmdPlatform|IsAllAmdPPT' | Select-Object -Last 3 | ForEach-Object { $_.Line }
```

Expected: the console's platform answer agrees with the physical CPU/iGPU vendor. A machine whose
CPU is Intel and iGPU is Intel must **not** report `IsAMDPlatform=1`, must not apply AMD power
fields (`UsesAmdPowerFields` false), and must not read/emit AMD PL values.

Failure observation: on an Intel machine the log/diagnostics show `AMD 平台: 是` or AMD PL values
are applied → the platform discriminator is wrong. Record the CPU name and the observed values; do
**not** write anything (the route is MQTT-only and EC writes are forbidden).

### Step 3.3 — Read the raw EC defaults (read-only)

```powershell
dotnet run --project src\Probe\Probe.csproj -- ec-read 1840,1841,1842,1843,1844,1845,1846,1847,1959,1960,1961,1962,2008,2009,2010
```

Expected: 14 byte values. Map Gaming PL1/PL2/PL4 = `1840/1841/1842`(+`1843`), Office =
`1844/1845/1846`(+`1847`), **Turbo** = `1959/1960/1961`(+`1962`), Tcc default offsets =
`2008/2009/2010`. Record raw output + SHA256 in `.omo\evidence\f3-generation-status.json`.

Sanity: Turbo values come from `1959-1962` (`GetTurboPLDefaultValue`), **not** BatterySaver. If
Turbo equals Office while the console shows a distinct Turbo limit, re-check addresses first.

### Step 3.4 — Confirm the app reads the same values, otherwise disables editing

Start the app, open the mode/power editor, then:

```powershell
Select-String -LiteralPath $log -Pattern 'EC defaults read for|disabling PL editing for|EC read failed for' |
  Select-Object -Last 8 | ForEach-Object { $_.Line }
```

Expected: one `EC defaults read for Gaming/Office/Turbo` per mode with numbers equal to step 3.3,
**or** `EC <addr> unreadable; disabling PL editing for <mode>` with the editor disabled and no
invented wattage. PL values are sent as **strings**, `CpuTccOffset = TjMax − offset`, and `PL4` is
halved only when the double-flag is set (T19).

### Step 3.5 — Two enums stay separate

```powershell
Select-String -LiteralPath $log -Pattern 'Turbo|BatterySaver|SET_OPERATING_MODE_DETAIL' | Select-Object -Last 10 | ForEach-Object { $_.Line }
```

Expected: the third default set is labelled **Turbo**; no `BatterySaver` label attached to the
`1959-1962` family; absolute quantities are sent under `SET_OPERATING_MODE_DETAIL` (PL1/PL2/PL4,
Tcc, AmdSPL/SPPT/FPPT, TGP target, DynamicBoost, clock offsets, FanSwitchSpeed).

### Step 3.6 — No EC write

```powershell
Select-String -LiteralPath $log -Pattern 'ECWRITE|0x9C40A48C' | Measure-Object | Select-Object -ExpandProperty Count
```

Expected: `0`. T19/T20 are read-only EC consumers; the only EC write in the product is the battery
charge limit.

**PASS (section 3):** the platform discriminator agrees with the physical CPU/iGPU; per-mode
defaults equal the raw EC bytes for all three modes; unreadable EC disables editing instead of
guessing; no EC write appears.

**Most likely failure signature:** an Intel machine reports AMD platform / AMD PL values (field
case 14650HX), or a mode shows a fabricated wattage when the EC bytes are unreadable.

---

## Section 4 — T22 + G0 payload gate (seven evidence items, per-generation pairs)

Task: `build(installer): ship only the newest 50-series GCU payload`.
Why hardware-dependent: G0 is the gate that permits deleting the older (40-series) payload trees;
it requires 30-series and 40-series real-machine evidence. BLOCKED-HW entries never satisfy G0, and
the 30/40 machine identities must differ (EC project byte and/or dGPU device-id), otherwise the
gate is invalid.

Run items (a)–(g) on **one 30-series machine and one 40-series machine**, storing each entry with
its embedded raw probe: (i) EC 1856/1868 raw bytes, (ii) dGPU PCI device-id, (iii) `ItemSupport`
dump or its hash. Write everything to `.omo\evidence\t22-gate.json`.

### Step 4.0 — Machine identity (must differ between the 30 and 40 entries)

```powershell
dotnet run --project src\Probe\Probe.csproj -- ec-read 1856,1868
(Get-CimInstance Win32_VideoController | Where-Object { $_.Name -match 'NVIDIA' }).PNPDeviceID
(Get-ItemProperty 'HKLM:\SOFTWARE\OEM\GamingCenter2\ItemSupport' -ErrorAction SilentlyContinue) |
  ConvertTo-Json -Depth 4
```

Expected: a distinct EC project byte and/or device-id per generation. Same identity for both
entries = gate invalid.

### Step 4.1 (a) — Service starts and becomes Ready

```powershell
Get-Service GCUBridge | Select-Object Status, Name
Get-ItemProperty 'HKLM:\SOFTWARE\OEM\GamingCenter2\ItemSupport' -ErrorAction SilentlyContinue |
  Select-Object ServiceReady
```

Expected: service `Running`, `ServiceReady` = `1`. `ServiceReady` must be set by the vendor
service, never by us.

### Step 4.2 (b) — `ItemSupport` non-empty and generation-appropriate

```powershell
$k = 'HKLM:\SOFTWARE\OEM\GamingCenter2\ItemSupport'
(Get-ItemProperty $k).PSObject.Properties | Where-Object { $_.Name -notmatch '^PS' } |
  Measure-Object | Select-Object -ExpandProperty Count
(Get-ItemProperty $k) | Select-Object BIOS_PROJECT_ID, IsNvGpu, DGpuDirectConnectionSupport, iGPUModeOnlySupport
```

Expected: non-empty profile; the generation-facing bits are consistent with the actual GPU (e.g.
`IsNvGpu=1` on the NVIDIA machine). A profile that contradicts the physical GPU is a FAIL.

### Step 4.3 (c) — GPU toggle ON and OFF both complete, and a reboot returns to the prior mode

Perform the dGPU-direct ON then OFF switch (Section 2, steps 2.4/2.6), then reboot and re-read the
route. Expected: both directions complete, and after the reboot the machine is back in the mode it
held before the test. Executed **over MQTT only** so the evidence is attributable to the vendor
service.

### Step 4.4 (d) — Route read-back is consistent

```powershell
Select-String -LiteralPath $log -Pattern 'confirmed=True|IGPUONLYCONNECTIONSWITCH_STATUS|DGPU_DIRECT_CONNECT' |
  Select-Object -Last 10 | ForEach-Object { $_.Line }
```

Expected: the post-switch read-back agrees with the requested route (`confirmed=True`), and after
the reboot the app reports the same route. The console has no firmware-variable read/write seam by
design; the read-back is the service's own status over MQTT, never a direct variable write.

### Step 4.5 (e) — Chassis fan table is served

```powershell
Select-String -LiteralPath $log -Pattern 'FanTable|fan table|UserFanTables' | Select-Object -Last 5 | ForEach-Object { $_.Line }
Get-ChildItem "$env:ProgramFiles\...\GCU\AiStoneService\MyControlCenter\UserFanTables" -ErrorAction SilentlyContinue |
  Select-Object -First 8 Name
```

Expected: the fan table for this machine's ProjectID directory is present in the payload and the
service serves it (the app reads a fan curve). Missing directory → `NotInSet` for this machine.

### Step 4.6 (f) — Charge limit writes

Set the charge limit slider and read back. Expected: the write succeeds and read-back equals the
request (upper `0x7B9` + lower `0x7D0` pair), or the machine is not in the verified set and the UI
says so without pretending. This is the **only** permitted EC write.

### Step 4.7 (g) — Exactly one service on 13688

```powershell
Get-Service | Where-Object { $_.Name -match 'GCU' } | Select-Object Name, Status
Get-NetTCPConnection -LocalPort 13688 -State Listen -ErrorAction SilentlyContinue |
  Select-Object LocalAddress, LocalPort, OwningProcess
```

Expected: exactly one GCU service and exactly one listener owning 13688.

### Step 4.8 — Record `t22-gate.json`

For each of (a)–(g) write `status` (`PASS`/`BLOCKED-HW`), the exact `probe` command, `raw` output,
and the SHA256 `hash` of `raw`; embed the machine identity from 4.0. Pass the gate only if every
item is non-BLOCKED-HW, both machine identities differ, and the 30-series **and** 40-series entries
are present.

**PASS (section 4):** all seven items pass, on two distinct machines (30 and 40), with embedded
probes and hashes; then and only then may the older payload trees be deleted.

**Most likely failure signature:** only the 50-series machine is available → records are
BLOCKED-HW → the gate does **not** pass and the older payload trees must remain (which does not
block using the newest payload).

---

## Section 5 — T26 post-install verification

Task: `feat(installer): verify service readiness after install`.
Why hardware-dependent: it verifies a real installed service, a real port owner and the service's
own registry write after a real install.
Detail: `installer\Install-Gcu.ps1` (`Test-GcuPostInstall`, `Write-GcuInstallStatus`).

### Step 5.1 — Install (or repair) and locate the status file

```powershell
$status = "$env:ProgramData\L-Mechrevo\logs\gcu-install-status.json"
Get-Content -LiteralPath $status -Raw -ErrorAction SilentlyContinue
Get-ChildItem "$env:ProgramData\L-Mechrevo\logs\gcu-install-*.log" | Sort-Object LastWriteTime -Descending |
  Select-Object -First 1 FullName, Length
```

Expected: `Status` = `ready`, `Checks` length 4, empty `Failed`. On failure: `Status` = `failed`
with an actionable `Reason`, and the newest `gcu-install-*.log` contains a
`FATAL: post-install verification failed` line naming the log path.

### Step 5.2 — The four invariants

```powershell
@(Get-Service GCUBridge -ErrorAction SilentlyContinue).Count
@(Get-NetTCPConnection -LocalPort 13688 -State Listen -ErrorAction SilentlyContinue).Count
(Get-ItemProperty 'HKLM:\SOFTWARE\OEM\GamingCenter2\ItemSupport' -ErrorAction SilentlyContinue).ServiceReady
(Get-ItemProperty 'HKLM:\SOFTWARE\OEM\GamingCenter2\ItemSupport' -ErrorAction SilentlyContinue).PSObject.Properties.Count
```

Expected: `1` service; `1` listener; `ServiceReady` = `1`; non-zero property count (the service
rewrote `ItemSupport`). None of these values is written by us.

### Step 5.3 — Silent-failure surface is visible

Run a `/VERYSILENT` install path and confirm that a broken invariant produces the failed status
file and the FATAL log line rather than "install success with no GCU service".

**PASS (section 5):** status file `ready`, four checks green, `ServiceReady` set by the service,
and a broken invariant fails loudly with a reason and log path.

**Most likely failure signature:** setup reports success while `gcu-install-status.json` is
`failed` or missing — Inno `[Run]` has no `ignoreerrors`, so the non-zero exit is logged but not
surfaced; that is exactly the defect this section detects.

---

## Section 6 — T32 keyboard lighting after resume

Task: `fix(keyboard): restore keyboard lighting effect after resume`.
Detail: `docs\hardware\runbooks\t32-keyboard-lighting-resume.md`.
Why hardware-dependent: the firmware may leave frame mode on resume; only real sleep/wake shows it.

### Step 6.1 — Baseline: light on

Turn the keyboard backlight on with an effect; confirm it is lit:

```powershell
Select-String -LiteralPath $log -Pattern 'RGB 自动恢复|KbPowerOn|HID 效果' | Select-Object -Last 3 | ForEach-Object { $_.Line }
```

### Step 6.2 — Sleep then wake; exactly one restore

```powershell
rundll32.exe powrprof.dll,SetSuspendState 0,1,0
Start-Sleep -Seconds 20
Select-String -LiteralPath $log -Pattern 'RGB 自动恢复|HID 效果|灯效开机恢复' | Select-Object -Last 8 | ForEach-Object { $_.Line }
```

Expected: exactly **one** `RGB 自动恢复：HID 效果 <mode> 已运行` for this resume; the light is lit
again with the previous effect; no second restore until the next sleep/wake.

### Step 6.3 — Resume with light off stays off

Turn the backlight off, sleep, wake. Expected: `RGB 唤醒恢复：用户已关闭键盘灯，不重放效果`, the
keyboard stays **dark**, no `powerstatus=1` frame and no `HID 效果 … 已运行`.

### Step 6.4 — Fast repeat wake does not double-restore, other channels unaffected

Sleep/wake twice within ~10 s → at most one restore per wake. Then check lightbar/logo still
restore once per cycle.

**PASS (section 6):** light-on resumes restore exactly once with the prior effect; light-off
resumes stay off; no double restore.

**Most likely failure signature:** after resume the light stays dark with no restore line (the
firmware left frame mode) — or the opposite regression, light comes on after a resume where the
user had turned it off.

---

## Section 7 — T33 screen blank

Task: `fix(display): make screen blank fail loudly instead of silently`.
Detail: `docs\hardware\runbooks\t33-screen-blank.md`.
Why hardware-dependent: whether a brightness interface exists is machine-specific.

### Step 7.1 — Is a brightness interface present?

```powershell
Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorBrightness -ErrorAction SilentlyContinue |
  Select-Object InstanceName, CurrentBrightness
```

Values shown → step 7.2 (happy). Nothing → step 7.4 (fail-loud).

### Step 7.2 — Blank: one dim, held awake

Click "息屏" once, then:

```powershell
Select-String -LiteralPath $log -Pattern 'Screen blank' | Select-Object -Last 5 | ForEach-Object { $_.Line }
```

Expected: exactly one `Screen blank: dimmed to 0% (was <n>%); system held awake
(ES_SYSTEM_REQUIRED|ES_DISPLAY_REQUIRED).`; the panel goes dark; the machine does **not** sleep.

### Step 7.3 — Restore on input and on watchdog

Move the mouse → `Screen blank: restored brightness to <n>% (user input).` If no input, after 30
minutes → `... (watchdog).`

### Step 7.4 — Fail-loud on a machine without the interface

On the 4060 machine exhibiting the bug, click the switch, then:

```powershell
Select-String -LiteralPath $log -Pattern 'SCREEN_BLANK_FAILED|Screen blank failed' | Select-Object -Last 5 | ForEach-Object { $_.Line }
```

Expected: `SCREEN_BLANK_FAILED: brightness read failed (no WMI instance)` (or `brightness write to
0% failed`), a visible warning dialog, the switch springs back to OFF, no config written, and the
app never claims the screen is blanked.

### Step 7.5 — No `SC_MONITORPOWER`

Confirm `Select-String -LiteralPath $log -Pattern 'WM_SYSCOMMAND|SC_MONITORPOWER'` → `0` hits.

**PASS (section 7):** with the interface, one dim + held-awake + restore; without it, a loud
failure and no false "blanked" claim.

**Most likely failure signature:** the switch silently does nothing with no `SCREEN_BLANK_FAILED`
line — the original field bug.

---

## Section 8 — T34 colour-profile (sRGB) switching

Task: `fix(display): make colour-profile switching take effect or fail visibly`.
Detail: `docs\hardware\runbooks\t34-color-calibration.md`.
Why hardware-dependent: the panel gamut change is visible only on real hardware.

### Step 8.1 — HDR off for the on-direction

```powershell
Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\VideoSettings' -ErrorAction SilentlyContinue
```

Expected: HDR off → sRGB switching can succeed; HDR on → step 8.4.

### Step 8.2 — Switch to sRGB and read back

Choose the sRGB profile, then:

```powershell
Select-String -LiteralPath $log -Pattern 'SetColorCalibration' | Select-Object -Last 5 | ForEach-Object { $_.Line }
```

Expected: `SetColorCalibration(mode 2) -> COLOR_CALIBRATION_ON_SRGB` and
`SetColorCalibration confirmed=True expectedMode=2 actualMode=2 ...`; the panel visibly changes to
sRGB. **Success means read-back == request.**

### Step 8.3 — Off and round-trip

Turn calibration off, back to sRGB, then to P3. Expected: each switch confirms only when the
read-back matches; `COLOR_CALIBRATION_OFF` carries the profile that was on; a second switch to the
same profile logs `already applied` and publishes nothing.

### Step 8.4 — HDR on must fail visibly

Turn HDR on, try sRGB. Expected: `SetColorCalibration(mode 2) blocked: HDR is enabled`, **zero**
`COLOR_CALIBRATION_ON_*` frames, the UI reports failure / keeps the current profile.

### Step 8.5 — Machine without the calibration registry

If `HKLM\SOFTWARE\OEM\GamingCenter2\MySetting\DisplayFeatures` is absent, the switch reports
failure and never claims a profile was applied.

**PASS (section 8):** a successful switch is confirmed by read-back (sRGB = 2); HDR/absent-registry
fail visibly with no wrong-profile write.

**Most likely failure signature:** `confirmed=True` while `actualMode != 2` (silent success), or the
HDR path publishing a `COLOR_CALIBRATION_ON_*` frame anyway.

---

## Section 9 — T35 status-bar overlap and DPI scaling (strict)

Task: `fix(ui): stop footer text/icon overlap and align DPI scaling` (round 5, strictly required).
Detail: `docs\hardware\runbooks\t35-footer-dpi.md`.
Why hardware-dependent: the audit paints at real viewports/scales.
**Exact resolutions and scales to check** (the audit matrix, from `UI\UiAuditRunner.cs`):

| Resolution | Scales |
|---|---|
| 1280×720 | 100%, 150%, 200% |
| 1366×768 | 125%, 200% |
| 1600×900 | 125%, 150% |
| 1920×1080 | 100%, 125%, 150%, 175%, 200% |
| 3840×2160 | 175%, 200% |

### Step 9.1 — Run the real-surface UI audit

```powershell
powershell -NoProfile -File scripts\test-ui.ps1
$report = Get-ChildItem artifacts\ui-audit-* -Directory | Sort-Object LastWriteTime -Descending |
  Select-Object -First 1 | ForEach-Object { Join-Path $_.FullName 'ui-audit.json' }
$r = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
"issues total    : $($r.IssueCount)"
"sibling-overlap : $(($r.Issues | Where-Object Kind -eq 'sibling-overlap').Count)"
"footer findings : $(($r.Issues | Where-Object { $_.Kind -match 'footer' }).Count)"
```

Expected: `sibling-overlap` **0** and `footer findings` **0** across every row of the table above.

### Step 9.2 — Footer rects on the affected machines (1366×768 @125%, 1920×1080 @150%)

Resize to those viewports and confirm the footer's text label and icon do not overlap and the
controls stay inside the footer band. If overlap is visible, capture the viewport + DPI and the
offending `ui-audit.json` rows:

```powershell
$r.Issues | Where-Object { $_.Form -eq 'Settings-Main' } |
  Select-Object Viewport, Kind, Control, Detail | Format-Table -AutoSize
```

### Step 9.3 — DPI reflow

Change Windows scaling to 125% then 150%, restart the app, re-check step 9.1. Expected:
`sibling-overlap`/`footer` stay 0 and the footer width follows the scale factor.

### Step 9.4 — No other footer finding kind

```powershell
$r.Issues | Group-Object Kind | Select-Object Count, Name | Sort-Object Count -Descending
```

Expected: no `footer-occlusion`, no `sibling-overlap`.

**PASS (section 9):** zero sibling-overlap and zero footer findings at every listed
resolution/scale and on both affected models; footer reflow tracks the scale factor.

**Most likely failure signature:** a `sibling-overlap` or `footer` finding at 1366×768 @125% or
1920×1080 @150% on the affected models.

---

## Section 10 — T36 reboot autostart and official-console coexistence prompt

Task: `fix(startup): make reboot autostart reliable and warn on console coexistence`.
Detail: `docs\hardware\runbooks\t36-autostart-coexistence.md`.
Why hardware-dependent: the scheduled task trigger and the reboot are real-machine events.

### Step 10.1 — The task exists and points at the installed image

```powershell
schtasks /query /fo LIST /v | Select-String -Pattern 'LMechrevo|TaskName' | Select-Object -First 6
```

Expected: a task named `LMechrevo_<your SID>` pointing at the **installed** `L-Mechrevo.exe` (not a
`%TEMP%` path), `LUA` run level, interactive token, LogonTrigger + ConsoleConnect trigger.

### Step 10.2 — Repair when the task is missing but autostart is enabled

```powershell
schtasks /delete /tn "LMechrevo_<your SID>" /f
Start-Process "<install dir>\L-Mechrevo.exe"
Start-Sleep -Seconds 20
schtasks /query /fo LIST /v | Select-String 'LMechrevo' | Select-Object -First 3
```

Expected: the task is rebuilt automatically because the user setting is enabled.

### Step 10.3 — Reboot and confirm it actually starts

Reboot; after logon (allow ~30 s):

```powershell
Get-Process L-Mechrevo -ErrorAction SilentlyContinue | Select-Object Id, StartTime
```

Expected: the process is running with `StartTime` after the boot time. (This is the BLOCKED-HW
half until run on the affected model; recorded in `.omo\evidence\t36-reboot-status.json`.)

### Step 10.0 — The installer must detect a PRE-INSTALLED .NET 10 runtime

Field bug: the beta18 installer did not detect an already-installed .NET 10 runtime, so a user who
installed it manually was still blocked. Two defects: it read **subkey** names where the .NET
installer records **value** names, and it read only the 64-bit registry view (on the affected
machine the populated key is in the 32-bit view).

Run this **before** installing, on a machine that already has .NET 10:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer\Test-DotNetDesktopRuntime.ps1
```

Expected: `DETECTED Microsoft.WindowsDesktop.App 10.<x> (x64)` and exit code `0`. A `MISSING` line
on a machine that has the runtime is the exact false-negative this step exists to catch; the line
also prints the registry values it did see, so the cause is visible in one step.

Cross-check the raw registry shape (versions are **value names**, and the populated view varies):

```powershell
reg query "HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App" /reg:32
reg query "HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App" /reg:64
Get-ChildItem "$env:ProgramFiles\dotnet\shared\Microsoft.WindowsDesktop.App" -Directory | Select-Object Name
```

Expected: at least one view lists a `10.x` value name, and/or the on-disk directory lists `10.x`.
Then run the installer: it must **not** prompt to download the runtime.

### Step 10.3a — The installer owns the elevated task (N5)

The autostart task is created by the **elevated installer** (`Install-Gcu.ps1`), not by the app at
runtime: an unelevated app cannot create a "run with highest privileges" task, which is the same
permission root cause as the original bug. The app is permanently elevated by owner decision, and
the installer is the single place elevation is obtained.

```powershell
$sid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
schtasks /query /tn "LMechrevo_$sid" /xml | Select-String 'RunLevel|Command|Arguments'
```

Expected: the task exists, `<RunLevel>HighestAvailable</RunLevel>` is present, `<Command>` is the
installed `L-Mechrevo.exe`, and there is **no** `<Arguments>` element (an elevated task whose action
takes arguments is an injectable elevation primitive).

```powershell
# launching the app manually must raise NO UAC prompt
Start-Process "$env:ProgramFiles\L-Mechrevo\L-Mechrevo.exe"
# after a reboot the app must be running, elevated, with no UAC prompt
Get-Process L-Mechrevo | Select-Object Id, StartTime
```

Expected: no UAC prompt on manual launch; after a reboot the process is running with `StartTime`
after the boot time and no prompt was shown.

```powershell
# uninstall must leave no boot-time elevation entry point
& "$env:ProgramFiles\L-Mechrevo\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
schtasks /query /tn "LMechrevo_$sid"
```

Expected: after uninstall the task no longer exists (`schtasks` reports it cannot be found).

### Step 10.3b — A failed registration must be visible, not silent

Autostart repair now registers the task **in place** (it never deletes the existing entry first, so
a denied re-register can no longer leave the user with no entry at all). If registration still
fails while the user has autostart enabled, the app shows a **tray balloon** — the log is off by
default, so the balloon is the only user-visible signal.

```powershell
Select-String -LiteralPath $log -Pattern 'Autostart registration failed' | Select-Object -Last 3 | ForEach-Object { $_.Line }
schtasks /query /tn "LMechrevo_<your SID>" /fo LIST /v | Select-String 'Last Result|上次结果'
```

Expected: no balloon and no log line on a healthy launch; the task's last result is `0x0`
(`0x41303` = "has not run yet" is normal before the first reboot). If the balloon **does** appear,
re-run step 10.2 and record the balloon text plus the `Autostart registration failed` line — that
is the permission denial this section exists to detect.

### Step 10.4 — Transient image must not register

Run from a `%TEMP%` build output, toggle autostart on, and confirm no task is registered and the
log explains why (`transient|临时`).

### Step 10.5 — Official-console coexistence is a prompt, never a silent delete

```powershell
Get-Service GCUBridge -ErrorAction SilentlyContinue | Select-Object Status, Name
Get-NetTCPConnection -LocalPort 13688 -State Listen -ErrorAction SilentlyContinue | Select-Object OwningProcess
```

If a **vendor** service/console is present, expected: the app shows the visible prompt
(`请手动卸载「官方控制台」应用 …`) and never deletes it silently. The GCU service takeover
(backup → uninstall → install ours → verify → rollback) is the installer's job; verify the machine
is left with exactly one GCU service, Running, and that no firmware/NVRAM write occurred:

```powershell
Select-String -LiteralPath $log -Pattern 'GCU|takeover|OemDisplayMode|SetFwVars' | Select-Object -Last 8 | ForEach-Object { $_.Line }
```

**PASS (section 10):** the task exists/repairs correctly, the app autostarts after a reboot,
transient images never register, and coexistence is a visible prompt with no silent deletion and no
firmware write.

**Most likely failure signature:** after a reboot the process is absent although autostart is
enabled and the task exists/points at a valid path — the exact field bug.

---

## Section 11 — Recording the run

When done, produce a single summary next to the log:

```powershell
$sections = 'T9','T16-18','T19-20','T22-G0','T26','T32','T33','T34','T35','T36'
foreach ($s in $sections) { "$s : <PASS|FAIL|BLOCKED-HW> - <one line>" | Tee-Object -Append .omo\evidence\first-machine.log }
Get-FileHash .omo\evidence\first-machine.log -Algorithm SHA256
```

Any `FAIL` must name the section and step. Any `BLOCKED-HW` must also be written to
`.omo\evidence\f3-generation-status.json` with `probe` + `raw` + `hash`.
