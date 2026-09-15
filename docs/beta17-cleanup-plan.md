# beta17 清理计划（RISKY 波次执行方案）

- 依据：`docs/code-audit-deadcode.md`（审计）+ beta16 commit `764f3f4`（SAFE 波执行，无独立 ledger 文件）。
- 状态基线：SAFE 波已随 beta16 发布（git diff 显示约 15,790 行删除，含 ~1,200 行手写代码 + ~1,900 行生成代码 + 59 个资源文件 + 20 个 locale resx）；全量测试 **1619 pass / 0 fail / 1 skip**；Release x64 构建 0 err / 4 warn（均为既有警告）。
- 注：`artifacts/run8-cleanup/CLEANUP-LEDGER.md` 不存在（目录为空），本计划基于 git diff 反推 SAFE 波覆盖范围。
- 本文档是**规划文档**，不含任何代码改动。执行时按波次独立分支、独立提交、独立验证。
- 行号说明：所有 `file:line` 均已在当前工作区用 grep 复核（2026-09-15）；执行前若文件有新改动，需先重跑对应 grep 确认行号。

---

## 1. 范围（Scope）：仍开放的审计发现

SAFE 波已随 beta16 发布（约 15,790 行删除，含 ~1,200 行手写代码 + ~1,900 行生成代码 + 59 个资源文件 + 20 个 locale resx）。以下为**尚未处理**的条目，分三类：审计 RISKY 波（W2.x）、SAFE 执行时跳过的条目、审计中标记 [待确认] 且仍未决的条目。

### 1.1 RISKY 波未做项（审计 §C Wave 2）

| # | 位置 | 内容 | 审计理由 |
|---|------|------|----------|
| R1 | `Gpu\GPUModeControl.cs:412`（审计记 :417，现 :412） | `if (HardwareControl.IsUsedGPU())` 确认分支；`IsUsedGPU` 实现恒 `=> false`（`HardwareControl.cs:147`） | 「GPU 正在被占用，是否仍切换」的确认对话框**永远不会弹出**，`ScheduleGpuEco(1, delay)` 无提示直接执行。假门禁。 |
| R2 | `Stubs.cs:80/81/93/94/127` | `HasRandomColor/HasSecondColor/IsAnyPeripheralConnect/IsAuraSync/IsDeviceReady` 全部 `=> false` | 外围设备面板整条链路无效（`Settings.cs:5056/5073/5103` 消费这些 stub）。 |
| R3 | `Gpu\IGpuOverclockControl.cs:24` | 接口默认实现 `bool WritesRequireElevation => false;` | 消费者 `MechrevoHw.cs:581`；NV 实现（`NvidiaGpuControl.cs:30`）返回真实值，但默认实现恒 false，非 NV 实现走这条即永久跳过提权。**[待确认]**：grep 确认 `IGpuOverclockControl` 唯一实现类是 `NvidiaGpuControl`，故默认实现当前不可达，可删；但需在执行时再确认无反射构造。 |
| R4 | `Settings.cs:5152` `ButtonFnLock_Click`、`Settings.cs:3963` `ComboKeyboard_SelectedValueChanged` | 事件处理器从未被 `+=` 订阅 | FnLock 按钮、键盘选择下拉**永不响应**——功能永不生效的强证据。 |
| R5 | `Program.cs:1429` `OnChargerEvent()` | 零调用（grep 确认仅定义，全库唯一出现） | 充电事件处理入口悬空；实际电源事件走 `Settings.cs` 的 `WndProc`/`PowerSetting` 分支。`SchedulePowerCheck()` 已由其他路径覆盖。 |
| R6 | `HardwareControl.cs:171` `RefreshBatteryHealth()` 空体 `{ }`，被 `Settings.cs:3397` 调用 | 「刷新电池健康」入口执行空操作 | 审计 A7-7。 |
| R6b | `Hardware\DeviceCapabilitySnapshot.cs:47` `IsKnown` 属性 | 零调用方（grep `.IsKnown` 全库 = 0） | `FeatureSupport` record struct 上的属性，SAFE 波未清理。编译器不会报（属性声明合法）。 |
| R7 | sync-over-async（审计 B4） | UI 可达的阻塞调用：`Settings.cs:2396`（`task.Result`）、`Mode\ModeControl.cs:95`（`_modeTask.Wait(5000)`）、`Hardware\MechrevoHw.cs:3105`（`DisconnectAsync().WaitAsync(...).GetAwaiter().GetResult()`）、`Gpu\NVidia\ElevatedGpuOverclockHelper.cs:328`、`Helpers\ProcessHelper.cs:361/369` | UI 线程可感知卡顿 / 死锁隐患。硬件时序类 `Thread.Sleep` 不动。 |
| R8 | 灯效下发路径收敛（审计 B3） | UI 侧直发：`RgbForm.cs:307`、`LightForm.cs:213`（`Program.hw.Publish` 直发 GETSTATUS）、`Settings.V2.cs:503/562`；`Program.cs:999/1102/1148/1179` 多处直接 `PublishLightPower`+`ObserveLightPower` | 同一状态多个真值来源；`Program` 层已用静态位去重补偿，说明重复下发确已造成固件重初始化/闪烁。 |
| R9 | 空 catch 补日志（审计 B6） | 35 处 `catch { }`（grep 复核，21 个文件）；会静默丢失真实失败的关键处：`Display\BrightnessCommitQueue.cs:116`、`Gpu\NVidia\NvmlHelper.cs:87`、`Hardware\MechrevoService.cs:787`、`Pawn\CpuInfo.cs:61`、`UI\RSlider.cs:246`、`RgbForm.cs:296`、`FanCurveForm.cs:247/273` | 硬件失败用户完全无感。**行为改变**（新增日志/提示）。 |

