# M1 探测发现（2026-08-04，实测于本机）

## 1. Broker 连接规则（已解决全部疑团）

| 项目 | 结论 |
|---|---|
| 协议版本 | **MQTT 3.1.1（V311）**。MQTTnet 5.x 默认 V5 被拒（"Error while authenticating"），必须 `.WithProtocolVersion(V311)` |
| 凭据 | `UWPClient_User_5` / `UWPClient_Pwd888881772688_5`，clientId `UWPClient_5` |
| CleanSession | `false`（与 UI 一致） |
| 之前 M2Mqtt 断连原因 | M2Mqtt.Net 客户端自身问题（MQTTnet + V311 完全正常，发布 System/Control 不断连） |
| 推流条件 | **连接 + 订阅即自动推流**（约 5s 周期），无需任何发布。System_ON 非必需（但触发 BatteryInfo/HardwareInfo/FanErrorInfo 额外消息） |

## 2. 连接即收（无需请求）

- `Customize/SupportInfo`：能力矩阵全量 JSON（含 CustomizeTarget=56、ProjectID=26、BIOS_PROJECT_ID=IDY、LiquidCoolingSupport=1 等）
- `Keyboard/Status`：键盘全量状态（solution=ITE、type=MEZone_3p1nd_101、effect/light/speed/direction/color、rkgcolor 7 色块、powerStatus、currentPowerMode=AC）
- `Settings/DeviceSwitchItemStatus`：`{"WIFIEnable":false,"BTEnable":true,"WebCamEnable":true,"TochpadEnable":false,"ScreenBrightness":60}`
- `Languages/Control {"Action":"EnableTray"}`（broker 推来的托盘指令）

## 3. 请求 → 响应

| 发布 | 响应 |
|---|---|
| `Keyboard/Ctrl {function:Init}` | 回显（broker 转发机制确认） |
| `Customize/Control {Action:GETSUPPORT}` | 回显 + `Customize/Info {"LightbarType":"3","KeyboardType":"1","LEDLightbarColorful":"1","LEDLightbarBreathEffect":"1","ProjectID":"26","SupportColorCalibration":2,"SupportRamFan1p5":"1","BatteryProtectionType":"2"}` |
| `Fan/Control {Action:GETSTATUS}` | 回显 + `Fan/Status`（全量：IsAC/OperatingMode/PL1-4/TccOffset/GPU TGP/DynamicBoost/WhisperMode/AMD 参数/TjMax/GameWhitelist 等，见下） |
| `Setting/Control {Action:GETSTATUS}` | 回显 + `Setting/Status`（全量开关状态） |
| `Keyboard/Ctrl {Action:GETSTATUS}` | 回显 + `Keyboard/Status` 重发 |
| `System/Control {Action:System_ON}` | 回显 + `System/FanErrorInfo`、`System/BatteryInfo`、`System/HardwareInfo` |

## 4. 周期推流（System/#）

```
System/FanInfo:     {"CpuFanDuty":25,"GpuFanDuty":25,"CpuFanRpm":1509,"GpuFanRpm":1746}
System/CpuInfo:     {"CpuUsage":"13","CpuTemperature":"45","CpuFrequency":"4439","CpuMaxFrequency":"2700"}
System/MemoryInfo:  {"TotalMemory":31.5,"TotalUsingMemory":10.6,"MemoryUsage":34}
System/GpuInfo:     {"GpuUsage":"20","GpuTemperature":"33","GpuCoreFreq":"615","GpuMemFreq":"810","GpuPState":"0","GpuMem":"16302"}
System/NetworkInfo: {"NetworkDownload":"232 Kbps","NetworkUpload":"3.9 Mbps"}
```

## 5. Fan/Status 全字段（M3 功耗墙/曲线页的地图）

