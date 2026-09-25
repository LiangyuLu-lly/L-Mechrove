Task completed in 8m 5s.

Agent: plan
Model: xai/grok-4.6

---

# L-Mechrevo Rust + Tauri 2 Full Functional Rewrite

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Save as:** `docs/superpowers/plans/2026-09-20-rust-tauri-rewrite.md`
>
> **Execution split:** Plan A is this session’s runnable stop. Plan B is the remaining Official + Added port list. Nothing in the feature inventory is dropped.

**Goal:** Replace the WinForms host with a Tauri 2 + React app whose Rust host owns all hardware, such that every Official-console feature that works on a given machine still works — without deleting `.NET`, killing a running L-Mechrevo, or touching `release\`.

**Architecture:** New tree `app/` beside `src/MechrevoLiteWin`. Tauri 2 host is the only process that speaks MQTT / HID / IOCTL / registry / NVAPI. The webview is untrusted UI: typed commands for writes, events for telemetry/hotplug/MQTT status. Protocol logic lives in workspace crates (`gcu-mqtt`, `hid-kb`, `ec-acpi`, `capabilities`, optional `nvapi-oc`), each TDD’d against golden JSON extracted from existing C# tests. Files ≤ 250 pure LOC.

**Tech Stack:** Tauri 2 + React-TS; rumqttc 0.25 (MQTT 3.1.1); hidapi 2.6 windows-native; windows-sys 0.61; windows-registry 0.6; libloading `nvapi64.dll`; tauri-plugin-log; NSIS perMachine + embedBootstrapper.

---

## Context

**User Request Summary.** Full functional rewrite of L-Mechrevo from .NET WinForms to Rust + Tauri 2. Polar star: every official-console feature that works on a given machine must work in the rewrite. Locked decisions are not to be re-interviewed.

**Interview findings (locked, do not reopen).**

| Decision | Value |
|---|---|
| Tree | Create `app/` via create-tauri-app react-ts. Keep `src/MechrevoLiteWin`. |
| Forbidden | Delete .NET; kill running L-Mechrevo; touch `release\`; git commit unless user asks |
| Trust | Host owns hardware. JS never MQTT/HID/IOCTL/registry. No secrets to JS. |
| MQTT product | Slot 4 only: `127.0.0.1:13688`, client `UWPClient_4`, user `UWPClient_User_4`, password `UWPClient_Pwd888881772688_4`, MQTT 3.1.1, `clean_session true`, keepalive 3s. **Never ship probe slot 5.** Helper processes use `UWPClient_3`. |
| Handshake | Await all SUBACKs → `System_ON` first → GETSTATUS on Fan / Setting / LCHWOC / Keyboard / HidLightbar / HidLightbar_Logo / BT_LC → BatteryProtection `{Report:"GET"}` |
| Lighting gate | ItemSupport fail-closed (`LightbarSupport` / `RGBLightbarSupport` / `LogoLightSupport`). Empty MQTT is not hardware (G16 false Logo/lightbar). |
| Keyboard | `KeyboardLightPathPolicy`: HID ITE8291 VID `048D` usage `FF03`; GCU fallback only when HID brightness fails |
| EC charge | ONLY `0x7B9` + `0x7D0`. Write IOCTL `0x9C40A48C` 5-byte `[u32 addr][u8 value]`. Read `0x9C40A488` 4-in/16-out. Forbidden: `0x7A6`, `0x78F`, any other EC write |
| Dashboard | 420 logical px wide. Update DESIGN.md footer (live UI has 6 keys including 诊断) before React components |
| TDD | Golden JSON from existing C# tests before each crate. Files ≤ 250 LOC |
| This-session stop | Working Tauri window ↔ fake GCU broker ↔ gated dashboard. Real GCU is a later verification wave |

**Research results (repo truth).**

- Topics: `src/MechrevoLiteWin/Hardware/MqttTopics.cs` — `HidLightbar_Logo/#` does **not** match `HidLightbar/#`.
- Product MQTT: `MechrevoHw.cs:41-65` slot 4 / helper slot 3. Probe (`src/Probe/MqttTransport.cs`) uses slot 5 — **do not copy into product**.
- Capabilities: `MechrevoDeviceCapabilities.FromValues` (`MechrevoDeviceCapabilities.cs:255-355`) + `FeatureMatrix` fail-closed bits.
- Quick switches: `MechrevoHw.SupportsQuickSwitch` (`MechrevoHw.cs:800-840`). Touchpad unconditional; wifi/bt/webcam Seen; whisper not in table.
- GPU: `DisplayRouteMatrix` Gen30/40/50. No Auto button. Hot swap 50-only. Confirm `Setting/Status` + `GPUDevice/Status`.
- Mode switch must also send `LCHWOC/Control` (`IsNormalRun` 0/1/2 or `IsCustomRun:true`) — `docs/hardware/README.md`.
- EC write layout proven: 5-byte, not 3/4 (`EcChargeLimit.cs:197-206`). 100% → write `0/0`. Lower = upper − 5 hysteresis. Slider 40–100.
- Keyboard HID: VID `0x048D`, usage page `FF03`, ITE8291 (`KeyboardRgb.cs:8-16`).
- DESIGN.md footer is stale (overlay/settings/updates/sponsor/exit). Live footer has **6** keys including 诊断.
- `app/` does not exist yet.
- Withdrawn (Must-NOT-Have): whisper, official battery 3-mode, `DISPLAY_*_MODE`, `NV_CTRL_PANEL`, Ally/XGM/AniMe, GPU Auto button, RamFan as UI, mid fan, hinge/sync lightbars.

**C# tests → golden JSON sources (extract, do not rewrite C#).**

| C# test | Golden for |
|---|---|
| `FeatureMatrixTests.cs` / `FeatureMatrixFailTests.cs` | capabilities fail-closed bits |
| `DeviceCapabilityTests.cs` / `SubLightbarTests.cs` / `CapabilitySnapshotTests.cs` | ItemSupport lighting gates (G16) |
| `BatteryChargeLimitTests.cs` / `BatteryChargeLimitHotfixTests.cs` | `0x7B9`/`0x7D0` encode, never `0x7A6` |
| `KeyboardLightPathPolicyTests.cs` / `KeyboardBrightnessFallbackN92Tests.cs` | HID vs GCU fallback |
| `GpuGenerationMatrixTests.cs` / `GpuSwitchPayloadPerActionN16Tests.cs` | DisplayRouteMatrix actions |
| `PayloadParsingTests.cs` / `PortedOfficialSwitchTests.cs` | MQTT payload shapes |
| `FanBoostEncodingTests.cs` | Fan boost packets |
| `GcuConnectionStatusTests.cs` | MQTT status → UI |

**Risk**

1. **Failure:** Product crate copies Probe slot 5 (`UWPClient_5` / `clean_session false`) and kicks the official UI or fails CONNACK. **Mitigation:** Slot 4 constants live in `gcu-mqtt` only; a compile-time / test assertion rejects client ids ending in `_5`; Probe stays in `src/Probe`.
2. **Failure:** Lighting UI keys off MQTT Seen and shows Logo/lightbar on G16. **Mitigation:** Gate in `capabilities` crate from ItemSupport; UI consumes `LightingVisibility` DTO, never raw MQTT.
3. **Failure:** EC write uses 8-byte or 4-byte in-buffer; `DeviceIoControl` returns success and register does not change. **Mitigation:** Golden test asserts in-buffer length == 5 and addresses ∈ `{0x7B9, 0x7D0}` only; mock records every IOCTL.

**Must-NOT-Have (executor guardrails).**

