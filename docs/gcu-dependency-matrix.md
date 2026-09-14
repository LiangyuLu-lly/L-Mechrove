# L-Mechrevo 功能 × GCU 依赖矩阵

本文回答一个问题：L-Mechrevo 的每个功能是否依赖 GCU（GCUBridge/GCUService 后台服务）？不装 GCU 会怎样？

所有 file:line 均已逐条核对源码，未做任何推测。

---

## 1. 传输底座（一切控制功能的前提）

| 项 | 值 | 依据 |
|---|---|---|
| 传输方式 | MQTT，broker 即 GCUBridge Windows 服务 | `src\MechrevoLiteWin\Hardware\MechrevoHw.cs:33-34`（Host=127.0.0.1, Port=13688） |
| 常驻实例 client id | `UWPClient_4`（官方 UI 占 slot 5） | `MechrevoHw.cs:45` |
| 短命辅助实例 client id | `UWPClient_3`（开机限充任务，必须与常驻不同，否则互踢） | `MechrevoHw.cs:57` |
| 连接判据 | `IsConnected` = MQTT socket 状态 | `MechrevoHw.cs:868` |
| KeepAlive | 3 秒 | `MechrevoHw.cs:2993` |
| 发布超时 | 5 秒（未连接时抛 `MqttPublishFailedException`） | `MechrevoHw.cs:2434`、`:2455` |
| 首连初始化序列 | System_ON → 各通道 GETSTATUS（见 §2 各行） | `MechrevoHw.cs:1027-1039` |
| 退出清理 | 尽力发 `System/Control {Action=System_OFF}` 停止遥测推送 | `Program.cs:1749-1778` |

**结论：只要功能走 MQTT，就 100% 依赖 GCU。** 不存在"部分降级"的 MQTT 通道。

---

## 2. 主矩阵：功能 × GCU 依赖

