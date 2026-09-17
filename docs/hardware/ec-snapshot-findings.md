# W4 — read-only EC snapshot findings (2026-09-17, live machine)

First run of the W4 step from `docs/upgrade-from-openrevo.md` (§4) on the development
machine. Read-only: the probe issues only `IOCTL_GPD_ACPI_ECREAD` (0x9C40A488) with the
live-verified 4-byte `[u32 address]` input and a 16-byte output; there is no write IOCTL
anywhere in `src/Probe/EcSnapshot*.cs`.

## Provenance

| | |
|---|---|
| Machine | `YAOSHI Series-X6AR55xY`, `BIOS_PROJECT_ID=IDY`, live `ProjectID=26` |
| Command | `dotnet run --project src\Probe -c Debug -- snapshot` |
| Dump | `src\Probe\artifacts\ec-snapshot-20260917-184149.txt` (gitignored) |
| Coverage | `0x400–0x4FF`, `0x720–0x7FF`, `0xF00–0xFFF` + breadcrumbs; 738 bytes, 0 read errors |
| Anchors | `PL1=75 PL2=85 PL4=145 TGP=150 Tcc=15 CpuFanRpm=2498 GpuFanRpm=2389 GpuTemp=44 Battery=100` |

## Resolved by this run (with evidence)