### 1.2 SAFE 执行跳过、需在 beta17 决策的条目（台账 §3）

| # | 位置 | 台账跳过原因 | beta17 处置建议 |
|---|------|--------------|-----------------|
| S1 | `AsusACPI.cs:219` `IsXGConnected() => false` | 被 `MultiModelCapabilityTests.cs:101` 与 `AsusResidueCleanupTests.cs:209`（注释「保留并恒 false，是有意的显式否认」）引用 | **不做**。测试明确声明这是有意保留的显式否认，删除需先改测试意图，收益为零。归入 NON-goals。 |
| S2 | `MechrevoHw.cs:244` `MidFanSeen => false` | 被 `MechrevoHw.cs:346` 诊断串 + `TelemetrySubscriptionTests.cs:65/205` 引用 | **不做**。有测试锚定其「恒 false」语义，删除是纯 churn。归入 NON-goals。 |
| S3 | `PowerNative` `BatteryLifeTime/BatteryFullLifeTime`、`NativeMethods.POWERBROADCAST_SETTING.DataLength` | 是 interop 结构字段，删除破坏 marshal 布局 | **不做**。审计误报，永久 NON-goal。 |
| S4 | `OfficialConsoleIsolation.CreatedUtc` | 序列化进 `OfficialConsoleState.json`，删除改持久化 schema | **不做**。schema 兼容性风险 > 收益。归入 NON-goals。 |
| S5 | `Hardware\OfficialConsoleCatalog.cs`（整文件） | 审计与台账均标 [待确认]（疑为在途改动） | **beta17 前置决策项**：执行前问 owner「是否保留作为未来接线」。若确认废弃 → 删除整文件（约 1 个文件，行数执行时确认）；若保留 → 移入 NON-goals。 |
| S6 | **W1.5 整体**（读但从不写的配置键） | 台账判定「删除读取点 = 行为改变，不满足 SAFE 的可证行为保持标准」 | **升级为 beta17 的一个波次**（见 Wave B）。这是 SAFE 波唯一被推迟的实质工作。 |
| S7 | W1.6 csproj 过期注释 | 台账核实注释已准确，无残留 | **关闭**，无工作。 |
| S8 | A4 未使用参数（`IsUsedGPU(int threshold)`、`AsusACPI.DeviceGet len`、`Stubs.ApplyBrightness log` 等） | 不在 W1.3 范围；`threshold` 与 R1 同门禁 | 随 R1 一并处理（删门禁时连带删参数）；其余参数属纯 churn，归入 NON-goals。 |

### 1.3 W1.5 具体内容（升级为 Wave B 的依据）

