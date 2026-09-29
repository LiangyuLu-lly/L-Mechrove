# GPU / display-route switching per generation (vendor evidence)

Read-only research over the decompiled vendor stack. Nothing was published on MQTT, nothing was
written to EC / NVRAM / registry / WMI, no service was touched and no GPU switching method was
called. Facts marked **B** come from a read-only byte scan (ASCII + UTF-16LE at both byte
alignments) of vendor binaries; nothing was executed.

## Evidence keys

| Key | Path (repo-relative; `_decompiled/`, `_extracted/`, `release/` are git-ignored) | Readability |
|---|---|---|
| `UC` | `_decompiled/gcuu-console/` - GamingCenterU 1.1.0.49 WPF console (GTX 10 / GTX 16 / RTX 20) | ConfuserEx: declarations + resources, bodies destroyed |
| `US` | `_decompiled/gcuu-service/` - GCUService 1.0.2.47 | declarations only |
| `S30` | `_decompiled/gcu30-41747/` - GCUService 1.0.2.70 (console 4.17.47.13) | declarations only |
| `S40` | `_decompiled/gcu40-51751-27/` - GCUService 1.0.2.70 (console 5.17.51.27) | **real method bodies** |
| `S40B` | `_decompiled/gcu40-51749/` - service for console 5.17.49.19 (cross-check) | declarations only (bodies = `throw new Exception("Runtime exception")`) |
| `S40A2` | `_decompiled/gcu40-51751/` - AiStone service for 5.17.51.34 (cross-check) | declarations only |
| `C50` | `_decompiled/ccuwinui/` - CCUWinUI 5.56.60.26 (`_extracted/ccu-msix/CCUWinUI.dll`) | **real method bodies** |
| `S50` | `release/GCU-only/AiStoneService/MyControlCenter/GCUService.exe` 1.2.0.0 (same size as `official-consoles/GCUService.exe`) | IL obfuscated: **B** only |
| `APP` | `src/MechrevoLiteWin/` | our code |

Marks: **P** proven from a readable method body or literal; **D** declaration only (member/const
exists, body unreadable); **B** binary string/identifier scan; **I** inferred (from another
generation or the family convention); **UNKNOWN**.

The 30 and 40 consoles are UWP .NET Native (`GamingCenter3_Cross.UWP_*.msixbundle`, no IL). The 30
console has symbol-level evidence (`.omo/evidence/g30-console-decompile.md`). **No 40 console is
decompiled anywhere**: every "40 console" statement below is really the service's accepted vocabulary.

## 1. Summary

| | 10/16/20 (GamingCenterU) | 30 | 40 three-mode (GCU-40-51751) | 40 two-mode (GCU-40-51749) | 50 |
|---|---|---|---|---|---|
| MUX dGPU-direct | absent | ON / OFF | ON / OFF / IGPU | ON / OFF (D) | DGPU Only / Hybrid / IGPU Only |
| Hybrid (Optimus) | always | = OFF | = OFF | = OFF | = Hybrid |
| iGPU-only through MUX | absent | absent | `TOGGLE_IGPU` | absent (D) | `TOGGLE_IGPU` |
| iGPU-only "RB" (hot) | absent | absent | accepted, **no hardware write** in this build | absent (D) | hot-swap card RB ON/OFF/AUTO; service behaviour UNKNOWN |
| Auto | absent | absent | `RB_AUTO` (registry only) | absent | `RB_AUTO`, judged by the console against AC/DC |
| `GPU_HOTSWAP_*` | absent | absent | absent | absent | enum + service literal (B); the console never sends it |
| Advanced Optimus / DDS | no evidence in any source | | | | |
| Write path | NVIDIA CPL preference through `NVControlSetting.dll` (not a route) | NVRAM `OemDisplayMode` (D/B) | NVRAM `OemDisplayMode` (P) | UNKNOWN | UNKNOWN (B: same member names as 40) |
| Reboot | none (I) | yes, manual: no RESTART action | `DGPU_DIRECT_CONNECT_RESTART` -> `shutdown /r /t 0` | RESTART not declared (D) | console sends RESTART 800 ms after the route message(s) |
| Capability gate | UNKNOWN | `OemDisplayMode != 255` (I) | `OemDisplayMode != 255`; iGPU-only WMI support == 85 | UNKNOWN | page: `IsNvGpu && DGpuDirectConnectionSupport`; buttons: `iGPUModeOnlySupport`, `GpuConfig` hot-swap keys, `APVersionCheck > 23` |

No readable build switches the display route through an EC register or an `\\.\ACPIDriver` IOCTL,
and none decides MUX support from the GPU SKU or PCI id (for the obfuscated S50 both are UNKNOWN).
C50 has no firmware-variable, EC, WMI-EC or IOCTL API at all (0 hits for
`FirmwareEnvironment`, `UEFI_Firmware`, `DeviceIoControl`, `ACPIDriver`, `AcpiTest_MULong`).

## 2. Shared mechanisms

### 2.1 Transport

- Broker GCUBridge on `localhost:13688`. The 50 console connects as clientId `UWPClient_5`,
  `cleanSession:false`, keepalive 1 s, with hard-coded credentials (values not reproduced)
  (`C50 DataService/MQTTDataService.cs:135-139,154`). Publish = JSON (`Json.StringifyAsync`), ASCII
  bytes, QoS 2, retain false (`MQTTDataService.cs:258,277`).
- Requests go to `Setting/Control`, replies come on `Setting/Status`; the dGPU power-saving extras
  report on `GPUDevice/Status` (`S40 MyControlCenter/Topic.cs:51,53,57`; `MySettingView.cs:7`;
  `GCService5/GPUDeviceItem.cs:321-328`).
- The service reads `val["Action"]` and dispatches on it (`S40 MyControlCenter/MySettingManager.cs:157-160`);
  other fields are read per action.