| Address | Finding | Evidence |
|---|---|---|
| `0x46B`/`0x46C` | **Second/GPU fan RPM, `BYTE1`=0x46C is the high byte.** | `0x09`/`0x55` → `0x0955` = **2389 = live `GpuFanRpm` exactly** (same instant as the MQTT frame). |
| `0x464`/`0x465` | **Main/CPU fan RPM, same big-endian order** (docs' "little-endian" wording is wrong; their own `0x09/0x44 → 2372 = 0x0944` example is big-endian). | `0x09`/`0x8C` → `0x098C` = 2444 vs live `CpuFanRpm` 2498 (~2% skew); `le` decode would be 35849, absurd. |
| `0x4AB` | Battery RSOC. | `0x64` = 100 = live `BatteryLifePercent`. |
| `0x4A6`/`0x4A7` | Battery cycle count, `BYTE1`=0x4A6 low. | `0x10`/`0x00` = 16 = live `BatteryCycleCount=16`. |
| `0x740` | **Carries the MQTT/mold `ProjectID` (26), not the GCUService `ProjectID` enum** (`PH6TRX1=21`). | `0x1A` = 26 = live `Customize/Info ProjectID=26` and sibling `builtin_models.json project_id=26`. |
| `0x744` | **Not TGP on IDY** — confirms `docs/hardware/README.md`. | `0x46` = 70 while live `GPU_ConfigurableTGPTarget=150`; candidate `ADDR_MYFAN2_L2_PWM`. |
| `0x7D8` | Per-mode **gaming TCC default** currently mirrors the live TCC setting. | `0x0F` = 15 = live `CPU_TccOffset=15` (and `CPU_AmdTccTarget=15`). |
| `0x7B9`/`0x7D0` | Charge-limit pair consistent with `EcChargeLimit` semantics (upper 80, lower = upper − 5). | `0x50`/`0x4B` = 80/75; protocol has no MQTT charge-limit field (docs §5.3). |
| `0x730`–`0x732` | **The active-profile PL1/PL2/PL4 mirror lives here in mode 1, not at `0x783`–`0x785`.** | `0x4B`/`0x55`/`0x91` = 75/85/145 = live `PL1/PL2/PL4` exactly. |
| `0xF00`–`0xF50` | Fan tables are RamFan1-shaped and active: six arrays at the documented bases, **10 non-255 breakpoints** (CPU up 48..85, GPU up 46..81, duty byte = 2×percent). | `temp_levels=10` matches a `builtin_models.json` entry shape; matches `Fan/Status FAN_TableName=M1T1`. |

### Correction to `docs/ec-per-generation.md` §5.1

`0x783`/`0x784`/`0x785` were listed as AGREE (PL1/PL2/PL4) on the earlier sample. This run
reads **0/0/0** while live PL is 75/85/145, and the live values instead match the gaming
default block `0x730`–`0x732`. The "setting value" block is **not** the reliable live
mirror on IDY; treat that row as state-dependent, not settled.

## Still ambiguous after the read-only pass

Read-only cannot separate these; each needs the W4 **double-write对照** (change the value
through the vendor/MQTT path while dumping EC before/after — never an EC write of ours).

| Address | Observed | Why still ambiguous | Follow-up |
|---|---|---|---|
| `0x743`–`0x746` | `00 / 46 / 00 / 00` | `0x744=70≠TGP 150` proves it is not cTGP, but `0x745=0`/`0x746=0` also do **not** mirror live `DynamicBoostTotalProcessingPowerTarget=255`/`DynamicBoostMaximum=25`; `0x743=0` only weakly matches `GPU_DynamicBoostSwitch=0`. | Toggle Dynamic Boost + cTGP via MQTT, diff these four bytes. |
| `0x786`/`0x787` | `00 / 00` | Neither mirrors live TCC 15 (which sits at `0x7D8`). Candidate `AP_EC_LOGO_R`/`L2_PWM_DEFAULT_MYFAN3` vs `TccOffset_Setting`/`FanSwitchSpeedT100mSec` unresolved. | Change TCC offset + logo colour, diff. |
| `0x7C6` vs `0xF5F` (fan-table latch) | `0x7C6=0x04`; `0xF5F=0xC8` | `0x7C6=0x04` equals the sibling's **commit** value, but no release/commit transition was observed. `0xF5D/F5E/F5F=C8/C8/C8` are byte-identical to the trailing GPU_DUTY0 padding (`0xF50`–`0xF5F`), so a read cannot tell latch from duty padding. | Apply a fan curve via GCU and dump before/after; watch `0x7C6` (0x00↔0x04) and `0xF5D`–`0xF5F`. |
| `0x751` | `0x00` | Matches `FanBoostEnable=0` ("Normal 0x00") but boost was off, so Turbo/FanBoost encodings untested. | Toggle FanBoost/Turbo via UI, diff. |
| `0x7AB` | `0x01` | No live anchor for `MyFanCCI_Mode_Index` vs `AP_EC_LOGO`. | Switch MyFan CCI mode / logo, diff. |
| `0x769`–`0x76B` | `00 / 32 / 32` | No keyboard-RGB/logo anchor was captured; defaults `0x76C`–`0x76E` read `00/32/32` too. | Change keyboard colour, diff against `Keyboard/Status`. |
| `0x789`–`0x78C` | all `0x00` | Unwritten on this model; MyFan2 vs MyFan3 layout unresolved. | Change MyFan curve, diff. |
| `0x727` | `0x80` | No camera e-shutter state reported. | Toggle e-shutter, diff. |
| `0x72A` / `0x7D2` / `0xCB` (MUX/GPU status) | `0x00` / `0x1A` / `0x00` | MUX was in Auto (`NV_CTRL_PANEL_AUTOSELECT`, both direct-connect switches OFF). `0x7D2=26` duplicating `0x740` is suspicious but unexplained. | Toggle GPU mode via the vendor path + reboot, diff. |
| `0x7E8` / `0xB0` (mode handshake) | `0x00` / `0x00` | `0x7E8=0` does **not** mirror live `OperatingMode=1`. | Switch modes, diff. |
| `0x74C` | `0xC6` = 198 | No live value matches; `BIOS_PROJECT_ID` is the string `IDY`. | Compare across machines / correlate with `ProjectID`. |
| `0x7E6` / `0x7E7` (LC fan/pump) | `0xA8` / `0x87` | No BLE cooler was connected to anchor against. | Dump while the cooler runs. |
| `0xC7`, `0xFF03` | not in ranges | OSD event code / HID usage page, not EC RAM. | Corroborate in `ec-bit-flags.json` / HID enumeration, not EC. |

## Re-run

```powershell
# from repo root, Debug, one build at a time
dotnet run --project src\Probe -c Debug -- snapshot
# or the already-built binary (no build)
src\Probe\bin\Debug\net10.0\Probe.exe snapshot --mqtt-seconds 6
```

Output defaults to `src\Probe\artifacts\ec-snapshot-<utc>.txt` (gitignored). Flags:
`[outFile]`, `--ranges 0x400-0x4FF,0x720-0x7FF`, `--mqtt-seconds N`, `--delay-ms N`.
