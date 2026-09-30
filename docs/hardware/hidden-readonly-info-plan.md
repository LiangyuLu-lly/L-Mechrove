# 隐藏信息只读展示：实现方案（供电方式 · 适配器功率 · 风扇占空比 · 电池）

状态：已实现（beta21，P1–P7、P9；P8 `power-watch` 未做，§7 脚本代替）。调研时间 2026-09-29，分支 `beta21-wip` @ `5f104a3`。
实现偏差：`PowerInputSample` / `BatteryStatusReading` 改为引用类型（无锁发布，驱动卡住时界面线程不等锁），样本多一个
`DecodeEnabled`；解码开关 = 服务档位 ≠ Legacy1020 且独显代际 ≠ 10/20；充电中 / 放电中在没有功率读数时另有文案；
双插先按圆口显示并在 tooltip 标注；温度帧同样按 6 s 新鲜度显示「—」。真机只读探针（`PowerInputMachineProbe`）在本机读回
`0x7CC=0x81`、`0x49F=0x5A`，电池行显示「圆口供电 · 已充满」，与 §4 一致。
本机只读核对只用了 EC 读 IOCTL `0x9C40A488` 与 Windows 标准电池接口，没有任何 EC 写入、没有发布 MQTT 命令。
前置文档：`docs/hardware/pd-typec-power.md`（Type-C 寄存器初查）、`docs/hardware/gcu-modes-and-profiles.md` §3（风扇占空比 ×2）。
证据键同 `docs/hardware/gpu-modes-implementation-plan.md` §1.1（`S40` = `_decompiled/gcu40-51751-27`，真实方法体；`L` = 本机实测）。

## 0. 结论

1. **风扇占空比已经在主界面了**：遥测行「CPU 54°C 25W 2047rpm 35%」里的百分比就是它，来自 GCU `System/FanInfo`
   （服务端读 EC `0x75B/0x75C` 再 ÷2，每 2 s 推一次）。本机 EC `0x57`→MQTT `43`、`0x46`→35% 对得上。只需小改：
   风扇停转时显示 `0%`、数据超过 6 s 未更新时隐藏；**不新增 EC 读取**。
2. **供电方式可以做**：EC `0x7CC` bit0 = 圆口在插，`& 0x06` = Type-C 在充电，配合 Windows `PowerLineStatus`。
   本机 `0x7CC=0x81`、交流在线 → 圆口，与厂商判据一致。Type-C 分支代码有出处但本机没实测过，需要用户插拔一次（§7）。
3. **适配器额定瓦数只在能确定时显示**：`0x49F & 0x78` 查厂商表；本机编码 `0x58`（88）不在表内，厂商会报 150 W，
   我方隐藏瓦数、tooltip 写「未知（编码 0x58）」。需要用户报一下适配器铭牌功率，按机型补表。
4. **电池**：循环次数可靠（EC `0x4A6/0x4A7`=17，MQTT 同值），已显示；**充电状态**（充电中 / 已接通未充电 / 已充满 / 已到上限 /
   放电）用 Windows 电池 IOCTL 的 `PowerState`，可靠，新增；**电池健康不上主界面**——Windows 报满充 = 设计 = 99 072 mWh（固件合成值），
   EC 的 _BIF 镜像另有 5800/6400 mAh，二者冲突。
5. **读取通道**：复用 `Probe.IEcReadTransport`（只读接口，唯一 IOCTL 就是读），每 5 s 至多读 2 个字节，挂在现有 2 s 传感器节拍里
   （窗口隐藏时不跑），电源事件时强制刷新一次；读失败就隐藏该段，不显示旧值。
6. **不做**：PD 协商功率 / 反向供电功率（地址未知）、`0x7CC` bit4/5/7、`0x490 ecPowSource`、电池温度（数值合理但厂商没用过，待验证）。
7. **不改行为**：`Program.ReadPowerSource` 继续只分电池/插电（它驱动性能模式重放与显卡自动档），Type-C 只做展示。

## 1. 信息项总表

