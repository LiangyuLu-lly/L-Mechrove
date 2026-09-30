# 显卡模式多代适配：实现方案（GTX 10/16 · RTX 20 → RTX 50）

状态：beta21 已实现（G1–G10、G12；G11 随安装器任务完成）。与方案的出入：
① 热切换的服务侧判据改看热切换寄存器 `IGpuOnlyConnectionSwitch_Status`（`MechrevoHw.IgpuOnlyRegister`），
不看合成后的 `GpuMode`——后者以独显通路为准，TOGGLE_OFF 下 RB_ON 仍显示混合，永远到不了「集显」；
② G6⑨（`IGPU_CANNOT_BE_SWITCH_NOW_VISIBILITY` 查询）未做：1.2.0.0 的行为 UNKNOWN，界面也不依赖它；
③ NVRAM `OemDisplayMode` 读取（§4.3）只写诊断日志（开机一行），偏移未在硬件上核对前不参与任何判定；
④ 自动档（§10 第 13 条）仍无按钮，服务自己处于 RB_AUTO 时的客户端路径只在 50 系热切换机型上放行，并同样要求设备在位。
调研时间 2026-09-29，分支 `beta21-wip` @ `5f104a3`。
调研全程只读：没有发布任何 MQTT 控制命令，没有写 EC / NVRAM / 注册表，没有切换显卡模式。
厂商行为的完整证据链见 `docs/hardware/gpu-switching-per-generation.md`（下称「逐代调研」），
本文只保留实现要用到的结论与出处，并补充本次新增的只读核对。

## 0. 结论

1. **能发哪些动作，由本机跑的 GCU 服务版本决定，不由独显代际决定。** 我方安装器给 30/40/50 装
   GCUService **1.2.0.0**（`release\GCU-only`），给 GTX 10/16、RTX 20 装 **1.0.2.47**（`release\GCU-1020`）
   （`installer/Select-GcuPayload.ps1:60-73,242-243`）。现有 `DisplayRouteMatrix` 按「厂商原装控制台 + 原装服务」建表，
   与我方实际部署不一致，需要加一条「服务档位」轴（§3.1）。
2. **「已生效」只认硬件回读。** `Setting/Status` 的 `*_Status` 是服务存的目标值：S40 在写 NVRAM **之前**就改状态串
   （`S40 MyControlCenter/MySettingManager.cs:1229-1271`），RB_* 在 S40 只写注册表。硬件回读三件套（§4）：
   内屏接在哪块显卡上（CCD，本机已验证可用）、NVIDIA 显示设备是否在位、10/20 的 NVIDIA 驱动首选 GPU 设置（DRS）。
3. **GTX 10/16、RTX 20**：没有 MUX，官方唯一选项是 NVIDIA 控制面板的全局首选图形处理器（自动选择 / 高性能 NVIDIA）。
   我方目前完全没有这个功能（代际判成 Unknown，显卡区隐藏）。方案：两段式按钮，回读 DRS `SHIM_RENDERING_MODE`。
4. **RTX 30**：独显直连 开/关，改完必须重启。我方当前被 `CanOfferGpuModeSwitch` 挡掉（只放 40/50）。方案：放开，
   1.2.0.0 服务用它的 `DGPU_DIRECT_CONNECT_RESTART`，厂商自带服务则由我方发起 Windows 重启。
5. **RTX 40**：全部是 NVRAM 目标 + 重启（三模档：核显/混合/直连；两模档：混合/直连）。`IGPU_ONLY_CONNECT_RB_*` 在 S40
   是只写注册表的死路径。**我方现在的「集显」在 40 三模档上发的是 RB_ON，服务回显状态后我方判定成功，硬件什么都没变**——
   这是一个现存的伪功能，必须改成 `TOGGLE_IGPU` + 重启。两模档不得提供核显。
6. **RTX 50**：非热切换机型 = 三个 NVRAM 目标 + 重启；热切换机型（本机属于这类）= 独显直连（重启）+ 热切换卡片
   RB_ON/RB_OFF（不重启）。热切换是否成功必须看 NVIDIA 显示设备真的断开/恢复；2026-09-10 本机实测 RB_ON 只翻转了
   服务的软件寄存器、独显没有断开（`APP Hardware/MechrevoService.cs:1294-1298`）。
7. **代际识别有一个 bug 要先修**：`FromDeviceId` 把 `0x20..0x25` 都当 Ampere（`APP Gpu/DgpuGeneration.cs:132-146`，区间在 `:141`），
   而 `0x21xx` 是 Turing TU116（GTX 1660 Ti / 1650），GTX 16 系机器会被判成 30 系。放开 30 系切换之前必须修。
8. **本机只读核对**（§2）：1.2.0.0 服务；ItemSupport 声明 MUX + 核显 + 热切换；内屏 `NE160QDM-NZL` 接在 Intel 核显上
   （= 当前混合）；NVRAM 读取需要管理员（非提权 Win32 1314），本次没读到 `OemDisplayMode`。

## 1. 口径

### 1.1 证据标记

沿用逐代调研的键：`UC` gcuu-console（10/16/20 控制台）、`US` gcuu-service（1.0.2.47）、`S30` gcu30-41747、
`S40` gcu40-51751-27（真实方法体）、`S40B` gcu40-51749（仅声明）、`C50` ccuwinui（50 系控制台，真实方法体）、
`S50` = `release/GCU-only/AiStoneService/MyControlCenter/GCUService.exe` 1.2.0.0（混淆，只能字节扫描）、`APP` = `src/MechrevoLiteWin/`。
新增：`L1020` = `release/GCU-1020/UniwillService/MyControlCenter/`；`L` = 本次本机只读实测。
标记：**P** 可读方法体/实测证实；**D** 仅声明；**B** 二进制字符串/常量扫描；**I** 推断；**UNKNOWN**。

### 1.2 「生效」的四个状态

| 状态 | 判据 | 界面 |
|---|---|---|
| `Confirmed` | 硬件回读（§4）与目标一致 | 正常高亮 |
| `PendingReboot` | NVRAM 目标已下发（服务状态为目标；提权时再加 NVRAM 回读），重启后才生效 | 高亮目标 + 行尾「重启生效」 |
| `NotApplied` | 超时，或回读与目标不一致（含重启后仍是旧路由） | 高亮**实际**路由 + 失败提示 |
| `Unknown` | 回读不可用（内屏目标不存在、CCD/CfgMgr 失败） | 不高亮任何段，tooltip 说明原因 |

## 2. 本机只读核对（2026-09-29，耀世 16 Ultra，BIOS_PROJECT_ID=IDY）