| # | 功能（中文 UI 名 / English） | GCU 必需？ | MQTT 主题 | 实现 file:line | 无 GCU 时的表现 | 回退路径 |
|---|---|---|---|---|---|---|
| 1 | 性能模式（Office/Gaming/Turbo/自定义，Performance Mode） | **是** | `Fan/Control`（SET_OPERATING_MODE_DETAIL）+ `LCHWOC/Control`（运行标记） | `MechrevoHw.cs:2190-2219`（SetMode）、`MechrevoService.cs:183-229`（服务层切换+确认） | 模式切换按钮无效果；自动模式被推迟（日志 "Automatic GPU mode deferred until the GCU connection is ready."） | 无 |
| 2 | 风扇转速曲线（Fan Curve） | **是** | `Fan/Control`（SET_FAN_SPEED_CURVE_SETTING / GET_FAN_SPEED_CURVE_SETTING） | `MechrevoHw.cs:2321-2335`（SetFanCurve）、`:1030-1031`（初始回读） | FanCurveForm 显示"未连接，未保存"（`FanCurveForm.cs:290`） | 无 |
| 3 | 电池充电保护三档（Battery Protection） | **是** | `BatteryProtection/Control`（PERFORMANCEDMODE/BALANCEDMODE/HEALTHYMODE + Report=GET） | `MechrevoHw.cs:2237-2266`、`:1038` | 档位无法读写，三档高亮不显示 | 无 |
| 4 | GPU 显卡模式切换（核显/混合/独显直连，GPU Mode） | **是** | `Setting/Control`（IGPU_ONLY_CONNECT_RB_*、DGPU_DIRECT_CONNECT_TOGGLE_*、DGPU_DIRECT_CONNECT_RESTART） | `MechrevoHw.cs:2268-2319`（SetGpuMode）、`MechrevoService.cs:879-918`（RequestGpuModeRestartAsync）、`MechrevoService.cs:1126-1130`（800ms 后发 RESTART） | UI 弹窗 "GCU 硬件服务尚未连接，请稍后再试。"（`Settings.cs:3825`）；开机自动切换被推迟（`Gpu\GPUModeControl.cs:347-354`，判据 `Hardware\GpuSwitchPolicy.cs:17-21`） | 无（重启兜底见 §5） |
| 5 | 屏幕刷新率切换（Display Refresh，GPU_HZSETTING） | **是** | `Setting/Control`（GPU_HZSETTING） | `MechrevoService.cs:1253` | 开关不可用（能力位 DisplayRefresh=false 时隐藏） | 无 |
| 6 | 局部背光（Local Dimming） | **是** | `Setting/Control`（LOCALDIMMING_ON/OFF） | `MechrevoService.cs:1226`；状态解析 `MechrevoHw.cs:1523` | 开关不可用 | 无 |
| 7 | 响应加速（LCD Overdrive） | **是** | `Setting/Control`（LCDOverdrive_ON/OFF） | `MechrevoService.cs:1240`；状态解析 `MechrevoHw.cs:1526-1529` | 开关不可用（本机实测 LCDOverdriveSupport=NotSupport，`MechrevoHw.cs:766-767`） | 无 |
| 8 | USB 关机充电（USB Charging） | **是** | `Setting/Control`（USB_CHARGER_ON/OFF） | `MechrevoService.cs:1266`；状态解析 `MechrevoHw.cs:1599` | 开关不可用 | 无 |
| 9 | 色域切换 / 色彩校准（Color Calibration，sRGB/P3/AdobeRGB） | **是** | `Setting/Control`（COLOR_CALIBRATION_OFF / COLOR_CALIBRATION_ON_DEFAULT/SRGB/P3/ADOBERGB） | `MechrevoService.cs:598-644`（SetColorCalibration）、`:829-832`（档位映射）、`:632`（OFF） | 开关不可用 | 无 |
| 10 | 键盘灯睡眠定时（Keyboard Light Timer） | **是** | `Setting/Control`（KEYBOARD_LIGHTBAR_TIMER_ON+Mins / _OFF） | `MechrevoService.cs:1509-1518` | 开关不可用 | 无 |
| 11 | GPU 超频（GPU Overclock，核心/显存偏移） | **否（可选）** | `LCHWOC/Control`（仅 GETSTATUS 与模式切换运行标记 `IsCustomRun`/`IsNormalRun`） | 直写驱动优先：`MechrevoHw.cs:2674-2743`（ApplyGpuOverclockAsync，NVAPI 本地写 → 提权助手）；GCU 通道判据 `MechrevoService.cs:354-360`（CanSetGpuOverclockThroughGcu）；提权助手 `Gpu\NVidia\ElevatedGpuOverclockHelper.cs:31-40` | 走 NVIDIA 驱动直写 + 提权助手，仍可用；GCU 硬边界参考值见 `MechrevoHw.cs:26-32` | **有**：驱动直写（无需 GCU） |
| 12 | 官方键盘灯效（Keyboard RGB，SetEffectALL 固件协议） | **是** | `Keyboard/Ctrl`（SetEffectALL / SetPower） | `MechrevoService.cs:1916-1997`（SetLightEffect/SetLightPower）、`:1034`（GETSTATUS） | RgbForm 显示"服务未连接"（`RgbForm.cs:254`） | 部分：直连 HID 效果见 #13 |
| 13 | 直连 RGB HID 灯效（KeyboardRgb，ITE 0x048D） | **否** | 不走 MQTT（HID SetFeature/WriteFile） | `Hardware\KeyboardRgb.cs`（Connect/StartMode/StopCurrentEffect）、`Hardware\HidDeviceWin.cs:198-207`（共享模式打开，因 GCUBridge 持有句柄）、自测入口 `Program.cs:395-432` | 完全可用；但必须共享打开，GCU 在跑时独占会失败 | **有**：本行即回退 |
| 14 | 灯条灯效（Lightbar，HidLightbar/Ctrl） | **是** | `HidLightbar/Ctrl` | `Settings.V2.cs:459-508`（UI 行 + SetLightEffect/SetLightPower）、`MechrevoService.cs:1924/1971`、`MechrevoHw.cs:1035` | 行仍显示但下发无确认；能力位 Lightbar=false 时隐藏 | 无 |
| 15 | Logo 灯效（Logo Light，HidLightbar_Logo/Ctrl） | **是** | `HidLightbar_Logo/Ctrl` | `Settings.V2.cs:519-567`、`MechrevoHw.cs:1036` | 同上 | 无 |
| 16 | 液冷箱控制（Liquid Cooling，泵/风扇档位） | **可选**（三路由） | `BT_LC/Control`（LC_PumpCtrl/LC_FanCtrl/Connect/Disconnect/DeviceMacSetting/ClearDevMAC/InputWater） | 路由解析 `Hardware\LiquidCoolingConnectionPolicy.cs:19-29`（DirectBle > Gcu > BluetoothObserved > None）；GCU 通道下发 `MechrevoService.cs:1526-1691`；直连 BLE `Hardware\WaterCoolerBle.cs:12-39` | 走 DirectBle 路由仍可用；GCU 路由不可用 | **有**：Direct-BLE 直连（`WaterCoolerBle.cs`，与 GCU 的 BT_LC 通道互斥，`WaterCoolerBle.cs:19`） |
| 17 | 液冷连接重试 / 灯效恢复 | **可选** | `BT_LC/Control` | 重试判据 `LiquidCoolingConnectionPolicy.cs:36-47`（5 次、间隔 8s）；恢复判据 `:49-57` | GCU 不可用时重试不启动 | DirectBle |
| 18 | Windows 个性化（任务栏自动隐藏/透明/深色主题） | **否** | 不走 MQTT | `Helpers\ShellPersonalization.cs:6-21`（SHAppBarMessage + HKCU 注册表 + WM_SETTINGCHANGE） | 完全可用 | 不适用 |
| 19 | 隔离官方控制台（Isolation） | **部分** | 不走 MQTT（但状态展示引用 GCU 进程） | `Helpers\OfficialConsoleIsolation.cs:117-138`（GetStatus：`IsProcessRunning("GCUBridge") && IsProcessRunning("GCUService")`，`:122-123`）、安装探测 `:928-945`（WindowsApps CCU.WinUI_* / OEM ControlCenterU.exe） | 隔离动作本身可用；状态文案会显示"已隔离 · GCU 后台待连接"（`:136`） | 不适用 |
| 20 | 核显切换独显占用预检（杀占卡进程） | **否**（但保护 GCU 进程） | 不走 MQTT | `Gpu\ElevatedProcessKiller.cs:15-28`（提权杀进程通道）、`:224-228`（二次核对）；保护名单 `Gpu\DgpuApplicationCoordinator.cs:20-29`（含 `gcuservice`/`gcubridge`/`aistoneservice`） | 功能可用；GCU 进程永远在保护名单内不会被杀 | 不适用 |

