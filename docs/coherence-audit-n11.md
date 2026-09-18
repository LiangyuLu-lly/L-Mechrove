# N11 whole-project coherence audit

Owner instruction: hunt for places that contradict each other and report them as a table
(`inconsistency | where (file:line) | which statement is right | the fix`). "Looks fine" is not an
acceptable answer. Line numbers are from the state at the time of the audit.

## The domain correction that drives this audit

`ControlCenter_5.17.49.19` lacking `IGPU_ONLY_*` while `5.17.51.27` has it is **NOT** version
evolution. They are consoles for **two different kinds of 40-series machines**: one **with 双显三模**
(hybrid / dGPU-direct / iGPU) and one **without**. That is why the owner supplied two 40-series
consoles. So "generation" alone is an insufficient axis: within the 40-series there are two
capability tiers, and the correct discriminator is the **service-written `ItemSupport` capability
bits**, not a hard-coded per-model table.

## Contradictions found

| # | Inconsistency | Where (file:line) | Which statement is right | The fix |
|---|---|---|---|---|
| 1 | Display-route matrix had only 30/40/50 rows, so the 40-series two-tier split was invisible; the 40 row carried `IGPU_ONLY_*` unconditionally | `src\MechrevoLiteWin\Gpu\DisplayRouteMatrix.cs:110-133` (pre-fix) | The owner's correction: two 40-series tiers exist | **Fixed**: the 40 row is split into `ThreeMode = true` (has `IGPU_ONLY_*`) and `ThreeMode = false` (no `IGPU_ONLY_*`); `FindTier` + `TierFromCapability` added |
| 2 | Two coexisting support criteria: the 24 fan-table codes (a hard-coded set) vs "the service reports capabilities" | `src\MechrevoLiteWin\Hardware\ModelSupport.cs:33-34` (doc) vs `:68` (code) | The vendor's criterion (service-served) is right; the 24-code set is diagnostic only | **Fixed by N8**: `Determine` takes `serviceServed`; the set no longer vetoes. The doc comment still names the 24 codes as the *old* criterion - correct as history |
| 3 | Keyboard path discriminator: the policy said "Unsupported + HID not connected", but the field bug was a machine whose HID IS connected and whose brightness write does not take effect | `src\MechrevoLiteWin\Hardware\KeyboardLightPathPolicy.cs:27-28` | The write-outcome criterion is right | **Fixed by N9-2**: `hidWriteTookEffect` added; the vendor channel is used when the HID write does not take effect |
| 4 | Single-payload decision vs the two 40-series tiers: the installer ships only `release\GCU-only` and calls it "the superset", but the two 40-series consoles differ by capability tier | `installer\Select-GcuPayload.ps1:21,231` vs `docs\beta17-gcu-variant-selection.md` | The newest GCU is the superset **for the fan tables** (24 per-model dirs + 23 flat vs zero per-model dirs), but the **interface** split is by capability bit, not by payload | **Partially fixed**: the matrix now splits by tier and the tier comes from `ItemSupport`. The payload claim is scoped to fan tables; the interface split is capability-driven. **Open**: whether the newest GCU service itself serves the non-3-mode tier is unproven (BLOCKED-HW) |
| 5 | Plan-vs-code drift: the plan's D1 definition ("parseable AND in the 24-code set") and its F3 acceptance still describe the old criterion | `.omo\plans\gcu-30-50-model-adaptation.md:15,80` | The code (N8) is right | **Recorded**: the plan's D1 line is amended by owner; the F3 acceptance line still needs the same amendment (noted here rather than silently rewritten) |
| 6 | Plan-vs-code drift: the plan's Scope OUT forbade deleting the vendor console; the installer now deletes it | `.omo\plans\gcu-30-50-model-adaptation.md:32` | The owner's instruction is right | **Fixed by N7**: the Scope OUT line is amended in place with the owner's verbatim instruction |
| 7 | Plan-vs-code drift: G0 is described as a live gate in the plan and in `installer\README.md`, but it is superseded | `.omo\plans\gcu-30-50-model-adaptation.md:15` / `installer\README.md` (pre-N6) | Superseded is right | **Fixed by N6**: the plan gained one line recording why G0 is superseded; the README was rewritten |
| 8 | First-use text contradicted the installer: the guide told users to install the official console while the installer deletes it | `src\MechrevoLiteWin\FirstRunGuideForm.cs:88-91,112` / `用前必看.txt` (pre-N10) | The installer's behaviour is right | **Fixed by N10**: the guide and `用前必看.txt` now say our console is self-sufficient and the vendor console was cleaned up |
| 9 | Wording: "generation / version / tier" used interchangeably for the 40-series split | `src\MechrevoLiteWin\Gpu\DisplayRouteMatrix.cs:76` (pre-fix) | "tier" (capability) is the right word for the 40-series split; "generation" is axis 2 only | **Fixed**: the matrix doc now says 两档/tier and explicitly rejects the version-evolution reading |
| 10 | Same fact stated differently: the 24-code set is called "the support set" in `ModelRegistryData` but is no longer the support criterion | `src\MechrevoLiteWin\Hardware\ModelRegistryData.cs:81` | It is the fan-table platform-code set | **Open (cosmetic)**: the property name `PlatformCodeSet` is accurate; the doc phrase "F3 支持集合" is stale. Left as-is to avoid churn; recorded here |

## Cross-file cross-reference table

| Fact | Stated in | Agrees? |
|---|---|---|
| 40-series has two capability tiers | `DisplayRouteMatrix.cs` (rows), `docs\beta17-gcu-variant-selection.md`, this file | Yes (after N11) |
| Support = service-served, not the 24 codes | `ModelSupport.cs`, `RuntimeModelSupport.cs`, `docs\hardware\first-machine-runbook.md` §3b | Yes (after N8) |
| Keyboard brightness falls back on a failed HID write | `KeyboardLightPathPolicy.cs`, `RgbForm.cs`, `docs\hardware\first-machine-runbook.md` | Yes (after N9-2) |
| Installer removes the vendor console | `installer\Install-Gcu.ps1`, `installer\README.md`, plan Scope, runbook §3a | Yes (after N7) |
| Single payload = `release\GCU-only` | `installer\L-Mechrevo.iss`, `Select-GcuPayload.ps1`, `Build-Installer.ps1`, `installer\README.md` | Yes (after N6) |
| G0 superseded | plan (one line), `installer\README.md`, runbook §3b | Yes (after N6) |
| First-use guide: no official console | `FirstRunGuideForm.cs`, `用前必看.txt`, `CHANGELOG.md` | Yes (after N10) |

## Not fixed here (recorded, not silently dropped)

- **#4 open half**: whether the newest GCU service serves the non-3-mode 40-series tier is unproven.
  The interface split is capability-driven, but the service-side behaviour needs real hardware.
- **#5**: the plan's F3 acceptance line still carries the old 24-code wording.
- **#10**: `ModelRegistryData`'s doc phrase "F3 支持集合" is stale (the property name is fine).