| 项目 | 来源 | 解码 | 厂商出处 | 定义于哪代服务 | 本机值（2026-09-29） | 可信度 | 决定 |
|---|---|---|---|---|---|---|---|
| 供电方式 | EC `0x7CC` + Windows | 见 §2.1 | `S40 MyECIO/MyEcCtrl.cs:144-157`、`GCService5/GPUDeviceItem.cs:128-145` | 30（仅 Type-C 位，D）/ 40（P）/ 50（B）；10/20 无 | `0x81` + 交流在线 → 圆口 | 圆口 P；Type-C P（代码）+ 未实测 | 做 |
| 适配器额定功率 | EC `0x49F` | `& 0x78` 查表，表外 = 未知 | `S40 MyEcCtrl.cs:106-143` | 40（P）/ 50（B）；30、10/20 无 | `0x5A` → `0x58` 表外 | 表内 P（厂商只用来区分机型，没显示过） | 做（仅表内） |
| 风扇占空比 | MQTT `System/FanInfo` | 服务端 EC 字节 ÷2 | `S40 MyControlCenter/FanInfo.cs:10-23` | 10/20（D，其 ECSpec 无 `0x75B/0x75C`）/ 30（D）/ 40（P）/ 50（L 对照） | 35–36%（EC `0x46/0x48`） | P（本机 EC 与 MQTT 对照一致） | 已有，微调 |
| 风扇转速 | MQTT `System/FanInfo` | CPU `0x464` 高/`0x465` 低；GPU `0x46C` 高/`0x46B` 低 | `S40 FanInfo.cs:24-41` | 同上 | 2043–2151 / 2258 rpm | P | 已有 |
| 循环次数 | MQTT `System/BatteryInfo` | EC `0x4A7<<8 \| 0x4A6` | `S40 MyControlCenter/BatteryInfo.cs:62-70` | 10/20、30（D）/ 40（P）/ 50（L 对照） | 17 | P | 已有，挪进 tooltip |
| 充电状态 | Windows `IOCTL_BATTERY_QUERY_STATUS` | `PowerState` 标志 + `Rate` | 厂商只用 `PowerLineStatus`（`S40 GCUService.MySystem/BatteryProtection2.cs:380-404`） | 与代际无关 | `PowerState=0x1`、`Rate=0`、100% → 已充满 | P（OS 标准接口） | 做 |
| 电池异常 | MQTT `BatteryAbnormal` | 0 正常 / 1 无电池 / 3 EC `0x494≠0` | `S40 MyControlCenter/BatteryInfo.cs:42-50,71-90`；C50 文案 `PowerManageViewModel.cs:1711-1720` | 40（P）/ 50（L） | `"0"` | P | 已有（异常时标红） |
| 电池健康 | Windows / EC _BIF 镜像 | 见 §2.6 | `S40 BatteryModel/BM_Manager.cs:19-24`（未被 UI 使用） | — | OS 100%（合成）vs EC 推断 90.6% | 冲突 | **不做** |
| 电池温度 | EC `0x4A2/0x4A3` | 0.1 K（推断） | 仅常量 `ecBt1Temperature`（`S40 Define/ECSpec.cs:133`），无读取方 | — | `0x0BF4` = 32.9 °C | I | 不做（待验证） |
| PD 输入 / 反向供电功率 | 未知 | — | `GetPowerFromTypeC` 只在 S50 标识符里（B） | 50 | — | UNKNOWN | 不做 |

## 2. 逐项说明

### 2.1 供电方式：EC `0x7CC ADDR_COMPLEX_POWER_STATUS`（`S40 Define/ECSpec.cs:405`）

厂商读法（P）：

```csharp
// S40 MyECIO/MyEcCtrl.cs:144-157
GetTypeCsatusFromEC()        => Read(1996) & 0x36;   // “Type-C 状态”
GetRoundSocketStatusFromEC() => Read(1996) & 0x01;   // 圆口在插
// S40 GCService5/GPUDeviceItem.cs:128-145
isTypeCCharging()      => (TypeC & 6) > 0;
isRoundSocketCharging() => round == 1 || (ACLineStatus == 1 && (TypeC & 6) == 0);
```

各代覆盖：`S40` 两个方法都有方法体；`S30` 只声明了 `GetTypeCsatusFromEC`（`_decompiled/gcu30-41747/MyECIO/MyEcCtrl.cs:99`），
`0x7CC` 常量在（`Define/ECSpec.cs:389`），没有圆口方法；10/20 的 `gcuu-service/Define/ECSpec.cs` **没有** `0x7CC`；
S50 有 `GetRoundSocketStatusFromEC`、`GetTypeCsatusFromEC` 标识符（B，本次字节扫描确认）。
`ItemSupport\TypeCSupport` 不是「有 Type-C 供电」：它等于 `GetTypeCAdaptorPrioritySupport()`（`S40 MyControlCenter/CustomizeCtrl.cs:438,466`），
而这个方法在 S40 恒为 false（`MyEcCtrl.cs:254-258`）。本机该值为 0，不能拿它隐藏 Type-C 状态。

我方解码（`PowerInputDecoder.Decode`）：

| Windows 交流 | `0x7CC` | bit0 | `& 0x06` | 显示 | 说明 |
|---|---|---|---|---|---|
| 离线 | 任意 | 任意 | 任意 | 电池 | Windows 为准；EC 仍报在插时记一次日志 |
| 在线 | 读不到 / 本机不解码 | — | — | 外接电源 | 不猜类型 |
| 在线 | 可读 | 1 | 0 | 圆口 | 厂商同判据，本机实测 |
| 在线 | 可读 | 0 | ≠0 | Type-C | 厂商 `isTypeCCharging`，本机未实测 |
| 在线 | 可读 | 1 | ≠0 | 圆口 + Type-C | 双插语义 UNKNOWN，§7 第 5 步确认前先按「圆口」显示、tooltip 标注 |
| 在线 | 可读 | 0 | 0 | 外接电源 | 厂商会当成圆口；我方不猜 |

