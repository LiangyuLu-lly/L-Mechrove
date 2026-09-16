# beta18 — 悬浮窗指标扩展（T3 全档）设计

> 状态：设计已定稿（decision-complete）。本文档是后续实现任务的唯一事实来源，实现者不应再做任何额外决策。
> 依据：读代码 + 本机 `%APPDATA%\MechrevoLite\lhm_sensors.txt` 实测 dump 验证，全部结论带 `file:line`。

## 0. 机主已定决策（不再讨论）

1. **范围 = T3 全档**：内存行、存储行、可选网络行、GPU 显存频率、CPU 核心最高温等全部纳入提案，按可达性分档落地。
2. **允许适度面积增长**：beta17 之前"固定 2 行 / 90 px 高、只加列"的约束解除，改为 §4 的具体数值上限。
3. **要按指标开关**：但只做简单的 per-row / per-metric on-off 配置键，不做复杂编辑器。
4. **绝不显示假值**：传感器缺失的格子一律隐藏或显示"不可用"，绝不显示 0。
5. **v1 不改 Settings UI**：per-block 键今天就是纯配置键（`overlay_show_*`，`HardwareOverlay.cs:941-956`），Settings 面板留到后续。

## 1. 现状与实测

### 1.1 布局与更新节奏

| 事实 | 证据 |
|---|---|
| 固定 2 行：GPU 上行 / CPU 下行 | `HardwareOverlay.cs:630-631`（名字列）、`:636-637`（温度+频率+风扇）、`:644-649`（功耗）、`:665-666`（占用条）——所有绘制都按 `topY` 与 `topY + lineH + lineGap` 两行排布 |
| 1 s 定时器整窗重绘 | `HardwareOverlay.cs:200`（`new(1000)`）、`:493`（每 tick `Invalidate()` 整窗 layered 重绘） |
| 固定缩放 2.0（与系统 DPI 解耦） | `HardwareOverlay.cs:70`（`BaseScale = 2.0f`）、`:350`（`GetScale()`） |
| 高度公式恒为 2 行 | `HardwareOverlay.cs:984`：`padY*2 + lineH*2 + lineGap`，200% 缩放下 = 4*2*2 + 18*2*2 + 1*2 = **90 px** |
| Default 宽 ≈452 px | `HardwareOverlay.cs:972-985`：padX 8 + 左列 144 + 功耗列 4+16+46 + padX 8 = 226 基准 × 2 = 452（`_showChart` 恒 false，`:950`，不占宽） |
| Complete 宽 ≈806 px（带电池列） | 同上再加 usage 列 11+30+4+5、mem 列 8+54+4+5、电池列 8+38+2+8（`:979-982`）= 403 基准 × 2 = 806 |
| 功耗历史曲线图代码在、永久关闭 | 绘制实现 `HardwareOverlay.cs:786-822`（`DrawStackedChart`），但 `:950` 硬编码 `_showChart = false`（注释："简洁模式：不显示曲线图"） |

### 1.2 字段与数据来源

| 悬浮窗字段 | 来源 | 证据 |
|---|---|---|
| GPU/CPU 温度 + 频率 | 厂商 MQTT `System/GpuInfo`、`System/CpuInfo` | `MechrevoHw.cs:1316-1322`（CpuTemperature/CpuUsage/CpuFrequency）、`:1324-1331`（GpuTemperature/GpuUsage/GpuCoreFreq/GpuMem）；经 `HardwareControl.AttachMechrevoHw`（`HardwareControl.cs:62-84`）同步到静态字段；绘制 `HardwareOverlay.cs:440-441` |
| 风扇 RPM | MQTT `System/FanInfo` | `MechrevoHw.cs:1339-1344`；绘制 `HardwareOverlay.cs:442-443` |
| GPU/CPU 封装功耗 | LibreHardwareMonitor 本地直读 | `LhmMonitor.cs:71-97`（按传感器名匹配 Package / GPU Package）；回退 NVML / CPU 采样 `HardwareControl.cs:92-97` |
| GPU/CPU 占用 % | MQTT | `MechrevoHw.cs:1319,1327`；绘制 `HardwareOverlay.cs:451-452,663-670` |
| VRAM/RAM 已用与占用 % | VRAM：LHM SmallData（`LhmMonitor.cs:91-94`）+ NVML 回退（`HardwareControl.cs:99-111`）；RAM：MQTT `System/MemoryInfo`（`MechrevoHw.cs:1333-1337`） | 绘制 `HardwareOverlay.cs:672-680` |
| 电池 % | MQTT `System/BatteryInfo`（`MechrevoHw.cs:1360-1371`）+ `SystemInformation.PowerStatus`（`HardwareOverlay.cs:460`） | 绘制 `HardwareOverlay.cs:683-698` |
| 电池充放瓦数 | **死字段**：`HardwareControl.batteryRate` 声明于 `HardwareControl.cs:33`，全库只有读取点（`HardwareOverlay.cs:479`、`Settings.cs:4459-4462`），**没有任何赋值点** ⇒ 悬浮窗电池瓦数格与 Settings 的充/放瓦数文字永远不渲染 | grep 全 src 确认 |