IsAC=true | OperatingMode=3 | Gaming/Office/Turbo/CustomProfileIndex | FanBoostEnable=0 | ProfileName="Mode4_Profile1" | FAN_FanSwitchSpeedEnabled=1 | FAN_FanSwitchSpeed=1700 | FAN_TableName="M4T1" | CPU_PL1/PL2/PL4=210（Min10 Max210）| CPU_TccOffsetSwitch=1 | CPU_TccOffset=10（Max10）| GPU_ConfigurableTGPSwitch=1 | GPU_ConfigurableTGPTarget=150（Min80 Max150）| GPU_DynamicBoostSwitch=0 | GPU_DynamicBoost=5（Min5 Max25）| GPU_WhisperModeSupport=true | GPU_WhisperModeSetting | CPU_AmdSPL=75/SPPT=85/FPPT=145 | CPU_AmdTccTarget=15 | TjMax=105 | CPU_PL4_Double_Flag=1 | TurboModeOption=1 | PowerMode=1 | SmartBalance=0 | GameWhitelistSwitch=0 | GPU_CoreClockOffsetOC=150 | GPU_MemoryClockOffsetMaximum=1000

## 6. Setting/Status 全字段（M4 快捷开关/电池页的地图）

WinKey="WINKEY_STATUS_UNLOCK" | LightBar="LIGHTBAR_STATUS_ON" | UsbCharger="USB_CHARGER_STATUS_OFF" | DGpu="NV_CTRL_PANEL_AUTOSELECT" | OSD="OSD_HIDDEN_OFF" | DisplayFeatureStatus="DISPLAY_FEATURE_STATUS_ON" | DisplayMode="DISPLAY_STANDARD_MODE" | SingleColorKBBL="SINGLE_COLOR_KBBL_STATUS_ON" | CloseTimer=10 | NumPad="NUMPAD_LOCK" | FnKey="FNKEY_LOCK" | TouchpadToggle="TOUCHPAD_TOGGLE_ON" | AcRecoverySwitch_Status="ACRECOVERY_TOGGLE_OFF" | DiscreteGpuDirectConnectionSwitch_Status="DGPU_DIRECT_CONNECT_TOGGLE_ON" | IGpuOnlyConnectionSwitch_Status="IGPU_ONLY_CONNECT_RB_OFF" | HighPerformancePowerModeSwitch="HIGHPERFORMANCEPOWERMODE_ON" | CopilotKey="COPILOTKEY_UNLOCK" | ColorCalibrationSwitch=1 | DeepSleepSwitch="DEEPSLEEP_OFF" | DeepSleepTime=1800 | LCDOverdriveSwitch="LCDOverdrive_OFF" | UniSwitch="Uni_OFF" | OmniSwitch="Omni_OFF" | PowerLightSwitch=0 | PowerLightBrightness=100 | 校色参数（Gaming/Video/Read/Cutomized × Brightness/Red/Green/Blue/ColorTemp/Contrast）

## 7. 其他

- `GPUDevice/Status`: `{"currentSaveingMode":0,"DisconnectMonitor":false,"DC_Once":false,"DC_HZ":false,"MODE_HZ":false,"Auto":false,"currentHZ":0,"currentHZList":["60","240","300"],"AutoEnable":false}`
- `System/HardwareInfo`: Intel Ultra 9 275HX / RTX 5080 Laptop GPU / BIOS N.1.32MRO60 / EC 2.6.0 / MECHREVO YAOSHI Series
- `System/BatteryInfo`: `{"BatteryLifePercent":"96","BatteryLifeRemaining":"-1","BatteryAbnormal":"0","BatteryCapacity":"0 mWh","BatteryCycleCount":"15"}`
- 本机键盘：ITE 方案（IT8297 类）、MEZone_3p1nd_101 布局、7 色块默认
- 无断连现象：整个握手期间连接稳定

## 8. MqttBackend 连接序列（固化到代码）

```
Connect (V311, cleanSession=false, UWPClient_5 凭据)
  → Subscribe(System/#, Fan/#, Setting/#, ...)
  → 自动推流开始（5s 周期）
  → 需要时：Customize/Control GETSUPPORT、各 GETSTATUS、System/Control System_ON
```
