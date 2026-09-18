# T35 first real-machine runbook — footer overlap + DPI scaling (strict)

Task: `fix(ui): stop footer text/icon overlap and align DPI scaling` (round 5, strictly required).

Field bugs (#8 无界15Xpro ai9H365, #10 耀世16u): footer text/icon rects overlap, and DPI scaling
misaligns the reflow. The acceptance is: **sibling-overlap / footer findings == 0 at every audited
viewport**, footer text/icon rects do not intersect, and the DPI reflow matches the scale factor.

Unit locks: `FooterOverlapDpiTests` (rect non-intersection, occlusion-free, reflow tracks scale,
layout scale never follows the audit paint DPI).

Run in **Windows PowerShell 5.1**. Do not kill `L-Mechrevo`.

---

## Step 1 — Real-surface UI audit (the acceptance gate)

```powershell
powershell -NoProfile -File scripts\test-ui.ps1
$report = Get-ChildItem artifacts\ui-audit-* -Directory | Sort-Object LastWriteTime -Descending |
  Select-Object -First 1 | ForEach-Object { Join-Path $_.FullName 'ui-audit.json' }
$r = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
"issues total      : $($r.IssueCount)"
"sibling-overlap   : $(($r.Issues | Where-Object Kind -eq 'sibling-overlap').Count)"
"footer findings   : $(($r.Issues | Where-Object { $_.Kind -match 'footer' }).Count)"
```

Expected for T35: `sibling-overlap` **0** and `footer findings` **0**. (Other kinds such as
`text-clipping` at 3840x2160 175/200% on other forms are pre-existing and outside this task's
acceptance.)

## Step 2 — Footer rects on the affected machine (1366x768 @125%, 1920x1080 @150%)

Open the app on the affected model, resize to the two affected viewports, and confirm by eye:
- the footer's text label and icon do not overlap;
- the footer controls stay inside the footer band (no clipping at the bottom edge).

If overlap is visible, capture the exact viewport + DPI and the `ui-audit.json` row:
```powershell
$r.Issues | Where-Object { $_.Form -eq 'Settings-Main' } |
  Select-Object Viewport, Kind, Control, Detail | Format-Table -AutoSize
```

## Step 3 — DPI reflow

Change Windows scaling to 125% and 150% (Settings -> Display -> Scale), restart the app, and
re-check step 1. Expected: `sibling-overlap`/`footer` stay 0 at every 1.25x / 1.5x / 2.0x viewport;
footer width follows the scale factor (locked by `TheFooterReflowTracksTheScaleFactor`).

## Step 4 — Regression check: no other footer finding kind

```powershell
$r.Issues | Group-Object Kind | Select-Object Count, Name | Sort-Object Count -Descending
```

Expected: no `footer-occlusion` and no `sibling-overlap` rows. Any such row is a T35 regression —
capture the report and the viewport and escalate.