### 1.3 配置键（现状）

`overlay_show_*` 家族只在 Complete 模式生效、默认全开（`IsNotFalse` 语义，`HardwareOverlay.cs:941-956`）：`overlay_show_temp` / `overlay_show_fans` / `overlay_show_power` / `overlay_show_usage` / `overlay_show_ram` / `overlay_show_battery`（三态：-1 跟随电源状态）。另有 `overlay_mode`、`overlay_scale_percent`、`overlay_names`、`overlay_game_only`（`AppConfig.cs:567-574`）、`overlay_screen` / `overlay_anchor` / `overlay_offset_x/y`（`HardwareOverlay.cs:893-906`）。`AppConfig.cs` 本身没有 overlay 键的白名单，键是自由字符串。

### 1.4 传感器数据面（本机 dump 实测）

`%APPDATA%\MechrevoLite\lhm_sensors.txt`（由 `LhmMonitor.cs:99-112` 首刷导出）实测内容：

- **Cpu**：Load 24 核 + Total + Core Max；Temperature 含 **Core Max / Core Average**、P-Core #1-8、E-Core #1-16、CPU Package、各核 Distance to TjMax；Clock 含 P/E-Core 各核；Power 含 CPU Package / Cores / Memory / Platform。
- **GpuNvidia**：Temperature **GPU Core + GPU Memory Junction**；Clock GPU Core + GPU Memory；Load Core / Memory Controller / Video Engine / Bus / Memory / D3D 各路；Voltage；SmallData 显存 Total/Used/Free；Power GPU Package；**Throughput GPU PCIe Rx / Tx**。
- **GpuIntel**：核显 Clock / Power / D3D Shared Memory / D3D Load。
- **没有** Memory（内存条）、Storage（NVMe）、Motherboard 硬件——因为 `LhmMonitor` 从未打开这三个开关（见 §2）。
- **没有** GPU hot spot 传感器（LHM 0.9.6 在本机不暴露，也无公开 NVML API）。

## 2. LHM 过滤机制与"白捡"的两个指标

**机制**：`LhmMonitor` 构造时只设 `IsCpuEnabled = true; IsGpuEnabled = true`（`LhmMonitor.cs:25-27`）。`Update()` 对枚举到的硬件**不做任何传感器过滤**——凡是 `sensor.Value.HasValue` 的全部遍历（`LhmMonitor.cs:68-70`），只是按名字挑出功耗与显存四类（`:71-94`）。所以：

- DRAM / NVMe / 主板传感器**不可见**的唯一原因是 `IsMemoryEnabled` / `IsStorageEnabled` / `IsMotherboardEnabled` 从未被置 true。这三个开关在 LibreHardwareMonitorLib 0.9.6 里都存在，打开即枚举。
- 打开开关的代价：Memory 硬件每轮 Update 会走 SPD 读取，Storage 会走 SMART IOCTL——这是 §8 的停顿风险来源。

**白捡的两个指标**（厂商 MQTT 已推送、解析端从未读取）：

| 字段 | 推送侧 | 解析侧现状 |
|---|---|---|
| `GpuMemFreq`（显存频率） | `System/GpuInfo` 载荷 | `OnSystemGpuInfo`（`MechrevoHw.cs:1324-1331`）只读 GpuTemperature/GpuUsage/GpuCoreFreq/GpuMem 四个字段，`GpuMemFreq` 被丢弃 |
| `CpuMaxFrequency`（CPU 最大频率） | `System/CpuInfo` 载荷 | `OnSystemCpuInfo`（`MechrevoHw.cs:1316-1322`）只读三个字段，`CpuMaxFrequency` 被丢弃 |