- Pacing (C50): `TopicSendToServerQueue.SendTopicToServer` calls `Task.Delay(800)` **without await**
  (`WinUIGallery.Helpers/TopicSendToServerQueue.cs:94`), so it does not pace; only
  `SendTopicToServerAsync` waits 800 ms (`:113`). The MUX restart dialog uses the sync form, so its
  route messages go out back-to-back and only its own `await Task.Delay(800)` precedes RESTART.

### 2.2 NVRAM `OemDisplayMode`

- Write: `NvramVariable.SetFwVars("OemDisplayMode", v)` sets the cached struct field
  (`S40 MyControlCenter/NvramVariable.cs:336-338`), marshals the **whole** `NVRAM_STRUCT` and calls
  `UEFI_Firmware.dll!WriteUefi(0, size, bytes)` (`:363`) only if `Monitor.TryEnter(NvramLock, 100)`
  succeeds (`:353`); otherwise the write is skipped silently, and exceptions are only logged. The
  caller sets the status string before the write (`MySettingManager.cs:1240-1267`), so read-back can
  report a target that never reached NVRAM.
- Read: `GetFwVars()` -> `ReadUefi(0, size)` (`NvramVariable.cs:129-160`, `:147`).
- Firmware variable (B, `UEFI_Firmware.dll` 1.0.0.1 in every 30/40 payload, 1.0.0.4-1.0.0.6 in the
  51751-34 / 57-51-60 payloads): `Get/SetFirmwareEnvironmentVariableW` with
  `SeSystemEnvironmentPrivilege`, name `UniWillVariable`, vendor GUID
  `{9f33f85c-13ca-4fd1-9c4a-96217722c593}`; the newer DLLs also contain `OemMagicVariable`. That
  `ReadUefi`/`WriteUefi` target `UniWillVariable` is I.
- Position: `OemDisplayMode` follows `ApUseFlag` (`S40 MyControlCenter/NVRAM_STRUCT.cs:137`,
  `S30 MyControlCenter/NVRAM_STRUCT.cs:133`). With default sequential marshalling (no `Pack`) it sits
  at byte offset 98 (0x62) in both layouts; the 30 struct lacks `APVersionCheck` (offset 67) but the
  next `uint` is 4-aligned, so the offset is the same. I (computed, not read on hardware).
- Encoding (P: write `S40 MySettingManager.cs:1229-1271`, read `:1593-1628`):

| Action | Handler arg | AMD | Intel | Meaning |
|---|---|---|---|---|
| `DGPU_DIRECT_CONNECT_TOGGLE_ON` | 1 | 1 | 2 | dGPU drives the panel (MUX direct) |
| `DGPU_DIRECT_CONNECT_TOGGLE_OFF` | 0 | 0 | 4 | hybrid: "MSHybrid" (AMD) / "Dynamic" (Intel) |
| `DGPU_DIRECT_CONNECT_TOGGLE_IGPU` | 2 | 2 | 1 | iGPU only |
| (read only) | - | 255 | 255 | not supported by BIOS -> `_Support = NotSupport` |

  AMD = host bridge PCI 00:00.0 vendor id 0x1022 (`S40 MyECIO/MyEcCtrl.cs:472-485`, cached at
  `MyControlCenter/CustomizeCtrl.cs:42`). The encodings conflict (1 = dGPU on AMD, iGPU on Intel), so
  no value may be written without knowing the platform. An unmapped read value keeps the default
  status `DGPU_DIRECT_CONNECT_TOGGLE_OFF` (`MySettingParams.cs:41`) while `_Support` says `Support` (I).
- The consoles never touch this variable (section 1; the hardware-API absence is also recorded in
  `.omo/drafts/gcu-30-50-model-adaptation.md:161-164`).

### 2.3 WMI `AcpiTest_MULong.GetSetULong`

Object `root\WMI` / `AcpiTest_MULong.InstanceName='ACPI\PNP0C14\1_1'`, method
`GetSetULong(Data: UInt64) -> Return` (`S40 MyControlCenter/WMIEC.cs:340-345`). Data layout (P, from
the callers): bits 0-15 EC address, bits 16-23 value, bits 40-47 command.

| Cmd | Data word | Use | Evidence (`S40 WMIEC.cs`) |
|---|---|---|---|
| 0 | `(value << 16) + addr` | EC RAM write | `:362-363` |
| 1 | `0x0000010000000000 + addr` | EC RAM read | `:342` |
| 3, n=0 | `0x0000030000000000` (3298534883328) | iGPU-only OFF | `:430` |
| 3, n=1 | `0x0000030000000001` | iGPU-only ON, only if NVHAD ready | `:407-414` |
| 3, n=2 | `0x0000030000000002` | read "dGPU status for iGPU-only" (85 = ready) | `:446` |
| 3, n=3 | `0x0000030000000003` | read iGPU-only support (85 = supported) | `:463` |
| 4 | `0x0000040000000000` | TjMax (not route) | `:480` |
| 5, n=1/2/3 | `0x0000050000000001..3` | CPU/GPU combine-run OFF / ON / support (not route) | `:497,514,531` |

NVHAD readiness: `select * from Win32_SoundDevice where name = 'NVIDIA High Definition Audio'`;
ready when there is no such device or its `StatusInfo == 3` (`WMIEC.cs:374-402`). What the BIOS WMI
method does for command 3 is UNKNOWN.

### 2.4 Things that look like GPU switching but are not

