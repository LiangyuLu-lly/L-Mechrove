# beta17 Wave Plan (cleanup + medium refactor)

> Transcribed from the beta17 planning-agent run (session `ses_f5a9fa084ffe9Z3SFqZZSTW7Dp`, model `opencode-go/deepseek-v4.1-flash`). The saved tool output was truncated mid-way through Task 0.1; sections absent from the source are marked accordingly.

## Context

**Goal.** Ship "beta17" of the shipped WinForms/.NET 10 x64 hardware-control app by (a) fixing a
shipped correctness defect (updater permanently unable to update), (b) deleting proven-dead code and
unifying conventions, and (c) medium refactors (god-method extraction, duplicate-logic convergence,
sync-over-async removal) — each **gated by characterization/regression tests + real-machine
re-verification**. No full config-model rewrite; no unbounded rewriting.

**Evidence base (read, not re-derived):** `artifacts/beta17/BASELINE.md` (HEAD `764f3f4`, clean tree),
`artifacts/beta17/safety-net.md`, `docs/beta17-cleanup-plan.md`, `docs/code-audit-deadcode.md`.

**Oracle adjudications (binding):** (a) MQTT topic constants — DO (mechanical `const string`, compiler-checked). (b) `HandleMessage` — extract-method ONLY; keep the single `try` (`:1220`) and single `switch(topic)` (`:1223`) as the routing table; no handler dictionary, no per-case try/catch. (c) `Settings.V2.cs` lightbar/logo duplication — DO; it is UI row construction, NOT the publish path; do not sell it as a flicker fix. (d) Installer GCU dedupe — TRACKED `.iss` only; try `/DCompression=lzma2/ultra64` first and measure; tree-drop is an owner product decision. (e) `PublishTrimmed` — **NON-GOAL/TRAP**.

**Verified during planning (supersedes stale docs):**
- `HandleMessage` = **905 LOC** (`MechrevoHw.cs:1212-2116`) — the "~401" was a brace-heuristic artifact. Still must be characterized before extraction (no direct unit test exists).
- `OfficialConsoleCatalog.cs` (58 LOC) + `DeviceCapabilitySnapshot`/`FeatureSupport`/`DeviceFeature`/`FeatureEvidence` are referenced **only** by `DeviceCapabilitySnapshotTests.cs:10-35`; only `FeatureAvailability` is production-live.
- `#if HARDWARE_DIAGNOSTICS` already deleted in beta16 — excluded from scope.
- `_dashboardPageHost`/`_lightingActionTable` readers are permanently-dead (never assigned).
- `PeripheralsProvider.{DetectAllAsusMice,RegisterForDeviceEvents,UnregisterForDeviceEvents,RefreshBatteryForAllDevices(bool)}` have live callers → KEEP.

## Baseline gate

Measured baseline (must be re-measured on execution day):

| Metric | Baseline |
|---|---|
| Debug x64 / Release x64 full compile (`-t:Rebuild`) | 0 err / **4 warnings** (CS8321 `D`×2 Settings.cs:195,418; CS0649 `_lightingActionTable`:307, `_dashboardPageHost`:68) |
| Full suite | **1619 pass / 0 fail / 1 skip** (1620 total), 2m44s |
| UI audit | **650 screenshots / 86 findings** = text-clipping 48 (FirstRunGuide) + parent-overflow 27 (`comboLightingSleepTimer`, 9 variants × 3 viewports) + docked-root-overflow 11 (KeyboardRgb) |
| Artifacts | exe 219,589,588 B; installer 128,930,733 B (`3F49B4C1…`); update zip 77,922,585 B |
| Source | `src\MechrevoLiteWin` 121 .cs / 43,152 LOC; `src` 147 .cs / 44,238 LOC |

## Measurement traps