【待确认】这两个字段名是否在本机载荷里真实出现、单位是什么（MHz 还是带单位的字符串）。探针（§7 第 2 步）一次性解决：把 `System/GpuInfo`、`System/CpuInfo` 原始 JSON 落盘一次即可定案。

## 3. 可达性分级表

| 档 | 指标 | 依据 |
|---|---|---|
| **A. 可得（零新数据源）** | GPU 显存频率（`GpuMemFreq`）、CPU 最大频率（`CpuMaxFrequency`） | MQTT 已推送未解析（§2）；【待确认】字段名/单位，探针定案 |
| **A. 可得（已枚举未读）** | GPU 显存温度（Memory Junction）、CPU Core Max / Core Average 温度、GPU PCIe Rx/Tx 吞吐 | 本机 dump 实测存在（§1.4）；`LhmMonitor.Update` 已遍历到它们，只是没挑出来（`LhmMonitor.cs:68-95`） |
| **B. 需探测** | DRAM 温度/频率、NVMe 温度 | 需打开 `IsMemoryEnabled`/`IsStorageEnabled` 重 dump 才知道本机暴露什么（§7 第 3 步）；SPD/SMART 读取有停顿风险（§8） |
| **C. 不可得** | GPU hot spot | 本机 dump 无此传感器；LHM 0.9.6 不暴露，无公开 NVML API。设计上不为其留格子 |
| **C. 不可得（显示层）** | 电池充放瓦数 | 数据链断裂：`batteryRate` 无赋值点（§1.2）。修复属独立任务，不在本设计数据面内；悬浮窗该格维持现状（空） |
| **D. 展示不可行** | 24 行 per-core 时钟/温度 | dump 实测 24 核（§1.4），2-4 行的窗口放不下；只取聚合值（Core Max 温度、最大核频） |

## 4. 目标与硬约束

**目标**：在 Complete（可定制）模式下，把 §3 的 A/B 档指标按用户开关加进悬浮窗；Default/Full/Light 模式布局不变。

**硬约束（数值均为提案，机主可调）**：

1. **行数 ≤ 4**（现有 GPU/CPU 两行 + 内存行 + 存储行；网络行若做则与存储行共享一行或替换，见 §5）。高度上限 = padY*2 + lineH*4 + lineGap*3 = 8*2 + 18*4 + 1*3 = 91 基准 × 2 = **≤ ~182 px @200%**；提案收紧到 **≤ ~160 px**（即最多 3 行新增中实际启用 ≤ 2 行时自然满足）。
2. **宽度最多增长一列**：新增列宽基准 ≤ ~64 px（一个"温度+短标签"格），Complete 总宽 ≤ ~934 px @200%（806 + 128）。
3. **空行自动收缩**：某行所有格子都无数据（传感器缺失或用户全关）时整行隐藏，`UpdateOverlaySize`（`HardwareOverlay.cs:969-987`）按实际行数算高度——窗口收缩到实际显示的内容，而不是留黑条。
4. **绝不显示假值**：沿用现有 null-隐藏惯例（`DrawUsagePercent` 的 `if (!usage.HasValue) return;`，`HardwareOverlay.cs:703`；功耗列空串隐藏，`:644-649`）。新增格子一律：无传感器 → 不画；传感器存在但读数无效（≤0 / NaN）→ 不画。**禁止**用 0 占位。
5. **所有增长必须留在现有钳位内**：拖动钳位（`HardwareOverlay.cs:293-297`）、缩放重锚（`:869-891`）、位置恢复钳位（`:1007-1012`）不改语义，只让它们对新高度/宽度继续成立。
6. **1 s tick 预算不膨胀**：新增读取全部挂在现有 `LhmMonitor.Update`（1 s 节流，`LhmMonitor.cs:44-49`）与 MQTT 快照上，不新增定时器、不新增每 tick 的额外 IOCTL（存储/内存的 SMART/SPD 读取降频到 ≥5 s 一次，见 §8）。

## 5. 指标与分组提案

每项给出数据来源与缺失时行为（缺失 = 隐藏，遵守硬约束 4）。

