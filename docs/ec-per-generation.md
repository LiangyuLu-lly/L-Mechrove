# Per-generation EC communication reference (MECHREVO 20/30/40/50-series)

Foundation document for removing the dependency on the vendor GCU service: the EC access
protocol to use, the function-by-function / generation-by-generation register map, how to
detect the generation at runtime, the gaps, and the cross-check against our extracted
`ec-registers.json`.

Everything here is **read-only research**. Nothing in this document has been executed on
hardware except where a row explicitly says "live-verified".

## Sources and evidence conventions

| Key | Source | Notes |
|---|---|---|
| `GCS:<line>` | `_decompiled/GCUService/GCUService.decompiled.cs` | Vendor GCUService. Method bodies are ConfuserEx-destroyed; only `const`/`enum`/`struct` declarations survived, so this proves **names + addresses**, never selection logic. |
| `REG:<name>` | `docs/hardware/ec-registers.json` | Our extraction of the same file (258 register slots). |
| `BITS:<enum>` | `docs/hardware/ec-bit-flags.json` | Bit-level meaning of EC status/control bytes. |
| `PID` | `docs/hardware/project-ids.json` | `ProjectID`, `BIOS_PROJECT_ID`, `GN20_GPU_SKU`, `GN21_GPU_SKU` enums. |
| `BM:L<n>` | `%TEMP%\opencode\openrevo-extract\builtin_models.json` | Sibling (OpenRevo) per-model database, 29 models, 10,953 lines. `L<n>` = line of the model's `"model_id"`. |
| `S:<file>:<line>` | `%TEMP%\opencode\openrevo-extract\{ascii,main_ascii_strings,strings,strings_a}.txt` | Strings of the sibling binary `open-revo.exe` (Rust/Tauri). Line = 1-based line in that dump. |
| `REL:<path>` | `release\` | Official vendor payloads (read-only). |
| `SRC:<path>:<line>` | our repo `src\` | Current EC access code. |

Markers used inside the table: **[E]** = directly evidenced by a declaration/string; **[I]** =
inferred (stated as such); **[G]** = not determinable from the sources (see section 4).

Generation labels: **20** = Turing / vendor `GN20`; **30** = Ampere / vendor `GN21`;
**40** = Ada (`RTX 40xx`, PH4 platform); **50** = Blackwell (`RTX 50xx`, PH6 platform).
The vendor has no `GN22`/`GN23` enum in this build; 40/50 are identified by `PH4`/`PH6` project
strings and RTX model/PCI id (`installer/Select-GcuPayload.ps1:11-20,72-107`).

---

## 1. The EC access protocol we will use

### 1.1 Device and IOCTLs

Use the vendor kernel driver `UWACPIDriver.sys` through the device `\\.\ACPIDriver`. This is the
same driver GCUService itself uses, and the only path with a live-verified write.

```
Service   UWACPIDriver                 Type=1 (SERVICE_KERNEL_DRIVER)  Start=3 (DEMAND)
Device    \\.\ACPIDriver               (device description string "ACPIDriver")
HWID      ACPI\INOU0000                Provider "Uniwill"
Binary    %SystemRoot%\System32\drivers\UWACPIDriver.sys
```
Evidence: `REL:release\GCU-common\UWACPIDriver\UWACPIDriver.inf` (`[Standard.NTamd64]`,
`ServiceType=1`, `StartType=3`, `Provider="Uniwill"`, `UWACPIDriver.DeviceDesc="ACPIDriver"`);
`REL:release\GCU-only\README.txt` (component list); `SRC:src\Probe\EcProbe.cs:17`.

The IOCTL constants are declared in the vendor source:

| Operation | Constant | Value | Declared | Input | Output |
|---|---|---|---|---|---|
| EC read | `IOCTL_GPD_ACPI_ECREAD` | `0x9C40A488` (2621482120) | `GCS:6930` | `[u32 address]` (4 B) | `>= 1 byte`; first byte = value |
| EC write | `IOCTL_GPD_ACPI_ECWRITE` | `0x9C40A48C` (2621482124) | `GCS:6932` | `[u32 address][u8 value]` (5 B) | buffer must be non-empty |
| Structured power table | `IOCTL_GPD_ACPI_SMAPCTABLE` | `0x9C40A500` (2621482240) | `GCS:6966` | driver-specific | `SMAPCTABLE_STRUCT` (9 bytes) |
| MMIO byte read | `IOCTL_GPD_ACPI_MMREADB` | `0x9C40A490` | `REG:Ioctls` | `[u32 phys addr]` | 1 byte |
| MMIO byte write | `IOCTL_GPD_ACPI_MMWRITEB` | `0x9C40A498` | `REG:Ioctls` | `[u32 phys addr][u8 value]` | — |

The EC address is a 16-bit value passed as a **u32** in the input buffer, not as a bank+offset
pair; there is no separate bank register. ECSpec registers span `0x0001`-`0xD4F`, fan tables
`0xF00`-`0xFFF`; the `0xFF02`-`0xFF12` values are ITE HID usage-page ids, not EC RAM. The MMIO alias maps the EC at physical `0xFED50000 + offset`
(`SRC:src\Probe\EcProbe.cs:90`), but the direct ECR EAD/ECWRITE path is what we use.

**Write shape is the classic trap.** `DeviceIoControl` returns success for a 3-byte
`[u16 addr][u8 value]` buffer, but the driver (`0x1400025B8`) memcpys 4 B + 1 B into ACPI method
`ECRW`, so a short buffer is misparsed as address `0x5007B9` and the register does not move.
A 4-byte read input is likewise required. Both facts are live-verified
(`SRC:src\MechrevoLiteWin\Hardware\EcChargeLimit.cs:10-14,166-198`;
`docs\hardware\README.md` "EC 直写通路").

### 1.2 Exact call shape (authoritative implementation)

```
CreateFile("\\.\ACPIDriver", GENERIC_READ|GENERIC_WRITE (0xC0000000), share=3, OPEN_EXISTING=3)
Read(addr):  in  = 4 bytes  [ (u32)addr                     ]
             out = 16 bytes
Write(addr,v): in = 5 bytes [ (u32)addr ][ (u8)v            ]
             out = 16 bytes (must be non-NULL, else ERROR_INVALID_PARAMETER 87)