| 项目 | 方法 | 结果 |
|---|---|---|
| GCU 服务 | `HKLM\SYSTEM\CurrentControlSet\Services\GCUBridge\ImagePath` + 文件版本 | `C:\Program Files\L-Mechrevo\GCU\AiStoneService\GCUBridge.exe` 1.0.1.10；`MyControlCenter\GCUService.exe` **1.2.0.0**（17 110 576 B）；`UEFI_Firmware.dll` 1.0.0.7 → 档位 `Modern12` |
| ItemSupport | `HKLM\SOFTWARE\OEM\GamingCenter2\ItemSupport` | `DGpuDirectConnectionSupport=1`、`IsNvGpu=1`、`IsAMDPlatform=0`、`APVersionCheck=24`、`iGPUModeOnlySupport=1`、`TypeCSupport=0` |
| 热切换键 | `…\MySetting\GpuConfig` | `GpuHotSwapSwitchSupport=1`、`lgpuHotSwapSwitchStatus=1` → 按 C50 规则 `IsIGPUHotSwap=true`（热切换布局） |
| MySetting | 同上 | 没有 `IGPUonly` / `IGPUautoSupport` / `DGpuPowerManagement`（1.2.0.0 没写 S40 的这几个键） |
| Setting/Status | 2026-09-28 只读基线帧（`.omo/evidence/ec-dumps/beta21-baseline-ac.txt`） | `DGpu=NV_CTRL_PANEL_AUTOSELECT`；`DiscreteGpuDirectConnectionSwitch_Status=…TOGGLE_OFF`，`_Support=Support`；`IGpuOnlyConnectionSwitch_Status=…RB_OFF`，`_Support=Enable` |
| 内屏接线 | `QueryDisplayConfig(QDC_ALL_PATHS)` + `DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME` | 目标 `outputTechnology=0x80000000`（INTERNAL）`NE160QDM-NZL` → 适配器 `VEN_8086&DEV_7D67`（Intel）→ **当前路由 = 混合**；外接 `Mi Monitor`（DP 外接）在 `VEN_10DE&DEV_2C19` |
| 活动路径 | `QDC_ONLY_ACTIVE` | 只有外接屏一条（内屏此刻没点亮）→ **只看活动路径会判不出路由，必须用 ALL_PATHS** |
| NVIDIA | `nvidia-smi`、NvAPIWrapper DRS 只读 | RTX 5080 Laptop，驱动 616.64，PCI `0x2C1910DE`；DRS 全局配置 `Base Profile` 用户设置 0 项，SHIM 三项都未设置（= 驱动默认 AUTO_SELECT） |
| NVRAM | `GetFirmwareEnvironmentVariableW("UniWillVariable", {9f33f85c-…})`，非提权 | 失败 **Win32 1314**（缺 `SeSystemEnvironmentPrivilege`）→ `OemDisplayMode` 未读到 |
| 字节扫描 | 见下 | S50：`NV_CTRL_PANEL_*`、`OemDisplayMode`、`DGPU_DIRECT_CONNECT_TOGGLE_ON`、`IGPU_ONLY_CONNECT_RB_ON` 字面量都在；有 ModuleRef `NVControlSetting`，但找不到 `SetNVCtrlPanel` 导入名。L1020 `GCUService.exe`：`NV_CTRL_PANEL_*`、`SetNVCtrlPanel`、`NVControlSetting.dll` 在；`OemDisplayMode` / `DGPU_DIRECT_*` / `IGPU_ONLY_*` 都不在；该载荷没有 `UEFI_Firmware.dll`。两份 `NVControlSetting.dll` 都只导出 `InitNV`、`SetNVCtrlPanel`，文件里有 32 位常量 `0x10F9DC80`@0x2024、`0x10F9DC81`@0x209E、`0x10F9DC84`@0x20D9 |

