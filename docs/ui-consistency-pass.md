# UI 一致性与简洁化 · 规范 + 现状清单

日期：2026-09-11　范围：`src/MechrevoLiteWin/`（WinForms 全部界面）
设计锚：[`DESIGN.md`](../DESIGN.md)（本文只做**补充**，冲突以 DESIGN.md 为准）
依据：`ui-ux-pro-max` skill（Style: Minimalism & Swiss Style；UX: Focus States / Color Contrast /
Color Only / Form Labels / Keyboard Navigation）+ 本仓库实测数据

---

## 0. 事实基线（先量后改）

| 项目 | 实测值 | 来源 |
|---|---|---|
| 构建 | 0 警告 / 0 错误 | `dotnet build -c Debug -p:Platform=x64` |
| 全量测试 | **1303 通过 / 0 失败 / 1 跳过** | `dotnet test`（2m22s） |
| UI 审计 | **0 条**（exit 0） | `--ui-audit artifacts/ui-audit-before`（331 张截图） |
| 现有截图 | 5 视口 × 夜/日 × 3 页 + 9 个弹窗 | 同上 |

> ⚠️ **审计 0 条 ≠ 构图合格。** 审计只查「文字截断 / 内容越界」两类几何缺陷，
> 不查**对齐、分组、层级、一致性**。本次发现的问题全部是后者，所以审计绿灯下依然存在。

> ⚠️ **审计的日间覆盖只有 1 页。** `UiAuditRunner` 主循环固定 `SetAuditNightMode(true)`，
> 只在「系统」页额外渲染一次日间（`*-Day-System`）。也就是说 常用/设备 两页与全部对话框
> **没有日间截图证据**。A 批因此改用「像素采样 + 对比度计算」验证日间，不依赖截图。
> 建议后续把日间覆盖扩到全部页面与对话框（约 +600 张截图，跑一轮约 +1 分钟）。

---

## 1. 现状清单

### 1.1 结构：3 页 11 分区

| 页 | 分区（卡片） | 备注 |
|---|---|---|
| 常用 | 性能模式 / 显卡模式 ‖ 电池充电限制 / 屏幕亮度（含局部调光·响应加速·屏幕校色） | 两列仅出现在 GPU+电池 一行 |
| 设备 | **快捷开关** / 液冷系统 ‖ 刷新率 | 快捷开关独占一行 |
| 系统 | 主题模式 / 官方控制台 / 版本 + 底部栏 | 全部单列 |

分区顺序硬编码在 `Settings.cs:2763-2779`（`ArrangeDashboard`），启用与否由
`RefreshDeviceCapabilities`（`:2618`）按能力位决定。

### 1.2 字号（28 处裸值 / 8 种）

| 字号 | 8 | 8.5 | 9 | 10 | 11 | 12 | 16 | 36 |
|---|---|---|---|---|---|---|---|---|
| 处数 | 5 | 4 | 12 | 1 | 1 | 3 | 1 | 1 |

按文件：`Settings.cs` 12 处（9×9、8.5×3）、`Settings.Designer.cs` 3、`ColorCalibrationForm.cs` 2、
`CustomModeForm.cs` 2、`FanCurveForm.cs` 2、`DonateForm.cs` 2、`Updates/UpdateForm.cs` 2、
`ToastForm.cs`(36，Toast 大字)、`RColorPicker.cs`、`RForm.cs` 各 1。

已用刻度 token 的调用点：**20 处**（`Settings.Designer.cs` 13、`FirstRunGuideForm.cs` 5、
`CustomModeForm.cs` 2）——即上一轮被中断的迁移。
**另有一档 7.125 已在上轮归一到 Caption**（当前无残留）。

### 1.3 颜色（211 处 `FromArgb` + 116 处命名色）

**饰面债**（该走 token 却写死）集中在 7 个文件，共 **176 处**：

| 文件 | FromArgb | 命名色 | 主要旧字面量 |
|---|---|---|---|
| `Settings.cs` | 42 | 19（White×16） | `30,30,30`×7、`0,200,80`×6、`45,45,45`×5 |
| `CustomModeForm.cs` | 23 | 8（White×8） | `0,200,80`×6、`220,80,80`×5、`70,70,70`×4 |
| `Settings.Designer.cs` | 1 | 29（Transparent×28） | `Color.Transparent` |
| `ColorCalibrationForm.cs` | 16 | 3 | `30,30,30`×3、`0,200,80`×3、`220,80,80`×3 |
| `FanCurveForm.cs` | 14 | 5 | `30,30,30`×5 |
| `Updates/UpdateForm.cs` | 11 | 2 | `220,80,80`×5、`220,180,0`×3 |
| `DonateForm.cs` | 1 | 2 | `160,160,160` |

**例外（不改，属数据或动态混色）**：`UiVisualStyle.cs`(31+1，调色板定义本体)、
`KeyboardRgb.cs`/`LightForm.cs`/`RgbForm.cs`/`WaterCoolerBle.cs`(设备色彩)、
`RColorPicker`/`RButton`/`RComboBox`/`RSlider`/`Slider`/`RForm`/`ControlHelper`/`ColorUtilities`/
`RTextBox`/`CustomContextMenu`(hover/alpha 动态计算)、`HardwareOverlay`(GDI 着色)、
`MechrevoService`(灯效默认色 `0,255,255`)。

**旧字面量 → 现 token 映射**（本轮替换依据）：

| 旧值 | 处数 | → token | 现色值（夜/日） |
|---|---|---|---|
| `(0,200,80)` | 18 | `Ok` | `#35C46B` |
| `(220,80,80)` | 13 | `Danger` | `#E5544B` |
| `(255,120,120)` | 4 | `Danger`（亮版，hover 用，保留） | — |
| `(220,180,0)` | 13 | `Warn` | `#E8B339` |
| `(30,30,30)` | 16 | `Window` / `Surface`（按层级） | `#0B1220`/`#111A2B` |
| `(45,45,45)` | 7 | `SurfaceRaised` | `#16223A` |
| `(70,70,70)` | 12 | `Border` | `#24344F` |
| `(160,160,160)`/`(140,140,140)` | 14 | `Muted` | `#8FA3BF` |
| `Color.White` | 64 | `Text` | `#E4ECF7` |
| `Color.Transparent` | 28 | 非缺陷（全是 `RButton.BorderColor`，见 P7），保留 | — |

### 1.4 间距与几何

| 项目 | 调用数 | **不同取值数** |
|---|---|---|
| `new Padding(a,b,c,d)` | 115 | **60** |
| `new Point(x,y)` | 84 | **56** |
| `new Size(w,h)` | — | **78** |

高频 Padding：`8/4/8/4`×8、`4/0/4/0`×7、`8/0/8/0`×7、`0/0/0/8`×6、`20/16/20/16`×5、
`0/3/0/3`×4、`4/5/4/5`×3 —— 4 的倍数与 3/5 混用，没有统一节奏。

### 1.5 构图问题（截图证据，审计查不出）