### 5.1 GPU 行（现有行，追加格子）

| 指标 | 来源 | 缺失行为 | 档 |
|---|---|---|---|
| 显存频率（GHz） | MQTT `GpuMemFreq`（解析后入 `HardwareControl`） | 字段缺失/0 → 不画 | A【待确认单位】 |
| 显存温度 | LHM `GpuNvidia/Temperature/GPU Memory Junction` | 传感器缺失 → 不画 | A |
| PCIe 吞吐（Rx，可选 Tx） | LHM `GpuNvidia/Throughput/GPU PCIe Rx/Tx` | 缺失 → 不画；默认关 | A |

### 5.2 CPU 行（现有行，追加格子）

| 指标 | 来源 | 缺失行为 | 档 |
|---|---|---|---|
| 核心最高温度（替代/并列 Package） | LHM `Cpu/Temperature/Core Max` | 缺失 → 维持 Package | A |
| 最大核频 | MQTT `CpuMaxFrequency`（优先）或 LHM `Cpu/Clock/*` 取最大 | 缺失 → 维持现有 `CpuFrequency` | A【待确认单位】 |

### 5.3 新增：内存行（第 3 行）

| 指标 | 来源 | 缺失行为 | 档 |
|---|---|---|---|
| RAM 占用 % + 已用 GB | 已有 `HardwareControl.ramUsage/ramUsedMb`（MQTT，`MechrevoHw.cs:1333-1337`） | 恒可得 | A |
| DRAM 温度 | LHM Memory 硬件（需 `IsMemoryEnabled`） | 探测无传感器 → 整格不画 | B |
| DRAM 频率 | LHM Memory Clock 传感器 | 同上 | B |

内存行即使 DRAM 探测失败也值得做（RAM 占用是确定可得的），所以该行**不因探测失败而取消**，只是少一格。

### 5.4 新增：存储行（第 4 行，默认关）

| 指标 | 来源 | 缺失行为 | 档 |
|---|---|---|---|
| NVMe 温度 | LHM Storage 硬件（需 `IsStorageEnabled`） | 探测失败或读数停顿不可接受 → 整行隐藏 | B |
| NVMe 已用/总量 | LHM Storage `Data Used` 类传感器 | 缺失 → 不画 | B |

存储行**默认关**（`overlay_show_storage` 默认 false）：SMART IOCTL 在 1 s tick 上的停顿风险最高（§8），先探针后放开。

### 5.5 可选：网络行（与存储行互斥占位，默认关）

| 指标 | 来源 | 缺失行为 | 档 |
|---|---|---|---|
| 下行/上行速率 | MQTT `System/NetworkInfo` 的 `NetworkDownload/NetworkUpload`（带单位字符串，原样展示；`MechrevoHw.cs:1373-1379` 已解析缓存，纯白捡） | `NetworkInfoSeen == false` → 整行隐藏 | A |

网络行数据零成本（已解析、已缓存），唯一成本是布局行。默认关，键 `overlay_show_network`。

### 5.6 明确不做

- per-core 24 行时钟/温度（§3 D 档）。
- GPU hot spot（§3 C 档，无数据源）。
- 电池瓦数修复（独立任务，见 §9）。
- 功耗历史曲线图重新打开（`_showChart` 恒 false 是有意的简洁模式决策，`HardwareOverlay.cs:950`；本设计不推翻）。

## 6. 配置键

沿用 `overlay_show_*` 家族与 `IsNotFalse` 语义（默认开、显式 false 关）。全部为配置键，v1 无 Settings UI。

| 键 | 默认 | Default 模式 | Full 模式 | Complete 模式 | 控制内容 |
|---|---|---|---|---|---|
| `overlay_show_vram_freq` | 未设（=开） | 关 | 关 | 开 | GPU 行显存频率格 |
| `overlay_show_vram_temp` | 未设（=开） | 关 | 关 | 开 | GPU 行显存温度格 |
| `overlay_show_pcie` | false | 关 | 关 | 开 | GPU 行 PCIe Rx/Tx 格 |
| `overlay_show_cpu_core_max_temp` | 未设（=开） | 关 | 关 | 开 | CPU 行核心最高温度格 |
| `overlay_show_cpu_max_freq` | 未设（=开） | 关 | 关 | 开 | CPU 行最大核频格 |
| `overlay_show_mem_row` | 未设（=开） | 关 | 关 | 开 | 内存行（RAM 占用/已用 + DRAM 探测结果） |
| `overlay_show_storage` | **false** | 关 | 关 | 开 | 存储行（NVMe 温度等） |
| `overlay_show_network` | **false** | 关 | 关 | 开 | 网络行（下行/上行） |

