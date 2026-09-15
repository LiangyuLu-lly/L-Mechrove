# ControlCenterX / MechrevoLiteWin 死代码与架构审计

- 审计范围：`src\MechrevoLiteWin\**`（主项目），旁及 `src\MechrevoLite\`、`src\Probe\`、`tests\`。
- 审计方式：只读。**未修改任何源文件、未 build、未跑测试、未使用 git 命令。**
- 统计基线：`src\MechrevoLiteWin` 源文件 121 个、约 41,000 行（不含 `bin/obj/dist`）。
- 排除目录：`_decompiled\`、`release\`、`official-consoles\`、`g-helper-main\`、`watercooler-manager-main\`、`artifacts\`、`dist*`、`_extracted`、`_tools`、`.build`、`size_analysis`、`design-previews`、`BetterRGB-*`、`Microsoft.PowerShell.Core`。

## 证据约定

本文每条结论都给出「文件:行 + 我用过的检索」。常用手段：

- **引用计数**：对符号 `X` 在全库 `.cs`（`src` 去掉 `bin/obj/dist` + `tests` 去掉 `bin/obj`）做正则 `\bX\b` 计数。计数 = 1 表示「只有声明、零调用方」；= 2 对资源串/P/Invoke 表示「只有声明 + 自身定义行」。
- **读/写配置键**：正则 `AppConfig\.(Get|Is|IsNotFalse|GetString|Exists|GetMode|IsMode|GetModeString|RemoveMode)\s*\(\s*"([^"]+)"` 为「读」，`AppConfig\.(Set|SetMode|Remove)\s*\(\s*"([^"]+)"` 为「写」，取差集。
- 标 **[待确认]** 的条目为不确定/可能是有意保留，删除前需再验证。

---

## A. 死代码 / 永不生效的代码

### A1. 未引用的类型（整类无人引用）

| # | 位置 | 内容 | 证据 |
|---|------|------|------|
| A1-1 | `Stubs.cs:57` | `class UpdatesControl` | `\bUpdatesControl\b` 全库计数 = 1（仅声明） |
| A1-2 | `Stubs.cs:59` | `class MKeyControl`（含 `ApplyAll`/`Reset`） | `\bMKeyControl\b` = 1；`\bApplyAll\b` = 1 |
| A1-3 | `Stubs.cs:111` | `enum AuraSpeed` | `\bAuraSpeed\b` = 1 |
| A1-4 | `Display\DisplayNative.cs:151` | `enum DisplayDeviceStates` | `\bDisplayDeviceStates\b` = 1 |
| A1-5 | `Hardware\FanDevice.cs:18` | `enum AsusMode` | `\bAsusMode\b` = 1 |
| A1-6 | `Hardware\HidDeviceWin.cs:22` | `struct SpDeviceInterfaceDetailData` | `\bSpDeviceInterfaceDetailData\b` = 1 |
| A1-7 | `UI\RComboBox.cs:311` | `enum RegionFlags` | `\bRegionFlags\b` = 1 |
| A1-8 | `Hardware\OfficialConsoleCatalog.cs:16` | `static class OfficialConsoleCatalog` | 全库 `OfficialConsoleCatalog` 出现 **1 次**（仅类声明）。**[待确认]**：实现文件存在但零调用方，疑为未提交的在途改动，删除前先确认其它任务是否正在接线。 |

### A2. 未引用的方法（零调用方）

分类列出；计数均 = 1（仅定义）。

**AppConfig（`AppConfig.cs`）**
- `GetDefaultCurve` :459、`GetFanConfig` :434、`SetFanConfig` :445、`GetModelShort` :338、`IsAlwaysUltimate` :557、`SaveDimming` :668
- 证明：`\bGetDefaultCurve\b`/`\bGetFanConfig\b`/… 全库 = 1。注意 `GetDefaultCurve` 与本项目 `Resources\DefaultCurve_*.json` 三份文件的默认曲线逻辑相关，删除后需确认那份 JSON 仍被 `FanCurveForm` 读取。

**AsusACPI（`AsusACPI.cs`，整文件 268 行，大量 ASUS 残留）**
- `FixFanCurve` :81、`IsEmptyCurve` :88、`SetFanRange` :194、`SetFanHysteresis` :196、`SetCores` :199、`GetAPUMem` :200、`SetAPUMem` :201、`GetVramMem` :203、`SetVramMem` :204、`GetVramOptions` :206、`ScanRange` :266
- `IsXGConnected()` :247 —— 实现体 `=> false`，且全库唯一提及在注释 `Settings.cs:4641`。
- 常量/字段：`CORES_MAX` :34、`CORES_MIN` :35、`GPU_BASE` :36、`DefaultCPU` :68、`DefaultTotal` :77、`ECoreMin` :78、`PCoreMin` :79、`DevsCPUFanCurve` :27。

**Mode**
- `Mode\ModeControl.cs`：`IsPawnAvailable` :49、`ResetPerformanceMode` :157、`CyclePerformanceMode` :273、`SyncExternalMode` :364（且 `mechrevoMode` 参数未用）
- `Mode\PowerNative.cs`：`PowerReadACValue` :45、`GetPowerMode` :153、`GetLidAction` :310、`SetLidAction` :332、`GetHibernateAfter` :367、`SetHibernateAfter` :381、`BatteryLifeTime` :423、`BatteryFullLifeTime` :424
- `Mode\Modes.cs:118` `IsCurrentCustom`

**Settings UI（`Settings.cs` / `Settings.V2.cs`）**
- `SetVersionLabel` :3645、`VisualizePeripherals` :5028、`UiStyleBodyFloat`（`Settings.V2.cs:672`）
- **事件处理器从未订阅**（这是「功能永不生效」的强证据）：
  - `ButtonFnLock_Click` :5194 —— `\bButtonFnLock_Click\b` = 1，说明没有任何 `+= ButtonFnLock_Click`；FnLock 按钮因此永远不响应。
  - `ComboKeyboard_SelectedValueChanged` :3932 —— 同样零订阅。
  - `Program.cs:1836 OnChargerEvent()` —— `\bOnChargerEvent\b` = 1，零调用；充电事件的处理入口悬空（实际电源事件走 `Settings.cs` 里的 `WndProc`/`PowerSetting` 分支）。
- 常量 `CompactHzLogicalHeight` :30、`CompactThemeModeLogicalHeight` :32、`LiquidCoolingHeaderLogicalHeight` :39、`LiquidCoolingLightLogicalHeight` :40。

**GPU / 显示**
- `Gpu\NVidia\NvidiaGpuControl.cs:323` `GetClocks`；`Gpu\NVidia\NvidiaSmi.cs:17` `GetMaxGPUPower`；`Gpu\NVidia\NvmlHelper.cs:44` `GetTemperature`
- `Gpu\GPUModeControl.cs` `NotifyManualGpuSwitchStarted` :341、`NotifyManualGpuSwitchFinished` :343
- `Display\ScreenControl.cs` `ToggleScreenRate` :61、`GetOptimalBrightness` :138
- `Display\ScreenNative.cs` `DefaultDevice` :10、`IsExternalDisplayConnected` :15

**其它**
- `HardwareControl.cs:169` `AmdApu()`（`=> false`）
- `Battery\BatteryControl.cs:79` `UnSetBatteryLimitFull`、:92 `SetAsusChargeLimit`
- `Helpers\ProcessHelper.cs:343` `StopDisableService`、:357 `StartEnableService`
- `Helpers\DynamicLightingHelper.cs:67` `SetEffect`
- `Helpers\SystemRestart.cs:24` `ConfirmationValidityMs`
- `Helpers\OfficialConsoleIsolation.cs:84` `CreatedUtc`
- `Overlay\HardwareOverlay.cs:352` `BaseModeWidth`
- `Pawn\RyzenSmu.cs:150/151/152` `CanSetTDP`/`CanSetCoAll`/`CanSetThm`
- `Hardware\WinPowerPlan.cs:15` `NoSubgroup`
- `Hardware\DeviceCapabilitySnapshot.cs:47` `IsKnown`
- `Updates\UpdateChecker.cs:46` `StableChannel`（常量）
- `UI\RCollapseGroup.cs:160` `ForceExpandedForAudit`（连测试也没用）
- `UI\ControlHelper.cs:17` `DarkMode`

**判定为误报、不要删的（框架回调 / 接口实现）**
- `UI\CustomContextMenu.cs:22 OnRenderItemCheck` —— `ToolStripRenderer` 的受保护重写，由 WinForms 调用。
- `UI\RButton.cs:81 ShowFocusCues` —— `Control` 受保护重写。
- `Settings.V2.cs:852 PreFilterMessage` —— `DashboardWheelFilter : IMessageFilter`（`Settings.V2.cs:847`），由消息泵调用。
- `Helpers\OSDBase.cs:292 Dispose(bool disposing)` —— `disposing` 未用但属标准模式。

### A3. 未引用的属性 / 字段 / 常量 / 事件

- `Hardware\MechrevoHw.cs`：`SupportsFanSettings` :465（唯一提及是定义本身 —— 风扇设置入口没有任何消费者）、`GpuOverclockWriteElevated` :570、`RefreshDirectGpuOverclock` :2660、`MidFanSeen` :244（`=> false`，仅在诊断字符串 :346 被引用）。
- `Hardware\KeyboardRgb.cs`：`ReconnectInFlight` :72、`LastSavedMode` :1135（`\b…\b` = 1）。
- `UI\RCollapseGroup.cs:157` `TitleLabel`（内部 `_titleLabel` 有使用，公开属性零引用）。
- `Hardware\HidDeviceWin.cs`：`InputReportByteLength` :34、`NumberLinkCollectionNodes` :39、`NumberInputButtonCaps` :40 … `NumberFeatureDataIndices` :48 —— 这些是 `HidpCaps` **互操作结构字段**，由 API 填充，**不是死代码，请勿删**（列在此处只为说明工具会误报）。
- `NativeMethods.cs` 中已失效的电源常量 / GUID（详见 A7、A11）。
- `Helpers\OSDBase.cs:362-370` `AW_HOR_POSITIVE/AW_HOR_NEGATIVE/…/AW_BLEND` 全部零引用。
- `Stubs.cs`：`RefreshBatteryForAllDevices(bool force)` :104 的 `force` 未用等。

### A4. 未使用的参数

- `HardwareControl.cs:147` `IsUsedGPU(int threshold = 10)` —— `threshold` 从未在体内使用（配合 A7-2 一起看）。
- `Settings.cs:4793` `VisualiseIcon(bool themeChange = false)` —— `themeChange` 未使用，参数无效果。
- `Settings.cs:3710` `OnTimedEvent(Object? source, ElapsedEventArgs? e)` —— `source` 未用。
- `AsusACPI.cs:122` `DeviceGet(uint code, int len = 4)` —— `len` 未用。
- `Stubs.cs:82` `ApplyBrightness(int brightness, string log = "Backlight")` —— `log` 未用。
- 说明：全库绝大多数「参数未用」命中来自 `[DllImport] extern` 声明（参数用于封送，不是真正的未使用）以及位置 record 的构造参数。这类**不要**按未使用参数处理。

### A5. 未使用的 `using` 指令

**未能可靠枚举**，需要编译器/分析器 pass。做法：`dotnet build -p:EnableNETAnalyzers=true` 打开 `IDE0005`（或 `dotnet format analyzers --diagnostics IDE0005`）一次性列出。

- 可疑样例（未证实，[待确认]）：`NativeMethods.cs:1 using MechrevoLite;`（本文件不在 `MechrevoLite` 命名空间下，疑似冗余）、`NativeMethods.cs:2` 的 BOM 前缀 `using System.Runtime.InteropServices;`。
- 另有大量 P/Invoke 文件在删除 A11 的 DllImport 后会出现新的未使用 using，需在该波次用分析器复跑。

### A6. 配置键：写了不读 / 读了不写（最有价值的一类）

方法：提取 `AppConfig.Get/Is/IsNotFalse/GetString/Exists/GetMode/IsMode/GetModeString/RemoveMode("…")` 为读集合（53 个键），`AppConfig.Set/SetMode/Remove("…")` 为写集合（46 个键）。**注意**：以变量名为首参的写入（如 `Hardware\MechrevoHw.cs:2887 DirectGpuProfileKey(...)`、`Hardware\WinPowerPlan.cs:71 planKey`、`UI\RCollapseGroup.cs:186 _configKey`、`Settings.cs:3862 colorField`）不计入字面量统计，已人工排除，因此下面的「读但从不写」是**没有代码写入路径**的键。

**读了但从不写（功能只能靠手改 config.json 生效，UI 无法设置）——共 53 个：**

```
auto_boost, charger_delay, check_updates, cpu_temp, cpu_uv, disable_osd,
disable_power_event, ec_charge_limit, fahrenheit, fn_lock, gpu_boost,
gpu_clock_limit, gpu_core, gpu_memory, gpu_power, gpu_temp, igpu_uv,
language, limit_cpu, limit_fast, limit_slow, limit_total, max_igpu_uv,
max_rate, max_temp, max_uv, min_gpu_clock, min_igpu_uv, min_rate,
min_temp, min_uv, mode_command, mode_delay, mouse_battery, nv_delay,
optimized_usbc, overlay_alpha, overlay_light_mode, overlay_names,
overlay_show_battery, overlay_show_fans, overlay_show_power,
overlay_show_ram, overlay_show_temp, overlay_show_usage, powermode,
reapply_time, refresh_delay, scheme, scheme_usbc, sensor_timer,
sensors_always, status_mode, theme, topmost, update_base_url,
usbc_profile
```

高价值个例（这些是「开关永远为默认值」的直接证据）：

| 键 | 读取点 | 影响 |
|----|--------|------|
| `disable_osd` | `Helpers\ToastForm.cs:121` `if (AppConfig.Is("disable_osd")) return;` | 无写入路径 → OSD 关闭开关永远无法打开 |
| `fahrenheit` | `Helpers\TempHelper.cs:5` `static readonly bool IsFahrenheit = AppConfig.Is("fahrenheit")` | 恒 false，温度单位锁定摄氏 |
| `theme` | `UI\RForm.cs:71` `flatTheme = AppConfig.GetString("theme")?.ToLower() == "flat"` | 恒 false，flat 主题不可达 |
| `topmost` | `Settings.cs:2256/4101/4115/5151`、`Program.cs:1884` | 5 处读取但零写入，置顶开关无法持久化 |
| `fn_lock` | `Settings.cs:5179` | 读但零写；且唯一按钮 `ButtonFnLock_Click`(A2) 从未订阅 |
| `sensors_always` | `Settings.cs:1894` | 恒默认 |
| `check_updates` | `Updates\UpdateChecker.cs:68` | 只能靠手改配置 |
| `overlay_names` | `Overlay\HardwareOverlay.cs:969` | 恒 false |
| `update_base_url` | `Updates\UpdateChecker.cs:65` | 更新源覆盖只能手改 |

**写了但从不读 —— 5 个：** `aura_mode`、`calib_mode`、`frequency`、`overlay`、`overlay_game_only`。
证明：写集合有、读集合无（字面量）。**注意**：这些键可能是给外部/官方控制台读的，或经由动态键名读取，标记 **[待确认]**，删除前要确认没有 `GetString(name)` 形式的动态读。

### A7. 永不生效的功能 / 硬编码 false 门禁

| # | 位置 | 内容 | 证据与影响 |
|---|------|------|-----------|
| A7-1 | `Program.cs:243-696` | `#if HARDWARE_DIAGNOSTICS` 整段（约 453 行） | `HARDWARE_DIAGNOSTICS` 在**全仓库（排除第三方目录）从未定义**：搜索 `*.cs,*.csproj,*.props,*.targets,*.ps1,*.yml,*.json` 只有 `Program.cs` 自身出现该符号。csproj 无 `DefineConstants`。→ 整段编译不进去，永远不可达。取反分支 `#if !HARDWARE_DIAGNOSTICS`（:233-241）才是实际生效的。 |
| A7-2 | `Gpu\GPUModeControl.cs:417` | `if (HardwareControl.IsUsedGPU())` 分支 | `IsUsedGPU` 实现 `=> false`（`HardwareControl.cs:147`）。→ 「GPU 正在被占用，是否仍切换」的确认对话框**永远不会弹出**，`ScheduleGpuEco(1, delay)` 无提示直接执行。 |
| A7-3 | `Hardware\MechrevoHw.cs:244` | `MidFanSeen => false` | 唯一引用在诊断字符串 :346（`Yn(MidFanSeen)` 恒 "N"）。中置风扇能力判定恒假。 |
| A7-4 | `AsusACPI.cs:247/266` | `IsXGConnected() => false`、`ScanRange() => false` | 两者零调用（见 A2）；ASUS 外置显卡坞路径已整体废弃。 |
| A7-5 | `Stubs.cs:88/89/101/102/136/137/141` | `HasRandomColor/HasSecondColor => false`、`IsAnyPeripheralConnect() => false`、`IsAuraSync => false`、`IsDeviceReady => false`、`Charging => false`、`HasBattery() => false` | `IsAnyPeripheralConnect` 只有 `Settings.cs:5030` 一个消费者（而 `VisualizePeripherals` 本身已死，见 A2）→ 外围设备面板整条链路无效。 |
| A7-6 | `IGpuOverclockControl.cs:24` | `bool WritesRequireElevation => false;`（接口默认实现） | 消费者 `MechrevoHw.cs:585`；NV 实现 `NvidiaGpuControl.cs:30` 会返回真实值，但**默认实现恒 false**，非 NV 实现走这条即永久跳过提权。**[待确认]** 是否有非 NV 实现类。 |
| A7-7 | `Helpers\ProcessHelper.cs` / `NativeMethods.cs` | `RefreshBatteryHealth()`（`HardwareControl.cs:172`）是空体 `{ }`，但被 `Settings.cs:3350` 调用 | 「刷新电池健康」按钮/入口执行空操作。 |

### A8. 不可达分支 / 无条件返回之后的代码

- 当前**未发现**现存的无条件 `return` 后死代码：`Program.cs:230-231` 注释明确指出「此前守卫无条件 return 导致 `#if HARDWARE_DIAGNOSTICS` 整段不可达」，该问题已修复，现为 `#if !HARDWARE_DIAGNOSTICS` 条件内返回。作为历史回归点记录，无需再改。
- 硬编码 false 造成的不可达分支见 A7-2。

### A9. 死资源

**字符串（`Properties\Strings.resx` + 20 个语言 resx + `Strings.Designer.cs`）**
- 总键数 282，**未被代码引用 181 个**。
- 证明：取 `Strings.Designer.cs` 中 `GetString("Key")` 的所有 Key，在「除 `Strings.Designer.cs` 外的全部 `src` + `tests` `.cs`」里做 `\bKey\b` 计数 = 0 即判未用。抽样验证 `MFont`、`VolumeUp` 在全库仅出现于 Designer 自身的声明与 `GetString` 字面量（计数 = 2），确认方法有效。
- 181 个中绝大多数是 ASUS 专属功能的文案：`Slash*`（16 个）、`Aura*`（10 个）、`Matrix*`（9 个）、`Mouse*`（13 个）、`*Deadzones`、`AnimeMatrix`、`AllyController`、`XGM` 等 —— 与仓库已删除的 ASUS 硬件族（见 `Stubs.cs` 头注释、`AppConfig.cs:517-529` 注释）同源。
- 另有「读但从不写」相关文案：`FnLockOn/FnLockOff`、`WindowTop`、`ToggleMiniled`、`ToggleClsLid` 等。

**位图/图标（`Properties\Resources.resx` + `Resources\*.png|ico`）**
- 资源属性总数 87，**未被代码引用 56 个**。
- 证明：从 `Resources.Designer.cs` 提取生成的合法属性名（而非原始文件名），在其余 `.cs` 做 `\bProp\b` 计数 = 0。注意必须先取生成的属性名：资源 `icons8-bicycle-48 (1)` 的属性名是 `icons8_bicycle_48__1_`，用原始文件名会得到假阳性。
- 未被引用样例：`MFont`（字体字节流）、`lighting_dot_24/32/48`、`dot_eco`、`dot_standard`、`dark_eco`、`light_eco`、`light_standard`、`cross_23`、`mouse_layout`，以及 40+ 个 `icons8_*` 图标（`icons8_fan_32`、`icons8_gauge_32`、`icons8_video_48`…）。
- 未引用资源文件本身仍在 `Resources\` 目录且多为 `EmbeddedResource`，删除可减小程序集。
- **[待确认]**：`DonateForm.cs:83` 用 `Image.FromFile` 从磁盘按路径加载图片（`Resources\qrcode1.jpg/qrcode2.jpg` 走 csproj `Content`），这类不经过 `Resources.Designer`，需单独核对，不能按属性未引用就删。

### A10. 未使用的 NuGet 包

`MechrevoLite.csproj` 的 6 个 `PackageReference` 逐一交叉核对实际类型/`using` 使用：

| 包 | 引用点（证据） | 结论 |
|----|----------------|------|
| `LibreHardwareMonitorLib` 0.9.6 | `Hardware\LhmMonitor.cs:1 using LibreHardwareMonitor.Hardware;`；`HardwareControl.cs:36 LhmMonitor? lhm` | 在用 |
| `MQTTnet` 5.2.0.1603 | `Hardware\MechrevoHw.cs:3-4 using MQTTnet; / MQTTnet.Formatter;` 等 | 在用 |
| `Newtonsoft.Json` 13.0.4 | `Hardware\MechrevoHw.cs:5`、`Diagnostics\FunctionVerifier.cs:3`、`Updates\UpdateChecker.cs:2` | 在用 |
| `NvAPIWrapper.Net` 0.8.1.101 | `Gpu\NVidia\NvidiaGpuControl.cs:2-8 using NvAPIWrapper.*` | 在用 |
| `System.Management` 10.0.10 | `AppConfig.cs:279/321`、`Display\ScreenBrightness.cs:33/61`、`Diagnostics\DiagnosticSystemInfo.cs:112` | 在用 |
| `TaskScheduler` 2.12.2 | `Helpers\OfficialConsoleIsolation.cs:463`、`Helpers\Startup.cs:123`（`Microsoft.Win32.TaskScheduler.TaskService`） | 在用 |

**结论：当前没有可删的 NuGet 包。** 注意包名 `TaskScheduler` 与 BCL 的 `System.Threading.Tasks.TaskScheduler`（`Program.cs:141`、`Settings.cs:2402`）同名，交叉核对时不要误判。

**csproj 中的过期注释（不会造成死代码，但会误导）：**
- 关于 FftSharp/AudioVisualizer 的注释仍保留，但该包已删除、`AudioVisualizer` 在源码中已不存在（`\bVisualiseAudio\b`、`\bAudioVisualizer\b` 全库 = 0，连设置项也已移除）。
- 关于 `Pawn\IntelMSR.bin` 的注释：该文件**不存在**（`Pawn\` 下只有 `RyzenSMU.bin`），注释属于历史遗留。

### A11. 未调用的 interop 声明（重点：删除「显示器关闭广播」后的残留）

**`NativeMethods.cs` —— 电源/会话检测 GUID 与常量（仓库近期删除 monitor-off 广播后的残留）：**
- 常量：`DEVICE_NOTIFY_SERVICE_HANDLE` :91、`PBT_APMRESUMESUSPEND` :95、`DataLength` :114。
- GUID（`\bName\b` 全库 = 1，仅定义）：`AcdcPowerSource` :121、`BatteryPercentageRemaining` :123、`GlobalUserPresence` :127、`MonitorPowerGuid` :129、`PowerSavingStatus` :131、`SessionDisplayStatus` :136、`SessionUserPresence` :139、`SystemAwaymode` :141、`IdleBackgroundTask` :145、`MinPowerSavings` :150、`MaxPowerSavings` :151、`TypicalPowerSavings` :152；`PowerSchemePersonality` :147 只出现在注释里。
- **仍在用的对照项（不要删）**：`ConsoleDisplayState`（`Program.cs:862`）、`EnergySaverStatus`（`Program.cs:864` + `Settings.cs:3439`）、`LIDSWITCH_STATE_CHANGE`（`Program.cs:863` + `Settings.cs:3420`），以及 `RegisterPowerSettingNotification`/`RegisterSuspendResumeNotification`/`Unregister*`（`Program.cs:862-865`）。
- `LockScreen()` :83 / `[DllImport] LockWorkStation` :79-81 —— `\bLockScreen\b` 方法唯一引用是自身；`Strings.LockScreen` 是**另一个**东西（资源串），别误删资源。此方法连同 P/Invoke 一并可删。

**`Helpers\OSDBase.cs` —— `User32`/`Gdi32` 内部类中零调用的声明（`\bName\b` 全库 = 1）：**
`AdjustWindowRectEx` :457、`AnimateWindow` :377、`ClientToScreen` :379、`CombineRgn` :467、`CreateBrushIndirect` :469、`CreateRectRgnIndirect` :473、`DispatchMessage` :381、`DrawFocusRect` :383、`GetClipBox` :479、`GetFocus` :387、`GetKeyState` :389、`GetMessage` :393、`GetParent` :395、`HideCaret` :405、`InvalidateRect` :407、`LoadCursor` :409、`MapWindowPoints` :411、`MoveWindow` :413、`PatBlt` :481、`PeekMessage` :415、`ScreenToClient` :423、`SetCursor` :427、`SetFocus` :429、`SetWindowRgn` :437、`ShowCaret` :439、`SystemParametersInfo` :445、`TrackMouseEvent` :447、`TranslateMessage` :449、`UpdateWindow` :453、`WaitMessage` :455、`SendMessage` :425（**唯一提及在 `OfficialConsoleIsolation.cs:1091` 注释里，不是调用**）。
仍在用（不能删）：`GetDC`/`ReleaseDC`/`SetWindowPos`/`GetWindowRect`/`ShowWindow`/`SetThreadDpiAwarenessContext`/`SetWindowLong`/`UpdateLayeredWindow`/`CreateCompatibleDC`/`SelectObject`/`DeleteObject`/`DeleteDC`/`SW_*`/`WS_*`/`WS_EX_*` 等。

**其它文件**
- `Display\DisplayNative.cs`：`CreateDC` :471、`SetICMMode` :477、`SetICMProfileW` :474、`WcsSetDefaultColorProfile` :480（零调用）。
- `Hardware\HidDeviceWin.cs:67` `HidD_GetHidGuid`（零调用）。
- `Gpu\NVidia\NvmlHelper.cs:13` `nvmlDeviceGetTemperature` 与 `:44` `GetTemperature`（零调用）。
- `Pawn\PawnIOWrapper.cs`、`Helpers\RestrictedProcessHelper.cs`、`Helpers\DeviceHelper.cs`、`All` 中的绝大多数参数未用命中属于正常封送，不作死代码处理。

### A12. 未引用的文件 / 项目（`src\` 下）

- **`src\MechrevoLite\**` 整个项目是孤儿**：18 个文件、约 695 行（C# + XAML，含 `Hardware\MqttBackend.cs`、`Services\TelemetryService.cs`、`Views\HomePage.xaml` 等）。
  - 证据：`MechrevoLite.slnx` 只列 `src/Probe`、`src/MechrevoLiteWin`、`tests/MechrevoLite.Tests`；全仓库检索 `MechrevoLite\MechrevoLite.csproj` 无任何 `ProjectReference`；`MechrevoLiteWin.csproj` 无 `ProjectReference`。→ 该 WPF 项目不参与构建、无引用者。
  - **[待确认]** 是否作为「未来 WPF 版」有意保留。若否，是最大的单块可删代码。
- `src\MechrevoLiteWin\bin`、`obj`、`dist`、`bin\x64\...\win-x64\R2R` 等构建产物被纳入工作区（见根目录与 `src` 下的 `bin/obj`），不属于源码死代码，但会干扰检索；建议 `.gitignore` 已覆盖（本次未查 git，故不下结论）。

---

## B. 架构 / 技术栈可优化项

按「位置 / 问题 / 影响 / 方向 / 工作量(S/M/L) / 行为是否保持」列出。

### B1. 上帝对象：`SettingsForm`
- 位置：`Settings.cs`（5202 行）+ `Settings.V2.cs`（1058 行）+ `Settings.Designer.cs`（1476 行）；单个 `partial class SettingsForm`。
- 问题：把仪表盘布局、传感器刷新、GPU 模式 UI、灯效、官方控制台隔离、开机自启状态、DPI、右键菜单全部塞进一个类；`Settings.cs` 内约 92 个 private 成员。
- 影响：任何一处改动都可能影响无关页面；合并冲突高发；测试只能整窗构造（已有大量 UI 测试）。
- 方向：按页面/职责拆 `partial` 或抽出控制器（Dashboard / Sensors / Gpu / Lighting / Startup）。**L，行为保持**（纯搬移 + 保持事件接线）。

### B2. 超长方法（>80 行，brace 计数启发式，上限 401）
| 方法 | 位置 | 行数（≥） |
|------|------|-----------|
| `InitializeComponent` | `Settings.Designer.cs:32` | 401（自动生成，可保留） |
| `Main` | `Program.cs:129` | 401 |
| `HandleMessage` | `Hardware\MechrevoHw.cs:1216` | 401 |
| `PowerLimits` | `Pawn\RyzenSmu.cs:95` | 401（启发式可能被字符串/注释中的括号干扰，[待确认]） |
| `Run` | `UI\UiAuditRunner.cs:39` | 401 |
| `BuildDashboardLayout` | `Settings.cs:2418` | 241 |
| `Render` | `UI\UiGlyph.cs:35` | 218 |
| `PerformPaint` | `Overlay\HardwareOverlay.cs:545` | 168 |
| `RefreshDeviceCapabilities` | `Settings.cs:2805` | 166 |
| `SetContextMenu` | `Settings.cs:3467` | 165 |
| `SwitchGpuMode` | `Hardware\MechrevoService.cs:990` | 163 |
| `SetCustomDetail` | `Hardware\MechrevoService.cs:327` | 155 |
| `SetGPUMode` | `Gpu\GPUModeControl.cs:109` | 154 |
| `SwitchMode` | `Hardware\MechrevoService.cs:145` | 120 |
| `ApplyTree` | `UI\UiVisualStyle.cs:332` | 114 |
| `WndProc` | `Overlay\HardwareOverlay.cs:245` | 104 |
| `RefreshSensors` | `Settings.cs:4436` | （>80，未逐条列出） |
- 影响：可读性差、异常边界难界定、难以单测。
- 方向：`Program.Main` 抽 `RunCliMode()`/`RunGuiMode()`；`HandleMessage` 按消息类型拆 `partial` 处理器。**M/L，行为保持**（先抽方法、不动逻辑）。

### B3. 同一件事的多份并行实现（灯效电源最典型）
- 现象：外置灯带/键盘灯「通电/断电/恢复」至少有 4 条并行路径：
  1. `Program.cs:1365 ReconcileLightingPowerAsync` → `RestoreExternalLightingAsync`(:1465) → `SetExternalLightingPowerAsync`
  2. `Program.cs:1513` 直接 `service.PublishLightPower(topic, requested)` + `ObserveLightPower`
  3. `Program.cs:1559/1587` 又各有一处对 `"Keyboard/Ctrl"` 直接 `PublishLightPower`/`ObserveLightPower`
  4. `Hardware\MechrevoService.cs:1977 PublishLightPower` / `:2023 ObserveLightPower` / `:1443 SetPowerLightBrightness`；UI 侧 `RgbForm.cs:307 service.SetLightPower("Keyboard/Ctrl", true)`、`LightForm.cs:212 Program.hw.Publish(_topic, …)`、`Settings.V2.cs:489 service.SetLightEffect(...)` 各自直发。
- 影响：同一状态有多个真值来源；`Program` 层已用 `_lightingRestoreRequestId` 等一堆静态位做去重补偿，说明重复下发确已造成固件重初始化/闪烁问题。
- 方向：把所有灯的电源/效果下发收敛到 `LightingRestoreCoordinator` / `MechrevoService` 单一入口，UI 只发意图。**M/L，行为保持**（接口收敛，不改时序）。
- 同类：刷新路径分散 —— `RefreshSensors`/`RefreshDeviceCapabilities`/`RefreshGcuStatus`/`RefreshOfficialConsolePanel`/`RefreshGpuModeUiStateAsync`/`RefreshStartupStatusAsync`/`RefreshHardwareStateAsync`/`RefreshAll`/`RefreshCustom`/`RefreshThemeButtons` 等十余个，散布在 `Program.cs`、`Settings.cs`、`CustomModeForm.cs`、`UpdateForm.cs`、`HardwareControl.cs`。**M，行为保持**（统一到一个刷新协调器）。

### B4. sync-over-async / 阻塞调用（可从 UI 线程触达）
| 位置 | 调用 | 风险 |
|------|------|------|
| `Settings.cs:2400` | `BeginInvoke(() => ApplyOfficialConsoleStatus(task.Result))` | `.Result` 在 UI 回调里取值；虽用 `ContinueWith` 保证已完成，仍是同步取值模式，易被后续改动引入死锁 |
| `Mode\ModeControl.cs:96` | `_modeTask.Wait(5000)`（`WaitForApply`） | 阻塞等待 5s，UI 路径调用即卡界面 |
| `Helpers\ProcessHelper.cs:397` | `readTask.Result` | 同步等待子进程输出 |
| `Hardware\MechrevoHw.cs:3115` | `DisconnectAsync().WaitAsync(Timeout).GetAwaiter().GetResult()` | 断开连接时同步阻塞 |
| `Gpu\NVidia\ElevatedGpuOverclockHelper.cs:328` | `RunAsync(...).GetAwaiter().GetResult()` | 提权助手路径同步等待 |
| `Program.cs:700` | `ApplyBatteryLimitAtBootAsync().GetAwaiter().GetResult()` | 启动 CLI 分支（无 UI），风险低 |
| `Program.cs:255` | `FunctionVerifier.RunAsync(...).GetAwaiter().GetResult()` | 诊断分支（#if 关闭），实际不可达 |
| `Thread.Sleep` | `Gpu\NVidia\NvidiaGpuControl.cs:81/434`、`Hardware\KeyboardRgb.cs:212/256/259`、`Mode\PowerNative.cs:261`、`Pawn\RyzenSmu.cs:251/254/449/509`、`Updates\UpdateInstaller.cs:314`、`Program.cs:449`、`Helpers\DynamicLightingHelper.cs:138` | 多为硬件时序/固件等待，**不能盲目改 async**，但其中若在 UI 线程调用会造成可感知卡顿。 |
- 方向：UI 路径上的 `Wait/.Result` 改为 `async void` 事件处理器 + `await`；硬件时序类 `Thread.Sleep` 保留但注明「必须在后台线程」。**S/M，`Wait`→`await` 行为保持（时序不变时）；把 UI 调用迁到后台是行为保持**。

### B5. 绕过共享布局助手的临时缩放/尺寸计算
- 已有共享助手：`UI\ResponsiveLayout.cs`（`LogicalToDevice` :16、`ScaleAuthoredControls` :30…）与 `UI\UiDpi`（`Layout`/`Paint`/`LayoutScale`）。
- 但存在**三套并存的约定**：
  1. `UiDpi.Paint(...) / 192f`：`UI\RButton.cs:196`、`UI\RBadgeButton.cs:27`、`UI\RComboDisplay.cs:21`、`UI\LiquidCoolingLightMenu.cs:74`、`UI\RComboBox.cs:45`、`UI\RColorButton.cs:37`
  2. `UiDpi.Paint/Layout(...) / 96f`：`UI\RCheckBox.cs:101/158`、`UI\RSlider.cs:170/230`、`UI\RScrollBar.cs:65`、`UI\RComboBox.cs:64`、`UI\RColorPicker.cs:71`
  3. 直接 `DeviceDpi / 96.0`：`UI\BufferedLayoutControls.cs:41/56`
- 影响：`/192` 与 `/96` 混用（前者等价于「逻辑 px×2 再换算」，后者是标准换算）极易在非 100%/150% DPI 下算出 2× 或 0.5× 尺寸；`BufferedLayoutControls` 绕开 `UiDpi` 导致「审计模式代理 DPI」失效（`ResponsiveLayout.cs:12-14` 注释正说明审计需要单一取值口径）。
- 方向：全部改走 `UiDpi`/`ResponsiveLayout`，把 `/192` 的“×2 逻辑基线”显式表达成命名常量。**M，行为保持**（等值改写，需真机多 DPI 视觉核对）。

### B6. 不一致的错误处理（空 catch / 静默吞异常）
- 出现 **34 处** `catch { }`（正则 `catch\s*(\([^)]*\))?\s*\{\s*\}`）。
- 多数是 `Dispose`/清理路径（如 `Hardware\WaterCoolerBle.cs:163/252/259/…`、`Helpers\Logger.cs:229/236`），可接受；但下列会**静默丢失真实失败**：
  - `Display\BrightnessCommitQueue.cs:116`（亮度提交失败被吞）
  - `Gpu\NVidia\NvmlHelper.cs:105`（NVML 调用失败）
  - `Hardware\MechrevoService.cs:787`（服务错误）
  - `Pawn\CpuInfo.cs:61`、`UI\RSlider.cs:246`、`RgbForm.cs:296`、`FanCurveForm.cs:247/273`
- 另有「只记日志、无用户反馈」模式：`Settings.cs`/`MechrevoService.cs` 大量 `catch (Exception ex) { Logger.WriteLine(…); return false; }`（如 `MechrevoService.cs:1985`、`:1459`）。对硬件失败用户完全无感。
- 方向：给空 catch 补最低限度日志；对用户可见动作（切换 GPU/模式/灯效）失败时走 `ToastForm` 提示。**S/M，行为改变**（会新增提示/日志，属可接受的 UX 变化）。

### B7. 魔法数字 / 硬编码字符串
- 硬编码协议主题串在多处重复：`"Keyboard/Ctrl"`（`MechrevoHw.cs:1106`、`MechrevoService.cs:1920/1968`、`Program.cs:1410/1411/1559/1560/1587/1588`、`RgbForm.cs:307`、`FunctionVerifier.cs:208`）、`"HidLightbar/Ctrl"`、`"HidLightbar_Logo/Ctrl"`（`Program.cs:1434/1435`、`Settings.V2.cs:466/470/479/484/489/493`、`UiAuditRunner.cs:171/172`、`FunctionVerifier.cs:208/209`、`MechrevoHw.cs:1107/1108`）。→ 应集中为常量/枚举（`Program.ExternalLightChannels` 已经是一份局部清单，其它地方未复用）。
- 魔法数字：`UI\UiAuditRunner.cs:14` 的 `48`（工作区底部预留）、`UI\RComboBox.cs:45` 的 `44`、`UI\RComboBox.cs:215` 的 `4`、`UI\RScrollBar.cs:65` 的 `40/24`、`UI\RColorButton.cs` 的圆角、`Helpers\OSDBase.cs` 的 `250/350/50`、`Hardware\KeyboardRgb.cs` 的时序常量等。
- 方向：抽 `const`/`static readonly` 并集中命名。**S，行为保持**。

### B8. 其它可维护性问题
- `Hardware\MechrevoHw.cs`（3389 行、284 个 public 成员）是第二个上帝对象：HID 连接、遥测、快捷开关、GPU 超频、电容风扇曲线、诊断字符串混在一起。**L，行为保持**（按职责拆 partial）。
- `Pawn\RyzenSmu.cs`（505 行）中 `PowerLimits` 疑似超长方法（B2），且 `CanSetTDP/CanSetCoAll/CanSetThm` 三方法实现完全相同（`_cpu is not CpuCodeName.Undefined`）—— 可合并为一个能力属性。**S，行为保持**。
- `Stubs.cs`（153 行）作为「已删 ASUS 模块的占位符」仍有活调用点，属于**技术债缓冲层**：建议在每个 stub 类上加 `[Obsolete]` 或分析器规则，阻止新代码再依赖空实现。

---

## C. 清理计划（分波，按风险排序）

> 每波给出：改什么、如何验证「没改行为」、预期删除量。所有波次都**不要一次性重排文件**，按条目独立提交、独立验证。

### Wave 1 — SAFE（可证行为保持；可随小版本发布）

**W1.1 删除 `#if HARDWARE_DIAGNOSTICS` 死代码**
- 改：删 `Program.cs:243-696` 的 `#if HARDWARE_DIAGNOSTICS` 段；保留 `#if !HARDWARE_DIAGNOSTICS`（:233-241）的「已禁用」提示并去掉条件编译（改为普通代码）。
- 验证：`dotnet build` 通过；`--verify-functions/selftest/rgbtest/lctest/customtest/colortest` 命令行仍返回 exit code 2 与提示文案（现有测试 `FunctionVerifier`/`PowerWallVerifierTests`、`UiAudit*` 覆盖部分）。
- 删除量：约 **450 行**。

**W1.2 清理未用 interop 与电源 GUID 残留**
- 改：删 A11 列出的 `NativeMethods` 未用常量/GUID + `LockScreen()`/`LockWorkStation`；删 `OSDBase.cs` 中 `User32`/`Gdi32` 的 31 个零调用声明与 `AW_*` 常量；删 `DisplayNative`/`HidDeviceWin`/`NvmlHelper` 中列出的零调用声明。
- 验证：编译通过；跑 WinForms 相关 UI 测试（`UiAudit*`、`HeaderAlignmentTests`、`ToasterForm` 相关）；真机点一遍 OSD Toast、Overlay、灯效弹窗（这些正是 OSDBase 的活路径）。**保留清单在 A11，务必逐条对照「仍在用」名单。**
- 删除量：约 **180-200 行**。

**W1.3 删除未引用类型与整类死方法**
- 改：`UpdatesControl`/`MKeyControl`/`AuraSpeed`（`Stubs.cs`）、`DisplayDeviceStates`、`AsusMode`、`SpDeviceInterfaceDetailData`、`RegionFlags`；以及 A2/A3 中**非接口/非重写**的零引用方法/属性。
- 验证：`dotnet build`（编译器会兜住漏删的引用）；跑全量 `tests\MechrevoLite.Tests`。注意 A2 误报清单（`OnRenderItemCheck`/`ShowFocusCues`/`PreFilterMessage`）不要删。
- 删除量：约 **250-350 行**。
- 例外：`OfficialConsoleCatalog`（A1-8）**先确认在途改动**再动。

**W1.4 删除死资源**
- 改：`Strings.resx` + 20 个语言 resx 中 181 个未用键及对应 `Strings.Designer.cs` 生成属性；`Resources.resx` 中 56 个未用属性与 `Resources\*.png|ico` 文件。
- 验证：用脚本重跑 A9 的「除 Designer 外引用计数 = 0」；编译（缺资源会在 `Properties.Resources.X` 处报错）；跑含本地化的 UI 测试；启动应用逐个页面目视（尤其 Settings 各分组、FirstRunGuide、Donate）。**[待确认]** 先排除 `Image.FromFile` 路径加载的图片。
- 删除量：约 **1,900 行生成代码 + 181×21 条 resx 条目 + 56 个资源文件**。

**W1.5 删除「读但从不写/写但从不读」中确证为死的配置键逻辑**
- 改：仅删除**读取点连带功能已死**的键——如 `disable_osd` 的早退（连同 `ToastForm` 开关）、`fahrenheit` 常量（若确认单位不做切换）、`theme` 的 `flatTheme` 分支。**不要**盲删全部 53 个键：很多是 CLI/官方控制台路径在用。
- 验证：对每个键 grep 确认零写入；跑 `AppConfigStorageTests`、`NumericSettingTests`；真机确认 OSD/温度单位/主题行为与改动前一致。
- 删除量：约 **40-80 行**。
- **写但不读的 5 个键（`aura_mode` 等）标记 [待确认]，本波不动。**

**W1.6 清理 csproj 过期注释**
- 改：更新 FftSharp/AudioVisualizer、IntelMSR.bin 两处已过期注释。
- 验证：无行为影响，`dotnet build`。
- 删除量：约 **10 行注释**。

**Wave 1 小计：约 1,200-1,500 行手写代码 + 约 1,900 行生成代码 + 56 个资源文件 + 约 181×21 条 resx。**

### Wave 2 — RISKY（触及行为/硬件路径；独立分支、真机验证后再发）

**W2.1 处理硬编码 false 门禁**
- `Gpu\GPUModeControl.cs:417` 的 `IsUsedGPU()`：要么接真实的「GPU 占用」判据（如 NVML/进程列表），要么删除该确认对话框分支。**行为改变**（会开始弹或不弹确认框）。
- `Stubs.cs` 的 `IsAnyPeripheralConnect`/`IsDeviceReady`/`IsAuraSync` 等 `=> false`：与「外围设备面板」一并决定是删除面板还是补真实实现。**行为改变**。
- `IGpuOverclockControl.cs:24` 默认 `WritesRequireElevation => false`：确认没有非 NV 实现类后可删默认实现。**[待确认]**。
- 验证：真机切换 GPU 模式、插拔外围设备；`Gpu*Tests`、`DgpuApplicationSafetyTests`。
- 预期：不减代码甚至略增，主要消除「以为能用」的假门禁。

**W2.2 灯效下发路径收敛（B3）**
- 把 UI 侧直发（`RgbForm`/`LightForm`/`Settings.V2`）统一到 `MechrevoService`/`LightingRestoreCoordinator` 单一入口。
- 验证：`LightingRestoreCoordinatorTests`、`LightingRestoreRetryTests`、`LightingStateTests`、`KeyboardPowerRestoreTests`、`LightingSleepConsistencyTests`；真机反复熄屏/唤醒/切换电源，确认灯条不闪。
- **行为改变**（时序若处理不当会复现闪烁），必须真机。

**W2.3 事件处理器补接线或删除**
- `ButtonFnLock_Click`、`ComboKeyboard_SelectedValueChanged`：确认设计意图（附件键/键盘选择）—— 接线（行为改变，功能新增）或删除（行为保持）。
- `OnChargerEvent`：确认是否应替代现有 `PowerSetting` 分支。
- 验证：`Settings*Tests`、真机点击。

**W2.4 sync-over-async（B4）**
- 逐条把 UI 可达的 `.Wait/.Result` 改 `await`；`Thread.Sleep` 仅确认调用线程后处理。
- 验证：`SwitchConcurrencyTests`、`GpuHotSwitchRollbackTests`；真机在低配机观察界面无卡顿；**不得改变硬件时序**，否则归入「行为改变」。

### Wave 3 — NOT WORTH IT（不建议做）

- **重写 `ApConfig`/`Settings` 的配置模型**：`AppConfig` 已是成熟的静态字典 + 原子写 + 损坏恢复（`WriteAtomic`/`TryRecoverConfig`），虽有历史包袱但改动收益低于回归风险。
- **删除 `Stubs.cs` 里所有空实现**：其中仍有活调用点（盖合、会话锁、GPU 切换路径），删除需连带重写这些路径；`Stubs.cs` 头注释已明确说明这属于另一件事。
- **`src\MechrevoLite\` 是否删**：它虽不被构建，但可能是「未来 WPF 版」的种子。**先确认意图**；若只是历史遗留，删除收益 695 行 + 18 文件，但那更适合单独决策而非「清理」。
- **未使用 `using` 的逐个手删**：没有编译器 pass 时误删风险高，收益低；等 Wave 1 删完代码后用 `dotnet format ... IDE0005` 一次性处理。

---

## D. NOT COVERED / UNKNOWN

1. **未使用 `using` 的完整清单**：需要编译器/分析器（`IDE0005`/`dotnet format`）。本次只给了可疑样例，未枚举。
2. **动态/反射引用未完全覆盖**：本审计用「名称引用计数」，无法捕获
   - `Strings.ResourceManager.GetString(变量)` 形式的动态取资源（已确认源码中只有 `"mode_name_" + mode` 这类配置键拼接，但第三方/未来代码不保证）；
   - `Activator.CreateInstance`/`Type.GetType`/DI 反射构造的类型；
   - WinForms `Designer` 序列化、obfuscation 配置（`obfuscar.xml`，`src\MechrevoLiteWin\obfuscar.xml`）保留下来的名字。
   → 会低估「死」的范围；对编译器会报错的地方以 build 为准。
3. **未使用的 `designer`/`Resources.resx` 内嵌项**：只覆盖了经由 `Resources.Designer` 属性访问的资源；直接 `Image.FromFile`/路径加载的资源（如 `DonateForm.cs:83`、`qrcode1.jpg/qrcode2.jpg`）未逐一核对。
4. **读写配置键的副作用路径**：写集合基于**字面量**键名。以变量名写入的键（`DirectGpuProfileKey("...")`、`planKey`、`_configKey`、`colorField`、`Modes.cs` 的 `+ "_" + mode` 后缀键）已排除，但若存在「把键名拼成字符串再写」的路径，A6 的「写了不读」可能仍需复核。
5. **第三方目录未审**：`g-helper-main`、`watercooler-manager-main`、`official-consoles`、`_decompiled`、`release`、`artifacts`、`dist*` 等按范围排除；其中是否含被主项目间接引用的代码未验证（`MechrevoLiteWin.csproj` 无 `ProjectReference`，但可能有运行时反射加载）。
6. **无构建/运行验证**：本次未编译、未跑测试、未启动应用，因此「删除后仍可编译/运行」的结论全部是**静态推断**，必须在 Wave 1 每步用 `dotnet build` + `tests` 实测。
7. **未量化**：未给出每个删除项的精确行数（给出的是区间/估计）；`B2` 的超长方法行数为 brace 计数启发式，`RyzenSmu.PowerLimits` 的 401 很可能是字符串/注释干扰，需人工确认。
8. **git 状态**：按规则未运行任何 git 命令；无法区分「工作区未提交改动」与「历史遗留」，故 `OfficialConsoleCatalog`、`src\MechrevoLite\` 等标记为 [待确认]。
9. **本地化翻译质量/一致性** 未审（只判是否有代码引用）。
10. **性能/内存问题**（除 `Thread.Sleep`/阻塞调用外）未系统评估。