1. Literal `dotnet build` without `-t:Rebuild` is an up-to-date no-op that prints "0 warnings" — **meaningless**. Warning counts require `-t:Rebuild`.
2. UI audit exits 1 when findings > 0 → **gate on `ui-audit.json` `IssueCount`**, never exit code. Baseline is environment-noisy (77/86/95 seen same day) → compare **per-category**, re-measure on execution day.
3. Only ONE `dotnet` build/test at a time (file locks). On lock: wait 60s, retry ≤3; a lock is NOT a test failure.
4. `release\` is **UNTRACKED** → never delete/move anything there. Only edit the **tracked** `installer\L-Mechrevo.iss`.

## Risk

- **R-A (high, Wave 4):** publish-path convergence serializes publishes that are currently parallel → a UI light toggle regresses from ~2.08s worst-case to a multiplied stall, or a duplicate/absent publish re-fires firmware re-init (flicker). *Mitigation:* never change publish **timing/order** — change only *who calls*; fake-publisher characterization asserting exact sequence+count per trigger must be GREEN before and after; real-machine 4-item matrix is a hard gate; any failure → full wave rollback.
- **R-B (high, Wave 3.2):** `HandleMessage` extract-method accidentally narrows the single `try` or reorders `case` arms → one malformed payload now crashes the receiver, or late fields/notifications get dropped. *Mitigation:* single `try` and single `switch` are invariant; recorded-payload characterization (RED→GREEN) pins per-topic parse results and the "one bad topic does not crash others" contract before any move.
- **R-C (medium, Wave 2 config keys):** deleting read sites is a real behavior change for users who hand-edited `config.json`; `sensors_always` is read in a timer branch. *Mitigation:* hardcode the shipped default (`false`) rather than deleting the branch semantics; grep-prove zero write path per key; document in release notes; owner confirmation U1.
- **R-D (medium, Wave 0):** the updater fix touches the security-critical download path. *Mitigation:* no change to sha256/whitelist/downgrade/response-validation rules; `UpdateSecurityHardeningTests` + `UpdateCheckerTests` must stay green; new test asserts refusal semantics unchanged.
- **R-E (medium, Wave 5.1):** `EnableCompressionInSingleFile` may inflate cold-start/time-to-tray on a startup/tray app. *Mitigation:* measure cold-start before/after; revert if regression > agreed threshold; fully reversible flag.

## Task Dependency Graph

| Task | Depends On | Reason |
|------|------------|--------|
| T0.1 Updater fix | None | Independent `Updates\*` files |
| T1.1 MQTT topic constants | None | Introduces `MqttTopics.cs` + rewrites literals |
| T2.1 Capability-model deletion | None | File-disjoint from all |
| T2.2 Settings.cs consolidation | None | Owns `Settings.cs` exclusively |
| T2.6 Config-key reads (non-Settings) | None | File-disjoint from all |
| T2.7 Orphan project / disk hygiene | None | File-disjoint from all |
| T2.3 Program.cs deletions | T1.1 | Same file (`Program.cs`) |
| T2.4 HW/GPU deletions | T1.1 | Shares `MechrevoService.cs` |
| T2.5 Stubs peripheral chain | T2.2 | Handlers that reference the stubs must be gone first |
| T3.1 Settings.V2 row parameterization | T1.1 | Uses `MqttTopics` constants |
| T3.2 HandleMessage extract-method | T1.1 | Same file (`MechrevoHw.cs`) |
| T3.4 Empty-catch logging | T1.1 | Shares `MechrevoService.cs`/`RgbForm.cs`/`FanCurveForm.cs` |
| T3.3 sync-over-async | T3.2, T2.2 | Shares `MechrevoHw.cs`, `Settings.cs` |
| T4.1 Publish-path convergence | T3.1, T3.2, T3.4 | Shares `MechrevoService.cs`, `Settings.V2.cs`, `Program.cs`, forms |
| T5.1 EnableCompressionInSingleFile | T4.1 | Measured on a complete tree |
| T5.2 Installer LZMA measurement | T4.1 | Measures final artifact |
| T5.3 DebugType=embedded decision | T4.1 | csproj change after code freeze |
| T6.1 Final gate | T5.1, T5.2, T5.3 | Requires frozen source |

## Parallel Execution Graph

```
Wave 0  (start immediately, all file-disjoint):
├── T0.1  Updater temp-path/handle/cleanup/error fix        (Updates\*)
├── T1.1  MQTT topic constants                              (MqttTopics.cs + hardware/UI literals)
├── T2.1  Capability-model deletion                          (Hardware\OfficialConsoleCatalog.cs, DeviceCapabilitySnapshot.cs, tests)
├── T2.2  Settings.cs consolidated deletions (+ warnings)    (Settings.cs)
├── T2.6  Config-key dead reads (non-Settings)               (TempHelper, RForm, RComboBox, ToastForm)
└── T2.7  Orphan project + favicon + dist hygiene            (src\MechrevoLite\**, stray ico, dist\beta8\)