审计 A6：「读了但从不写」共 53 键。台账只反对**盲删全部**，审计 W1.5 本身也只要求删「读取点连带功能已死」的键。grep 复核当前状态：

| 键 | 读取点（已复核） | 现状 |
|----|------------------|------|
| `disable_osd` | `Helpers\ToastForm.cs:121` | 无写入路径 → OSD 关闭开关永远无法打开 |
| `fahrenheit` | `Helpers\TempHelper.cs:5` | 恒 false，温度单位锁定摄氏 |
| `theme`（flat） | `UI\RForm.cs:71`（`flatTheme`），消费点 `UI\RComboBox.cs:259` | 恒 false，flat 主题不可达 |
| `topmost` | `Settings.cs` 4 处 + `Program.cs`（审计 :2256/4101/4115/5151、:1884） | 置顶开关无法持久化 |
| `fn_lock` | `Settings.cs:5137` | 读但零写；且唯一按钮 `ButtonFnLock_Click`（R4）从未订阅 |
| `sensors_always` | `Settings.cs:1890` | 恒默认 |
| 其余 ~46 键 | 审计 A6 清单 | 多为 CLI/官方控制台路径在用，**不动** |

「写了但从不读」5 键（`aura_mode`/`calib_mode`/`frequency`/`overlay`/`overlay_game_only`）：grep 复核发现**审计结论已过时**——`AppConfig.cs:563/568` 的 `IsOverlay()`/`IsOverlayGameOnly()` 正是读 `overlay`/`overlay_game_only`（经包装方法读取，审计的字面量统计漏掉）；`Settings.cs:938/3965/4549/4567` 写入、`ScreenControl.cs:254` 写 `frequency`。→ 这 5 键**全部移入 NON-goals（不删）**，审计 A6 该子项作废。

---

## 2. 波次计划（Wave Plan）

共 5 个波次，按「低风险高收益 → 高风险硬件路径」排序。每波独立分支、独立发布、独立验证。**波次间不得合并提交。**

### Wave A — 假门禁与死 UI 入口（行为改变最小、决策项最集中）

- **目标**：消除「以为能用」的假门禁和永不响应的 UI 入口。每项先做产品决策（删 vs 接线），再执行。
- **文件/区域**：
  - R1：`Gpu\GPUModeControl.cs:412` + `HardwareControl.cs:147`（`IsUsedGPU` 及其 `threshold` 参数，S8 连带）。
  - R3：`Gpu\IGpuOverclockControl.cs:24` 删默认实现（已确认唯一实现类 `NvidiaGpuControl`，其 :30 返回真实值）。
  - R4：`Settings.cs:5152` `ButtonFnLock_Click`、`Settings.cs:3963` `ComboKeyboard_SelectedValueChanged` —— **决策点**：接线（= 新功能，需 owner 确认设计意图）或删除（行为保持）。默认建议：删除处理器 + 连带删除 `fn_lock` 读取点（`Settings.cs:5137`，与 Wave B 协同）。
  - R5：`Program.cs:1429` `OnChargerEvent` —— 决策点：删除（实际电源事件走 WndProc/PowerSetting 分支，功能无缺失）。
  - R6：`HardwareControl.cs:171` `RefreshBatteryHealth` 空体 —— 决策点：删除方法 + `Settings.cs:3397` 调用点，或补真实实现（需硬件协议支持，超出清理范围，默认删）。
  - R6b：`Hardware\DeviceCapabilitySnapshot.cs:47` `IsKnown` —— 零调用属性，直接删除。
  - R2：`Stubs.cs` 外围设备链路 —— **决策点**：`Settings.cs:5056/5073/5103` 的外围面板消费恒 false 的 stub。建议整链删除（面板 + stub 方法），或保留面板但明确标注「无外围设备支持」。此项涉及 UI 可见面，放本波末位，可独立拆出。
- **风险**：
  - R1 删除确认分支：GPU 模式切换在「GPU 占用中」场景从「（永不弹的）确认后执行」变为「直接执行」——**用户可见行为不变**（因为对话框本来就不弹），但若未来有人接真实判据会失去挂点。缓解：删除时在 `GPUModeControl` 留一行注释说明历史。
  - R4 若选择接线：FnLock 是键盘功能键路径，接错会劫持 Fn 行为。缓解：默认走删除路线；接线需 owner 明确签字。
  - R2 删外围面板：Settings 页面布局变化，UI 测试可能断言相关元素。缓解：先跑 UI 测试找断言点，再删。