规则：

- 与现有键一致，只有 Complete 模式读这些键（`ApplyPreset` 的 `complete ?` 分支模式，`HardwareOverlay.cs:942-956`）；Default/Full/Light 保持固定布局，不受影响。
- "未设（=开）"沿用 `IsNotFalse`：老用户升级后 Complete 模式自动多出白捡指标；B 档（storage/network）默认关，避免未经同意的面积增长与 IOCTL 风险。
- 行级开关之上不再做格子级编辑器（决策 3）：每行一个键，行内格子跟随各自数据可得性自动显隐。

## 7. 探针步骤（第一步，先于一切实现）

**探针 1：放开 LHM 枚举（不改产品代码，用一次性诊断构建或临时补丁）**

1. 把 `LhmMonitor.cs:25-27` 临时改为 `IsCpuEnabled = IsGpuEnabled = IsMemoryEnabled = IsStorageEnabled = IsMotherboardEnabled = true`。
2. 以管理员运行一次，等首刷 dump 重写 `%APPDATA%\MechrevoLite\lhm_sensors.txt`（`LhmMonitor.cs:99-112`）。
3. **判据与预期**：
   - dump 出现 `Memory/Temperature/*` 或 `Memory/Clock/*` → DRAM 温度/频率进 **B→A** 档；只有 Load 无温度 → DRAM 温度降 **C** 档，内存行只做 RAM 占用。
   - dump 出现 `Storage/Temperature/*` → NVMe 温度进 **B→A** 档；无 Storage 硬件 → 存储行整行砍掉。
   - 同时用秒表/日志观察放开后每轮 `Update()` 耗时（在 `LhmMonitor.cs:44` 节流点前后打点）：增幅 ≤ ~20 ms → 可常开；增幅大或偶发 >100 ms 停顿 → Memory/Storage 读取降频到 5 s，或存储行维持默认关。
4. 探针结束后还原补丁，dump 文件与耗时数据作为证据归档到本文档附录或实现 PR 描述。

**探针 2：MQTT 原始载荷落盘**

1. 在 `MechrevoHw` 的消息解析入口临时把 `System/GpuInfo`、`System/CpuInfo` 原始 JSON 写日志（一次即可）。
2. **判据**：载荷含 `GpuMemFreq` / `CpuMaxFrequency` 字段 → §2 两个白捡指标定案，记录单位（MHz 数字 vs 带单位字符串）；不含 → 从提案中删除对应格子，§5.1/5.2 相应收缩。
3. 【待确认】由此步消除：字段是否存在、字段名精确拼写、单位。

**探针结果 → 分档映射**：探针 1 决定 §5.3/§5.4 的 DRAM/NVMe 格子去留与降频策略；探针 2 决定 §5.1/5.2 的两个频率格去留。其余提案不依赖探针。

## 8. 成本与风险