| # | 问题 | 证据 |
|---|---|---|
| P1 | **快捷开关网格参差**：27 项平铺在一个 FlowLayout 里（各项宽度随标签长短参差），末行只剩 2 项、右侧大片空洞；行内还夹了一个下拉框（电源灯）打断列对齐 | `Settings-Devices-1280x720-100pct-top.png` |
| P2 | **5 个分区静默丢失卡片外观**：`Settings.cs:83` 的 `if (section is BufferedPanel card)` 只给 `BufferedPanel` 设 `CardStyle`，以下分区是普通 `Panel` → 无 1px 描边、无 8px 圆角：**刷新率**(`596`)、**液冷系统**(`868`)、**官方控制台**(`2083`)、**主题模式**(`2496`)、**版本**(Designer)。底色因 `ApplySection` 仍是 Surface，所以肉眼只看到"少了一圈边"。**像素实测**：常用页卡片顶边是 `Border #24344F`，设备页快捷卡顶边直接 `Window → Surface` 无边线（B 批已修好快捷开关这处）。`panelFooter` 不在此列——DESIGN.md §4 给底部栏单独定义了样式 | `Settings.cs:83`、`596`、`868`、`2083`、`2496`；Designer `panelVersion` |
| P3 | **卡片标题两套格式**：4 张卡有 32px 位图图标（icons8 PNG，`picturePerf/GPU/Screen/Battery`），快捷开关·液冷·刷新率·主题·官方控制台·版本 没有 | `Settings.Designer.cs:211/453/709/916` |
| P4 | **数值读数两套**：电池限制的 `96%` 有 Input 底框，屏幕亮度的 `100%` 是裸文本 | `...Common-...png` |
| P5 | **滑条宽度不一致**：亮度滑条定宽（约 330px），电池滑条撑满卡片 | 同上 |
| P6 | **液冷卡三控件列宽不一致**：泵速档位下拉 ≠ 风扇档位下拉 ≠ 水冷灯光按钮，无网格 | `Settings-Devices-...-middle.png` |
| P7 | ~~`Color.Transparent` 28 处~~ **已核实为非缺陷**：这 28 处全是 `RButton.BorderColor = Color.Transparent`，语义是"不画描边"（RButton 的 BorderColor 由外部驱动），不是 `BackColor` 透明。真正危险的是 `BackColor = Color.Transparent`——`Settings.cs` 液冷状态按钮上有 1 处，已在 A2 批改为父卡片同色 `Surface` | `Settings.Designer.cs`（保留）、`Settings.cs:793`（已修） |
| P8 | **「开机自启」还是原生复选框**：`checkStartup = new CheckBox()`，与全应用 13 处 `RCheckBox` 开关外观不一致——日间截图里它是一个小方框，旁边全是拨动开关；而且它所在的分区 `panelVersion` 也是普通 `Panel`（无卡片描边） | `Settings.Designer.cs:48/101`、`Settings-Day-System-*.png` |
| P9 | **原生控件残留清单**（待 C 批逐个定性）：`CheckBox` 2 处、`Button` 15 处、`ComboBox` 15 处、`NumericUpDown` 1 处 vs 自绘 `RCheckBox` 8 / `RButton` 33 / `RSlider` 4 / `RComboBox` 1。**不能一律判为缺陷**——`ApplyTree` 已对原生控件做统一深色主题化（owner-draw + `SetWindowTheme`），ComboBox 属于此列；需要逐个核对"是否有意保留原生" | 全项目构造计数（含对象初始化器写法） |

---

## 2. 规范（normative，建议并入 DESIGN.md）

### 2.1 字号：只用 5 档，禁止裸值

```csharp
UiVisualStyle.TypeScale.Caption  = 8.5F   // 辅助文字、密集行、页脚
UiVisualStyle.TypeScale.Body     = 9F     // 正文与控件默认
UiVisualStyle.TypeScale.Subtitle = 9.5F   // 分组标签、次级标题
UiVisualStyle.TypeScale.Title    = 12F    // 卡片/窗口标题
UiVisualStyle.TypeScale.Display  = 15F    // 引导页等大标题
```
唯一例外：Toast 的 36pt 倒计时数字（`ToastForm.cs`，独立视觉层级，记为例外白名单）。

### 2.2 间距：只用 5 档

```csharp
Space.Xs = 4    Space.Sm = 8    Space.Md = 12    Space.Lg = 16    Space.Xl = 24
```

| 位置 | 取值 |
|---|---|
| 卡片内边距 | `Lg` 水平 / `Md` 垂直（紧凑卡可用 `Md`/`Sm`） |
| 卡片之间 | `Md`（纵向堆叠） |
| 卡片内行距 | `Xs` |
| 标题行与内容 | `Sm` |
| 分段控件段间距 | 0（无缝，靠 1px 分隔线） |

### 2.3 颜色：只认 token

- 饰面/文本/状态**一律** `UiVisualStyle.<Token>`，禁止 `Color.FromArgb` 与 `Color.White/Black/Gray`。
- **禁止 `Color.Transparent`**（父容器自绘时会画成黑）：要用"透出底"就写**父容器的实际颜色**。
- 例外白名单（可写死颜色）：设备色彩数据（键盘/灯条/液冷灯）、颜色选择器、hover/alpha 动态混色、Overlay GDI 着色、`UiVisualStyle.Palette` 本体。

### 2.4 卡片格式（统一）

```
┌ Surface 底 + 1px Border + 8px 圆角 ─────────────────────────┐
│ [图标] 标题（Subtitle 600）          右侧：状态点/读数/动作 │  ← 标题行 高 28
│ 内容行……                                                    │
└─────────────────────────────────────────────────────────────┘
```
- 图标**要么每张卡都有，要么都没有**；有则必须是**线性图标**（GDI+ 绘制或单色矢量），
  不用彩色位图（现 icons8 PNG 与 DESIGN.md「无 emoji 图标、统一线性」冲突 → 见 §3 决策）。

### 2.5 行格式

标签左对齐（`Body`）→ 弹性空白 → **数值右对齐（`Consolas` + `Muted`）** → 控件最右。
同一卡片内所有行共用一套列宽（`TableLayoutPanel` 百分比列），禁止各行自定义宽度。

### 2.6 快捷开关分组（25 项 → 4 组；铰链/同步灯带两项已随功能移除）

| 组 | 项 |
|---|---|
| 输入设备 | 触摸板、触摸板切换键、WiFi、蓝牙、摄像头、小键盘锁 |
| 键盘与热键 | Win键锁、Fn键锁、Copilot键锁、OSD提示 |
| 灯效 | 灯条开关、Logo灯带、电源灯、电池Logo灯 |
| 电源与系统 | USB充电、高性能电源、风扇增强、来电自启、CPU高级性能、游戏白名单、任务栏自动隐藏、透明效果、深色主题 |

每组一个小标题（`Caption` + `Muted`）或一条 `1px Border` 分隔线；组间距 `Sm`。

### 2.7 网格规则

- 列数按可用宽度取 **2 / 3 / 4**（与 `GetDashboardColumnCount` 同一套断点），**等宽列**。
- 末行不满时**左对齐、不拉伸**，不留"半格空洞"的观感。
- 网格内**不混入其他控件类型**（下拉框/按钮另起一行，或放到卡片标题行右侧）。

### 2.8 可访问性（skill 优先级 1-2 的硬项）

