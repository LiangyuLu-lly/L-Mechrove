# ControlCenterX 5.56.60.26 (Mechrevo 机械革命) 逆向分析文档

> 分析对象：`ControlCenter_5.56.60.26_Mechrevo_GX.exe`（Inno Setup 安装包）及本机已安装的完整组件
> 分析日期：2026-08-04
> 用途：为二次开发提供架构地图。反编译产物位于 `_decompiled/` 目录。

---

## 1. 总览

这是机械革命（AISTONE GLOBAL (SUZHOU) LIMITED）笔记本的"控制中心"全家桶，属于 Tongfang（同方）ODM 方案的 Control Center 分支。该方案被多个国产品牌复用（机械师/雷神/魔方等），仅通过皮肤配置区分。

三层架构：

```
┌─────────────────────────────────────────────────────────────┐
│ 第1层 UI       CCUWinUI.exe (WinUI 3 / .NET 8, MSIX 打包)     │
│                SystrayComponent.exe (托盘, 原生 C++, 命名管道) │
└───────────────────────────┬─────────────────────────────────┘
                            │ MQTT (localhost:13688, TLS 关闭)
┌───────────────────────────▼─────────────────────────────────┐
│ 第2层 桥接     GCUBridge.exe (Windows 服务, 内嵌 MQTT broker) │
│                = 会话0 常驻, 凭证校验, 客户端路由              │
└───────────────────────────┬─────────────────────────────────┘
                            │ 子进程 (CreateProcessAsUser → 用户会话)
┌───────────────────────────▼─────────────────────────────────┐
│ 第3层 硬件     GCUService.exe (用户会话, 硬件执行体)           │
│                ACPIDriverDll.dll (原生) + UWACPI.sys (内核驱动)│
│                HID / WMI / ACPI / SMI 操作                    │
└───────────────────────────┬─────────────────────────────────┘
                            ▼
                        EC (嵌入式控制器) / RGB 键盘 / 灯条 / 传感器
```

**当前运行状态**（本机验证）：
- `GCUBridge.exe` PID 6228，会话 0，服务名 `GCUBridge`，监听 `0.0.0.0:13688`
- `GCUService.exe` PID 10956，会话 1，为 GCUBridge 的子进程
- `SystrayComponent.exe`（MSIX 包内 Win32\），通过命名管道 `SystrayExtensionPipe` 与 UI 通信

---

## 2. 安装包结构

### 2.1 根目录（桌面包）
| 文件 | 说明 |
|---|---|
| `ControlCenter_5.56.60.26_Mechrevo_GX.exe` | Inno Setup 安装器 (51MB, PE32) |
| `setup.ini` | UTF-16LE；`[APP]` 应用名(加密)、`[Customize] customizeTarget=56`、语言列表 en-us/zh-cn、扬声器校准范围 |
| `RGBKeyboard.reg` | UTF-16LE；RGB 键盘/灯条默认配置（见 §7） |
| `UserFanTables/<机型>/M1T1.json...` | 预置风扇曲线（见 §6） |
| `logo/` | 图标、`Language/*.json`（UI 文案 en/zh-cn/zh-cn-ga）、`License/*`（用户协议） |
| `PreinstallKit/` | MSIX 应用包 + 依赖运行时 + 签名证书 `CCU.WinUI.cer` |

### 2.2 MSIX 包内容（`PreinstallKit/CCU.WinUI.msix`）
- 身份：`CCU.WinUI`，Publisher=`AISTONE GLOBAL (SUZHOU) LIMITED`，版本 5.56.60.26
- WinUI 3 + WindowsAppRuntime 1.7，全信任桌面应用（`runFullTrust`），协议 `ccuwinui://`
- **主程序集** `CCUWinUI.dll`（2.5MB，可完整反编译，155,805 行）
- 关键依赖：`M2Mqtt.Net.dll`（MQTT 客户端）、`CommunityToolkit.Mvvm`、`LiveChartsCore`（图表）、`LibreHardwareMonitorLib`（传感器，未实际使用）、`HidSharp`（未实际使用）、`Newtonsoft.Json`、`Vanara.PInvoke`、`WebView2`、`SkiaSharp`
- `Strings_Json/`：20 种语言
- `Win32/`：`SystrayComponent.exe`（托盘）、`M2Mqtt.Net.dll`、`Newtonsoft.Json.dll`
- `appsettings*.json`：**OEM 品牌皮肤**（同内核多品牌）：