| 风险 | 说明 | 缓解 |
|---|---|---|
| LHM 新增 SPD/SMART IOCTL 的停顿 | 打开 `IsMemoryEnabled`/`IsStorageEnabled` 后，每轮 `Update()`（1 s 节流，`LhmMonitor.cs:44-49`，持 `_updateLock`）会多走 SPD 读与 SMART IOCTL；这些是同步 IO，卡在锁内会拖慢所有依赖 `RefreshLocalMonitoring` 的路径（悬浮窗 tick、功耗墙采样，`HardwareControl.cs:86-113`） | 探针 1 第 3 步实测耗时；超阈值则 Memory/Storage 传感器读取降频到 ≥5 s（独立节流，不阻塞 CPU/GPU 主路径）；存储行默认关 |
| 管理员权限 | LHM 内核驱动注册非提权必失败，现有重开逻辑 5 次封顶放弃（`LhmMonitor.cs:167-178,196-211`）；Memory/Storage 枚举同样依赖驱动 | 不新增权限要求；非提权会话新增行自然无数据 → 按硬约束 4 自动隐藏，不显示假值 |
| 布局守卫测试 | `SettingsLayoutTests.cs:396-413`（`OverlayTelemetryColumn_ReservesSpaceForFrequencyBeforePower`）断言遥测列宽 ≤ 功耗列前可用宽，参数化 35/100/300% 三档缩放 × 风扇开关 | 新增格子必须同步扩展 `UpdateOverlaySize`（`HardwareOverlay.cs:969-987`）与该测试的宽度模型；每加一格跑一次该测试 |
| 全窗重绘成本 | 每 tick `Invalidate()` 整窗 layered 重绘（`HardwareOverlay.cs:493`），行数 2→4 使每帧填充面积最多翻倍 | 绘制已是预分配数组 + 缓存 Font/Pen（`HardwareOverlay.cs:157-169,607-615`）；90→~160 px 高度在 layered window 预算内可接受；若实测 CPU 占用上升，再考虑脏矩形，不在 v1 范围 |
| 行数增加的锚点/钳位回归 | 高度变化后右下锚定与拖动钳位依赖 Width/Height（`HardwareOverlay.cs:293-297,463-471,869-891,1007-1012`） | 复用现有"尺寸变化前记录 rightEdge/bottomEdge 再回贴"模式（`:463-471` 已有先例）；行显隐切换走同一路径 |
| 假值回归 | 新代码图省事把 null 当 0 画 | 硬约束 4 + code review 检查点：所有新 DrawXxx 必须 null-early-return（对齐 `HardwareOverlay.cs:703,710` 先例） |

## 9. 开放问题（各带建议）

1. **电池瓦数数据链修复**（`batteryRate` 无赋值点，§1.2）：建议独立任务——先探针 `System/BatteryInfo` 及相关主题是否推瓦数；若协议根本没有，考虑本地 `PowerStatus` 或 Win32 `BatteryStatus` 换算。不阻塞本设计。
2. **网络行与存储行是否互斥**：两者都是"第 3/4 行候选"。建议：允许共存（最多 4 行 + 网络 = 5 行时超出高度上限，则网络行与存储行互斥，后开的挤掉先开的）；v1 先都默认关，实际冲突概率低。
3. **PCIe 吞吐的单位与刷新**：LHM 给的是 B/s 类 Throughput，1 s 窗口抖动大。建议显示 Rx 单值、3 tick 滑动平均；Tx 默认不显示。
4. **DRAM 探测失败后是否重试**：建议进程生命周期内一次探针定档，不自动重试（对齐 `LhmMonitor` 重开封顶的克制风格，`LhmMonitor.cs:167-178`）。
5. **Complete 之外是否给 Full 模式加内存行**：建议不加，保持 Full = Complete 减 mem/电池 的现有语义（`HardwareOverlay.cs:952-955`）。

## 10. 实施顺序（每步独立可验证 + 证据）

1. **探针（必须第一步）**：§7 两个探针。证据：新 `lhm_sensors.txt`、Update 耗时打点、MQTT 原始载荷片段。产出：§5 各格子的最终 A/B/C 定档 + 降频阈值。
2. **数据层：白捡指标接线**：`MechrevoHw` 解析 `GpuMemFreq`/`CpuMaxFrequency` → `HardwareControl` 静态字段。证据：探针 2 的字段在 UI/日志可见；不影响布局（尚未画）。可独立合入。
3. **数据层：LHM 新传感器挑取**：`LhmMonitor` 增加显存温度 / Core Max 温度 / PCIe 吞吐的挑取（模式对齐现有 `IsGpuPowerSensorName` 精确名匹配，`LhmMonitor.cs:137-151`）；按探针结论决定是否打开 Memory/Storage 开关及降频。证据：新增字段非 null 的日志样本 + Update 耗时对比。
4. **布局层：行模型改造**：`HardwareOverlay` 从固定 2 行改为"行列表 + 空行自动收缩"，`UpdateOverlaySize`/`PerformPaint` 按行数计算；先只迁移现有 2 行（行为等价，纯重构）。证据：`SettingsLayoutTests.cs` 全绿 + 截图对比 2 行模式像素级不变。
5. **布局层：新增行/格子**：按 §5 逐格接入（GPU 行两格 → CPU 行两格 → 内存行 → 存储/网络行），每格一个提交，各自带 `overlay_show_*` 键。证据：每格的截图（有数据 + 拔掉传感器/关键两种状态）+ 布局守卫测试扩展后全绿。
6. **收尾**：拖动/缩放/锚定回归手测（约束 5），200%/100%/35% 三档缩放截图，`git status` 干净。

