# 原版 ControlCenterX UI 完整开关清单（二次开发依据）

> 来源：CCUWinUI.dll 反编译（155,805 行）全量分析，2026-08-04
> 用途：简洁版控制台的移植功能清单。所有开关 = 页面控件 → MQTT topic → 载荷结构。

## 通用约定
- Broker: localhost:13688, QoS2, JSON 载荷；UI 发布控制，订阅 Status
- 页面进入时发 `GETSTATUS`/`GET_FAN_SPEED_CURVE_SETTING` 拉取状态
- 机型能力从 `Customize/Info`、`Customize/SupportInfo`、注册表 `HKLM\OEM\GamingCenter2\ItemSupport` 查询
- 动作常量枚举：`SettingAciton`（CCUWinUI.decompiled.cs L139083）、`RamFanMode1p5`（L138092）、`LightBarAction`（L139033）

## 1. 散热与性能（HomePage + CustomModeSettingPage）
| 功能 | 控件 | 发布 topic | 载荷要点 |
|---|---|---|---|
| 运行模式切换(办公/游戏/性能/自定义) | LeftRightSwitchButton+RadioButton | Fan/Control + LCHWOC/Control | `{Action: OPERATING_OFFICE_MODE/GAMING_MODE/TURBO_MODE/CUSTOM_MODE, ProfileIndex}` + `{IsNormalRun:0/1/2}` 或 `{IsCustomRun:true}` |
| Turbo 子模式(静音/超频) | TurboSubModePicker | Fan/Control | `{Action: SET_CPU_CORE_OFFSET_SILENT, SILENT=1}` / `{SET_CPU_CORE_OFFSET_EXTREME, EXTREME=1}` |
| Turbo GPU OC | ToggleButton | Fan/Control | `{SET_OPERATING_MODE_DETAIL, GpuCoreClockOffsetOC}` |
| 风扇增强 FanBoost | ImageToggleButton | Fan/Control | `{Action: FAN_BOOST_ON/OFF}` |
| 智能平衡 | ToggleSwitch | Fan/Control | `{SET_BALANCE_SMART, SmartBalanceSwitch:1/0}` |
| 自定义 Profile 选择 | ComboBox | Fan/Control | `{OPERATING_CUSTOM_MODE, ProfileIndex}` |
| 自定义 Profile 命名 | TextBox+Button | Fan/Control | `{SET_CUSTOM_PROFILE_OSD_STRING, ProfileName}` |
| 恢复默认 | Button | Fan/Control | `{RESTORE_OPERATING_MODE_DETAIL}` + `{RESTORE_FAN_SPEED_CURVE_SETTING, Name}` |
| CPU/GPU 风扇曲线(16点) | LineChart 拖拽 | Fan/Control | `{SET_FAN_SPEED_CURVE_SETTING, Name, Type:CPU/GPU, T0..T15}` |
| 风扇独立控制 | ToggleSwitch | Fan/Control | `{SET_FAN_CONTROL_RESPECTIVE, Name, FanControlRespective}` |
| 风扇切换速度 | ToggleSwitch+OCSlider | Fan/Control | `{SET_OPERATING_MODE_DETAIL, FanSwitchSpeedEnabled, FanSwitchSpeed}` |
| HWOC 超频 | ToggleSwitch | Fan/Control | `{SET_OPERATING_MODE_DETAIL, OverClockingSwitch:"1"/"0"}` |
| CPU 高级性能 | ToggleSwitch | Fan/Control | `{CPUPerformanceAndOverClockMenuSwitch_ON/OFF}` |
| PL1/PL2/PL4 | OCSlider×3 | Fan/Control | `{SET_OPERATING_MODE_DETAIL, PL1/PL2/PL4}` |
| Intel TccOffset | ToggleSwitch+OCSlider | Fan/Control | `{CpuTccOffsetSwitch, CpuTccOffset}`（按 TjMax 换算） |
| AMD SPL/SPPT/FPPT/TjMax/核心 OC | OCSlider×6 | Fan/Control | `{CpuAmdSPL/SPPT/FPPT/CpuAmdTccTarget/CpuAmdCoreFreqOC/CpuAmdCoreVoltageOC}` |
| GPU ConfigurableTGP | ToggleSwitch+OCSlider | Fan/Control | `{GpuConfigurableTGPTarget}`（与 DynamicBoost 互斥） |
| GPU DynamicBoost | ToggleSwitch+OCSlider | Fan/Control | `{GpuDynamicBoostSwitch/GpuDynamicBoost}` |
| GPU 核心/显存频率偏移 | OCSlider×2 | Fan/Control | `{GpuCoreClockOffsetOC/GpuMemoryClockOffsetOC}` |
| 游戏白名单 | 命令 | Fan/Control | `{OPERATING_GAME_WHITE_LIST, GameWhitelistSwitch}` |
状态订阅：Fan/Status(MyRamFan1p5 L138116)、Fan/Table(FanTable1p5 L138290)、LCHWOC/Status、BT_LC/Status、WhisperMode/Status