| 文件 | 品牌/主题 | 差异点 |
|---|---|---|
| `appsettings.json` | 默认(机械革命) | 主题色 #FF5E5AE4，RGB 分区配置（singlezone/mezone） |
| `appsettings_black/white.json` | 深色/浅色主题 | themestyle 1/0，主题色不同 |
| `appsettings_MCJ.json` | MCJ | 背景 background_creator.jpg |
| `appsettings_Machenike.json` | 机械师 | themestyle 0（浅色），主色 #FF0000FF |
| `appsettings_monster.json` | 雷神/怪物 | 语言默认 zh-cn |
| `appsettings_LightMaster.json` | LightMaster | 主色红色 |

### 2.3 安装目录（`C:\Program Files\OEM\机械革命控制中心\`）
| 目录 | 内容 |
|---|---|
| `AiStoneService\` | **GCUBridge.exe**（服务，.NET，已混淆）+ `UEFI_Firmware.dll`（BIOS 更新）+ install.bat（sc.exe 注册服务） |
| `AiStoneService\MyControlCenter\` | **GCUService.exe**（17MB，.NET，已混淆）+ GCUServicePlugin.dll（遥测插件）+ GCUUtil.exe + ACPIDriverDll.dll（原生）+ 全套辅助工具 |
| `GamingCenter\` | 旧世代 UI：`ControlCenterU.exe`、`GamingCenterU.exe`（.NET） |
| `UWACPIDriver\` | **UWACPI.sys**（内核驱动，Tongfang 经典 WMI ACPI 驱动）+ INF + 目录文件 |
| `AirplaneDriver\` | 飞行模式虚拟 HID 驱动 vhidmini.sys |
| `AistonePowerManagement\` | 电源管理配置包 .ppkg |
| `MyControlCenter\` 数据目录 | AppSettings、DisplayProfile、ICCProfile、GamingMonitor、KeyboardManager、UserFanTables、UserPofiles、SQLiteDB、BTSavingSettings、GPUPowerSavingSettings、LCSavingSettings、Command、Config |

---

## 3. 反混淆现状

| 程序集 | 可读性 | 说明 |
|---|---|---|
| `CCUWinUI.dll` (UI) | ✅ 完整可读 | 155,805 行，无混淆 |
| `GCUService.exe` | ⚠️ 部分可读 | ConfuserEx anti-tamper：方法体运行时解密，ILSpy 得到 `throw new Exception("Runtime exception")` 桩；**类签名/常量/字符串/命名空间完整** |
| `GCUBridge.exe` | ❌ 方法体全毁 | ConfuserEx（ILSpy 直接报 Invalid MethodBodyBlock） |
| `GCUServicePlugin.dll` | ✅ 可读 | 遥测/分析上报 |
| `ACPIDriverDll.dll` | 原生 C++ | 需 Ghidra/IDA 分析 |

**进一步还原路径**（如需要方法体级细节）：
1. 动态法：服务运行时方法体已在内存中解密，可用 dnSpy 调试器断点抓取，或 JIT hook 工具 dump
2. de4dot 变体（de4dot-cex / de4dot.ConfuserEx）尝试静态脱壳
3. 协议级重写：其实无需方法体——MQTT 协议 + 风扇表格式 + 注册表键已完整，可直接从协议层对接

---

## 4. MQTT 通信协议（核心）

### 4.1 Broker
- 地址：`localhost:13688`（GCUBridge 内嵌 MQTTnet broker，也监听 0.0.0.0）
- UI 客户端凭据（硬编码）：`UWPClient_User_5` / `UWPClient_Pwd888881772688_5`，clientId `UWPClient_5`
- broker 侧有 4 类客户端管理：`_UWPClient`、`_KeyboardClient`、`_MyTrayClient`、`_PluginClient`（凭据存于注册表 `\OEM\GamingCenter2\`）
- QoS 2，可 retain

### 4.2 订阅主题（UI 端 `MQTTDataService.InitlizeTopics` 完整清单）
遥测：
- `System/CpuInfo`、`System/GpuInfo`、`System/MemoryInfo`、`System/NetworkInfo`、`System/DiskInfo`
- `System/BatteryInfo`、`System/FanInfo`、`System/FanErrorInfo`、`System/HardwareInfo`、`System/HwFuelGauge`、`System/StaticsData`
- `GPUDevice/Status`、`GPUDeviceItem/Status`、`Settings/DeviceSwitchItemStatus`（MUX 显卡切换状态）

控制/状态：
- `Fan/Status`、`Fan/Table`、`WhisperMode/Status`（安静模式）
- `Setting/Status`、`Keyboard/Status`、`KeyboardSingleZone/Status`
- `MyRgbLightbar/Status`、`MyRgbLightbar/Type`（EC 灯条）
- `HidLightbar/Status`、`HidLightbar_Logo/Status`、`HidLightbar_Hinge/Status`、`HidLightbar_Sync/Status`（HID 灯条）
- `Customize/Info`、`Customize/SupportInfo`（机型支持能力）
- `Languages/Info`、`Languages/AutoDetectStatus`
- `OSD/Status`、`Display/Status`、`TouchPadWorkArea/Status`
- `GameProfile/Status`、`KeyboardManager/Status`、`GamingMonitor/Status`
- `BT_LC/Status`、`LCHWOC/Status`（液冷相关）
- `DoudouGame/Status`、`Doudou/DoudouGameManager`（AI 豆豆游戏助手）
- `OTA/CCU6Control`（OTA 通道）

发布主题（Topic 常量表，另含）：`Fan/Control`、`EC/Control`、`EC/Status`、`System/Control`、`BatteryProtection/Control`、`RGBLB_Control=MyRgbLightbar/Control`、`RGBKeyboard_Control=Keyboard/Ctrl`、`RGBKeyboardSingleZone_Control=KeyboardSingleZone/Ctrl`、`HIDRGBLightbar_*=/Ctrl`、`Service/Close`、`Customize/Control`、`Languages/Control`、`Setting/Control`、`BT_LC/Control`、`LCHWOC/Control`

### 4.3 载荷格式
JSON。UI 端发布带节流（`SemaphoreSlim` + 200~300ms 延迟），订阅回包同 topic。RGB 初始化会向 `keyboard/ctrl` 发布匿名对象（机型相关的灯效配置）。

### 4.4 旧世代 IPC（仍在 GCUService 中保留）
- **WCF**：`IWCFService.SendToServer(Topic, Command)`（net.tcp 自宿主，用户名密码校验 `CustUsernamepwdValidator`），服务端回调 `IDataCallback.ReceiveData(code, value)`——旧 UI（GamingCenterU.exe）走此通道，GCUService 内部再转成 MQTT
- **Win32 窗口消息**：`WM_MSG_MYAPP=21575`、`WM_MSG_OSD=1225`、`WM_MSG_GAMINGCENTER=21577`、`WM_MSG_GAMINGCENTERTRAY=21584`，窗口名 `GamingCenter`；消息值 `MSG_MYAPP_AP_OPEN/CLOSE/SHOW/HIDE/MINIMIZE`
- **命名管道**：`SystrayExtensionPipe`（UI ⇄ SystrayComponent）

---

## 5. 硬件访问链路

### 5.1 EC（嵌入式控制器）
```
GCUService (MyECIO namespace)
  └─ AcpiCtrl (DeviceIoControl 直通)
       ├─ IOCTL_GPD_ACPI_ECREAD/ECWRITE (0x9C40_204C/0x9C40_2050)
       ├─ IOCTL_GPD_ACPI_MMREADB/D, IOREAD/WRITE, TMPREAD/WRITE1-3
       ├─ IOCTL_GPD_ACPI_SMAPCTABLE (0x9C40_2240) — 读"Smart APC 表"(EC NVRAM 中的设置表)
       ├─ IOCTL_GPD_ACPI_CUSTOMCTL — 自定义控制
       └─ SMI 命令: SMRW_CMD_READ=0xBB, SMRW_CMD_WRITE=0xAA (ACPI 方法 SMRW)
  ├─ ACPIDriverDll.dll (原生): SMAPCTable() P/Invoke
  └─ IOdriverEC / MyEcCtrl 高级封装
