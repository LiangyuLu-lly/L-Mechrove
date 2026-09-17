# Upgrade-from-OpenRevo — master adoption plan

Generated: 2026-09-17. Repo: `ControlCenterX_5.56.60.26_Mechrevo` (read-only apart from this file).
Source of the sibling app: OpenRevo (`open-revo.exe`, Rust + Tauri v2, MIT). All claims below are
tagged with the exact artifact they came from. Anything not found is listed as a **gap** — nothing is
inferred from vibes.

---

## 0. Inventory of the RE drop + gaps

### 0.1 Artifacts present (`%TEMP%\opencode\`)

| Artifact | Size | What it actually is |
|---|---|---|
| `openrevo-extract\builtin_models.json` | 178,713 B | **The prize.** 29 machine entries, full schema (§2). Carved from `open-revo.exe` at absolute offset ~6,973,319. |
| `openrevo-extract\listener_decompiled\openrevo_acpi_listener.decompiled.cs` | 1,143 B | **Complete ILSpy source** of the WMI ACPI hotkey bridge (44 lines). Directly portable C#. |
| `openrevo-extract\decomp\Program.cs` | 933 B | Same program, decompiler project form. |
| `openrevo-extract\final\UWACPIDriver.sys` | 46,352 B | The real vendor driver. `Get-AuthenticodeSignature` → **Status=Valid**, signer `CN=Microsoft Windows Hardware Compatibility Publisher` (WHQL), cert `NotAfter 2026-02-19`. **Byte-identical size to the copy we already ship.** |
| `openrevo-extract\UWACPIDriver.sys` | 24,576 B | Truncated carve → `Status=NotSigned`. Do **not** use; it is an artifact of the carve, not the shipping driver. |
| `openrevo-extract\UWACPIDriver.sys.p7b` | 21,776 B | Cert-collection sidecar (unreadable via `X509Certificate2Collection.Import` — malformed/truncated). |
| `openrevo-extract\{kamehameha,cyber_lotus,singularity,cyber_marquee}.brfx` | 1.8–8.2 KB | **Text lighting-effect scripts** (`@name/@author/@default_*` header + `fn render_key(kx,ky,ctx)` DSL). OpenRevo community content. |
| `openrevo-extract\brfx\*.brfx` | 4 files | Second carve of the same scripts; `brfx2.py` documents the boundary offsets. |
| `open-revo-strings.txt` | 730,908 B | Raw strings dump of `open-revo.exe`. Primary behavioral evidence for §3. |
| `open-revo-src\` | docs-only | Public GitHub repo (branch `main`, tag `open-revo`): **README.md, README_en.md, docs/models_customization_guide.md, screenshots, LICENSE (MIT)**. No Rust source. |
| `openrevo-extract\*.py` | ~15 scripts | RE agents' carving scripts (`parse.py`, `schema*.py`, `brfx*.py`, `recon*.py`). No findings/notes markdown was written. |

### 0.2 Gaps (missing — do not guess)

| Missing | Consequence |
|---|---|
| **Decompiled Rust source** (`src\driver\ec_bridge.rs`, `src\driver\wmi_event_listener.rs`, `src\system\hardware_probe.rs`, `src\commands\hardware_cmd.rs` are named in string offsets 365,320 / 571,691 but the files are absent) | His Rust logic cannot be read — only its strings. Everything must be re-implemented in C#, not ported. |
| **EC register addresses/bit offsets** in OpenRevo | His strings name fields (`Charge_Limit_Upper`, `Office_PL1_Default`, `HardwareSupportByte1..3`, …) but **no addresses**. We must map names → addresses ourselves against `docs/hardware/ec-registers.json`. |
| **Runtime `models.json`** | Only the embedded `builtin_models.json` was carved. Runtime file is `%APPDATA%\OpenRevo\models.json`, regenerated from the embedded copy — so the carved file *is* the factory baseline. |
| **No RE notes/log** | The extract dir has no findings file; all evidence had to be re-derived here. |
| **`UWACPIDriver.inf` / `.cat`** | Not in extract, but we already ship both in `release\GCU-common\UWACPIDriver\` and `release\GCU-only\UWACPIDriver\`. |
| **UEFI advanced-menu (BIOS) toggle** | OpenRevo README advertises it; **no string or artifact evidence** in the drop. Cannot adopt. |

---

## 1. Master adoption table

Legend — **Reuse as-is** = data/SQL-ish or complete C# source we can drop in; **Re-implement C#** = needs the Rust logic rebuilt from strings; **Reference only** = we already have an equal-or-better path.

| # | Upgrade point | Evidence (file / offset) | Lands in our app | Reuse vs re-implement | Risk |
|---|---|---|---|---|---|
| A1 | **29-model power/fan preset DB** | `openrevo-extract\builtin_models.json` (whole file) | new `docs/hardware/model-presets.json` + `Hardware\ModelPresetStore.cs` | **Reuse data as-is**, but convert (duty ×2, 3-preset → our 6-table) | Static presets must never override live MQTT range authority (`MechrevoHw` capability model). |
| A2 | **`BIOS_PROJECT_ID` ↔ `ProjectID` mapping** (23 confirmed triples) | `builtin_models.json` fields `model_id`/`project_id`/`bios_project_id`; our `docs\hardware\README.md:50-52` marks this **“尚未建立”** | new `docs/hardware/model-id-map.json` | **Reuse data as-is** | OpenRevo `project_id` (16/24/25/26) is **not** GCUService `ProjectID` (§2.3) — only `model_id` + `bios_project_id` may be joined. |
| A3 | **4-signal generation probe (30/40/50 + legacy)** | `open-revo-strings.txt` offsets **360,089–360,700** | `Hardware\MechrevoDeviceCapabilities.cs` (today: registry `BIOS_PROJECT_ID` only) | **Re-implement C#** | Signals are string fragments; exact predicates must be reconstructed and brute-checked against real DMI/registry. |
| A4 | **WMI ACPI hotkey event bridge** | `listener_decompiled\openrevo_acpi_listener.decompiled.cs` (full 44-line source); strings 366,280 | new `Hardware\AcpiHotkeyListener.cs` | **Reuse source as-is** (it is C#) | Requires the vendor driver's `AcpiTest_EventULong` WMI class to exist; fail-closed if absent. |
| A5 | **Q-Key / Fn hotkey state machine** (debounce, echo filtering, mode cycle) | strings 365,320 + 366,280 (`Q-Key debounced (elapsed < 500ms)`, `Fn+1`, `Fn+F6`, `ACPI Mode State Echo received: 168 (0xA8)`, `keyboard brightness … 240`) | `Hardware\AcpiHotkeyListener.cs` | **Re-implement C#** | Event codes are model-variant; a wrong code could toggle the wrong thing. Gate by model, log, no write on unknown code. |
| A6 | **`\\.\ACPIDriver` EC read/write (IOCTL)** | strings 572,248 `[ACPIDriver_IOCTL / WMI]`, 365,320 `src\driver\ec_bridge.rs` | **already ours** `src\Probe\EcProbe.cs`, `Hardware\EcChargeLimit.cs` | **Reference only** | No new IOCTL here — same `ECREAD 0x9C40A488` / `ECWRITE 0x9C40A48C`, same 5-byte write layout we already documented. |
| A7 | **Driver embedding + kernel-service deploy** | strings 408,100 `Could not extract embedded UWACPIDriver.sys…`, `Successfully deployed embedded UWACPIDriver.sys to System32/drivers`, `UWACPIDriver binPath= type=kernel /scan-devices` | ours already: `installer` + `release\*\UWACPIDriver\{.sys,.inf,.cat}` | **Reference only** | We install via `pnputil` + `.inf`/`.cat` (better). Do not duplicate OpenRevo's raw copy-to-System32 route. |
| A8 | **`.brfx` key-light effect DSL** (4 scripts) | `openrevo-extract\*.brfx` | `Hardware\KeyboardRgb.cs` (renderer) | **Reuse data; re-implement interpreter** | Rendering is already ours; a DSL VM is new surface + licensing need (BetterRGB attribution). |
| A9 | **EC named-field map** (`ProjectID`, `Charge_Limit_Upper/Lower`, `Office_PL1_Default`, `CpuFanRpm_High/Low`, `HardwareSupportByte1..3`, `WinKey_Lock`, `AC_Recovery`, `PowerOff_USB`, `CameraPower`, `SingleKBL_Enable`) | strings 572,248 | `docs/hardware/ec-registers.json` (cross-check names) | **Reference only** (names, no addresses) | Names alone cannot be written; addresses must come from our own table. |
| A10 | **Registry capability keys** | strings 408,136: `RamFan1p5Support`, `DGpuDirectConnectionSupport`, `LiquidCoolingSupport`, `LiquidCoolingAutoModeSupport`, `TurboModeSupport`, `PerformanceReady`, `ServiceReady`, `FanSettingsSupport`, `ColorCalibrationSupport`, `OcSettingsSupport`, `SystemMonitorSupport`, `CPUPerformanceAndOverClockMenuSupport`, `SOFTWARE\OEM\GamingCenter2\ItemSupport`, `MyFanTableEcVersion1.17` | `Hardware\MechrevoDeviceCapabilities.cs` | **Reference only** | We already read these exact keys. Confirms our capability model is correct, not new. |
| A11 | **Water-cooler BLE (Nordic UART, name + NUS-UUID discovery)** | strings 365,320 (`[BLE Scanner] Discovered Mechrevo cooler by name:'…' (MAC: …)`, `by NUS Service UUID`) | already ours `Hardware\WaterCoolerBle.cs` | **Reference only** | Parity already; no work. |
| A12 | **GPU-mode via UEFI NVRAM variable** | strings 572,248: `NVRAM`, `UniWillVariable`, `(iGPU Only)`, `(Hybrid/dGPU)`, `[ENABLED]/[DISABLED]`, `F2/Del` | our GPU mode uses MQTT `Setting/Control` | **Re-implement C#** (blocked) | **Variable name/GUID/DATA layout is NOT in the drop** → cannot implement. See gap. |
| A13 | **Takeover: pause/resume OEM services** | strings ~401,276: `OEM services successfully paused in zero latency.`, `OEM services successfully resumed.`, service list `controlcenter gamingcenter gcuservice gcubridge aistone…`; `GCUService.exe` paths | conflicts with MQTT fallback | **Do not adopt** | See §5. |
| A14 | **Model preset export/import** | strings 360,089 (`export_profile_preset`, `import_profile_preset`, `set_active_profile_id`) | our custom-profile slots | **Re-implement C#** (optional) | Low value; our slots already persist. |

---

## 2. PRIORITY — 20–50 series model compatibility

### 2.1 The schema (exact, from `builtin_models.json`)

Top level is a **JSON array of 29 objects**. Per-object keys (union of all entries — two shapes exist):

```
mine:bool, model_id:string, display_name:string, alias:string,
project_id:int, bios_project_id:string,
[optional] factory_specs: { office|balanced|turbo: {pl1,pl2,pl4,temp_offset}, base_tgp, max_db },
[optional] presets:      { office|balanced|turbo: {pl1,pl2,pl4,temp_offset} },
pl1_default:int, pl2_default:int, pl4_default:int,
base_tgp:int, max_db:int, max_fan_duty:int, temp_levels:int,
fan_control_respective:bool, has_water_cooler:bool, keyboard_type:string,
fan_tables: { office|balanced|turbo: {
    cpu_upt[16], cpu_downt[16], cpu_duty[16],
    gpu_upt[16], gpu_downt[16], gpu_duty[16], max_duty:int } }