| 规则 | 现状 | 动作 |
|---|---|---|
| 焦点可见（Focus States，High） | 自绘控件未验证 | 键盘 Tab 走查，RButton/RCheckBox/RSlider 补可见焦点环 |
| 文本对比度 ≥4.5:1（High） | ✅ **已完成**：`PaletteContrastTests`（夜/日 × Text·Muted·Danger·Ok·Warn × Window·Surface·SurfaceRaised·Input），并据此修掉 3 个不达标 token | — |
| 不只用颜色表意（High） | 状态点仅颜色 | 状态点旁补文字（已有部分） |
| 控件有可访问名（High） | 部分缺 `AccessibleName` | 补齐（图标按钮优先） |
| Tab 顺序 = 视觉顺序（High） | 未验证 | 走查修正 |

---

## 3. 改动清单（4 批，可独立验收）

| 批次 | 内容 | 影响文件 | 风险 | 验收 |
|---|---|---|---|---|
| **A 样式归一** | 28 处裸字号 → token；176 处硬编码色 → token；Padding/Point/Size 收敛到 `Space` | 7 个 UI 文件 | 低（纯样式，行为不变） | 构建 0/0 + 1303 测试 + 审计 0 条 + 截图逐页比对 |
| **B 快捷开关重建** | 弃 `BuildQuickSwitchPanel` 的绝对坐标/硬编码底，改为 token 化卡片 + 4 组分组 + 自适应列网格 | `Settings.cs` | 中（控件树重建，焦点/回滚逻辑需保留） | 回归：`ApplyQuickSwitchAsync` 焦点还原、回滚按真实状态、可见性按能力 |
| **C 一致性补齐** | 卡片标题格式（含 P2 的 4 个 `Panel`→`BufferedPanel` 卡片化）、数值读数框、滑条宽度、液冷行网格 | `Settings.cs` + `Settings.Designer.cs` | 中（几何改动，多 DPI 易回归） | 5 视口截图 + 审计 0 条 |
| **D 可访问性** | 焦点环、AccessibleName、Tab 走查（对比度断言已在 A 批提前完成） | `UI/*` + `tests/` | 低 | 新增对比度测试 + Tab 走查记录 |

**护栏（建议与 A 同批落地）**：加一条**源码扫描测试**，一旦在 UI 文件里新增
`UiVisualStyle.Font(<数字>)`、`Color.FromArgb`、`Color.Transparent` 即失败——
把规范变成可执行约束，否则半年后必然回退。

---

### 3.1 批次 A 执行记录

| 子批 | 内容 | 结果 | 验证 |
|---|---|---|---|
| **A1 字号**（已完成） | 28 处裸字号 → `TypeScale` | 35 处替换（含 `ApplyTitle` 5 处显式尺寸 + `UiVisualStyle` 内部 3 处）；**裸字号 28 → 0**（Toast 36f 例外，已就地标注） | 构建 0/0；1303 测试；**审计 1 条 → 已修，见下** |
| **A2 颜色**（已完成） | 176 处饰面硬编码 → token | **145 处替换**；剩 31 处全在例外白名单：28 处 `RButton.BorderColor = Color.Transparent`（"无描边"语义）、2 处灯效默认色、1 处 alpha 混色 | 构建 0/0；截图逐页比对 |
| **A3 间距** | Padding / Point / Size 收敛到 `Space` | **未做** —— 与几何改动同源，合并进 C 批，避免同一批文件被改两遍 | — |

A 批引入、随后在 B 批修掉的一处几何缺陷（**流程教训**）：

`--ui-audit` 的缺陷数**不在退出码里**，而在产物 `ui-audit.md` 的 `Geometry issues` 行。A 批我三次
用 `... | tail` 之后读 `$?` 判断，读到的是 `tail` 的退出码 0，于是把「1 条溢出」误报成「0 条」。
真实情况：A1 把 FanCurve 底部说明文字的字号从 8pt 提到 `Caption(8.5)` 后，200% 缩放下该行折成两行，
而容器是固定高 48 → 第二行被裁（`[parent-overflow] FanCurve/1280x720-200pct`）。
修法：底部条改 `AutoSize`（`MinimumSize` 保住原视觉高度），标题行从「会换行的 FlowLayout + 固定 44 高」
改成不换行的 `TableLayoutPanel`。**验证方式固定为读 `ui-audit.md`，不看退出码。**

A 批附带修掉的**可读性缺陷**（A2 把对话框从"永远深色"改成跟随主题后才暴露，已加测试钉住）：

| 色板 | 原值 | 对比度（最差底面） | 现值 |
|---|---|---|---|
| 日间 `Ok` | `#1E9E54` | **2.99:1** 于 SurfaceRaised | `#16783F`（4.78:1） |
| 日间 `Warn` | `#B77E14` | **3.02:1** 于 SurfaceRaised | `#8B5F0F`（4.85:1） |
| 夜间 `Danger` | `#E5544B` | **4.31:1** 于 SurfaceRaised | `#F0584E`（4.69:1） |

即"状态文字在日间几乎读不出来"——DESIGN.md §9 验收条件 3 本来要求 ≥4.5:1，旧值从未被验证过。
新增 `PaletteContrastTests` 覆盖 夜/日 × 5 前景 × 4 底面，两个方向都做过反证（改回旧值即红）。

A 批附带修掉的两处：
1. `Settings.cs` 液冷状态按钮 `BackColor = Color.Transparent` → `Surface`（幽灵按钮在自绘卡片上会画成黑条，日间模式尤甚）；
2. `Display/ScreenNative.cs` 里 1 个非 UTF-8 字节（CP1252 破折号）→ UTF-8，此前该文件对编译器是"非法编码"。

### 3.2 批次 B 执行记录（快捷开关重建）

替换掉的原实现：`new Panel`（普通 Panel 拿不到 `CardStyle`）+ 固定 `Height=180` + 绝对坐标
（`Location(10,36)`、`Size(390,136)`）+ 一个平铺 27 项的 `FlowLayoutPanel` + 手工重算高度的
`FitQuickPanel()` 补丁。

新实现：

| 项 | 做法 |
|---|---|
| 卡片 | `BufferedPanel { CardStyle = true, AutoSize = GrowAndShrink }` —— 与其它分区同源（Surface + 1px Border + 8px 圆角） |
| 结构 | `TableLayoutPanel`（单列）纵向堆：标题 → 组标题 → 组网格 ×4 |
| 分组 | 输入设备 6 / 键盘与热键 4 / 灯效 4 / 电源与系统 11（`QuickSwitchGroups` 静态表，漏键直接抛，不静默丢项） |
| 列对齐 | ~~所有开关定宽~~ **返工：维持 `AutoSize`（自然宽度）** —— 见下方「返工记录」 |
| 换行 | 仍交给 `FlowLayoutPanel`：窄窗口/高 DPI 下只是每行少放一项，不会裁剪（保留原设计的抗裁剪性质） |
| 空组 | 整组项都被机型能力挡住时，连组标题一起收掉（判据用 `visibleByKey`，不读 `child.Visible`——那个 getter 会把父容器隐藏也算进去） |
| 高度 | ~~由 AutoSize 承担~~ **返工：保留 `FitQuickGroups()`**（每组网格喂宽度后算高度，见返工记录） |

**返工记录（两次，都是先被截图/日志证伪才改对）**：

