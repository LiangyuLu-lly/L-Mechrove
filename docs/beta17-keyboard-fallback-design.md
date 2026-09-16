# beta17 — 键盘灯回退（HID 不支持的机型改走 GCU）设计

> 状态：设计已定稿（decision-complete）。本文档是六个后续实现任务（T1..T6）的唯一事实来源，实现者不应再做任何额外决策。
> 依据 HEAD `0d9a23d`，工作区干净。

## 需求

**原始诉求（机主原话的中文转述）**：部分机型的键盘控制器芯片不支持软件灯效控制，即使软件里打开了开关，键盘灯也毫无反应。需要增加一个探测逻辑：先检测本机键盘控制器是否支持软件控制；若不支持，则把键盘灯控制改走"官方的路"（即官方控制台所用的 GCU/MQTT 通道）；其余灯光（灯条、Logo 等）不受影响。睡眠/唤醒路径不得回归。

**机主的 4 项约束性决策（已定，不再讨论）**：

1. "官方的路" = 走 GCU/MQTT 主题（`Keyboard/Ctrl`）。
2. 探测信号 = 自己发 HID 探测（不依赖厂商服务或注册表）。
3. GCU 回退只覆盖 **电源 + 亮度**（不覆盖任意灯效）。
4. 不支持时键盘行**保留可见** + 状态文字（不隐藏整行）。

**机主后续补充的 4 项定案**（对应原设计第 11 节的 4 个开放问题，现已全部有答案，见 §11）：

- 乐观显示：不等永远不会出现的确认，发布即视为已应用。
- 亮度控件不隐藏，但为 **5 档**。
- 保留当前 GCU 回报的效果作为亮度载体（不钉死 "Single"）。
- 探测瞬时失败 → Unknown → 按现状当"支持"处理。

## 关键结论：亮度的真实能力边界（先读这一节，它决定范围）

以下结论是**读代码验证过的**，不是推断：

| 事实 | 证据 |
|---|---|
| `light` **是**可设置的 GCU 字段 | `MechrevoService.cs:1972` — `["light"] = light.ToString()` |
| `light` 是 **0–4，五个离散档位** | 注释 `MechrevoService.cs:1934-1935`；默认 `light = 4`（`:1936`） |
| `brightNess` 是**只读**字段（仅入站状态） | `MechrevoHw.cs:1451-1460` 将其按十进制百分比解析；没有任何命令会发送它 |
| 不存在独立的"设亮度"命令 | `SetEffectALL` 载荷把 `effect/speed/light/direction/color` 打包在一起（`MechrevoService.cs:1967-1976`） |

**对机主的诚实边界**：GCU 键盘通道**可以**设亮度，但只能通过 `light` 字段、**5 档（0–4）**——不是 HID 的 0–100 刻度——而且**只能在一个同时携带效果名的 `SetEffectALL` 载荷里**。也就是说，不改效果名就无法改亮度。此外，现有回退路径本身就是坏的：它发送的是 `hid.Name`（中文显示名，如"流畅彩虹"，`Settings.V2.cs:347,431`），而协议要求官方英文名（`MechrevoService.cs:1934-1935`），所以旧回退发出的效果名是无效的。

**设计决策**：回退模式下，亮度载体 = **重发 GCU 当前回报的效果名**（`MechrevoHw.KeyboardEffect`，由 `MechrevoHw.cs:1448` 填充，按定义必为官方英文名）+ 新的 `light` 值。这样效果被保留，且**不需要任何映射表**。若 `KeyboardEffect` 为空，使用唯一规范默认值 `"Single"`。被否决的替代方案：为 10 个 HID 中文效果建英文映射表——因为它重新引入了"效果选择"（超出机主范围），且 10 个 HID 效果与 4 个官方效果并非一一对应。承诺的回退范围：**电源（完整）+ 亮度（5 档，效果保留）**；不含效果/速度/方向/颜色，不含 0–100 亮度。

## 1. 探测（Probe）