- **测试**：
  - 保持绿：全量 `tests\MechrevoLite.Tests`（基线 1619/0/1）；特别关注 `Gpu*Tests`、`DgpuApplicationSafetyTests`、`Settings*Tests`、`AsusResidueCleanupTests`（确认不误删其锚定的 `IsXGConnected`）。
  - 新增：`IGpuOverclockControl` 无默认实现后，若测试反射检查接口成员需更新；为「删除 IsUsedGPU 后 GPU 切换无确认直接执行」补一条静态扫描测试（防回归复活）。
- **验证**：`dotnet build`（Debug+Release x64）0 err / ≤4 既有 warn；全量测试绿；`--ui-audit` findings 不高于当前基线（注意台账记录的基线含并发任务引入的 +9 Settings overflow，执行时先重测当前基线）；真机：切换 GPU 模式（Eco↔Standard）各 2 次、打开 Settings 各分组确认无缺失控件、FnLock 按键行为与改动前一致（即：本来就不响应，删除后仍不响应）。
- **预期 LOC**：约 **−120 ~ −200**（R1 ~15、R3 ~5、R4 ~40、R5 ~10、R6 ~5、R6b ~3、R2 ~50-120 视决策）。

### Wave B — 配置键死逻辑（W1.5 升级波）

- **目标**：删除「读取点连带功能已死」的配置键逻辑（§1.3 表格前 6 键），不盲删其余 46 键。
- **文件/区域**：`Helpers\ToastForm.cs:121`（`disable_osd` 早退）、`Helpers\TempHelper.cs:5`（`fahrenheit` 常量及其消费点）、`UI\RForm.cs:71` + `UI\RComboBox.cs:259`（`flatTheme` 分支）、`topmost` 的 5 处读取点（`Settings.cs` ×4 + `Program.cs` ×1，执行时 grep 复核行号）、`Settings.cs:1890`（`sensors_always`）、`Settings.cs:5137`（`fn_lock`，若 Wave A 未连带删）。
- **风险**：
  - **这是行为改变**：手改 `config.json` 设置这些键的用户会失去该（从未在 UI 可达的）能力。台账正是因此跳过。缓解：每键删除前 grep 确认零写入路径（含动态键名拼接）；在发布说明中注明。
  - `fahrenheit`：若 owner 未来想做温度单位切换，删除读取点会增加返工。缓解：执行前向 owner 确认「摄氏锁定是最终决策」。
  - `flatTheme` 分支删除触及 `RComboBox.cs:259` 的渲染路径。缓解：删除后跑 UI audit 对比 DPI 缩放 findings。
- **测试**：保持绿：`AppConfigStorageTests`、`NumericSettingTests`、全量套件。新增：为「`disable_osd`/`fahrenheit`/`theme` 键已无读取点」补静态扫描测试（防复活）。
- **验证**：build + 全量测试 + UI audit；真机：OSD Toast 正常弹出、温度显示摄氏、主题为默认、置顶行为与改动前一致。
- **预期 LOC**：约 **−40 ~ −80**（审计 W1.5 估计值，未执行过，以实际为准）。

### Wave C — sync-over-async（UI 阻塞点，逐条改）

- **目标**：消除 UI 线程可达的 `.Wait/.Result` 阻塞。**不改硬件时序**。
- **文件/区域**（grep 复核后的 UI 可达清单；`UpdateSelfTest.cs`、`Program.cs:250` 为 CLI/诊断分支，低优先）：
  1. `Mode\ModeControl.cs:95` `_modeTask.Wait(5000)` —— UI 路径调用即卡界面 5s，**收益最大**。
  2. `Settings.cs:2396` `task.Result` —— 已有 `ContinueWith` 保证完成，改为透传参数即可。
  3. `Hardware\MechrevoHw.cs:3105` `DisconnectAsync().WaitAsync(...).GetAwaiter().GetResult()` —— 在 Dispose 路径，同步上下文特殊，**谨慎**：若无法安全 async 化则保留并加注释「Dispose 路径，有意同步」。
  4. `Gpu\NVidia\ElevatedGpuOverclockHelper.cs:328` —— 提权助手路径，确认调用线程后决定。
  5. `Helpers\ProcessHelper.cs:361/369` —— 子进程输出等待，已有超时，改 await 需评估调用方是否同步签名。
  - `Thread.Sleep`（`NvidiaGpuControl.cs`、`KeyboardRgb.cs`、`RyzenSmu.cs` 等）：**只确认调用线程并加注释，不改**。