| Item | What it is | Evidence |
|---|---|---|
| `NV_CTRL_PANEL_AUTOSELECT` / `_HIGHPERFORMANCE`, status `DGpu`, registry `MySetting\DGpuPowerManagement` | NVIDIA Control Panel global preferred GPU via `NVControlSetting.dll!SetNVCtrlPanel(byte)` (`NvControlPanel {NV_CTRL_AUTOSELECT = 0, NV_CTRL_HIGHPERFORMANE = 1}`); the DLL loads `nvapi64.dll` through `nvapi_QueryInterface` (B). Active in GCU-U and 30 (D); vestigial in S40 (status only, no handler, no DllImport); C50 declares the enum and never sends it | `US MyControlCenter/NVControlSetting.cs:14-21`; `S40 MyControlCenter/NVControlSetting.cs:3-17`, `MySettingManager.cs:1345,1364-1367`; `C50 WinUIGallery.Models/SettingAciton.cs:19-20` |
| `m_DGpuMode`, `SetDGpuModeWithOverBoost`, `SetGPUdstateByGpuMode` | power-profile G level -> GPU D-state in EC `0x78B` (1931) bits 2:0 = 1 (G1/G2/Benchmark) or 2 (G3) | `S40 MyControlCenter.MyFan/MyFanManager_RamFan1p5.cs:2729-2751`; `MyControlCenter/MyFanManager_QC.cs:937-966` |
| `GPU_POWERSAVEINGMODE`, `GPU_DISCONNECTMONITOR {Enable}`, `GPU_DC_ONCE {Enable}` | "dGPU power saving": `Disable-PnpDevice` then `Enable-PnpDevice` on `*NVIDIA*` Display devices; the flags re-run it on display change / AC->DC. Present in 30, 40 and 50 (B) | `S40 MySettingManager.cs:305-308`; `GCService5/GPUDeviceItem.cs:29-31,341-362`; `_extracted/cc-51751-27/setup/{app}/UniwillService/MyControlCenter/Command/disableDGpu.ps1:1`, `enableDGpu.ps1:1` |
| EC `0x7D2` (2002) & 0x1F | GPU module / SKU id for TGP and OC tables | `S40 MyECIO/MyEcCtrl.cs:71-75`; `GCUService.MyFan.BTCooling/LiquidHWOC.cs:84` |
| EC `0x7C5` (1989) bit 5 | "monitor signal", read only by `SwitchIGPUafterDetectDisplayScanCode`, which has no caller | `S40 GCService5/GPUDeviceItem.cs:227-262` |
| `PHxGPURule1 = 203`, `TimAP_DISPLAY_In = 209` | WMI event scan codes, not EC addresses; 203 unhandled, 209 is an empty case | `S40 Define/ECSpec.cs:567,575`; `MyControlCenter/WMIEC.cs:290` |
| `ADDR_GPU_STATUS` (1834 / 0x72A) | declared in S50 (B), absent from S40; usage UNKNOWN | - |

## 3. Per generation

### 3.1 GTX 10 / GTX 16 / RTX 20 - GamingCenterU

- **Modes**: no display-route switch of any kind. The only GPU "mode" is the Settings tile
  "Discrete GPU" = NVIDIA Control Panel preferred GPU. Strings: "Discrete GPU" /
  "Discrete GPU is a first priority for your use after enabling it. Select "On" to keep the laptop
  in high performance mode." (`UC GamingCenterU.Resources.en-us.resx:294,41`); zh-cn
  "独立显卡" / "启用时，设定显卡电源管理模式在最大效能" (`GamingCenterU.Resources.zh-cn.resx:285,41`).
- **Console**: view model `UWP_Refactor.ViewModels/SettingViewModel.cs:790` `GpuIsNvidia`, `:806`
  `GpuGridSupport`, `:818` `DGPUSwitch` (bodies destroyed); DTO `UWP_Refactor.Models/MySetting.cs:11`
  `DGpu`; topics `GamingCenterU/MainWindow.cs:366`, `ControlCenter_COML.Views_Coml/SettingView.cs:76,78`.
  B: the console's identifier heap holds enum members `NV_CTRL_PANEL_HIGHPERFORMANCE` and
  `NV_CTRL_PANEL_AUTOSELECT`, and no `DGPU_DIRECT`, `IGPU_ONLY`, `OemDisplayMode` or `HOTSWAP`
  (`_extracted/GamingCenterU/setup/{app}/Core/GamingCenterU.exe`).
- **Wire** (I, bodies destroyed): `Setting/Control {"Action":"NV_CTRL_PANEL_HIGHPERFORMANCE"}` for On,
  `{"Action":"NV_CTRL_PANEL_AUTOSELECT"}` for Off.
- **Service** (D): consts `Define/ServCMD.cs:242,244`; `Define/FastSwitch.cs:19,21`
  (`dGPUOn = 1`, `dGPUOff = 0`); `MyControlCenter/MySettingManager.cs:158` `UserSetNVCtrlPanel(int)`,
  `:346` `SetNVCtrlPanel_AutoSelect`, `:351` `SetNVCtrlPanel_HighPerformance`, `:403`
  `SetNVCtrlPanelREG`; P/Invoke `MyControlCenter/NVControlSetting.cs:14-21`,
  `MyControlCenter/PowerOptionAPI.cs:139-140`. B: literals `NV_CTRL_PANEL_*`, `DGpuPowerManagement`,
  `\OEM\GamingCenter2\MySetting`, `AcpiTest_MULong`; **absent**: `OemDisplayMode`, `DGPU_DIRECT_*`,
  `IGPU_ONLY_*`, `shutdown`, `NVIDIA High Definition Audio`, and any `\OEM\GamingCenter2\ItemSupport`
  path. The installer ships no `UEFI_Firmware.dll`; `Define/ECSpec.cs` has no route register.
- **Write** (I from the identical 30/40 members): registry `HKLM\SOFTWARE\OEM\GamingCenter2\MySetting\DGpuPowerManagement`
  0/1 plus `SetNVCtrlPanel(0/1)`. The NVAPI DRS setting id is UNKNOWN (native DLL).
- **Reboot**: none (I: driver profile setting, no restart code).
- **Read-back** (I from S40 `MySettingManager.cs:857,1364-1367`): `Setting/Status.DGpu` =
  `NV_CTRL_PANEL_AUTOSELECT` / `NV_CTRL_PANEL_HIGHPERFORMANCE` (key `US Define/ClientCMD.cs:134`,
  field `MyControlCenter/MySettingParams.cs:11`).