- **判定来源**：原样复用 `KeyboardRgb.Connect()`（`KeyboardRgb.cs:202-238`）作为探测体——`ResolveDevice()`（`:163-175`）→ `Open()`（`HidDeviceWin.cs:187-204`）→ `EnterCustomMode()`（`:243-249`）。探测 ≡ 真实路径，杜绝分叉。
- **三态判定映射**：
  - `ResolveDevice()` 返回 null（无候选）→ **Unsupported**（确定性结论：无兼容 HID 接口 = 机主所说的"控制器芯片不支持"）。
  - `Open()` 失败**或** `EnterCustomMode()` 失败 → 100 ms 后重试一次 → 仍失败 → **Unsupported**。
  - 全部步骤成功 → **Supported**。
  - 任何异常 / 超过墙钟上限 → **Unknown**（绝不因此回归可用机型）。
- **运行位置与时机**：在接缝处惰性触发（第一次需要做键盘控制决策时），后台线程执行；**不在启动时**、**不在唤醒时**。拿到确定性结论后进程生命周期内缓存。
- **超时**：`Task.Run` 包裹，`await` 加 3 s 上限（枚举 + 内部 `Thread.Sleep(20)` 只需毫秒级，3 s 是余量）。超限 → Unknown。
- **副作用分析**：探测会打开设备并进入自定义模式——即改变控制器模式。这**可以接受，前提是探测不是孤立动作**：对外暴露为 `KeyboardRgb.EnsureHidReadyAsync()`，Supported 时**保持流打开并停留在自定义模式**（调用方随即启动灯效）；Unsupported/Unknown 时释放流。探测从不写帧，因此不会留下"打开但空白"的状态。与静默忽略（silent-ignore）的交互：进入自定义模式并不能解除固件/官方效果稍后的接管，`ReInitCustomMode` 仍是既有补救手段（不变）。
- **探测明确无法检测到的东西**：
  1. 已记录的静默忽略场景——控制器存在、接受写入，但固件/官方效果接管后忽略帧（`KeyboardRgb.cs:417`，`Program.cs:856-858`）。探测仍会报 Supported；这仍是 `ReInitCustomMode` 的职责（不变）。
  2. 错误设备在 `ScoreCandidate`（`:182-191`）中侥幸得分的假阳性。
  3. 瞬时 `Open` 失败（占用/权限）→ 假 Unsupported；由重试一次 + Unknown 判定缓解。

## 2. 能力模型

- 在 `KeyboardRgb` 上新增 `FeatureAvailability ControllerAvailability { get; private set; } = FeatureAvailability.Unknown;`（复用现有三态枚举，`FeatureAvailability.cs:3-8`；先例 `SilentTurboAvailability`，`MechrevoDeviceCapabilities.cs:71-74`）。
- **不**加入 `MechrevoDeviceCapabilities`——那是注册表/GCU 派生的能力集，HID 探测与之正交。这遵循 `MechrevoHw.cs:440-447` 的两类纪律：能力 = 可见性，控制路径 = 第二道闸。
- **缓存**：进程生命周期内缓存在 `KeyboardRgb`。单调性：Unknown → (Supported | Unsupported) 只发生一次；Supported → Unsupported 的降级只允许在用户显式重探时发生（见 §6），绝不自动。
- **消费者**：(a) `Settings.RefreshDeviceCapabilities` 的 UI 状态行；(b) 接缝的路径选择；(c) `MechrevoService` 的服务级第二道闸（`SupportsLightTopic`，`MechrevoService.cs:2142-2151`）保持原样，继续兜底。

## 3. 唯一接缝（防双发）

- 新增**纯函数**策略（可单测，先例 `ShouldRestartEffect` `:414`、`ScoreCandidate` `:182`）：

  ```csharp
  internal static bool ShouldUseGcuKeyboardFallback(
      FeatureAvailability hid, bool hidConnected, bool serviceConnected)
      => serviceConnected && hid == FeatureAvailability.Unsupported && !hidConnected;
  ```

- 所有调用方**独占地、提前返回**地分支——绝不穿透：

  ```csharp
  if (pathIsHid) { /* HID */ return; }
  if (pathIsGcu) { /* GCU */ return; }
  ```