「本机不解码」= 服务档位是 `Legacy1020`（10/20 没有这个寄存器；档位定义见显卡方案 G1）。`0x36` 里的 bit4/5 与 bit7
（本机 bit7=1）厂商从不读，不用。

### 2.2 适配器额定功率：EC `0x49F ADDR_BIOS_INFO_3_BYTE`（`S40 Define/ECSpec.cs:131`）

`S40 MyEcCtrl.cs:106-143`：`Read(1183) & 0x78` →
`0→330、8→230、16→180、24→150、32→120、40→90、48→65、56→40、64→280`，表外**默认 150**。

- 厂商只在 `GetProject2ExID` 里用它区分 6151/6155 等机型（`MyEcCtrl.cs:487-583`，判据 `!= 150`），任何 UI 都没显示过
  （C50 对 `Adapter|Watt|RoundSocket|TypeCPower` 0 命中）。
- 同一字节 bit1 = 狂暴模式支持（`MyEcCtrl.cs:247-253`、`MyControlCenter/TrayCtrl.cs:101-104`），解码时必须先掩码。
- 我方已有一份照抄厂商的 `ModelRegistry.AdapterWattFor`（`src/MechrevoLiteWin/Hardware/ModelRegistry.cs:217-230`），带默认 150，
  专供机型展开（`:208-210`），**不能拿来显示**；展示用新写的 `TryDecodeAdapterWatts`，表外返回 false。
- 本机：`0x5A` → `0x58`（88，即码位 11），表外。`pd-typec-power.md` 记为 330 W 适配器，本次没有核对铭牌。
- 表内值只在「圆口」状态显示（Type-C 时这个字节反映的是什么不清楚），并在 tooltip 注明「按官方表换算」。

### 2.3 风扇占空比（已有）

- 厂商：`GetEcCpuFanDuty = Read(1883)/2`、`GetEcGpuFanDuty = Read(1884)/2`（`S40 MyControlCenter/FanInfo.cs:10-23`，
  常量 `ADDR_EC_MAIN_FAN_L/R_DUTY_BYTE`，`Define/ECSpec.cs:243,245`）；`System/FanInfo` 每 2 s 发布
  （`MyControlCenter/MySystemManager.cs:148,450-453`），非 NVIDIA 机型只有 CPU 两项（`:644-661`），定时器在收到 `system_on` 后才启动（`:253`）。
- 10/20 的 ECSpec 里没有 `0x75B/0x75C`（它读哪个地址看不到，方法体被破坏），所以**不做 EC 直读**，一律走 MQTT。
- 我方：`OnSystemFanInfo`（`src/MechrevoLiteWin/Hardware/MechrevoHw.cs:1571-1590`，缺字段 = -1）→ `HardwareControl.AttachMechrevoHw`
  （`HardwareControl.cs:81-102`）→ 遥测行 `TelemetryParts`（`Settings.V2.cs:154-167`，只在 `duty > 0` 时显示，`:165`）与托盘提示
  （`Settings.cs:4769-4776`）。连接时先发 `System_ON`（`MechrevoHw.cs:1281-1283`）。
- 本机：基线帧 EC `0x75B/0x75C=0x57/0x57` ↔ MQTT `CpuFanDuty/GpuFanDuty=43/43`；今天 `0x46/0x46`、`0x48/0x48`（35%/36%）。

### 2.4 循环次数（已有）

- 厂商：`GetECBatteryCycleCount = Read(1191)<<8 | Read(1190)`（`S40 MyControlCenter/BatteryInfo.cs:62-70`），
  `System/BatteryInfo` 每 60 s 一次（`MySystemManager.cs:145,445`），连接时一次（`:511`）。30、10/20 服务有同名方法（方法体被破坏：
  `gcu30-41747/MyControlCenter/BatteryInfo.cs:55`、`gcuu-service/MyControlCenter/BatteryInfo.cs:47`）。
- Windows 这条路不可用：`BATTERY_INFORMATION.CycleCount=0`，WMI `BatteryCycleCount=0`（L）。
- 我方：解析在 `MechrevoHw.cs:1592-1604`；显示在电池行右列，只在没有充放电功率时出现（`Settings.cs:4802-4803,5325-5333`）。
  `BatteryCapacity` 字段厂商写死 `"0 mWh"`（`S40 MyControlCenter/BatteryInfo.cs:52-55`），我方已过滤（`Settings.cs:5316-5323`）。

### 2.5 充电状态（新增）

