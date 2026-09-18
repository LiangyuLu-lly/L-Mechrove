# T32 first real-machine runbook — keyboard lighting restored after resume

Task: `fix(keyboard): restore keyboard lighting effect after resume`.

Field bug (#4 耀世16u): after sleep/resume the keyboard backlight effect is gone. The resume
chain is `Program.cs:1469 → 1474-1488 → 649`; the restore path now force-restarts the effect
thread (a still-`IsAlive` thread does **not** mean the light is on — the firmware may have left
frame mode) and only when the user left the light on.

Unit/E2E proof is under fakes (`KeyboardLightingResumeTests`). This runbook is the real-machine
confirmation. Run in **Windows PowerShell 5.1**. Do not kill `L-Mechrevo`.
`$log = "$env:AppData\MechrevoLite\log.txt"`.

---

## Step 0 — Baseline: keyboard light ON

Turn the keyboard backlight on in the app (choose any effect, not "off"). Confirm the light is
lit, then:

```powershell
Select-String -LiteralPath $log -Pattern 'RGB 自动恢复|KbPowerOn|HID 效果' | Select-Object -Last 3
```

Record the current mode number (`rgb.KbHidMode`).

## Step 1 — Suspend

Put the machine to sleep (`powercfg /h off` is NOT needed; use Start -> Sleep, or
`rundll32.exe powrprof.dll,SetSuspendState 0,1,0`).

## Step 2 — Resume and confirm exactly one forced restore

Wake the machine. Within ~5 s:

```powershell
Select-String -LiteralPath $log -Pattern 'RGB 自动恢复|StartMode|HID 效果|灯效开机恢复' | Select-Object -Last 8
```

Expected:
- exactly **one** `RGB 自动恢复：HID 效果 <mode> 已运行` line for this resume;
- the keyboard light is lit again with the previous effect;
- no second restore line until the machine sleeps/wakes again.

Failure observation: the light stays dark and the log shows no `HID 效果 … 已运行` after resume
— capture the log window (look for `RGB 唤醒恢复失败`, `RGB probe`, `未找到兼容的`).

## Step 3 — Resume with the light OFF must stay off

Turn the keyboard backlight off in the app, confirm it is dark, sleep, and wake. Then:

```powershell
Select-String -LiteralPath $log -Pattern '用户已关闭键盘灯|powerstatus' | Select-Object -Last 8
```

Expected:
- a `RGB 唤醒恢复：用户已关闭键盘灯，不重放效果` line;
- the keyboard stays **dark** (no `powerstatus=1` frame for `Keyboard/Ctrl`);
- no `HID 效果 … 已运行` line.

Failure observation: the light comes on after resume with the light switched off — that is the
exact regression this task guards.

## Step 4 — Fast repeat wake must not double-restore

Sleep/wake twice within ~10 s. Expected: each wake produces at most one restore line; a wake
arriving while a restore is still pending is dropped (`_resumeKeyboardRestorePending`), not
double-applied.

## Step 5 — Other channels unaffected

Confirm lightbar / logo still restore on resume (they share the coordinator):

```powershell
Select-String -LiteralPath $log -Pattern 'HidLightbar|HIDLightbar_Logo' | Select-Object -Last 5
```

Expected: each external channel restores once per cycle; no runaway repeat.