- **最小改动点**：
  - `Settings.V2.cs:337-348`（效果下拉）与 `:383-409`（电源 ON）：Unsupported 时跳过 HID 连接/StartMode 分支；走 `:411-435` / `:448-456` 的 GCU。Unsupported 时**抑制并发的固件电源旁路**（`:394-396`）——HID 未使用，无需旁路。
  - `RgbForm.cs:276-316`（`ApplyModeSelection`）：Unsupported 时不进入 HID 连接分支，电源/亮度交给 GCU。
  - `Program.cs:996-1002` 与 `:1148-1155`：Unsupported 时 `rgb.KbPowerOn` 必为关（没有 HID 效果），现有 GCU-only 的 OFF 路径已经正确；断言不发生任何 HID 调用。
- **防双发是结构性的**：接缝是 `(缓存判定, hidConnected, serviceConnected)` 的纯函数；两个分支按构造互斥且调用方 `return`。另加断言测试（§7.3）。
- 亮度：新增唯一的 `MechrevoService.SetKeyboardBrightnessPreservingEffect(int level0to4)`，内部调 `SetLightEffect(KeyboardCtrl, currentOrDefaultEffect, light: level0to4, ...)`，只在 Gcu 分支使用。

## 4. 亮度载体

- 解决方案：**重发 GCU 回报的当前效果 + 新 `light` 档位**。`MechrevoHw.KeyboardEffect`（`MechrevoHw.cs:1448` 填充）本身就是官方英文名，因此：
  1. 效果被保留（不切换到别的效果）；
  2. 完全绕开"发中文名"的既有缺陷；
  3. **不需要任何 HID→GCU 效果映射表**。
- 若 `KeyboardEffect` 为空/未知，使用唯一规范默认 `"Single"`。
- 被否决的替代方案：为 10 个 HID 中文效果建英文映射表——重新引入效果选择（超出机主范围），且 10↔4 非一一对应。
- 承诺范围重申：回退 = 电源（完整，`SetPower` 独立可设）+ 亮度（5 档，效果保留）。不含任意效果、速度/方向/颜色，不含 0–100 亮度。

## 5. UI

- `rowKeyboard` 保持可见：`Settings.cs:2845` 与 `Settings.V2.cs:311-315` 不变。
- 在键盘块内新增一行 `Label`（命名 `labelKeyboardControllerStatus`），位置在 `Settings.V2.cs:310-462` 的键盘块内。
- Unsupported 时的文案（中文，仓库 UI 语言）：**"本机控制器不支持软件灯效控制，已改用官方通道（仅电源与亮度）"**。Supported/Unknown 时隐藏（空文本）——现有用户布局不变。
- **设置位置**：`Settings.RefreshDeviceCapabilities()` 内的键盘块（`Settings.cs:2835-2857`），读取 `Program.rgb?.ControllerAvailability`。可见性规则沿用现有 `Show(bool)` 风格：`label.Visible = audit || unsupported`（`audit` 取自 `Settings.cs:2793`）。
- **审计模式规则**：绝不从 UI 启动探测（无真实 HID 设备；与 `Settings.V2.cs:320,377` 的 `Program.UiAuditMode` 守卫同一纪律）。审计模式下判定保持 Unknown，状态标签**因审计而可见**（用于布局演练），走 HID 分支，不走 GCU 回退——UI 审计在不碰硬件的前提下覆盖到标签。
- 判定在后台线程到达后，经现有 `InvokeRequired` 守卫（`Settings.cs:2789`）封送回 `RefreshDeviceCapabilities()`。**不得**在 `RefreshDeviceCapabilities`（UI 线程）里调用探测。

## 6. 睡眠/唤醒约束

- **缓存判定；唤醒/重连时绝不重探。** 重探会在唤醒路径上开/关 HID 并切换自定义模式——正是历史上出过缺陷的那类动作；且重探可能导致 HID↔GCU 抖动。
- **时序透明**：接缝不给恢复协调器增加任何 await、延迟或命令数。Supported 机型上行为与今天逐字节一致（同样的 HID + 同样的 GCU 电源旁路）。Unsupported 机型上 HID 分支天然惰性（无控制器），协调器既有的 GCU `PublishLightPower`/`ObserveLightPower` 调用（`Program.cs:999-1000,1148-1149,1179-1180`）不变，且协调器中**不**新增亮度命令——亮度重发只发生在用户显式调亮度时。
- **唤醒后的冲突**：若缓存的 Unsupported 后来控制器变得可用（罕见：热插拔/USB 重枚举），**不**自动翻转。只有显式用户动作（打开 `RgbForm` / 切换键盘行）才触发一次性重探。
- **防路径抖动**：判定在会话内单调；无定时器、无唤醒钩子；接缝只依赖缓存判定 → 无振荡。