- **Capability**: UNKNOWN (the console name `GpuIsNvidia` suggests an NVIDIA presence gate).

### 3.2 RTX 30 - console 4.17.47.13, GCUService 1.0.2.70

- **Modes**: dGPU-direct ON / OFF (hybrid). No iGPU-only, no auto, no restart, no hot swap
  (`.omo/evidence/g30-console-decompile.md:64,80-85`).
- **Console** (symbol level): topic `Setting/Control`, actions `DGPU_DIRECT_CONNECT_TOGGLE_ON` /
  `_OFF`, handler `DgpuSwitchView.Toggle_PointerPressed`, view model
  `DGpuDirectConnectionSwitch/_Support`, `strDGpuDirectConnectionInfo_AMD/_Intel`,
  `strDGpuDirectConnectionWarning`; read-back `DiscreteGpuDirectConnectionSwitch_Status/_Support` +
  `GETSTATUS`; `SetToWMIEC` absent (`g30-console-decompile.md:61-70`). The same string keys in the 5.56
  table read "Disable NVIDIA Optimus" / "“On” is dGPU only. “Off” is MSHybrid setting. Need to reboot
  device to enable the Display mode." (AMD) / "… “Off” is Dynamic setting …" (Intel)
  (`_extracted/ccu-msix/Strings_Json/en-us.json:101-103`); that the 30 console shows the same text is I.
- **Wire** (I, field name by family convention): `Setting/Control {"Action":"DGPU_DIRECT_CONNECT_TOGGLE_ON"}` /
  `{"Action":"DGPU_DIRECT_CONNECT_TOGGLE_OFF"}`.
- **Service** (D): `Define/ServCMD.cs:352,354` (ON/OFF only); `MyControlCenter/MySettingParams.cs:39,41`;
  `MyControlCenter/MySettingManager.cs:248` `UserSetDGgpuDirectConnectionSwitch(int)`;
  `MyControlCenter/NvramVariable.cs:91-101` (UEFI_Firmware imports). B: the `SetFwVars` case-label
  literals include `OemDisplayMode`; the ItemSupport literal list includes `DGpuDirectConnectionSupport`;
  the default-status literal run matches S40 `MySettingParams` (`… NotSupport, ACRECOVERY_TOGGLE_OFF,
  FN_WITH1_HOTKEY_TOGGLE_ON, DGPU_DIRECT_CONNECT_TOGGLE_OFF …`); **absent**: `_TOGGLE_IGPU`,
  `_RESTART`, `IGPU_ONLY_*`, `shutdown`, `NVIDIA High Definition Audio`.
- **Write** (I): NVRAM `OemDisplayMode` in `UniWillVariable`, AMD ON 1 / OFF 0, Intel ON 2 / OFF 4 (values
  from S40; the 30 body is destroyed).
- **Reboot**: required (UI text); the service has no RESTART action and no restart code, so the user
  reboots. Whether the native console can reboot by itself is UNKNOWN.
- **Read-back** (I): `Setting/Status.DiscreteGpuDirectConnectionSwitch_Status` = `..._TOGGLE_ON` / `_OFF`,
  `_Support` = `Support` / `NotSupport`.
- **Capability** (I): `OemDisplayMode != 255` -> `_Support = Support` and
  `ItemSupport\DGpuDirectConnectionSupport = 1`.
- The NV CPL preferred-GPU members still exist (`MySettingManager.cs:182,399,404,429`).

### 3.3 RTX 40 - two service tiers

| Tier | Payload / console | Vocabulary | Evidence |
|---|---|---|---|
| three-mode | GCU-40-51751: 5.17.51.27 (Uniwill), 5.17.51.34 (AiStone) | TOGGLE_ON/OFF/IGPU, RESTART, RB_ON/OFF/AUTO, IGPU_ONLY_CHECK_DGPU_SUPPORT | `S40 Define/ServCMD.cs:356-370`; `S40A2 Define/ServCMD.cs:357-369` |
| two-mode | GCU-40-51749: 5.17.49.19 | TOGGLE_ON/OFF only; no RESTART, no TOGGLE_IGPU, no iGPU-only members, no `RequestSystemRestartAsync`, no `system` import | `S40B Define/ServCMD.cs:353,355`; `MyControlCenter/MySettingManager.cs:260-263`; 0 hits for the rest |

The two-mode tier's bodies are stripped, so everything below is the three-mode build (`S40`, P).

**Accepted actions** (`MySettingManager.cs`):

| Wire (`Setting/Control`) | Handler | Effect |
|---|---|---|
| `{"Action":"DGPU_DIRECT_CONNECT_TOGGLE_ON"}` | `:663-668` | `UserSetDGgpuDirectConnectionSwitch(1)` -> AMD 1 / Intel 2; also clears the `GPU_DISCONNECTMONITOR` and `GPU_DC_ONCE` flags |
| `{"Action":"DGPU_DIRECT_CONNECT_TOGGLE_OFF"}` | `:774-777` | (0) -> AMD 0 / Intel 4 |
| `{"Action":"DGPU_DIRECT_CONNECT_TOGGLE_IGPU"}` | `:780-785` | (2) -> AMD 2 / Intel 1; clears the same two flags |
| `{"Action":"DGPU_DIRECT_CONNECT_RESTART"}` | `:492-495` | `system("shutdown /r /t 0 /c \"DGPU Direct Connect Toggle Switch\"")` (`:1326-1335`), immediate and unconditional |
| `{"Action":"IGPU_ONLY_CONNECT_RB_ON","SetToWMIEC":…}` | `:623-627` | `UserSetIGgpuONLYConnectionSwitch(1, …)` + `UpdateIGPUAUTOSupport(0)` |
| `{"Action":"IGPU_ONLY_CONNECT_RB_OFF","SetToWMIEC":…}` | `:645-649` | (0, …) + `UpdateIGPUAUTOSupport(0)` |
| `{"Action":"IGPU_ONLY_CONNECT_RB_AUTO"}` | `:582-586` | (2, "") + `UpdateIGPUAUTOSupport(1)` |
| `{"Action":"IGPU_ONLY_CHECK_DGPU_SUPPORT"}` | `:684-687` | publishes `{"CheckDGpuStatusforIGpuOnlySwitch": <byte>}` (`:826-847`) |
| `{"Action":"GETSTATUS"}` | `:271-274` | full `Setting/Status` frame (`:848-898`) + `GPUDevice/Status` |

