# Vendor-parity audit — our product vs the decompiled vendor console

Acceptance question for this report: **does our product do what the vendor's console does?**
The reference specification is the decompiled vendor software, not our plan.

Read-only audit. No `src\`, `tests\`, `installer\` or `release\` file was modified; this report is
the only artifact. Line numbers are from the current checkout / decompiled trees at audit time and
are stated as `file:line`, never guessed.

## 0. Sources actually used (and one correction)

| Reference | Path | State |
|---|---|---|
| 50-series console/service (5.17.51.27) | `%TEMP%\opencode\cc-51751-27\decompiled\` (`MyControlCenter\`, `GCUService.*`, `Define\`, `MyECIO\`, `Utility\`, `LightingModel\`) | present |
| 40-series service, non-3-mode build | `%TEMP%\opencode\dl-gcuservice\full\GCUService.decompiled.cs` (2,319,757 B, FileVersion 1.0.2.70) | present |
| 40-series WITH three-mode service | `release\GCU-40-51751\AiStoneService\MyControlCenter\GCUService.exe` (14,447,864 B) | present (binary) |
| 40-series WITHOUT three-mode service | `release\GCU-40-51749\UniwillService\MyControlCenter\GCUService.exe` (11,983,608 B) | present (binary) |
| 50-series service we ship | `release\GCU-only\AiStoneService\MyControlCenter\GCUService.exe` (FileVersion 1.2.0.0) | present (binary) |
| 30-series console | `ControlCenter_4.17.47.13_Mechrevo.zip` → `%TEMP%\opencode\cc-41747-13\decompiled\` (metadata heap + PDB strings) | present |
| 5.17.49.19 asset | `ControlCenter_5.17.49.19_Mechrevo\...\ControlCenter_5.17.49.19_Mechrevo.exe` (171 MB Costura-packed) | present — **string scan inconclusive (compressed)** |
| Our product | `src\MechrevoLiteWin\**` | present |

**Correction to the job brief:** `_decompiled\CCUWinUI.decompiled.cs` (~155k lines) **does not
exist in this repo**. `official-consoles\CCUWinUI.exe` is a 203 KB binary, not source. The 5.17.51.27
decompiled sources are the `cc-51751-27\decompiled\` tree; the 5.56-era service is the obfuscated
`release\GCU-only` binary (no readable IL). Where a fact could only come from the obfuscated 5.56
service, it is marked **UNVERIFIED**.

## 1. How the vendor gates features (the reference model)

Four layers, in order of authority:

1. **`ItemSupport` DWORD registry** — `HKLM\SOFTWARE\OEM\GamingCenter2\ItemSupport\*`, written by
   `CustomizeCtrl.RefreshItemSupportReg()` (`cc-51751-27\decompiled\MyControlCenter\CustomizeCtrl.cs:384-483`)
   and republished over MQTT (`MqttClientCtrl.cs:237-263`). 34 keys. This is the main gate.
2. **`Customize/Info` support blob** — `LightbarType`, `KeyboardType`, `LEDLightbarColorful`,
   `LEDLightbarBreathEffect`, `ProjectID`, `SupportColorCalibration`, `SupportRamFan1p5`,
   `BatteryProtectionType` (`CustomizeCtrl.cs:176-191`).
3. **Runtime EC/UEFI reads** — e.g. `OemDisplayMode != 255` for the mux (`CustomizeCtrl.cs:390`),
   `WMIGetECIGPUonlySupport() == 85` for iGPU-only (`MySettingManager.cs:1761-1771`), EC RAM bit
   reads for turbo/liquid-cooling (`MyECIO\MyEcCtrl.cs:176-252`).
4. **Hard-coded lists / per-OEM `CustomizeTarget`** — five project-ID lists
   (`Utility\ProjectIdExtensions.cs`) and magic `GetCustomizeTarget()` values (3, 9, 17, 23, 27, 39,
   42, 46) checked in `CustomizeCtrl.cs`.

**Verdict legend:** `parity` = same gate & same exposure; `dead button` = we show an enabled control
the vendor hides or that cannot work; `missing feature` = the vendor exposes it, we do not;
`wrong gate` = we read a different signal than the vendor (or none); `UNVERIFIED` = no reference
evidence.

## 2. Feature-by-feature parity

### 2.1 Performance / modes / fans

| Feature | Vendor behaviour + gate (file:line) | Ours (file:line) | Verdict | Suggested fix |
|---|---|---|---|---|
| Operating mode Office/Gaming/Turbo | `TurboModeSupport` from EC 0x1183 bit1 (`MyECIO\MyEcCtrl.cs:247-252`), written `CustomizeCtrl.cs:465`; custom fan/OC only when non-commercial (`CustomizeCtrl.cs:412-423` sets `FanSettingsSupport=0`,`OcSettingsSupport=0` for commercial PH4/PH6); turbo hidden at `MyFanManager.cs:654` | `caps.TurboMode` (`Settings.cs:2923`), custom `Settings.cs:2927` `caps.CpuPerformanceTuning \|\| caps.FanSettings \|\| hw?.HasAnyCustomRange == true` | `wrong gate` | Custom must not be enabled by runtime ranges alone on a machine whose `FanSettingsSupport=0`; match the vendor branch. |
| Silent-Turbo sub-mode | `IsTurboSubModeSupport == 1` (`CustomizeCtrl.cs`; ours cited in `MechrevoDeviceCapabilities.cs:72-83`) | tri-state `SilentTurboAvailability` (`MechrevoDeviceCapabilities.cs:80-83`, `Settings.cs:2926`) | `parity` | — |
| Fan curve / custom fan | `FanSettingsSupport` (ItemSupport, `CustomizeCtrl.cs:458`); `FanBoostBtnSupport` from QkeyDefine (`:208-238`) | `caps.FanSettings`/`caps.FanBoost`; `FanCurveForm` opened from custom only; `SupportsFanRespective` (`FanCurveForm.cs:259-260`) | `parity` | — |
| FanBoost button | `GetFanBoostBtnSupport()` (`CustomizeCtrl.cs:208-238`) | `caps.FanBoost` + quick switch | `parity` | — |

### 2.2 GPU mode (the highest-risk area)

| Feature | Vendor behaviour + gate (file:line) | Ours (file:line) | Verdict | Suggested fix |
|---|---|---|---|---|
| Mux on/off (hybrid ↔ dGPU-direct) | Gate `OemDisplayMode != 255`; written `DGpuDirectConnectionSupport` (`CustomizeCtrl.cs:390,449`; `MySettingManager.cs:1593-1600`) | `caps.DgpuDirect` (`MechrevoDeviceCapabilities.cs:323` reads `DGpuDirectConnectionSupport`); `SupportsDgpuDirect` (`MechrevoHw.cs:449`) | `parity` | — |
| **iGPU-only** | Third state; gate is EC query `WMIGetECIGPUonlySupport() == 85` (`WMIEC.cs:457-472`, `MySettingManager.cs:1761-1771`) + service `case 2` + `IGPU_ONLY_*` vocabulary (51.27/5.56 only) | `CanOfferIgpuOnly => SupportsIgpuOnly && IsGpuActionAllowedByGeneration(IgpuOnlyOn)` (`MechrevoHw.cs:468-469`); `SupportsIgpuOnly => IgpuOnlyStatusSupport ?? Capabilities.IgpuOnly` (`:450`); `Capabilities.IgpuOnly` reads `iGPUModeOnlySupport`/`IGpuOnlyModeSupport`/`IntegratedGpuOnlySupport` (`MechrevoDeviceCapabilities.cs:301`) | **`wrong gate`** | `iGPUModeOnlySupport` is **not written by any vendor source or binary we scanned** (0 hits in `cc-51751-27`, `dl-gcuservice`, and all four `GCUService.exe`). Gate iGPU-only on `IsNvGpu && (APVersionCheck…)` plus the EC `0x300000000003 == 85` query, not on this key. |
| iGPU AUTO | `IGPU_ONLY_CONNECT_RB_AUTO` (`ServCMD.cs:368`); auto runtime depends on AC (`IgpuOnlySemantics.AutomaticRuntime`) | `GpuAuto` requires `SupportsIgpuOnly` (`MechrevoHw.cs:634`) | `wrong gate` | Same root as above. |
| GPU hot swap | 50-series has `GPU_HOTSWAP_ON/OFF` vocabulary, but the vendor handler is a log-only no-op (`cc-51751-27` display-route facts); 40-series/30-series absent | `SupportsGpuHotSwap => Capabilities.GpuHotSwap && SupportsIgpuOnly` (`MechrevoHw.cs:451`); `GpuHotSwap` reads `GpuHotSwapSwitchSupport && lgpuHotSwapSwitchStatus` (`MechrevoDeviceCapabilities.cs:348`) | **`wrong gate`** | Both keys are **0 hits** in the vendor trees/binaries scanned. `GpuHotSwap`/`HotSwap` exist only as strings in the 5.56 `GCU-only` binary; the exact `GpuHotSwapSwitchSupport`/`lgpuHotSwapSwitchStatus` names are unverified even there. Do not gate on them. |
| GPU switch route | Vendor restarts only for the mux change (`shutdown /r /t 0`, `MySettingManager.cs:1326-1335`); 50-series switches can be hot | **Every** change is forced to `Restart` (`GpuSwitchPolicy.cs:23-36`, resolved at `Settings.cs:4368`) | **`wrong gate`** | Restore the vendor's route selection (hot-swap/direct vs restart); a blanket restart is not vendor behaviour and interacts badly with the confirmation bug below. |
| iGPU switch confirmation | Vendor publishes `CheckDGpuStatusforIGpuOnlySwitch` = `85`/`170` (`MySettingManager.cs:840-844, 1289-1317`, `MySettingParams.cs:49`). `CheckDGpuStatusforIGpuOnlyOnSuccess` is **absent from every 5.17 decompiled source**; it appears only as a string in the 5.56 `GCU-only` 1.2.0.0 binary. | We read `CheckDGpuStatusforIGpuOnlyOnSuccess` (`MechrevoHw.cs:1959`) and treat `2`/`1` as success (`IgpuOnlySemantics.cs:20-31`) | **`wrong gate`** | Accept either field, but normalise values per build: on 5.17-era services expect `...Switch` ∈ {85,170}; on 5.56 accept `...OnSuccess`. This is the strongest single candidate root cause for the five-machine iGPU failure. |
| `SetToWMIEC` payload field | Vendor service **reads** it: `UserSetIGgpuONLYConnectionSwitch(1, val["SetToWMIEC"].ToString())` (`MySettingManager.cs:625,647`); the parameter is unused in the handler body (`:1281-1321`) | We send `["SetToWMIEC"]="OK"` (`MechrevoService.cs:906-907,1162`) | `parity` (harmless) | The in-flight N15 hypothesis that this field is "our invention" is **contradicted by the vendor source** — the vendor service reads it. Sending it is not the root cause; the value is ignored. Remove only if a 5.56-era service rejects unknown fields (UNVERIFIED). |
| GPU disconnect monitor / DC once / DC Hz | `IsGamingMonitorSupport` (read-only, defaults 0, `MqttClientCtrl.cs:259`); `DisconnectMonitor` user JSON (`GPUDeviceItem.cs:61,74,348-350`) | `GPU_DISCONNECTMONITOR`/`GPU_DC_ONCE`/`GPU_DC_HZ` wired (`docs\hardware\README.md`) | `parity` | — |

### 2.3 Battery / charge

| Feature | Vendor behaviour + gate (file:line) | Ours (file:line) | Verdict | Suggested fix |
|---|---|---|---|---|
| Charge limit | `BatteryProtection2`: NVRAM `ChargeMaximumLimit`/`ChargeMinimumLimit` defaults 100/95 (`GCUService.MySystem\BatteryProtection2.cs:53-131`), MQTT `System/BatteryProtection`; no ItemSupport bit | EC direct `0x7B9`+`0x7D0` (`EcChargeLimit.cs:32-35,117-161`); panel always visible (`Settings.cs:2963`) | **`dead button`** | The slider is rendered and enabled whenever the service is disconnected; `BatteryControl` then fails the write silently. Hide or disable-with-reason when `!RuntimeModelSupport.Current().IsSupported`, and surface the failure. |
| Battery-protection 3-mode flow | Vendor implements `PERFORMANCEDMODE`/`BALANCEDMODE`/`HEALTHYMODE` | Removed from UI by owner decision (`docs\hardware\README.md` "官方三档流程整体摘除") | `missing feature` (deliberate) | Document as an intentional divergence; re-add if parity is required. |
| AC recovery state | Vendor `AcRecoverySwitchBiosSupport` (UEFI) + runtime string `ACRECOVERY_TOGGLE_ON/_OFF` (`CustomizeCtrl.cs:387,446`; `MySettingManager.cs:1184-1215`) | `AcRecoveryOn` nullable, string primary / hex fallback (`MechrevoDeviceCapabilities.cs:59-66,333-335,362-378`) | `parity` (fix in flight for field bug #17) | Ensure the icon never infers OFF from a missing field. |

### 2.4 Keyboard / lighting

| Feature | Vendor behaviour + gate (file:line) | Ours (file:line) | Verdict | Suggested fix |
|---|---|---|---|---|
| Keyboard RGB present | `KeyboardType` = `SingleColorKeyboardProjectIDs` match AND EC 0x078C bit0 (`ProjectIdExtensions.cs:58-69,101-104`; `NvramVariable.cs:584-606,644-656`) | `caps.Keyboard` (`KeyboardSupport`/`RGBKeyboardSupport`/`SingleColorKeyboardSupport`/`KeyboardType>0`, `MechrevoDeviceCapabilities.cs:315-316`) | `parity` | — |
| Keyboard RGB entry reachability | Vendor hides the keyboard feature when unsupported | Dashboard row hidden (`Settings.cs:2885`), **but the tray/context-menu action is ungated**: `Settings.cs:3790 AddAction("键盘灯效", false, () => OpenRgbForm())` | **`dead button`** | Gate the context-menu action by the same `keyboard` predicate. |
| Per-zone / 4-zone / 2.1 / 3rd-gen | Hardware `RGBKB_Type` enum + HID factory (`LightingModel\RGBKB_Type.cs:3-34`, `HIDKeyboardFactory.cs:20-73`), not a project list | `KeyboardRgb` HID path + `KeyboardLightPathPolicy` | `parity` | — |
| Lightbar / Logo / Hinge | `LightbarSupport`=type≥2, `RGBLightbarSupport`=type==2 (`CustomizeCtrl.cs:397-405`), topics `HidLightbar[_Logo/_Hinge/_Sync]/Ctrl` | `caps.Lightbar`/`caps.LogoLight` + runtime `*Seen` (`Settings.cs:2876-2877`) | `parity` | — |
| **Local dimming** | Exposed via `Setting/Control` `LOCALDIMMING_ON/OFF` + `LOCALDIMMING_*` status (`docs\gcu-dependency-matrix.md` #6); backend support-flag **UNVERIFIED** (not found in decompiled backend) | `buttonMiniled` permanently hidden because `AsusACPI.DeviceGet` returns `-1` for `ScreenMiniled1/2` (`AsusACPI.cs:89-95`, `Settings.cs:4170-4207`); `MechrevoService.SwitchLocalDimming` has **no UI caller** | **`missing feature`** | Wire the mini-LED control to `SupportsLocalDimming` (runtime `LocalDimmingSupport != false`) and the service method, instead of the dead ACPI device path. |
| LCD overdrive | `Setting/Control` `LCDOverdrive_ON/OFF` + `LCDOverdriveSupport` status; backend gate UNVERIFIED | `caps.LcdOverdrive \|\| hw.SupportsLcdOverdrive`, hidden when unavailable (`Settings.cs:2909-2911`, `MechrevoHw.cs:495-496`) | `parity` (low confidence) | Confirm vendor gate from the (obfuscated) 5.56 service before changing. |
| Keyboard light timer | Always available; seconds value (`MySettingManager.cs:1491-1503`) | Inherits the lighting group, no own gate (`Settings.cs:2880`) | `parity` | — |
| Backlight brightness / HID fallback | Vendor goes through the service; keyboard effect is HID | `KeyboardLightPathPolicy` uses the vendor channel when the HID write does not take effect (`KeyboardLightPathPolicy.cs:27-28`, field bug #16) | `parity` | — |

### 2.5 Display / peripherals / misc

| Feature | Vendor behaviour + gate (file:line) | Ours (file:line) | Verdict | Suggested fix |
|---|---|---|---|---|
| Display refresh | No capability key; panel mode list + `AutoEnable` (`GCService5\GPUDeviceItem.cs:81-118,200-225,537-551`) | Runtime `maxFrequency` only (`UI\ScreenPanelVisibility.cs:12-15`, `Settings.cs:4153-4154`) | `parity` | — |
| Color calibration | `ColorCalibrationSupport` (UEFI) + `GamingCenter2\ColorCalibration` switch (`CustomizeCtrl.cs:63,99-110,391,448`) | `caps.ColorCalibration \|\| SupportsColorCalibration` (`Settings.cs:2908,2914`) | `parity` | — |
| USB charge | **Unconditional** — `UserSetUSBCharger` has no support gate (`MySettingManager.cs:1016-1029`) | Quick switch hidden unless `UsbChargerSeen` (`MechrevoHw.cs:720`, `Settings.cs:2852`) | **`missing feature`** | If the service reports the field, show it; matching the vendor means not requiring a `Seen` latch the vendor doesn't. |
| Touchpad | Unconditional; EC 1958 bit3 for LED (`MySettingManager.cs:1117-1140,1741-1758`) | Hidden unless `TouchpadSeen` (`MechrevoHw.cs:712`) | `missing feature` (over-gated) | Prefer unconditional exposure unless the service says otherwise. |
| OSD | Unconditional; `App.m_Osd.EnableByService()` (`MySettingManager.cs:1515-1537`) | Hidden unless `OsdSeen` (`MechrevoHw.cs:719`) | `missing feature` (over-gated) | Same. |
| WiFi/BT/Webcam | `DeviceSwitchItem`, no capability gate; webcam routed by `APVersionCheck==23` (`DeviceSwitchItem.cs:44-75,185-211`) | `WifiSeen`/`BluetoothSeen`/`WebcamSeen` | `parity` | Keep an eye on the `Seen` over-gating pattern. |
| Numpad | `NumPadSupport = !IsProjectId_NonNumPad()` (`CustomizeCtrl.cs:407,459`) | `caps.Numpad` + `NumpadSeen` (`Settings.cs:2838`, `MechrevoHw.cs:718`) | `parity` | — |
| Win / Fn lock | WinKey unconditional; FnKey BIOS value with 255 fallback (`MySettingManager.cs:1031-1043,1142-1164,1632-1654`) | Quick switches from `WinKeySeen`/`FnKeySeen` | `parity` | — |
| Overclock | `OcSettingsSupport` = 1 only on non-commercial, forced 0 for CustomizeId 39 (`CustomizeCtrl.cs:410-427,460,468`) | `SupportsOverclockMenu` + writable + ranges (`MechrevoHw.cs:757-759`, `CustomModeForm.cs:460`) | `wrong gate` (over-exposure) | Mirror the vendor's commercial/39 exclusion. |
| Liquid cooling | `LiquidCoolingSupport` = EC 1956 bit7; `LiquidCoolingAutoModeSupport` = EC 1992 bit5 (`MyEcCtrl.cs:176-181,240-245`) | `caps.LiquidCooling \|\| LcStatusSeen` (`Settings.cs:2916`, `MechrevoHw.cs:487`) | `parity` | — |
| Whisper mode | `IsProjectIdCommercialHAVE20DB` from the 14-entry `_HAVE_20DB` list + NVIDIA 2.0 API (`ProjectIdExtensions.cs:40-56,96-99`; `GPUTypeClass.cs:65`) | Withdrawn (parse-only) after two real-machine falsifications (`docs\hardware\README.md`) | `missing feature` (deliberate) | Keep withdrawn; record as a known parity gap. |
| Type-C adaptor priority | `GetTypeCAdaptorPrioritySupport()` hard-returns `false` (`MyEcCtrl.cs:254-257`) | No UI consumer | `parity` (both absent) | — |
| Power light | Derived from operating mode (EC 1957), no user toggle found (`MyFanManager_Intel.cs:2034-2057`) | Quick switch `powerlight` when `PowerLightSeen` (`MechrevoHw.cs:736`) | `UNVERIFIED` | Confirm whether the vendor console exposes a user toggle before keeping ours. |
| Battery logo | **UNVERIFIED** — not found in the decompiled backend | Read as `BatteryLogo_Status` (`docs\hardware\README.md`) | `UNVERIFIED` | — |

### 2.6 Cross-cutting gate model

| Item | Vendor | Ours | Verdict | Fix |
|---|---|---|---|---|
| Two 40-series capability tiers | Discriminator is `OemDisplayMode != 255` (mux present) plus the third state from EC `WMIGetECGPUonlySupport()==85`; the WITH tier's service adds `case 2`/`DGPU_DIRECT_CONNECT_TOGGLE_IGPU` + `IGPU_ONLY_*` (binary-verified: present in 51.27/5.56, absent in 51.49) | `DisplayRouteMatrix.TierFromCapability(iGPUModeOnlySupport)` (`Gpu\DisplayRouteMatrix.cs:219`) — key never written by vendor | **`wrong gate`** | Derive the tier from `DGpuDirectConnectionSupport` + the EC `==85` query, not `iGPUModeOnlySupport`. |
| Manual model override allowlist | Identity is the EC project byte; the vendor maps it to its enum, no user override | `ModelOverrideStateMachine.ValidateManual` accepts **only** the 24-code `PlatformCodeSet` (`ModelOverrideStateMachine.cs:48-58`) while auto support is service-served (`ModelSupport.cs:68-77`) | **`wrong gate`** | The manual path is stricter than the auto path; accept any parseable identity or document the constrained set in the UI. |
| Platform codes | 24 non-`150W` `PH4*/PH6*` entries in `Define\ProjectID.cs:23-49` (27 total incl. 3 `*150W`) | `Resources\model-registry.json` 24-code set | `parity` | — |
| Vendor name lists | `CommercialProjectIDs` 28, `_HAVE_20DB` 14, `SingleColorKeyboardProjectIDs` 9, `NonNumPadProjectIDs` 3, `IDYIDPProjectIDs` 3 (`Utility\ProjectIdExtensions.cs:8-83`) | Mirrored in `docs\hardware\model-support-matrices.md` §2 | `parity` | `IDYIDPProjectIDs` has no caller in the vendor tree — UNVERIFIED purpose. |
| `APVersionCheck` | NVRAM byte; used only for webcam (`==23`) and fan-table selection — **not** an iGPU gate (`CustomizeCtrl.cs:444,469`; `DeviceSwitchItem.cs:68`) | Not used for GPU gating | `parity` | — |
| `FeatureMatrix` vs `MechrevoDeviceCapabilities` | Vendor has one registry model | Two parallel capability systems; the UI reads the fail-closed one (`Settings.cs:2829`), so `FeatureMatrix`'s `VendorConstantOn` defaults rarely apply | `wrong gate` (internal) | Unify the capability source so default policies are consistent. |

## 3. Field-bug cross-check

| Field bug | Explained by this audit? |
|---|---|
| **#12** 24极光X "什么功能都用不了" | Yes in part: the support decision was cached in `Lazy<SupportDecision>` and froze a read-only verdict (`EcChargeLimit.cs:108-114` fix comment; N15); plus 30-series `IGPU_ONLY_*`/`RESTART`/`HOTSWAP` are `ProvenAbsent` (`DisplayRouteMatrix.cs:116-126`) so those controls are correctly absent — but many features are also over-gated by `*Seen`/missing vendor keys, which reads as "nothing works". |
| **#13/#16** iGPU switch fails | Yes: confirmation-field/value mismatch + `iGPUModeOnlySupport` gate (above). Same mechanism across machines. |
| **#15** charge threshold invalid + dash | Partly: the dash is the honest unknown placeholder `BatteryLimitUnknownText="无法读取"` (`Settings.cs:5077,5085-5088`); the invalid-threshold write was the profile-lag veto (`EcChargeLimit.cs:98-102`). |
| **#16** keyboard lighting fails | Yes: `KeyboardLightPathPolicy.hidWriteTookEffect` (`KeyboardLightPathPolicy.cs:27-28`); plus the HID-vs-service path documented in `docs\beta17-gcu-variant-selection.md`. |
| **#17** AC-recovery icon OFF while ON | Yes: `AcRecoveryOn` must be `null`, never `false`, when unreadable (`MechrevoDeviceCapabilities.cs:59-66,362-378`). |
| Five-machine iGPU failure = one root cause? | **Yes, one shared seam.** There is no per-model switch code; all machines run `MechrevoService.SwitchGpuMode` (`MechrevoService.cs:1101-1310`) + a capability gate + a confirmation poll. Two concrete mechanisms are evidenced: (a) we poll a confirmation field/value pair the 5.17-era services do not emit (`CheckDGpuStatusforIGpuOnlyOnSuccess` vs `...Switch`), and (b) we gate on `iGPUModeOnlySupport`/`GpuHotSwapSwitchSupport` keys the vendor never writes. The 50-series-only success is consistent with (a), since only the 5.56 `GCU-only` binary contains `...OnSuccess`. **Caveat:** the in-flight N15 hypothesis (invented `SetToWMIEC`) is not supported — `MySettingManager.cs:625,647` reads that field. |

## 4. Counts and UNVERIFIED list

Findings in §2: **dead button 3**, **missing feature 5** (local dimming, USB charge, touchpad,
OSD, whisper — the latter deliberate), **wrong gate 9** (iGPU capability key, AUTO, hot-swap keys,
forced-restart, confirmation field, custom/OC over-exposure, tier discriminator, manual allowlist,
dual capability systems). Most serious five:

1. iGPU-only capability key `iGPUModeOnlySupport` is never written by the vendor → iGPU-only/auto disabled or mis-gated.
2. iGPU switch confirmation reads `CheckDGpuStatusforIGpuOnlyOnSuccess` (2/1) while 5.17 services publish `CheckDGpuStatusforIGpuOnlySwitch` (85/170) → switch never confirms on 40-series.
3. Blanket "always restart" GPU route (`GpuSwitchPolicy.cs:34-35`) is not vendor behaviour.
4. 40-series tier discriminator reads a non-existent key instead of `OemDisplayMode != 255` + EC `==85`.
5. Charge-limit panel is always enabled and silently fails when the service is disconnected (dead button).

UNVERIFIED (do not treat as fact):
- Local dimming / LCD overdrive / battery logo backend gates — not found in the decompiled backend; may be front-end- or driver-level, or inside the obfuscated 5.56 service.
- `CheckDGpuStatusforIGpuOnlyOnSuccess` value semantics (2/1) — its string exists only in the obfuscated 5.56 `GCU-only` binary; the 5.17.51 handler uses `...Switch` = 85/170.
- `GpuHotSwapSwitchSupport` / `lgpuHotSwapSwitchStatus` exact names — absent everywhere scanned; `GpuHotSwap`/`HotSwap` strings exist only in the 5.56 binary.
- `IDYIDPProjectIDs` purpose — defined, no caller in the vendor tree.
- 5.17.49.19 client behaviour — its EXE is Costura-compressed, so its negative string scan is not evidence of absence.
- Whether the 5.56 `GCU-only` service (which we ship for 50-series and possibly others) serves the non-3-mode 40-series tier — previously recorded as BLOCKED-HW.