`IOCTL_BATTERY_QUERY_STATUS` 返回的 `BATTERY_STATUS.PowerState`：`0x1 POWER_ON_LINE`、`0x2 DISCHARGING`、`0x4 CHARGING`、`0x8 CRITICAL`。
我方 `BatteryRateReader`（`src/MechrevoLiteWin/Battery/BatteryRateReader.cs:55-77`）已经发这个 IOCTL，但只留了 `Rate`，改成顺带返回
`PowerState`，不增加 I/O。

| 条件 | 显示 |
|---|---|
| `CHARGING` 或 `Rate>0` | 充电 {W} |
| `DISCHARGING` 或 `Rate<0` | 放电 {W} |
| `POWER_ON_LINE`、非充电、`Rate==0`，电量 ≥ 99% | 已充满 |
| 同上，充电上限 <100 且电量 ≥ 上限 − 5（`EcChargeLimit.RechargeHysteresis`，`Hardware/EcChargeLimit.cs:51`） | 已到充电上限 |
| 同上，其余 | 未充电 |

本机：`PowerState=0x1`、`Rate=0 mW`、`Capacity=99072 mWh`、`Voltage=16560 mV`，`SystemInformation.PowerStatus` = Online / High / 100% → 已充满。

### 2.6 电池健康（不做）

| 来源 | 设计容量 | 满充容量 | 结论 |
|---|---|---|---|
| Windows `IOCTL_BATTERY_QUERY_INFORMATION`（L） | 99 072 mWh | 99 072 mWh | 100%，与 17 次循环后的实际不符，像是固件把设计值填了两遍 |
| EC _BIF 镜像 `0x400–0x40F`（L） | `0x402=0x1900`=6400 mAh（`ECSpec.cs:201-203` 命名 BIF_DC） | `0x404=0x16A8`=5800 mAh（按 ACPI _BIF 字段顺序推断为 LFCC，厂商未命名） | 90.6%（I） |

旁证：`6400 mAh × 15.48 V（0x408/0x409 = BIF_DV）= 99 072 mWh`，正好是 Windows 报的两个数；满电时 EC 剩余容量
`0x436=0x16A8`（BST_BRC）也是 5800。倾向于 EC 的 90.6% 是真值，但厂商从未解码 `0x404`，Windows 路径又给不出，
**两边冲突就不上主界面**。原始值进诊断包（§8 P7）。厂商 `BM_Manager.GetBatteryLife`（`S40 BatteryModel/BM_Manager.cs:19-24`）
走的就是 Windows 那条路，在本机同样会得出 100%。

### 2.7 其他看过但不用的字段

- `0x438/0x439` BST_BPV 当前电压 16 560 mV，与 Windows 完全一致（P），信息量低，只放 tooltip。
- `0x434/0x435` BST_BPR（厂商叫 Current，`MyControlCenter/HwFuelGaugeInfo.cs:29-36`）= 0；充放功率已有 Windows 来源。
- `0x490 ecPowSource = 0x07`、`0x491=0xC0`：位义没有任何出处。
- `0x494` 电池告警 = 0：已经体现在 MQTT `BatteryAbnormal`。
- `0x4A2/0x4A3` 电池温度 `0x0BF4` = 3060 → 按 ACPI 0.1 K 惯例为 32.9 °C，数值合理，但只有常量没有读取方（I）。

## 3. 读取通道、频率与失败处理

- **只用只读接口**：`Probe.IEcReadTransport`（`src/Probe/EcSnapshot.cs:9-17`）+ `AcpiDriverReadTransport.TryOpen`（`:72-97`，
  读 IOCTL `0x9C40A488`，4 B 入 16 B 出，`:27,47-63`）。应用内入口是 `MechrevoService.EcReadTransportFactory`
  （`src/MechrevoLiteWin/Hardware/MechrevoService.cs:161-164`，已是测试接缝）。不要用 `EcChargeLimit`（含写 IOCTL，`EcChargeLimit.cs:56-57`）
  或 `EcProbe`（含写/MM 路径，`src/Probe/EcProbe.cs:18-93`）。
- **权限**：本机非提权进程可以打开 `\\.\ACPIDriver`（GENERIC_READ|WRITE，L），不需要管理员。
- **频率**：`PowerInputSampler.Refresh(force)` 放进 `HardwareControl.ReadSensors()`（`HardwareControl.cs:171`），与电池功率同样 5 s 限频
  （`BatteryRateRefreshIntervalMs = 5000`，`:40`）。传感器节拍是 1 s 定时器 + 2 s 节流，只在主窗口可见时运行
  （`Settings.cs:2298,3636,4012-4015,4736-4740`）；托盘展开时也会刷新一次（`Program.cs:1583-1587`）。
  `0x7CC` 每次采样读 1 字节；`0x49F` 只在启动、`0x7CC` 变化、交流状态变化时读。电源事件去抖后（`Program.cs:1471-1481`）强制刷新一次。
  开销：≤ 2 次 EC 读 / 5 s，厂商服务自己每 2 s 就要读 6 字节做 FanInfo。