- **MUX write** (`:1229-1271`): no-op when `_Support == "NotSupport"`; sets the status string, then
  writes NVRAM (section 2.2). No status is pushed; the client must send `GETSTATUS`. The status is
  the NVRAM **target**, not the live route, until the reboot.
- **iGPU-only RB** (`:1281-1322`): if ON and NVHAD not ready -> return with no field change; reads WMI
  cmd 3 / n=2; if `IGpuOnlyConnectionSwitch_Support == "NotSupport"` or the byte != 85 -> sets
  `sCheckDGpuStatusforIGpuOnlySwitch = "170"`; else sets `IGpuOnlyConnectionSwitch_Status`, registry
  `HKLM\SOFTWARE\OEM\GamingCenter2\MySetting\IGPUonly` = 1 / 0 / 2 and `"85"`, then calls
  `InitIGPUSetting()`, which is **empty** (`:83-86`). `writeToWMIEC` is unused, `UserSetIGgpuONLY_ON/OFF`
  are empty (`:1273-1280`), and the only callers of `WMIWriteECIGPUonlyON/OFF` are
  `_ManagerTimersTimer_Elapsed_IGPUonlyON/OFF` (`:87-116`), which nothing subscribes. **In this build
  RB_* never touches hardware**, and the `"85"/"170"` string is never published (it is not part of
  `UpdateStatusToClient`). This matches our own field note at `APP Hardware/MechrevoService.cs:1285-1289`.
- `SetToWMIEC` is mandatory for RB_ON / RB_OFF here: a missing field makes
  `val["SetToWMIEC"].ToString()` throw inside the `async void` handler (I). RB_AUTO does not read it.
- **Init** (`:1593-1628`): `OemDisplayMode == 255` -> `_Support = NotSupport`, else `Support` and the
  status from the value. `InitIGPUonlySupport` (`:1761-1771`): WMI cmd 3 / n=3 == 85 -> `Enable`, else
  `NotSupport`. `LoadRegistry` (`:1345-1350`) restores `IGPUonly`, `IGPUautoSupport`, `DGpuPowerManagement`.
- **Reboot**: required for every MUX change; only RESTART reboots.
- **Capability**: `ItemSupport\DGpuDirectConnectionSupport = (OemDisplayMode != 255)`
  (`CustomizeCtrl.cs:390,449`); `IsAMDPlatform` (`:451`); `IsNvGpu` = a `Win32_VideoController`
  caption contains "NVIDIA" (`:452`; `Utility/UtilityExtensions.cs:59-66`;
  `MyControlCenter/HardwareInfoCollect.cs:171-184`); `APVersionCheck` from NVRAM (`:469`;
  `NVRAM_STRUCT.cs:98`). This build never writes `iGPUModeOnlySupport` or the `GpuConfig` hot-swap keys.
- No handler exists for `IGPUONLYCONNECTIONSWITCH_STATUS`, `IGPU_CANNOT_BE_SWITCH_NOW_VISIBILITY` or
  `GPU_HOTSWAP_*`, and the fields `CheckDGpuStatusforIGpuOnlyOnSuccess` / `IGpuCannotBeSwitchNowVisibility`
  are never produced (case-sensitive search: 0 hits in S40). A 50 console talking to this service
  therefore times out on every hot-swap request and rolls back.

### 3.4 RTX 50 - CCUWinUI 5.56.60.26, service 1.2.0.0

**Gate and layout** (P, `C50`):
- GPU page visible iff `ItemSupport\IsNvGpu == 1 && ItemSupport\DGpuDirectConnectionSupport == 1`
  (`CCUWinUI.ViewModels/NavigationPageViewModel.cs:976-980`). A machine without MUX has no GPU page, so
  no hot-swap card either.
- `RefreshItemSupport` (`WinUIGallery.ViewModels/GpuSettingPageViewModel.cs:1412-1466`):
  `APVersionCheck` (`:1417`), `IsNvGpu` (`:1423`), `iGPUModeOnlySupport` with default **1** when the key
  is missing (`:1429`), `MySetting\GpuConfig\GpuHotSwapSwitchSupport` default 0 (`:1435`),
  `MySetting\GpuConfig\lgpuHotSwapSwitchStatus` (lower-case L) read only if NVIDIA and supported
  (`:1440`); `IsIGPUHotSwap` = status only when `APVersionCheck > 23` (`:1443`), forced off when
  `APVersionCheck == 23` or not NVIDIA (`:1447`) or iGPU mode unsupported (`:1455`);
  `customizeTarget == 49` disables iGPU mode (`:1451`); `customizeTarget == 9` sets the `IsDgpuOnly`
  label from a status containing "OFF" (`:1358`).
- Buttons: IGPU hidden if `!IGPUModeSupport || IsIGPUHotSwap` (`:1091`), Hybrid hidden if
  `IsIGPUHotSwap` (`:1115`), hot-swap card shown iff `IGPUModeSupport && IsIGPUHotSwap` (`:1127`). So
  hot-swap machines get "DGPU Only" (MUX) + the hot-swap card; others get DGPU Only / Hybrid /
  IGPU Only (all MUX).

**MUX modes (reboot)** - clicking DGPU Only / Hybrid / IGPU Only (`:186-275`) only changes the local
value and raises `RestartInquire` when the previous value is neither -1 nor the target. Nothing is sent
until the dialog "Please reboot for the setting to take effect." (`WinUIGallery.Pages/GpuSettingPage.cs:1515-1532`;
text `_extracted/ccu-msix/Strings_Json/en-us.json:516`). Restart (`:1534-1585`):

