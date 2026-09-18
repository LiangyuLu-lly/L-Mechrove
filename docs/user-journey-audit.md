# User-journey audit — L-Mechrevo, from a normal user's point of view

Read-only audit. No `src\`, `tests\`, `installer\` or `release\` file was modified; this report is
the only artifact. Every row cites a real `file:line` or a concrete UI location. `UNVERIFIED` is
used where the exact runtime behaviour could not be confirmed from the artifacts.

Severity: **High** = feature unusable / data-loss-like / hard dead end; **Medium** = confusing or
recoverable-only-with-help; **Low** = cosmetic / minor friction.

## 1. Getting the installer

| # | Problem | Where (file:line) | How the user experiences it | Severity | Fix |
|---|---|---|---|---|---|
| 1.1 | Setup EXE is unsigned; no `SignTool` in `[Setup]` | `installer\L-Mechrevo.iss:44-79`; `installer\README.md:209-211` says the staged app is unsigned | Windows SmartScreen "Unknown publisher / Windows protected your PC" — many users stop here. | High | Sign setup + app; publish the cert/instructions. |
| 1.2 | The installer filename embeds `beta18` | `installer\L-Mechrevo.iss:60` `OutputBaseFilename={#AppName}-{#AppLabel}-setup`; `:26-28` label `beta18`; `:21` version `0.289.0-beta18` | File is `L-Mechrevo-beta18-setup.exe`; users read "beta" as unstable. | Low | Drop the beta token from release installers. |
| 1.3 | No publisher/support URL in file metadata | `installer\L-Mechrevo.iss:18,48,75-77` `L-Mechrevo contributors`, no `AppPublisherURL`/`AppSupportURL` | Properties show no vendor/support link. | Low | Add URL metadata. |
| 1.4 | Download size not stated anywhere user-visible | `installer\README.md:203-206` ("~120 MB bundled source"); actual size only in build `artifacts` (`Build-Installer.ps1:232-234`) | User cannot judge the download. | Low | Put the measured size in release notes. |

## 2. Installing

| # | Problem | Where (file:line) | How the user experiences it | Severity | Fix |
|---|---|---|---|---|---|
| 2.1 | GCU payload selection is **fatal** for unsupported/undeterminable machines | `installer\L-Mechrevo.iss:139` (`[Run]`, no `ignoreerrors`) → `installer\Select-GcuPayload.ps1:221` throws `"cannot determine the NVIDIA dGPU generation (30/40/50) … refusing to install the GCU payload - no fallback…"` | Files are copied then setup rolls back with a **generic** error; the real reason is only in the log. Non-NVIDIA/unsupported users cannot install at all. | High | Gate at wizard start with a translated message, or make GCU optional with a visible "hardware service unavailable" state. |
| 2.2 | The long GCU step runs hidden; one generic status line | `installer\L-Mechrevo.iss:87-88` status text, `:139` `Flags: runhidden waituntilterminated`; `Install-Gcu.ps1:50` `Write-Host` suppressed | No progress, no pass/fail detail. | Medium | Pipe script milestones to Inno `StatusMsg` / show a summary on failure. |
| 2.3 | Failure reason is log-only; no FATAL detail in the dialog | `Install-Gcu.ps1:843-851` `FATAL: post-install verification failed: …` / `FALLBACK:`; status json `:500-514,840` | Setup says "failed"; the actionable reason lives in `%ProgramData%\L-Mechrevo\logs\gcu-install-*.log`. | High | On exit 1, show the failed check + log path. |
| 2.4 | Vendor-console deletion is silent (log-only, no consent) | `Install-Gcu.ps1:791-804` `[2/8] removing the vendor official console…`; `:361-443` `Remove-VendorConsole` (removes Appx packages, deletes `{ProgramFiles}\L-Mechrevo\GCU*` dirs and Run/RunOnce values) | The user's official Control Center disappears with no pre-notice. | High | Add an explicit consent page naming what will be removed. |
| 2.5 | The deletion is irreversible; the app's "restore" cannot bring the console back | delete: `Install-Gcu.ps1:381,410`; app restore only restores startup entries/files/tasks: `OfficialConsoleIsolation.cs:715-822`; UI still offers `Settings.cs:2410` `恢复官方控制台` | User clicks "restore", the app is never reinstalled — a misleading dead end. | Medium | Rename to "恢复官方控制台启动项" or keep an installer for real reinstall. |
| 2.6 | AppData ACL grant resolves the **elevating** account's profile | `Install-Gcu.ps1:868-870` `$ConfigDir = Join-Path $env:APPDATA 'MechrevoLite'` (comment acknowledges admin-install caveat) | Standard user who elevates with a different admin account gets ACLs on the wrong profile. | Low | Pass the invoking user's AppData, or drop the grant. |
| 2.7 | No offline .NET runtime fallback | `installer\L-Mechrevo.iss:91-96` `RuntimeRequired=…`; `installer\README.md:69` "No offline fallback exists" | Air-gapped users cannot install. | Medium | Document the prerequisite / offer an offline bundle. |

## 3. First launch

| # | Problem | Where (file:line) | How the user experiences it | Severity | Fix |
|---|---|---|---|---|---|
| 3.1 | **The first-run guide never tells the user to install the vendor console** — it explicitly forbids it. This is correct. | `FirstRunGuideForm.cs:89-90` `"无需安装任何其他控制台"` / `"…厂商「官方控制台」已由安装器清理并替换为 L-Mechrevo，不需要再下载或安装它。"`; `:113` `// N10: no "official download" button` | — | — | Keep. |
| 3.2 | The guide's primary button `前往系统页` is a **no-op** | `FirstRunGuideForm.cs:116-120` sets `DialogResult.OK`; `Settings.cs:2811-2812` calls `SelectDashboardPage(2, persist:true)`; `Settings.cs:2819-2821` body is empty | Clicking the main CTA does nothing; the user thinks it is broken. | Low | Remove the dead navigation or scroll to the System section. |
| 3.3 | Guide is shown only once, and only on a non-startup launch (or after first tray restore) | `Settings.cs:2803-2813`; `Program.cs:450`, `:1540-1544` | Autostart users see it only when they first open the window. Acceptable. | Low | — |
| 3.4 | The published README contradicts the shipped product | published README (copy at `%TEMP%\opencode\README.md`): "程序会提示可以隔离…（不会卸载官方控制台，GCU 后台服务保留）", "自带 .NET 运行时", "不依赖管理员权限"; vs `Install-Gcu.ps1:361-443` (deletes console), `installer\README.md:69` (no bundled runtime), `Install-Gcu.ps1:632` (`-RunLevel Highest`) | Users read one contract; the installer does another. | High | Regenerate the README from actual beta18 behaviour. |
| 3.5 | The dead coexistence prompt, if ever enabled, would also contradict the installer | `GcuCoexistence.cs:46-50` `"…请手动卸载「官方控制台」应用…"` — zero callers | Currently inert; still a latent contradiction. | Low | Delete or rewrite to match the installer. |

## 4. Model identification

| # | Problem | Where (file:line) | How the user experiences it | Severity | Fix |
|---|---|---|---|---|---|
| 4.1 | Recognized/service-served machine is **not** locked read-only | `RuntimeModelSupport.cs:35-42`; `ModelSupport.cs:68-77` (service-served criterion) | Good. | — | Keep. |
| 4.2 | **EC-unreadable (`Unparsable`) machine gets no notice and no manual override** — a hard dead end | `RuntimeModelSupport.cs:28-31` returns `Unparsable`; `IsPositivelyUnsupported` only true for `NotInSet` (`:52-53`); banner only for `unsupported` (`Settings.cs:3019`); the override UI is built only inside the banner (`Settings.cs:2570,3033-3113`) | If the EC/ACPIDriver read fails, the user sees no read-only notice and cannot reach the manual override. | High | Show the banner for `Unparsable` too, with "identity unreadable" wording. |
| 4.3 | The unrecognized-machine reason is readable, but the override requires undocumented knowledge | reason `Settings.cs:3018` `"当前机型不在支持列表（识别到 {ProjectId}），已进入只读模式。"`; caption/buttons `Settings.cs:3067,3092-3093` | The user must know a valid 24-code ProjectID; there is no list or dropdown. | Medium | Offer a dropdown of supported codes or a "报告此机型" link. |
| 4.4 | Invalid override status leaks an English enum into a Chinese UI | `Settings.cs:3098` `… $"无效（{decision.Reason}）"` renders `无效（NotInSet）` | Confusing internal identifier shown to the user. | Low | Localize the reason. |
| 4.5 | The manual override allowlist is stricter than the automatic path | `ModelOverrideStateMachine.cs:48-58` accepts only the 24-code `PlatformCodeSet`, while auto uses service-served (`ModelSupport.cs:68-77`) | A machine the service serves but whose code is outside the 24 cannot be manually pinned even if the user wants to. | Medium | Make the allowlist match the auto criterion. |

## 5. Feature availability (at a glance)

| # | Problem | Where (file:line) | How the user experiences it | Severity | Fix |
|---|---|---|---|---|---|
| 5.1 | Unsupported features are **hidden with no per-item reason** | `Settings.cs:2831` `Show(bool supported) => audit || supported;`; e.g. `:2924-2930`, `:2937-2947`; the only help is one generic line in `用前必看.txt:14` `某项功能未显示时，通常表示当前机型不支持该功能。` | Users cannot distinguish "not supported" from "service not connected" from "bug". | Medium | Add a disabled-with-reason state or per-section tooltip. |
| 5.2 | The `*Seen` latch over-gates features the vendor exposes unconditionally | USB charge vendor-unconditional (`MySettingManager.cs:1016-1029`) vs ours hidden unless `UsbChargerSeen` (`MechrevoHw.cs:720`); same for touchpad (`:712`) and OSD (`:719`) | On a connected machine with an unexpected frame, features silently never appear. | Medium | Only hide when the service explicitly reports "not supported". |
| 5.3 | Charge-limit control is always shown and can silently do nothing | panel `Settings.cs:2963` (`EnableSection(panelBattery, true)`); slider only disabled on `NotInSet`; write path `EcChargeLimit.cs:113-114` | A user with the service disconnected moves the slider; nothing happens, no message. | High | Disable-with-reason when the support decision is not `Ok`, and surface write failures. |
| 5.4 | One good disabled-with-reason precedent exists but is not generalized | `Settings.cs:2901-2905` (keyboard controller status shown only when `FeatureAvailability.Unsupported`) | Inconsistent UX. | Low | Reuse the pattern. |
| 5.5 | Tray/context-menu "键盘灯效" is ungated | `Settings.cs:3790` `AddAction("键盘灯效", false, () => OpenRgbForm())` | Opens a lighting editor on machines the dashboard says have no keyboard lighting. | Medium | Gate by the same `keyboard` predicate. |

## 6. Daily use (mode / fan / lighting / GPU / charge)

| # | Problem | Where (file:line) | How the user experiences it | Severity | Fix |
|---|---|---|---|---|---|
| 6.1 | Mode, fan, battery and lighting commands are MQTT-only; when disconnected most callers only log | `docs\gcu-dependency-matrix.md` §5 lists the silent paths; e.g. `MechrevoHw.cs:2455` throws `MqttPublishFailedException`, upper layers catch+log | Clicking a control does nothing and no message appears. | High | Surface a single "GCU not connected" state in the UI and disable the controls. |
| 6.2 | GPU mode switch confirmation is brittle and can silently fail | `MechrevoService.cs:1241` logs `SwitchGpuMode confirmed=False…`; `:2549` `SetGpuMode not confirmed…`; see `docs\vendor-parity-audit.md` for the field mismatch | "切换核显没反应 / 重启后没有切换" (#13/#16/#9). | High | Fix the confirmation field/value per service build; show a clear success/failure. |
| 6.3 | Every GPU switch forces a restart | `GpuSwitchPolicy.cs:34-35`; used `Settings.cs:4368` | A simple hybrid↔dGPU toggle always reboots the machine, unlike the vendor console. | Medium | Restore vendor route selection (hot-swap where supported). |
| 6.4 | Charge-limit feedback is honest only after the fact | `Settings.cs:5077,5085-5088` (`无法读取`); `EcChargeLimit.cs:136-157` rollback | A failed write reverts but the user may not notice why. | Low | Toast the rollback reason. |

## 7. Reboot / sleep / wake

| # | Problem | Where (file:line) | How the user experiences it | Severity | Fix |
|---|---|---|---|---|---|
| 7.1 | **Installer autostart task ≠ app's plan** → the app rewrites/downgrades it | installer `Install-Gcu.ps1:632` `-RunLevel Highest`; `:628-631` two `New-ScheduledTaskTrigger -AtLogOn` (comment claims ConsoleConnect); app `Startup.cs:321` requires `LUA`, `:336-346` requires a `SessionStateChangeTrigger` `ConsoleConnect` | On first launch the app finds the task "doesn't match plan" and re-registers it at LUA (`Startup.cs:279-286,309-314`); if that fails only a tray balloon appears. The documented "Highest task" never survives. | High | Make the installer create exactly the app's plan, or make the app accept the installer's task. |
| 7.2 | Autostart depends on the GCU install step succeeding | `Install-Gcu.ps1:858-862` `[8/8] … Register-AutostartTask` | An aborted GCU step leaves no autostart task. | Medium | Create the task independently of the GCU payload. |
| 7.3 | Missing MQTT firewall rule is log-only and logs default OFF | `Program.cs:384-387`; warning text `MqttSecurity.cs:151-154`; default `Off` `Logger.cs:240-245` | A GPO/third-party firewall change silently disables the security warning. | Medium | Tray balloon / one-click fix. |
| 7.4 | Duplicate launch exits silently | `ProcessHelper.cs:77-79`; `Program.cs:287-291` | Double-clicking the desktop icon appears to do nothing (window hidden in tray). | Low | Bring the existing instance's window up instead of exiting. |
| 7.5 | Sleep/wake keyboard restore is best-effort but sound | `Program.cs:1462-1483`, `:1487-1513`; off-state guard `:1499-1502` | Field bug #4 addressed; residual failures are silent (only logged). | Low | Surface if restore fails after the bounded retries. |

## 8. Uninstall

| # | Problem | Where (file:line) | How the user experiences it | Severity | Fix |
|---|---|---|---|---|---|
| 8.1 | Driver removal is best-effort; no reboot notice | `L-Mechrevo.iss:142-144`; `Uninstall-Gcu.ps1:109-111` `WARNING: driver removal returned {0} (may require reboot)` | A driver may survive until reboot with no explanation. | Low | Tell the user a reboot may be required. |
| 8.2 | Leftover registry marker values | `Install-Gcu.ps1:682-691` writes six values; `Uninstall-Gcu.ps1:139` removes four (omits `GcuPayloadSha256`, `GcuInstallerVersion`) | Stale values under `HKLM\SOFTWARE\L-Mechrevo`. | Low | Remove all values. |
| 8.3 | Isolation snapshot survives uninstall | path `OfficialConsoleIsolation.cs:113-115`; `L-Mechrevo.iss:146-153` deletes only `{commonappdata}\L-Mechrevo\logs` + GCU subdirs | State file left on disk. | Low | Add it to `[UninstallDelete]`. |
| 8.4 | No way to restore the vendor console the installer deleted | `Install-Gcu.ps1:361-443`; `Uninstall-Gcu.ps1:146-176` never reinstalls | Uninstalling L-Mechrevo does not bring the original Control Center back. | Medium | Document prominently; offer a vendor reinstall path. |
| 8.5 | **Collateral-deletion risk in the release GCU-only bats** | `release\GCU-only\卸载官方控制台.bat` `for %%P in (CCUWinUI ControlCenterU GamingCenterU GCUUI Tray) do taskkill /f /im %%P.exe` and deletes dirs `AirplaneDriver AistonePowerManagement … logo PreinstallKit …`; `install.bat` overwrites the vendor service `binPath` | A process literally named `Tray.exe` (generic name) is force-killed; broad directory deletion under the OEM root. | Medium | Pin to the vendor install root + explicit file list; drop bare `Tray`. |
| 8.6 | Manual `install.bat` failures are a dead end | `release\GCU-only\install.bat` `"[!] Service register FAILED. Screenshot this window and send for help."`, `"[!] Service is not RUNNING. …"` | "Send for help" with no URL/email. | Medium | Add the Issues URL (already in `DiagnosticPackExporter.cs:35`). |

## 9. When something breaks

| # | Problem | Where (file:line) | How the user experiences it | Severity | Fix |
|---|---|---|---|---|---|
| 9.1 | Logging defaults to **OFF** | `Logger.cs:240-245` `_ => LogLevel.Off`; only a 64 KB ring buffer + `crash.txt` exist (`:134-139,374-389`) | A normal bug leaves no `log.txt`; support gets almost nothing. | Medium | Default to `ErrorOnly`. |
| 9.2 | No in-app "open log folder"; the user doc does not give the path | log path `Logger.cs:30-38,53-61` (`%AppData%\MechrevoLite\log.txt`); `用前必看.txt:18` just says "日志" | The user cannot find the log. | Medium | Add "打开日志目录" beside "导出诊断包" and print the path in the guide. |
| 9.3 | Rotation discards history | `Logger.cs:134-136` (10 MB cap, retain 1 MB), `:335-346` | The 1 MB tail can cut a long install/update session. | Low | Archive instead of truncate. |
| 9.4 | Diagnostic pack is solid (positive) | `DiagnosticPackCommand.cs:48-59,77-85`; `DiagnosticPackExporter.cs:29-35,89-117` (privacy note + Issues URL) | Self-service path works. | — | Keep. |
| 9.5 | No USER-facing troubleshooting text is shipped | `用前必看.txt:16-18` is the only shipped help; the FAQ lives in the GitHub README (not installed: `L-Mechrevo.iss:110-111,126-127`); `docs\hardware\first-machine-runbook.md:1-11` is **owner-facing**, confirmed | Offline users get one generic line. | Medium | Ship a local FAQ/HTML or link to Issues from the app. |
| 9.6 | Self-rescue CLI switches are undocumented for users | `Program.cs:200-212` (`--secure-mqtt`), `:214-231` (`--official-isolate/--official-restore`), `:249-255` (`selftest`, logged only, log OFF) | Users cannot discover them. | Low | Document them in `用前必看.txt`. |

## 10. Cross-cutting summary

- **Silent failures (6):** 2.3/2.4 (installer), 5.3 (charge limit), 6.1 (MQTT commands), 6.2 (GPU
  confirmation), 7.1 (autostart rewrite), 7.3 (firewall rule) + 7.4 (duplicate launch).
- **Dead ends (6):** 2.1 (unsupported machine cannot install), 4.2 (Unparsable model, no override),
  3.2 (guide CTA no-op), 2.5/8.4 (deleted vendor console cannot be restored), 8.6 ("send for help"
  with no contact).
- **Misleading wording (5):** 3.4 (published README contradicts installer), 3.5 (dead coexistence
  prompt), 2.5 (`恢复官方控制台` cannot restore), 7.1 (README/runbook claim a Highest autostart task
  that does not survive), 9.2 (vague log path).

## 11. UNVERIFIED

- Exact Inno Setup failure dialog for a non-zero `[Run]` exit code (the structural problem — hidden
  output and log-only reason — is confirmed; the exact string is not).
- Whether `startup_enabled` defaults to on, which determines whether 7.2 is reachable in practice.
- Whether the vendor console actually exposes controls for power-light toggle, local dimming, LCD
  overdrive and battery logo at runtime (backend gates not recoverable from the artifacts).
- Whether the 5.56 `GCU-only` service (shipped for 50-series) also serves the non-3-mode 40-series
  tier; recorded as BLOCKED-HW.
