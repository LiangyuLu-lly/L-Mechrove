# Run5 · 剩余二级/三级界面重设计规格（2026-09-14 重定基）

> 范围：`run4-p2p3-design-spec.md` 之外仍需处理的 8 个表面。
> 本文原为 run5 开工前的**设计文档**；run5 期间其中一部分已落地、一部分被更大的重构取代、一部分描述的对象已被删除。
> 2026-09-14 按当前源码**重新定基**：每个表面给出状态与证据，只保留仍然有效的设计指导，不新增设计要求。
> 上游约束：`docs/run4-p2p3-design-spec.md`（§0 节奏基线、§4 横切规则继续有效）、`docs/ui-consistency-pass.md` v2–v8。
> 行号基于 2026-09-14 工作区源码，逐文件核实；无法核实的数字一律标 **UNVERIFIED**。

---

## 0. 状态总表（先读）

| 表面 | 状态 | 证据（已读） | 剩余事项 |
|---|---|---|---|
| RgbForm（键盘灯效） | **OBSOLETE（被更大重构取代）** | `RgbForm.cs:251-253`（本窗不再有自己的模式下拉，单一真相源 `_rgb.KbHidMode`）；`Settings.V2.cs:601`（睡眠时间选择器已从 RgbForm 迁出到仪表盘灯光行）；`RgbForm.cs:104-106,143-154`（状态行 0/D(25) 动态占行、高度跟随内容表）；全文无「自定义效果」字样（src 全量检索 0 命中） | 本文 §2 的目标布局整体作废；唯一残留缺陷是色块仍是原生 `Button`（`RgbForm.cs:410`），见 §2 末尾 |
| FanCurveForm（风扇曲线） | **PARTIAL** | 已做：内容实测窗口尺寸（`FanCurveForm.cs:196-206`）、内边距统一走 `D()`（`:69,95-96,101,116,140,149,155`）；测试 `CustomModeFormTests.FanCurveForm_IsContentDerivedAndCurveStillEditable`（`CustomModeFormTests.cs:169`） | 未做：双图仍纵向 50/50 堆叠（`FanCurveForm.cs:165-167`），未改 CPU 左/GPU 右；`btnSave` 仍是原生 `Button`（`:98-107`）；`_status` 仍混用状态与常驻提示（`:74`）。§3 设计指导保留 |
| LightForm（灯条/Logo） | **REMAINING** | 缺陷原样存在：`LightForm.cs:47`（420×240）、`:202`（Percent-100 填充行） | §4 全部保留 |
| RColorPicker（取色器） | **REMAINING** | 缺陷原样存在：`RColorPicker.cs:133`（随机→`Apply(Color.Black)`）、`:100`（`BorderStyle.FixedSingle`）、`:107,256`（rgbLabel 非等宽） | §5 全部保留 |
| ColorCalibrationForm（屏幕校色） | **OBSOLETE（窗体已删除，方案被取代）** | `src\MechrevoLiteWin\` 目录无 `ColorCalibrationForm.cs`；`Settings.cs:851`（注释：P3/AdobeRGB 在本机无实际效果，删除，仅保留校色下拉）、`:858-859`（`comboColorCalibration` 位于屏幕行头）；测试 `Run5LcCalibTests.cs:74,90,116,123,142,154`；真机证据 `artifacts\run5-lc-calib\README-evidence.md` + `geometry-uia.txt` | §6 作废；替代方案（行头圆角下拉，仅 默认/sRGB）已交付，见 §6 末尾 |
| UpdateForm（更新） | **REMAINING** | 缺陷原样存在：`UpdateForm.cs` 无「暂无更新说明」空态文案、`_notes` 仍为原生只读框、`SetBusy`（`:309`）未覆盖反馈按钮 | §7.1 全部保留 |
| DonateForm / DonateControl（赞助） | **REMAINING** | 缺陷原样存在：`DonateForm.cs:17`（760×560）、`DonateControl.cs:29`（`RForm.colorTurbo` 遗留描边）；QR 缺失空态文案未加 | §7.2 全部保留 |
| FirstRunGuideForm（首启引导） | **REMAINING** | 缺陷原样存在：`FirstRunGuideForm.cs:177`（`MaximumSize=(485,0)` 固定像素宽） | §8 全部保留 |

run5 期间与本规格相关的**额外交付**（本文原未覆盖，详见 `docs/run5-delivery-index.md`）：更多开关组新增 深度睡眠/仅关闭显示器/开机自启动、静音狂暴按机型支持三态门控、液冷手动档摘要改百分比、灯效空闲休眠统一时钟重构、CustomModeForm 重做+压缩与「切换未确认」修复。

---

## 1. ui-ux-pro-max 检索记录（本文引用依据）

（原样保留，仍是 REMAINING 项的设计依据。）

| 检索 | 命中准则（严重度） | 本文应用 |
|---|---|---|
| `compact dialog form spacing target size` (ux) | Target Size Minimum（High，≥24 逻辑 px）/ Touch Spacing（Medium，相邻 ≥8px）/ Text Reflow（Critical，禁固定高裁字） | 所有行高 ≥24、按钮间距 ≥Space.Sm；填充行改内容驱动高度 |
| `empty state placeholder` (ux) | Empty States（Medium：空内容给提示+动作，不留白板） | UpdateForm 空说明、DonateForm QR 缺失 |
| `segmented control toggle` (ux) | Keyboard Navigation（High）/ Active State（Medium） | RColorPicker 分段组语义（若后续重做） |
| `form label input error validation` (ux) | Input Labels（High）/ Focusable Error Summary（High） | RColorPicker Hex 错误态已有，补 aria 语义说明 |
| `submit feedback loading progress` (ux) | Submit Feedback（High）/ Progress Indicators（Medium） | UpdateForm 下载互斥+进度、FanCurveForm 保存回显（已有，钉住） |
| `dashboard data monospace technical` (typography) | Dashboard Data（Mono+Sans） | 印证既有决策：读数用 Consolas，正文 YaHei UI，不引入新字体 |

调色板结论（同 run4 §0）：全部沿用 `UiVisualStyle` token 与 `TypeScale` 五档、`Space` 4/8/12/16/24，不引入外部色值。

---

## 2. RgbForm（键盘灯效）— OBSOLETE

**作废原因**：run5 对 RgbForm 的重构方向与本文原设计不同且已走得更远：

- 本窗**不再有自己的模式下拉**。单一真相源是 `_rgb.KbHidMode`（仪表盘键盘行写它），本窗按目录项直接跟随（`RgbForm.cs:251-253`），2s 内重放仪表盘改动（`:156-166`）。原「首项分组头 + 选中弹回」问题连同整个下拉一起消失。
- 「睡眠时间」选择器**迁出到仪表盘灯光行头**（`Settings.V2.cs:601` 注释明示迁移），设备侧计时固定为 0，由应用内空闲检测统一实现（`RgbForm.cs:230-245`）。
- 状态行改为**有事才占行**：行高在 0 与 D(25) 间切换（`RgbForm.cs:104-106,143-154` 注释），常驻说明已删（`:201,302`）。
- 窗口尺寸**内容实测**：宽度=标签列实测+控件列，高度跟随内容表（`:127-154`）；原 520×440 固定尺寸方案作废。
- src 全量检索「自定义效果」0 命中：原 idle caption「自定义效果（HID 直连）」已删除。

**唯一残留缺陷（保留为待办，不另立设计）**：Static/Rain/Matrix 色块仍是原生 `Button` 46×24（`RgbForm.cs:410`，Tag="color-swatch"），与 RColorButton 双 swatch 语义不符。若后续收尾，按原方案替换为 `RColorButton`（`RColorButton.cs:33-53`），Click 仍开 `RColorPicker`，驱动路径不变。

---

## 3. FanCurveForm（风扇曲线，三级）— PARTIAL / 剩余项保留

**已落地（run5）**：

- 窗口尺寸内容实测（FixedSingle 不可调）：宽 = max(标题行实测, 曲线最小宽+内距)，高 = 标题行 + 2×曲线最小高 + 底行（`FanCurveForm.cs:196-206`）。真机约 661×842 设备像素（UNVERIFIED：数字来自 run5 交接，`artifacts\run5-rgb-fancurve\fancurve-dialog-compact.png` 为截图，仓库内无持久化的尺寸记录文件）。
- 全部 ad-hoc 内边距统一到 `D()`/token（`:69,95-96,101,116,140,149,155`）。
- 自绘 token（run4 已修）保持：Track/Border/Muted/Accent/Danger/SurfaceRaised/Text。

**剩余缺陷（设计指导保留）**：

| # | 缺陷 | 位置 |
|---|---|---|
| 1 | 双曲线仍**纵向堆叠**各占 50%，GPU 在上 CPU 在下，与主窗「CPU 优先」阅读顺序相反 | `FanCurveForm.cs:165-167` |
| 2 | `btnSave` 仍是原生 `Button` 88×30（ApplyPrimaryButton 只刷色） | `FanCurveForm.cs:98-107` |
| 3 | `_status` 仍混用操作提示与状态两种语义（初始「拖动曲线点上下调整占空比」，后被「表：xxx」覆盖） | `FanCurveForm.cs:74,131` |

**剩余目标布局**（横向双图方案，原 §3 设计继续有效）：

```
┌────────────────────────────────────────────────────┐
│ 风扇曲线（当前自定义档）   表：GT0  · 拖动点调占空比 │ 26 标题行（状态+常驻提示拆两个 Label）
│ ┌──────────── CPU ────────────┐┌──────────── GPU ────────────┐│
│ │ 网格=Track 虚线  轴=Border   ││ 线/点=Danger                ││
│ │ 轴字=Muted     线/点=Accent ││                             ││
│ └─────────────────────────────┘└─────────────────────────────┘│
│ [保存(主按钮 RButton)] ☐风扇独立控制  关=双风扇跟随GPU曲线 │ 34 底行
└────────────────────────────────────────────────────────────┘
```

- MOVE：charts 1×2 纵向 → 2×1 横向（CPU 左、GPU 右）；`Geo()` 内边距不变，仅容器变化。
- REPLACE：`btnSave` → `RButton` + `ApplyPrimaryButton`（驱动 `Program.hw.SetFanCurve` 不变）。
- REMOVE：无控件删除；`_status` 拆两段（状态 + 常驻提示，同 run4 §3.3 语义拆分）。

**DoD（剩余部分）**：
- [ ] CPU 在左、GPU 在右。
- [ ] 无原生 Button。
- [ ] 保存回显保留（「已确认 HH:mm:ss」/「保存未确认」路径不变）。
- [ ] 175% 无裁剪/无 parent-overflow（UiAuditRunner）。

---

## 4. LightForm（灯条/Logo ×2）— REMAINING / S

**现状（未动）**：420×240 FixedSingle（`LightForm.cs:47`），5 行参数表，全 token、R* 控件齐备、关灯禁用已实现。**基本达标**，三项残余缺陷原样存在：

| # | 缺陷 | 位置 |
|---|---|---|
| 1 | 行标签「灯光」与 RCheckBox 文本「灯光开关」重复，标签列白占 96px | `LightForm.cs`（行标签与 checkbox 文本） |
| 2 | 末尾 Percent-100 填充行：内容约 170 逻辑 px，240 高窗口底部留白 ~60px | `LightForm.cs:202` |
| 3 | `colorBtn` 作为唯一取色入口偏小，且与 RgbForm 色块宽度不一致 | `LightForm.cs:168-188` |

**目标布局**（420×**210**，缩 30）：

```
┌────────────────────────────────────┐ 420×210
│ ☐ 灯光开关（跨2列）                │ 28
│ 模式     [RComboBox──────────]     │ 32
│ 亮度     [RSlider────────────]     │ 32
│ 速度     [RComboBox──────────]     │ 32
│ 单色颜色 [RColorButton 36×24]      │ 28
└────────────────────────────────────┘  （无填充行，AutoSize 收口）
```

**Remove / Move / Add**：
- MOVE：灯光开关跨 2 列、删行标签。
- REMOVE：Percent-100 填充行（`:202`）；ClientSize 240→210，root 改 AutoSize 收口（钳制工作区）。
- REPLACE：`colorBtn` → 36×24（对齐 RgbForm 色块尺寸；仍 `RColorButton`，驱动 `RColorPicker` 不变）。
- ADD：无。

**DoD**：
- [ ] 无 >16 逻辑 px 纯填充行；高度 ≤210。
- [ ] 开关行无冗余标签。
- [ ] 关灯时参数行禁用（现有行为保留，回归验证）。
- [ ] 两个实例（灯条/Logo）截图通过。

---

## 5. RColorPicker（取色器，三级模态）— REMAINING / S

**现状（未动）**：≈412×330 手排（`RColorPicker.cs`），中文化/主题描边/Hex 校验均已修（run4）。三项残余缺陷原样存在：

| # | 缺陷 | 位置 |
|---|---|---|
| 1 | **「随机颜色」按钮点击后 `Apply(Color.Black)`**，「随机」实际是「黑色」，语义死亡 | `RColorPicker.cs:130-135` |
| 2 | 预览框 `BorderStyle.FixedSingle` 原生 3D 边框，与全应用 1px token 描边不一致 | `RColorPicker.cs:100` |
| 3 | RGB 读数 `rgbLabel` 非 Consolas，与「数据读数等宽」规范不符 | `RColorPicker.cs:107,256` |

**目标布局**（结构不变，≈412×330）：SV 200×200 + 色相 18×200 + 预览 150×110（1px Border 描边）+ 十六进制输入 + RGB 读数（Consolas，Muted）+ 默认/自定义色板 + [随机] [确定] [取消]。

**Remove / Move / Add**：
- FIX：`random.Click` → 从 `Defaults` 色板随机取色（`aura_color_custom` 持久化路径不变）；若判定「随机」无需求则 REMOVE 该按钮（仅 `allowRandom=true` 调用方可见，先确认调用点）。
- REPLACE：预览描边 → 1px `UiVisualStyle.Border` 自绘（Swatch 同款画法）。
- REPLACE：`rgbLabel` 字体 → Consolas（读数）。
- ADD：无。

**DoD**：
- [ ] 「随机」按钮行为与文案一致（或移除）。
- [ ] 无原生 FixedSingle 边框。
- [ ] RGB 读数等宽。
- [ ] 175% 手动 S() 缩放回归无裁剪。

---

## 6. ColorCalibrationForm（屏幕校色）— OBSOLETE（窗体已删除）

**作废原因**：2026-09-14 用户决策：P3/AdobeRGB 在本机无实际效果，删除；校色入口改为**屏幕行头内的圆角下拉**，仅保留 默认/sRGB 两档（`Settings.cs:851` 注释原文记录了该决策）。

**实际交付形态**（替代原 §6 设计）：

- `comboColorCalibration`（AccessibleName「屏幕校色」）位于屏幕行头、自动刷新率开关左侧（`Settings.cs:858-859`；布局测试 `Run5LcCalibTests.ScreenHeadRow_PlacesCalibComboLeftOfTheAutoRefreshSwitch`，`Run5LcCalibTests.cs:123`）。
- 下拉仅两项（`Run5LcCalibTests.ColorCalibrationCombo_HasExactlyDefaultAndSrgb`，`:74`）；回显当前档（`:90`）；索引只映射两个活档（`:116`）；写入走既有 `Program.service.SetColorCalibration` 路径（`:142`）；被删色域保持删除（`RemovedGamutModes_StayRemoved`，`:154`）。
- 真机证据：`artifacts\run5-lc-calib\README-evidence.md`（UIA 几何：下拉与自动刷新率开关同行、30px 间距、无重叠；注册表回读 ColorCalibration=1 与下拉显示一致）。
- 写路径单测覆盖 `SetColorCalibration(1/2/3/0)` publish+confirm 共 6 条（MechrevoHardwareTests）。

**UNVERIFIED（真机）**：sRGB↔默认 的活体写往返（测试时桌面锁定，无法注入点击；见 `artifacts\run5-lc-calib\README-evidence.md` 末节）。

原 §6 的「原生按钮→分段组、删体内重复标题、缩至 ~120」设计随窗体删除一并作废。

---

## 7. UpdateForm（更新）+ DonateForm/DonateControl（赞助）— REMAINING / S

### 7.1 UpdateForm — REMAINING / S

**现状（未动）**：520×380 模态，全 token、RButton×4 全接线。四项缺陷原样存在：

| # | 缺陷 | 位置 |
|---|---|---|
| 1 | **空态违例**：检查失败/已是最新时 `_notes` 是空只读框占满中部 | `UpdateForm.cs`（`_notes` 空态路径） |
| 2 | 下载中 `SetBusy` 禁用 install/downloadPage/later 但漏了反馈按钮 | `UpdateForm.cs:309`（SetBusy 定义处） |
| 3 | 4 个按钮宽度各不相同，行节奏参差 | `UpdateForm.cs`（按钮行） |
| 4 | `_notes` 原生 `TextBox` FixedSingle 边框 | `UpdateForm.cs`（`_notes` 构造处） |

**目标布局**（520×380 不变；空态时高度收缩）：标题/版本行 + 更新说明（RTextBox 只读，无内容时整行隐藏、显示 Muted「暂无更新说明」）+ 状态行 + 进度条（仅下载中可见）+ 按钮行（统一宽 96/96/88/88 两档，间距 Space.Sm）。

**Remove / Move / Add**：
- ADD：空说明态（数据源 `info.Notes`；无新功能）。
- FIX：`SetBusy` 补反馈按钮 `Enabled = !busy`。
- REPLACE：`_notes` → `RTextBox`。
- 统一：按钮宽度两档。
- REMOVE：无。

**DoD**：
- [ ] 无更新说明时无空框（有文案）。
- [ ] 下载中 4 按钮全部禁用。
- [ ] 按钮宽度两档对齐。
- [ ] 无原生 TextBox。

### 7.2 DonateForm + DonateControl — REMAINING / S

**现状（未动）**：DonateForm 760×560 Sizable（`DonateForm.cs:17`），Display 15pt 标题 + Subtitle 副标题 + 双 QR 50/50。DonateControl 菜单已修（run4）。

| # | 缺陷 | 位置 |
|---|---|---|
| 1 | **QR 缺失时是空 `SurfaceRaised` 色块**，无任何提示 | `DonateForm.cs`（QR 探测路径） |
| 2 | 760×560 偏大：内容实际 ~460 高 | `DonateForm.cs:17` |
| 3 | 徽章提醒描边用遗留静态 `RForm.colorTurbo`，不随主题 | `DonateControl.cs:29` |

**目标布局**（760×**500**）：标题 + 副标题 + 双 QR（Zoom，1px Border）；QR 缺失时 PictureBox 内叠 Muted 居中文案「二维码资源缺失」（Empty States；数据源即现有 `Resources/qrcode1/2.jpg` 探测）。徽章描边 `RForm.colorTurbo` → `UiVisualStyle.Accent`。

**DoD**：
- [ ] QR 缺失有可见文案（删掉图片文件验证）。
- [ ] 高度 ≤500；徽章描边随日夜主题。
- [ ] 菜单中文/无死项回归保持。

---

## 8. FirstRunGuideForm（首启引导）— REMAINING / S

**现状（未动）**：580×390 FixedDialog 模态，3 步骤 + 3 按钮，徽章已 token 化（run4）。残余缺陷原样存在：

| # | 缺陷 | 位置 |
|---|---|---|
| 1 | 描述 `MaximumSize=(485,0)` 固定像素宽，175% DPI 下可能提前折行或右边距不齐 | `FirstRunGuideForm.cs:177` |
| 2 | 步骤行间距 10 不在 Space 刻度（4/8/12/16/24） | `FirstRunGuideForm.cs`（步骤行 Margin/Padding） |
| 3 | 底部按钮行三按钮百分比列宽不齐 | `FirstRunGuideForm.cs`（底行列宽） |

**目标布局**（580×390 不变）：描述宽度自适应（按 `ResponsiveLayout.LogicalToDevice` 计算或 TLP Percent 列，Text Reflow Critical：禁固定宽裁字）；步骤行间距 10→`Space.Sm=8`；底部三列等宽（33/34/33），主按钮视觉权重由 ApplyPrimaryButton 承担。

**DoD**：
- [ ] 无固定像素 MaximumSize；175% 与审计缩放截图无裁剪。
- [ ] 间距全部落在 Space 刻度。
- [ ] 三步内容/按钮 handler 保留。

---

## 9. 汇总（重定基后）

| 表面 | 状态 | 优先级 | 工作量 | 核心动作 | 一句话判定 |
|---|---|---|---|---|---|
| RgbForm | OBSOLETE | — | — | 仅剩色块原生 Button 一项 | 重构已越过本规格，规格作废 |
| FanCurveForm | PARTIAL | P2 | S | 双图纵改横、btnSave→RButton、状态/提示拆分 | 压缩已做，布局换代未做 |
| LightForm | REMAINING | P2 | S | 删填充行缩至 210、开关跨列去冗余标签、色块 36 宽 | 最接近达标，快赢 |
| RColorPicker | REMAINING | P3 | S | 随机按钮语义修复、预览描边 token、RGB 读数等宽 | 已修大半，残余三小项 |
| ColorCalibrationForm | OBSOLETE | — | — | — | 窗体已删，替代方案已交付 |
| UpdateForm | REMAINING | P3 | S | 空态文案、SetBusy 补反馈、按钮宽度两档、RTextBox | 空态+互斥是硬项 |
| DonateForm/Control | REMAINING | P3 | S | QR 空态文案、缩高至 500、徽章色 token | 展示窗，空态是硬项 |
| FirstRunGuideForm | REMAINING | P3 | S | MaximumSize 自适应、间距归刻度、底列等宽 | 已修大半，DPI 收尾 |

**设计系统新增提案：无。** 剩余表面全部可用现有 R* 控件集 + `UiVisualStyle` token 覆盖；唯一跨窗体前置依赖沿用 run4 §4.3：`RComboBox`/`RTextBox` 禁用态绘制补全（状态补全，非新控件）。

**执行顺序（剩余项）**：LightForm(S) → FanCurveForm 剩余(S) → UpdateForm(S) → RColorPicker(S) → DonateForm(S) → FirstRunGuideForm(S)。完成后在 `docs/ui-consistency-pass.md` 追加 v9 记录（截至 2026-09-14 该文件仍止于 v8，v9 未追加）。