```
DGPU Only (1):  {"Action":"DGPU_DIRECT_CONNECT_TOGGLE_ON"}
                {"Action":"IGPU_ONLY_CONNECT_RB_OFF","SetToWMIEC":"OK"}
                {"Action":"DGPU_DIRECT_CONNECT_TOGGLE_ON"}              (:1543-1559)
Hybrid (0):     {"Action":"DGPU_DIRECT_CONNECT_TOGGLE_OFF"}             (:1560-1566)
IGPU Only (2):  {"Action":"DGPU_DIRECT_CONNECT_TOGGLE_IGPU"}            (:1567-1573)
then            await Task.Delay(800)                                   (:1575)
                {"Action":"DGPU_DIRECT_CONNECT_RESTART"}                (:1577-1580)
```

Cancel reverts the local value and sends nothing (`:1586-1596`). `DGPU_DIRECT_CONNECT_RESTART` is a
generic "reboot now": the Deep Sleep dialogs send it too (`WinUIGallery.ViewModels/PowerManageViewModel.cs:1890,1940`).

**Hot-swap card (no reboot)** (`GpuSettingPageViewModel.cs:276-479`):

```
RB_ON   {"Action":"IGPU_ONLY_CONNECT_RB_ON","SetToWMIEC":"OK"}   success: CheckDGpuStatusforIGpuOnlyOnSuccess == 2   (:293-300)
RB_OFF  {"Action":"IGPU_ONLY_CONNECT_RB_OFF","SetToWMIEC":"OK"}  success: == 1                                   (:357-364)
RB_AUTO {"Action":"IGPU_ONLY_CONNECT_RB_AUTO"}                   success: IsAC ? == 1 : == 2                        (:421-440)
loop    Task.Delay(2000); check; break when count > 60; resend when count % 4 == 0; count++
        -> 62 checks (~124 s) plus 16 resends that each await 800 ms
timeout IGpuCannotBeSwitchNowVisibility = true
        {"Action":"IGPUONLYCONNECTIONSWITCH_STATUS","Status":<previous 0|1|2>}   (:325-326, :389-390, :465-466)
```

`IsAC` comes from `Fan/Status` (`:1334`). The hot-swap toggle itself only logs (`:169-180`);
`GPU_HOTSWAP_ON/OFF` exist in the enum (`WinUIGallery.Models/SettingAciton.cs:61-62`) with no send site.
Advanced Optimus has no trace in C50 or its string table.

**Read-back** (`:1286-1405`): `GETSTATUS` on page activation (`:1292`);
`DiscreteGpuDirectConnectionSwitch_Status` -> -1, then 0 / 1 / 2 (`:1345-1357`);
`IGpuOnlyConnectionSwitch_Status` -> 0 / 1 / 2 (`:1367-1381`); `IGpuCannotBeSwitchNowVisibility`
warning in hybrid unless the value contains "false" (`:1385`); `CheckDGpuStatusforIGpuOnlyOnSuccess`
-> `Contains("1")` 1, `Contains("2")` 2, else 0 (`:1390`); first load in hybrid sends
`{"Action":"IGPU_CANNOT_BE_SWITCH_NOW_VISIBILITY"}` once (`:1392-1399`). `_Support` fields are
declared in the DTO (`WinUIGallery.Models/MySetting.cs:73-85,101`) but not used for gating.

**Service 1.2.0.0** (B only; behaviour UNKNOWN):
- literals: every `DGPU_DIRECT_CONNECT_*`, `IGPU_ONLY_CONNECT_RB_*`, `IGPUONLYCONNECTIONSWITCH_STATUS`,
  `IGPU_CANNOT_BE_SWITCH_NOW_VISIBILITY`, `GPU_HOTSWAP_ON/OFF`, `shutdown /r /t 0`, the
  `Disable-PnpDevice … *NVIDIA* -Class Display` command, topics `IGPUonly/Support` and
  `IGPUonly/DetectDGPUStatus` (no C50 counterpart);
- identifiers: `UserSetDGgpuDirectConnectionSwitch`, `UserSetIGgpuONLYConnectionSwitch(…, writeToWMIEC)`,
  `UserSetIGgpuONLY_ON/OFF`, `UserSetGpuHotSwap(isOn)`, `SetNVServiceOnOff(turnON)`, `RestartService`,
  `RestartSingleService(serviceName)`, `RequestSystemRestartAsync`, `WMICheckNVHADstatus`,
  `WMIWriteECIGPUonlyON/OFF`, `WMIGetECDGPUStatusForIGPUonly`, `WMIGetECIGPUonlySupport`,
  `NVRAM_STRUCT.OemDisplayMode`, `ADDR_GPU_STATUS`;
- status payloads: single-field frames `{CheckDGpuStatusforIGpuOnlySwitch}`,
  `{CheckDGpuStatusforIGpuOnlyOnSuccess}`, `{IGpuCannotBeSwitchNowVisibility}`,
  `{IGpuOnlyConnectionSwitch_Status}` and a 53-field frame containing `DGpu`,
  `DiscreteGpuDirectConnectionSwitch_Status/_Support`, `IGpuOnlyConnectionSwitch_Support`;
- not present as plain literals in any scanned payload: `iGPUModeOnlySupport`, `GpuHotSwapSwitchSupport`,
  `lgpuHotSwapSwitchStatus`, `AcpiTest_MULong`, so the writer of those keys is UNKNOWN.

I: the 50 service keeps the 40 family (NVRAM `OemDisplayMode` + reboot for MUX, WMI command 3 for the
hot iGPU-only switch) and adds a hot-swap path that restarts NVIDIA services.

## 4. `Setting/Status` fields