## 7. 测试计划（表征先行）

**第 0 步（表征，先于任何编辑）**：为 **Supported** 机型钉住当前行为——HID 路径、并发的 GCU 电源旁路（`Settings.V2.cs:394-396`）、恢复序列（`Program.cs:1140-1189`）——使后续改线可证明为行为保持。

`tests/MechrevoLite.Tests/` 下新增 6 个测试文件：

1. `KeyboardControllerProbeTests.cs` — 判定映射：ResolveDevice null→Unsupported；Open 失败→重试一次后 Unsupported；EnterCustomMode 失败→Unsupported；全部通过→Supported；异常→Unknown。用假 `HidDeviceWin`（虚接缝先例 `KeyboardRgb.cs:258`）。
2. `KeyboardLightPathPolicyTests.cs` — 纯策略 `ShouldUseGcuKeyboardFallback` 在 判定 × hidConnected × serviceConnected 上的全矩阵；钉死"恰好一条路径"。
3. `KeyboardFallbackNoDoubleApplyTests.cs` — **防双发**：spy rgb + spy service；每个电源动作恰好一次 {HID StartMode} 或 {GCU PublishLightPower}；Unsupported 时绝不进 HID 分支；Supported 时绝不进 GCU 分支。
4. `KeyboardFallbackBrightnessTests.cs` — GCU 亮度使用 `light`∈0–4（由 HID 0–100 映射为 5 档），且效果名等于 `_hw.KeyboardEffect`（保留），绝不等于 `hid.Name`。
5. `KeyboardUnsupportedStatusUiTests.cs` — Unsupported 时标签文案存在；Supported 时隐藏；审计模式可见。
6. `KeyboardPathStabilityAcrossResumeTests.cs` — 调用恢复协调器两次（模拟唤醒）：判定不变、**无**重探调用、发布序列与第 0 步基线一致（表征）。

**必过回归清单（全绿）**：`LightingSleepConsistencyTests`、`KeyboardPowerRestoreTests`、`KeyboardEffectProtectTests`、`LightingRestoreCoordinatorTests`、`LightingRestoreRetryTests`、`LightingAlignmentTests`、`LightRowPowerEffectTests`、`KeyboardRgbCompatibilityTests`、`KeyboardRgbReconnectTests`、`KeyboardRgbPacingTests`、`KeyboardFirmwareTakeoverTests`、`KeyboardToggleTests`、`MechrevoHardwareTests`、`LightingStateTests`、`SettingsLayoutTests`、`UiAudit*`。

## 8. 真机验证（S0）

- 熄屏→唤醒 ×10：键盘灯效恢复，无闪烁，不被固件静态效果覆盖，路径不抖动。
- 睡眠→唤醒 ×N（≥5）：**Supported** 机型与改动前逐字节一致（命令序列相同）。
- Unsupported 机型：键盘电源经 GCU 切换；亮度在 5 档间移动；回报的效果被保留（不被重置为中文名的无效效果）；状态行显示；其他灯光（灯条/Logo）不变。
- 唤醒周期中断开/重连 GCU：无双发，无路径翻转。
- 确认唤醒路径上没有新增的 HID 开/关（日志中 `RGB connect`、`HID candidate` 计数不变）。

## 9. 风险登记