- **不阻塞 UI**：`RefreshSensors(true)` 会在 UI 线程被调用（如 `Settings.cs:3495,3598,3616`），读取放后台任务、限时 250 ms，超时返回缓存
  （照 `BatteryRateReader.ReadWatts` 的 `Task.Run + Wait(1000)` 写法，`BatteryRateReader.cs:25-36`）。
- **失败**：打开失败或读回 -1 → 本次 `Unknown`，显示退化为「外接电源 / 电池」（只靠 Windows），tooltip「供电类型未知（EC 读取失败）」；
  连续 3 次失败丢弃缓存；日志用 `Logger.WriteLineIfChanged`，不刷屏。风扇数据超过 6 s 未更新 → 遥测行隐藏占空比与转速。
- **不改 `Program.ReadPowerSource`**（`Program.cs:1441-1448`）：它决定性能模式重放与 `GPUModeControl.IsPlugged`（`Gpu/GPUModeControl.cs:343-344`），
  返回 USBC 会让 Type-C 供电被当成「未插电」。要不要让 Type-C 影响行为是另一个决定。

## 4. 本机只读实测（2026-09-29 11:41–12:02，交流在线，圆口适配器在插）

Windows：`SystemInformation.PowerStatus` Online / High / 100% / 剩余时间 -1；`Win32_Battery` `BatteryStatus=2`、`EstimatedChargeRemaining=100`、
`DesignVoltage=16560`（实为当前电压）；电池 IOCTL 见 §2.5、§2.6；WMI `BatteryStatus` `Voltage=16537`、`PowerOnline=True`、`Charging=False`；
`BatteryStaticData` 查询失败（常规故障）。

EC（11:45 一次全段 + 12:02:25/29/33 三次抽样，结果一致；2026-09-28 20:52 基线取自 `.omo/evidence/ec-dumps/beta21-baseline-ac.txt`）：

| 地址 | 名称（`S40 Define/ECSpec.cs`） | 09-28 基线 | 09-29 | 解读 |
|---|---|---|---|---|
| `0x7CC` | ADDR_COMPLEX_POWER_STATUS（:405） | `0x81` | `0x81` | 圆口在插，非 Type-C 充电，bit7 未知 |
| `0x49F` | ADDR_BIOS_INFO_3_BYTE（:131） | `0x5A` | `0x5A` | 适配器码 `0x58` 表外；bit1=狂暴支持 |
| `0x490` | ecPowSource（:129） | `0x07` | `0x07` | 未解码 |
| `0x75B/0x75C` | MAIN_FAN_L/R_DUTY（:243,245） | `0x57/0x57` | `0x46`–`0x48` | ÷2 = 43% / 35–36% |
| `0x464/0x465` | MAIN_FAN_RPM（:225,227） | `0x09 0x97` = 2455 | 2043 / 2151 / 2047 | 高字节在前 |
| `0x46C/0x46B` | SECOND_FAN_RPM（:231,233） | 2389 | `0x08D2` = 2258 | 高字节在 `0x46C` |
| `0x4A6/0x4A7` | BT1CycleCount（:221,223） | 17 | 17 | 与 MQTT 一致 |
| `0x4AB` | ecBt1RSOC（:135） | `0x64` | `0x64` | 100% |
| `0x494` | ADDR_BATTERY_ALERT_BYTE（:343） | `0x00` | `0x00` | 无告警 |
| `0x4A2/0x4A3` | ecBt1Temperature（:133） | `0xF4 0x0B` | 同 | 32.9 °C（推断） |
| `0x400–0x40F` | BIF 镜像 | `01 00 00 19 A8 16 01 00 78 3C 2E 01 B5 00 B5 00` | 同 | 见 §2.6 |
| `0x434–0x439` | BST_BPR/BRC/BPV（:209-219） | `00 00 A8 16 B0 40` | 同 | 0 / 5800 mAh / 16 560 mV |

基线帧同时抓到 MQTT：`System/FanInfo {"CpuFanDuty":43,"GpuFanDuty":43,"CpuFanRpm":2455,"GpuFanRpm":2389}`、
`System/BatteryInfo {…"BatteryCapacity":"0 mWh","BatteryCycleCount":"17"}`，与 EC 逐项对上。

## 5. 界面（紧凑，G-Helper 风格）

不加新面板、不加新定时器。

- **电池行右列**（`labelBattery`，行头由 `BuildHeadRow` 组装，`Settings.cs:2229-2231`）一行 muted 小字，格式
  `〈供电〉 · 〈状态〉`，必要时尾随红色「电池异常」：
  - 本机现在：`圆口供电 · 已充满`
  - 瓦数已知：`圆口 230W · 充电 45.2W`
  - `Type-C 供电 · 充电 38.0W`；`电池供电 · 放电 18.5W`；EC 不可用：`外接电源 · 未充电`
