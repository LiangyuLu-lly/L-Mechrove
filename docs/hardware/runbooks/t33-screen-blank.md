# T33 first real-machine runbook — screen blank must fail loudly

Task: `fix(display): make screen blank fail loudly instead of silently`.

Field bug (#6 耀世15pro4060): "息屏" (blank-without-sleep) silently does nothing. The controller
uses the panel brightness (`WmiMonitorBrightnessMethods`) plus
`SetThreadExecutionState(ES_SYSTEM_REQUIRED|ES_DISPLAY_REQUIRED)` — it never broadcasts
`SC_MONITORPOWER` (that puts Modern-Standby machines to sleep). It now reports
`ScreenBlankOutcome` and `LastFailureReason`; the UI logs `SCREEN_BLANK_FAILED:` and warns.

Run in **Windows PowerShell 5.1**. Do not kill `L-Mechrevo`.
`$log = "$env:AppData\MechrevoLite\log.txt"`.

---

## Step 0 — Is a brightness interface present?

```powershell
Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorBrightness -ErrorAction SilentlyContinue |
  Select-Object InstanceName, CurrentBrightness
```

- Values shown -> go to step 1 (happy path).
- Nothing shown -> go to step 4 (fail-loud path).

## Step 1 — Blank: exactly one dim + held awake

Click the "息屏" (monitor off) switch once. Then:

```powershell
Select-String -LiteralPath $log -Pattern 'Screen blank' | Select-Object -Last 5
```

Expected: exactly one `Screen blank: dimmed to 0% (was <n>%); system held awake
(ES_SYSTEM_REQUIRED|ES_DISPLAY_REQUIRED).`; the panel goes dark; the machine does **not** sleep
(check Event Viewer Kernel-Power 506/507/566 do **not** appear within ~2 s).

## Step 2 — Input restores

Move the mouse / press a key. Expected:
`Screen blank: restored brightness to <n>% (user input).` and the panel is back at the previous
brightness.

## Step 3 — Watchdog restores

If no input arrives, after 30 minutes:
`Screen blank: restored brightness to <n>% (watchdog).` The panel never stays black forever.

## Step 4 — Fail-loud on a machine without the WMI brightness interface

On the 4060 machine that exhibits the bug (step 0 shows nothing), click the switch. Then:

```powershell
Select-String -LiteralPath $log -Pattern 'SCREEN_BLANK_FAILED|Screen blank failed' | Select-Object -Last 5
```

Expected:
- a `SCREEN_BLANK_FAILED: brightness read failed (no WMI instance)` (or
  `brightness write to 0% failed`) line;
- a visible warning dialog (`息屏未能生效（屏幕亮度接口不可用）…`) — visible prompt, not silence;
- the switch springs back to OFF and **no** config is written;
- the app never claims the screen is blanked.

Failure observation: the switch silently does nothing and no `SCREEN_BLANK_FAILED` line appears —
that is the exact regression this task guards.

## Step 5 — No regression to sleep behaviour

Confirm the app never broadcasts `SC_MONITORPOWER` (grep the log for `WM_SYSCOMMAND` /
`SC_MONITORPOWER` -> 0 hits) and the machine stays awake while blanked.