1. **"AutoSize 能自己搞定高度和换行"——错。** 会换行的 `FlowLayoutPanel` 一旦 `AutoSize`，
   它按"不换行"报首选宽度 → 每行只放得下一项。真机截图抓到的就是整列竖排。
   修法：沿用原实现的做法，显式喂宽度（`grid.Width = panel.ClientSize.Width - Padding.Horizontal`）
   再按该宽度算高度；卡片高度跟着 root 底部走。
2. **"把开关定宽就能列对齐"——错。** 按最宽标签 + 52 逻辑像素算出的项宽，在审计缩放下
   实测 250–286 设备像素，**超过半张卡片**（网格 562），于是每行又只剩一项。
   根因是宽度要自己算 DPI 缩放，而"测量文字的 DPI"与"容器尺寸的缩放语义"不是同一套。
   修法：放弃定宽，维持 `AutoSize`。列对齐是次要收益，**换行不裁剪 + 分组清晰才是主要目标**。

教训：**FlowLayoutPanel 的换行宽度是显式输入，不是派生量。** 谁能给它宽度，谁就必须负责喂。
| 电源灯 | 开关 + 亮度下拉仍是同一个 FlowLayoutPanel 项（高 DPI 换行会把标签和下拉拆开），但排到「灯效」组最后，不再夹在网格中间 |

### 3.3 紧凑化（用户 2026-09-11 报「页面不够紧凑、占了很多无用空间」）

**先量后改**（像素列扫描，`artifacts/_band_scan.py`）：

| 页面 | 改动前 | 改动后 |
|---|---|---|
| 常用 | 内容结束于 y=458，底部 **63px 空白** | 填满，无滚动 |
| 设备 | 可滚动 | 可滚动 |
| 系统 | **584px 里 280px 空白**（约一半） | 534px 里仍有约 **235px 空白** |
| 默认窗口高 | 520 逻辑 | **470 逻辑** |

**改动清单**（三处有真实富余的）：刷新率卡 `96 → 80`、官方控制台卡 `56 → 46`、默认窗口高 `520 → 470`。

**失败并回退的尝试（重要教训）**：一度把分段行高、标题行高、常用页三张卡高都改成 `D(…)` 缩放常量，
以为是在"收紧"。实测 **248 条 text-clipping + 288 条 parent-overflow**。根因：

> **这个文件的几何是混合体**——卡片高度按 `D()`（DPI 缩放），而分段行高 / 标题行高是**未缩放字面量**
> （`tablePerf` 42、`brightLayout` 38、`panelCPUTitle` 20…）。把行高改成 `D(42)` 后在 175% 下
> 变成 73 设备像素，比原来大 74%，直接把卡内内容挤爆。

所以"卡片比内容胖一圈"的观感，本质是**两条缩放规则不一致**，不是卡片真的有余量。
要统一就得动整个这个文件的几何体系（改行高为缩放值 → 卡片同步放大），属于另一个量级的改动，
且在高 DPI 下视觉会**变大**而非变小。**结论：紧凑化只动真正有余量的卡片。**

**遗留的结构性问题**：系统页三张小卡装不满一页（约 235px 空白）。这不是"卡片太胖"，
**只能靠信息架构解决**：把某张卡从常用/设备移到系统，或合并成两页。属产品决策，待用户拍板。

## 4. 决策记录

| 议题 | 决定（2026-09-11） |
|---|---|
| 卡片标题图标 | **全部改为 GDI+ 线性图标**：每张卡都有，单色线条跟随 `Text`/`Muted`，弃用 icons8 彩色位图（与 DESIGN.md「无 emoji 图标、统一线性」对齐）→ 归入 C 批 |
| 快捷开关分组 | **4 组**：输入设备 / 键盘与热键 / 灯效 / 电源与系统（§2.6）→ 归入 B 批 |
| 起步批次 | **A 批先行**（已完成，§3.1），随后 B → C → D |
## 5. v2 预览 1:1 复刻（2026-09-12，进行中）
- 结构已落地：BuildHeadRow 行头 / 遥测行 / 屏幕行(Hz 分段+亮度) / 灯光组每灯一行 / SettingsDialog.cs ⚙ 弹窗 / footer 图标行 / 折叠默认态按预览。
- 验证：构建 0/0；测试 1313 通过/0 失败；审计 p2-r29 全绿（0 条几何问题，682 张截图）。
- 剩余偏差（下一轮）：文字/按钮比例与预览的 px 差异（对数字号、间距、行高需再一轮校准），对齐 material 比较 —— 下一轮直接对照 inconsistency.png 修。

## v2 复刻收口补充（2026-09-12）

### 审计 r31-r35：灯光组两个缺陷修复

1. **RCollapseGroup 内容压标题**（WinForms Dock 语义）：Dock=Top 按 z 序**倒序**布局——最后 Controls.Add 的控件排在最顶。旧序 header→content→divider 实际渲染为 divider(顶)→content→header(底)，展开态标题跑到内容下方（r29/r30 审计截图实证）。PowerShell Add-Type 实验复现（最后加的 y=0）。修复：添加顺序改为 content→header→divider（RCollapseGroup.cs）。锁测 CollapseGroup_HeaderRendersAboveItsContent。
2. **灯光组整组消失**：RefreshDeviceCapabilities 先 _enabledDashboardSections.Clear() 再逐分区 EnableSection，灯光组的调用点在 Clear **之前**，set 成员被清掉 → ArrangeDashboard 不把它装进页面栈。修复：Clear 之后补发 EnableSection(_lightGroup, audit || anyLighting)（Settings.cs）。锁测 LightingCollapseGroup_IsArrangedIntoTheDashboardStack。
3. **lightRows 行溢出 45 条 parent-overflow**：body TLP 无显式 ColumnStyle，隐式单列按子行首选宽布局（窄视口 345>346 client）。修复：显式 Percent 100 列 + 行内 Dock=Fill Label 关 AutoSize（Settings.V2.cs）。

证据：rtifacts/ui-audit-p2-r35（**0 几何问题 / 682 截图**，灯光组首次进入审计树），全量测试 **1315 通过 / 0 失败 / 1 跳过**。最终对比图 rtifacts/ui-compare/inconsistency-175.png（r35 vs 预览，同标度）。

已知fixture差异（非 bug）：审计工厂 Settings 无 fake 硬件 → 遥测 CPU —/GPU —、Hz 分段行与灯光效果列为占位（真机有数据时渲染）。


## v3 校准批（2026-09-12，真机 1:1 对表）

以预览 `design-previews/2026-09-11-lmechrevo-ia-v2/index.html` 为唯一标尺，按真机截图逐项对表修正：

### 结构/几何（对齐预览 CSS 数值）
- 分段控件皮肤重做（`RButton`）：轨道 = Input 底 + 1px 描边；选中 = 内缩 Accent 圆角块（不再整格实底）；
  分隔线上下内缩 5 逻辑 px 且与选中段相邻时隐藏；中段补顶/底描边（外框跨段连续）；轨道圆角 8。
- 行高：性能/显卡/屏幕分段行 42/48/40 → 统一 34；行头 30 → 26；亮度/电池行 28；灯光行 30 → 32；
  页脚 72/54 → 51（预览 660 高窗口的节奏；真机不再出现纵向滚动条）。