## 2. GPU / 显卡切换（GpuSettingPage）
| 功能 | 控件 | 发布 topic | 载荷要点 |
|---|---|---|---|
| 核显模式(iGPU Only) | RadioButton | Setting/Control | `{Action: IGPU_ONLY_CONNECT_RB_ON}`（轮询+超时回滚） |
| 独显直连 | RadioButton | Setting/Control | `{Action: IGPU_ONLY_CONNECT_RB_OFF}` |
| 自动模式 | RadioButton | Setting/Control | `{Action: IGPU_ONLY_CONNECT_RB_AUTO}` |
| 切换确认+重启 | ContentDialog | Setting/Control | `{DGPU_DIRECT_CONNECT_TOGGLE_ON/OFF/IGPU}` + `{IGPU_ONLY_CONNECT_RB_OFF, SetToWMIEC:"OK"}` + `{DGPU_DIRECT_CONNECT_RESTART}` |
| GPU 节能模式 | Button | Setting/Control | `{Action: GPU_POWERSAVEINGMODE}` |
| 拔外接显示器 | ToggleSwitch | Setting/Control | `{GPU_DISCONNECTMONITOR, Enable}` |
| 电池省电(DC一次) | ToggleSwitch | Setting/Control | `{GPU_DC_ONCE, Enable}` |
| 刷新率 | RadioButton | Setting/Control | `{GPU_HZSETTING, Hz}` |
| 自动刷新率 | ToggleSwitch | Setting/Control | `{GPU_DC_HZ, Enable}` |
状态订阅：Setting/Status(DiscreteGpuDirectConnectionSwitch_Status 等)、Fan/Status(IsAC)。独显直连仅交流电可用、需重启。

## 3. 显示（DisplaySettingPage / ColorCalibrationView）
| 功能 | 控件 | 发布 topic | 载荷要点 |
|---|---|---|---|
| 色彩校正开关 | ToggleSwitch | Setting/Control | `{COLOR_CALIBRATION_ON/OFF, FileName}` 关时带 `{DISPLAY_FEATURE_STATUS_ON}` |
| 色域(默认/sRGB/P3/AdobeRGB) | RadioButton×4 | Setting/Control | `{COLOR_CALIBRATION_ON_DEFAULT/SRGB/P3/ADOBERGB}` |
| 局部调光 | ToggleSwitch | Setting/Control | `{LOCALDIMMING_ON/OFF}` |
| LCD Overdrive | ToggleSwitch | Setting/Control | `{LCDOverdrive_ON/OFF}` |
| OLED 屏保 | ToggleSwitch | Setting/Control | `{ScreenSaver_ON/OFF}`（仅 PanelType==2） |
| 任务栏自动隐藏/透明/深色 | ToggleSwitch×3 | 无MQTT | Win32 SHAppBarMessage / 注册表 EnableTransparency / AppsUseLightTheme |
状态订阅：Setting/Status、GPUDevice/Status。HDR 开启时禁色彩校正；支持性查 ItemSupport。