- **风险**：`Wait→await` 若时序变化会复现竞态（尤其 GPU 切换、模式应用）。缓解：逐条改、逐条跑 `SwitchConcurrencyTests`、`GpuHotSwitchRollbackTests`；`MechrevoHw.cs:3105` 若有疑虑直接保留（本波允许「确认后保留」作为完成态）。
- **测试**：保持绿：`SwitchConcurrencyTests`、`GpuHotSwitchRollbackTests`、`Mode*Tests`、全量。新增：无（行为保持型改动，靠既有并发测试兜底）。
- **验证**：build + 全量测试；真机：低配场景（传感器刷新进行中）切换性能模式、GPU 模式，观察 UI 无卡顿；熄屏/唤醒循环 3 次无异常。
- **预期 LOC**：约 **±30**（改写为主，非删除）。

### Wave D — 灯效下发路径收敛（B3，硬件路径，最后做）

- **目标**：把 UI 侧直发的灯电源/效果下发收敛到 `MechrevoService` 单一入口，UI 只发意图。
- **文件/区域**（grep 复核）：
  - `RgbForm.cs:307`（直发 `SetLightPower("Keyboard/Ctrl", true)`）
  - `LightForm.cs:213`（`Program.hw.Publish` 直发 GETSTATUS —— 注意这是查询不是下发，先确认是否在收敛范围内，可能只需改走 service）
  - `Settings.V2.cs:503/562`（`HidLightbar`/`HidLightbar_Logo` 直发）
  - `Program.cs:999/1102/1148/1179`（多处 `PublishLightPower`+`ObserveLightPower` 对）—— 这几处是 `Program` 层的恢复/协调逻辑，收敛目标是让它们走 `LightingRestoreCoordinator` 而非散落直发。
  - 连带（审计 B7）：`"Keyboard/Ctrl"`/`"HidLightbar/Ctrl"`/`"HidLightbar_Logo/Ctrl"` 魔法串抽常量（`MechrevoHw.cs:1106-1108`、`MechrevoService.cs:1983`、`FunctionVerifier.cs:208/209`、`UiAuditRunner.cs:171/172` 等）。
- **风险**：**本计划中风险最高的一波**。时序处理不当会复现「固件重初始化/灯条闪烁」——这正是 `Program` 层一堆静态去重位存在的原因。`Settings.V2.cs:387` 注释表明 `SetLightPower` 最坏耗时 ≈2.08s，收敛时若把本可并行的下发串行化，开关会明显变慢。缓解：不改任何下发时序，只改「谁调用」；每改一处跑灯效测试族；真机验证是硬性门槛。
- **测试**：保持绿：`LightingRestoreCoordinatorTests`、`LightingRestoreRetryTests`、`LightingStateTests`、`KeyboardPowerRestoreTests`、`LightingSleepConsistencyTests`、`LightingAlignmentTests`、`KeyboardEffectProtectTests`、全量。
- **验证**：build + 全量测试 + UI audit；真机（必做，逐项留证据）：① 熄屏/唤醒 ×10，灯条不闪、颜色模式保持；② 电源插拔 ×10，外置灯带断电/恢复时序与改动前一致；③ RgbForm/LightForm/Settings.V2 三处 UI 开关灯各 ×5；④ 快速连续切换灯效 ×10 无固件重初始化。
- **预期 LOC**：约 **−50 ~ +30**（收敛以搬移/删重复为主，可能新增少量协调代码）。

### Wave E — 空 catch 补日志（B6，可选波，可与 D 并行或紧随）

