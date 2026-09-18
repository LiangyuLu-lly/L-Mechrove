# T18 first real-machine verification runbook — iGPU-only retry / rollback semantics

Task: `fix(gpu): match vendor iGPU-only retry and rollback semantics`.

Vendor contract (50-series console, CCUWinUI `53527-53729`): retry the iGPU-only request every
2 s, give up after `count > 60` (~122 s), additionally resend every 4th poll, and treat
`CheckDGpuStatusforIGpuOnlyOnSuccess == 2` as ON-success / `== 1` as OFF-success; on timeout
publish `IGPUONLYCONNECTIONSWITCH_STATUS` with the pre-switch switch value.

The pure semantics, the service wiring and the timeout rollback are proven under fakes. This
runbook is the real-machine confirmation that the vendor GCU service agrees.

Run in **Windows PowerShell 5.1**. Do not kill `L-Mechrevo`.
`$log = "$env:AppData\MechrevoLite\log.txt"`.

---

## Step 1 — Start from a known state

```powershell
Get-Service GCUBridge | Select-Object Status
Select-String -LiteralPath $log -Pattern 'SwitchGpuMode|IGPU_ONLY_CONNECT_RB' | Select-Object -Last 3
```

Expected: service Running; last known mode recorded. Start the app if not running.

## Step 2 — iGPU-only ON that the service accepts

Switch to iGPU-only in the UI (on AC power). Then:

```powershell
Select-String -LiteralPath $log -Pattern 'IGPU_ONLY_CONNECT_RB_ON|CheckDGpuStatus|confirmed=True|elapsed=' |
  Select-Object -Last 10 | ForEach-Object { $_.Line }
```

Expected:
- `IGPU_ONLY_CONNECT_RB_ON` with `SetToWMIEC=OK`;
- polls stay ~2 s apart (`IGPU_ONLY_CONNECT_RB_ON` resent every 4th poll);
- `confirmed=True` once the status frame reports `CheckDGpuStatusforIGpuOnlyOnSuccess == 2`.

## Step 3 — The same ON request must be bounded at ~122 s

For a machine that will not reach result `2` (or by forcing a failure), watch the timing:

```powershell
Select-String -LiteralPath $log -Pattern 'Hot switch not confirmed|elapsed=' | Select-Object -Last 5
```

Expected: the attempt ends at most ~122 s after it started (61 polls × 2 s), never loops forever.
Then the rollback line appears (step 4).

## Step 4 — Timeout rolls back to the pre-switch value

```powershell
Select-String -LiteralPath $log -Pattern 'rolling back to pre-switch mode|IGPUONLYCONNECTIONSWITCH_STATUS|GETSTATUS' |
  Select-Object -Last 6 | ForEach-Object { $_.Line }
```

Expected: `IGPUONLYCONNECTIONSWITCH_STATUS` with `Status` = the value for the mode **before** the
attempt (`RB_OFF`=0, `RB_ON`=1, `RB_AUTO`=2), followed by a `GETSTATUS` refresh. The UI must end
up showing the old mode, not the failed target.

## Step 5 — OFF success criterion

Switch from iGPU-only back to standard/hybrid and confirm the log shows the switch completing
through the OFF path (`CheckDGpuStatusforIGpuOnlyOnSuccess == 1` when the field is present).
Because leaving iGPU-only is carried by the restart route, the observable here is: the machine
returns to the requested route after the restart, with no rollback line.

## Step 6 — Failure/edge cases to record

1. **AC vs battery (AUTO)**: set GPU mode to AUTO. On AC the runtime should become 1; on battery 2.
   Record the `runtime=…/expected=…` line from `SwitchGpuMode confirmed=…`.
2. **Rollback must not fire on success**: after a successful switch there must be **zero**
   `IGPUONLYCONNECTIONSWITCH_STATUS` lines.
3. **No EC write**: `Select-String -LiteralPath $log -Pattern 'ECWRITE|0x9C40A48C' | Measure-Object`
   must be `0`.

Failure observation for any step: a switch that reports success while the status field disagrees,
or a timeout with no rollback, is a bug — capture the log window and escalate.