## 4. RGB 灯效（LightPage 子页）
| 功能 | 控件 | 发布 topic | 载荷要点 |
|---|---|---|---|
| 键盘开关 | ToggleSwitch | Keyboard/Ctrl | `{function: SetPower, powerstatus:1/0}` |
| 键盘效果/亮度/速度/方向/颜色 | RadioButton+Slider+色块+拾色器 | Keyboard/Ctrl | `{mode:Lighting, function:SetEffectALL, effect, light, speed, direction, nv_save:"0", color:{isCircular, ColorBlocks, ColorBuffer[RGB_S]}, alphabet}` |
| 彩色波浪 | ToggleSwitch | Keyboard/Ctrl | effect 在 colorfulwave↔wave |
| Welcome 模式 | Button | Keyboard/Ctrl | `{mode: Welcome}` |
| 单区键盘 | RadioButton+色块 | Keyboard/Ctrl | `{effect:Single/Manual, MonochromeIndex, ManualIndex1-6, ManualInterval, light}` |
| 单色键盘灯 | ToggleSwitch | Setting/Control | `{SINGLE_COLOR_KBBL_STATUS_ON/OFF}` |
| 逐键用户模式 | 键盘布局+拾色器 | Keyboard/Ctrl | `{effect:UserMode, color:{isCircular:false, ColorBuffer[每键RGB_S]}}` |
| 灯带开关/效果/亮度/速度/方向/颜色 | 同键盘 | HidLightbar/Ctrl | 同结构 |
| 铰链/Logo/同步灯带 | 同上 | HidLightbar_Hinge/Ctrl / HidLightbar_Logo/Ctrl / HidLightbar_Sync/Ctrl | 同结构 |
| EC 灯带(旧机型) | ToggleSwitch+Slider×3 | MyRgbLightbar/Control | `{Action: POWER_ON/OFF, COLORFUL_ON/OFF, BREATHINGLIGHT, DEFAULT, <R/G/B>, Level}`（电池下动作名加 _DC） |
| 灯效定时关闭 | ComboBox | Setting/Control | `{KEYBOARD_LIGHTBAR_TIMER_ON, Mins}` / `{OFF}` |
状态订阅：Keyboard/Status、HidLightbar*/Status、MyRgbLightbar/Status。键盘布局分 MEZone 多机型(97~103键)。

## 5. 电池（PowerManagePage）
| 功能 | 控件 | 发布 topic | 载荷要点 |
|---|---|---|---|
| 电池健康模式(性能/平衡/健康) | RadioButton×3 | BatteryProtection/Control | `{Action: PERFORMANCEDMODE/BALANCEDMODE/HEALTHYMODE}`；查询 `{Report:"GET"}` |
| USB 充电 | ToggleSwitch | Setting/Control | `{USB_CHARGER_ON/OFF}` |
| 来电自动开机 | ToggleSwitch | Setting/Control | `{ACRECOVERY_TOGGLE_ON/OFF}` |
| 高性能电源模式 | ToggleSwitch | Setting/Control | `{HIGHPERFORMANCEPOWERMODE_ON/OFF}` |
| 电池 Logo 灯 | ToggleSwitch | Setting/Control | `{BATTERYLOGO_TOGGLE_ON/OFF}` |
| 深度睡眠+时间 | ToggleSwitch+ComboBox | Setting/Control | `{DEEPSLEEP_ON/OFF}`、`{DEEPSLEEP_ON, Secs}`(900-7200) |
| 电源灯开关+亮度 | ToggleSwitch+OCSlider | Setting/Control | `{PowerLight_ON/OFF, Brightness}` |
状态订阅：System/BatteryProtection、Setting/Status。