```
`MyEcCtrl` 能力面（方法签名完整，是了解 EC 能力的最好入口）：
- 机型识别：`GetProjectIdFromEC`、`GetSystemIdFromEC`、`GetnModuleIdFromEC`、`GetnModule2IdFromEC`、`GetROMIdFromEC`、`GetROMId2FromEC`、`GetBiosProjctID`、`Is6BitID`、`GetProject2ExID`
- 电源/适配器：`GetAdapterWattFromEC`、`GetPowerFromTypeC`、`GetTypeCsatusFromEC`、`GetRoundSocketStatusFromEC`、`GetDBType`、`GetTypeCAdaptorPrioritySupport`
- 能力探测：`isSupportRGBLBFromEC`、`GetSmartBalanceSupportFromEC`、`GetTurboModeSupport`、`GetOTASupport`、`IsSupportLiquidCooling`、`IsSupportLC_HWOC_ADL`、`IsSupportAntiOC`、`IsSupportRamFan1p5`、`IsSupportLCFanTable`、`IsChinaMode`
- 控制：`SetCustomModetoEC`、`Set_APExistToEC`、`SetHWOC`（硬件超频）
- 状态：`GetThermalProtectStatusFromEC`、`GetGPUCoreFreqFromEC`、`GetGPUMemFreqFromEC`、`GetTouchPadLedStatusFromEC`

### 5.2 RGB 键盘 / 灯条
- 键盘：`MyRGBKeyboard` / `GCUService.MyRgbKeyboard`（HID 直连，`UsbHidModel` 用 SetupAPI 枚举 + HidD/HidP 读写 feature report）；灯效模式：单色/多区/自定义/随音（Enable_RGB_Music=0xFE）
- 灯条：`LightingModel`（EC 灯条 MyRgbLightbar，POWER/RL/GL/BL/呼吸/流光）+ HID 灯条（HidLightbar_* 四通道：本体/Logo/转轴/Sync）
- 键盘分区模式：`singlezone_Keyboard`（单区）、`mezone_Keyboard`（多区）、`mezone_lightbar` 等配置在 appsettings

### 5.3 驱动
- **UWACPI.sys**：Tongfang 经典 WMI ACPI 驱动（设备接口对接 IOCTL_GPD_* 系列），提供 EC/CM/MM/PE/IO 端口读写
- **vhidmini.sys**（AirplaneDriver）：虚拟 HID 用于飞行模式开关
- 电源管理：AistonePowerManagement.ppkg（Windows 配置设计器包，ECO/STD 电源计划）

### 5.4 传感器遥测
`GCUService.MySystem.*`（Battry/GPUInfo）、`MyControlCenter.MySystem.CPUInfo`：Windows 管理 API（WMI/Counter）+ 显卡 NV API（NVControlSetting.dll）上报到 MQTT。

---

## 6. 数据文件格式

### 6.1 风扇曲线（UserFanTables/`<ProjectID>`/M1T1.json）
- 文件命名：`M<模式><T<曲线>.json` — M1=Gaming, M2=Office（T1~T5 为用户曲线位），另有 `DefaultFanTable_Gaming/Office/Turbo.json`
- 结构：
```json
{
  "Activated": true,
  "Name": "M1T1",
  "PL1": "40", "PL2": "40",           // 交流功耗限制 (W)
  "PL1_dc": "0", "PL2_dc": "0",       // 电池功耗限制
  "TCC": "-5",                         // 热节流偏移 (°C)
  "CTGP": "15",                        // CPU 热保护点
  "DB": "0",                           // Dynamic Boost
  "WM": "NA",                          // Wake Mode
  "CpuTemp_DefaultMaxLevel": 11,       // 曲线点数 (0-15)
  "GpuTemp_DefaultMaxLevel": 11,
  "FanControlRespective": false,       // 是否独立风扇控制 (部分机型)
  "CPU": [ {"ID":0, "UpT":0, "DownT":37, "Duty":0}, ... 16 点 ],
  "GPU": [ ... ]
}
```
- 温度-占空比点：`UpT`/`DownT` 为带回差（hysteresis）的上下阈值，`Duty` 为风扇占空比 %；`255` 表示该点禁用（曲线终点填 255 哨兵）
- 目录名即机型 ProjectID（PH4A/PH4P/PH4T/PH6P/PH6T 系列 = Tongfang 板号）

### 6.2 应用-模式绑定（AppSettings/AppProfileBinding.json）
每应用绑定办公/游戏/极速模式与曲线序号：`Desktop/OfficeEnable/GameEnable/TurboEnable + *ProfileIndex`

### 6.3 界面定制（MSIX 内 appsettings*.json）
`UWPCustomSettings`：主题色、背景、透明度、themestyle(0浅/1深)、默认语言、RGB 默认灯效。

### 6.4 其他
- `AppSettings/KeyboardManager.json`、`GamingMonitor.json`、`KeyboardManager/settings.json`
- `Config/DeviceBase64.dll`：实际为原生 DLL（设备配置辅助，非 base64 文本）
- SQLite：`SQLiteDB`（EntityFramework + System.Data.SQLite 持久化）

---

## 7. 注册表（`HKLM\SOFTWARE\OEM\GamingCenter2\`）

`RGBKeyboard.reg` 完整结构（机械革命默认值）：
- `RGBKeyboard\`：`PowerSwitch=1`、`DCLight=0`（电池灯效亮度级）、`ACLight=3`（交流灯效亮度级）
- `Lightbar`、`Lightbar_logo`（转轴 logo 灯）各自独立 PowerSwitch/Effect/DC/AC/Color_R/G/B
- 全套机型灯效状态持久化于此；服务端 MQTT 凭据亦在此子树

---

## 8. UI 功能地图（CCUWinUI.dll）

UI = 微软 WinUI Gallery 示例程序 fork（`WinUIGallery.*` 命名空间 10.8 万行）+ 自有代码（`CCUWinUI.*` 4.6 万行）。注意：**厂商把业务页面放进了 WinUIGallery 命名空间**，自有命名空间反而只有少数页面。

### 页面清单
| 页面 | 功能 |
|---|---|
| `HomePage` | 首页仪表盘（性能雷达 RadarMap、硬件状态） |
| `PowerManagePage` | 电源模式（Gaming/Office/Turbo/Custom）、ECO/STD 电源计划 |
| `CustomModeSettingPage` | 自定义风扇曲线编辑器（SmartSlider、TurboSubModePicker） |
| `GpuSettingPage` | GPU 设置 / MUX 切换（IGPUHotSwapCard，热切换 iGPU/dGPU） |
| `DisplaySettingPage` / `DisplayView` / `RefreshRateView` | 显示设置、刷新率、ColorCalibrationView（校色） |
| `LiquidCoolingSettingPage` | 液冷散热（高端机型，BT_LC/LCHWOC） |
| `QuickSwitchPage` | 快捷切换（QKEY_MODESWITCH=0 / QKEY_FANBOOST=1） |
| `SettingsPage` | 设置（语言、OSD、触摸板、开机自启） |
| `LightPage` + `SmartLightbar*` (IDB/IDZ/EC/Logo/Hinge/Sync/UserMode) | 灯条 RGB（EC 灯条与 HID 灯条两套） |
| `RgbKeyboardView` / `SingleColorKeyboardView` / `SingleZoneKeyboardView` / `UserModeKeyBoardView` / `RainbowColorPicker` / `SolidColorPicker` | 键盘 RGB（全键/单色/单区/自定义） |

### 自有代码结构（CCUWinUI.*）
- `ViewModels`：`NavigationPageViewModel`（主导航）、`DisplayViewModel`、`HzIndexClass`
- `Models`：`PerformanceRadarData`、`PopularGame`（游戏库）、`DiskInfo`、消息对象（ModeSwitch/WindowState/MinimizeWindow/MqttConnectStatus）
- `Helpers`：`BackgroundService`、`NamedPipeServer`（SystrayExtensionPipe）、`AnimationHelper`
- `Controls`：`RadarMap`、`IGPUHotSwapCard`、`SmartSlider`、`TurboSubModePicker`、`SegmentedProgressBar`、`LeftRightSwitchButton`
- `DataService`：`MQTTDataService`（§4.2 订阅源）、`Topic` 常量表
- 注册协议 `ccuwinui://`；单实例管理（AppInstance.FindOrRegisterForKey）；启动即拉起 `Win32\SystrayComponent.exe`

