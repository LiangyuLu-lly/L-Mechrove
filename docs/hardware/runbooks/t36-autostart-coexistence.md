# T36 first real-machine runbook — reboot autostart + vendor-console coexistence

Task: `fix(startup): make reboot autostart reliable and warn on console coexistence`.

Field bug (#10 耀世16u): the app does not start after a reboot. Autostart is a **user-level
scheduled task** (`Helpers/Startup.cs`; the task name is `LMechrevo_<SID>`), never a service and
never `schtasks.exe` — it uses the managed Task Scheduler API. A transient image path (under
`%TEMP%`) is never registered.

Round 5 correction: the earlier "uninstalling the official console reports SEVERE → coexistence
conflict" was a mis-inference (that line was an example, not a real defect). The console must
**prompt** the user to remove the vendor console; the GCU service takeover (uninstall → install
ours → rollback guardrails) is the installer's job.

Run in **Windows PowerShell 5.1**. Do not kill `L-Mechrevo`.
`$log = "$env:AppData\MechrevoLite\log.txt"`.

---

## Step 1 — The task exists and matches the current user/image

```powershell
schtasks /query /fo LIST /v | Select-String -Pattern 'LMechrevo|TaskName' | Select-Object -First 6
```

Expected: a task named `LMechrevo_<your SID>` pointing at the **installed** `L-Mechrevo.exe`
(not a `%TEMP%` path), `LUA` run level, interactive token, LogonTrigger + ConsoleConnect trigger.

## Step 2 — Repair when the task is missing but the user enabled autostart

Delete the task, then start the app (the app repairs on launch when the quick switch is ON):

```powershell
schtasks /delete /tn "LMechrevo_<your SID>" /f    # 删掉任务
Start-Process "<install dir>\L-Mechrevo.exe"
Start-Sleep -Seconds 20
schtasks /query /fo LIST /v | Select-String 'LMechrevo' | Select-Object -First 3
```

Expected: the task is **rebuilt** automatically (because the user setting is enabled). If it is
not rebuilt, capture `Select-String -LiteralPath $log -Pattern 'startup|Startup'`.

## Step 3 — Reboot and confirm it actually starts

Reboot the machine. After logon (allow ~30 s for the trigger):

```powershell
Get-Process L-Mechrevo -ErrorAction SilentlyContinue | Select-Object Id, StartTime
```

Expected: the process is running and `StartTime` is after the boot time. This is the real-machine
half; it is recorded as BLOCKED-HW until run on the affected model (see
`.omo/evidence/t36-reboot-status.json`).

## Step 3a — The installer owns the elevated task (N5)

The task is created by the elevated installer (`Install-Gcu.ps1`), not by the app: an unelevated app
cannot create a highest-privileges task. The app is permanently elevated by owner decision; the
installer is the only place elevation is obtained.

```powershell
$sid = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
schtasks /query /tn "LMechrevo_$sid" /xml | Select-String 'RunLevel|Command|Arguments'
```

Expected: `<RunLevel>HighestAvailable</RunLevel>`, `<Command>` = the installed `L-Mechrevo.exe`, and
no `<Arguments>` element. Manual launch and post-reboot launch must both show **no UAC prompt**.
After uninstall, `schtasks /query /tn "LMechrevo_$sid"` must report the task is gone.

## Step 3b — A failed registration is visible (permission root cause)

The repair path no longer deletes the task before recreating it: the old `UnSchedule(); Schedule();`
deleted first, so a denied re-register (insufficient rights / task in use) left the user with **no**
autostart entry — and the failure went only to a log that is off by default. Registration is now an
in-place overwrite, and a failure while autostart is enabled raises a **tray balloon**.

```powershell
Select-String -LiteralPath $log -Pattern 'Autostart registration failed' | Select-Object -Last 3 | ForEach-Object { $_.Line }
schtasks /query /fo LIST /v | Select-String 'Last Result|上次结果'
```

Expected: no balloon / no matching log line on a healthy launch. If the balloon appears, the entry
is absent or stale — record the balloon text and the log line, then re-run Step 2.

## Step 4 — Transient image must not register

Run the app from a temp build output and toggle autostart on:

```powershell
schtasks /query /fo LIST /v | Select-String 'LMechrevo'
Select-String -LiteralPath $log -Pattern 'transient|临时'
```

Expected: no task is registered while running from a `%TEMP%` image (and a log line explains why).

## Step 5 — Vendor-console coexistence is a prompt, never a silent delete

```powershell
Get-Service GCUBridge -ErrorAction SilentlyContinue | Select-Object Status, Name
Get-NetTCPConnection -LocalPort 13688 -State Listen -ErrorAction SilentlyContinue |
  Select-Object OwningProcess
```

If a **vendor** service/console is present, expected: the app shows the visible prompt
(`请手动卸载「官方控制台」应用 …`) and never deletes it silently. The GCU service takeover
(backup → uninstall → install ours → verify → rollback) is performed by the installer; verify it
did not leave the machine without a working GCU service:

```powershell
Get-Service GCUBridge | Select-Object Status
Select-String -LiteralPath $log -Pattern 'GCU|takeover'
```

Expected: exactly one GCU service, Running. No firmware/NVRAM write occurs during any of this.