CloseHandle
```
`SRC:src\MechrevoLiteWin\Hardware\EcChargeLimit.cs:117,166-198`.

**Do not copy `EcProbe.ReadReg`.** It uses a 2-byte input (`Marshal.WriteInt16`) and its comment
claims `0x9C402108` while the constant next to it is `0x9C40A488`
(`SRC:src\Probe\EcProbe.cs:7,28-29`). That is inconsistent with the live-verified 4-byte read
layout and with `EcChargeLimit`; treat `EcProbe` as diagnostic-only until corrected.

### 1.3 Locking / serialisation

The EC is a single, slow (typically ms) bus behind an ACPI method; there is no per-register
atomicity. All EC traffic in a process must go through **one** gate.

- Today `EcChargeLimit` serialises with `static readonly object Gate`
  (`SRC:src\MechrevoLiteWin\Hardware\EcChargeLimit.cs:50,115,153`) but `EcProbe` has no lock, and
  the two are independent. Before adding more callers, move both behind a single EC module with
  one lock, and keep each logical write group (e.g. charge-limit upper+lower) inside one lock
  scope so a foreign writer cannot interleave.
- There is no handshake/acknowledge register evidenced for EC writes; the vendor code confirms by
  **read-back** (`SRC:src\MechrevoLiteWin\Hardware\EcChargeLimit.cs:123-143`), and the sibling
  does the same (`S:ascii.txt:24598` `, EC 1859=0x`, `:24599` `, EC 1860=`). Use read-back, not
  `DeviceIoControl` success, as the success criterion.
- Any writer must tolerate a one-shot read-back miss caused by EC bus timing and re-read before
  declaring failure (`SRC:src\MechrevoLiteWin\Battery\BatteryControl.cs:120-134`).

### 1.4 The sibling's protocol (considered, not adopted)

`open-revo.exe` reaches EC two ways, both evidenced only as strings (the IOCTL immediates are in
code, not in the string table, so the exact codes are **not** recoverable from the dumps):

1. **ACPIDriver IOCTL**: `CreateFileA` + `DeviceIoControl` (`S:imports.txt` decoded: `CreateFileA`,
   `DeviceIoControl`, `NtDeviceIoControlFile`); log tags ` via ACPIDriver IOCTL` (`S:ascii.txt:24587`),
   `ACPIDriver device open failed` (`:24620`), `IOCTL failed` (`:24623`), `CreateFileA failed` (`:24632`).
   The only device path string is `\\.\ACPI` (verbatim `\\.\ACPIH`, the `H` is adjacent-byte bleed;
   `S:ascii.txt:9404,9410,9424,9431`).
2. **WMI fallback**: `wmi_read_ec(` (`S:ascii.txt:24585,24589`), with AML method fragments `ECRR`
   (`S:strings_a.txt:233098`) and `ECRW` (`S:strings_a.txt:233134`).

It also embeds and self-deploys `UWACPIDriver.sys` to `System32\drivers`
(`S:ascii.txt:25141-25142`). We do **not** adopt `\\.\ACPI`/WMI: the codes are unknown, the path
string is ambiguous, and our `\\.\ACPIDriver` + `0x9C40A488/0x9C40A48C` path is the one that
matches GCUService's own declarations and has been live-verified.

### 1.5 PawnIO is not an EC path

`PawnIOWrapper` talks to `\\?\GLOBALROOT\Device\PawnIO` and exists only for AMD SMU (Ryzen)
writes; it is dormant (triple-gated) and is not used for EC
(`SRC:src\MechrevoLiteWin\Pawn\PawnIOWrapper.cs:9-33`). Keep it out of the EC design.

### 1.6 Risks of this transport

1. `\\.\ACPIDriver` grants read/write to non-admin processes; combined with `WRITE_PORT_*` that is
   a local privilege-escalation primitive (`docs\hardware\README.md`, "两个必须知道的风险").
   Depending on it makes that a product requirement.
2. The driver signing certificate expired 2026-02-19; existing binaries still load because kernel
   drivers check the timestamp, but no new signed build will come. If Windows tightens policy the
   whole path dies (`docs\hardware\README.md`).
   Mitigation for both: keep every EC write read-back-verified and degrade to "ignore + log"
   rather than assuming success, exactly as `EcChargeLimit`/`BatteryControl` already do.

---

## 2. Function x generation table

Addresses are EC-space bytes unless noted. **GEN** columns: an address listed in a column is
usable on that generation's platform **as far as the sources show**; "variant" means the register
layout is selected per model, not per generation, and the selector is not recoverable ([G], §4).

### 2.1 Main table

| # | Function | EC address(es) | Byte / bit semantics | R/W | Required sequence / order | 20 | 30 | 40 | 50 | Evidence |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | Fan curve RAM (CPU/GPU up-temp, down-temp, duty) | RamFan1/1p5: `0xF00` CPU_UP0, `0xF10` CPU_DOWN0, `0xF20` DUTY0, `0xF30` GPU_UP0, `0xF40` GPU_DOWN0, `0xF50` GPU_DUTY0 (stride 0x10, 16 entries). RamFan2: `0xF00`-`0xFE0`, 15 arrays (F1..F3 x {CPU_UP,CPU_DOWN,DUTY,GPU_UP,GPU_DOWN}) | Up-temp/down-temp = degrees C (255 = unused slot); Duty = byte, scale differs between vendor payload (basis 100) and sibling DB (up to 180) [G] | W (write whole table) | **[I]** release latch -> write thresholds+duties -> commit latch. Vendor order itself is destroyed [G] | variant | variant | variant | variant | `GCS:1111-1177` (RamFan1 0xF00-0xF50, RamFan1p5 +0xF5D/0xF5E/0xF5F, RamFan2 0xF00-0xFE0); `REG:RamFan1_ECSpec/RamFan1p5_ECSpec/RamFan2_ECSpec`; `REL:...\UserFanTables\DefaultFanTable_Turbo.json` (`CPU[]{ID,UpT,DownT,Duty}`, 16 rows) |
| 2 | Fan duty/RPM read-back | `0x75B` main fan L duty, `0x75C` main fan R duty, `0x464`+`0x465` main fan RPM (16-bit), `0x46B`+`0x46C` second fan RPM | duty = byte; RPM = little-endian 16-bit (live: `0x09/0x44` -> 2372) | R | none | E | E | E | E | `GCS:659-679`; live read `docs\hardware\README.md` (0x464/0x465) |
| 3 | Fan-table latch | GCU variant: `0xF5F` `ADDR_RAMFAN1P5_TABLE_CTRL` (+ `0xF5D`/`0xF5E` status). Sibling variant: `0x7C6` `ADDR_AP_OEM_BYTE6` = `0x00` release / `0x04` commit | values not in declarations [G]; sibling proves 0x00/0x04 | W | release -> table write -> commit (sibling, `S:ascii.txt:24617,24682,24701`); GCU order unknown [G] | variant | variant | `0x7C6` (sibling) | `0x7C6` (sibling) | `GCS:1137-1141`; `S:ascii.txt:24617` `Fan table hardware latch released: EC 1990 = 0x00`, `:24682` `... committed: EC 1990 = 0x04`, `:24701` full-table commit |
| 4 | Fan boost / turbo bits | `0x751` `ADDR_MAFAN_CONTROL_BYTE`; event byte `0xA7` `OSD_FANBOOST_UPDATE` | Normal `0x00`, Turbo `0x10`, FanBoost `0x40`, User `0x80`-`0x85`, User_HiMode `0xA0` (`BITS:MyFanCTLByteFlag`); support bit `0x80` `BITS:SupportByteOneFlag.FanBoost`; status bit `0x04` `BITS:StatusByteOneFlag.FanBoost`; trigger `0x04` `BITS:TriggerByteFlag.FanBoost_Trigger` | R/W | none evidenced; sibling toggles bit directly | E | E **differs** | E | E | `GCS:671` (`ADDR_MAFAN_CONTROL_BYTE = 1873`), `GCS:993` (`OSD_FanModeSwitch`, unrelated event); `BITS`; sibling `S:ascii.txt:24662` `EC 1873: 0x`, `:24671` `(40/50-series mode): EC 1873 Bit 6 (0x40)`, `:24674` `(30-series mode): 1873=` |
| 5 | PL1 / PL2 / PL4 (setting) | `0x783` PL1, `0x784` PL2, `0x785` PL4 | watts, byte; defaults: gaming `0x730`/`0x731`/`0x732`, office `0x734`/`0x735`/`0x736`, battery-saver `0x7A7`/`0x7A8`/`0x7A9` | R/W | write setting bytes; read-back confirm. Order vs TCC/fan table unknown [G] | E | E | E | E | `GCS:741-745`, `GCS:725-737`, `GCS:819-825`; `SMAPCTABLE_STRUCT.PL1/PL2/PL4` `GCS:7608-7612` via `IOCTL_GPD_ACPI_SMAPCTABLE` `GCS:6966`; live `docs\hardware\README.md` (0x783-0x785 = 210); sibling `S:ascii.txt:24652-24655` `PL1=...W (1923), PL2=...(1924), PL4=...(1925)` |
| 6 | TCC offset | setting `0x786` (RamFan1p5 `ADDR_TimAP_TccOffset_Setting`); per-mode defaults `0x7D8` gaming, `0x7D9` office, `0x7DA` turbo | degrees C offset, byte | R/W | none evidenced | variant | variant | variant | variant | `GCS:865-869`, `GCS:1143`; sibling `S:ascii.txt:24643` `factory_tcc=`, `:24644` ` -> EC 1926=0x` |
| 7 | cTGP (Configurable TGP) | `0x744` `ADDR_ConfigurableTGP_VALUE` (ctrl byte `0x743`) | watts, byte; also exposed as `SMAPCTABLE_STRUCT.ConfigurableTGP`/`DefaultTGP` | R/W | write value; read-back confirm | [G] | E via GN21 special path | E | E | `GCS:703-705`, `GCS:7616-7622`; `REL:...\UserPofiles\Mode4_Profile1.json` `CustomTGPinGCUforGN21_Value/_Enable`; live contradiction on 0x744 (see §5) |
| 8 | Dynamic boost | ctrl `0x743`, total-processing-power target `0x745`, max TGP `0x746`; sibling writes `0x743`+`0x744` | watts, byte; `SMAPCTABLE_STRUCT.DynamicBoost`/`DynamicBoostCpuTdp` | R/W | write ctrl then value, read back each. Sibling order: ctrl `0x743` then `0x744` (`S:ascii.txt:24593-24599`) | E | E | E | E | `GCS:703-709`, `GCS:7618-7624`; sibling `S:ascii.txt:24593` `W -> EC 1859 (0x`, `:24595` `), EC 1860=`, `:24601` `set_dynamic_boost: DISABLE \| EC writes: 1859=0x` |
| 9 | GPU mode / MUX | **No EC address evidenced.** Candidates only: `0x72A` `ADDR_GPU_STATUS`, `0x7D2` `ADDR_ModuleID_GPU`, `0xCB` `PHxGPURule1` | GPU mode is driven by MQTT actions `DGPU_DIRECT_CONNECT_*` / `IGPU_ONLY_CONNECT_RB_*`, and internally by `m_DGpuMode` / `SetDGpuModeWithOverBoost`; not an EC write in any declaration | [G] | vendor path = MQTT `Setting/Control`, then a Windows restart; NVRAM access differs 40 ("Direct") vs 50 ("Door-assisted") | [G] | [G] | [G] | [G] | `GCS:2363-2389` (actions), `GCS:22102,22946,22210` (`SetDGpuModeWithOverBoost`, `SetGPUdstateByGpuMode`), `GCS:861` (`ADDR_GPU_STATUS = 1834`); sibling `S:ascii.txt:36190` ` MUX `, `:36194` MUX reboot, `:24495` `40-Series (Direct)50-Series (Door-assisted)` |
| 10 | Battery charge limit | upper `0x7B9`, recharge lower `0x7D0`; mode byte DBAP `0x7A6` | percent, byte; upper 0 = firmware "no limit"; lower = upper - hysteresis | R/W | read both -> write upper -> read-back -> write lower -> read-back; roll both back on any mismatch | live-verified (IDY) | [G] | [G] | [G] | `GCS:835` (`ADDR_BATTERY_CHARGE_LIMIT_UP = 1977`), `GCS:857` (`ADDR_BATTERY_CHARGE_LIMIT_DOWN = 2000`), `GCS:817` (`ADDR_AP_OEM_BYTE4 = 1958` = DBAP); `SRC:src\MechrevoLiteWin\Hardware\EcChargeLimit.cs:32-35,103-147`; `docs\hardware\README.md` charge-limit section |
| 11 | Keyboard RGB (level/power) | EC: `0x769`/`0x76A`/`0x76B` `ADDR_RGBKB_LEVEL_R/G/B`, defaults `0x76C`-`0x76E`, music `0x76F`. HID: usage pages `0xFF02`/`0xFF03`/`0xFF12`, report bytes `0xB1`/`0xB2`/`0xF0`/`0xB6` | R/G/B bytes; power/state via trigger bytes `0x767`/`0x768` | R/W (EC) or HID feature report | none evidenced | variant (`keyboard_type`) | variant | variant | variant | `GCS:1095-1107`, `GCS:4565-4575` (`EC_RGBKB_LEVEL_*`), `GCS:4979-4983`; `REG:HIDKeyboard/ITE_SPEC`; model `keyboard_type` in `BM` (TypeA/TypeB/SingleZone_RGB) |
| 12 | Lightbar power + brightness | `0x748` `ADDR_LIGHTBAR_CONTROL_BYTE`, `0x749`/`0x74A`/`0x74B` `RED/GREEN/BLUEBAR_CONTROL_BYTE` | `BITS:RGBLightbarControlByteFlag` (AP_Exist 0x0, PowerSave 0x2, S0 0x4, S3 0x8, Welcome 0x80); `BITS:RGBLightBarCtrlByteFlag` (ApExit 0x1, LB10NoKey 0x10, LB10KeyPress 0x20, Breath 0x40, Welcome 0x80) | R/W | none evidenced | variant (lightbar support) | variant | variant | variant | `GCS:767-773`; `BITS` |
| 13 | Logo power + brightness | `0x7AB` `ADDR_AP_EC_LOGO`; `0x769`-`0x76B` `ADDR_AP_EC_LOGO_R/G/B`; confirm `0x78C` `ADDR_AP_EC_LOGO_CONFIRM`; `0x78C` `ADDR_SINGLEKBL_ENABLE` / `0x78E` `ADDR_SINGLEKBL_SUPPORTPOWER`; single-zone `0xD00`; `0x7C5` BatteryLogoEnable | R/G/B bytes; enable/confirm bytes | R/W | none evidenced | variant | variant | variant | variant | `GCS:789-803`, `GCS:757-759`, `GCS:787,799`; `REG:0x7C5` |
| 14 | Liquid cooler pump / fan / light | fan `0x7E6` `ADDR_LC_FAN_VALUE`, pump `0x7E7` `ADDR_LC_PUMP_VALUE`; cooling mode `0x7C7` `CoolingModeECAddress` (= `ADDR_AP_OEM_BYTE7`); events `0xC0` `SC_SwitchOC`, `0xC1` `SC_PumpFanAuto`, `0xC8` `SC_ThermalProtect`, `0xC9` `SC_CustomMode` | byte values; semantics not in declarations [G] | [G] (R/W shape unknown) | unknown [G]. Official runtime drives the cooler over BLE, not EC (`S:ascii.txt:24250-24255`) | n/a | n/a | n/a | n/a | `GCS:871-873`, `GCS:19230,19675` (`CoolingModeECAddress = 1991`), `GCS:46626` (`UserSet_CoolingMode`); `REG:ECSpec 0xC0/0xC1/0xC8/0xC9`; `BM.has_water_cooler` |
| 15 | Power-mode handshake | `0x7E8` `ADDR_EC_DEFAULT_MODE`; event `0xB0` `OSD_FanModeSwitch`; `0x751` Turbo_Mode `0x10`; per-mode default blocks `0x730`-`0x737`, `0x7A7`-`0x7AA`, `0x7D8`-`0x7DA` | mode index byte; `ADDR_EC_DEFAULT_MODE` exists but value map unknown [G] | R/W | unknown [G]. Official sequence is two MQTT commands: `Fan/Control` (`SET_OPERATING_MODE_DETAIL` + `ProfileIndex`) then `LCHWOC/Control` (`IsNormalRun`/`IsCustomRun`) | variant | variant | variant | variant | `GCS:875`, `GCS:993`, `GCS:725-737,819-825`; `REL:...\UserPofiles\MainOption.json` `"OperatingMode": 2`; sibling `S:ascii.txt:24694-24700` (`apply_mode_fan_table`, `OperatingMode`), `:22544` `Safely gating EC to Balance (2)`; `docs\gcu-dependency-matrix.md` mode-switch section |
| 16 | Identity / model read (gating input) | `0x740` `ADDR_PROJECT_ID_BYTE`, `0x74C` `ADDR_OEMSERVICE_PROJECT_ID_BYTE`, `0x456` `ADDR_SystemID`, `0x454` `ecOEM_EC_VER`, `0x4AB` `ecBt1RSOC`, `0x490` `ecPowSource` | project id byte(s); EC version; RSOC percent; power-source bitfield (`BITS:StatusByteOneFlag` family) | R | read-only | E | E | E | E | `GCS:687,775,879,1045,569,563`; live reads `docs\hardware\README.md` (0x4AB, 0x490) |

Row count: **16**.

### 2.2 Which register block each generation would select (best available mapping)

The sources do **not** contain the selector. The following is the strongest available statement,
with the evidence for each block's existence:

| Block | Addresses | Applies to | Evidence |
|---|---|---|---|
| `RamFan1_ECSpec` (6 arrays) | `0xF00`-`0xF50` | [G] | `GCS:1109`, `REG:RamFan1_ECSpec` |
| `RamFan1p5_ECSpec` (6 arrays + status/ctrl) | `0xF00`-`0xF50`, `0xF5D`/`0xF5E`/`0xF5F`, `0x786`, `0x787` | [G] — selected by `RamFan1p5Support` capability bit | `GCS:1123-1145`, `REG:RamFan1p5_ECSpec`; `docs\hardware\README.md`; `MechrevoDeviceCapabilities.cs:250` |
| `RamFan2_ECSpec` (3 tables x 5 arrays) | `0xF00`-`0xFE0` | [G] | `GCS:1147-1177`, `REG:RamFan2_ECSpec` |
| MyFan2 address block | `0x743`-`0x747`, `0x789`-`0x78D` | [G] — chosen vs MyFan3 by `ADDR_MAFAN_CONTROL_BYTE`/`MyFanCCI_Mode_Index` | `GCS:693-719`, ambiguity list `docs\hardware\README.md` |
| MyFan3 address block | `0x786`-`0x78B`, `0x738` | [G] | `GCS:747-755,761,783` |

`TempLevels` (the number of active points) differs per model and is the only per-model fan-table
resolution signal in the sibling DB: 6/7/8/10/11 points across 29 models (`BM:L4` GM6IX9B=10,
`BM:L399` X6AR55=6, `BM:L1105` XxPTL=11, ...).

---

## 3. Generation detection at runtime

### 3.1 Signals, strongest first

| Signal | 50 | 40 | 30 | 20 | Evidence |
|---|---|---|---|---|---|
| NVIDIA GPU marketing name (`Win32_VideoController` / `Win32_PnPEntity`) | `RTX 50[5-9]x` | `RTX 40[5-9]x` | `RTX 30xx` | `RTX 20xx` | `installer\Select-GcuPayload.ps1:72-79` |
| NVIDIA PCI device-id high byte | `0x2B/0x2C/0x2D/0x2E/0x2F` (Blackwell) | `0x26/0x27/0x28` (Ada) | not evidenced | not evidenced | `installer\Select-GcuPayload.ps1:88-95`; `installer\README.md` GCU selection rule |
| BIOS project string (`ItemSupport\BIOS_PROJECT_ID`, fallback SMBIOS `SystemProductName`) | `PH6*` | `PH4*` | legacy `GK*`/`GM5*` in vendor enum | legacy `GI`/`GJ`/`GK` in vendor enum | `installer\Select-GcuPayload.ps1:98-107`, `:165-171`; `MechrevoDeviceCapabilities.cs:215`; `PID:ProjectId` (`GI=1`, `GJ=2`, `GK=3`, `PH4*=0x1801..`, `PH6*=0x1704/0x1706/0x1805..`) |
| Vendor GPU-SKU enums | (none) | (none — no `GN22`/`GN23` in this build) | `GN21_GPU_SKU` | `GN20_GPU_SKU` | `GCS:1342`, `GCS:1355`; `PID:Gn20GpuSku`, `PID:Gn21GpuSku` |
| Sibling boolean | `is_50_series` field | `RTX 40/30-series (Standard architecture)` | same as 40 | not evidenced | `S:ascii.txt:22416`, `:22651-22652`; `S:ascii.txt:22628` `30-series machine detected: bypassing nvidia-smi power probe` |

Sibling's own 3-signal detector (useful as a corroborating design):

```
Signal 1: Active Profile            -> RTX 50 | 40/30
Signal 2: Registry (RTX 50 / RTX 40/30 / RTX 30 / GTX)
Signal 3: DMI Baseboard (PH/GK/GM5) + GPU Bridge
values seen: 5060/5070/5080/5090, 4060/4070/4080/4090
```
`S:ascii.txt:22548` (concatenated field/pool), `:22652` `PLATFORM_DETECT` +
`RTX 40/30-series (Standard architecture)RTX 50-series (Next-Gen architecture)`.

### 3.2 Detection we will implement

1. Read NVIDIA PCI device id(s) and marketing name (authoritative for 40 vs 50).
2. Read `HKLM\SOFTWARE\OEM\GamingCenter2\ItemSupport\BIOS_PROJECT_ID` (or the sibling keys
   `GamingCenter\ItemSupport`, `ControlCenter\ItemSupport`); `PH6*` -> 50, `PH4*` -> 40
   (`MechrevoDeviceCapabilities.cs:12-17,215`; `installer\Select-GcuPayload.ps1:153-171`).
3. For 30/20, fall back to the RTX model family and (if present) the `GN20`/`GN21` SKU
   classification; there is **no runtime code path** for this in any source -> [G].
4. Cross-check the capability profile (`ItemSupport`) flags, which are per-project and already
   consumed by `MechrevoDeviceCapabilities`; they select the layout variants in §2.2.

EC-side identity bytes `0x740`/`0x74C` exist and could carry a project id, but their value map is
not recoverable from declarations -> [G]; do not use them for generation until probed.

**Naming collision to avoid.** `builtin_models.json` `"project_id"` (24/25/26) is the sibling's
own MQTT-reported `ProjectID` (live machine reports `ProjectID=26`,
`docs\superpowers\plans\m1-findings.md`), **not** the GCUService `ProjectID` enum (where
`PH6TRX1=21`). Its `"bios_project_id"` mixes real BIOS ids (`IDB`) with project-code suffixes
(`TRX1`, `PTL`, `PG7`, ...). Never compare the two schemes numerically.

---

## 4. Gaps (function not determinable from the sources)

| # | Gap | What would settle it |
|---|---|---|
| 1 | Which fan-table spec (`RamFan1` / `RamFan1p5` / `RamFan2`) applies to 20/30/40/50, and the `RamFan1p5Support` selector | Read-only dump of `0xF00`-`0xFFF` on one real machine per generation + compare to the written table shape |
| 2 | Fan-table latch: register (`0xF5F` vs `0x7C6`) and the values that release/commit | GCU double-write observation (change a fan curve via GCU, dump EC before/after) |
| 3 | 30-series fan-boost encoding at `0x751` (the sibling branch is truncated: `(30-series mode): 1873=`) | Full sibling constant `open-revo.exe` static disassembly, or a 30-series probe |
| 4 | GPU mode / MUX EC mechanism (whether it is EC at all) | Probe `0x72A`/`0x7D2`/`0xCB` while toggling via the vendor path |
| 5 | `0x743`-`0x746`: cTGP/DynamicBoost vs `MYFAN2_L1..L4_PWM` choice per model | Detect MyFan generation (`0x751`/`0x7AB`) + `OcSettingsSupport`; live evidence already contradicts the name (see §5) |
| 6 | `0x786`/`0x787`: ECSpec (`AP_EC_LOGO_R`/`L2_PWM_DEFAULT_MYFAN3`) vs RamFan1p5 (`TccOffset_Setting`/`FanSwitchSpeedT100mSec`) | Per-generation read of the spec-class actually in use |
| 7 | Exact EC addresses the sibling uses for battery charge limit (`Charge_Limit_Upper/Lower` are WMI field names only) and for keyboard/logo (`has_ite8291`, `is_ec_logo`) | Static disassembly of `open-revo.exe` around those symbols |
| 8 | Liquid-cooler EC writes (values, R/W direction, sequence) — official runtime uses BLE | Observe EC while the vendor cooler UI changes pump/fan |
| 9 | Fan-table row layout (3 fields vs 5 fields per row) and Duty scale per generation | Correlate written table with `FanTable1p5Buffer`/`FanTable2Buffer` (`GCS:47577,47628`) on real hardware |
| 10 | Write ordering across blocks (PL/TCC/cTGP/fan table/latch) and any unlock/preamble | GCU double-write observation; declarations contain no sequence |
| 11 | Whether a mode-handshake register (`0x7E8`/`0xB0`) must be written before/after parameter writes | Toggle modes via vendor path and diff EC |
| 12 | 20-series support in this firmware build | `installer` ships only 40/50 payloads; `GN20_GPU_SKU` exists but has no runtime path |
| 13 | Whether `\\.\ACPI` (sibling) is the same device as `\\.\ACPIDriver` | Compare driver symbolic links on a machine with the sibling running |

---

## 5. Cross-check against `docs/hardware/ec-registers.json`

Verdict legend: **AGREE** = our map has the same address for the same function; **DISAGREE** =
different address/semantics; **SILENT** = our map (or the sibling) has no entry.

### 5.1 Agreements

| Function | Address | Our entry (`REG`) | Independent evidence | Verdict |
|---|---|---|---|---|
| Fan boost | `0x751` | `ADDR_MAFAN_CONTROL_BYTE` (`GCS:671`) | sibling `EC 1873 Bit 6 (0x40)` (`S:ascii.txt:24671`) | AGREE (bit 6 = `0x40` matches `BITS:MyFanCTLByteFlag.FanBoost_Mode`) |
| PL1/PL2/PL4 | `0x783`/`0x784`/`0x785` | `ADDR_PL1/PL2/PL4_SETTING_VALUE` (`GCS:741-745`) | sibling `(1923)/(1924)/(1925)` (`S:ascii.txt:24652-24655`) | AGREE |
| Dynamic boost ctrl | `0x743` | `ADDR_ConfigurableTGP_DynamicBoost_CTRL_BYTE` (`GCS:703`) | sibling `EC 1859` (`S:ascii.txt:24593`) | AGREE |
| Charge limit | `0x7B9`/`0x7D0` | `ADDR_BATTERY_CHARGE_LIMIT_UP/DOWN` (`GCS:835,857`) | live write + read-back (`EcChargeLimit.cs`, `README.md`) | AGREE (live-verified) |
| Fan table latch name | `0x7C6` | `ADDR_AP_OEM_BYTE6` alias-only (`GCS:801`) | sibling latch writes `0x7C6` | AGREE on address; sibling adds semantics |
| Cooling mode | `0x7C7` | `CoolingModeECAddress` (`GCS:19230,19675`) = `ADDR_AP_OEM_BYTE7` | — | AGREE |

### 5.2 Disagreements

1. **`0x744` is not TGP on the live machine.** `ec-registers.json` declares
   `ADDR_ConfigurableTGP_VALUE`, and the sibling writes DynamicBoost/`1860` there; but the live
   IDY machine reads 70 while TGP is 150, i.e. on that model `0x744` is `ADDR_MYFAN2_L2_PWM`
   (`docs\hardware\README.md`; `GCS:705,695`).
2. **Fan-table latch register.** GCUService `RamFan1p5` exposes `0xF5F`
   `ADDR_RAMFAN1P5_TABLE_CTRL` (`GCS:1141`); the sibling latches via `0x7C6` = `0x00`/`0x04`
   (`S:ascii.txt:24617,24682`). Different register, different protocol.
3. **`0x7C5` semantics.** Sibling uses `0x7C5` bit 7 for fan-isolated output
   (`S:ascii.txt:36645`); `ec-registers.json` names `0x7C5` `BatteryLogoEnable` only. The
   decompiled source has **both** `ADDR_AP_OEM_BYTE5 = 1989 (0x7C5)` (`GCS:787`) and
   `BatteryLogoEnable`; our JSON dropped `ADDR_AP_OEM_BYTE5`.
4. **`0x786` / `0x787` spec-class clash.** Sibling `TccOffset = EC 1926` and fan ramp
   `EC 1927` (`S:ascii.txt:24644,36621`) agree with `RamFan1p5_ECSpec`
   (`GCS:1143,1145`) but not with `ECSpec`, where `0x786`/`0x787` are
   `ADDR_AP_EC_LOGO_R`/`ADDR_L2_PWM_DEFAULT_MYFAN3`. The address is generation/variant
   dependent, exactly the documented ambiguity class.
5. **Sibling DynamicBoost target address.** Sibling writes the DB value to `0x744`/`1860`;
   GCUService declares the DB target as `0x745` `ADDR_DynamicBoost_TotalProcessingPowerTarget_VALUE`
   (`GCS:707`) and `0x744` as cTGP value. Same two bytes, opposite roles.
6. **Liquid cooler path.** `ec-registers.json` has a full EC path
   (`0x7E6`/`0x7E7`/`0x7C7`, events `0xC0`/`0xC1`/`0xC8`/`0xC9`); the sibling only drives the
   cooler over BLE (`S:ascii.txt:24250-24255`). Our map is the only source of an EC path, and it
   is unverified.
7. **MUX / GPU mode.** No EC address in either source; both use non-EC channels. Our map's
   `0x72A`/`0x7D2`/`0xCB` are candidates, not confirmations.
8. **Project-id scheme collision.** `builtin_models.json` `project_id` 24/25/26 vs the GCUService
   `ProjectID` enum (`PID`); `builtin_models.json` `bios_project_id` mixes `IDB` (real BIOS id)
   with `TRX1`/`PG7`/`PTL` (project codes). Not numeric-comparable.
9. **Alias loss in `ec-registers.json`.** The decompiled source holds aliases that our JSON keeps
   only under one `PrimaryName`:
   - `0x741` -> JSON `ADDR_FAN_ALERT_BYTE`; dropped `ADDR_AP_OEM_BYTE` (`GCS:689,779`)
   - `0x766` -> JSON `Light_ChinaMode`; dropped `ADDR_SUPPORT_BYTE2` (`GCS:685`)
   - `0x782` -> JSON `Light_SetToChinaMode`; dropped `ADDR_BIOS_OEM_BYTE2` (`GCS:723`)
   - `0x7C5` -> JSON `BatteryLogoEnable`; dropped `ADDR_AP_OEM_BYTE5` (`GCS:787`)

### 5.3 Silent

- Battery charge-limit addresses: sibling exposes only WMI field names
  (`Charge_Limit_Upper`/`Charge_Limit_Lower`, `set_battery_limit`;
  `S:ascii.txt:36078,22548`) — silent on the EC address.
- Keyboard/logo EC addresses: sibling names only `has_ite8291`, `is_ec_logo`,
  `Lightbar_logo_*` registry keys (`S:ascii.txt:22416,24320`) — silent on EC addresses.

---

## 6. Payload-level generation differences (release\)

| Item | 40-series payload (`release\GCU-40-51751`, `GCU-40-51749`) | 50-series payload (`release\GCU-only`) | Evidence |
|---|---|---|---|
| Service dir | `AiStoneService` (51751) / `UniwillService` (51749) | `AiStoneService` | `installer\Select-GcuPayload.ps1:53-57` |
| `UserFanTables` layout | 23 flat files: `M1T1`..`M4T5` (20) + `DefaultFanTable_{Gaming,Office,Turbo}` (3), no per-model dirs | same 23 flat files **plus** 24 per-model dirs `PH4*`/`PH6*` | directory listing of `release\GCU-only\...\UserFanTables` and `release\GCU-40-51751\...\UserFanTables` |
| `DefaultFanTable_Turbo.json` shape | same schema | same schema (`Activated`, `Name`, `FanControlRespective`, `CpuTemp_DefaultMaxLevel`, `GpuTemp_DefaultMaxLevel`, `CPU[]`/`GPU[]` of `{ID,UpT,DownT,Duty}`, 16 rows) | `REL:...\UserFanTables\DefaultFanTable_Turbo.json` |
| Driver | shared | shared | `installer\README.md` (`payload\common`), `REL:release\GCU-common\UWACPIDriver\UWACPIDriver.inf` |

---

## Appendix A - decompiled EC constant map (`GCUService.decompiled.cs`)

Complete set of `ADDR_*` / `ec*` / `CoolingModeECAddress` / `PIXART_*` / `MonitorOn` constant
declarations, with the C# line. Format: `line name = decimal (0xHEX)`.

```
  563: ecPowSource = 1168 (0x490)
  565: ADDR_BIOS_INFO_3_BYTE = 1183 (0x49F)
  567: ecBt1Temperature = 1186 (0x4A2)
  569: ecBt1RSOC = 1195 (0x4AB)
  635: ADDR_EC_BIF_DC_BYTE1 = 1026 (0x402)
  637: ADDR_EC_BIF_DC_BYTE2 = 1027 (0x403)
  639: ADDR_EC_BIF_DV_BYTE1 = 1032 (0x408)
  641: ADDR_EC_BIF_DV_BYTE2 = 1033 (0x409)
  643: ADDR_EC_BST_BPR_BYTE1 = 1076 (0x434)
  645: ADDR_EC_BST_BPR_BYTE2 = 1077 (0x435)
  647: ADDR_EC_BST_BRC_BYTE1 = 1078 (0x436)
  649: ADDR_EC_BST_BRC_BYTE2 = 1079 (0x437)
  651: ADDR_EC_BST_BPV_BYTE1 = 1080 (0x438)
  653: ADDR_EC_BST_BPV_BYTE2 = 1081 (0x439)
  655: ADDR_EC_BT1CycleCount_BYTE1 = 1190 (0x4A6)
  657: ADDR_EC_BT1CycleCount_BYTE2 = 1191 (0x4A7)
  659: ADDR_EC_MAIN_FAN_RPM_BYTE1 = 1124 (0x464)
  661: ADDR_EC_MAIN_FAN_RPM_BYTE2 = 1125 (0x465)
  663: ADDR_EC_BIOS_INFO5 = 1126 (0x466)
  665: ADDR_EC_SECOND_FAN_RPM_BYTE1 = 1132 (0x46C)
  667: ADDR_EC_SECOND_FAN_RPM_BYTE2 = 1131 (0x46B)
  669: ADDR_AP_BIOS_CONTROL_BYTE = 1798 (0x706)
  671: ADDR_MAFAN_CONTROL_BYTE = 1873 (0x751)
  673: ADDR_CPU_VRM_CURRENT_LIMIT_BYTE = 1875 (0x753)
  675: ADDR_CPU_VRM_MAXI_CURRENT_LIMIT_BYTE = 1876 (0x754)
  677: ADDR_EC_MAIN_FAN_L_DUTY_BYTE = 1883 (0x75B)
  679: ADDR_EC_MAIN_FAN_R_DUTY_BYTE = 1884 (0x75C)
  681: ADDR_TRIGGER_BYTE2 = 1885 (0x75D)
  683: ADDR_SUPPORT_BYTE1 = 1893 (0x765)
  685: ADDR_SUPPORT_BYTE2 = 1894 (0x766)
  687: ADDR_PROJECT_ID_BYTE = 1856 (0x740)
  689: ADDR_AP_OEM_BYTE = 1857 (0x741)
  691: ADDR_SUPPORT_BYTE5 = 1858 (0x742)
  693: ADDR_MYFAN2_L1_PWM = 1859 (0x743)
  695: ADDR_MYFAN2_L2_PWM = 1860 (0x744)
  697: ADDR_MYFAN2_L3_PWM = 1861 (0x745)
  699: ADDR_MYFAN2_L4_PWM = 1862 (0x746)
  701: ADDR_MYFAN2_L5_PWM = 1863 (0x747)
  703: ADDR_ConfigurableTGP_DynamicBoost_CTRL_BYTE = 1859 (0x743)
  705: ADDR_ConfigurableTGP_VALUE = 1860 (0x744)
  707: ADDR_DynamicBoost_TotalProcessingPowerTarget_VALUE = 1861 (0x745)
  709: ADDR_DynamicBoost_MaxinumTGP_VALUE = 1862 (0x746)
  711: ADDR_L1_PWM_DEFAULT_MYFAN2 = 1929 (0x789)
  713: ADDR_L2_PWM_DEFAULT_MYFAN2 = 1930 (0x78A)
  715: ADDR_L3_PWM_DEFAULT_MYFAN2 = 1931 (0x78B)
  717: ADDR_L4_PWM_DEFAULT_MYFAN2 = 1932 (0x78C)
  719: ADDR_L5_PWM_DEFAULT_MYFAN2 = 1933 (0x78D)
  721: ADDR_BIOS_OEM_BYTE = 1870 (0x74E)
  723: ADDR_BIOS_OEM_BYTE2 = 1922 (0x782)
  725: ADDR_GAMING_PL1_DEFAULT_VALUE = 1840 (0x730)
  727: ADDR_GAMING_PL2_DEFAULT_VALUE = 1841 (0x731)
  729: ADDR_GAMING_PL4_DEFAULT_VALUE = 1842 (0x732)
  731: ADDR_GAMING_D_DEFAULT_VALUE = 1843 (0x733)
  733: ADDR_OFFICE_PL1_DEFAULT_VALUE = 1844 (0x734)
  735: ADDR_OFFICE_PL2_DEFAULT_VALUE = 1845 (0x735)
  737: ADDR_OFFICE_PL4_DEFAULT_VALUE = 1846 (0x736)
  739: ADDR_OFFICE_D_DEFAULT_VALUE = 1847 (0x737)
  741: ADDR_PL1_SETTING_VALUE = 1923 (0x783)
  743: ADDR_PL2_SETTING_VALUE = 1924 (0x784)
  745: ADDR_PL4_SETTING_VALUE = 1925 (0x785)
  747: ADDR_L1_PWM_DEFAULT_MYFAN3 = 1926 (0x786)
  749: ADDR_L2_PWM_DEFAULT_MYFAN3 = 1927 (0x787)
  751: ADDR_L3_PWM_DEFAULT_MYFAN3 = 1928 (0x788)
  753: ADDR_L4_PWM_DEFAULT_MYFAN3 = 1929 (0x789)
  755: ADDR_L5_PWM_DEFAULT_MYFAN3 = 1930 (0x78A)
  757: ADDR_SINGLEKBL_ENABLE = 1932 (0x78C)
  759: ADDR_SINGLEKBL_SUPPORTPOWER = 1934 (0x78E)
  761: ADDR_MYFAN3_CPU_TAU = 1848 (0x738)
  763: ADDR_TRIGGER_BYTE = 1895 (0x767)
  765: ADDR_STAUTS_BYTE = 1896 (0x768)
  767: ADDR_LIGHTBAR_CONTROL_BYTE = 1864 (0x748)
  769: ADDR_REDBAR_CONTROL_BYTE = 1865 (0x749)
  771: ADDR_GREENBAR_CONTROL_BYTE = 1866 (0x74A)
  773: ADDR_BLUEBAR_CONTROL_BYTE = 1867 (0x74B)
  775: ADDR_OEMSERVICE_PROJECT_ID_BYTE = 1868 (0x74C)
  777: ADDR_BATTERY_ALERT_BYTE = 1172 (0x494)
  779: ADDR_FAN_ALERT_BYTE = 1857 (0x741)
  781: ADDR_SILENTMODE_STATUS_BYTE = 1115 (0x45B)
  783: ADDR_MYFAN3_GPU_SETTING = 1931 (0x78B)
  785: ADDR_AP_OEM_BYTE2 = 1932 (0x78C)
  787: ADDR_AP_OEM_BYTE5 = 1989 (0x7C5)
  789: ADDR_AP_EC_LOGO = 1963 (0x7AB)
  791: ADDR_AP_EC_LOGO_R = 1897 (0x769)
  793: ADDR_AP_EC_LOGO_G = 1898 (0x76A)
  795: ADDR_AP_EC_LOGO_B = 1899 (0x76B)
  797: ADDR_AP_EC_LOGO_CONFIRM = 1932 (0x78C)
  799: ADDR_AP_OEM_SingleZone = 3328 (0xD00)
  801: ADDR_AP_OEM_BYTE6 = 1990 (0x7C6)
  803: ADDR_SUPPORT_BYTE6 = 1934 (0x78E)
  805: ADDR_MYFANI_MIN_SPEED = 1950 (0x79E)
  807: ADDR_MYFANI_MIN_TEMP = 1951 (0x79F)
  809: ADDR_MYFANI_EXTRA_SPEED = 1952 (0x7A0)
  811: ADDR_BIOS_OEM_BYTE3 = 1955 (0x7A3)
  813: ADDR_AP_BIOS_BYTE = 1956 (0x7A4)
  815: ADDR_AP_OEM_BYTE3 = 1957 (0x7A5)
  817: ADDR_AP_OEM_BYTE4 = 1958 (0x7A6)
  819: ADDR_BATTERYSAVER_PL1_DEFAULT_VALUE = 1959 (0x7A7)
  821: ADDR_BATTERYSAVER_PL2_DEFAULT_VALUE = 1960 (0x7A8)
  823: ADDR_BATTERYSAVER_PL4_DEFAULT_VALUE = 1961 (0x7A9)
  825: ADDR_BATTERYSAVER_D_DEFAULT_VALUE = 1962 (0x7AA)
  827: ADDR_MyFanCCI_Mode_Index = 1963 (0x7AB)
  829: ADDR_MyFanCCI_Mode_Profile1 = 1968 (0x7B0)
  831: ADDR_MyFanCCI_Mode_Profile2 = 1969 (0x7B1)
  833: ADDR_MyFanCCI_Mode_Profile3 = 1970 (0x7B2)
  835: ADDR_BATTERY_CHARGE_LIMIT_UP = 1977 (0x7B9)
  837: ADDR_AP_OEM_BYTE7 = 1991 (0x7C7)
  839: ADDR_AP_OEM_BYTE8 = 1992 (0x7C8)
  841: ADDR_AP_OEM_BYTE9 = 1830 (0x726)
  843: ADDR_AP_OEM_BYTE10_CUSTOM_LIGHT_TEST = 1831 (0x727)
  845: ADDR_AP_OEM_BYTEB = 1832 (0x728)
  847: ADDR_AP_MISCSWITCH2 = 3407 (0xD4F)
  849: ADDR_ESHUTTER_STATUS = 1831 (0x727)
  851: ADDR_CPU_DOUBLE_FLAG_SUPPORT = 1831 (0x727)
  853: ADDR_BIOS_OEM_BYTE8 = 1994 (0x7CA)
  855: ADDR_COMPLEX_POWER_STATUS = 1996 (0x7CC)
  857: ADDR_BATTERY_CHARGE_LIMIT_DOWN = 2000 (0x7D0)
  859: ADDR_ModuleID_GPU = 2002 (0x7D2)
  861: ADDR_GPU_STATUS = 1834 (0x72A)
  863: ADDR_ModuleID = 2003 (0x7D3)
  865: ADDR_GAMING_TCC_OFFSET_DEFAULT_VALUE = 2008 (0x7D8)
  867: ADDR_OFFICE_TCC_OFFSET_DEFAULT_VALUE = 2009 (0x7D9)
  869: ADDR_TURBO_TCC_OFFSET_DEFAULT_VALUE = 2010 (0x7DA)
  871: ADDR_LC_FAN_VALUE = 2022 (0x7E6)
  873: ADDR_LC_PUMP_VALUE = 2023 (0x7E7)
  875: ADDR_EC_DEFAULT_MODE = 2024 (0x7E8)
  877: ADDR_CHANGE_PORT_ID_WKD = 2043 (0x7FB)
  879: ADDR_SystemID = 1110 (0x456)
  881: ADDR_ROMID = 1905 (0x771)
  883: ADDR_ROMID2 = 1906 (0x772)
  993: OSD_FanModeSwitch = 176 (0xB0)
 1045: ecOEM_EC_VER = 1108 (0x454)
 1047: ecOEM_SUB_VER2 = 1149 (0x47D)
 1095: ADDR_RGBKB_LEVEL_R = 1897 (0x769)
 1097: ADDR_RGBKB_LEVEL_G = 1898 (0x76A)
 1099: ADDR_RGBKB_LEVEL_B = 1899 (0x76B)
 1101: ADDR_RGBKB_LEVEL_DEFAULT_R = 1900 (0x76C)
 1103: ADDR_RGBKB_LEVEL_DEFAULT_G = 1901 (0x76D)
 1105: ADDR_RGBKB_LEVEL_DEFAULT_B = 1902 (0x76E)
 1107: ADDR_RGBKB_MUSIC_NO = 1903 (0x76F)
 1111: ADDR_CPU_FAN_TABLE_TEMP_UP0 = 3840 (0xF00)
 1113: ADDR_CPU_FAN_TABLE_TEMP_DOWN0 = 3856 (0xF10)
 1115: ADDR_CPU_FAN_TABLE_DUTY0 = 3872 (0xF20)
 1117: ADDR_GPU_FAN_TABLE_TEMP_UP0 = 3888 (0xF30)
 1119: ADDR_GPU_FAN_TABLE_TEMP_DOWN0 = 3904 (0xF40)
 1121: ADDR_GPU_FAN_TABLE_DUTY0 = 3920 (0xF50)
 1125: ADDR_CPU_FAN_TABLE_TEMP_UP0 = 3840 (0xF00)
 1127: ADDR_CPU_FAN_TABLE_TEMP_DOWN0 = 3856 (0xF10)
 1129: ADDR_CPU_FAN_TABLE_DUTY0 = 3872 (0xF20)
 1131: ADDR_GPU_FAN_TABLE_TEMP_UP0 = 3888 (0xF30)
 1133: ADDR_GPU_FAN_TABLE_TEMP_DOWN0 = 3904 (0xF40)
 1135: ADDR_GPU_FAN_TABLE_DUTY0 = 3920 (0xF50)
 1137: ADDR_RAMFAN1P5_TABLE_STATUS1 = 3933 (0xF5D)
 1139: ADDR_RAMFAN1P5_TABLE_STATUS2 = 3934 (0xF5E)
 1141: ADDR_RAMFAN1P5_TABLE_CTRL = 3935 (0xF5F)
 1143: ADDR_TimAP_TccOffset_Setting = 1926 (0x786)
 1145: ADDR_TimAP_FanSwitchSpeedT100mSec = 1927 (0x787)
 1149: ADDR_TABLE_F1_CPU_TEMP_UP0 = 3840 (0xF00)
 1151: ADDR_TABLE_F1_CPU_TEMP_DOWN0 = 3856 (0xF10)
 1153: ADDR_TABLE_F1_DUTY0 = 3872 (0xF20)
 1155: ADDR_TABLE_F1_GPU_TEMP_UP0 = 3888 (0xF30)
 1157: ADDR_TABLE_F1_GPU_TEMP_DOWN0 = 3904 (0xF40)
 1159: ADDR_TABLE_F2_CPU_TEMP_UP0 = 3920 (0xF50)
 1161: ADDR_TABLE_F2_CPU_TEMP_DOWN0 = 3936 (0xF60)
 1163: ADDR_TABLE_F2_DUTY0 = 3952 (0xF70)
 1165: ADDR_TABLE_F2_GPU_TEMP_UP0 = 3968 (0xF80)
 1167: ADDR_TABLE_F2_GPU_TEMP_DOWN0 = 3984 (0xF90)
 1169: ADDR_TABLE_F3_CPU_TEMP_UP0 = 4000 (0xFA0)
 1171: ADDR_TABLE_F3_CPU_TEMP_DOWN0 = 4016 (0xFB0)
 1173: ADDR_TABLE_F3_DUTY0 = 4032 (0xFC0)
 1175: ADDR_TABLE_F3_GPU_TEMP_UP0 = 4048 (0xFD0)
 1177: ADDR_TABLE_F3_GPU_TEMP_DOWN0 = 4064 (0xFE0)
19230: CoolingModeECAddress = 1991 (0x7C7)
19675: CoolingModeECAddress = 1991 (0x7C7)
26769: PIXART_VID = 2362 (0x93A)
26771: PIXART_PID = 597 (0x255)
26773: PIXART_PID_DUAL_PAD = 628 (0x274)
27304: MonitorOn = 204 (0xCC)
```

Types holding the layout: `ECSpec` (`GCS:439`, 199 slots), `RamFan1_ECSpec` (`GCS:1109`),
`RamFan1p5_ECSpec` (`GCS:1123`), `RamFan2_ECSpec` (`GCS:1147`), `LiquidCooling`
(`GCS:19188`), `LiquidHWOC` (`GCS:19675`), `HIDKeyboard`/`ITE_SPEC`/`HIDRGBLightbar`
(`REG`), `SMAPCTABLE_STRUCT` (`GCS:7606`), `FanTable2Buffer` (`GCS:47577`),
`FanTable1p5Buffer` (`GCS:47628`).

Enums for generation/SKU (values in `PID`): `ProjectID` (`GCS:1253`),
`BIOS_PROJECT_ID` (`GCS:1301`), `GN20_GPU_SKU` (`GCS:1342`), `GN21_GPU_SKU` (`GCS:1355`).

## Appendix B - sibling per-model database (`builtin_models.json`)

29 models; `L<n>` = line of `"model_id"`. Per-model parameters (all `[E]`):

`project_id`, `bios_project_id`, `pl1_default`, `pl2_default`, `pl4_default`, `base_tgp`,
`max_db`, `max_fan_duty`, `temp_levels` (fan-table point count), `fan_control_respective`
(separate CPU/GPU curves), `has_water_cooler`, `keyboard_type`; plus `factory_specs` and
`presets` (office/balanced/turbo: `pl1`, `pl2`, `pl4`, `temp_offset`) for 17 of 29 models;
plus `fan_tables` (`office`/`balanced`/`turbo`; each `cpu_upt/cpu_downt/cpu_duty/gpu_upt/
gpu_downt/gpu_duty` arrays of 16 + `max_duty`).

| L | model_id | project_id | bios_project_id | base_tgp | max_db | temp_levels | fan_control_respective | has_water_cooler | keyboard_type |
|---|---|---|---|---|---|---|---|---|---|
| 4 | GM6IX9B | 25 | IDB | 150 | 25 | 10 | False | True | TypeA |
| 399 | X6AR55 | 26 | TRX1 | 140 | 25 | 6 | False | True | TypeA |
| 752 | XXAF55 | 26 | TRX1 | 140 | 75 | 6 | False | True | TypeA |
| 1105 | XxPTL | 26 | PTL | 115 | 25 | 11 | True | False | TypeA |
| 1458 | CANGLONG | 26 | TRX1 | 150 | 25 | 6 | False | False | TypeA |
| 1811 | JIAOLONG | 26 | TRX1 | 150 | 25 | 6 | False | False | SingleZone_RGB |
| 2164 | PH6TRX1 | 26 | TRX1 | 150 | 25 | 6 | False | True | TypeA |
| 2517 | PH6PG7x | 26 | PG7 | 140 | 25 | 11 | False | False | TypeA |
| 2870 | PH6PG3x | 26 | PG3 | 115 | 25 | 11 | False | False | TypeA |
| 3223 | PH6PG0x | 26 | PG0 | 100 | 25 | 11 | False | False | TypeB |
| 3576 | PH6ARxx | 26 | AR | 95 | 20 | 8 | False | False | TypeB |
| 3929 | PH4TRX1 | 24 | TRX1 | 105 | 25 | 6 | False | False | TypeA |
| 4282 | PH4PGx1 | 16 | PG1 | 140 | 25 | 7 | False | False | TypeA |
| 4635 | PH4AQE3 | 24 | AQE3 | 105 | 25 | 11 | False | False | TypeA |
| 5030 | PH4AQxx | 24 | AQ | 115 | 25 | 8 | False | False | TypeA |
| 5425 | PH4AUxf | 24 | AU | 115 | 25 | 8 | False | False | TypeA |
| 5820 | PH4AUxx | 24 | AU | 115 | 25 | 6 | False | False | TypeA |
| 6215 | PH4AXxx | 24 | AX | 140 | 25 | 8 | False | False | TypeA |
| 6610 | PH4PGx2 | 24 | PG2 | 150 | 25 | 7 | False | True | TypeA |
| 7005 | PH4PRxx | 24 | PR | 150 | 25 | 7 | False | True | TypeA |
| 7400 | PH4PUxx | 24 | PU | 150 | 25 | 6 | False | True | TypeA |
| 7795 | PH4TQx1 | 24 | TQ1 | 105 | 25 | 8 | False | False | TypeA |
| 8190 | PH4TUX1 | 24 | TU1 | 150 | 25 | 6 | False | True | TypeA |
| 8585 | PH6AQxx | 26 | AQ | 115 | 25 | 8 | False | False | TypeB |
| 8980 | PH6PG0x150W | 26 | PG0 | 115 | 25 | 11 | False | False | TypeB |
| 9375 | PH6PG3x150W | 26 | PG3 | 125 | 25 | 11 | False | False | TypeA |
| 9770 | PH6PG7x150W | 26 | PG7 | 140 | 25 | 11 | False | False | TypeA |
| 10165 | PH6PGEx | 26 | PGE | 150 | 25 | 11 | False | True | TypeA |
| 10560 | PH6PRxx | 26 | PR | 150 | 25 | 11 | False | True | TypeA |

29 rows. `max_fan_duty` is 100 for all 29; `pl1/pl2/pl4_default` and `factory_specs`/`presets`
are per model (see the file itself for the arrays).

## Appendix C - what the `.brfx` scripts are (and are not)

`singularity.brfx`, `cyber_lotus.brfx`, `cyber_marquee.brfx`, `kamehameha.brfx`
(`%TEMP%\opencode\openrevo-extract\brfx\`) are RGB **lighting-effect DSL** scripts
(`fn render_key(kx, ky, ctx)` returning `[r,g,b]`). They contain **no EC addresses** and no
EC sequences — verified by search for `EC`, `0x7`, `0xF`, `write`, `register`, `pump`, `fan`
across all four files. They are not an EC evidence source.