---

## 9. 组件间的进程/通信关系图

```
                        ┌──────────────────────────────┐
  MSIX 包(WindowsApps)  │ CCUWinUI.exe ──管道──SystrayComponent.exe
                        └──────┬───────────────────────┘
                               │ MQTT :13688 (QoS2, JSON)
                        ┌──────▼───────────────────────┐
  服务 (会话0)          │ GCUBridge.exe (broker+路由)   │
                        └──────┬───────────────────────┘
                               │ CreateProcessAsUser(用户会话)
                        ┌──────▼───────────────────────┐
  硬件执行体 (会话1)    │ GCUService.exe                │
                        │  ├─ ACPIDriverDll ─DeviceIoControl─► UWACPI.sys ─► EC
                        │  ├─ HID (键盘/灯条)   SetupAPI+HidD
                        │  ├─ WMI/电源/显示 API
                        │  └─ GCUServicePlugin(遥测上报→HTTP)
                        └──────────────────────────────┘
   旧世代 (已弃用)      GamingCenterU.exe ──WCF──► GCUService
```

---

## 10. 二次开发扩展点与建议

### 10.1 推荐切入层
1. **MQTT 协议层（最干净）**：协议完全公开，凭据已知，可写独立客户端（Python/C#/Rust）直接订阅 `System/*` 遥测、发布 `Fan/Control`、`EC/Control`、`Keyboard/Ctrl`。这是"控制中心替代品/自动化脚本"的最快路径。
2. **UI 层**：CCUWinUI.dll 可完整反编译且无混淆——可基于 WinUI Gallery fork 结构直接改 UI 加功能页，重新签名打包（需替换证书，MSIX 签名用 `CCU.WinUI.cer`）。
3. **数据/配置层**：风扇曲线 JSON、AppProfileBinding、注册表键、appsettings 皮肤——不动代码即可改行为（改曲线、换皮肤、改默认灯效）。