- 行头右列文案：性能行 = 当前模式名（预览「自定义」位置）；显卡行 = 「重启生效」常驻提示。
- 遥测行：改单行「CPU 50°C 2022rpm 35% · GPU 33°C 1877rpm 35%」，温度亮色、其余 Muted
  （拆多段 Label 实现同句双色）；有功耗数据时按「温度 W 转速 占空比」插入。
- 电池行：补「限充」左标签 + 读数改 Consolas/Muted 右对齐（同预览 .sliderline）。
- 灯光行：效果读数换 `RComboDisplay`（Input 底 + 边框 + 圆角 + 右端 ▾ 的只读下拉形态）；
  四列全部百分比（绝对列在审计缩放里被算小是既有教训）。
- 折叠组：组头补线性图标列（预览有）；分隔线改为仅液冷组（预览只在电池下方有一条）。
- 页脚：五键改「幽灵键」（`Tag=footer-ghost` 退出 ApplyTree 的 Secondary 皮肤，去掉方块底/描边）；
  图标按预览逐一重绘：⊙悬浮窗 ♥赞助 ✕退出 ⚙设置（新增 4 个 UiGlyph 图形）；悬浮窗激活态改为强调色图标/文字
  （不再画蓝色描边圈）。
- 摘要文案补齐：灯光「N 通道 · M 开启」；更多开关「N/M 开启」；液冷连通时带「· 泵自动 · 风扇自动」
  （GCU 落 AppConfig、BLE 直连读 WaterCoolerBle 记忆档，任一为自动即显示）。

### 证据
- 构建 0 警告 / 0 错误；测试 **1315 通过 / 0 失败 / 1 跳过**。
- UI 审计 v6：**0 条几何问题 / 634 张截图**（16 视口矩阵；v4 曾出现 723 条被逐类修回：
  遥测空段 zero-size、灯光编辑键/限充标签 text-clipping、200pct 下性能/显卡/屏幕面板 parent-overflow）。
- 真机截图 `artifacts/app-real-v4.png`（175% DPI 感知抓取）；同标度对比图
  `artifacts/ui-compare/v3-real-vs-preview.png`（左 = 预览，右 = 真机），逐区形态一致。

### 已知数据性差异（非缺陷，随真机数据变化）
- 刷新率只有 240/60（面板实际支持集）；效果名/开关状态/循环次数等均来自真机。
- 遥测功耗位在 LHM 未上报时自动省略。
- 液冷「泵自动/风扇自动」后缀取决于当前档位是否为自动。

### 待用户拍板的两个「文案级」差异
1. 窗口标题栏：预览为自绘标题栏（■L-Mechrevo + ─ □ ✕），真机仍是系统原生标题栏。
2. 性能分段 5 键（静音模式/平衡模式/静音狂暴/狂暴/自定义，含真机子模式）vs 预览 4 键
   （静音/办公/狂暴/自定义）；显卡分段次序（集显/标准/直连）vs 预览（直连/混合/集显）。
   改文案/次序不损失功能，但会与官方叫法及其它入口文案不一致，等拍板。


## v4 真机交互批（2026-09-12，用户四项反馈）

| 反馈 | 修复 |
|---|---|
| 展开/收起时窗口朝下长、收起后下方露出桌面 | `UpdateDashboardWindowHeight` 在高度变化后按记录的工作区重贴右下角（底边钉死、增长向上展开）；重贴只在窗口正式呈现过之后生效——构造期 `Screen.FromControl` 会落到多显示器里 OS 给的默认落点（三屏真机实证，窗口曾被挪去副屏） |
| 右侧滚动条是系统白条 | 新增 `RScrollBar` 自绘滚动条（8 逻辑宽、透明轨道、Border 色圆角滑块）；`dashboardPageHost` 关闭 AutoScroll 改为偏移驱动（锚定栈 + `Value` 应用为 `Top=-value`）；滚轮经 `DashboardWheelFilter`（IMessageFilter）转发，指针下的下拉/滑条不拦截；组件尺寸变化即重算范围，无溢出自动隐藏 |
| 风扇停转时遥测行向前压缩、不占位 | 功耗/转速段用 `MinimumSize` 固定占位槽（84 逻辑宽）——数据消失留空、不再向前塌；段尺寸变化触发重排（审计缩放后字体度量变化也能对齐） |
| 液冷灯光键太大 | 液冷组重排为紧凑三行（32 逻辑行高）：固定窄列小键（132 宽 × 26 高）+ 单行省略号状态（完整文本仍走 tooltip、点击重试保留）；面板高 156 → 112 |

附带修复：
- footer 悬浮窗键曾被设置弹窗「吸走」：`SettingsDialog` 会把 footer 的按钮整个 reparent 进弹窗，弹窗建过一次后 footer 永久少一键（真机截图实证）。改为弹窗内独立 `RCheckBox`（打开时与 footer 键状态同步）。
- 审计豁免补丁：`dashboardPageHost` 改自绘滚动后 `AutoScroll=false`，它的内容包含性检查加名称豁免（等价于原有 AutoScroll 宿主豁免，滚动视口的内容偏移是设计行为）。

证据：构建 0 警告 / 0 错误；测试 **1315 通过 / 0 失败 / 1 跳过**；UI 审计 v10 **0 条几何问题 / 430 张截图**（张数下降是内容变矮后 top/middle/bottom 切图减少，覆盖不变）；真机实测：展开 1186↔1474、底边恒定 1495（工作区底 - 12 逻辑边距）、滚动条随内容出现/隐藏、滚轮滚动 value 0→70、收起后偏移自动回夹。截图 `artifacts/app-real-v6.png`。


### v4 追加：遥测行居中对齐（同日真机反馈）

用户反馈「转速那一栏并不居中」——根因：转速占位槽（84 逻辑）内的留白默认堆在文本右端，
把 `·` 向左推、整行失衡。修法：
1. 转速段**右对齐**（TextAlign=MiddleRight，槽内留白落到文本左侧，文本右端与 `·` 的间距恒定）；
2. 整行**水平居中**（按首选宽汇总后居中放置）；
3. 槽宽继续走 `MinimumSize`（随 DPI/审计缩放同源缩放），不用 `D()`/TextRenderer 自测宽——
   审计视口会把自测宽定格在宿主标尺，产生 parent-overflow（v11/v12 审计 99/24 条后回退验证）。

证据：审计 v13 **0 条 / 430 张**；测试 1315 通过；真机截图 `artifacts/app-real-v7.png`（`·` 居中、
两侧对称）。


### v4 追加 2：footer 键位对齐（同日真机反馈）

用户反馈 footer「没对齐」。像素测量（measure-footer2.py 扫描五个键的图标/文字行带）：
- 「设置」键整键比其他四个高 4 物理 px——它是唯一没设 Anchor=None 的按钮（缺省 Top|Left 贴单元格顶，
  其余四个 Anchor=None 垂直居中）。修复：补 Anchor=None。
- 「✕退出」图标只有邻居一半大（glyph 0.24..0.76w vs 邻居约 0.85w），看着像没对齐。
  修复：扩到 0.18..0.82w。

复测：五个图标顶沿全部 1100（±1）、文字行带 1125（±2）；审计 v14 0 条 / 430 张；测试 1315 通过；
真机截图 rtifacts/app-real-v8.png。