行数：**20**。

---

## 3. GCU 侧安装面（release\GCU-only\）

| 项 | 内容 | 依据 |
|---|---|---|
| 组件 | `AiStoneService\GCUBridge.exe`（MQTT broker + 服务宿主）、`AiStoneService\MyControlCenter\GCUService.exe`（硬件执行体及全部依赖）、`UWACPIDriver\UWACPIDriver.sys`（EC 访问内核驱动，ACPI\INOU0000，KMDF） | `release\GCU-only\README.txt:15-17` |
| 安装位置 | `%ProgramFiles%\L-Mechrevo\GCU`（先 robocopy 复制，再 Authenticode 验签 GCUBridge.exe / GCUService.exe / UWACPIDriver.sys 三个文件，任一失败即中止） | `install.bat:11,15,26-31` |
| install.bat 步骤 | ① 管理员自检（net session）→ ② 复制到 Program Files → ③ 验签 → ④ `pnputil /add-driver UWACPIDriver.inf /install` → ⑤ `sc create GCUBridge binPath=... start=auto obj=LocalSystem`（已存在则 `sc config` 更新路径）→ ⑥ 防火墙：删除并重建入站**阻止**规则 "L-Mechrevo - Block remote GCU MQTT"（TCP 13688）→ ⑦ `sc start GCUBridge` → ⑧ 5 秒后查 RUNNING 状态 | `install.bat:2-7,15,26,35,41-48,55-56,64,72` |
| 防火墙规则 | 名 "L-Mechrevo - Block remote GCU MQTT"，dir=in action=block protocol=TCP localport=13688（阻止局域网访问本机 MQTT 端口） | `install.bat:55-56`、`README.txt:40` |
| 服务形态 | 纯后台服务，无窗口/图标；安装后直接运行 L-Mechrevo.exe | `README.txt:4-5,77` |
| 附带数据 | `AiStoneService\MyControlCenter\` 下 UserPofiles（Mode1-4_Profile*.json）、UserFanTables（含 PH6TRX1/PH6PRxx/PH6PGEx/PH6PG7x150W 子目录）、ICCProfile（BOE0D55/XMI27B3 面板校色包）、KeyboardManager\settings.json | `release\GCU-only\` 目录清单 |
| 卸载 | `uninstall.bat`（停/删服务；驱动保留无害） | `README.txt:42` |
| 独立分发包 | `release\GCU-独立安装包.zip` | 文件存在（已确认） |

---

## 4. 机型 / Project-ID 判别

**代码中不存在任何 GCU 版本号检查。** 全库检索仅命中三处"版本"字样，均为注释或提示文案（`MechrevoHw.cs:1697`、`:1843`、`Settings.cs:3832`），没有版本比较逻辑。机型判别完全靠两类信号：

### 4.1 静态信号：注册表能力档案（官方 GCU 写入）

`Hardware\MechrevoDeviceCapabilities.cs`：

- 注册表路径（三选一，按优先级）：`SOFTWARE\OEM\GamingCenter2\ItemSupport`、`GamingCenter\ItemSupport`、`ControlCenter\ItemSupport`（`:12-17`）；GpuConfig 与 RGBKeyboard 同构（`:19-31`）。
- ProjectId 取值键：`BIOS_PROJECT_ID` / `BIOSProjectID` / `BIOSProjectId` / `ProjectID` / `ProjectId`（`:202`）。
- 能力位（全部为布尔/数值标志，`:211-244`）：Keyboard、Lightbar、RgbLightbar、LogoLight、DisplayRefresh、ColorCalibration、DgpuDirect、IgpuOnly、FanBoost、FanSettings、OverclockSettings、CpuPerformanceTuning、LiquidCooling、LiquidCoolingAutoMode、Numpad、AcRecovery、LcdOverdrive、LocalDimming、TurboMode、TurboSubMode、SmartBalance、RamFan15、NvidiaGpu、GpuHotSwap（= NvidiaGpu && IgpuOnly && APVersionCheck>23 && GpuHotSwapSwitchSupport && lgpuHotSwapSwitchStatus，`:239-240`）、AmdPlatform、IsOldType、KeyboardType、DisplayRefreshLevel。
- 注释明示语义：这些是"官方 GCU 为当前 project ID 写入的静态特性位；运行期 MQTT 字段对可调范围保持权威"（`:5-8`）。

### 4.2 Project-ID 枚举表

`docs\hardware\project-ids.json`（来源：反编译 GCUService，生成于 2026-08-25）共 110 个成员，含 GI/GJ/GK/GICN/GJCN/GK5CN_X/GK7CN_S、PF 系列、CML_Gaming、PH4*/PH6* 全系（PH4TRX1、PH4TUX1、PH4TQx1、PH6TRX1、PH6TQxx、PHxAxxx、PHxPxxx、PH4ARxx、PH4AUxx、PH4AXxx、PH6AQxx、PH6ARxx、PH6AGxx、PH4PRxx、PH4PUxx、PH4PGx1/x2、PH6PRxx、PH6PGEx、PH6PG0x/3x/7x 及 150W 变体）、ID 系（IDR/IDX/IDV/…/ID65/ID1New/ID9/IDH/IDR2）、E3-E8/P0/P1/P0R/P1R/MaxQ/X2-X11 等。

**40 系 vs 50 系的判别方式：** 没有专门的"代际"字段。区分只体现在两点：
1. Project-ID 命名本身（PH4* = 40 系平台段，PH6* = 50 系平台段，如 PH6TRX1/PH6PG7x150W 只出现在 50 系机型）；
2. 各 project-id 对应的注册表能力档案内容（哪些 ItemSupport 位被官方 GCU 置 1）。

代码不做代际推断，只读当前机器的注册表档案 + 运行期 MQTT 上报。

### 4.3 运行期信号（权威）

- `Setting/Status` 同帧上报的能力/状态字段：`LCDOverdriveSupport`（`MechrevoHw.cs:1515`）、`LCDOverdriveSwitch`（`:1526`）、`LOCALDIMMING_*`（`:1523`）、`USB_CHARGER_STATUS_*`（`:1599`）、`KEYBOARD_LIGHTBAR_TIMER`（`:1593`）、GPU 模式 `IGPU_ONLY_CONNECT_RB_*`（`:570-611`）。
- GCU 上报的超频范围字段只作参考，实测可超出（`MechrevoHw.cs:26-32` 注释：核心 250 生效/300 被丢，显存实测到 2000）。
- 运行期能力门禁：`CanSwitchGpuMode`（`MechrevoHw.cs:2271`）、`SupportsIgpuOnly`/`SupportsDgpuDirect`/`SupportsGpuHotSwap`（`MechrevoHw.cs:2276-2280`）、`CanSetGpuOverclockThroughGcu/ThroughDriver`（`MechrevoService.cs:354-360`）。

---

## 5. 降级模式缺口（哪里静默失败，哪里弹窗）

### 弹窗（有明确提示）

| 场景 | 用户可见文本 | 位置 |
|---|---|---|
| GPU 模式切换时 GCU 未连 | "GCU 硬件服务尚未连接，请稍后再试。" | `Settings.cs:3825` |
| GPU 模式不支持 | "当前机型或当前 GCU 版本未报告支持此显卡模式。" | `Settings.cs:3832` |
| 重启切换请求发送失败 | "GCU 未能发送重启切换请求，显卡模式未被标记为已切换。" | `Settings.cs:4019` |
| GCU 收到 RESTART 后 15 秒未重启系统 | "GCU 未自动重启系统。显卡模式指令已写入，重启后才会生效。点击「确定」5 秒后重启 Windows 完成切换。"（Windows 重启兜底） | `Settings.cs:4030-4041`（WatchGcuRestartFallbackAsync） |
| 液冷自动连接、GCU 未回报状态 | "GCU 未返回液冷状态。是否尝试兼容直连？"（YesNo 弹窗 → DirectBle 回退） | `Settings.cs:1035` |
| 液冷已检测到但 GCU 未接管 | 状态栏 "已检测到水冷，等待 GCU 连接" | `Settings.cs:1046` |
| 泵/风扇档位 GCU 不支持 | "GCU 未提供该泵速档位，已恢复原档位" / "GCU 未提供该风扇档位，已恢复原档位" | `Settings.cs:1144`、`:1201` |
| 液冷未连接时点档位 | "液冷系统未连接。" / "…，暂不可控制。请等待 GCU 完成连接后重试。" | `Settings.cs:1263-1264` |
| 蓝牙水冷灯光设置被挡 | "Windows 已检测到水冷蓝牙连接，请等待 GCU 完成连接后再设置灯光。" | `Settings.cs:1342` |

### 状态栏文本（液冷路由，3 秒轮询）

| 路由 | 文本 | 位置 |
|---|---|---|
| Gcu | "GCU 已连接水冷" / "GCU 流量异常" | `Settings.cs:1472-1473` |
| BluetoothObserved | "蓝牙已连接"（tooltip "蓝牙已连接，等待 GCU 接管"） | `Settings.cs:1482-1483` |
| DirectBle | "已直连" / "正在确认水流" / "流量异常" | `Settings.cs:1453-1456` |
| None | "未连接（点击连接）" | `Settings.cs:1497` |

### 静默失败（无弹窗，仅日志或界面无反应）

| 场景 | 表现 | 位置 |
|---|---|---|
| 性能模式/风扇曲线/电池保护等 MQTT 命令 | 未连接时 `Publish` 抛 `MqttPublishFailedException`（"MQTT 未连接"），多数调用方只 catch 记日志，UI 无提示 | `MechrevoHw.cs:2455`、`MqttPublishFailedException.cs:6` |
| 开机自动 GPU 模式切换 | 仅日志 "Automatic GPU mode deferred until the GCU connection is ready."，UI 无任何提示 | `Gpu\GPUModeControl.cs:347-354` |
| 模式切换发布失败 | 解除 8 秒过期包过滤窗口后抛出；上层只记日志 | `MechrevoHw.cs:2196-2213`、`MechrevoService.cs:194-210` |
| 电池保护确认失败 | 返回 false + 日志 "SetBatteryProtection not confirmed"，UI 不弹窗 | `MechrevoHw.cs:2262-2263` |
| GPU 模式确认失败 | 返回 false + 日志 "SetGpuMode not confirmed"，UI 不弹窗 | `MechrevoHw.cs:2315-2316` |
| 超频直写被拒且无提权助手 | 日志 "GPU OC direct write was rejected and no elevated helper is available."，UI 无提示 | `MechrevoHw.cs:2732-2735` |
| 灯效下发未确认 | 日志 "SetLightPower(…) not confirmed"，UI 无提示 | `MechrevoService.cs:1994` |
| 隔离状态展示 | GCU 进程不在时文案为"已隔离 · GCU 后台待连接"，不算失败但易误读 | `OfficialConsoleIsolation.cs:136` |

---

## 6. 一句话总结

除 #11（GPU 超频，驱动直写）、#13（直连 RGB HID）、#16（液冷 DirectBle 路由）、#18（Windows 个性化）、#20（杀进程预检）外，**其余全部硬件控制功能都经 127.0.0.1:13688 的 MQTT 通道，GCU 不在即不可用**；液冷是唯一具备完整三路由回退的功能，GPU 模式切换是唯一有"Windows 重启兜底"的功能。
