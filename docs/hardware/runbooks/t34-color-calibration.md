# T34 first real-machine runbook — colour-profile switching must take effect or fail visibly

Task: `fix(display): make colour-profile switching take effect or fail visibly`.

Field bug (#7 耀世15pro4060): switching to sRGB (or P3/AdobeRGB) does nothing — the UI shows
success while the panel never changes. Contract: success means **read-back == request**
(on: profile index, sRGB = 2; off: switch), and HDR blocks the on-direction with a visible
failure instead of a silent no-op.

Run in **Windows PowerShell 5.1**. Do not kill `L-Mechrevo`.
`$log = "$env:AppData\MechrevoLite\log.txt"`.

---

## Step 1 — HDR must be off for the on-direction

```powershell
Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\VideoSettings' -ErrorAction SilentlyContinue
```

Or check the app's display page: HDR off. Expected: switching to sRGB works. If HDR is on, go to
step 4.

## Step 2 — Switch to sRGB and read back

In the app choose the sRGB profile. Then:

```powershell
Select-String -LiteralPath $log -Pattern 'SetColorCalibration' | Select-Object -Last 5
```

Expected:
- `SetColorCalibration(mode 2) -> COLOR_CALIBRATION_ON_SRGB`;
- `SetColorCalibration confirmed=True expectedMode=2 actualMode=2 ...` (read-back 2);
- the panel visibly changes to the sRGB gamut.

Failure observation: `confirmed=False` with `actualMode` != 2 — the console must report failure
(no silent success), and the profile list must not show sRGB as current.

## Step 3 — Off and profile round-trip

Turn calibration off, then back to sRGB, then to P3:

```powershell
Select-String -LiteralPath $log -Pattern 'COLOR_CALIBRATION_OFF|confirmed=' | Select-Object -Last 8
```

Expected: `COLOR_CALIBRATION_OFF` with `FileName` = the profile that was on; each switch
confirms only when the read-back matches. A second switch to the same profile logs
`already applied` and publishes nothing.

## Step 4 — HDR on: must fail visibly, never fake success

Turn HDR on, then try to select sRGB. Expected:
- `SetColorCalibration(mode 2) blocked: HDR is enabled` in the log;
- **zero** `COLOR_CALIBRATION_ON_*` frames published;
- the UI reports a failure / keeps the current profile (no silent success).

## Step 5 — Machine without the calibration registry

On a machine where `HKLM\SOFTWARE\OEM\GamingCenter2\MySetting\DisplayFeatures` is absent, the
switch must report failure and never claim a profile was applied:

```powershell
Select-String -LiteralPath $log -Pattern 'SetColorCalibration' | Select-Object -Last 3
```

Expected: a failure (no `confirmed=True`), and no profile shown as current.