| Field | Producer | Values | Note |
|---|---|---|---|
| `DGpu` | GCU-U / 30 (D/B), S40 `:857` | `NV_CTRL_PANEL_AUTOSELECT` / `NV_CTRL_PANEL_HIGHPERFORMANCE` | CPL preference, not a route |
| `DiscreteGpuDirectConnectionSwitch_Status` | 30 (B), S40 `:887`, S50 (B) | `DGPU_DIRECT_CONNECT_TOGGLE_ON` / `_OFF` (default) / `_IGPU` | NVRAM target; set before the write |
| `DiscreteGpuDirectConnectionSwitch_Support` | S40 `:888` | `Support` / `NotSupport` (default) | 255 -> NotSupport |
| `IGpuOnlyConnectionSwitch_Status` | S40 `:889`, S50 (B) | `IGPU_ONLY_CONNECT_RB_ON` / `_OFF` (default) / `_AUTO` | S40: from registry `IGPUonly` |
| `IGpuOnlyConnectionSwitch_Support` | S40 `:890` | `Enable` / `NotSupport` | "Enable", not "Support" |
| `IGpuAutoSupportSwitch` | S40 `:891` | `IGPU_ONLY_CHECK_IGPU_AUTO_DETECT_ON` / `_OFF` / "" | ON after RB_AUTO |
| `CheckDGpuStatusforIGpuOnlySwitch` | S40 `:842` (own frame, only after `IGPU_ONLY_CHECK_DGPU_SUPPORT`) | JSON number: 170 = NVHAD not ready, else the WMI byte (85 = ready) | readiness probe, not a switch result |
| `CheckDGpuStatusforIGpuOnlyOnSuccess` | S50 (B) | string with "1" (dGPU on) / "2" (dGPU off) (I) | C50 success criterion |
| `IGpuCannotBeSwitchNowVisibility` | S50 (B) | "true" / "false" (I) | reply to `IGPU_CANNOT_BE_SWITCH_NOW_VISIBILITY` (I) |
| `GPUDevice/Status` topic | S40 `GPUDeviceItem.cs:59-62,321-328` | `currentSaveingMode, DisconnectMonitor, DC_Once, DC_HZ, MODE_HZ, Auto, currentHZ, currentHZList, AutoEnable` | power-saving extras |

## 5. Capability keys (`HKLM\SOFTWARE\OEM\GamingCenter2\…`)

| Key | Writer | Semantics | Readers |
|---|---|---|---|
| `ItemSupport\DGpuDirectConnectionSupport` | S40 `CustomizeCtrl.cs:390,449`; 30 (B) | 1 iff `OemDisplayMode != 255` | C50 `NavigationPageViewModel.cs:977`; APP `Hardware/MechrevoDeviceCapabilities.cs:335` |
| `ItemSupport\IsNvGpu` | S40 `CustomizeCtrl.cs:452` | `Win32_VideoController` caption contains "NVIDIA" | C50 `:976`, `GpuSettingPageViewModel.cs:1423` |
| `ItemSupport\IsAMDPlatform` | S40 `CustomizeCtrl.cs:451` | PCI 00:00.0 vendor 0x1022 | not used by the C50 GPU page |
| `ItemSupport\APVersionCheck` | S40 `CustomizeCtrl.cs:469` (NVRAM byte) | number | C50 `:1417,1443,1447` (hot swap needs > 23) |
| `ItemSupport\iGPUModeOnlySupport` | UNKNOWN | missing = 1 in C50 | C50 `:1429`; APP `MechrevoDeviceCapabilities.cs:311` (missing = 0) |
| `MySetting\GpuConfig\GpuHotSwapSwitchSupport` | UNKNOWN | 1 = supported | C50 `:1435`; APP `:366` |
| `MySetting\GpuConfig\lgpuHotSwapSwitchStatus` | UNKNOWN | > 0 = on | C50 `:1440`; APP `:366` |
| `MySetting\IGPUonly` | S40 `MySettingManager.cs:1305,1310,1315` | RB 1 / 0 / 2 | S40 `:1349` |
| `MySetting\IGPUautoSupport` | S40 `:816,821` | 1 after RB_AUTO | S40 `:1350` |
| `MySetting\DGpuPowerManagement` | GCU-U / 30 (D), S40 default 0 | CPL preference 0 / 1 | S40 `:1345` |

GamingCenterU machines have no `ItemSupport` key at all (B), so every key above is absent there.

## 6. Our app against the vendor