- **tooltip**（同一 label）多行明细：供电方式与原始 `0x7CC`、适配器额定功率（官方表 / 未知 + 编码）、电量与电压、循环次数。
  循环次数从行内挪到这里（行内已经被供电/状态占用）。
- **遥测行**：格式不变；占空比在数据新鲜时总是显示（含 `0%`），不新鲜时与转速一起隐藏。
- **托盘提示**：电池那一行换成同一条「供电 · 状态」文本（`Settings.cs:4774-4776`）。

## 6. 字符串 key（`Properties/Strings.resx` + `Strings.zh-CN.resx`）

| key | zh-CN | en |
|---|---|---|
| `PowerSourceBattery` | 电池供电 | On battery |
| `PowerSourceBarrel` | 圆口供电 | DC adapter |
| `PowerSourceBarrelWatts` | 圆口 {0}W | {0} W adapter |
| `PowerSourceTypeC` | Type-C 供电 | USB-C power |
| `PowerSourceBarrelAndTypeC` | 圆口 + Type-C | DC + USB-C |
| `PowerSourceExternal` | 外接电源 | AC power |
| `BatteryStateCharging` | 充电 {0}W | Charging {0} W |
| `BatteryStateDischarging` | 放电 {0}W | Discharging {0} W |
| `BatteryStateFull` | 已充满 | Full |
| `BatteryStateHeldAtLimit` | 已到充电上限 | Held at limit |
| `BatteryStateIdle` | 未充电 | Not charging |
| `BatteryAbnormalTag` | 电池异常 | Battery fault |
| `BatteryCycleCountLine` | 循环次数：{0} | Cycle count: {0} |
| `PowerTipSource` | 供电：{0}（EC 0x7CC=0x{1:X2}） | Power: {0} (EC 0x7CC=0x{1:X2}) |
| `PowerTipAdapter` | 适配器额定功率：{0}W（按官方表） | Adapter rating: {0} W (vendor table) |
| `PowerTipAdapterUnknown` | 适配器额定功率：未知（编码 0x{0:X2} 不在官方表内） | Adapter rating: unknown (code 0x{0:X2} not in the vendor table) |
| `PowerTipSourceUnknown` | 供电类型未知（EC 读取失败） | Power source type unknown (EC read failed) |

`Settings.cs:5306,5309,5329` 里硬编码的「循环 {0} 次」「电池异常」换成上面的 key。

## 7. 需要用户配合的验证（Type-C）

目的：确认 `0x7CC` 的 Type-C 位在本机真的会变，顺带为「PD 功率寄存器」收集差分数据。全程只读，GCU 服务保持运行即可。

1. 准备：记下圆口适配器铭牌上的输出功率（例如 `20V⎓16.5A` = 330 W）和手上 USB-C PD 充电器的标称功率。
2. 只插圆口：运行下面脚本 `Dump A`，得到 `%TEMP%\ec-A.txt`。
3. 拔掉圆口，只用电池，等 30 秒：`Dump B`。
4. 只插 USB-C PD 充电器，等 60 秒：`Dump C`；另开一个窗口跑 `Watch`，看 `0x7CC` 是否在插上瞬间变化。
5. 圆口 + USB-C 同时插：`Dump D`。
6. （可选）换一个功率不同的 PD 充电器（如 65 W）：`Dump E`。
7. （可选）拔掉所有电源，用 USB-C 给手机充电：`Dump F`。

判定：C 中 bit0=0 且 `0x7CC & 0x06 ≠ 0` → Type-C 解码成立；C 中 `0x7CC` 与 B 相同 → 本机不报 Type-C，界面在 Type-C 时只显示「外接电源」；
D 决定「圆口 + Type-C」的文案；C 与 E 之间随充电器功率变化的字节是 PD 功率寄存器的候选（另起文档）。