| 失败方向 | 缓解 |
|---|---|
| **把可用机型误判为 Unsupported**（对现有用户的回归） | 只有"枚举结果为 null"才是确定性结论；Open/EnterCustomMode 失败重试一次后仍有假阴性风险 → 任何瞬时/异常一律判 **Unknown**，Unknown 保持今天的行为（尝试 HID）。判定只允许在用户显式动作下降级。 |
| **把不可用机型误判为 Supported**（机主原始抱怨未修复，静默无效） | 探测要求"有得分候选 **且** 自定义模式被接受"双条件；记录残余风险（存在但被静默忽略的控制器），保留 `ReInitCustomMode` 作为既有补救。回退是增量式的，误判 Supported 只会维持现状，不会更糟。 |
| **唤醒时双发 / 路径抖动** | 纯接缝的结构性互斥；唤醒不重探；缓存判定单调；`KeyboardPathStabilityAcrossResumeTests` 钉死。 |
| **亮度静默无效**（对机主承诺了不存在的能力） | §"关键结论"已写明诚实的 5 档/效果耦合边界；回退模式下 UI 不得呈现 0–100 控件（见 §11 定案：5 档）。 |

## 10. 文件与规模

沿用既有模式：能力枚举 + `RefreshDeviceCapabilities` + 服务级第二道闸（`MechrevoService.cs:2142`）+ 禁用并给状态（`RgbForm.cs:225-241`）。

| 文件 | 改动 | 约行数 |
|---|---|---|
| `Hardware/KeyboardRgb.cs` | `ControllerAvailability`、`EnsureHidReadyAsync`、探测 | +40 |
| `Hardware/KeyboardLightPathPolicy.cs`（新建） | 纯接缝策略 | +30 |
| `Hardware/MechrevoService.cs` | `SetKeyboardBrightnessPreservingEffect` | +25 |
| `Settings.V2.cs` | 3 处回退调用点改走接缝；状态标签 | +40 / −15 |
| `Settings.cs` | 状态标签接线 | +15 |
| `RgbForm.cs` | `ApplyModeSelection` 用接缝 | +15 |
| `Program.cs` | Unsupported 时跳过 HID 分支 | +10 |
| tests（6 个新文件） | §7 | +350 |

生产代码 diff ≈ **180 行 / 6 个文件**；测试 ≈ 350 行。

## 11. 开工前仍需确认（已全部定案）

原设计留了 4 个问题给机主；机主已逐一答复，本节按"已定决策"记录：

1. Unsupported 机型上 GCU 键盘电源永远等不到确认（已记录的"未确认"情形）时，UI 是否乐观上报"已应用到官方通道"（仅发布，与恢复语义一致）？
   → **定案：乐观显示。发布即视为已应用，不等永不出现的确认。**
2. 亮度粒度：接受 GCU 的 5 档（0–4）作为"亮度"，还是在回退模式下干脆隐藏亮度控件以免显得粗糙？
   → **定案：亮度控件不隐藏，但为 5 档。**
3. 通过 `_hw.KeyboardEffect` 保留当前回报的效果（推荐），还是钉死单一规范效果 "Single"？
   → **定案：保留当前 GCU 回报的效果作为亮度载体，不钉死 "Single"。**
4. 确认 Unknown（探测瞬时失败）按 Supported 处理（即今天的行为）——Unknown 绝不路由到 GCU？
   → **定案：是。Unknown 按现状当"支持"处理。**

## 任务分解（已批准）

依赖图：

| 任务 | 依赖 | 原因 |
|------|------|------|
| T1 纯接缝策略 + 测试 | 无 | 无前置；纯函数 |
| T2 KeyboardRgb 探测（`ControllerAvailability`、`EnsureHidReadyAsync`）+ 测试 | 无 | 与 T1 独立；提供判定来源 |
| T3 改线接缝调用点（`Settings.V2.cs`、`RgbForm.cs`、`Program.cs`）+ 防双发测试 | T1, T2 | 需要策略与判定 |
| T4 状态行 UI（`Settings.V2.cs` 标签 + `Settings.cs` 接线）+ 测试 | T2 | 需要判定才能渲染 |
| T5 亮度载体（`MechrevoService.SetKeyboardBrightnessPreservingEffect`）+ 测试 | T3 | 扩展接缝的 Gcu 分支 |
| T6 回归（§7 全清单） | T3, T4, T5 | 最终闸门 |

并行执行图：