- Do not delete, move, or “clean up” `src/MechrevoLiteWin`.
- Do not kill processes named `L-Mechrevo`, `GCUBridge`, `GCUService`.
- Do not write under `release\`.
- Do not put MQTT password, HID, IOCTL, or registry APIs in `app/src/` (TypeScript).
- Do not resurrect withdrawn features listed above.
- Do not `git commit` unless the user explicitly asks.
- Do not implement Plan B inside the Plan A stop condition — but do not drop Plan B rows from this document.

---

## Task Dependency Graph

| Task | Depends On | Reason |
|------|------------|--------|
| 1. Scaffold `app/` Tauri 2 react-ts workspace | None | Empty tree; create-tauri-app + Cargo workspace |
| 2. DESIGN.md web tokens + footer 6 keys | None | Design contract before any React component |
| 3. Extract golden JSON vectors from C# tests | None | TDD fixtures for crates; read-only on .NET |
| 4. `capabilities` crate + ItemSupport golden tests | 3 | Needs golden vectors |
| 5. `gcu-mqtt` handshake + payload golden tests | 3 | Needs topic/payload goldens; independent of HID/EC |
| 6. `hid-kb` crate + KeyboardLightPathPolicy tests | 3 | Independent of MQTT/EC |
| 7. `ec-acpi` crate + charge-limit goldens (never 0x7A6) | 3 | Independent of MQTT/HID |
| 8. Fake GCU broker + HID mock + EC ioctl mock | 5, 6, 7 | Mocks implement crate traits |
| 9. Tauri host: commands + events + AppManifest lock | 1, 4, 5, 6, 7 | Host wires crates; needs scaffold |
| 10. Shell UI: 420px dashboard gated by capabilities | 2, 9 | DESIGN.md + host snapshot events |
| 11. Plan A scenario contract (3 scenarios) | 8, 10 | End-to-end against fakes |
| 12. Performance modes + fan curve + LCHWOC + boost + custom PL | 11 | Plan B; needs runnable host |
| 13. GPU route matrix (no Auto; hot-swap 50-only) | 11 | Plan B |
| 14. Battery EC slider 40–100 | 11 | Plan B (crate exists; host+UI remaining) |
| 15. Lighting HID/GCU + lightbar + logo (ItemSupport gates) | 11 | Plan B |
| 16. Display Hz / brightness / calibration / overdrive / local dimming | 11 | Plan B |
| 17. Quick switches full table + Windows personalization + startup + monitor-off | 11 | Plan B |
| 18. Liquid cooling MQTT BT_LC + BLE NUS fallback | 11 | Plan B |
| 19. Overlay HUD, tray, footer 6 keys, settings dialog | 10 | Overlay can start after shell; full wiring Plan B |
| 20. Updates (stats.l-mechrevo.cn) + usage telemetry | 11 | Plan B |
| 21. First-run + unsupported-model read-only + diagnostic pack | 11 | Plan B |
| 22. NSIS perMachine + embedBootstrapper + hide console + tauri-plugin-log | 11 | Packaging after runnable app |
| F1. Plan A fake-broker window QA | 11 | Stop condition |
| F2. Official-row coverage audit (no dropped rows) | 12–21 | Plan B completeness |
| F3. Real-GCU verification wave (later, not this session) | 22 | Hardware; out of Plan A stop |

---

## Parallel Execution Graph

```
Wave 0 (Start immediately):
├── Task 1: Scaffold app/ Tauri 2 react-ts (no deps)
├── Task 2: DESIGN.md web tokens + footer 6 keys (no deps)
└── Task 3: Extract golden JSON vectors (no deps)

Wave 1 (After Wave 0):
├── Task 4: capabilities crate (depends: 3)
├── Task 5: gcu-mqtt handshake + payloads (depends: 3)
├── Task 6: hid-kb crate (depends: 3)
└── Task 7: ec-acpi crate (depends: 3)
    Task 1 scaffold should be done so crates live under app/src-tauri/crates/

Wave 2 (After Wave 1):
├── Task 8: Fake broker + HID mock + EC ioctl mock (depends: 5,6,7)
└── Task 9: Tauri host commands/events/AppManifest (depends: 1,4,5,6,7)

Wave 3 (After Wave 2):
└── Task 10: Shell UI gated dashboard (depends: 2, 9)

Wave 4 — Plan A stop (After Wave 3):
└── Task 11: Three scenario contracts + fake-broker window (depends: 8, 10)

Wave 5 — Plan B feature ports (After Plan A stop; parallel by subsystem):
├── Task 12: Performance / fan / LCHWOC (depends: 11)
├── Task 13: GPU routes (depends: 11)
├── Task 14: Battery slider host+UI (depends: 11)
├── Task 15: Lighting channels (depends: 11)
├── Task 16: Display (depends: 11)
├── Task 17: Quick switches (depends: 11)
├── Task 18: Liquid cooling (depends: 11)
├── Task 19: Overlay / tray / footer / settings (depends: 10)
├── Task 20: Updates + telemetry (depends: 11)
└── Task 21: First-run / unsupported / diagnostics (depends: 11)

Wave 6 — Packaging (After Plan B core):
└── Task 22: NSIS + hide console + log plugin (depends: 11; ideally after 12–21)

Wave F — Verification:
├── F1 after Task 11
├── F2 after Tasks 12–21
└── F3 later (real GCU); listed so it is not forgotten

Critical Path: 3 → 5 → 9 → 10 → 11 → (12…21) → 22
Estimated Parallel Speedup: Wave 0 and Wave 1 are ~3–4× vs sequential; Plan B subsystems ~60% faster in parallel.
```

**Plan A session stop:** Task 11 + F1 green. Plan B is a complete remaining-task list, not optional scope.

---

## Target tree (locked)

```
app/
  DESIGN.md                          # web tokens; footer 6 keys
  src/                               # React UI only (commands + events)
    app.css
    main.tsx
    App.tsx
    lib/api.ts                       # invoke/listen wrappers; no secrets
    lib/types.ts                     # DTOs mirroring host
    components/                      # Row, Segmented, Slider, Collapse, Footer
    sections/                        # Performance, Gpu, Display, Battery, Lighting, Switches, Lc
  src-tauri/
    Cargo.toml                       # workspace
    tauri.conf.json
    capabilities/default.json        # AppManifest::commands allowlist
    src/
      lib.rs                         # plugin + invoke handler
      main.rs                        # windows_subsystem
      commands/                      # one file per domain, ≤250 LOC
      events.rs
      secrets.rs                     # MQTT creds; never exported to JS
    crates/
      capabilities/
      gcu-mqtt/
      hid-kb/
      ec-acpi/
      nvapi-oc/                      # optional; stub until Task 12 OC path
    tests/fake_gcu/                  # in-process broker for host tests