| # | Our behaviour | Vendor | Verdict |
|---|---|---|---|
| 1 | `CreateGpuModePayload`: RB_ON / RB_OFF with `SetToWMIEC=OK`, RB_AUTO without (`APP Hardware/MechrevoService.cs:1098-1105`) | C50 `:293-295,357-359,421`; S40 requires the field for ON/OFF | match |
| 2 | Restart route dGPU = TOGGLE_ON, RB_OFF, TOGGLE_ON; hybrid = TOGGLE_OFF; iGPU = TOGGLE_IGPU; RESTART after 800 ms (`MechrevoService.cs:1182-1250`) | C50 `GpuSettingPage.cs:1543-1580` | match: both send the route messages back-to-back, then wait 800 ms before RESTART |
| 3 | Auto restart route = TOGGLE_OFF + RB_AUTO + RESTART (`:1240-1245`) | the vendor never sends RB_AUTO before a reboot; on S40 RB_AUTO only writes the registry | our invention (reachable on 50 only: the 40 rows omit RB_AUTO, so `CanSwitchGpuMode(GpuAuto)` is false there); what the 50 service does with RB_AUTO before a reboot is UNKNOWN |
| 4 | Without dGPU-direct support the restart route is RB_OFF / RB_ON (+ RESTART) (`:1234-1239`) | the vendor never reboots after RB, and shows no GPU page at all without MUX; S40 RB is registry-only | on a machine that reports iGPU-only support but no MUX this is a reboot that applies nothing, the same class the empty-route guard (`:1187-1194`) prevents |
| 5 | Timeout rollback `IGPUONLYCONNECTIONSWITCH_STATUS` 0/1/2 (`:1474-1493`; `Gpu/IgpuOnlySemantics.cs:40`) | C50 `:325-326`; S40 has no handler | match on 50, no-op on 40 |
| 6 | `PollLimit = 61` (`Gpu/IgpuOnlySemantics.cs:15`) | 62 checks (break when count > 60 after the check) | 2 s difference, harmless |
| 7 | `CheckDGpuStatusforIGpuOnlySwitch` 85 -> success(2), 170 -> 1 (`Hardware/MechrevoHw.cs:2149-2157`, comment `:2127-2128`) | S40 publishes it only after `IGPU_ONLY_CHECK_DGPU_SUPPORT` (which we never send) as a readiness byte; the 85/170 it assigns elsewhere is never published | mislabelled; dead on 40 |
| 8 | 40 two-mode row allows `TOGGLE_IGPU` and `RESTART`, sourced to the three-mode service (`Gpu/DisplayRouteMatrix.cs:156-181`) | S40B declares neither, and no iGPU-only status field (`S40B MyControlCenter/MySettingParams.cs:44,46` are its only route fields) | this row is what a 5.17.49.19-tier machine gets (no iGPU-only field -> `ThreeMode = false`, `APP Hardware/MechrevoHw.cs:495,500,510`), so iGPU is offered and every restart route ends in a RESTART the service does not declare: likely no reboot while we report `Requested` (D, needs hardware) |
| 9 | 30 has no switch at all: `CanOfferGpuModeSwitch` requires Gen40/Gen50 (`Hardware/MechrevoHw.cs:529-534`) | the 30 console has a dGPU-direct page (`g30-console-decompile.md:64-66`) and the service has the NVRAM path (D/B) | parity gap; the in-code reason "no switch page on 30" conflicts with the g30 evidence |
| 10 | Hot swap = `GpuHotSwapSwitchSupport && lgpuHotSwapSwitchStatus` and iGPU-only support (`MechrevoDeviceCapabilities.cs:366`, `MechrevoHw.cs:496`) | C50 also needs `APVersionCheck > 23`, NVIDIA, not target 49 | deliberate (`Hardware/HardcodeDispositions.cs:46-49`); can offer hot swap where the vendor would not |
| 11 | `iGPUModeOnlySupport` missing = false | C50 missing = true | deliberate fail-closed |
| 12 | We parse `IGpuCannotBeSwitchNowVisibility` (`MechrevoHw.cs:2160-2163`) but never request it | C50 requests it once in hybrid | `IgpuSwitchBlocked` may stay null |
| 13 | Generation from the display-class registry (`Gpu/GpuGenerationProvider.cs:84-117`) | vendor `IsNvGpu` uses live `Win32_VideoController` | ours likely survives an iGPU-only boot; the vendor page may disappear (UNKNOWN, needs hardware) |

10/16/20 machines resolve to `DgpuGenerationKind.Unknown` (`Gpu/DgpuGeneration.cs:114-128`), so no
route action is offered; that matches the vendor, which has none. We read `DGpu` without sending
`NV_CTRL_PANEL_*` (`Hardware/MechrevoHw.cs:323-326`), which matches 40/50.

## 7. Corrections to existing notes (not edited here)

- `docs/hardware/model-support-matrices.md:79` and `Gpu/DisplayRouteMatrix.cs:137-140` cite a 40 iGPU-only
  WMI write path as PROVEN; in S40 those writes are dead code and RB_* only writes the registry.
- `Gpu/DisplayRouteMatrix.cs:133-135,160-162` and `model-support-matrices.md:85-86` call the 40
  console-side protocol "per-method decompiled"; the 40 consoles are .NET Native and the cited files
  are service files.
- `.omo/drafts/gcu-30-50-model-adaptation.md:143`: the WMI words are `0x30000000000 + n`
  (3 << 40 | n), not `0x300000001/0/2/3`. The draft's "40B" is a `%TEMP%` decompile that is not in the
  repo; the repo's two-mode service (S40B) has stripped bodies, so its encodings cannot be re-checked here.
- `docs/ec-per-generation.md:162` (row 9): `m_DGpuMode` / `SetDGpuModeWithOverBoost` /
  `SetGPUdstateByGpuMode` are the GPU D-state of the power profile (EC 0x78B), `0xCB PHxGPURule1` is
  WMI scan code 203, and the "40 Direct vs 50 Door-assisted" NVRAM claim was already retracted in the draft.
- `.omo/drafts/gcu-30-50-model-adaptation.md:166` ("send queue paced 800 ms"): only the async send path waits.

## 8. UNKNOWN / needs hardware

1. S50 behaviour for every action (IL obfuscated): whether RB_* issues WMI command 3, what
   `UserSetGpuHotSwap` / `SetNVServiceOnOff` do, the `IGPUONLYCONNECTIONSWITCH_STATUS` handler, and the
   `IGPUonly/*` topics.
2. The writer of `iGPUModeOnlySupport` and the two `GpuConfig` hot-swap keys.
3. The 30 and 40 two-mode handler bodies (values written, reboot behaviour); on a 5.17.49.19-tier
   machine, whether `DGPU_DIRECT_CONNECT_RESTART` reboots and whether `TOGGLE_IGPU` changes anything.
4. The 40 console (UWP) flows: sequencing, whether it sends RESTART / TOGGLE_IGPU / RB_*.
5. BIOS side: what each `OemDisplayMode` value does at POST, whether iGPU-only hides the dGPU from PCI
   (this drives the vendor `IsNvGpu` gate), and what WMI command 3 touches.
6. The `UniWillVariable` offset 0x62 of `OemDisplayMode` (a read-only firmware-variable dump would settle it).
7. The NVAPI DRS setting written by `NVControlSetting.dll`; the GCU-U capability gate and exact
   `NV_CTRL_PANEL_*` payload.
8. The meaning of `CheckDGpuStatusforIGpuOnlyOnSuccess` 1 / 2 (inferred from the C50 success criteria only).