```powershell
# 只读：唯一的 IOCTL 是 IOCTL_GPD_ACPI_ECREAD (0x9C40A488)。用法：. .\ec-ro.ps1; Dump A  或  Watch
# 句柄按读写方式打开，是因为该 IOCTL 的访问位要求写权限；脚本里没有写 IOCTL。
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Threading;
public static class EcRo {
  const uint Read = 0x9C40A488u;
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFile(string n, uint a, uint s, IntPtr p, uint d, uint f, IntPtr t);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(IntPtr h, uint c, IntPtr i, int n, IntPtr o, int m, out int r, IntPtr v);
  public static int[] Bytes(int start, int count) {
    var result = new int[count]; IntPtr h = CreateFile(@"\\.\ACPIDriver", 0xC0000000u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
    if (h == new IntPtr(-1)) { for (int k = 0; k < count; k++) result[k] = -1; return result; }
    IntPtr inb = Marshal.AllocHGlobal(4), outb = Marshal.AllocHGlobal(16);
    try { for (int k = 0; k < count; k++) { Marshal.WriteInt32(inb, 0, start + k); int r;
      result[k] = DeviceIoControl(h, Read, inb, 4, outb, 16, out r, IntPtr.Zero) && r > 0 ? Marshal.ReadByte(outb) : -1; Thread.Sleep(3); } }
    finally { Marshal.FreeHGlobal(inb); Marshal.FreeHGlobal(outb); CloseHandle(h); }
    return result;
  }
}
'@
Add-Type -AssemblyName System.Windows.Forms
function Dump([string]$tag) {
  $out = Join-Path $env:TEMP "ec-$tag.txt"
  $lines = @("tag=$tag time=$(Get-Date -Format o) ac=$([System.Windows.Forms.SystemInformation]::PowerStatus.PowerLineStatus)")
  foreach ($r in @(@(0x400,0x100), @(0x700,0x100), @(0xD00,0x100))) {
    $b = [EcRo]::Bytes($r[0], $r[1])
    for ($row = 0; $row -lt $r[1]; $row += 16) { $lines += ('0x{0:X3} ' -f ($r[0] + $row)) + (($b[$row..($row + 15)] | ForEach-Object { if ($_ -lt 0) { '??' } else { '{0:X2}' -f $_ } }) -join ' ') }
  }
  $lines | Set-Content -Encoding ASCII $out; "saved $out"
}
function Watch { while ($true) { $a = [EcRo]::Bytes(0x7CC, 1)[0]; $w = [EcRo]::Bytes(0x49F, 1)[0]; $p = [EcRo]::Bytes(0x490, 1)[0]
  '{0:HH:mm:ss} ac={1,-7} 7CC=0x{2:X2} 49F=0x{3:X2} 490=0x{4:X2}' -f (Get-Date), [System.Windows.Forms.SystemInformation]::PowerStatus.PowerLineStatus, $a, $w, $p; Start-Sleep 1 } }
```

（也可以用现成的 `Probe.exe snapshot <文件> --ranges 0x400-0x4FF,0x700-0x7FF,0xD00-0xDFF`，但它还会以 `UWPClient_5` 连 MQTT 并发
`System_ON` / `GETSTATUS` 这类查询，`src/Probe/EcSnapshotReport.cs:228-251`。）

## 8. 实现改动清单

| # | 文件 / 函数 | 做什么 |
|---|---|---|
| P1 | 新增 `src/MechrevoLiteWin/Hardware/PowerInputInfo.cs` | `enum PowerInputKind { Unknown, Battery, Barrel, TypeC, BarrelAndTypeC, ExternalUnknown }`；`readonly record struct PowerInputSample(PowerInputKind Kind, int? AdapterWatts, int ComplexStatusRaw, int BiosInfo3Raw, long TakenTick)`；`static class PowerInputDecoder`：常量 `ComplexPowerStatusAddress=0x7CC`、`BiosInfo3Address=0x49F`、`RoundSocketMask=0x01`、`TypeCChargingMask=0x06`、`AdapterCodeMask=0x78`（每个常量注释写厂商出处），`Decode(bool acOnline, int complexStatus, bool decodeEnabled)`（§2.1 表），`TryDecodeAdapterWatts(int raw, out int watts)`（表外 false）；`sealed class PowerInputSampler(Func<Probe.IEcReadTransport?> transport, Func<bool> acOnline, Func<bool> decodeEnabled, Func<long> tick)`，`Refresh(bool force)` 按 §3 限频/缓存/容错，只读 `0x7CC`、`0x49F` 两个地址 |
| P2 | `HardwareControl.cs` | 新字段 `powerInput`、`batteryPowerState`；`RefreshPowerInput(bool force = false)`；`ReadSensors()`（:171）与 `SampleLocalPower()`（:179-184）里调用；`RefreshBatteryRate`（:47-53）同时存 `PowerState`；`decodeEnabled` = 服务档位 ≠ Legacy1020（档位未实现前临时用「GCUBridge 镜像不在 `UniwillService` 下」） |
| P3 | `Program.cs`：`OnPowerSettled`（:1471-1481）、`SetAutoModes(init)` | 插拔后 `HardwareControl.RefreshPowerInput(force: true)` + `settingsForm?.RefreshSensors(true)`；**不动** `ReadPowerSource` |
| P4 | `Battery/BatteryRateReader.cs` | 新 `internal readonly record struct BatteryStatusReading(uint PowerState, int RateMilliwatts, uint CapacityMilliwattHours, uint VoltageMillivolts)` 与 `ReadStatus()`（沿用 1 s 超时）；`ReadWatts()` 改为基于 `ReadStatus()`，对外签名不变 |
| P5 | `Settings.cs`：`RefreshSensors`（:4736-4808）、`BatteryHealthSuffix` / `BatteryHealthText` / `BatteryRateText`（:5302-5343） | 新增纯函数 `internal static string BatteryHeadlineText(PowerInputSample?, decimal? rateW, uint? powerState, int percent, int limit, bool abnormal)` 与 `BatteryDetailsTooltip(...)`；`labelBattery` 文本与 tooltip 只在变化时写；托盘提示复用 headline；`limit` 取 `BatteryControl.KnownLimit`（缓存，不触发 EC 读）；硬编码中文换成 §6 的 key |
| P6 | `Hardware/MechrevoHw.cs`：`OnSystemFanInfo`（:1571-1590）；`Settings.V2.cs`：`TelemetryParts`（:154-167） | 记录 `FanInfoTick`；遥测行 `duty >= 0` 且 6 s 内有新帧才显示，允许 `0%` |
| P7 | `Diagnostics/DiagnosticSystemInfo.cs` | 诊断包加一节「供电/电池只读原始值」：`0x7CC`、`0x49F`、`0x490`、`0x400–0x40F`、`0x434–0x439`、`0x4A2–0x4AB` 与 Windows `BATTERY_INFORMATION`/`BATTERY_STATUS`，给以后补适配器表和核对电池健康用 |
| P8 | `src/Probe/`（可选） | 新子命令 `power-watch`：只读 EC + Windows 电源状态，每秒输出变化，不连 MQTT，替代 §7 的脚本 |
| P9 | `Properties/Strings*.resx` + `Strings.Designer.cs` | §6 的 key（zh-CN 与中性 en） |