- **目标**：给会静默丢失真实失败的空 catch 补最低限度日志（§1.1 R9 列出的 8 处）；Dispose/清理路径的空 catch（`WaterCoolerBle.cs`、`Logger.cs` 等）**不动**。
- **文件/区域**：`Display\BrightnessCommitQueue.cs:116`、`Gpu\NVidia\NvmlHelper.cs:87`、`Hardware\MechrevoService.cs:787`、`Pawn\CpuInfo.cs:61`、`UI\RSlider.cs:246`、`RgbForm.cs:296`、`FanCurveForm.cs:247/273`。
- **风险**：最低。新增日志行有性能开销（可忽略）；`ToastForm` 用户提示属 UX 变化，**本波只加日志不加 Toast**，Toast 提示留待 owner 单独决策。
- **测试**：全量保持绿；新增：无（日志无断言价值）。
- **验证**：build + 全量测试；真机：触发一次亮度提交失败场景（如快速拖动亮度条）、GPU 模式切换，确认日志文件出现新条目且无异常行为。
- **预期 LOC**：约 **+15 ~ +25**（净增）。

---

## 3. 排序理由（Ordering Rationale）

1. **Wave A 最先**：单位风险最低的「行为改变」——删除的分支/处理器本来就永不生效（恒 false 门禁、零订阅处理器），用户可见行为几乎不变，但能一次性清掉最多决策项（5 个决策点集中处理），并为 Wave B 扫清 `fn_lock` 依赖。
2. **Wave B 第二**：与 A 同属「删永不生效的逻辑」，且 A 中 R4 的处置（删 `ButtonFnLock_Click`）直接决定 B 中 `fn_lock` 键的删除方式——**同文件（Settings.cs）工作合并处理，避免二次 churn**。
3. **Wave C 第三**：纯行为保持型改写，不依赖 A/B 的结果，但放在灯效收敛之前——先消除 UI 阻塞，能让 Wave D 真机验证时的「卡顿」噪声与「闪烁」噪声解耦，便于归因。
4. **Wave D 最后（硬件路径隔离）**：风险最高、验证成本最高（需真机 4 项专项测试）。放最后使前四波已验证的成果不被其潜在回滚拖累；且 D 触及的 `Program.cs`/`Settings.V2.cs` 与 C 触及文件有重叠，分开做避免同文件冲突。
5. **Wave E 独立可插**：零耦合、净增行数，可在任意间隙执行，也可与 D 并行（不同文件）。放在末位仅因它是「可选增强」而非清理。
6. **通用原则**：每波独立分支独立发布；同文件条目尽量并入同一波；硬件/固件路径（D）最后且隔离；每波执行前重跑 grep 复核行号（本计划的行号基于 2026-09-15 工作区）。

---

## 4. 明确不做（NON-goals）