## v5 真机五连修（2026-09-12，用户反馈 5 项）

| 反馈 | 修复 | 证据 |
|---|---|---|
| footer「设置」字色与其余四键不同 | `settingsButton` 的 `ForeColor` 由 `Muted` 改为 `UiStyleXXX()`（Text）——它是唯一没走 `ResetLegacyFooterButton` 的键 | 五键文字核心像素全部 RGB(228,236,247) |
| 遥测状态行仍偏左 | 回退到审计全绿的手动布局，仅修正居中公式：WinForms 显式 `Location` 相对客户区原点、**不受父容器 Padding 影响**，去掉原 `- Padding.Left` 的误减项 | 行中心 378.5 vs 窗中心 379.5 → **−1px** |
| 亮度/限充两条滑条风格不一 | `sliderBattery` 由旧 `Slider`（粗条/实心大钮/刻度）换成 `RSlider`（与亮度同款：4 逻辑轨道 + 白芯蓝环旋钮）；`Step`/`supportedValues` 移除（连续 40..100，键盘/滚轮步长 1%） | 两条滑条像素几何一致（细轨 + 同径圆钮） |
| 「更多开关」里残留灯效分类与开关（与灯光组重复） | 整组移除：分组表行 + 4 个开关项 + 对应构建分支/静态能力位/可见性/字段/回显定时器，共 15 处切除；<br>硬件/服务/灯光组/恢复链路全部保留 | 摘要 25→21（真机「11/21 开启」）、展开面板无灯效组；1308 测试全绿 |
| 窗口高度是外部增长/展开收起时内容先出再整体上移 | 改为**固定窗高** `min(660 逻辑, 工作区上限)`，`UpdateDashboardWindowHeight` 不再读内容高（签名不变即不重设尺寸），内容超出由内嵌自绘滚动条滚动 | 收起灯光/展开灯光/展开更多开关全过程窗口矩形恒为 759×1219（客户区 660 逻辑）；展开态出现深色滚动条且内容内滚 |

过程中被审计抓出并修掉的问题（均在本批内闭环）：遥测 flow 方案在窄视口超宽（240 条）→ 回退手动布局；LC 面板内层可用高比三行少 2px（96 条）→ LC 面板垂直内边距 6→4；测试环境混标尺导致灯光键断言不稳定 → 改为与同行状态键比较。

验证：构建 0/0；测试 **1308 通过 / 0 失败 / 1 跳过**；UI 审计 **0 条 / 430 张 × 16 视口**；真机截图 `artifacts/fix-final2.png`（收起态）、`artifacts/fix-expanded4.png`（展开态含滚动条）；独立 Oracle 审查：**无阻断项**（备注：S2 注释已按审查更正；`ReflowLightingActions` 等为更早遗留死代码，未动）。

已知取舍（用户明确要求/先前已定）：电源灯亮度与电池 Logo 灯的快捷入口随该组移除而无 UI 入口（服务/硬件/FunctionVerifier 覆盖仍在）；电池滑条不再吸附 60/80/100 三个刻度点。


## v6 真机四组修复（2026-09-12，用户反馈 4 张图）

| 用户反馈 | 修复 | 证据 |
|---|---|---|
| ① 打开时窗口太高，要「如图一样的高度」 | 固定客户区高 660 → **529 逻辑**（175% = 926px；`CompactDashboardLogicalClientSize=(420,529)`，clamp 改读同一常量，去掉第二处硬编码 660） | 真机 RECT **759x990**（客户区 926px = 529.14 逻辑，误差 0.1px），底边仍钉在 B=1495；折叠态无滚动条、展开态内嵌滚动（符合上批设计） |
| ② 液冷行：泵速/风扇下拉有「不生效的文字档位」；灯光键过大、右侧连接状态文字溢出 | 泵速/风扇项移除占位项（`ProfileUnset`）并改为纯百分比：泵速 `自动/45%/60%/90%`、风扇 `自动/40%/50%/60%/90%`；灯光键列 132 → **D(84)**，键加 `AutoEllipsis`；跨列 span 保留 | 真机展开态：灯光键 ≈147px = D(84) ≤ D(96)=168，绿色状态文字右端 669 < 面板内容右界 705（不再溢出）；泵/风扇无「档位」文案 |
| ③ 灯光行：开关与文字不在同一行；下拉栏没有真实灯效；编辑要能调细节（P2 窗口） | 开关 `Anchor = Left`（TLP 垂直居中）；只读 `RComboDisplay` → 真实 `ComboBox`，按区填入权威灯效表（键盘 10 / 灯条 5 / Logo 3）并预选已保存项，选中即下发；`编辑` 继续打开既有 P2 编辑器 LightForm/RgbForm | 像素：三行 文字/开关/下拉/编辑 同行中线（≤0.5px）；真机下拉文本 = **流畅彩虹**（键盘）、**波浪**（灯条）；P2 编辑器审计截图（Lightbar 175%）= 模式/亮度/速度/颜色齐全、无裁切 |
| ④ 性能模式/显卡模式/屏幕/电池 标题+图标不对齐 | 四个行头统一走 `BuildHeadRow`（图标列 D(26)、Margin 0、标题 Percent 100、字号 ApplyTitle）。**根因**：`panelBrightness` 与屏幕行头建得早，被窗体 DPI 自动缩放再乘一次系数（内边距 28→49、图标列 46→80）→ 整卡右移 21px、图标再右移 16px；新增 `NormalizeScreenCardMetrics()`（`ApplyResponsiveBounds` 内、布局后按逻辑值重设，幂等） | 四行头首墨 x：修复前 84/**82**/**99**/81 → 修复后 **82/82/84/79**（屏幕系统偏移消除；残余 ≤3px 为图标字形内部差异）；屏幕卡内容回到 78（与性能/显卡一致） |

验证：构建 0 error（3 条既有警告：`D` 未使用 / 两个 CS0649 死字段）；测试 **1318 通过 / 0 失败 / 1 跳过 / 1319**；UI 审计 **0 条 / 430 张 × 16 视口**；真机证据 `artifacts/fix-v5-open.png`、`fix-v5-lc-visible.png`、`_crop-lightrows.png`、`ui-audit-final2/`。

已知取舍：`group_*_open` 仅持久化不回读（`RCollapseGroup` 构造只取 `defaultExpanded`）——启动默认态固定为 液冷收/灯光展/更多开关收，属既有行为，本批未改动。


### v6 补充（审查门闭环）
- 液冷状态行内文字统一改短：设备名/MAC（含 `DescribeGcuLiquidCoolingState` 的 GCU 回退分支）只进 tooltip，任何可达状态的可见文案 ≤19 字（列宽≈25 字），不再出现省略号截断；tooltip 保留完整信息。
- 四行头首墨残差更正：82 / 82 / 84 / 81-84（≤3px，属图标字形内差；四个图标盒位置按 `BuildHeadRow` 构造完全相同，此前“79”为单行采样+抗锯齿阈值伪差）。
- 独立审查（Oracle）两轮：第一轮 1 条判据阻断（S-B2 截断）+ 1 条证据冲突（S-D）；修复后第二轮结论 **无判据阻断，S-A/B1/B2/C1/C2/C3/D 全部 MET**。


## v7 真机四组修复（2026-09-13，用户反馈 2 图 + 2 现象）

