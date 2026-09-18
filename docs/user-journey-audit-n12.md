# N12 user-perspective audit + N14 vendor-parity audit

Owner instruction (N12): walk the whole user journey and hunt for things that do not hold up **from
the user's point of view** - confusing, unclickable, misleading, unrecoverable, silently failing,
dead buttons, no way to self-rescue. Owner instruction (N14): stop being reactive; use the
decompiled vendor consoles/services as the reference spec and enumerate every user-facing feature,
capability gate and per-machine/per-tier branch, then ask whether we match.

Line numbers are from the state at the time of the audit.

## N12 - user journey, segment by segment

| # | Problem | Where | How the user feels | Severity | Fix |
|---|---|---|---|---|---|
| 1 | The first-use guide told users to install the vendor console, which the installer deletes | `FirstRunGuideForm.cs:88-91,112` (pre-N10) | "I followed the guide and it put back what the installer removed" | High | **Fixed (N10)**: guide + `用前必看.txt` now say our console is self-sufficient |
| 2 | A machine the vendor serves was locked read-only, so every control was dead | `ModelSupport.cs` (pre-N8) | "Nothing works at all" | Critical | **Fixed (N8)**: gate follows the vendor's service-served criterion |
| 3 | The read-only verdict was frozen at the first sample, so a machine that started before the service connected stayed dead | `EcChargeLimit.cs:101` (pre-N15) | "Rebooting does not help" | Critical | **Fixed (N15 #12)**: re-evaluated each call |
| 4 | The charge-limit value rendered as a bare dash with no explanation | `Settings.cs:5077` (pre-N15) | "Is it broken, unsupported, or still loading?" | Medium | **Fixed (N15 #15)**: reads `无法读取` |
| 5 | The AC-recovery icon showed OFF while the feature was ON | `MechrevoHw.cs:1914` (pre-N15) | "The switch lies to me" | Medium | **Fixed (N15 #17)**: the vendor's status byte is now a fallback; unknown is not OFF |
| 6 | iGPU switching silently failed on five machines | `MechrevoService.cs:906-907,1172` (pre-N15) | "I click and nothing happens" | High | **Fixed (N15 #13/#16②)**: the invented payload field is gone |
| 7 | Autostart did nothing after install | `Install-Gcu.ps1:626` (pre-N15) | "It never starts on boot" | High | **Fixed (N15 #14)**: the task carries the `startup` argument |
| 8 | The CPU advanced menu was offered on 30/40-series where it does nothing | `MechrevoHw.cs:747` | "The button does nothing" | Medium | **Already correct**: the gate is the vendor capability; pinned by tests (N13) |
| 9 | A failing test left the QA harness with no evidence file | `run-qa.ps1:58` (pre-fix) | (owner-facing) | Medium | **Fixed earlier**: evidence is written on failure |
| 10 | The installer removed the vendor console silently | `Install-Gcu.ps1` (pre-N7) | "Where did my console go?" | Medium | **Fixed (N7)**: every removal step is announced and logged |

### Remaining, not fixed here

| # | Problem | Where | Why not fixed |
|---|---|---|---|
| 11 | The user-facing troubleshooting text is still the owner's runbook | `docs\hardware\first-machine-runbook.md` | N10 asked for a separate user-facing text; the runbook is explicitly owner-facing. A user-facing doc is a writing task, not a code fix - recorded, not improvised |
| 12 | `catch { }` blocks remain in disposal/teardown paths | `WaterCoolerBle.cs`, `Logger.cs`, `LhmMonitor.cs` | Deliberate: teardown must not throw. Not silent failures of user actions |

## N14 - vendor-parity audit (reference spec = decompiled vendor binaries)

| # | Vendor feature / gate | Vendor source | Do we have it? | Same gate? | Verdict |
|---|---|---|---|---|---|
| 1 | iGPU-only switch (`IGPU_ONLY_CONNECT_RB_ON/OFF/AUTO`) | `GCUService.decompiled.cs:2375-2383` | Yes | Action name only | **Fixed (N15)**: we had invented an extra field |
| 2 | AC recovery (`ACRECOVERY_TOGGLE_ON/OFF` + `ACRecoveryStatus` byte) | `GCUService.decompiled.cs:2355-2357,21092` | Yes | String + byte | **Fixed (N15)**: the byte was unread |
| 3 | CPU advanced menu (`CPUPerformanceAndOverClockMenuSupport`) | `GCUService` capability bit | Yes | Vendor capability | Match (N13) |
| 4 | 40-series two capability tiers (with/without 双显三模) | two vendor consoles | Yes | `ItemSupport` bit | **Fixed (N11)** |
| 5 | Fan tables: 24 per-model dirs + 23 flat | `release\GCU-only\...\UserFanTables` | Yes | Per-model dir, else flat | **Fixed (N8)** |
| 6 | Keyboard brightness (`Keyboard/Ctrl` `SetEffectALL` `light`) | `GCUService` + console | Partial | HID primary | **Open**: N9-2 reverted; the vendor path exists but the fallback is not wired |
| 7 | GPU hot-swap (`GPU_HOTSWAP_ON/OFF`) | console enum, log-only handler | No | n/a | Correct: the vendor's own handler is a no-op, so we must not offer it |
| 8 | Whisper mode | console properties only, no send point | No | n/a | Correct: no command shape exists to copy |

### The systematic finding

Every field bug in this batch had the same shape: **we invented or froze something the vendor
derives at runtime.** The invented payload field (#1), the frozen support verdict (#3), the unread
status byte (#2), the hard-coded 24-code gate (#5). The durable rule is the one already in the
plan: **capability comes from the service-written `ItemSupport`, and payloads come from the vendor's
own action vocabulary - never from our inference.**