```

**Critical shape fact:** only **17 of 29** entries carry `factory_specs`+`presets`. The other **12**
(`X6AR55, XXAF55, XxPTL, CANGLONG, JIAOLONG, PH6TRX1, PH6PG7x, PH6PG3x, PH6PG0x, PH6ARxx, PH4TRX1, PH4PGx1`)
have **neither** — per `models_customization_guide.md` those power values are read from EC at first
boot, so only `pl1_default/pl2_default/pl4_default`, `fan_tables`, `base_tgp`, `max_db`,
`temp_levels`, `fan_control_respective`, `has_water_cooler`, `keyboard_type` are present.

`temp_levels` ∈ {6,7,8,10,11} — the count of active breakpoints; the 16-slot arrays are padded with
**255 sentinel**.

### 2.2 The actual entries (all 29)

`new?` = not present in our official `docs\hardware\fan-table-defaults.json` (24 Projects × 3 console versions).

| model_id | new? | project_id | bios_project_id | TL | resp | water | keyboard | base_tgp | max_db | defaults pl1/pl2/pl4 | turbo pl1/pl2/pl4 (@off) |
|---|---|---|---|---|---|---|---|---|---|---|---|
| GM6IX9B | **NEW** | 25 | IDB | 10 | False | True | TypeA | 150 | 25 | 75/125/175 | 205/205/200 (@5) |
| X6AR55 | **NEW** | 26 | TRX1 | 6 | False | True | TypeA | 140 | 25 | 75/85/145 | — (EC-derived) |
| XXAF55 | **NEW** | 26 | TRX1 | 6 | False | True | TypeA | 140 | 75 | 75/85/145 | — (EC-derived) |
| XxPTL | **NEW** | 26 | PTL | 11 | **True** | False | TypeA | 115 | 25 | 65/115/140 | — (EC-derived) |
| CANGLONG | **NEW** | 26 | TRX1 | 6 | False | False | TypeA | 150 | 25 | 75/85/145 | — (EC-derived) |
| JIAOLONG | **NEW** | 26 | TRX1 | 6 | False | False | **SingleZone_RGB** | 150 | 25 | 75/85/145 | — (EC-derived) |
| PH6TRX1 | | 26 | TRX1 | 6 | False | True | TypeA | 150 | 25 | 75/140/175 | — (EC-derived) |
| PH6PG7x | | 26 | PG7 | 11 | False | False | TypeA | 140 | 25 | 65/115/140 | — (EC-derived) |
| PH6PG3x | | 26 | PG3 | 11 | False | False | TypeA | 115 | 25 | 55/100/125 | — (EC-derived) |
| PH6PG0x | | 26 | PG0 | 11 | False | False | TypeB | 100 | 25 | 45/80/100 | — (EC-derived) |
| PH6ARxx | | 26 | AR | 8 | False | False | TypeB | 95 | 20 | 45/65/80 | — (EC-derived) |
| PH4TRX1 | | 24 | TRX1 | 6 | False | False | TypeA | 105 | 25 | 40/40/90 | — (EC-derived) |
| PH4PGx1 | | 16 | PG1 | 7 | False | False | TypeA | 140 | 25 | 65/115/140 | — (EC-derived) |
| PH4AQE3 | | 24 | AQE3 | 11 | False | False | TypeA | 105 | 25 | 45/75/115 | 115/115/140 (@5) |
| PH4AQxx | | 24 | AQ | 8 | False | False | TypeA | 115 | 25 | 55/85/125 | 125/125/150 (@5) |
| PH4AUxf | | 24 | AU | 8 | False | False | TypeA | 115 | 25 | 65/95/135 | 135/135/160 (@5) |
| PH4AUxx | | 24 | AU | 6 | False | False | TypeA | 115 | 25 | 65/95/135 | 135/135/160 (@5) |
| PH4AXxx | | 24 | AX | 8 | False | False | TypeA | 140 | 25 | 65/115/140 | 140/140/165 (@5) |
| PH4PGx2 | | 24 | PG2 | 7 | False | True | TypeA | 150 | 25 | 75/125/175 | 175/175/200 (@5) |
| PH4PRxx | | 24 | PR | 7 | False | True | TypeA | 150 | 25 | 75/125/175 | 175/175/200 (@5) |
| PH4PUxx | | 24 | PU | 6 | False | True | TypeA | 150 | 25 | 75/125/175 | 175/175/200 (@5) |
| PH4TQx1 | | 24 | TQ1 | 8 | False | False | TypeA | 105 | 25 | 55/85/125 | 125/125/150 (@5) |
| PH4TUX1 | | 24 | TU1 | 6 | False | True | TypeA | 150 | 25 | 75/125/175 | 175/175/200 (@5) |
| PH6AQxx | | 26 | AQ | 8 | False | False | TypeB | 115 | 25 | 55/85/125 | 125/125/150 (@5) |
| PH6PG0x150W | | 26 | PG0 | 11 | False | False | TypeB | 115 | 25 | 55/95/125 | 125/125/150 (@5) |
| PH6PG3x150W | | 26 | PG3 | 11 | False | False | TypeA | 125 | 25 | 65/115/140 | 140/140/165 (@5) |
| PH6PG7x150W | | 26 | PG7 | 11 | False | False | TypeA | 140 | 25 | 75/125/160 | 160/160/185 (@5) |
| PH6PGEx | | 26 | PGE | 11 | False | True | TypeA | 150 | 25 | 75/140/175 | 175/175/200 (@5) |
| PH6PRxx | | 26 | PR | 11 | False | True | TypeA | 150 | 25 | 75/140/175 | 175/175/200 (@5) |

**Join result vs our official tables:** 23 of 29 overlap. **6 are new**: `CANGLONG, GM6IX9B, JIAOLONG,
X6AR55, XXAF55, XxPTL`. **1 of ours is absent**: `PH4ARxx`. Five of the six new entries are non-MECHREVO
OEM brands (YAOSHI / CANGLONG / JIAOLONG) with `bios_project_id` values (`IDB`, `PTL`) that are
**already in our `project-ids.json`** (`IDB`=23, `PTL` not in BiosProjectId list → gap).

### 2.3 How the generation is detected (OpenRevo, from strings 360,089–360,700)

There is **no `ProjectID`-generation inference**. OpenRevo uses a **4-signal probe** in
`src\system\hardware_probe.rs` (named at string 571,691):

| Signal | Source | Buckets it resolves |
|---|---|---|
| **Signal 1** | Active Profile (registry `SOFTWARE\OEM\GamingCenter2\ItemSupport`) | `30-series/Legacy GK/GM5/PH` (`PH4`,`PH6`) · `GM/PH/Yaoshi (legacy/40-series)` (`GM6`,`GM7`) · `CANGLONG/X6` (`X6`,`X6XR`) · `RTX 30/RTX30/GTX` · `RTX 40/RTX40` · `RTX 50/RTX50` |
| **Signal 2** | DMI Baseboard product | `30-series (PH/GK/GM5)` · `GM/PH/GJ/GK (legacy/40-series)` · `CANGLONG/X6` |
| **Signal 3** | Registry GPU generation | `RTX 30-series / GTX` · `RTX 40/30-series` · `RTX 50-series` |
| **Signal 4** | AMD CPU platform | `Canglong architecture` |
| default | fallback | `Not 30-series` · `40-series / Legacy GM fallback` |

And a second, coarser gate: `PLATFORM_DETECT` → `30-series architecture check`,
`40-series (Legacy GM chassis)`, `50-series (Next-Gen / Canglong architecture)`,
`50-series / Canglong architecture check`.

Model identity itself is read from:
`HARDWARE\DESCRIPTION\System\BIOS` → `BaseBoardProduct`, `SystemProductName`, `BIOSVersion`;
plus `SOFTWARE\OEM\GamingCenter2\ItemSupport`; plus
`C:\Program Files\OEM\AiStoneService\MyControlCenter\UserPofiles`.
Output fields recorded per machine include `matched_profile`, `board_product`, `ec_project_id`,
`is_trusted`, `confidence_reason`, `INFERRED_CHIP_PROFILE`.

> **Honesty note:** the strings show buckets **30/40/50 + GTX/legacy**. There is **no explicit
> “20-series” bucket** in the evidence. If the headline “20–50 series” means literal RTX 20-series,
> that data is **not** in this drop — it is a gap. The verified range is 30 → 50 plus “legacy/GTX”.
> Our own `docs\gcu-dependency-matrix.md:87-89` says the same: `PH4*`=40-series platform, `PH6*`=50-series
> platform, and “代码不做代际推断”.

### 2.4 Concrete per-generation differences (measured, not narrated)

Grouped by OpenRevo `project_id` (a **mold tier**, see §2.5):

| project_id | models | base_tgp | temp_levels | fan_control_respective | water | keyboard |
|---|---|---|---|---|---|---|
| 16 | 1 (`PH4PGx1`) | 140 | 7 | False | False | TypeA |
| 24 | 11 (all `PH4*`) | 105 / 115 / 140 / 150 | 6 / 7 / 8 / 11 | False | False, True | TypeA |
| 25 | 1 (`GM6IX9B`) | 150 | 10 | False | True | TypeA |
| 26 | 16 (all `PH6*` + 5 new) | 95 / 100 / 115 / 125 / 140 / 150 | 6 / 8 / 11 | False, **True (XxPTL)** | False, True | **TypeA/TypeB/SingleZone_RGB** |

Concrete differences that change code paths:

1. **`temp_levels` drives fan-table length** — 6, 7, 8, 10 or 11 active points. 255 = end-of-table sentinel.
2. **`fan_control_respective`** is `true` only for `XxPTL`; everywhere else `false` (CPU follows GPU). Our
   `MechrevoService.SwitchFanRespective` already has this concept — this is the per-model default.
3. **`has_water_cooler`** splits the SKU space; our water-cooler group already hides when absent.
4. **`keyboard_type` ∈ {`TypeA`(24), `TypeB`(4), `SingleZone_RGB`(1)}** — must line up with
   `docs\hardware\keyboard-zones.json` (22 layouts). `SingleZone_RGB` = `JIAOLONG`.
5. **`max_db` = 20 for `PH6ARxx`, 75 for `XXAF55`, 25 everywhere else** — `max_db` is *not* a constant.
6. **Fan duty encoding is different between the two datasets.** Our official tables store `Duty` in
   **percent** (0–100, 255 sentinel). OpenRevo stores the **raw EC fan-speed byte**
   (`duty = percent × 2`), e.g. `PH4AQE3` turbo `cpu_duty=[0,108,126,144,162,180]` = 0/54/63/72/81/90 %.
   Confirmed by our own `docs\hardware\ec-bit-flags.json` → `MyFan2SpeedByteFlag`
   (`Speed30=0x3C(60)`, `Speed50=0x64(100)`, `Speed55=0x6E(110)`, `Speed70=0x8C(140)`), i.e.
   `speed_byte = 2 × percent`. **Conversion rule: `percent = ec_speed_byte / 2`, clamp 0..100.**
   OpenRevo `max_duty` is uniformly `180` (= 90 %) on turbo for every model — a constant, not a
   per-model value, so it carries no information and must not overwrite our per-model table caps.

### 2.5 Port target and config shape (exact)

**Do NOT** read `project_id` as GCUService `ProjectID` — they are different number spaces
(OpenRevo `PH4PGx1`=16, `PH4TRX1`=24, `PH6*`=26 vs our enum `PH4PGx1`=6147/`0x1803`,
`PH4TRX1`=18, `PH6TRX1`=21). Join **only** on `model_id` (which for 23/29 equals the GCUService
`ProjectID` **member name**) and `bios_project_id`.

Target file — `docs\hardware\model-presets.json` (data only, no code dependency):

```jsonc
{
  "schema": 1,
  "source": "open-revo builtin_models.json (carved) + docs/hardware/fan-table-defaults.json",
  "duty_unit": "percent",                       // convert: percent = openrevo_duty / 2
  "entries": [
    {
      "model_id": "PH6TRX1",
      "bios_project_id": "TRX1",
      "display_name": "MECHREVO Gaming Laptop PH6TRX1",
      "has_water_cooler": true,
      "keyboard_type": "TypeA",
      "temp_levels": 6,
      "fan_control_respective": false,
      "base_tgp": 150, "max_db": 25, "max_fan_duty": 100,
      "pl_defaults": { "pl1": 75, "pl2": 140, "pl4": 175 },
      "presets": null,                          // null => read from EC at runtime (12 entries)
      "fan_tables": { "office": {...}, "balanced": {...}, "turbo": {...} }
    }
  ]
}
```

Loader — `Hardware\ModelPresetStore.cs`:
`TryLoad()` → parse + validate (fail-closed: bad/missing file ⇒ store unavailable, **never** throw,
never guess); `FindByModelId(string)`, `FindByBiosProjectId(string)`;
`DutyToEcByte(percent)`, `EcByteToDuty(byte)` (the ×2 rule).
Authority rule: **preset values are defaults only.** Live MQTT-reported min/max remain authoritative
(`MechrevoHw` `*Seen` capability model) — mirroring the existing rule in
`docs\gcu-dependency-matrix.md:81`.

---

## 3. The no-GCU migration map (per control function)

Ground truth for the driver: OpenRevo and we use the **same** `\\.\ACPIDriver`
(`UWACPIDriver.sys`, KMDF/WHQL). Our copy is already shipped
(`release\GCU-common\UWACPIDriver\UWACPIDriver.sys`, 46,352 B, + `.inf` + `.cat`) and
install-time-verified (`docs\gcu-dependency-matrix.md:60`). The carved 24,576 B copy is a truncation
artifact and is **not** the shipping binary.

**Signing status (verified this session):** `final\UWACPIDriver.sys` → **Authenticode Valid**,
`CN=Microsoft Windows Hardware Compatibility Publisher` (WHQL), cert `NotAfter 2026-02-19`
(timestamp-signed, so it still loads; documented in `docs\hardware\README.md` — same conclusion).
**We do not need “his driver” — we already ship the identical, signed vendor driver.**

**PawnIO status (verified this session):** the only PawnIO module in the product is
`src\MechrevoLiteWin\Pawn\RyzenSMU.bin` (38,196 B, AMD SMU). There is **no EC/LPC/ACPIEC module**.
`PawnIOWrapper`'s only consumer is `RyzenSmu.cs` (AMD STAPM/CO/temp). Therefore:

> **Zero EC-dependent control functions are replaceable via PawnIO today.** Any EC path needs either
> the vendor driver (already ours) or a *new* PawnIO module that is not present in the repo or the
> extract drop. Adding such a module (e.g. a signed `LpcACPIEC`-class module) is the preferred
> long-term route — it removes the dependency on a driver whose signing cert expired — but it is
> **new work and a gap**, not a port.

| Function | OpenRevo call path (evidence) | Replaceable TODAY via PawnIO? | Need his driver? | Our current path | Verdict |
|---|---|---|---|---|---|
| **Fans — duty / RPM** | EC write via `\\.\ACPIDriver` IOCTL, `src\driver\ec_bridge.rs` (strings 365,320); field names `CpuFanRpm_High/Low`, `GpuFanRpm_Low/High` (572,248); fan tables in `builtin_models.json` | **No** (no PawnIO EC module) | No — ours | MQTT `Fan/Control` SET_FAN_SPEED_CURVE_SETTING | **MQTT stays primary.** EC direct is blocked on the 15 ambiguous addresses + write-verification. |
| **PL1 / PL2 / PL4 / TCC** | `Office_PL1_Default`/`Balanced_PL1_Default` (572,248); our EC `0x783/0x784/0x785`, `SMAPCTABLE` `0x9C40A500` | **No** | No — ours | MQTT `Fan/Control` SET_OPERATING_MODE_DETAIL | **MQTT stays.** `0x783-0x785` are read-verified only. |
| **TGP / Dynamic Boost** | `base_tgp`/`max_db` in JSON; EC `ADDR_ConfigurableTGP_VALUE` (`0x744`) | **No** | No — ours | MQTT `SetOperatingModeDetail` `GpuConfigurableTGPTarget` / `GpuDynamicBoost` | **MQTT stays.** ⚠ `0x744` is **provably not TGP** on `BIOS_PROJECT_ID=IDY` (our README:58-60) — do not direct-write. |
| **GPU mode / MUX** | UEFI NVRAM variable: `NVRAM`, `UniWillVariable`, `(iGPU Only)`, `(Hybrid/dGPU)`, `[ENABLED]/[DISABLED]`, `F2/Del` (572,248) | **No** (and no PawnIO NV module) | No — ours | MQTT `Setting/Control` `IGPU_ONLY_CONNECT_RB_*` / `DGPU_DIRECT_CONNECT_*` + restart | **MQTT must stay.** Variable name/GUID/layout absent from extract → not implementable. |
| **Battery charge limit** | `Charge_Limit_Upper` / `Charge_Limit_Lower` (572,248), `-> limit applied: limit=` (365,320), `src\driver\ec_bridge.rs` | **No** (PawnIO) — but **already replaced via our own `\\.\ACPIDriver` path** | No — ours | **already EC direct**: `Hardware\EcChargeLimit.cs` EC `0x7B9`/`0x7D0`, fail-closed rollback, model-gated | ✅ **Provably replaceable and already replaced.** Model gate currently `YAOSHI` only — extend using the §2 mapping. |
| **Keyboard lighting (effect)** | Direct HID `ITE 8291 USB` (572,248) + BetterRGB streaming; `.brfx` DSL | n/a (HID, not EC) | No | **already direct HID** `Hardware\HidDeviceWin.cs` + `KeyboardRgb.cs` | ✅ **Already replaced.** Optional: port the `.brfx` DSL (A8). |
| **Lightbar / Logo lighting** | `HidLightbar_*` / EC `HIDRGBLightbar_*` (our `ec-registers.json`); registry `Lightbar_logo_PowerSwitch` etc. (strings) | **No** | No | MQTT `HidLightbar/Ctrl`, `HidLightbar_Logo/Ctrl` | **MQTT stays** (non-keyboard lights confirmed working GCU-side, `beta17-gcu-variant-selection.md:92`). |
| **Water cooler** | BLE Nordic UART discovery by name and by NUS UUID (365,320) | n/a (BLE) | No | **already direct BLE** `Hardware\WaterCoolerBle.cs` (DirectBle route) | ✅ **Already replaced / parity.** |
| **Device switches** (touchpad, WiFi, BT, camera, WinKey, FnLock, USB-off-charge, AC recovery) | EC fields `WinKey_Lock`, `FnKey_Lock`, `CameraPower`, `PowerOff_USB`, `AC_Recovery`, `BatteryMode_Touchpad`, `HardwareSupportByte1..3`, `SingleKBL_Enable` (572,248) | **No** | No — ours | MQTT `Settings/DeviceSwitchItemStatus` + `Setting/Control` | **MQTT stays.** No addresses in extract. |
| **Power mode** (office/balanced/turbo) | PL defaults written straight to EC | **No** | No — ours | MQTT `Fan/Control` `OPERATING_*_MODE` + `LCHWOC/Control` run-marker | **MQTT stays** (the run-marker is required; `docs\hardware\README.md`). |
| **Hotkeys / OSD** | WMI `AcpiTest_EventULong` + `openrevo_acpi_listener.exe` + `src\driver\wmi_event_listener.rs` | n/a (WMI, not EC) | No | **we have none** | **New capability** — portable C# source available (A4/A5). Best-value adoption after the model DB. |

### 3.1 Bottom line

- **Provably replaceable today (already done in our app):** battery charge limit (EC direct),
  keyboard RGB (HID direct), water cooler (BLE direct), Windows personalization, GPU OC (NVAPI).
- **Provably NOT replaceable today:** every remaining EC function (fans, PL/TCC, TGP/dB, device
  switches, power mode) and GPU mode/MUX. Blockers: (a) no PawnIO EC module, (b) the 15 ambiguous EC
  addresses unresolved, (c) GPU-mode NVRAM variable unknown.
- **MQTT must stay as the fallback for all of them.** Do not remove any MQTT path in this work.

---

## 4. Wave plan

Waves are ordered so each is independently verifiable and the first three touch **no hardware**.
Our single build slot ⇒ only one *build/test* wave at a time; analysis and data authoring parallelise.

| Wave | Work | Touches hardware? | Verification that proves it | Parallelisable |
|---|---|---|---|---|
| **W0** | Import `builtin_models.json` → `docs\hardware\model-presets.json` (converted: duty ×2, `null` presets for the 12 EC-derived, merged with `fan-table-defaults.json`); add `model-id-map.json` (23 triples) | No | New xUnit test parses both files; asserts 29 entries, 6 flagged new, duty round-trips `EcByteToDuty(DutyToEcByte(p))==p` for p∈0..100, every `temp_levels` matches the count of non-255 breakpoints | ✅ any time |
| **W1** | `Hardware\ModelPresetStore.cs` (load/validate/query, fail-closed) + wire as *defaults only* behind config flag `model_presets=0/1`, default 0 | No | Unit tests on fixtures (valid, missing, malformed, unknown sentinel); full suite stays ≥1756 green with flag off | Serial after W0 (same build) |
| **W2** | `Hardware\GenerationProbe.cs` — re-implement the 4-signal detector as a pure function over injected DMI/registry values; log `matched_profile`/`confidence_reason` | No | Table-driven tests with the exact string buckets from §2.3; default fallback asserted | ✅ parallel with W1 authoring; serial on build |
| **W3** | `Hardware\AcpiHotkeyListener.cs` — port the 44-line WMI listener, subscribe `AcpiTest_EventULong` on a background thread, fail-closed if class absent | Read-only (event subscribe) | Manual: `READY` line appears; no exception when driver/WMI class missing; unit test the debounce/echo filter pure logic | ✅ authoring parallel |
| **W4** | Read-only EC full snapshot across modes/AC states; extend `src\Probe` to dump `0x400–0x4FF`, `0x720–0x7FF`, `0xF00–0xFFF`; anchor-reverse against known MQTT fields | **Read-only** | Dumps archived; every one of the 15 ambiguous addresses either resolved with evidence or explicitly still ambiguous | Serial (needs the machine) |
| **W5** | Shadow-write one low-risk function (fan duty) behind config, with watchdog: “RPM not seen rising within N s ⇒ roll back + log” | **Write** | RPM readback delta in log; rollback path forced by test seam | Must serialise; only after W4 gives write-verified addresses |
| **W6** | Optional `.brfx` DSL renderer + model-preset export/import UI | No | Golden-frame tests vs the 4 carved scripts | Last; parallel authoring |

**Immediately startable:** W0 and W2 authoring today (no hardware, no build conflict).
**Must serialise:** W1 → W3 → W5 (single build slot + only one may touch the machine).

---

## 5. Non-adoption list (with reasons)

| Not adopting | Reason |
|---|---|
| **Whole-app `requireAdministrator`** | OpenRevo deploys a kernel service at runtime (`UWACPIDriver binPath=… type=kernel`, strings 408,100) and pauses OEM services, which needs elevation. We are LUA-friendly with a user-level autostart task (`Helpers\Startup.cs`). Do not regress. |
| **Dropping MQTT outright** | §3.1: all EC functions remain blocked; MQTT is the only working path for fans/PL/TGP/MUX/device-switches today. Removing it would break the product. |
| **Shipping an unsigned driver** | Not applicable and must stay that way: we ship the WHQL-signed vendor binary + `.inf` + `.cat`. Never ship the truncated 24,576 B carve (Authenticode `NotSigned`). |
| **Pausing/stopping OEM GCU services (“takeover”)** | `OEM services successfully paused/resumed` + service list. Directly conflicts with “keep MQTT as fallback”; would make our fallback impossible. |
| **UI overhaul** | Out of scope by instruction; OpenRevo is React/Tauri, ours is WinForms with 20 locales. No change. |
| **`nvidia-smi` shelling for the power probe** | OpenRevo shells `nvidia-smi --query-gpu=power.default_limit,power.max_limit` and even has a 30-series bypass. We already use in-process NVAPI (`NvidiaSmi.cs`/NVAPI). Adding a CLI dependency is a regression. |
| **`\\.\LCD` brightness IOCTL** | OpenRevo opens `\\.\LCD` and uses `IOCTL_VIDEO_QUERY/SET_DISPLAY_BRIGHTNESS`. We use standard Windows brightness; no need. |
| **UEFI advanced-menu (BIOS) toggle** | Advertised in README but **zero evidence** in the drop (no variable, no code, no strings). Cannot implement safely. |
| **`.brfx` DSL adoption without licence review** | Scripts are OpenRevo Community content; the streaming engine credits BetterRGB (separate project). Reuse requires attribution/licence check before shipping. |
| **Overriding per-model table caps with OpenRevo `max_duty`** | It is a constant `180` on turbo for every model (no information); applying it would overwrite our per-model caps. |

---

## 6. Risk (concrete failure modes + mitigations)

- **R1 — Model mis-join** (`project_id` treated as `ProjectID`). We could bind `PH4PGx1`(16) to the
  wrong GCUService ProjectID and push wrong PL/fan defaults. *Mitigation:* join only on `model_id` +
  `bios_project_id` (W0 test enforces it); `project_id` is stored as an opaque tag.
- **R2 — Fan duty unit error** (writing `180` where `100` is expected, or vice-versa) would command
  ~90 % where ~100 % was meant, or ~50 % where 90 % was meant. *Mitigation:* single conversion
  helper + round-trip property test; clamp 0..100; never write without readback.
- **R3 — EC address ambiguity.** 15 addresses remain ambiguous; `0x744` is already proven *not* TGP on
  `IDY`. A wrong write could brick thermals. *Mitigation:* W4 must resolve before W5; W5 only writes
  with a post-write RPM/readback watchdog and rollback.
- **R4 — Static presets overriding live capabilities.** Firmware that reports a narrower range than a
  preset would be overridden, breaking a working SKU. *Mitigation:* presets are defaults only; live
  `*Seen` min/max stay authoritative; feature-flagged off by default.
- **R5 — Driver certificate expiry (2026-02-19).** When Windows tightens policy the whole EC path
  (including our shipping charge-limit feature) fails. *Mitigation:* keep the existing fail-closed
  “ignore request + log” branch; treat a PawnIO EC module (signed) as the strategic replacement, not
  an OpenRevo port.

## 7. Verification commands used (all read-only)

```powershell
Get-AuthenticodeSignature "$env:TEMP\opencode\openrevo-extract\final\UWACPIDriver.sys"   # Valid / WHQL
Get-AuthenticodeSignature "release\GCU-only\UWACPIDriver\UWACPIDriver.sys"              # same 46352 B
python ...gen_table.py                                                                  # 29 entries, 6 new, duty ×2
Select-String -Path "$env:TEMP\opencode\open-revo-strings.txt" -Pattern 'Fan/Control'    # 0 hits -> no MQTT
```