## 9. 单元测试

- `PowerInputDecoderTests`：§2.1 真值表逐行（含 `0x81`+在线 → Barrel，`0x81`+离线 → Battery，`0x02`/`0x04`+在线 → TypeC，
  `0x03`+在线 → BarrelAndTypeC，`0x00`+在线 → ExternalUnknown，`-1` → 仅按 Windows，`decodeEnabled=false` → 不看 EC）；bit4/5/7 不影响结果（`0xB1` 等同 `0x81`）。
- `AdapterRatingTests`：九个表内码逐一对应；码外位不影响（`0x1A` → 150）；`0x5A`（本机）、`0x48`、`0x78` → false，绝不回落 150；
  同时断言 `ModelRegistry.AdapterWattFor(0x5A) == 150` 不变（机型展开保持厂商等价）。
- `PowerInputSamplerTests`：假传输记录地址——只出现 `0x7CC`/`0x49F`；5 s 内第二次调用不读 EC、`force` 例外；`0x49F` 只在首读、
  `0x7CC` 变化、交流变化时读；传输返回 -1 / 抛异常 → Unknown 且不抛出；连续 3 次失败丢缓存；工厂返回 null → Unknown。
- `BatteryHeadlineTextTests`：本机组合 → `圆口供电 · 已充满`；瓦数已知 → `圆口 230W · 充电 45.2W`；Type-C、电池、外接电源、
  上限保持、异常标记各一例；未知段不出现占位符（无 `-1`、无 `?`）。
- `BatteryStatusReadingTests`：`PowerState` 标志与 `Rate` 符号组合出 §2.5 各状态；`0x80000000` 未知哨兵 → 无功率。
- `TelemetryFanDutyTests`：新鲜 `0` → `0%`；过期 → 不显示；`-1` → 不显示；`43` → `43%`。
- `PowerSourceBehaviourUnchangedTests`：采样器给出 TypeC 时 `Program.ReadPowerSource()` 仍返回 `Barrel`。
- IL 守卫：`PowerInputInfo` 中的类型不引用 `0x9C40A48C`（写 IOCTL）、不调用 `EcChargeLimit`/`EcProbe` 的任何成员（写法参照
  `tests/MechrevoLite.Tests/FanTableNoEcWriteGuard.cs`）。

## 10. 不确定点

1. Type-C 位在本机从未见过置位；bit4/5/7 含义未知。
2. `0x49F` 码位 11（本机）对应多少瓦未知；表内码在 50 系 EC 上是否仍是同一张表未知（S50 混淆）。
3. 双插（圆口 + Type-C）时各位怎么报未知。
4. 30 系 EC 的 bit0 是否表示圆口：30 服务没有圆口方法，只能靠 §2.1 的一致性规则兜底。
5. 电池健康：EC `0x404` 是否就是 LFCC、单位是否为 mAh，都是按 ACPI _BIF 顺序推断的。
6. 电池温度单位（0.1 K）是惯例推断。
7. 风扇占空比在 10/20 机器上的 MQTT 字段与缩放（方法体被破坏），首台 10/20 机器需对照一次。