| 用户反馈 | 修复 | 证据 |
|---|---|---|
| ①「这两个按钮还是稍微大了一些，而且没有设计圆角等，显得很素」 | 液冷灯光行的 `水冷灯光` 按钮与连接状态读数改为 `RButton` 迷你键：`BorderRadius=12`（=6 逻辑px，对齐设计稿 `.mini` 的 `border-radius:6px`）、`BackColor=Surface`、1px `Border` 描边、hover `SurfaceRaised`、`Margin=(0,4,0,4)`（行内高 24 逻辑px，原 28.6）。**额外必要改动**：`UiVisualStyle.ApplyTree` 新增 `mini-chip` 标签分支——构建后的主题二次刷色会把普通按钮统一刷成 SurfaceRaised，且 `ApplyRowSkins` 已把 lcPanel 去卡片化为 Window（"Surface 卡"前提过期），无该分支则测试契约无法成立且主题切换后会静默回退 | 真机 4x 放大目检：按钮与状态胶囊**均为圆角+描边**，无文字裁切/直角渗色/错位；`SettingsLayoutTests` 28/28 绿（含既有文字适配断言） |
| ②「键盘灯光开关不起作用（logo 和灯带正常）」 | 根因：开关**只发固件通道命令**，从不驱动应用内 HID 渲染器；且本机固件键盘通道**永不确认**（日志 `KB: … power=False`、`SetLightPower(Keyboard/Ctrl,True) not confirmed`）。修为双通道：OFF = `KbPowerOn=false` + 后台 `StopCurrentEffect` + 固件断电；ON = **先 `Connect()`**（同 RgbForm 可用路径）→ `ReInitCustomMode` + `StartMode`，HID 不可用才回落固件；回读改 `Program.rgb?.KbPowerOn ?? hw?.KeyboardPower` | 真机：ON 点击后 `RGB connect OK: VID=048D PID=600B` → **`RGB heartbeat 1801 帧/60s`**（~30fps 持续刷帧=键盘灯点亮）；OFF 点击 `rgb.cfg kbPower` 1→0 且恢复引擎遵从"保持关闭" |
| ③「先把下拉切为非上次选择的灯效，再打开开关 → 闪两下然后保持常亮」 | 根因：关态下切下拉仍下发 `SetEffectALL`（闪1）；开灯只发 `SetPower`，固件上电默认 Single/常亮（闪2）；补偿性的延时重下发只存在于 `Program.SetExternalLightingPowerAsync`，开关够不到。修为：关态切下拉**只持久化不下发**；抽出共享 `Program.ApplyLightChannelEffectAsync(topic, settings)`（无锁、带 shouldApply 重检），恢复路径与开关共用；开灯确认后**恰好一次**应用所选灯效 | 真机日志（复现 3 次）：开灯 = `SetLightPower(...) confirmed` **1 次** + `SetLightEffect(topic, <存储灯效>, save=False)` **恰好 1 次**，无重复无双闪；`LightRowPowerEffectTests` RED 3 失败 → GREEN（含"关态选 X 不下发"、"开灯恰好一次 X"、logo 同验，断言 `EffectAllCount==1`） |
| ④「把「外接屏断独显」「电池切独显」这两个功能取消掉」 | **全链切除**：`Settings.cs` 分组/条目/处理器分支 → `MechrevoService.SwitchDisconnectMonitor/SwitchDcOnce` → `MechrevoHw` 能力表/属性/两个 `OptionalBool` 解析块（保留 DC_HZ/currentHZ）→ `FunctionVerifier` 键与分派 → 相关测试与文档。**注意**：用户提到的 `gpu_auto_hot_switch`/`gpu_auto_force_dgpu_close` 早已是死键（源码零读写，仅 config 遗留），真正生效的是 `exmonitor`/`exbattery` | 更多开关摘要由 **`11/21 开启` → `11/19 开启`**（总数正好少 2）；新增负向测试断言两键不复活且 currentHZ=165 仍解析；src+tests 仅剩合规引用（移除说明注释 + "不会复活"断言） |

验证：构建 Debug/Release **0 error**（仅 3 条既有警告）；测试 **1324 通过 / 0 失败 / 1 跳过 / 1325**；UI 审计 **0 条 / 430 张 × 16 视口**（`artifacts/ui-audit-run2-final2`）。真机证据：`artifacts/run2-lc-rows-4x.png`（圆角目检）、`run2-light-collapsed.png` + `run2-quicksummary-4x.png`（摘要 11/19）、日志 `%APPDATA%\MechrevoLite\log.txt`（HID 心跳 / 单次 SetEffectALL）。

未在真机验证的变体：下拉"切到非默认灯效"由合成点击无法驱动（原生弹窗列表），该变体由 `LightRowPowerEffectTests` 覆盖；效应应用路径与灯效 id 无关（取存储值），风险低。

审查门备注已修：`mini-chip` 分支现同时重设 `RButton.BorderColor = Border`，使迷你键描边色跟随日夜主题切换（此前只在构建期赋值，切天后仍留夜/昼旧色）。修后 Debug/Release 0 error、测试 1324/0/1/1325、审计 430/0。


## v8 真机六项修复（2026-09-13，用户反馈 5 条 + 2 图）

| 用户反馈 | 修复 | 证据 |
|---|---|---|
| ① 关掉键盘灯后再在右边选灯效，仍会点亮键盘 | 键盘行下拉补上「关态只持久化」守卫（与灯带/Logo 同规则）：`!sw.Checked` 时只写 `KbHidMode`+`QueueSaveConfig()` 并 return，不置 `KbPowerOn`、不 `StartMode`、不下发固件 | `KeyboardToggleTests.KeyboardCombo_WhileChannelOff_PersistsWithoutPublishing` RED→GREEN（断言 KbPowerOn 保持 false + 无 Keyboard/Ctrl 下发） |
| ② 键盘开关的开启响应明显比灯带/Logo 慢 | 根因：HID 出帧被"固件电源旁路"挡住，而本机固件永不确认（预算 180+500+700+700 ≈ **2080ms**）。改为**出帧先行、旁路并发**（`RunHidWhileFirmwarePowerBypassCompletesAsync`），并加代际守卫（`_kbCmdGen`）与 OFF 有界等待（250ms） | 真机日志顺序：`RGB connect OK`(20.337) → `RGB heartbeat`(20.351) → `SetLightPower(...) not confirmed`(**22.434**，~2.1s 后才落地且不再阻塞)；测试 RED→GREEN |
| ③ 首次展开「更多开关」组间距异常，关掉再开才正常 | 根因：折叠期卡片被放进"从未可见"的容器（WinForms 跳过不可见 Dock=Top 子控件布局），窄宽算出的超高网格被冻结。修：`FitQuickGroups` 先 `root.PerformLayout()` 再取底边；并把拟合提升为 `_fitQuickCards` 闭包，在 `Toggled`（展开后）确定性重跑 | 真机同次启动内**首次展开与二次展开的波段签名完全一致**；机制级测试 RED→GREEN（窄宽冻结→展开纠正） |
| ④a 待机时蓝色高光跳到右侧/下一个开关 | `ApplyQuickSwitchAsync` 禁用前先把焦点停到父容器（行布局面板：可编程聚焦、不画焦点环、不进 Tab 序），确认后原样归还；Windows 三连开关同款 | 结构性前提测试 GREEN（停放点为 Panel 系且 `TabStop=false`）；真机待机截图未能取到（时序窗口 0.2-2s，已如实标注） |
| ④b 水冷灯光下拉很丑、分类要重做 | 新类 `UI/LiquidCoolingLightMenu`（`: CustomContextMenu`，DWM 圆角）+ `LiquidCoolingMenuRenderer`（6 逻辑px 圆角悬停、分组头部 Muted 不可点、1px 令牌分隔线、绘制时取色随主题）；条目按 **头部灯效 / 自定义颜色 / 风扇灯效（仅 Mk2）** 分组 + 关闭全部；`InitContextMenuTheme` 联动 `ApplyTheme()` | `LiquidCoolingLightMenuTests` 5/5 GREEN（渲染器类型/头部契约/顺序/高度[28,34]/主题令牌）；生产条目改走 `AddItem` 工厂（高度契约覆盖真实路径）；真机弹窗截图未取到（原生弹窗无法被合成点击稳定打开） |
| ⑤ footer「设置」只能打开一次 | 根因：非模态 `Close()` 会释放窗体及被过继的主界面面板，缓存字段未清空 → 再次构造用已释放控件抛异常被吞。修：照 RgbForm 加 `FormClosing` 取消用户关闭并 `Hide()` | 真机「设置」可见窗口计数 **0 → 1（首开）→ 0（关）→ 1（二开）→ 1（三开）**，无新 `ObjectDisposedException` |