## 11. 探针 1 结果（2026-09-17 实测）

条件：Debug x64 探针构建（三开关全开 + 逐硬件 `Stopwatch` 打点，跑完已还原），应用非提权运行（与正式运行同权限），另做一次静默提权对照（本机 UAC=无提示提权）。证据：3 份 dump 差集 + 182 条 `LHM probe:` 日志。

### 11.1 新传感器（相对基线 dump 144 行）

| 设计问题 | 实测新增 | 定档 |
|---|---|---|
| DRAM 温度 / 频率 | **无**。Memory 组只多出 3 个传感器名：`Memory/Data/Memory Used`、`Memory/Data/Memory Available`、`Memory/Load/Memory`（两种权限一致；无 DIMM/SPD 专属温度或 Clock 传感器） | B → **C 不可得** |
| NVMe/SSD 温度 | **仅提权**：3 个盘（21/13/21 传感器），含 `Storage/Temperature/Composite Temperature`、`Temperature #1/#2`、`Warning/Critical Temperature`；另送 `Level/Life`、`Data/Data Read|Data Written`、`Factor/Power On Count|Power On Hours`、`Load/Used Space`、`Data/Free|Total Space`、`Load/Read|Write|Total Activity`、`Throughput/Read Rate|Write Rate`、`Level/Available Spare|Available Spare Threshold|Percentage Used`。非提权 Storage 组枚举 **0 个设备** | B → **条件可得（仅提权）** |
| 主板 / 风扇 RPM | **无**（Motherboard 硬件存在但 0 传感器，两种权限一致） | 新增 **C 不可得** |
| 其他新传感器 | 同上 Storage 行（寿命/读写量/通电时间/备用块/百分比），记录备查、本次不使用 | — |

### 11.2 耗时实测（逐硬件切片）

| 硬件 | 非提权（正式运行权限） | 提权 |
|---|---|---|
| Memory（新增） | med 12µs / max 1.1ms | med 5µs / max 1.0ms |
| Motherboard（新增） | med 1µs / max 40µs | med 0µs / max 32µs |
| Storage（新增） | 0 设备 ⇒ 0 成本 | med 12.7ms / max 252ms，5/192 切片 ≥100ms（每轮合计约 5–40ms，尖峰 ~250ms） |
| GpuNvidia（原有） | med 83ms / max 1166ms | med 605ms / max 1022ms |
| Cpu（原有） | med 10ms / max 101ms | med 7.6ms / max 81ms |

对照构建（仅 `IsCpuEnabled`+`IsGpuEnabled`，42 轮）：GpuNvidia med 85ms / max 810ms / 48% 轮次 ≥100ms ⇒ **GpuNvidia 的百毫秒~1 秒级停顿是既有现象，与新增开关无关**。新增组在非提权下合计 ≤ ~50µs/轮；三次启动均干净（dump 在启动后 ≤10s 写出），日志无 LHM 报错。

### 11.3 开关去留（本次提交后的代码状态）

| 开关 | 决定 | 理由 |
|---|---|---|
| `IsMemoryEnabled` | **保留** | 有真实传感器、~25µs/轮、无每轮 IOCTL；但不提供 DRAM 温度/频率 |
| `IsStorageEnabled` | **还原** | 非提权（正式权限）枚举 0 设备；提权下每轮 6–250ms SMART IOCTL，未降频前违反硬约束 6（§8） |
| `IsMotherboardEnabled` | **还原** | 两种权限均 0 传感器 |

### 11.4 分档/实施影响

- DRAM 温度、DRAM 频率：**砍掉**（§5.3 内存行只做 RAM 占用，来源维持 MQTT）。
- NVMe 温度、存储已用/总量：保留提案，但标注为「仅提权会话有数据」；落地前提 = ≥5s 降频 + 明确提权策略（§8），否则非提权用户永远只是空行。
- 主板风扇/板载传感器：不进入提案。
- 探针 2（`GpuMemFreq`/`CpuMaxFrequency` 原始载荷）仍未做，§2 两个白捡指标仍待确认。
