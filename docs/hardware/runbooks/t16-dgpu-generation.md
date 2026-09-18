# T16 first real-machine verification runbook — dGPU generation probe + display-route matrix

Task: `feat(gpu): record per-generation display-route matrix with evidence marks`.

The generation probe is hardware-dependent (it reads the GPU marketing name / NVIDIA PCI
device-id). Hard testing without a machine is limited to the pure logic + fakes; this runbook
is the ordered, copy-pasteable confirmation on real hardware.

`BLOCKED-HW` status: the unit chains are proven under fakes; steps 4-7 below are the
real-machine execution that this runbook defers to.

Run every step in **Windows PowerShell 5.1** on the target machine. Do not kill any
process named `L-Mechrevo`; close the console normally instead.

---

## Step 1 — Read the GPU adapters (source of truth for this machine)

```powershell
Get-CimInstance Win32_VideoController |
  Select-Object Name, PNPDeviceID | Format-List
```

Expected: one line whose `Name` contains `NVIDIA GeForce RTX 30xx/40xx/50xx`
(and possibly an Intel/AMD iGPU line). Record the NVIDIA name and the `DEV_` part of
`PNPDeviceID`.

Failure observation to record: no NVIDIA line at all -> the probe must report `NoDgpu`.

## Step 2 — Derive the expected generation by hand

```powershell
$gpu = (Get-CimInstance Win32_VideoController | Where-Object { $_.Name -match 'NVIDIA' }).Name
$dev = (Get-CimInstance Win32_VideoController | Where-Object { $_.Name -match 'NVIDIA' }).PNPDeviceID
"NVIDIA name : $gpu"
"device id   : " + ([regex]::Match($dev, 'DEV_([0-9A-F]{4})').Groups[1].Value)
```

Expected generation = the leading two digits of the `RTX NNNN` model:
`30` -> Gen30, `40` -> Gen40, `50` -> Gen50. (PCI device-id high byte `20..25`=Gen30,
`26..28`=Gen40, `2B..30`=Gen50 is the fallback only.)

## Step 3 — Confirm `BIOS_PROJECT_ID` is NOT used as the generation signal

```powershell
Get-ItemProperty 'HKLM:\SOFTWARE\OEM\GamingCenter2\ItemSupport' -ErrorAction SilentlyContinue |
  Select-Object BIOS_PROJECT_ID
```

Expected: whatever this prints, the generation in step 4 must match step 2's model number,
**not** anything derivable from this value. If they disagree, step 4 wins.

## Step 4 — Start the app and read the resolved generation from the log

```powershell
$log = "$env:AppData\MechrevoLite\log.txt"
Start-Process "$PWD\src\MechrevoLiteWin\bin\x64\Debug\net10.0-windows10.0.19041.0\L-Mechrevo.exe"
Start-Sleep -Seconds 15
Select-String -LiteralPath $log -Pattern 'dGPU generation resolved' |
  Select-Object -Last 1 | ForEach-Object { $_.Line }
```

Expected: exactly one line `dGPU generation resolved: Gen<NN> source=<MarketingName|PciDeviceId> hasDgpu=True`
with `Gen<NN>` equal to step 2. `source=MarketingName` when the name parsed.

Failure observation: `Generation=Unknown` while step 1 clearly showed an RTX 30/40/50 model
means the probe missed a naming shape — record the exact `Name` string and file a bug.

## Step 5 — Confirm the persisted value and fingerprint

```powershell
$cfg = "$env:AppData\MechrevoLite\config.json"
if (-not (Test-Path $cfg)) { $cfg = "$env:ProgramData\MechrevoLite\config.json" }
Get-Content -LiteralPath $cfg -Raw |
  Select-String -Pattern 'dgpu_generation"|dgpu_generation_fingerprint"|dgpu_has_dgpu"' -AllMatches |
  ForEach-Object { $_.Line }
```

Expected: `"dgpu_generation":"Gen<NN>"`, `"dgpu_has_dgpu":"1"`, and a non-empty
`dgpu_generation_fingerprint`.

## Step 6 — Confirm re-resolution only happens on hardware change

```powershell
Stop-Process -Name 'L-Mechrevo'
Start-Sleep -Seconds 2
$before = (Get-Item "$env:AppData\MechrevoLite\log.txt").Length
Start-Process "$PWD\src\MechrevoLiteWin\bin\x64\Debug\net10.0-windows10.0.19041.0\L-Mechrevo.exe"
Start-Sleep -Seconds 15
$new = Get-Content -LiteralPath "$env:AppData\MechrevoLite\log.txt" | Select-Object -Skip 0
($new | Select-String 'dGPU generation resolved').Count
```

Expected: **0** new `dGPU generation resolved` lines on the second start (fingerprint
unchanged -> cached value reused). If the GPU is added/removed, the fingerprint changes and
exactly one new line appears.

## Step 7 — Confirm 30-series gating (only on a 30-series machine)

On a **Gen30** machine, in the app log, trigger a GPU mode restart (Settings -> GPU mode).
```powershell
Select-String -LiteralPath "$env:AppData\MechrevoLite\log.txt" -Pattern 'DGPU_DIRECT_CONNECT_RESTART|IGPU_ONLY_CONNECT_RB' |
  Select-Object -Last 5 | ForEach-Object { $_.Line }
```

Expected: **no** `DGPU_DIRECT_CONNECT_RESTART` and **no** `IGPU_ONLY_CONNECT_RB_*` is ever
published on a Gen30 machine (the matrix marks both `ProvenAbsent` for 30), even if the
service capability bits claim otherwise.

On a 40/50-series machine the same step may legitimately show those actions.

## Step 8 — Confirm no firmware variable was written by the console

```powershell
Select-String -LiteralPath "$env:AppData\MechrevoLite\log.txt" -Pattern 'OemDisplayMode|SetFwVars' |
  Measure-Object | Select-Object -ExpandProperty Count
```

Expected: `0`. The console is MQTT-only for display routing; it never writes the firmware
variable.

Failure observation: any hit means the MQTT-only guardrail was broken -> stop and escalate.