| 项 | 理由 |
|----|------|
| 重写 AppConfig/Settings 配置模型（审计 Wave 3） | `AppConfig` 已有原子写 + 损坏恢复，成熟稳定；改动收益低于回归风险。 |
| 删除 `Stubs.cs` 全部空实现（审计 Wave 3） | 仍有活调用点（盖合、会话锁、GPU 切换路径），删除需连带重写这些路径；属另一件事。只处理 R2 涉及的外围设备链路。 |
| 删除 `src\MechrevoLite\` 孤儿项目（审计 A12/Wave 3） | 可能是「未来 WPF 版」种子；695 行的删除收益不值得在清理版本里夹带此决策。若 owner 确认废弃，单独开任务。 |
| 未使用 `using` 逐个手删（审计 A5/Wave 3） | 无编译器 pass 时误删风险高。SAFE 波完成后用 `dotnet format analyzers --diagnostics IDE0005` 一次性处理，属独立小任务，不占 beta17 波次（若 owner 要求可追加为 Wave F）。 |
| `AsusACPI.IsXGConnected`（S1）、`MechrevoHw.MidFanSeen`（S2） | 测试明确锚定其「恒 false」语义（`AsusResidueCleanupTests.cs:209`、`TelemetrySubscriptionTests.cs:65/205`），删除是纯 churn。 |
| interop 结构字段（S3：`BatteryLifeTime`/`BatteryFullLifeTime`/`DataLength`） | 审计误报，删除破坏 marshal 布局。永久 NON-goal。 |
| `OfficialConsoleIsolation.CreatedUtc`（S4） | 序列化进持久化 JSON，schema 兼容风险 > 收益。 |
| 「写了但从不读」5 键（`aura_mode`/`calib_mode`/`frequency`/`overlay`/`overlay_game_only`） | **审计结论已过时**：grep 复核确认 `AppConfig.cs:563/568` 经包装方法读取 `overlay`/`overlay_game_only`，其余三键有活跃写入消费。不删。 |
| A4 其余未使用参数（`DeviceGet len`、`ApplyBrightness log`、`OnTimedEvent source`、`VisualiseIcon themeChange`） | 纯 churn，无行为收益。仅 `IsUsedGPU threshold` 随 R1 连带删。 |
| B6 的「用户可见失败加 Toast 提示」 | UX 变化超出清理范围，需 owner 单独决策。Wave E 只加日志。 |
| B1/B2/B8 上帝对象拆分、超长方法重构、B5 DPI 口径统一、B7 其余魔法数字 | 均为行为保持型大重构，churn 与回归风险远超清理版本承受范围；如需做应独立立项。 |
| A10 NuGet 包 | 审计 + 台账双重确认 6 包全部在用，无可删。 |
| W1.6 csproj 注释 | 台账已核实无残留，关闭。 |

---

## 5. 每波完成定义（Definition of Done）

每波 DoD 均为二元可判定陈述。执行者必须逐条给出证据（命令输出/截图/日志路径），缺一即未完成。

**通用前置（每波）**：
- [ ] 执行前重跑本计划对应 grep，行号仍准确（漂移则先更新计划再动手）。
- [ ] 独立分支；每条目独立提交。

**Wave A**：
- [ ] `dotnet build` Debug + Release x64：0 err，警告 ≤ 4（既有基线），0 新警告。
- [ ] 全量 `dotnet test tests\MechrevoLite.Tests`：≥1619 pass / 0 fail（新增测试计入 pass 数）。
- [ ] 新增 ≥1 条静态扫描测试，断言 `IsUsedGPU` 确认分支不存在；`FeatureSupport.IsKnown` 无调用方。
- [ ] `--ui-audit` findings ≤ 执行当日重测的基线值（先重测基线再对比，不沿用本文档数字）。
- [ ] 真机：GPU 模式切换 Eco↔Standard 各 2 次成功；Settings 全部分组打开无缺失/错位控件；FnLock 键行为与改动前一致。证据：照片或日志。

**Wave B**：
- [ ] 对删除的每个键 grep 证明零写入路径（输出存 `artifacts/beta17/`）。
- [ ] build 0 err / 0 新警告；全量测试 ≥ 基线 pass / 0 fail。
- [ ] 新增 ≥1 条静态扫描测试，断言 `disable_osd`/`fahrenheit`/`theme` 无读取点。
- [ ] 真机：OSD Toast 弹出正常、温度显示摄氏、主题默认、置顶行为不变。证据：截图。

**Wave C**：
- [ ] §2 Wave C 清单中每条：已改 await，或书面确认「有意保留同步」并附注释（`MechrevoHw.cs:3105` 允许保留）。
- [ ] build 0 err / 0 新警告；`SwitchConcurrencyTests`、`GpuHotSwitchRollbackTests` 单独跑 + 全量测试 0 fail。
- [ ] 真机：传感器刷新进行中切换性能模式与 GPU 模式，UI 无可感知卡顿；熄屏/唤醒 ×3 无异常。证据：录屏或日志时间戳。

**Wave D**：
- [ ] `RgbForm`/`LightForm`/`Settings.V2` 无对 `Program.hw.Publish`/`service.SetLightPower` 的绕过协调器的直发（grep 证明，输出存档）。
- [ ] 灯效测试族 7 个文件全部绿 + 全量测试 0 fail。
- [ ] 真机 4 项专项全部通过并留证据：① 熄屏/唤醒 ×10 灯条不闪；② 电源插拔 ×10 时序一致；③ 三处 UI 开关灯各 ×5；④ 快速连续切换 ×10 无固件重初始化。
- [ ] 任一专项失败 → 本波回滚，不得带病发布。

**Wave E**：
- [ ] §2 Wave E 列出的 8 处空 catch 均含 `Logger.WriteLine`；Dispose/清理路径空 catch 数量不变（grep 对比）。
- [ ] build 0 err / 0 新警告；全量测试 0 fail。
- [ ] 真机：触发一次亮度快速调节 + GPU 切换，日志出现新条目，行为无异常。

**发布门槛（全部波次后）**：
- [ ] Release x64 构建 0 err / ≤4 既有 warn。
- [ ] 全量测试最终计数 ≥ 各波新增测试之和 + 1619，0 fail。
- [ ] `--ui-audit` findings ≤ 执行当日基线。
- [ ] beta17 发布说明中列明所有行为改变点（Wave A 删除的入口、Wave B 失效的手改配置键）。

---

## 6. NOT COVERED / 不确定项

1. **`OfficialConsoleCatalog.cs` 的意图（S5）**：审计与台账均标 [待确认]。需 owner 回答「是否保留作为未来官方控制台接线」才能决定删除或归入 NON-goals。**这是 Wave A 前唯一的阻塞决策。**
2. **R4 的设计意图**：`ButtonFnLock_Click`/`ComboKeyboard_SelectedValueChanged` 是「未完成的功能」还是「废弃的残留」？审计无法判定。本文档默认按「删除」规划；若 owner 要接线，Wave A 范围扩大为功能新增，需重新评估测试与真机验证项。
3. **`fahrenheit` 的产品意图**：温度单位切换是否在路线图上？若是，Wave B 不应删 `TempHelper.cs:5` 的读取点。
4. **`LightForm.cs:213` 的 GETSTATUS 直发是否属于 B3 收敛范围**：它是查询而非下发，审计未明确。执行 Wave D 时需先确认（倾向：改走 service 但保持查询语义）。
5. **动态键名写入路径的完整性**：审计 D.4 指出 A6 统计基于字面量键名。本文档已复核「写了不读」5 键实为误报，但「读了不写」53 键中其余 ~46 键是否存在 `GetString(变量)` 式动态读，未逐一验证。Wave B 执行时对每个删除键必须重查。
6. **UI audit 基线漂移**：台账记录的 95 findings 含并发任务引入的 +9，且 audit 本身环境敏感（3 次运行 2 次中止）。本文档不沿用该数字，要求每波执行当日重测基线。
7. **反射/序列化保留名**：`obfuscar.xml` 与 Newtonsoft 序列化可能按名字保留成员，静态引用计数无法覆盖（审计 D.2）。删除 `OfficialConsoleCatalog` 等项前需检查 obfuscar 配置。
8. **精确 LOC**：本文档 LOC 均为区间估计（基于审计估计值 + grep 复核的条目数），未逐文件计数。执行时以 `git diff --numstat` 实测为准。
9. **`RyzenSmu.PowerLimits` 是否真为 401 行超长方法**：审计自述 brace 计数可能被字符串/注释干扰（D.7），未人工确认。本文档未将其列入任何波次（属 B2 重构，已归 NON-goals）。
10. **测试套件在 beta17 开发期间的基线漂移**：当前 1619 pass 基线含并发任务新增的 10 个测试；beta17 启动时需重测基线并更新 DoD 中的数字。
11. **SAFE 波的实际覆盖范围**：审计 A2 列出的零引用方法（`SetVersionLabel`、`VisualizePeripherals`、`UiStyleBodyFloat`、`IsPawnAvailable`、`ResetPerformanceMode`、`CyclePerformanceMode`、`UnSetBatteryLimitFull`、`SetAsusChargeLimit`、`StopDisableService`、`StartEnableService`、`IsCurrentCustom` 等）在 beta16 中已全部删除（grep 确认全库 = 0）。但审计 W1.3 描述的范围较窄（仅列「类型级」），本计划 Wave A 已将剩余未清理项补入。若执行时发现更多 SAFE 波已删但审计未记录的条目，直接跳过即可。
12. **`IsXGConnected` 与 `MidFanSeen` 的测试锚定**：审计 A7-3/A7-4 列为「假门禁」，但 `AsusResidueCleanupTests.cs:209` 和 `TelemetrySubscriptionTests.cs:65/205` 明确锚定其「恒 false」语义为有意设计。删除需先改测试意图，收益为零。已在 §4 NON-goals 中排除。