过程中独立审查门抓出并修复的阻断：
1. **S4b 高度契约走的是死路径**：生产侧条目用裸 `ToolStripMenuItem`（高 22px），只有测试用的 `AddItem` 有 padding → 已改为生产侧也走 `AddItem` 工厂，并用 `FanLedItems` 取代孤儿局部列表。
2. **测试套件污染真实配置**：新增的 I3 测试用 `UiAuditMode=false` + `Toggle()` 触发 `AppConfig.Set` → 已给 `AppConfig` 加 `LMECHREVO_CONFIG_FILE` 环境旁路（测试用 `[ModuleInitializer]` 指向临时目录），并给 `SyncFallbackConfig()` 加旁路守卫，避免回写机器级 `ProgramData` 回退。实测全套件运行前后 **AppData 与 ProgramData 两份配置 mtime/哈希均不变**。
3. 两条备注（旁路任务改到 UI 线程创建并登记；Windows 三连开关同款焦点停放）也已修。
4. 修正一条被中断子代理留下的**测试自身算术错误**（加了 4 项却断言 `Items.Count==3` 且访问 `Items[3]`）→ 改为 4，保留其全部顺序意图。

验证：构建 Debug/Release **0 error**（仅既有 CS8321/CS0649）；测试 **1335 通过 / 0 失败 / 1 跳过 / 1336**（+11 条新测试）；UI 审计 **0 条 / 430 张 × 16 视口**；独立审查门最终结论 **无阻断，S1–S5 与 GLOBAL 全 PASS**。

未能真机取证的项（已在交付说明中标注）：④a 的待机截图（时序窗口短）、④b 的弹窗截图（原生弹窗合成点击不可靠）、①的下拉选择真机复现（同因）——三项均由单元测试/源码审查覆盖。


---

## v9 二级界面重设计收尾（2026-09-14，`docs/run5-secondary-ui-redesign.md` 全项完成）

| 表面 | 变更 | 文件:行 |
|---|---|---|
| RgbForm | 色块原生 `Button` → `RColorButton`（Swatch 方法），Tag=`"color-swatch"` | `RgbForm.cs:421-432` |
| FanCurveForm | 双图纵向→横向（CPU 左/GPU 右）；`btnSave` → `RButton` + `ApplyPrimaryButton`；`_status`+`hint` 拆两个 Label | `FanCurveForm.cs:75-80,103-108,162-174,230` |
| LightForm | 删 Percent-100 填充行；ClientSize 内容实测（≤210）；colorBtn 36×24 | `LightForm.cs:47,174,224` |
| RColorPicker | 随机按钮→`Defaults[Random.Shared.Next]`；预览→Swatch 1px Border；rgbLabel→Consolas | `RColorPicker.cs:100,107,116,144` |
| UpdateForm | 空态"暂无更新说明"+ ApplyNotesState；SetBusy 补 `_feedback.Enabled`；按钮 96/96/88/88；`_notes`→RTextBox | `UpdateForm.cs:20-21,95-102,127-152,186-205,365-374` |
| DonateForm | QR 空态"二维码资源缺失"；ClientSize 高 500 | `DonateForm.cs:17,89-101` |
| DonateControl | 徽章描边 `RForm.colorTurbo` → `UiVisualStyle.Accent` | `DonateControl.cs:30` |
| FirstRunGuideForm | 删固定 MaximumSize；Resize/FontChanged ReflowDescription；间距→Space.Sm；底部 33/34/33 | `FirstRunGuideForm.cs:107-109,142,173-192` |

护栏测试：`Run5SecondaryCloseoutTests` 7 条全 GREEN（`LightForm_NoFillRow`、`FanCurveForm_HorizontalCharts`、`UpdateForm_EmptyNotesState`、`RColorPicker_RandomFromPalette`、`DonateForm_HeightCappedAt500`、`FirstRunGuideForm_NoFixedDescriptionWidth`、`RgbForm_SwatchUsesRColorButton`）。

验证：构建 Debug/Release **0 error**（4 预存 Settings.cs 警告）；测试 **1501 通过 / 0 失败 / 1 跳过 / 1502**（=绿色基线）；UI 审计 **65 条 / 0 新增 / 458 张 × 16 视口**（预存集：Settings parent-overflow 6 + KeyboardRgb docked-root-overflow 11 + FirstRunGuide text-clipping 48）。

真机实测（2026-09-14 收尾验证，175% DPI，`artifacts\run5-secondary\README.md`）：LightForm 实测 420×138 逻辑 px（填充行已删，≤210 上限）；DonateForm 实测恰 760×500 逻辑 px；触达的 8 个对话框控件 Right/Bottom 越界 **0**。护栏双向验证：正向=真机日志 `SetLightEffect(HidLightbar/Ctrl, Wave, …) 已发送` + `SetLightPower(...) confirmed` 随真实 UIA 手势下发（18:00–18:02 捕获时刻）；反向=护栏单测（UpdateForm SetBusy 覆盖反馈键、FanCurveForm 未连接拒存/`_saveInProgress` 互斥、RColorPicker 非法 Hex 拒绝与随机落色板、LightForm 关灯禁用）。真机截图：`Lightbar-`/`LogoLight-`/`KeyboardRgb-`/`Donate-`/`UpdateForm-`/`FirstRunGuide-`（交互捕获）+ `FanCurve-`/`ColorPicker-audit-render-175pct.png`（审计渲染回退，交互捕获因 UIA 模态阻塞/token 错误不可达）。

未能真机取证的项：所有 7 个对话框的真机目视节奏验证（测试覆盖尺寸/无裁剪，但无法验证视觉间距与对齐）；DonateForm QR 缺失空态的视觉居中对齐；日间模式下全部对话框的主题一致性。
