# T17 first real-machine verification runbook — display-route switching over MQTT

Task: `feat(gpu): add per-generation display-route switching over MQTT`.

The command layer is proven under fakes (topic/action/payload, per-generation vocabulary,
800 ms restart delay). This runbook is the real-machine confirmation that the vendor GCU
service actually reacts to those MQTT messages.

Run in **Windows PowerShell 5.1** on the target machine. Do not kill `L-Mechrevo`; close it
normally. Keep the GCU service running (the whole point is that **the service** performs the
hardware write; the console only speaks MQTT).

All observations are read from the app log:
`$log = "$env:AppData\MechrevoLite\log.txt"`.

---

## Step 0 — Preconditions

```powershell
Get-Service GCUBridge | Select-Object Status, Name
Get-NetTCPConnection -LocalPort 13688 -State Listen -ErrorAction SilentlyContinue |
  Select-Object LocalAddress, LocalPort, OwningProcess
```

Expected: service `Running`; one listener on 13688. If not, fix the GCU install before continuing.

## Step 1 — Record the detected generation

```powershell
(Get-CimInstance Win32_VideoController | Where-Object { $_.Name -match 'NVIDIA' }).Name
Select-String -LiteralPath $log -Pattern 'dGPU generation resolved' | Select-Object -Last 1
```

Expected: the log generation matches the model number (`30xx`->Gen30, `40xx`->Gen40, `50xx`->Gen50).
Use it to pick the expected branch below.

## Step 2 — Gen40/Gen50: force iGPU-only, confirm the MQTT action and read-back

In the app: Settings -> GPU mode -> "iGPU only" (核显直连/switchable). Then:

```powershell
Select-String -LiteralPath $log -Pattern 'IGPU_ONLY_CONNECT_RB_ON|CheckDGpuStatus|confirmed=True' |
  Select-Object -Last 8 | ForEach-Object { $_.Line }
```

Expected:
- exactly the vendor action `IGPU_ONLY_CONNECT_RB_ON` with `SetToWMIEC=OK` is sent;
- the console polls `GETSTATUS` every 2 s (first retry of the target after poll 0, then every
  4th poll) up to 61 polls (~122 s);
- on success, `CheckDGpuStatusforIGpuOnlyOnSuccess` reads `2` for ON (`1` for OFF) and the log
  shows `confirmed=True`.

Failure observation: after ~122 s without success, the log must show
`IGPUONLYCONNECTIONSWITCH_STATUS` with the **pre-switch** status value, then a `GETSTATUS`
refresh — that is the official rollback. Any other behaviour is a bug.

## Step 3 — Gen40/Gen50: direct-connect restart, confirm the 800 ms delay

Switch to "direct connect" (独显直连). Then:

```powershell
Select-String -LiteralPath $log -Pattern 'DGPU_DIRECT_CONNECT_TOGGLE|DGPU_DIRECT_CONNECT_RESTART' |
  Select-Object -Last 6 | ForEach-Object { $_.Line }
```

Expected: the target toggle(s) first, then `DGPU_DIRECT_CONNECT_RESTART` as the **last** action.
The machine restarts and boots in the requested route. (The 800 ms pre-restart delay is covered
by the unit tests; here confirm the ordering and that the restart actually happens.)

## Step 4 — Gen30: confirm nothing iGPU-only / restart is ever sent

On a **Gen30** machine, request iGPU-only or direct-connect in the UI, then:

```powershell
Select-String -LiteralPath $log -Pattern 'rejected: action not in dGPU generation|refusing to publish|IGPU_ONLY|DGPU_DIRECT_CONNECT_RESTART' |
  Select-Object -Last 8 | ForEach-Object { $_.Line }
```

Expected:
- a `SwitchGpuMode(...) rejected: action not in dGPU generation Gen30 vocabulary` (or
  `refusing to publish DGPU_DIRECT_CONNECT_RESTART`) line;
- **zero** `IGPU_ONLY_CONNECT_RB_*` and **zero** `DGPU_DIRECT_CONNECT_RESTART` lines;
- the machine does **not** reboot.

This is the intended fail-closed behaviour: 30-series payloads contain no `IGPU_ONLY_*` and no
`*_RESTART` (T16 matrix `ProvenAbsent`), so the console refuses instead of booting for nothing.

## Step 5 — Confirm the console stayed MQTT-only (no firmware variable write)

```powershell
Select-String -LiteralPath $log -Pattern 'OemDisplayMode|SetFwVars' | Measure-Object |
  Select-Object -ExpandProperty Count
```

Expected: `0`. All display-route traffic above went out on the MQTT topic `Setting/Control`;
the console never wrote the `OemDisplayMode` firmware variable.

If you have the W4 read-only probe available, capture MQTT for the switch and confirm every
frame is on `Setting/Control` — no `EC/Control`, no direct NVRAM call from the console.

## Step 6 — Confirm no EC write was introduced

```powershell
Select-String -LiteralPath $log -Pattern 'ECWRITE|0x9C40A48C|WritePort' | Measure-Object |
  Select-Object -ExpandProperty Count
```

Expected: `0`. The only permitted EC write in the product remains the battery charge-limit path.