Wave 1  (after Wave 0; file-disjoint):
├── T2.3  Program.cs deletions                               (depends T1.1)
├── T2.4  HW/GPU dead-code deletions                         (depends T1.1)
├── T2.5  Stubs peripheral chain                             (depends T2.2)
└── T3.1  Settings.V2 row parameterization                   (depends T1.1)

Wave 2  (behavior-preserving refactors):
├── T3.2  HandleMessage extract-method                       (depends T1.1)
└── T3.4  Empty-catch logging                                (depends T1.1)

Wave 3  (serialized; shares MechrevoHw.cs + Settings.cs):
└── T3.3  sync-over-async                                    (depends T3.2, T2.2)

Wave 4  (HIGHEST RISK — last, isolated):
└── T4.1  Light-power publish-path convergence + harness     (depends T3.1/T3.2/T3.4)

Wave 5  (isolated measured experiments):
├── T5.1  EnableCompressionInSingleFile (measured)
├── T5.2  Installer LZMA measurement + GCU decision
└── T5.3  DebugType=embedded decision (csproj; serialize with T5.1)

Wave 6  (final gate):
└── T6.1  Full suite + UI audit + release build + review

Critical path: T1.1 → T3.2 → T3.3 → T4.1 → T6.1
```

**Note on parallelism vs the one-build rule:** tasks within a wave have disjoint file ownership, so
**edits** can be produced in parallel by separate agents; **verification (build/test) is serialized**
(one `dotnet` at a time). Commit each task only after its own build+test has run.

## Tasks

> The saved source was truncated partway through Task 0.1's HOW steps. Task 0.1 is transcribed verbatim up to the truncation point; the remaining tasks are reconstructed from the planning agent's verified reasoning (file:line references preserved) and the dependency table above. VERIFY blocks for T1.1–T6.1 are synthesized from the source's stated gates.

### Task 0.1: Fix the updater download path (HIGHEST VALUE — correctness defect)
**WHERE:** `src\MechrevoLiteWin\Updates\UpdateInstaller.cs:62-64` (fixed per-version temp path), `:81` (`FileMode.Create, FileShare.None`), `:106` (swallowed `TryDelete`), `:336-338` (catch-empty `TryDelete`); call site `Updates\UpdateForm.cs:317-323`; `Updates\UpdateSelfTest.cs:62`.
**WHY:** A stale/locked temp file makes every retry fail identically forever ("permanently unable to update"; field: 4 identical IOException retries, package stuck at 2.22 MB of 74 MB).
**HOW:**
1. Unique per-attempt path: `TempRoot/<version>/<attempt-guid>/<package.zip>` (or `<name>.<guid8>.part`). The fixed-path collision disappears.
2. Release the handle before any retry/cleanup: keep `await using` scoped so disposal precedes the `catch`; wrap the stream open so a locked-open failure is distinguished from a network failure.
3. Stale cleanup across versions: at the start of `DownloadAsync`, best-effort delete sibling attempt dirs and other-version dirs, **excluding** the current attempt dir and `TempRoot/updater`. Log (never sil

*— source truncated here. Reconstructed from the planning reasoning:*
4. Surface a readable error: change `DownloadAsync` to return a small `internal sealed record DownloadResult(string? Path, string? Reason)` instead of a bare nullable path (async methods cannot take `out` params). Only 2 call sites (`UpdateForm.cs:317`, `UpdateSelfTest.cs:62`); no test calls `DownloadAsync` directly, so blast radius is low. `UpdateForm` surfaces the reason to the user.
5. Cleanup must exclude the current attempt dir and `TempRoot/updater` (the running updater's copy); best-effort with a logged warning, not a silent swallow.
**VERIFY:** RED→GREEN characterization: pre-create a locked file at the fixed temp path (hold a `FileShare.None` handle), call `DownloadAsync` against the existing loopback stub server (`UpdateClientEndToEndTests`) → today throws IOException / returns null; after fix, a unique per-attempt name makes the download succeed. `UpdateSecurityHardeningTests` + `UpdateCheckerTests` stay green (refusal semantics unchanged).

### Task 1.1: MQTT topic constants
**WHERE:** new `Hardware\MqttTopics.cs` (`internal static class MqttTopics`); literal rewrite sites: `MechrevoHw.cs`, `MechrevoService.cs`, `Program.cs`, `Settings.V2.cs`, `RgbForm.cs`, `LightForm.cs`, `CustomModeForm.cs`, `FanCurveForm.cs`, `FunctionVerifier.cs`, `UiAuditRunner.cs`.
**WHY:** Topic strings are duplicated as raw literals across 10 files; a typo silently breaks subscribe/handle matching. Mechanical, compiler-checked unification.
**HOW:** Define one `const string` per topic in `MqttTopics.cs`; replace literals everywhere in MechrevoLiteWin. `src\Probe\MqttProbe.cs` (8 matches) is a separate project with no ProjectReference — leave its literals alone (out of scope, noted).
**VERIFY:** Build (0 new warnings) + full suite + a new subscribe-set == handled-set test (both sets derive from `MqttTopics`).

### Task 2.1: Capability-model deletion
**WHERE:** delete `Hardware\OfficialConsoleCatalog.cs` (58 LOC, incl. `OfficialRgbLayout` enum); `Hardware\DeviceCapabilitySnapshot.cs` → keep only the `FeatureAvailability` enum, relocated to new `Hardware\FeatureAvailability.cs`, delete the old file; delete `tests\...\DeviceCapabilitySnapshotTests.cs`.
**WHY:** Death proof: grep shows the only non-self references to `OfficialConsoleCatalog` + `DeviceCapabilitySnapshot`/`FeatureSupport`/`DeviceFeature`/`FeatureEvidence` are `DeviceCapabilitySnapshotTests.cs:10-35`. Internal types, no reflection, no JSON serialization, no obfuscar entry. `FeatureAvailability` alone is production-live (`MechrevoDeviceCapabilities.cs:71-74`, `MechrevoService.cs:2071`, `Settings.cs:2892/3557/4888`) — KEEP it.
**HOW:** Delete the two files, create `FeatureAvailability.cs` with the enum, delete the test file. `Run5AutostartAndTurboGateTests` (lines 184-221) uses `capabilities.SilentTurboAvailability` (`FeatureAvailability`) — those tests stay.
**VERIFY:** Build + full suite; expected suite delta −3 (see "Tests that may be legitimately deleted").

### Task 2.2: Settings.cs consolidated deletions (+ warnings)
**WHERE:** `Settings.cs` — CS8321 unused local functions `D` at :195 and :418; CS0649 never-assigned fields `_dashboardPageHost` (:68, dead read at :245) and `_lightingActionTable` (:307, dead reads at :3059-3062, plus `ReflowLightingActions` if it becomes fully dead — grep callers first); peripheral handlers `ButtonPeripheral_MouseEnter`, `ButtonPeripheral_Click`, `MouseSettings_FormClosed`, `MouseSettings_Disposed` (:5060-5132) + field `AsusMouseSettings? mouseSettings` (:61) + `UpdateKeyboardLabel()` (:5054-5057) if zero callers; `ButtonFnLock_Click` (:5152) + `VisualiseFnLock` (grep first); `ComboKeyboard_SelectedValueChanged` (:3963) (+ `SetAura` if it becomes dead); `fn_lock` read (:5137); `sensors_always` reads (:1890, :2223, :3409, :4469); `topmost` reads in Settings.cs; `RefreshBatteryHealth` caller (:3397).
**WHY:** All proven dead or warning-generating; consolidating all Settings.cs edits into ONE task avoids same-file parallel conflicts and churn.
**HOW:** Pre-edit grep for event wiring (`ButtonPeripheral_Click +=` etc.) and remove the `+=` lines when handlers are deleted; leave Designer/resx and the button controls untouched. For `sensors_always`, collapse to the shipped default `false` (timer throttle becomes always 2000 ms; `_sensorTimer.Enabled = this.Visible`) — runtime-equivalent to today. Delete `ButtonPeripheral_Click` carefully: `AllPeripherals().ElementAt(index)` on the always-empty list would throw — the handler is unreachable, deletion is behavior-preserving.
**VERIFY:** Build with `-t:Rebuild` → 0 warnings (baseline 4 eliminated); full suite green.

### Task 2.3: Program.cs deletions
**WHERE:** `Program.cs` — `OnChargerEvent` (:1429, never subscribed); `topmost` read (:1884).
**WHY:** Dead handler + dead config-key read; Program.cs is owned by T1.1 in Wave 0, so this must follow it.
**HOW:** Delete the handler and the read site.
**VERIFY:** Build + full suite.

### Task 2.4: HW/GPU dead-code deletions
**WHERE:** `HardwareControl.RefreshBatteryHealth()` (empty body) — method deletion; `GPUModeControl.cs:412` `IsUsedGPU` gate R1 (never fires) + its `threshold` param; `IGpuOverclockControl.cs:24` default impl `WritesRequireElevation => false` (confirm only `NvidiaGpuControl` implements, then delete the default member); `MechrevoService.cs` shares the file with T1.1 → after it.
**WHY:** Owner-endorsed deletions of unreachable defaults/gates; behavior-preserving.
**HOW:** Delete the empty method, the gate + param, the default interface member.
**VERIFY:** Build + full suite + static-scan anti-regression test for the deleted gate.

### Task 2.5: Stubs peripheral chain deletion
**WHERE:** `Stubs.cs` — `IPeripheral`, `AsusMouse`, `AsusMouseSettings`, `PeripheralType` enum; `PeripheralsProvider` dead members `IsAnyPeripheralConnect`, `IsAuraSync`, `AllPeripherals`. KEEP `DetectAllAsusMice`, `RegisterForDeviceEvents`, `UnregisterForDeviceEvents`, `RefreshBatteryForAllDevices(bool)` (live callers: `Program.cs:418/419/1561`, `Settings.cs:3419`).
**WHY:** The peripheral panel is unreachable (`AllPeripherals()` always returns empty); the stub types are referenced only by the handlers deleted in T2.2.
**HOW:** Depends on T2.2 (handlers gone first, then the types compile-clean). Leave Designer/resx untouched.
**VERIFY:** Build + full suite.

### Task 2.6: Config-key dead reads (non-Settings files)
**WHERE:** `TempHelper.cs:5` `IsFahrenheit` + `FormatTemp` branch + `CelsiusToFahrenheit` (`fahrenheit` — owner explicitly decided: delete, lock Celsius); `RForm.cs:71` (`theme`/flatTheme); `RComboBox.cs:259` (flatTheme usage); `ToastForm.cs:121` (`disable_osd` + early return).
**WHY:** Dead config keys; behavior change only for hand-edited configs (documented).
**HOW:** Delete the read sites; hardcode shipped defaults. Static-scan anti-regression tests asserting no read sites remain (source-shape assertions are acceptable per project convention — cf. `UiStyleDisciplineTests`).
**VERIFY:** Build + full suite + real machine (UI behaves identically with default config).

### Task 2.7: Orphan project + favicon + dist hygiene
**WHERE:** delete `src\MechrevoLite\` (18 files, 695 LOC orphan WPF prototype; not in slnx; its own DEPRECATED.md says delete); delete unreferenced `favicon_backup.ico` (63,418 B); disk-only: `src\MechrevoLiteWin\dist\beta8\` (untracked).
**WHY:** Dead weight; recovery from git history.
**HOW:** PRECONDITION (U4): confirm `git ls-files src/MechrevoLite` shows it tracked; if untracked, stop and get owner confirmation. No source overlap with other tasks.
**VERIFY:** `git status` shows only intended deletions; build + full suite unaffected.

### Task 3.1: Settings.V2 lightbar/logo row parameterization
**WHERE:** `Settings.V2.cs` only.
**WHY:** Duplicated UI row-construction logic for lightbar/logo rows; behavior-preserving dedup (NOT the publish path — do not sell as a flicker fix).
**HOW:** Parameterize the row construction; depends on T1.1 (uses `MqttTopics` constants).
**VERIFY:** Characterization test pinning row layout + full suite + UI audit per-category comparison.

### Task 3.2: HandleMessage extract-method
**WHERE:** `MechrevoHw.cs:1212-2116` (905 LOC; next method `WaitForStateAsync` at :2117).
**WHY:** God method; extract per-topic handlers while keeping the single `try` (:1220) and single `switch(topic)` (:1223) as the routing table — no handler dictionary, no per-case try/catch.
**HOW:** Characterization tests FIRST (recorded-payload corpus pinning per-topic parse results and the "one bad topic does not crash others" contract), RED→GREEN, then extract. Depends on T1.1.
**VERIFY:** Recorded-payload characterization GREEN before and after; `ProtocolFuzzTests` green; full suite.

### Task 3.3: sync-over-async UI-path fixes
**WHERE:** `ModeControl.cs:95`, `Settings.cs:2396`, `MechrevoHw.cs:3105` (keep + comment), `ElevatedGpuOverclockHelper.cs:328`, `ProcessHelper.cs:361/369`.
**WHY:** Sync-over-async on UI paths risks deadlocks/jank.
**HOW:** Serialized after T3.2 (shares `MechrevoHw.cs`) and after T2.2 (shares `Settings.cs`).
**VERIFY:** Build + full suite + real-machine smoke of the affected controls.

### Task 3.4: Empty-catch logging
**WHERE:** 8 sites: `BrightnessCommitQueue.cs`, `NvmlHelper.cs`, `MechrevoService.cs`, `CpuInfo.cs`, `RSlider.cs`, `RgbForm.cs`, `FanCurveForm.cs`.
**WHY:** Silent swallows hide real failures.
**HOW:** Add logged warnings (never rethrow-crash); depends on T1.1 (shares `MechrevoService.cs`/`RgbForm.cs`/`FanCurveForm.cs`).
**VERIFY:** Build + full suite.

### Task 4.1: Light-power publish-path convergence (HIGHEST RISK — last)
**WHERE:** `CustomModeForm.cs`, `FanCurveForm.cs`, `LightForm.cs`, `Settings.V2.cs`, `Program.cs`, `MechrevoService.cs`.
**WHY:** Duplicate publish logic; convergence must not change publish timing/order (R-A).
**HOW:** Fake-publisher characterization harness asserting exact publish sequence+count per trigger, GREEN before and after; change only *who calls*, never timing/order. Real-machine 4-item matrix is a hard gate; any failure → full wave rollback.
**VERIFY:** Characterization harness + real-machine matrix + full suite.

### Task 5.1: EnableCompressionInSingleFile (measured)
**WHERE:** csproj/publish flag.
**WHY:** Artifact-size reduction candidate.
**HOW:** Measure exe/installer/zip sizes AND cold-start before/after (R-E); revert if regression > agreed threshold.
**VERIFY:** Measured numbers recorded; reversible.

### Task 5.2: Installer LZMA measurement + GCU decision
**WHERE:** `installer\L-Mechrevo.iss` (TRACKED only — never touch untracked `release\`).
**WHY:** Installer size; GCU tree dedupe.
**HOW:** Try `/DCompression=lzma2/ultra64` first and measure. Dropping the default-unreachable `40-51749` GCU tree is an owner product decision (U3) requiring override-path + README changes.
**VERIFY:** Measured installer size; owner decision recorded.

### Task 5.3: DebugType=embedded decision
**WHERE:** csproj (Release configuration).
**WHY:** PDB size; loses stack line numbers — owner decision (U2).
**HOW:** csproj change after code freeze; serialize with T5.1 (both touch csproj).
**VERIFY:** Owner decision recorded; release build measured.

### Task 6.1: Final gate
**WHERE:** whole repo.
**WHY:** Frozen-source verification.
**HOW:** Full suite + UI audit re-measure (gate on `ui-audit.json` `IssueCount`, per-category) + Release build gate + `review-work`.
**VERIFY:** All gates green; deltas vs baseline recorded.

## Scenario Contract

From the planning reasoning (S1–S5, binary pass conditions + real surface):

- **S1 Happy path:** with a stale locked temp file present, update download succeeds on a fresh attempt. Surface: loopback-stub E2E test (`UpdateClientEndToEndTests`) + (real machine, needs human) actual update from a locally served package.
- **S2 Edge:** malformed/locked temp + mid-download abort then retry → second attempt uses a new path and completes; first attempt's partial file is cleaned. Surface: unit/integration test with injected lock + partial file.
- **S3 Adjacent-surface regression:** MQTT receiver — a malformed payload on one topic still does not crash `HandleMessage` and other topics still parse (single try preserved). Surface: `ProtocolFuzzTests` + recorded-payload characterization (RED→GREEN for HandleMessage extraction).
- **S4 Publish-path:** exact publish sequence+count per trigger unchanged (fake publisher). Surface: characterization test.
- **S5 Config keys:** deleting dead keys yields identical runtime defaults; UI behaves same. Surface: build + full suite + real machine.

TDD RED→GREEN per production change: for pure dead-code deletion, the RED test is the anti-regression static scan (assert the dead symbol/read no longer exists) — RED before deletion, GREEN after.

## Definition of Done per Wave

_(not present in source; to be filled by the orchestrator)_

## NON-goals

From the planning reasoning:

- `PublishTrimmed` (trap — oracle adjudication: NON-GOAL)
- ReadyToRun disable (deferred)
- Full config-model rewrite
- Deleting all Stubs (only the proven-dead peripheral chain)
- Rewriting Program arg parsing
- `IsXGConnected` / `MidFanSeen`
- Interop struct fields
- `CreatedUtc`
- The 5 "written-not-read" config keys
- A4 unused params (except the `IsUsedGPU` `threshold` param)
- B1/B2/B8 god-object full split
- B5 DPI convention unification
- ToastForm user-facing failure prompts
- Unused-using sweep (optional, at most)
- Probe project constants (`src\Probe\MqttProbe.cs` — separate project, no ProjectReference)
- `release\` untracked tree deletion
- Installer `Check:` as a size measure

## Tests that may be legitimately deleted

- `DeviceCapabilitySnapshotTests.cs` — **3 `[Fact]`** whose sole subjects are `OfficialConsoleCatalog` + `DeviceCapabilitySnapshot`/`FeatureSupport`/`DeviceFeature`, all proven dead (grep: only test refs; internal; no reflection/JSON/obfuscar). Two catalog tests (lines 8-24) are tautological; the third (fingerprint) tests `DeviceCapabilitySnapshot`. All 3 die with the model.
- Expected suite delta: **−3** (baseline 1619 pass → 1616 expected before new tests). Justification recorded here.
- `Run5AutostartAndTurboGateTests` (lines 184-221) uses `capabilities.SilentTurboAvailability` (`FeatureAvailability`, live) — those tests STAY.
- No other test deletions identified (no fn_lock/peripheral/HandleMessage-handler tests exist).
- New tests added: updater (~3-5), topic-set (1), HandleMessage characterization (~5-8), publish-sequence (~2-3), static-scan anti-regression (~3) → final count > 1619.

## Effort estimates

From the planning reasoning (S/M/L per task):

| Task | Effort |
|------|--------|
| T0.1 | M |
| T1.1 | M |
| T2.1 | S |
| T2.2 | M |
| T2.3 | S |
| T2.4 | S |
| T2.5 | S |
| T2.6 | S |
| T2.7 | S |
| T3.1 | M |
| T3.2 | L |
| T3.3 | M |
| T3.4 | S |
| T4.1 | L |
| T5.1 | S |
| T5.2 | M |
| T5.3 | S |
| T6.1 | M |

## Missing measurements to take before/during execution

_(not present in source; to be filled by the orchestrator)_

## Owner confirmations required

- **U1:** Owner explicitly decided **`fahrenheit` → delete (lock Celsius)**. The other five dead keys (`disable_osd`, `theme`, `topmost`, `sensors_always`, `fn_lock`) follow the endorsed `beta17-cleanup-plan.md` Wave B (delete). **Confirm at Wave-2 gate.**
- **U2:** `DebugType=embedded` for Release (loses stack line numbers) — **owner decision**, Wave 5.
- **U3:** Dropping the default-unreachable `40-51749` GCU tree from the installer bundle — **owner product decision**, Wave 5 (requires override-path + README changes).
- **U4:** `src\MechrevoLite\` deletion requires confirming it is **git-tracked** (recoverable). If untracked, stop and ask.

## Open questions

- Commit strategy granularity: one commit per task where the task is independently buildable+testable; bundle only when files are interdependent (Settings.cs batch, Stubs+Settings). Build+test after each commit's edits, before committing. With the one-dotnet-at-a-time rule, tasks in a wave are applied sequentially in *verification* but edits can be staged in parallel.
- Whether `UpdateKeyboardLabel()` (:5054-5057) has zero callers (grep before deleting).
- Whether `VisualiseFnLock` (:5134) is used by the UI audit (grep before deleting).
- Whether `ReflowLightingActions` becomes fully dead after `_lightingActionTable` deletion (grep callers).
- Whether `InputDispatcher.ToggleFnLock` and `SetAura` become dead after handler deletions (delete only if zero other refs).
- Whether `buttonPeripheral` controls are wired via `+=` in the Designer (pre-edit grep `ButtonPeripheral_Click|ButtonPeripheral_MouseEnter|MouseSettings_FormClosed|MouseSettings_Disposed`); if wired, remove just the `+=` lines; leave Designer/resx untouched.