```

Do not modify `src/MechrevoLiteWin/**` except if a golden extractor script lives under `app/` or `scripts/` and **reads** C# tests.

---

## IPC contract (locked)

**Commands (writes + snapshots). JS may call only these names. Lock in `src-tauri/capabilities/default.json` + `AppManifest`.**

| Command | Direction | Notes |
|---|---|---|
| `hw_snapshot` | read | Full gated DTO: modes, gpu, lighting visibility, switches, telemetry |
| `set_performance_mode` | write | Office/Gaming/Turbo/SilentTurbo/Custom |
| `set_fan_curve` / `set_fan_boost` / `set_custom_pl` | write | Fan/Control + LCHWOC |
| `set_gpu_mode` | write | iGPU / standard / dGPU; generation matrix; no Auto |
| `set_charge_limit` | write | 40–100; host → ec-acpi only |
| `set_lighting` | write | channel + effect; host picks HID vs GCU |
| `set_quick_switch` | write | key from allowlist |
| `set_display` | write | Hz, brightness, calibration, overdrive, local dimming |
| `set_liquid_cooling` | write | MQTT then BLE fallback |
| `overlay_set` / `tray_set` | write | Added features |
| `updates_check` / `telemetry_opt` | write | HTTPS only |
| `diagnostics_export` | write | zip pack |
| `first_run_ack` | write | |

**Events (host → UI).** `telemetry`, `mqtt_status`, `capabilities`, `hotplug`, `command_result`.

**Forbidden in JS:** rumqttc, hidapi, DeviceIoControl, windows-registry, MQTT password, slot numbers.

---

## Handshake (product, copy byte-for-byte)

Subscribe filters (await every SUBACK, reject `0x80`):

`Fan/#`, `Keyboard/#`, `HidLightbar/#`, `HidLightbar_Logo/#`, `Setting/#`, `Settings/#`, `System/#`, `GPUDevice/#`, `BT_LC/#`, `LCHWOC/#`

Then publish **in this order**:

1. `System/Control` `{Action:"System_ON"}`
2. GETSTATUS: `Fan/Control`, `Setting/Control`, `LCHWOC/Control`, `Keyboard/Ctrl`, `HidLightbar/Ctrl`, `HidLightbar_Logo/Ctrl`, `BT_LC/Control` each `{Action:"GETSTATUS"}`
3. `BatteryProtection/Control` `{Report:"GET"}`

Exit: `System_OFF` QoS0. Keepalive 3s. `clean_session true`. Client `UWPClient_4` only in the tray process.

---

## Scenario contract (minimum 3 — Task 11)

| ID | Kind | Given / When / Then |
|---|---|---|
| S1 | Happy | Given fake broker accepts slot 4. When host connects and UI sets Office. Then handshake order is SUBACK → System_ON → GETSTATUS… and broker sees `Fan/Control` `{Action:"OPERATING_OFFICE_MODE", ProfileIndex:0}` plus `LCHWOC/Control` `{IsNormalRun:0}`. Dashboard shows GCU Connected. |
| S2 | Edge | Given ItemSupport has Lightbar/RGB/Logo all unset/false, but fake broker publishes empty/nonempty HidLightbar status. When snapshot is rendered. Then lighting rows for lightbar and Logo are **absent**. Empty MQTT is not hardware. |
| S3 | Regression | Given `set_charge_limit(80)`. When ec-acpi mock records IOCTLs. Then writes are only `(0x7B9, 80)` and `(0x7D0, 75)` via `0x9C40A48C` 5-byte buffers. **Zero** writes to `0x7A6` or `0x78F`. |

Verify without a Mechrevo laptop: fake MQTT broker in tests, HID mock, EC ioctl mock. Real GCU = Task F3, not Plan A stop.

---

## Tasks

### Task 1: Scaffold `app/` Tauri 2 react-ts workspace

**Description**: Create `app/` with create-tauri-app (React + TypeScript), Cargo workspace members for crates, `windows_subsystem` stub, tauri-plugin-log file target. Do not delete .NET. Do not touch `release\`.

**Delegation Recommendation:**
- Category: `quick` - mechanical scaffold, one tree
- Skills: [`programming`] - Rust/TS manifests must follow strict toolchain rules

**Skills Evaluation:**
- INCLUDED `programming`: Cargo.toml / package.json / tsconfig are in scope
- OMITTED `frontend`: no UI components yet
- OMITTED `git-master`: no commit unless user asks
- OMITTED remaining catalog (bibliography, CTF, papers, opencode config, playwright, visual-qa, security-research, etc.): no domain overlap

**Depends On**: None

**Files:**
- Create: `app/` (entire create-tauri-app output)
- Create: `app/src-tauri/Cargo.toml` workspace with members `crates/capabilities`, `crates/gcu-mqtt`, `crates/hid-kb`, `crates/ec-acpi`
- Create: `app/src-tauri/src/main.rs` with `#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]`
- Modify: `app/src-tauri/tauri.conf.json` productName `L-Mechrevo`, identifier reserved; NSIS keys later in Task 22
- Must-not: `src/MechrevoLiteWin/**`, `release/**`

**TDD:** None (scaffold). QA is `cargo check` + `bun run build` in `app/`.

**Acceptance Criteria**:
- `app/src-tauri` builds empty window
- Workspace members exist as stub crates
- `.NET` tree unchanged (`git status` shows no deletes under `src/MechrevoLiteWin` or `release`)

**Verification:** `cd app/src-tauri; cargo check` and `cd app; bun install; bun run build`

---

### Task 2: DESIGN.md web tokens + footer 6 keys

**Description**: Before any React component: copy Deep Console tokens into `app/DESIGN.md`, update root `DESIGN.md` §0.1 footer to the live 6 keys (悬浮窗 / 设置 / 更新 / 诊断 / 赞助 / 退出) plus version. 420 logical px. Collapse groups unchanged. No new visual language.

**Delegation Recommendation:**
- Category: `visual-engineering` - design-system contract
- Skills: [`frontend`, `ui-ux-pro-max`] - DESIGN.md is the implementation contract

**Skills Evaluation:**
- INCLUDED `frontend`: DESIGN.md before components is mandatory
- INCLUDED `ui-ux-pro-max`: token/spacing/type scale already in root DESIGN.md
- OMITTED `programming`: markdown only
- OMITTED remaining catalog: no domain overlap

**Depends On**: None

**Files:**
- Modify: `DESIGN.md` §0.1 footer diagram and §0.4 mapping — add 诊断 as 6th footer key
- Create: `app/DESIGN.md` with Night/Day palettes, TypeScale (map pt → rem: Caption 11px, Body 12px, Subtitle 13px, Title 16px, Display 20px), Space 4/8/12/16/24, 420px width, footer 6 keys, anti-slop bans (no emoji icons, no purple, no glass)

**TDD:** None. QA: `app/DESIGN.md` contains `420`, `#0B1220`, `诊断`, and `LightbarSupport`.

**Acceptance Criteria**: Footer documents 6 keys including 诊断. Tokens match `UiVisualStyle`. No component files yet.

**Verification:** Read `DESIGN.md` and `app/DESIGN.md`; grep footer keys.

---

### Task 3: Extract golden JSON vectors from C# tests

**Description**: Read-only extraction of payload / capability / EC / keyboard-policy fixtures into `app/src-tauri/crates/_golden/`. Do not change C# tests.

**Delegation Recommendation:**
- Category: `deep` - one goal: golden corpus
- Skills: [`programming`] - typed JSON fixtures

**Skills Evaluation:**
- INCLUDED `programming`: fixture schema
- OMITTED `frontend`: no UI
- OMITTED remaining catalog: no domain overlap

**Depends On**: None

**Files (create):**
- `app/src-tauri/crates/_golden/item_support_g16_no_lightbar.json`
- `app/src-tauri/crates/_golden/item_support_logo_true.json`
- `app/src-tauri/crates/_golden/feature_matrix_fail_closed.json`
- `app/src-tauri/crates/_golden/mqtt/operating_office.json` → `{Action:"OPERATING_OFFICE_MODE", ProfileIndex:0}`
- `app/src-tauri/crates/_golden/mqtt/lchwoc_office.json` → `{IsNormalRun:0}`
- `app/src-tauri/crates/_golden/mqtt/handshake_order.json`
- `app/src-tauri/crates/_golden/ec/charge_limit_80.json` → writes `[{addr:0x7B9,value:80},{addr:0x7D0,value:75}]`
- `app/src-tauri/crates/_golden/ec/forbidden_addrs.json` → `[0x7A6, 0x78F]`
- `app/src-tauri/crates/_golden/kb/path_policy.json`
- `app/src-tauri/crates/_golden/gpu/gen40_actions.json`
- `app/src-tauri/crates/_golden/gpu/gen50_hotswap.json`

**TDD:** Goldens are the tests’ expected values. Extract from C# assertions, not from memory.

**Acceptance Criteria**: Each JSON is loadable; handshake_order lists System_ON before GETSTATUS; forbidden_addrs includes 0x7A6 and 0x78F.

**Verification:** `Get-Content` / `jq` parse; no writes under `src/MechrevoLiteWin`.

---

### Task 4: `capabilities` crate + ItemSupport golden tests

**Description**: Port `FeatureMatrix` + `MechrevoDeviceCapabilities.FromValues` lighting/GPU/fan bits. Fail-closed. Lighting visibility = ItemSupport, not MQTT Seen.

**Delegation Recommendation:**
- Category: `ultrabrain` - protocol/capability matrix
- Skills: [`programming`] - typed Rust, TDD

**Skills Evaluation:**
- INCLUDED `programming`: `.rs` crate
- OMITTED `frontend`: no UI
- OMITTED remaining catalog: no domain overlap

**Depends On**: Task 3

**Files:**
- Create: `app/src-tauri/crates/capabilities/src/{lib.rs,matrix.rs,item_support.rs,lighting_gate.rs,gpu_gen.rs}` each ≤250 LOC
- Test: `app/src-tauri/crates/capabilities/tests/lighting_gate.rs`

**TDD:**
1. Red: `lighting_visibility_hides_logo_when_itemsupport_false` loads G16 golden → expects `logo=false`, `lightbar=false` even if MQTT seen flags are true
2. Green: `LightingGate::from_item_support`
3. Red/green: FeatureMatrix missing `LightbarSupport` → unsupported

**Acceptance Criteria**: G16 golden hides Logo/lightbar. Empty registry → all fail-closed bits false except vendor-constant-on bits matching C# `FeatureMissingPolicy.VendorConstantOn` (Keyboard, SystemMonitor, AcRecoverySwitch).

**Verification:** `cargo test -p capabilities`

---

### Task 5: `gcu-mqtt` handshake + payload golden tests

**Description**: rumqttc 0.25 MQTT 3.1.1 client. Slot 4 only. Handshake order locked. Payload encode/decode from goldens. Trait `MqttTransport` so tests use an in-memory fake.

**Delegation Recommendation:**
- Category: `ultrabrain` - protocol correctness
- Skills: [`programming`] - Rust async + types

**Skills Evaluation:**
- INCLUDED `programming`: crate + tests
- OMITTED remaining catalog: no domain overlap

**Depends On**: Task 3 (and Task 1 for workspace membership)

**Files:**
- Create: `app/src-tauri/crates/gcu-mqtt/src/{lib.rs,topics.rs,handshake.rs,payloads.rs,client.rs,fake.rs}`
- `topics.rs` must copy `MqttTopics.cs` strings byte-for-byte including `HidLightbar_Logo/#`
- `secrets` stay in host `secrets.rs`, not this crate’s public API — crate takes `ConnectParams` from host
- Test: `tests/handshake_order.rs`, `tests/payload_office.rs`, `tests/reject_slot5.rs`

**TDD:**
1. Red: handshake recorder expects `["subscribe:*","pub:System/Control System_ON", "pub:Fan/Control GETSTATUS", …]`
2. Red: `ConnectParams{client_id:"UWPClient_5"}` rejected
3. Green: encoder for Office + LCHWOC IsNormalRun=0 matches golden JSON
4. Keepalive 3s and `clean_session true` asserted on options builder

**Acceptance Criteria**: Slot 5 cannot be constructed. Handshake order matches lock. Topics byte-identical to C#.

**Verification:** `cargo test -p gcu-mqtt`

---

### Task 6: `hid-kb` crate + KeyboardLightPathPolicy tests

**Description**: Port policy + HID enumerate VID 048D usage FF03. Production uses hidapi 2.6 windows-native behind a `HidTransport` trait. Tests never open real HID.

**Delegation Recommendation:**
- Category: `ultrabrain` - HID protocol + policy
- Skills: [`programming`]

**Skills Evaluation:**
- INCLUDED `programming`: `.rs`
- OMITTED remaining catalog: no domain overlap

**Depends On**: Task 3

**Files:**
- Create: `app/src-tauri/crates/hid-kb/src/{lib.rs,policy.rs,ite8291.rs,fake.rs}`
- Test: `tests/path_policy.rs` from `KeyboardLightPathPolicy.cs:23-32`

**TDD:** Table-driven:

| hid | connected | service | brightness_ok | use_gcu |
|---|---|---|---|---|
| Unsupported | false | true | * | true |
| Supported | true | true | false | true |
| Supported | true | true | true | false |
| * | * | false | * | false |
| Unknown | true | true | false | false |

**Acceptance Criteria**: Matches C# `ShouldUseGcuKeyboardFallback`. Fake HID records feature reports; no real device required.

**Verification:** `cargo test -p hid-kb`

---

### Task 7: `ec-acpi` crate + charge-limit goldens (never 0x7A6)

**Description**: Port `EcChargeLimit`. Device `\\.\ACPIDriver`. Read `0x9C40A488` 4/16. Write `0x9C40A48C` 5-byte. Addresses **only** 0x7B9 and 0x7D0. `IoctlTransport` trait for mock.

**Delegation Recommendation:**
- Category: `ultrabrain` - IOCTL layout is a known footgun
- Skills: [`programming`]

**Skills Evaluation:**
- INCLUDED `programming`: windows-sys DeviceIoControl
- OMITTED remaining catalog: no domain overlap

**Depends On**: Task 3

**Files:**
- Create: `app/src-tauri/crates/ec-acpi/src/{lib.rs,ioctl.rs,charge_limit.rs,fake.rs}`
- Test: `tests/charge_limit_encode.rs`, `tests/forbidden_addrs.rs`

**TDD:**
1. Red: `value_for(100)==0`, `lower_value_for(80)==75`, `percent_for(0)==100`
2. Red: mock write of 80 records two 5-byte buffers; `in_size==5`
3. Red: crate API has no function that accepts address 0x7A6; `forbidden_addrs` test scans source / allowlist
4. Green: `TrySet` rollback if lower write fails (record previous)

**Acceptance Criteria**: S3 regression is already true at crate level. No 0x7A6/0x78F symbols in this crate except the forbidden-list constant.

**Verification:** `cargo test -p ec-acpi`

---

### Task 8: Fake GCU broker + HID mock + EC ioctl mock (host test harness)

**Description**: In-process MQTT test double that accepts slot 4, records publish order, replies canned Fan/Status for Office. Wire HID/EC fakes from crates. Used by host integration tests and `LMECHREVO_FAKE_GCU=1` runtime.

**Delegation Recommendation:**
- Category: `deep` - one deliverable: fake harness
- Skills: [`programming`]

**Skills Evaluation:**
- INCLUDED `programming`: async test harness
- OMITTED remaining catalog: no domain overlap

**Depends On**: Tasks 5, 6, 7

**Files:**
- Create: `app/src-tauri/crates/gcu-mqtt/src/fake_broker.rs` (if not already in Task 5)
- Create: `app/src-tauri/tests/fake_gcu.rs`
- Create: `app/src-tauri/src/hw_backend.rs` enum `Real | Fake`

**TDD:** Fake broker test: connect slot 4 → handshake → Office publish recorded.

**Acceptance Criteria**: `LMECHREVO_FAKE_GCU=1` does not open 13688 on the real GCU; uses in-process fake. Slot 5 connect attempt fails.

**Verification:** `cargo test -p gcu-mqtt --test fake_broker` (or host test)

---

### Task 9: Tauri host commands + events + AppManifest lock

**Description**: Wire crates into Tauri. `hw_snapshot` + `mqtt_status` event. Commands allowlisted. MQTT password only in `secrets.rs`. JS `lib/api.ts` invoke wrappers.

**Delegation Recommendation:**
- Category: `deep` - host glue
- Skills: [`programming`]

**Skills Evaluation:**
- INCLUDED `programming`: Rust + TS types
- OMITTED `frontend`: no visual components yet (api.ts only)
- OMITTED remaining catalog: no domain overlap

**Depends On**: Tasks 1, 4, 5, 6, 7

**Files:**
- Create: `app/src-tauri/src/{lib.rs,commands/snapshot.rs,commands/mode.rs,events.rs,secrets.rs,hw_backend.rs}`
- Create: `app/src/lib/{api.ts,types.ts}`
- Modify: `app/src-tauri/capabilities/default.json` — exact command list
- Modify: `app/src-tauri/src/main.rs`

**TDD:** Host test with Fake backend: `hw_snapshot` returns `mqtt: Connecting then Connected` after handshake; snapshot.lighting from capabilities not MQTT.

**Acceptance Criteria**: `rg "UWPClient_Pwd" app/src` finds nothing. `rg "UWPClient_5" app/src-tauri/src` finds nothing. Commands not in allowlist cannot be invoked.

**Verification:** `cargo test -p l-mechrevo-tauri` (app package name) && `rg` guards

---

### Task 10: Shell UI — 420px gated dashboard

**Description**: React shell per `app/DESIGN.md`: performance segmented control, fan telemetry row, GPU row placeholder, screen row placeholder, battery slider placeholder, collapse groups (液冷/灯光/更多开关) gated by snapshot. Footer 6 keys. GCU status pill. No MQTT in JS.

**Delegation Recommendation:**
- Category: `visual-engineering`
- Skills: [`frontend`, `programming`, `visual-qa`]

**Skills Evaluation:**
- INCLUDED `frontend`: UI implementation
- INCLUDED `programming`: TSX
- INCLUDED `visual-qa`: after first paint
- OMITTED remaining catalog: no domain overlap

**Depends On**: Tasks 2, 9

**Files:**
- Create: `app/src/components/{Row.tsx,Segmented.tsx,Slider.tsx,Collapse.tsx,Footer.tsx,StatusPill.tsx}`
- Create: `app/src/sections/{Performance.tsx,Lighting.tsx}`
- Create: `app/src/app.css` tokens from DESIGN.md
- Modify: `app/src/App.tsx` width 420px

**TDD:** Vitest: Lighting section receives `{lightbar:false, logo:false}` → queryByText 灯条 / Logo is null. Footer has 6 buttons including 诊断.

**Acceptance Criteria**: Window 420 logical px. Night palette. Footer: 悬浮窗 设置 更新 诊断 赞助 退出. Lighting rows follow ItemSupport DTO.

**Verification:** `cd app; bun test` then visual-qa 420px screenshot vs DESIGN.md

---

### Task 11: Plan A scenario contract (S1, S2, S3) — session stop

**Description**: One host+UI path against fakes covering the three scenarios. This is the Plan A stop condition.

**Delegation Recommendation:**
- Category: `deep` - one deliverable: three green scenarios
- Skills: [`programming`, `frontend`]

**Skills Evaluation:**
- INCLUDED `programming`: integration tests
- INCLUDED `frontend`: UI assertion for S2
- OMITTED remaining catalog: no domain overlap

**Depends On**: Tasks 8, 10

**Files:**
- Create: `app/src-tauri/tests/scenario_s1_office.rs`
- Create: `app/src-tauri/tests/scenario_s2_g16_lighting.rs`
- Create: `app/src-tauri/tests/scenario_s3_charge_limit.rs`
- Create: `app/src/sections/Lighting.test.tsx` (S2 UI)

**TDD:** Write failing scenario tests first; they should fail until Tasks 8–10 are correct; do not weaken assertions.

**Acceptance Criteria**:
- S1: handshake order + Office + LCHWOC IsNormalRun=0
- S2: no lightbar/Logo rows
- S3: IOCTL log has only 0x7B9/0x7D0, 5-byte writes, never 0x7A6
- `cargo tauri dev` with `LMECHREVO_FAKE_GCU=1` shows gated dashboard and GCU Connected

**Verification:**
```
cd app/src-tauri; cargo test --test scenario_s1_office --test scenario_s2_g16_lighting --test scenario_s3_charge_limit
cd app; bun test
$env:LMECHREVO_FAKE_GCU=1; cargo tauri dev
```

---

### Task 12: Performance modes + fan curve + boost + custom PL/TCC/TGP/OC

**Description**: Port Official performance block. Fan/Control actions + LCHWOC IsNormalRun/IsCustomRun. SilentTurbo via `SET_CPU_CORE_OFFSET_SILENT`. Fan boost encoding from C# tests. Custom PL1/PL2/PL4, TCC, TGP, GPU OC offsets. No RamFan UI. No mid fan. No whisper.

**Delegation Recommendation:**
- Category: `ultrabrain` - protocol
- Skills: [`programming`, `frontend`]

**Skills Evaluation:**
- INCLUDED `programming`: payloads
- INCLUDED `frontend`: Performance + fan curve UI
- OMITTED remaining catalog: no domain overlap

**Depends On**: Task 11

**Files:** `gcu-mqtt` payloads + `app/src-tauri/src/commands/mode.rs` + `app/src/sections/Performance.tsx` + fan-curve dialog. Goldens from `FanBoostEncodingTests`, FEATURES.md §1.

**TDD:** Encode/decode each mode pair (Fan + LCHWOC). Fan curve 16-point SET_FAN_SPEED_CURVE_SETTING golden.

**Acceptance Criteria**: Office/Gaming/Turbo/SilentTurbo/Custom all emit both packets. UI has no RamFan, no mid fan, no whisper, no GPU Auto.

**Verification:** `cargo test -p gcu-mqtt` + bun test Performance

---

### Task 13: GPU route matrix (no Auto; hot-swap 50-only)

**Description**: Port `DisplayRouteMatrix`. Confirm Setting/Status + GPUDevice/Status. Gen30: ON/OFF only. Gen40: three-mode where proven. Gen50: hot swap. No Auto button (`IGPU_ONLY_CONNECT_RB_AUTO` not offered).

**Delegation Recommendation:**
- Category: `ultrabrain`
- Skills: [`programming`, `frontend`]

**Skills Evaluation:**
- INCLUDED `programming` + `frontend`
- OMITTED remaining catalog: no domain overlap

**Depends On**: Task 11

**Files:** `capabilities` gpu_gen + `commands/gpu.rs` + `sections/Gpu.tsx`. Goldens from `GpuSwitchPayloadPerActionN16Tests`, `GpuGenerationMatrixTests`.

**TDD:** Per-generation allowed actions table. Hot-swap command only when both ItemSupport flags true.

**Acceptance Criteria**: UI options ⊆ matrix. Auto absent. Restart confirm for dGPU direct.

**Verification:** `cargo test -p capabilities` + gpu command tests

---

### Task 14: Battery EC slider 40–100 (host + UI)

**Description**: Snapshot + slider wired to `ec-acpi`. No MQTT 3-mode. Fail visible on ioctl failure.

**Delegation Recommendation:**
- Category: `quick` - crate exists; wire + UI
- Skills: [`programming`, `frontend`]

**Skills Evaluation:**
- INCLUDED `programming` + `frontend`
- OMITTED remaining catalog: no domain overlap

**Depends On**: Task 11

**Files:** `commands/battery.rs`, `sections/Battery.tsx`

**TDD:** S3 already exists; add UI test slider 80 → invoke `set_charge_limit` 80.

**Acceptance Criteria**: Slider 40–100 step 1. No PERFORMANCEDMODE/BALANCEDMODE/HEALTHYMODE publishes.

**Verification:** `rg "PERFORMANCEDMODE" app/src-tauri` empty except comments/goldens-as-forbidden

---

### Task 15: Lighting HID/GCU + lightbar + logo (ItemSupport gates)

**Description**: Keyboard via hid-kb policy. Lightbar/Logo MQTT only if capabilities say so. Effects: HID 10 / GCU 11 as DESIGN.md. No hinge/sync.

**Delegation Recommendation:**
- Category: `ultrabrain`
- Skills: [`programming`, `frontend`]

**Skills Evaluation:**
- INCLUDED `programming` + `frontend`
- OMITTED remaining catalog: no domain overlap

**Depends On**: Task 11

**Files:** `commands/lighting.rs`, `sections/Lighting.tsx`, persist `lightbar.cfg`/`logolight.cfg`/`keyboard.cfg` under `%AppData%\MechrevoLite` (same as C#).

**TDD:** Policy tests already in hid-kb; add publish tests for HidLightbar/Ctrl only when gate true.

**Acceptance Criteria**: G16 hides rows. Hinge/Sync strings absent from UI.

**Verification:** bun test Lighting + cargo test lighting commands

---

### Task 16: Display Hz, brightness, calibration, overdrive, local dimming

**Description**: Port display Official rows. Calibration/overdrive/local dimming in settings dialog “显示” per DESIGN.md. Hz + brightness on main screen row. No `DISPLAY_*_MODE` send. No `NV_CTRL_PANEL` send.

**Delegation Recommendation:**
- Category: `deep`
- Skills: [`programming`, `frontend`]

**Skills Evaluation:**
- INCLUDED `programming` + `frontend`
- OMITTED remaining catalog: no domain overlap

**Depends On**: Task 11

**Files:** `commands/display.rs`, `sections/Screen.tsx`, settings dialog display block

**TDD:** Hz payload is **string** (hardware README). Brightness commit queue behavior from `BrightnessCommitQueueTests`.

**Acceptance Criteria**: HDR on disables calibration (C# rule). Forbidden display-mode actions never published.

**Verification:** payload tests + bun test Screen

---

### Task 17: Quick switches full table + personalization + startup + monitor-off

**Description**: Port `SupportsQuickSwitch` table. Touchpad unconditional. wifi/bt/webcam Seen. winkey/fn/copilot/osd/usb/highperf/acrecovery/cpuadvperf/gamewhitelist/deepsleep. Windows personalization (`ShellPersonalization`). Startup task. Monitor-off Added. Field name `TochpadEnable` (missing u) must be copied.

**Delegation Recommendation:**
- Category: `deep`
- Skills: [`programming`, `frontend`]

**Skills Evaluation:**
- INCLUDED `programming` + `frontend`
- OMITTED remaining catalog: no domain overlap

**Depends On**: Task 11

**Files:** `commands/switches.rs`, `sections/MoreSwitches.tsx`, `shell_personalization.rs`, `startup.rs`

**TDD:** Goldens from `PortedOfficialSwitchTests`, `ShellPersonalizationTests`, `AutostartRebootTests`.

**Acceptance Criteria**: Every key in `SupportsQuickSwitch` has a UI row gated the same way. Whisper absent. Uni/Omni mutually exclusive.

**Verification:** table-driven switch tests

---

### Task 18: Liquid cooling MQTT BT_LC + BLE NUS fallback

**Description**: Port BT_LC MQTT + `WaterCoolerBle` NUS. Collapse group hidden when `LiquidCoolingSupport` false.

**Delegation Recommendation:**
- Category: `ultrabrain`
- Skills: [`programming`, `frontend`]

**Skills Evaluation:**
- INCLUDED `programming` + `frontend`
- OMITTED remaining catalog: no domain overlap

**Depends On**: Task 11

**Files:** `crates` maybe `bt-lc` or module under host; `sections/LiquidCooling.tsx`

**TDD:** Fake MQTT LC status; BLE path mocked (no real radio).

**Acceptance Criteria**: MQTT first; BLE fallback when MQTT LC disconnected and capability true.

**Verification:** cargo test lc + bun test

---

### Task 19: Overlay HUD, tray, footer 6 keys, settings dialog

**Description**: Overlay HUD (Added), system tray, footer actions, settings dialog (theme, display extras, official-console isolation status). Isolation is status+button like C#; do not kill GCU.

**Delegation Recommendation:**
- Category: `visual-engineering`
- Skills: [`frontend`, `programming`, `visual-qa`]

**Skills Evaluation:**
- INCLUDED `frontend` + `programming` + `visual-qa`
- OMITTED remaining catalog: no domain overlap

**Depends On**: Task 10

**Files:** overlay window (second Tauri webview or overlay crate), tray in `lib.rs`, `sections/SettingsDialog.tsx`, Footer handlers

**TDD:** Footer has 6 keys. Overlay telemetry uses snapshot events not a second MQTT client.

**Acceptance Criteria**: Footer: 悬浮窗 设置 更新 诊断 赞助 退出. Overlay does not create MQTT slot 5.

**Verification:** bun test Footer + visual-qa

---

### Task 20: Updates (stats.l-mechrevo.cn) + usage telemetry

**Description**: Port `UpdateChecker` HTTPS rules and `UsageTelemetry`. Host-only HTTP. JS only gets `{updateAvailable, latestVersion}` DTO.

**Delegation Recommendation:**
- Category: `deep`
- Skills: [`programming`]

**Skills Evaluation:**
- INCLUDED `programming`
- OMITTED remaining catalog: no domain overlap

**Depends On**: Task 11

**Files:** `commands/updates.rs`, `telemetry.rs`. Goldens from `UpdateCheckerTests`, `UsageTelemetryTests`.

**TDD:** Reject http non-loopback download_url. Version compare uses client version not server current_version.

**Acceptance Criteria**: Same URL shape `https://stats.l-mechrevo.cn/api/update_check.php?version=&channel=`.

**Verification:** cargo test updates

---

### Task 21: First-run, unsupported-model read-only, diagnostic pack

**Description**: First-run guide. Unsupported model → all writes disabled, snapshot still shown. Diagnostic pack zip (logs, snapshot, ItemSupport dump) — no secrets in zip if possible; MQTT password never included.

**Delegation Recommendation:**
- Category: `deep`
- Skills: [`programming`, `frontend`]

**Skills Evaluation:**
- INCLUDED `programming` + `frontend`
- OMITTED remaining catalog: no domain overlap

**Depends On**: Task 11

**Files:** `commands/diagnostics.rs`, first-run modal, read-only banner. Goldens from `FirstUseGuideN10Tests`, `DiagnosticPackTests`.

**TDD:** Unsupported → `set_performance_mode` returns error without publish. Zip file list excludes `secrets.rs` values.

**Acceptance Criteria**: Read-only mode is obvious. Diagnostic pack generates without real GCU (fake snapshot).

**Verification:** cargo test diagnostics + bun test first-run

---

### Task 22: NSIS perMachine + embedBootstrapper + hide console + tauri-plugin-log

**Description**: Bundler config. `windows_subsystem` already in Task 1. File logger. Do not write into `release\` of the .NET tree; Tauri `target/` / `app/src-tauri/target` only.

**Delegation Recommendation:**
- Category: `unspecified-high`
- Skills: [`programming`]

**Skills Evaluation:**
- INCLUDED `programming`: tauri.conf.json
- OMITTED remaining catalog: no domain overlap

**Depends On**: Task 11 (can complete after Plan B)

**Files:** `app/src-tauri/tauri.conf.json` bundle.nsis: `perMachine` + `embedBootstrapper`; plugin-log to `%AppData%\MechrevoLite\logs`.

**TDD:** Config unit test or snapshot of tauri.conf keys.

**Acceptance Criteria**: nsis perMachine true, embedBootstrapper true, release subsystem windows, log files created in fake run.

**Verification:** inspect tauri.conf.json; `cargo tauri build --debug` does not touch repo `release\`

---

### Task F1: Plan A fake-broker window QA

**Description**: Run the app against fake GCU; confirm window, GCU pill, gated rows, S1–S3.

**Delegation Recommendation:**
- Category: `unspecified-high`
- Skills: [`programming`, `visual-qa`]

**Skills Evaluation:**
- INCLUDED `programming` + `visual-qa`
- OMITTED remaining catalog: no domain overlap

**Depends On**: Task 11

**Acceptance Criteria**: Evidence: test output + screenshot of 420px dashboard. No real 13688 required.

**Verification:** commands in Task 11 + screenshot path `artifacts/tauri-plan-a/`

---

### Task F2: Official-row coverage audit (no dropped rows)

**Description**: Checklist FEATURES.md Official rows 1–8 plus Added list against Plan B tasks. Fail if any Official row lacks a task/file.

**Delegation Recommendation:**
- Category: `writing`
- Skills: [`programming`] - trace code paths

**Skills Evaluation:**
- INCLUDED `programming`: trace
- OMITTED remaining catalog: no domain overlap

**Depends On**: Tasks 12–21

**Acceptance Criteria**: Table in PR/plan notes maps every Official row → file. Withdrawn rows explicitly absent.

**Verification:** grep FEATURES.md actions vs `app/src-tauri/crates/gcu-mqtt`

---

### Task F3: Real-GCU verification wave (later)

**Description**: Not this session. On a Mechrevo laptop with GCU running: slot 4 coexistence with official UI isolated, handshake, Office mode, charge slider, lighting gates vs ItemSupport. Do not use slot 5.

**Delegation Recommendation:**
- Category: `unspecified-high`
- Skills: [`debugging`]

**Skills Evaluation:**
- INCLUDED `debugging`: runtime
- OMITTED remaining catalog until hardware is present

**Depends On**: Task 22

**Acceptance Criteria**: Listed so it cannot be dropped. Not a Plan A gate.

**Verification:** first-machine runbook analogue for Tauri

---

## Official + Added coverage map (nothing dropped)

| Inventory | Plan task |
|---|---|
| 1 Performance Office/Gaming/Turbo/SilentTurbo/Custom + Fan/LCHWOC + curve + boost + custom PL/TCC/TGP/OC | 12 |
| 2 GPU iGPU/standard/dGPU per matrix; hot-swap 50-only; no Auto | 13 |
| 3 Battery EC slider only | 7 + 14 |
| 4 Lighting keyboard HID/GCU, lightbar, logo; ItemSupport gates | 4 + 6 + 15 |
| 5 Display Hz, brightness, calibration, overdrive, local dimming | 16 |
| 6 Quick switches full table + personalization + startup + monitor-off | 17 |
| 7 Liquid cooling MQTT + BLE NUS | 18 |
| 8 Overlay, tray, footer 6 keys, settings, updates, telemetry | 19 + 20 |
| 9 First-run, unsupported read-only, diagnostic pack | 21 |
| Handshake + slot 4 | 5 + 9 + 11 |
| Packaging | 22 |

Withdrawn (must stay absent): whisper, official battery 3-mode, DISPLAY_*_MODE, NV_CTRL_PANEL, Ally/XGM/AniMe, GPU Auto, RamFan UI, mid fan, hinge/sync lightbar, probe slot 5.

---

## How to verify without a Mechrevo laptop

| Seam | Fake | Used by |
|---|---|---|
| MQTT | In-process `FakeBroker` (slot 4, records pubs, canned Fan/Status) | gcu-mqtt + host + S1/S2 |
| HID | `HidTransport` mock | hid-kb + lighting |
| EC | `IoctlTransport` mock recording (code, in_bytes) | ec-acpi + S3 |
| Registry ItemSupport | HashMap fixture goldens | capabilities + S2 |
| BLE | mock NUS | Task 18 |
| NVAPI | libloading not loaded in Fake backend | Task 12 OC skip |

Runtime: `LMECHREVO_FAKE_GCU=1` selects Fake backend. Never open real `127.0.0.1:13688` in CI.

---

## Commit Strategy

User locked: **no git commit unless the user asks.** Workers implement and stop. If later asked to commit, use these atomic commits (do not squash Plan A with Plan B):

1. `chore(app): scaffold Tauri 2 react-ts workspace` (Task 1)
2. `docs(design): web tokens and footer 6 keys` (Task 2)
3. `test(golden): extract C# protocol vectors` (Task 3)
4. `feat(capabilities): ItemSupport fail-closed lighting gates` (Task 4)
5. `feat(gcu-mqtt): slot-4 handshake and payload codecs` (Task 5)
6. `feat(hid-kb): ITE8291 path policy` (Task 6)
7. `feat(ec-acpi): charge limit 0x7B9/0x7D0 5-byte ioctl` (Task 7)
8. `feat(host): Tauri commands events fake backend` (Tasks 8–9)
9. `feat(ui): 420px gated dashboard shell` (Task 10)
10. `test(plan-a): S1 office S2 g16 lighting S3 no 0x7A6` (Task 11)
11. One commit per Plan B domain (Tasks 12–21)
12. `build: nsis perMachine embedBootstrapper file log` (Task 22)

Never commit secrets as literals in JS. MQTT password may exist in Rust `secrets.rs` matching C# (already public in-repo). Do not add slot 5.

---

## Success Criteria

**Plan A (this session stop):**
- `app/` exists; `.NET` and `release\` untouched
- `cargo test` green for capabilities, gcu-mqtt, hid-kb, ec-acpi
- S1, S2, S3 green
- `LMECHREVO_FAKE_GCU=1 cargo tauri dev` shows 420px dashboard, GCU Connected, lighting gated
- `rg UWPClient_5 app/src-tauri/src` empty; `rg UWPClient_Pwd app/src` empty

**Plan B (later session, listed so nothing is dropped):**
- Every Official row in the coverage map has implementation + tests
- Withdrawn features absent
- NSIS perMachine + embedBootstrapper
- F2 audit passes
- F3 real GCU is a separate wave

**Polar star:** On a machine where official console feature X works, the rewrite’s host offers X (gated the same way). Empty MQTT never invents hardware.

---

## Todos

- [ ] 1. Scaffold `app/` Tauri 2 react-ts workspace
- [ ] 2. DESIGN.md web tokens + footer 6 keys
- [ ] 3. Extract golden JSON vectors from C# tests
- [ ] 4. `capabilities` crate + ItemSupport golden tests
- [ ] 5. `gcu-mqtt` handshake + payload golden tests
- [ ] 6. `hid-kb` crate + KeyboardLightPathPolicy tests
- [ ] 7. `ec-acpi` crate + charge-limit goldens (never 0x7A6)
- [ ] 8. Fake GCU broker + HID mock + EC ioctl mock
- [ ] 9. Tauri host commands + events + AppManifest lock
- [ ] 10. Shell UI 420px gated dashboard
- [ ] 11. Plan A scenarios S1/S2/S3 + fake-broker window
- [ ] 12. Performance modes + fan curve + LCHWOC + boost + custom PL
- [ ] 13. GPU route matrix (no Auto; hot-swap 50-only)
- [ ] 14. Battery EC slider 40–100 host+UI
- [ ] 15. Lighting HID/GCU + lightbar + logo ItemSupport gates
- [ ] 16. Display Hz / brightness / calibration / overdrive / local dimming
- [ ] 17. Quick switches full table + personalization + startup + monitor-off
- [ ] 18. Liquid cooling MQTT BT_LC + BLE NUS fallback
- [ ] 19. Overlay HUD, tray, footer 6 keys, settings dialog
- [ ] 20. Updates stats.l-mechrevo.cn + usage telemetry
- [ ] 21. First-run + unsupported-model read-only + diagnostic pack
- [ ] 22. NSIS perMachine + embedBootstrapper + hide console + tauri-plugin-log

## Final verification wave

- [ ] F1. Plan A fake-broker window QA
- [ ] F2. Official-row coverage audit (no dropped rows)
- [ ] F3. Real-GCU verification wave (later; listed so it is not dropped)

---

## TODO List (ADD THESE)

> CALLER: Add these TODOs using TodoWrite/TaskCreate and execute by wave.

### Wave 0 (Start Immediately - No Dependencies)

- [ ] **1. Scaffold `app/` Tauri 2 react-ts workspace**
  - What: create-tauri-app react-ts in `app/`; Cargo workspace members; `windows_subsystem`; do not touch `.NET` or `release\`
  - Depends: None
  - Blocks: 9, 10
  - Category: `quick`
  - Skills: [`programming`]
  - QA: `cd app/src-tauri; cargo check` and `cd app; bun run build`

- [ ] **2. DESIGN.md web tokens + footer 6 keys**
  - What: Update root DESIGN.md footer; write `app/DESIGN.md` tokens; 420px; 诊断 included
  - Depends: None
  - Blocks: 10
  - Category: `visual-engineering`
  - Skills: [`frontend`, `ui-ux-pro-max`]
  - QA: grep `诊断` and `#0B1220` and `420` in `app/DESIGN.md`

- [ ] **3. Extract golden JSON vectors from C# tests**
  - What: Read-only extract into `app/src-tauri/crates/_golden/`
  - Depends: None
  - Blocks: 4, 5, 6, 7
  - Category: `deep`
  - Skills: [`programming`]
  - QA: JSON parse; handshake_order has System_ON before GETSTATUS; forbidden_addrs has 0x7A6

### Wave 1 (After Wave 0 Completes)

- [ ] **4. capabilities crate + ItemSupport golden tests**
  - What: Port FeatureMatrix + lighting gate fail-closed; G16 hides Logo/lightbar
  - Depends: 3
  - Blocks: 9
  - Category: `ultrabrain`
  - Skills: [`programming`]
  - QA: `cargo test -p capabilities`

- [ ] **5. gcu-mqtt handshake + payload golden tests**
  - What: rumqttc 0.25 slot 4; SUBACK then System_ON then GETSTATUS; reject slot 5
  - Depends: 3
  - Blocks: 8, 9
  - Category: `ultrabrain`
  - Skills: [`programming`]
  - QA: `cargo test -p gcu-mqtt`

- [ ] **6. hid-kb crate + KeyboardLightPathPolicy tests**
  - What: VID 048D FF03; policy table; HidTransport mock
  - Depends: 3
  - Blocks: 8, 9
  - Category: `ultrabrain`
  - Skills: [`programming`]
  - QA: `cargo test -p hid-kb`

- [ ] **7. ec-acpi crate + charge-limit goldens**
  - What: 5-byte write 0x7B9/0x7D0 only; never 0x7A6
  - Depends: 3
  - Blocks: 8, 9
  - Category: `ultrabrain`
  - Skills: [`programming`]
  - QA: `cargo test -p ec-acpi`

### Wave 2 (After Wave 1 Completes)

- [ ] **8. Fake GCU broker + HID mock + EC ioctl mock**
  - What: In-process fakes; `LMECHREVO_FAKE_GCU=1`
  - Depends: 5, 6, 7
  - Blocks: 11
  - Category: `deep`
  - Skills: [`programming`]
  - QA: fake broker unit test records handshake order

- [ ] **9. Tauri host commands + events + AppManifest lock**
  - What: hw_snapshot, events, secrets.rs, no secrets in JS
  - Depends: 1, 4, 5, 6, 7
  - Blocks: 10, 11
  - Category: `deep`
  - Skills: [`programming`]
  - QA: `rg UWPClient_Pwd app/src` empty; cargo test host

### Wave 3 (After Wave 2 Completes)

- [ ] **10. Shell UI 420px gated dashboard**
  - What: DESIGN.md components; footer 6 keys; lighting from capabilities DTO
  - Depends: 2, 9
  - Blocks: 11, 19
  - Category: `visual-engineering`
  - Skills: [`frontend`, `programming`, `visual-qa`]
  - QA: `bun test` + 420px screenshot

### Wave 4 — Plan A stop (After Wave 3 Completes)

- [ ] **11. Plan A scenarios S1/S2/S3 + fake-broker window**
  - What: Happy Office; G16 hide lighting; charge limit never 0x7A6; tauri dev + fake GCU
  - Depends: 8, 10
  - Blocks: 12–22, F1
  - Category: `deep`
  - Skills: [`programming`, `frontend`]
  - QA: three scenario tests + `LMECHREVO_FAKE_GCU=1 cargo tauri dev`

### Wave 5 — Plan B (After Plan A stop; parallel)

- [ ] **12. Performance modes + fan + LCHWOC + boost + custom PL**
  - What: FEATURES.md §1; both Fan and LCHWOC packets; no RamFan/mid/whisper
  - Depends: 11
  - Blocks: F2
  - Category: `ultrabrain`
  - Skills: [`programming`, `frontend`]
  - QA: payload goldens + bun test Performance

- [ ] **13. GPU route matrix**
  - What: DisplayRouteMatrix; no Auto; hot-swap 50-only
  - Depends: 11
  - Blocks: F2
  - Category: `ultrabrain`
  - Skills: [`programming`, `frontend`]
  - QA: generation action table tests

- [ ] **14. Battery EC slider host+UI**
  - What: 40–100; no 3-mode MQTT
  - Depends: 11
  - Blocks: F2
  - Category: `quick`
  - Skills: [`programming`, `frontend`]
  - QA: S3 + slider invoke test

- [ ] **15. Lighting HID/GCU + lightbar + logo**
  - What: ItemSupport gates; no hinge/sync
  - Depends: 11
  - Blocks: F2
  - Category: `ultrabrain`
  - Skills: [`programming`, `frontend`]
  - QA: lighting command tests + bun test Lighting

- [ ] **16. Display Hz/brightness/calibration/overdrive/local dimming**
  - What: Official display rows; no DISPLAY_*_MODE send
  - Depends: 11
  - Blocks: F2
  - Category: `deep`
  - Skills: [`programming`, `frontend`]
  - QA: Hz string payload tests

- [ ] **17. Quick switches full table + personalization + startup + monitor-off**
  - What: SupportsQuickSwitch parity; TochpadEnable spelling
  - Depends: 11
  - Blocks: F2
  - Category: `deep`
  - Skills: [`programming`, `frontend`]
  - QA: table-driven switch tests

- [ ] **18. Liquid cooling MQTT + BLE NUS**
  - What: BT_LC then BLE fallback; gate on LiquidCoolingSupport
  - Depends: 11
  - Blocks: F2
  - Category: `ultrabrain`
  - Skills: [`programming`, `frontend`]
  - QA: fake MQTT + mock BLE tests

- [ ] **19. Overlay HUD, tray, footer 6 keys, settings dialog**
  - What: Added overlay; isolation status without killing GCU
  - Depends: 10
  - Blocks: F2
  - Category: `visual-engineering`
  - Skills: [`frontend`, `programming`, `visual-qa`]
  - QA: Footer 6-key test + overlay uses events not MQTT

- [ ] **20. Updates + usage telemetry**
  - What: stats.l-mechrevo.cn HTTPS; host-only HTTP
  - Depends: 11
  - Blocks: F2
  - Category: `deep`
  - Skills: [`programming`]
  - QA: UpdateChecker goldens

- [ ] **21. First-run + unsupported read-only + diagnostic pack**
  - What: writes disabled when unsupported; zip without MQTT password
  - Depends: 11
  - Blocks: F2
  - Category: `deep`
  - Skills: [`programming`, `frontend`]
  - QA: DiagnosticPackTests port + first-run bun test

### Wave 6 (Packaging)

- [ ] **22. NSIS perMachine + embedBootstrapper + hide console + tauri-plugin-log**
  - What: tauri.conf nsis; log to AppData; do not write repo `release\`
  - Depends: 11
  - Blocks: F3
  - Category: `unspecified-high`
  - Skills: [`programming`]
  - QA: tauri.conf keys + debug build does not touch `release\`

### Wave F

- [ ] **F1. Plan A fake-broker window QA**
  - What: Evidence screenshot + S1–S3
  - Depends: 11
  - Blocks: none (Plan A done)
  - Category: `unspecified-high`
  - Skills: [`programming`, `visual-qa`]
  - QA: artifacts/tauri-plan-a/

- [ ] **F2. Official-row coverage audit**
  - What: FEATURES.md vs code map; withdrawn absent
  - Depends: 12–21
  - Blocks: none
  - Category: `writing`
  - Skills: [`programming`]
  - QA: every Official row has a file

- [ ] **F3. Real-GCU verification (later)**
  - What: Slot 4 on hardware; not Plan A gate
  - Depends: 22
  - Blocks: none
  - Category: `unspecified-high`
  - Skills: [`debugging`]
  - QA: first-machine runbook for Tauri

## Execution Instructions

1. **Wave 1 of work (Wave 0):** Fire Tasks 1, 2, 3 IN PARALLEL.
   ```
   task(category="quick", load_skills=["programming"], run_in_background=false, prompt="Task 1: ...")
   task(category="visual-engineering", load_skills=["frontend","ui-ux-pro-max"], run_in_background=false, prompt="Task 2: ...")
   task(category="deep", load_skills=["programming"], run_in_background=false, prompt="Task 3: ...")
   ```
2. **Wave 1 crates:** After Wave 0, fire Tasks 4, 5, 6, 7 IN PARALLEL (`ultrabrain` + `programming`).
3. **Wave 2:** Tasks 8 and 9 in parallel after crates.
4. **Wave 3–4:** Task 10 then Task 11. **STOP Plan A** when S1–S3 and fake-broker window pass.
5. **Wave 5:** Plan B Tasks 12–21 in parallel by subsystem (do not drop rows).
6. **Wave 6 + F:** Task 22, F1 (with Plan A), F2 after Plan B, F3 later on hardware.
7. **Do not git commit** unless the user explicitly asks.
8. **Do not** spawn implementers that edit `src/MechrevoLiteWin` or `release\`.

<task_metadata>
session_id: ses_f41d33ae9ffeZp0ExbWROqdI6W
task_id: ses_f41d33ae9ffeZp0ExbWROqdI6W
subagent: plan
</task_metadata>