最后三个常量是 NVIDIA DRS 的 `SHIM_MCCOMPAT_ID` / `SHIM_RENDERING_MODE_ID` / `SHIM_RENDERING_OPTIONS_ID`，即 NVIDIA 控制面板
「首选图形处理器」背后的 Optimus 设置（取值见 [NVIDIA/nvapi NvApiDriverSettings.h](https://github.com/NVIDIA/nvapi/blob/main/NvApiDriverSettings.h)：
`INTEGRATED=0x0`、`ENABLE=0x1`、`AUTO_SELECT=0x10`（默认）、`OVERRIDE_BIT=0x80000000`）。这是 10/20 系回读方案的依据（§4.4）。

## 3. 两条判定轴

### 3.1 服务档位 `GcuServiceTier`（新增）

| 档位 | 判据 | 可发的显卡动作 | 出处 |
|---|---|---|---|
| `Legacy1020` | GCUBridge 镜像路径含 `L-Mechrevo` 且在 `\GCU\UniwillService\`，`GCUService.exe` 版本 1.0.2.47，同目录无 `UEFI_Firmware.dll` | `NV_CTRL_PANEL_AUTOSELECT` / `_HIGHPERFORMANCE` | `US Define/ServCMD.cs:242,244`；`L` 字节扫描 |
| `Modern12` | 路径含 `L-Mechrevo` 且在 `\GCU\AiStoneService\`，版本 ≥ 1.2 | `DGPU_DIRECT_CONNECT_TOGGLE_ON/OFF/IGPU`、`_RESTART`、`IGPU_ONLY_CONNECT_RB_ON/OFF/AUTO`、`IGPUONLYCONNECTIONSWITCH_STATUS`、`IGPU_CANNOT_BE_SWITCH_NOW_VISIBILITY`（B，行为 UNKNOWN） | 逐代调研 §3.4 |
| `Foreign` | 厂商自己装的服务（路径不含 `L-Mechrevo`，`APP Helpers/GcuCoexistence.cs:63-69`） | 只用所有 30/40 服务都有的交集 `TOGGLE_ON/OFF`；不发 `RESTART` / `TOGGLE_IGPU` / RB_*，重启由我方做 | `S30 Define/ServCMD.cs:352,354`；`S40B Define/ServCMD.cs:353,355` |
| `Unknown` | 服务未注册 / 读不到版本 | 无 | — |

`Foreign` 不细分：厂商 30 服务与两个 40 档服务都是 1.0.2.70，只能靠文件大小区分（11 983 608 / 14 447 864 B），太脆弱。

### 3.2 机型能力位（来源不变，规则收紧）

| 能力 | 运行时（优先） | 冷启动兜底 | 官方规则 | 我方现状 |
|---|---|---|---|---|
| MUX（有独显直连） | `DiscreteGpuDirectConnectionSwitch_Support=Support`（S40 由 `OemDisplayMode!=255` 得出，`MySettingManager.cs:1593-1600`） | `ItemSupport\DGpuDirectConnectionSupport`（`APP Hardware/MechrevoDeviceCapabilities.cs:335`） | C50 显卡页可见 = `IsNvGpu==1 && DGpuDirectConnectionSupport==1`（`C50 CCUWinUI.ViewModels/NavigationPageViewModel.cs:976-980`） | `SupportsDgpuDirect`（`APP Hardware/MechrevoHw.cs:493`） |
| 核显 NVRAM 目标 | `IGpuOnlyConnectionSwitch_Support=Enable`（S40 WMI 命令 3/n=3 == 85，`MySettingManager.cs:1761-1771`） | `iGPUModeOnlySupport`（我方缺省 0，`MechrevoDeviceCapabilities.cs:311`） | C50 缺省 **1**、`customizeTarget==49` 关闭（`GpuSettingPageViewModel.cs:1429,1452-1455`） | `SupportsIgpuOnly`（`MechrevoHw.cs:495`），另兼作 40 档判据（`:500`） |
| 热切换 | — | `GpuHotSwapSwitchSupport && lgpuHotSwapSwitchStatus`（`MechrevoDeviceCapabilities.cs:366`） | 另需 `APVersionCheck>23`、`IsNvGpu`、`iGPUModeOnlySupport`、`customizeTarget!=49`（`GpuSettingPageViewModel.cs:1435-1459`） | `SupportsGpuHotSwap`（`MechrevoHw.cs:496`）；去掉 APVersionCheck 是业主确认过的（`APP Hardware/HardcodeDispositions.cs:46-48`） |
| 10/20 首选 GPU | `Setting/Status.DGpu` 是可识别的 `NV_CTRL_PANEL_*` | 无（GamingCenterU 机器没有 ItemSupport） | 控制台门控 `GpuIsNvidia` / `GpuGridSupport`，方法体被破坏（`UC UWP_Refactor.ViewModels/SettingViewModel.cs:790,806,818`） | 只解析不下发（`MechrevoHw.cs:323-327,2093-2095`） |

### 3.3 独显代际修正（`Gpu/DgpuGeneration.cs`）

- 新增 `DgpuGenerationKind.Gen1020`：营销名与安装器用同一正则 `GTX\s*1[06][5-8]0|RTX\s*20[5-8]0|MX\s*[1-3][1-5]0`
  （`installer/Select-GcuPayload.ps1:123`）；device-id 高字节 `0x1B–0x1F`、`0x21` → Gen1020。
- Ampere 区间收窄到 `0x22–0x25`（`0x20` 是数据中心 GA100，`0x21` 是 Turing TU116）。现状 `0x20..0x25`
  （`DgpuGeneration.cs:141`）会把 GTX 1660 Ti（`DEV_2191`，名字不含 RTX，走 device-id 分支）判成 Gen30。
- RTX 2050 是 GA107（高字节 0x25），按名字判成 Gen1020（名字优先，`DgpuGeneration.cs:97-107`），与安装器选 1020 载荷一致。
- 已持久化的错误结果不会自愈：`DgpuGenerationStore` 只在指纹变化时重判（`Gpu/GpuGenerationProvider.cs:31-40,195-196`）。
  新增存储键 `dgpu_generation_rules=2`，不等即重判；`Load` 接受 Gen1020（现只收 30/40/50，`:53`）；`IsResolved`（`DgpuGeneration.cs:68`）同步。

## 4. 硬件回读 `GpuRouteProbe`（新增，只读）

### 4.1 内屏接在哪块显卡上（MUX 路由）

`QueryDisplayConfig(QDC_ALL_PATHS=1)`，找 `outputTechnology` 为 `INTERNAL(0x80000000)` / `DISPLAYPORT_EMBEDDED(11)` /
`LVDS(6)` / `UDI_EMBEDDED(13)` 且 `targetAvailable` 的目标，用 `DisplayConfigGetDeviceInfo(type=4 GET_ADAPTER_NAME)`
取适配器设备路径：含 `VEN_10DE` → 独显驱动内屏（直连）；`VEN_8086` / `VEN_1002` → 核显驱动内屏。

- 结构体已在 `APP Display/DisplayNative.cs:21-44,92,231-345`；`ScreenNative.FindLaptopScreen`（`Display/ScreenNative.cs:59`）
  判内屏的逻辑（`:11-18`）可复用，但它会写 `internal_display` 配置，新方法不要复用写配置那部分。
- 必须用 ALL_PATHS：本机内屏未点亮时活动路径里没有它（§2）。
- 内屏目标不存在（BIOS 关内屏、外接独显启动）→ `Unknown`。

### 4.2 NVIDIA 显示设备是否在位（核显-only / 热切换）

`CM_Get_Device_ID_List(filter="PCI\VEN_10DE", CM_GETIDLIST_FILTER_PRESENT|CM_GETIDLIST_FILTER_CLASS)`（类 GUID
`{4d36e968-e325-11ce-bfc1-08002be10318}`）+ `CM_Get_DevNode_Status`：`DN_STARTED` → `Present`；问题码 22（已禁用）→ `Disabled`；
没有在场节点 → `Absent`。

- **不得用 NVML / NVAPI / LHM**：它们会唤醒独显，还会占住句柄让热切换失败（§10 第 9 条）。
- 不能复用 `GpuAdapterReader.ReadFromRegistry`（`Gpu/GpuGenerationProvider.cs:84-117`）：注册表类键包含不在场的设备。

### 4.3 NVRAM `OemDisplayMode`（可选，仅提权时）

`GetFirmwareEnvironmentVariableW("UniWillVariable", "{9f33f85c-13ca-4fd1-9c4a-96217722c593}")`，先
`AdjustTokenPrivileges` 打开 `SeSystemEnvironmentPrivilege`。偏移 **0x62**（按 `S40 MyControlCenter/NVRAM_STRUCT.cs`
默认顺序布局计算，结构体共 188 B；未在硬件上核对）。解码：Intel 2=直连、4=混合、1=核显；AMD 1/0/2；255=不支持
（`S40 MySettingManager.cs:1593-1628`）。

- 应用清单是 `asInvoker`（`APP app.manifest:7`），手动启动时通常不提权 → 这一项只能当加分证据，不能当必需判据。
- 与 IL 守卫兼容：`tests/MechrevoLite.Tests/FirmwareWriteGuard.cs:18-28` 只禁 `Set*/Write*` 调用和**完整等于**
  `"OemDisplayMode"` 的字面量。读取器只调 `GetFirmwareEnvironmentVariableW`，偏移用常量，不写这个字面量。

### 4.4 10/20 首选 GPU（NVIDIA 驱动 DRS）

NvAPIWrapper.Net 0.8.1.101 已是依赖（`APP MechrevoLite.csproj:109`）：
`DriverSettingsSession.CreateAndLoad()` → `CurrentGlobalProfile.GetSetting(0x10F9DC81u)`（`SHIM_RENDERING_MODE`）：

| 读到 | 含义 |
|---|---|
| `null`（未设置） | 驱动默认 `AUTO_SELECT` |
| `(v & 0x3) == 1` | 高性能 NVIDIA |
| `v & 0x10` 且 `(v & 0x3) == 0` | 自动选择 |
| `(v & 0x3) == 0` 且无 `0x10` | 集成显卡（官方无此选项，显示为 Unknown） |

只允许 `CreateAndLoad` / `GetSetting`，禁止 `SetSetting` / `Save`（加 IL 守卫）。本机为 50 系、未设置任何项，读法已验证可用；
`NVControlSetting.dll` 实际写哪几项、写进哪个配置是 UNKNOWN，需要 10/20 真机确认（§10 第 6 条）。

### 4.5 由回读推出当前路由

| 内屏适配器 | NVIDIA 设备 | 路由 |
|---|---|---|
| 独显 | — | 直连 |
| 核显 | `Present` | 混合 |
| 核显 | `Disabled` / `Absent` | 核显-only（NVRAM 目标或热切换） |
| `Unknown` | 任意 | `Unknown` |

## 5. 逐代方案

### 5.1 GTX 10 / GTX 16 / RTX 20（GamingCenterU 1.1.0.49 + GCUService 1.0.2.47）

- **官方**：设置页一个「独立显卡」开关，说明文字「启用时，设定显卡电源管理模式在最大效能」
  （逐代调研 §3.1，`UC GamingCenterU.Resources.zh-cn.resx:285,41`）。没有 MUX、没有核显-only。
- **命令**（I，方法体被破坏；动作名 P）：`Setting/Control {"Action":"NV_CTRL_PANEL_HIGHPERFORMANCE"}`（开）/
  `{"Action":"NV_CTRL_PANEL_AUTOSELECT"}`（关）。出处：`UC UWP_Refactor.Models/SettingAciton.cs:19-20`、
  `US Define/ServCMD.cs:242,244`、主题 `US MyControlCenter/Topic.cs:43`；服务处理器 `US MyControlCenter/MySettingManager.cs:158,346,351,403`；
  P/Invoke `NVControlSetting.dll!SetNVCtrlPanel(byte)`（0=AUTOSELECT，1=HIGHPERFORMANCE，`US MyControlCenter/NVControlSetting.cs:14-21`）。
- **实际写入**（B）：`L1020 NVControlSetting.dll` 内含 DRS `SHIM_*` 三个设置 id（§2）→ NVIDIA 驱动全局首选图形处理器。
- **重启 / 电源**：不需要重启（I：驱动配置，对之后启动的程序生效）；无交流电限制。
- **生效判据**：DRS 回读（§4.4）；`Setting/Status.DGpu`（`US Define/ClientCMD.cs:134`）只作辅助。
- **我方现状**：`DGpu` 只解析不下发（`APP Hardware/MechrevoHw.cs:323-327,2093-2095`）；10/20 代际 = Unknown
  （`Gpu/DgpuGeneration.cs:13-14,113-129`）→ 显卡区隐藏并提示「无法识别独显代际」（`APP Settings.cs:2969-2975`）。
  安装器已能选 1020 载荷（`installer/Select-GcuPayload.ps1:70-73`），但两个 `.iss` 只打包 `payload\50`
  （`installer/L-Mechrevo.iss:131`、`installer/L-Mechrevo-GCU.iss:82`），所以 10/20 机器目前拿不到 1.0.2.47 服务。
- **改动**：G2、G4、G5、G6、G7、G9；前置依赖 G11（安装器）。

### 5.2 RTX 30（控制台 4.17.47.13，服务 1.0.2.70）

- **官方**：独显直连 开/关（关 = Optimus 混合），文案提示需重启；没有核显-only、没有 RESTART 动作，用户手动重启
  （`.omo/evidence/g30-console-decompile.md:61-70,97-101`）。
- **命令**：`{"Action":"DGPU_DIRECT_CONNECT_TOGGLE_ON"}` / `_OFF`（`S30 Define/ServCMD.cs:352,354`；字段名按家族推断 I）。
- **写入**：NVRAM `OemDisplayMode`（`S30 MyControlCenter/NvramVariable.cs:91-101`、`NVRAM_STRUCT.cs:133`）。编码沿用 S40
  （Intel 直连 2 / 混合 4，AMD 1 / 0；I）。换成 1.2.0.0 服务后同一写法（I，S50 含 `OemDisplayMode` 字面量）。
- **重启**：必须。`Modern12` 用 `DGPU_DIRECT_CONNECT_RESTART`（S50 B）；`Foreign` 没有这个动作 → 我方 `SystemRestart.RequestRestart`。
- **生效判据**：重启前 `PendingReboot`（服务状态 + 可选 NVRAM）；重启后看内屏适配器（§4.1）。
- **我方现状**：`CanOfferGpuModeSwitch` 只放 Gen40/Gen50（`APP Hardware/MechrevoHw.cs:529-535`）；Gen30 行没有 RESTART
  （`Gpu/DisplayRouteMatrix.cs:106-127`）→ `CreateGpuRestartTargetPayloads` 对 Gen30 返回空（`Hardware/MechrevoService.cs:1224-1233`）。
  测试把这一点钉死了（`GpuCapabilityGatingFailTests.cs:36`、`GpuSwitchCommandTests.cs:82`）。
- **改动**：G1、G2（先修代际）、G5、G6、G7、G9、G12。

### 5.3 RTX 40 三模档（5.17.51.27 / 5.17.51.34）

- **官方**：40 控制台是 UWP .NET Native，没有被反编译；UI 形态按服务词汇推断（I）：直连 / 混合 / 核显，全部重启。
- **命令**（P，`S40 MyControlCenter/MySettingManager.cs`）：`TOGGLE_ON` :663-668、`TOGGLE_OFF` :774-777、`TOGGLE_IGPU` :780-785；
  `DGPU_DIRECT_CONNECT_RESTART` :492-495 → `shutdown /r /t 0`（:1326-1335），立即无条件重启；
  NVRAM 编码 :1229-1271；初始化 :1593-1628（255 → NotSupport）。
- **RB_*（不要用）**：`IGPU_ONLY_CONNECT_RB_ON/OFF/AUTO` :582-649 → `UserSetIGgpuONLYConnectionSwitch` :1281-1322 只写
  `MySetting\IGPUonly` 注册表，`InitIGPUSetting` 是空方法（:83-86）；WMI 写入只在没人订阅的定时器里（:87-116）。
- **重启 / 电源**：每次 MUX 变更都要重启；无交流电限制。
- **生效判据**：同 30；核显-only 重启后是否把独显从 PCI 上隐藏是 UNKNOWN（影响 §4.5 的第三行，§10 第 4 条）。
- **我方现状（伪功能）**：40 三模行放行 RB_ON/RB_OFF（`Gpu/DisplayRouteMatrix.cs:129-154`）→ `CanOfferIgpuOnly` 为真
  （`Hardware/MechrevoHw.cs:517-518`）→ 「集显」按钮可见；点击后 `GpuSwitchPolicy` 给 `HotSwitch`（传的是 `CanOfferIgpuOnly`，
  `APP Settings.cs:4496-4501`）→ `SwitchGpuMode` 在非热切换机型上走 `CreateGpuSwitchPayload`，因 `supportsIgpuOnly` 为真而发
  **RB_ON**（`Hardware/MechrevoService.cs:1115-1122`）→ S40 回显 `IGPU_ONLY_CONNECT_RB_ON` → `ResolveGpuModeStatus` 映射成集显
  （`MechrevoHw.cs:786-791`）→ `TargetReached` 成立（`MechrevoService.cs:1331-1343`）→ 报成功。重启路由的集显已经正确地用
  `TOGGLE_IGPU`（`MechrevoService.cs:1245-1247`）。
- **改动**：G5（40 行去掉 RB_*）、G6、G7、G8（40 上集显一律走重启）。

### 5.4 RTX 40 两模档（5.17.49.19）

- **官方**：服务只声明 `TOGGLE_ON/OFF`（`S40B Define/ServCMD.cs:353,355`，`MyControlCenter/MySettingManager.cs:260-263`），
  没有 RESTART、没有核显。
- **判档**：沿用 `ThreeMode = SupportsIgpuOnly`（`Hardware/MechrevoHw.cs:500`，MQTT `IGpuOnlyConnectionSwitch_Support` 优先）。
- **我方现状**：两模行放行 `TOGGLE_IGPU` + `RESTART`（`Gpu/DisplayRouteMatrix.cs:156-181`）；UI 因 `CanOfferIgpuOnly` 为假不显示集显，
  但 `CanSwitchGpuMode(GpuIGpu)` 仍为真（`MechrevoHw.cs:719-721`），托盘/自动流程理论上可达。
- **改动**：G5 去掉 `TOGGLE_IGPU`；RESTART 改由档位决定（`Modern12` 有，`Foreign` 无）。这类 BIOS 是否接受核显目标 UNKNOWN，不提供。

### 5.5 RTX 50 非热切换机型

- **官方**（P）：独显直连 / 混合输出 / 核显模式（核显仅 `IGPUModeSupport`）三个 NVRAM 目标（`C50 WinUIGallery.ViewModels/GpuSettingPageViewModel.cs:186-275`，
  可见性 :1091-1147）。点击只改本地值，旧值不是 -1 且不同于目标时弹「请重启以使设置生效」；确认后发送
  （`C50 WinUIGallery.Pages/GpuSettingPage.cs:1534-1573`）：直连 = `TOGGLE_ON` + `RB_OFF{SetToWMIEC:"OK"}` + `TOGGLE_ON`；
  混合 = `TOGGLE_OFF`；核显 = `TOGGLE_IGPU`；然后 `Task.Delay(800)` + `DGPU_DIRECT_CONNECT_RESTART`（:1575-1580）；取消则回滚本地值（:1586-1596）。
- **回读**：页面激活发 `GETSTATUS`，`DiscreteGpuDirectConnectionSwitch_Status` → 0/1/2（`GpuSettingPageViewModel.cs:1345-1357`）。
- **我方现状**：重启路由与官方一致（`Hardware/MechrevoService.cs:1236-1241`）；两处自创：自动档在重启前发 RB_AUTO（`:1248-1253`）、
  无 MUX 时用 RB_OFF/RB_ON + RESTART（`:1242-1247`），逐代调研 §6 #3、#4。
- **改动**：G7（删两处自创）、G9、G12。

### 5.6 RTX 50 热切换机型（本机）

- **官方**（P）：`IsIGPUHotSwap` 为真时隐藏 NVRAM 的「核显」「混合」按钮，只留「独显直连」，另显示热切换卡片
  （`GpuSettingPageViewModel.cs:1091-1135`；卡片内有与 `IsHotSwapHybridMode` 双向绑定的混合项，`GpuSettingPage.cs:1258`）。
  卡片三项（官方文案「核显 / 混合 / 自动」）：
  - RB_ON `{"Action":"IGPU_ONLY_CONNECT_RB_ON","SetToWMIEC":"OK"}`，成功 = `CheckDGpuStatusforIGpuOnlyOnSuccess==2`（:293-300）；
  - RB_OFF 同形，成功 `==1`（:357-364）；RB_AUTO 无 `SetToWMIEC`，成功按 `IsAC` 取 1/2（:421-440）；
  - 每 2 s 检查，`count>60` 放弃，`count%4==0` 重发；超时发 `{"Action":"IGPUONLYCONNECTIONSWITCH_STATUS","Status":<旧值>}` 回滚并显示「当前无法切换」。
- **不需要重启**；前提是独显空闲（S40 要求 NV HD Audio 就绪，`S40 MyControlCenter/WMIEC.cs:374-414`）；无交流电限制（只有 RB_AUTO 的成功判据看 AC）。
- **我方现状**：三按钮 集显/标准/直连；标准→集显走热切换（`Hardware/GpuSwitchPolicy.cs:37-40`，`MechrevoService.cs:1319-1343`），
  成功判据只看服务回报（`Gpu/IgpuOnlySemantics.cs:20-31`）；超时回滚与官方一致（`MechrevoService.cs:1482-1493`）。
  本机 2026-09-10 实测：RB_ON 只翻转软件寄存器，独显没断开，重启后仍是混合（`MechrevoService.cs:1294-1298`）。
  独显占用预检 `PrepareDgpuApplicationsForHotSwitchAsync` 写好了但没有调用方（`APP Settings.cs:4530`）。
- **改动**：G3（设备在位探测进确认谓词）、G6、G7、G8、G9。「自动」档见 §10 第 13 条（需业主决定）。

## 6. 改动清单

| # | 文件 / 函数 | 做什么 |
|---|---|---|
| G1 | 新增 `Gpu/GcuServiceTier.cs`：`enum GcuServiceTier`、`static class GcuServiceTierProbe { Detect(string? bridgeImagePath, Func<string, Version?> fileVersion, Func<string,bool> fileExists); Current(); Invalidate(); }` | 按 §3.1 判档。镜像路径用 `GcuCoexistence.ReadServiceImagePath(VendorServiceName)`（`Helpers/GcuCoexistence.cs:139`）；`GCUService.exe` 在 `<bridge 目录>\MyControlCenter\`。进程内缓存，MQTT 重连时 `Invalidate()` |
| G2 | `Gpu/DgpuGeneration.cs`：`DgpuGenerationKind` 加 `Gen1020`；`FromMarketingName`、`FromDeviceId`、`DgpuIdentity.IsResolved`；`Gpu/GpuGenerationProvider.cs`：`DgpuGenerationStore.Load/Save` 加规则版本键 | 按 §3.3。版本不等即重判 |
| G3 | 新增 `Gpu/GpuRouteProbe.cs`：`PanelAdapterKind ReadInternalPanelAdapter()`、`DgpuPresence ReadDgpuPresence()`、`byte? TryReadOemDisplayModeElevated()`；纯函数 `GpuRouteInference.Infer(PanelAdapterKind, DgpuPresence)`、`DecodeOemDisplayMode(byte, bool isAmd)`；`Display/DisplayNative.cs` 补 `DISPLAYCONFIG_ADAPTER_NAME` 与 `GET_ADAPTER_NAME=4` | 按 §4.1–4.3。探测放后台线程、限时 500 ms、失败返回 Unknown；全文件不引用 NVML/NVAPI/LHM |
| G4 | 新增 `Gpu/NvPreferredGpu.cs`：`enum NvPreferredGpu { Unknown, AutoSelect, HighPerformance, Integrated }`、`Decode(uint?)`、`FromServiceStatus(string?)`、`ReadFromDriver()` | 按 §4.4；只读 DRS |
| G5 | `Gpu/DisplayRouteMatrix.cs` | ① 加常量 `NvCtrlPanelAutoSelect/HighPerformance` 并进 `KnownActions`（:262-265）；② 新增 Gen1020 行（仅 `NV_CTRL_PANEL_*`，RESTART/iGPU/热切换 = ProvenAbsent，出处 = §2 字节扫描）；③ 40 三模行删 `IgpuOnlyOn/Off`，说明改为「S40 注册表死路径」并按逐代调研 §7 订正出处；④ 40 两模行删 `ToggleIgpu`；⑤ `Restart` 从各行词汇中移出，改由档位给出；⑥ 新重载 `DisplayRoutePolicy.AllowsAction(generation, action, threeMode, GcuServiceTier tier, bool hotSwap)`：`Legacy1020` 只放 Gen1020 的 `NV_CTRL_PANEL_*`；`Modern12` = 行词汇 +（行含 `TOGGLE_ON` 时）`RESTART`，RB_* 仅 `Gen50 && hotSwap`；`Foreign` = 行词汇 ∩ {`TOGGLE_ON`,`TOGGLE_OFF`}；`Unknown` = 空。旧重载保留给测试，生产调用全部换新 |
| G6 | `Hardware/MechrevoHw.cs` | ① `ServiceTier` 属性（连接成功时取 `GcuServiceTierProbe.Current()`）；② `CanOfferGpuModeSwitch`（:529-535）= Gen30/40/50 ∧ `SupportsDgpuDirect` ∧ 档位 ∈ {Modern12, Foreign}（没有 MUX 就没有显卡行，同 C50）；③ 新 `CanOfferIgpuMuxTarget` = `SupportsDgpuDirect` ∧ 放行 `TOGGLE_IGPU` ∧ ((Gen40 ∧ ThreeMode) ∨ (Gen50 ∧ `SupportsIgpuOnly` ∧ ¬热切换))；④ `CanOfferGpuHotSwap`（:521-523）= Gen50 ∧ 档位 Modern12 ∧ `SupportsGpuHotSwap` ∧ `Capabilities.NvidiaGpu`；⑤ `CanOfferIgpuOnly` 的调用方全部改用 ③/④；⑥ `CanSwitchGpuMode`（:714-730）按 §7 重写；⑦ 新 `CanOfferNvPreferredGpu` = 档位 Legacy1020 ∧ Gen1020 ∧ `NvPreferredGpu.FromServiceStatus(NvControlPanelPreference) != Unknown`；⑧ `CheckDGpuStatusforIGpuOnlySwitch` 的 85/170 映射（:2150-2161）删除：它是 S40 只在 `IGPU_ONLY_CHECK_DGPU_SUPPORT` 之后才发的就绪字节，不是切换结果（逐代调研 §6 #7）；⑨ 热切换机型首次进入混合时发一次 `{"Action":"IGPU_CANNOT_BE_SWITCH_NOW_VISIBILITY"}`（C50 :1392-1399），让 `IgpuSwitchBlocked`（:2163-2166）有值 |
| G7 | `Hardware/MechrevoService.cs` | ① `CreateGpuRestartTargetPayloads`（:1224-1262）加参数 `GcuServiceTier tier`，返回 `GpuRestartRoute(IReadOnlyList<Dictionary<string,object>> Payloads, bool ServiceRestart)`：直连 = Gen50 三连（官方原样）、Gen30/40 只发 `TOGGLE_ON`；混合 = `TOGGLE_OFF`；核显 = `TOGGLE_IGPU`（需 `CanOfferIgpuMuxTarget`）；自动 = 空（删 RB_AUTO 自创）；无 MUX = 空（删 RB 兜底）；`ServiceRestart = tier == Modern12`；② `PublishGpuRestartAsync`（:1190-1222）：`ServiceRestart` 为假时发完载荷等 800 ms 后返回新结果 `GpuRestartRequestOutcome.RequiresAppRestart`，不发 RESTART；③ `SwitchGpuMode`（:1299-1474）：热切换只在 `CanOfferGpuHotSwap` 时走，`TargetReached` 追加 `GpuRouteProbe.ReadDgpuPresence()`（RB_ON 要 `Disabled/Absent`，RB_OFF 要 `Present`），与 `GETSTATUS` 同一 2 s 节拍；非热切换的「Direct」RB 路径删除；自动档「接受当前状态」分支（:1429-1437）删除或同样要求探测；④ 新 `Task<GpuApplyResult> SetNvPreferredGpuAsync(NvPreferredGpu target)`：只在 `CanOfferNvPreferredGpu` 时发 `NV_CTRL_PANEL_*`，每 500 ms 读 DRS，5 s 内一致 → Confirmed，否则 NotApplied；⑤ 热切换前若悬浮窗开着，先 `HardwareControl.DisableLocalMonitoring()`（`HardwareControl.cs:68-80`）并 `NvmlHelper.Shutdown()`，结束后恢复 |
| G8 | `Hardware/GpuSwitchPolicy.cs`：`Resolve`（:22-43） | 参数 `supportsHotSwap` 由调用方传 `CanOfferGpuHotSwap`（现为 `CanOfferIgpuOnly`，`Settings.cs:4500`）。规则：涉及直连或核显 NVRAM 目标的一律 `Restart`；标准↔集显仅热切换机型为 `HotSwitch`，否则 `Restart`；删除非热切换的 `Direct` 路由（10/20 首选 GPU 不经过这里） |
| G9 | `Settings.cs` 与 `Gpu/GPUModeControl.cs` | ① `RefreshDeviceCapabilities`（`Settings.cs:2842-2848`）按 §7 算出 `GpuRowLayout { Hidden, NvPreference, Mux2, Mux3, HotSwap }`；② `VisualiseGPUButtons`（:4951-4974）改为 `VisualiseGpuRow(GpuRowLayout)`，复用 buttonEco/buttonStandard/buttonUltimate，NvPreference 布局把 buttonStandard/buttonUltimate 改文案为「自动选择 / 独显优先」；③ 三个点击处理（:4452-4466）先按布局分派，NvPreference → `SetNvPreferredGpuAsync`；④ `SwitchGpuModeFromUi`（:4473-4527）：热切换前调用现成的 `PrepareDgpuApplicationsForHotSwitchAsync`（:4530，热切换机型上它的「改用重启」选项改为取消），`RequiresAppRestart` → `SystemRestart.CaptureUserConfirmation()` 已在确认框里拿到后调 `SystemRestart.RequestRestart("gpu route", …)`（`Helpers/SystemRestart.cs:33,52`）；⑤ `ShowGpuRestartPromptAsync`（:4648-4705）文案进资源，另存 `gpu_restart_target`；⑥ `VisualiseGPUMode`（:5011 起）高亮**回读到的实际路由**，挂起目标另标「重启生效」（:4785 的字面量进资源）；⑦ 托盘菜单（:3887-3918）同一布局；⑧ `GPUModeControl.InitGPUMode`（`Gpu/GPUModeControl.cs:24-53`）改用同一套能力判定 |
| G10 | `Gpu/IgpuOnlySemantics.cs` | 成功判据拆成两步：服务回报（现 `IsSuccess`，:30-31）+ 设备在位（G3）；`PollLimit=61`（:15）保持 |
| G11 | 安装器（依赖项，不属本文实现） | `.iss` 打包 `payload\1020` 并按 `Select-GcuPayload.ps1` 选择；否则永远出现不了 `Legacy1020`（beta21 任务 5） |
| G12 | 新增 `Gpu/GpuRestartVerifier.cs` | 启动时若 `gpu_restart_pending` 因重启被清掉（判定在 `Settings.cs:4628-4640`），读 `gpu_restart_target` 与 G3 回读比较：一致 → 提示已生效；不一致 → 提示未生效并以实际路由为准；回读 Unknown → 不提示、只记日志。结果写日志 `GPU route verify: target=… actual=…` |

## 7. 界面显示规则

显卡行是一行分段按钮（G-Helper 风格），标题固定「显卡模式」，行尾右侧 muted 小字只放状态。

| 条件 | 分段 | 点击 | 行尾状态 |
|---|---|---|---|
| 档位 Legacy1020 ∧ Gen1020 ∧ `DGpu` 可识别 | 自动选择 · 独显优先 | `NV_CTRL_PANEL_*`，DRS 回读确认 | 无 |
| Gen30 ∧ MUX | 标准 · 直连 | 重启确认框 → 重启路由 | 「重启生效」（挂起时） |
| Gen40 ∧ MUX ∧ ¬ThreeMode | 标准 · 直连 | 同上 | 同上 |
| Gen40 ∧ MUX ∧ ThreeMode | 集显 · 标准 · 直连 | 三项都走重启（集显 = `TOGGLE_IGPU`） | 同上 |
| Gen50 ∧ MUX ∧ ¬热切换 | [集显] · 标准 · 直连（集显 = `CanOfferIgpuMuxTarget`） | 三项都走重启 | 同上 |
| Gen50 ∧ MUX ∧ 热切换 | 集显 · 标准 · 直连 | 集显/标准 = RB_ON/RB_OFF 热切换（设备在位确认）；直连及离开直连 = 重启 | 切换中「正在切换…」；当前为直连时集显置灰，tooltip「先切到标准（需重启）」 |
| 无 MUX / 档位 Unknown / Gen Unknown / NoDgpu | 整行隐藏（沿用现有横幅，`Settings.cs:2969-2975`） | — | — |

补充：
- 任意时刻高亮的是 §4.5 回读出的**实际**路由；回读 Unknown 时不高亮，tooltip 说明原因。
- 确认框之前不发任何命令（与 C50 一致），取消即无副作用。
- 失败统一 `ToastForm.ShowFailure`，文案给出实际路由。

## 8. 字符串 key（`Properties/Strings.resx` + `Strings.zh-CN.resx`）

| key | zh-CN | en |
|---|---|---|
| `GpuPrefAuto` | 自动选择 | Auto-select |
| `GpuPrefHighPerf` | 独显优先 | Prefer NVIDIA |
| `GpuPrefTip` | NVIDIA 控制面板的全局首选图形处理器，对之后启动的程序生效，无需重启。 | NVIDIA Control Panel global preferred GPU. Applies to apps started afterwards; no restart needed. |
| `GpuPrefNotApplied` | NVIDIA 驱动未回读到首选显卡变更，当前仍为「{0}」。 | The NVIDIA driver did not report the change; it is still "{0}". |
| `GpuRestartPendingTag` | 重启生效 | Restart to apply |
| `GpuRestartConfirm` | 切换到「{0}」需要重启电脑。\n「是」发送指令并立即重启，「否」取消且不做任何更改。 | Switching to "{0}" requires a restart. Yes: send and restart now. No: cancel without changes. |
| `GpuRouteApplied` | 显卡模式已是「{0}」（已由屏幕连接确认）。 | GPU mode is now "{0}" (confirmed from the display wiring). |
| `GpuRouteNotApplied` | 重启后显卡模式仍是「{0}」，切换没有生效。 | After the restart the GPU mode is still "{0}"; the switch did not apply. |
| `GpuHotSwitchNotApplied` | 独显没有断开，未切到集显，已恢复原模式。 | The dGPU did not power off; iGPU mode was not applied and the previous mode is restored. |
| `GpuRouteUnknown` | 无法确认当前显卡模式（内屏未连接或读取失败）。 | Unable to confirm the current GPU mode (internal display not found). |
| `GpuIgpuNeedsStandard` | 先切到标准（需重启），再切集显。 | Switch to Standard first (restart), then iGPU. |

现有 key 保留：`GpuRouteIgpu=集显`、`GpuRouteStandard=标准`、`GpuRouteDirect=直连`、`GpuModeSwitchFailed`。

## 9. 单元测试

新增：
- `GcuServiceTierTests`：`…\L-Mechrevo\GCU\AiStoneService\GCUBridge.exe` + 1.2.0.0 → Modern12；`…\L-Mechrevo\GCU\UniwillService\…` + 1.0.2.47 且无 `UEFI_Firmware.dll` → Legacy1020；厂商路径 + 1.0.2.70 → Foreign；路径为空 / 版本读不到 → Unknown。
- `DgpuGeneration1020Tests`：`GTX 1660 Ti` + `DEV_2191` → Gen1020（回归：现为 Gen30）；`RTX 2060` + `DEV_1F15`、只有 `DEV_1C20`、`GTX 1650` → Gen1020；`RTX 2050` + `DEV_25xx` → Gen1020（名字优先）；`DEV_2206` → Gen30；持久化的 `Gen30` + 旧规则版本 → 重判。
- `GpuRouteInferenceTests`：§4.5 四行 + AMD 核显（`VEN_1002`）同 Intel；`DecodeOemDisplayMode`：Intel 2/4/1、AMD 1/0/2、255、未知值 → null。
- `NvPreferredGpuTests`：`null`→AutoSelect，`0x1`、`0x80000001`→HighPerformance，`0x10`→AutoSelect，`0x0`→Integrated；服务串两种 + 垃圾串。
- `DisplayRoutePolicyTierTests`：Gen30+Modern12 放行 `TOGGLE_ON/OFF/RESTART`、拒绝 `TOGGLE_IGPU`/RB；Gen30+Foreign 拒绝 RESTART；Gen40 任意档拒绝 RB_*；Gen40 两模拒绝 `TOGGLE_IGPU`；Gen50 无热切换拒绝 RB_*；Gen50 热切换放行 RB_ON/OFF；Gen1020+Legacy1020 只放行 `NV_CTRL_PANEL_*`；Legacy1020 下任何 `DGPU_*` 被拒。
- `GpuRestartRouteTierTests`：Gen50 直连三连 + `ServiceRestart=true`；Gen30/Modern12 直连 `[TOGGLE_ON]` + RESTART；Gen30/Foreign `[TOGGLE_ON]` + `RequiresAppRestart`，且**不发** RESTART；自动档、无 MUX 均为空并报 Unsupported。
- `GpuHotSwitchProbeTests`：服务回报成功但设备仍 `Present` → 不确认、发回滚 `IGPUONLYCONNECTIONSWITCH_STATUS`、返回 false；设备 `Disabled` + 回报成功 → 确认。
- `NvPreferredGpuApplyTests`：DRS 5 s 内变为目标 → Confirmed；不变 → NotApplied 且界面回到实际值；非 Legacy1020 档位不发布任何命令。
- `GpuRestartVerifierTests`：挂起目标=直连、回读直连 → 已生效；回读混合 → 未生效；回读 Unknown → 静默。
- `GpuRowLayoutTests`：§7 每一行一个用例（含本机组合：Gen50 + MUX + 热切换 → HotSwap 布局）。
- IL 守卫：`GpuRouteProbe` / `NvPreferredGpu` 不调用任何名字含 `Set`、`Save`、`Write` 的 NVAPI/固件方法（参照 `FirmwareWriteGuard.cs` 写法）。

需要改写的现有测试（断言与新规则相反）：`GpuCapabilityGatingFailTests.cs:18,36,55,82,97`（30 系不再全关，只关核显/热切换）、
`GpuSwitchCommandTests.cs:82,92,106,159`、`GpuGenerationMatrixTests.cs:102,137`、`GpuGenerationMatrixFailTests.cs:30,68`、
`DisplayRouteTierN11Tests.cs:30,38,66`（三模档不再携带 RB_*）、`GpuSwitchPolicyTests.cs:15`（路由表）。

## 10. 风险与无法离线验证的点

1. S50（1.2.0.0）每个动作的真实行为都不可静态确认（IL 混淆）：TOGGLE_* 在 30/40 硬件上是否照 S40 写 NVRAM、RESTART 是否可靠、RB_ON 是否断开独显。本机现有证据是 RB_ON **没有**断开独显。
2. 30 系 BIOS 的 `OemDisplayMode` 编码是按 S40 推断的；首个 30 系用户必须按「切直连 → 重启 → 回读 → 切回」走一遍，失败即关闭该代功能。
3. 40 两模档 BIOS 对核显目标的行为未知，方案不提供。
4. NVRAM 核显模式开机后独显是否从 PCI 消失未知；若不消失，§4.5 会把核显-only 判成混合（Hybrid 与 IgpuOnly 需要再加「独显 D3 且无显示输出」之类的判据）。
5. 内屏在 BIOS 里被关、或只接外屏开机时 CCD 找不到内屏目标 → 只能 Unknown。
6. 10/20：`NVControlSetting.dll` 写哪几个 SHIM 设置、写进 Base 还是 Global 配置、写的值是否就是 `0x1/0x10` 都是推断；Windows「设置 → 显示 → 图形」的按应用 GPU 偏好会覆盖全局首选。需要一台 10/20 机器验证 DRS 回读。
7. 1.0.2.47 服务（GCUBridge 1.0.1.4）的 MQTT 端口/凭据还没确认（beta21 进度笔记），且安装器尚未打包 1020 载荷。
8. NVRAM 回读需要管理员；应用默认不提权，所以 PendingReboot 多数时候只能靠服务状态。
9. 我方自身会占住独显：悬浮窗开着时 LHM 打开 GPU 传感器（`Hardware/LhmMonitor.cs:26`），NVML 在 `Gpu/NVidia/NvmlHelper.cs`；而 `HardwareControl.KillGPUApps` / `DisposeGpuControl` 都是空实现（`HardwareControl.cs:187,189`）。热切换前不释放，会让 RB_ON 失败（本机 09-10 的失败是否与此有关未知）。
10. RTX 2050（GA107）机器出厂用哪一代控制台未确认；安装器与本方案都按 1020 处理。
11. `Foreign` 下分不出厂商 40 三模 / 两模 / 30 服务，所以不用厂商的 RESTART（改由我方重启，效果等价），也不提供核显 NVRAM 目标：
    还装着厂商 40 三模服务的用户会少一个「集显」，装上我方 GCU 载荷后恢复。
12. 我方不要求 `APVersionCheck>23`（业主决定），与 C50 不同：两键为 1 而 `APVersionCheck≤23` 的机器，我方会显示热切换而官方不会。
13. 官方热切换卡片有「自动」（RB_AUTO，服务按 AC/DC 自动）；我方 2026-09-11 按业主决定移除了自动档的客户端路径与按钮（`Gpu/GPUModeControl.cs:439-444`、`Settings.cs:2164`）。是否恢复需要业主决定；恢复的话只在热切换机型提供，并按当前电源检查设备在位。