## 6. 快捷开关（QuickSwitchPage）
| 功能 | 发布 topic | 载荷 |
|---|---|---|
| 触摸板/摄像头/蓝牙/WiFi/Win键/OSD/小键盘/Fn/Copilot/触摸板切换键 | Setting/Control | `{Action: TOUCHPAD_*/WEBCAM_*/BT_*/WIFI_*/WINKEY_*/OSD_HIDDEN_*/NUMPAD_*/FNKEY_*/COPILOTKEY_*/TOUCHPAD_TOGGLE_*}` |
| Uni/Omni（互斥） | Setting/Control | `{Uni_ON/OFF}` / `{Omni_ON/OFF}` |
状态订阅：Settings/DeviceSwitchItemStatus(WIFI/BT/触摸板/摄像头)、Setting/Status。快捷菜单本地可定制≤6项。

## 7. 设置（SettingsPage）
| 功能 | 发布 topic | 载荷 |
|---|---|---|
| 语言/自动检测 | Languages/Control + Languages/AutoDetect | `{Action: SET, Lang}` / `{DetectOn/DetectOff}` |
| OTA 检查 | OTA/CCU6Control | `{Action: OTA}` |
| 打开应用商店 | Setting/Control | `{OPEN_APP_STORE}` |
| 背景/主题色 | 无 | 本地 AppSetting |
| 系统监控启停 | System/Control | `{System_ON/OFF}`（窗口激活/5s定时器） |

## 8. 服务端命令全集（GCUService 字符串提取）
补充服务端词汇：`GETSETUPINFO/GETSTATUS/GETSUPPORT`、`GET_FAN_SPEED_CURVE_SETTING/RESTORE_FAN_SPEED_CURVE_SETTING`、`COPY_AC_SETTINGS`、`CPU_SILENT_MODE_STATUS_*`、`DISABLE_PASSIVECOOLING_MODE_*`、`FAN_OFFICE_MODE_BASIC/ADVANCED_*`（T1-T5 PWM、LV1-4、MIN_SPEED/MIN_TEMP）、`FAN_TURBO_MODE_LV1-4/BOOST/CPUBOOST`、`POWER_PLAN_*`、`SYSPOWER_*`、`SETSCREENBRIGHTNESS`、`DISPLAY_*_MODE(+RECOVERY/VALUE)`、`HDR_STATUS_CHANGE`、`ICCPROFILESETING`、`AUTOBRIGHT_*`、`LIGHT_BAR_TRIGGER`、`KEYBOARD_LIGHTBAR_TIMER_*`、`NV_CTRL_PANEL_*`、`Omni/Uni/GAMER/BENCHMARK`、`OSDTP_*`、`TOUCHPAD_LED_*`、`BT_OFF/BT_ON`、`USB_CHARGER_*`、`BATTERY_CHARGINGLIMIT_SETTING`、`ACRECOVERY_TOGGLE_*`、`BATTERYLOGO_TOGGLE_*`、`DEEPSLEEP_*`、`FNKEY_LOCK/UNLOCK`、`FN_WITH1_HOTKEY_TOGGLE_*`、`COPILOTKEY_LOCK/UNLOCK`、`NUMPAD_LOCK/UNLOCK`、`WinKey`、`OSD_*`(全部 OSD 事件)、`Tray_*`(托盘模式图标)、`LIGHTBAR_*`、`SingleColorKBBL`、`GPUDevice` 相关全部

## 9. 存疑项（移植时需实测确认）
1. Aurora 开关——只读状态，未找到发布点
2. GPU Whisper 模式——只读
3. TypeC 适配器优先——只读
4. POWER_PLAN_*、GAMER、BENCHMARK、AUTOBRIGHT、LIGHT_BAR_TRIGGER——服务端/旧版使用
5. EC 灯带 RGB 滑条动作名——从 XAML 确认（推测 R/G/B）
6. 液冷自动模式载荷——疑似复用 LC_FanCtrl index