### 10.2 需绕过的障碍
- GCUService/GCUBridge 的 anti-tamper：**不要改二进制**（运行时自校验风险），需要改硬件行为时走协议层
- 服务凭据管理在注册表 + 混淆代码中，改动注册表凭据会断掉 UI 连接——二次开发客户端应使用独立 clientId 与 broker 交互（broker 支持多客户端；也可申请加入 `OEM\GamingCenter2` 凭据区）
- 机型能力由 EC `Get*FromEC` + `Customize/SupportInfo` 下发，新功能要按机型分支

### 10.3 值得进一步深挖的方向（如需）
- `GCUService` 方法体还原（dnSpy 动态抓取 JIT 后 IL）
- `ACPIDriverDll.dll` 原生逆向（Ghidra）——SMAPCTable 表结构
- UWACPI.sys 驱动接口文档化（IOCTL 语义全表）
- 键盘 HID feature report 协议（不同机型 VID/PID 分支）
- OTA 流程（OTA.exe + RestSharp，`OTA/CCU6Control` topic）

---

## 11. 关键文件索引

| 路径 | 内容 |
|---|---|
| `_decompiled/CCUWinUI.decompiled.cs` | UI 全量源码（155,805 行） |
| `_decompiled/GCUService/GCUService.decompiled.cs` | 服务端（99,845 行，方法体部分桩化） |
| `_decompiled/GCUBridge/GCUBridge.decompiled.cs` | broker（结构完整，方法体损坏） |
| `_decompiled/GCUServicePlugin/GCUServicePlugin.decompiled.cs` | 遥测插件 |
| `_extracted/msix/` | MSIX 解包内容 |
| `C:\Program Files\OEM\机械革命控制中心\` | 完整安装树（含驱动） |