```
Wave 1（立即并行启动）:
├── T1: 纯接缝策略 + 测试        (无依赖)
└── T2: 探测 + 判定 + 测试       (无依赖)

Wave 2（Wave 1 之后）:
├── T3: 改线接缝调用点           (依赖: T1, T2)
└── T4: 状态行 UI                (依赖: T2)

Wave 3（T3 之后）:
└── T5: 亮度载体 + 测试          (依赖: T3)

Wave 4（T3, T4, T5 之后）:
└── T6: 回归套件                 (依赖: T3, T4, T5)

关键路径: T1/T2 → T3 → T5 → T6
```

各任务明细（类别 / 技能 / 验收标准）：

- **T1 纯接缝策略** — 新增 `KeyboardLightPathPolicy.ShouldUseGcuKeyboardFallback(FeatureAvailability, bool hidConnected, bool serviceConnected)` 纯静态；先写 `KeyboardLightPathPolicyTests.cs`（全矩阵）。类别 `quick`；技能 `programming`。验收：新纯文件 + 矩阵测试绿；不改其他生产文件。
- **T2 HID 探测 + 判定** — `KeyboardRgb` 增加 `ControllerAvailability` 与 `EnsureHidReadyAsync`（复用 `Connect()` 体；重试一次；3 s 上限；Unsupported/Unknown 释放流）。`KeyboardControllerProbeTests.cs` 用假 `HidDeviceWin` 接缝。类别 `unspecified-low`；技能 `programming`、`debugging`（须理解 `KeyboardRgb.cs:417` 的静默忽略）。验收：§1 判定映射表被钉住；仅 Supported 时保持流打开。
- **T3 改线唯一接缝** — 将 `Settings.V2.cs:337-348,383-435,448-456`、`RgbForm.cs:276-316`、`Program.cs:996-1002,1148-1155` 的 HID/GCU 阶梯替换为独占提前返回分支；补第 0 步表征测试与 `KeyboardFallbackNoDoubleApplyTests.cs`。类别 `ultrabrain`（并发/时序重、爆炸半径大）；技能 `programming`、`debugging`。验收：表征测试绿（Supported 行为逐字节一致）；防双发测试绿。
- **T4 状态行 UI** — `Settings.V2.cs:310-462` 内加 `labelKeyboardControllerStatus` 行；在 `Settings.RefreshDeviceCapabilities()`（`Settings.cs:2835-2857`）以审计感知的 `Show` 设置文本/可见性。`KeyboardUnsupportedStatusUiTests.cs`。类别 `visual-engineering`；技能 `frontend`、`programming`、`ui-ux-pro-max`。验收：键盘行仍可见；仅 Unsupported（或审计）时显示标签；`SettingsLayoutTests` + `UiAudit*` 绿。
- **T5 亮度载体** — 新增 `MechrevoService.SetKeyboardBrightnessPreservingEffect(int level0to4)` 并接入 Gcu 分支；`KeyboardFallbackBrightnessTests.cs` 断言 `light`∈0–4 且效果 = `_hw.KeyboardEffect`。类别 `unspecified-low`；技能 `programming`。验收：亮度测试绿；线上不再出现无效中文效果名。
- **T6 回归闸门** — 跑 §7 全部回归清单；逐套件报告通过/失败；有红即阻塞。类别 `unspecified-high`；技能 `debugging`。验收：所有点名套件绿。

建议的原子提交序列：

1. `test(keyboard): pin supported-machine seam + restore characteristics`（第 0 步）
2. `feat(keyboard): add pure GCU-fallback path policy`（T1）
3. `feat(keyboard): probe controller support with three-state verdict`（T2）
4. `refactor(keyboard): single HID/GCU seam, exclusive branches`（T3）
5. `feat(settings): keyboard fallback status line`（T4）
6. `feat(keyboard): GCU brightness preserving current effect`（T5）

每个提交可编译且自身测试通过；任何提交不触碰 `release\`。

**成功标准**：§7 新测试绿、回归清单绿；Unsupported 时 `rowKeyboard` 可见且带状态行，Supported 时不变；每个动作恰好一条路径发布（无双发）；唤醒路径命令顺序/数量/延迟不变、唤醒不重探；对机主承诺的亮度边界与真实一致（5 档、效果耦合）。
