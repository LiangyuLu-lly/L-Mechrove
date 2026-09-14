# L-Mechrevo 二级/三级界面设计规格（P2/P3 批次）

> 范围：主仪表盘（P0/P1 已完成）之外的所有二级窗口与三级嵌套对话框。
> 本文为**设计文档**，不含任何源码改动。所有行号基于当前工作区源码。
> 依据：`src\MechrevoLiteWin\UI\UiVisualStyle.cs`、`DESIGN.md`、`docs\ui-consistency-pass.md`（v5..v8）、`ui-ux-pro-max` 设计数据库。

---

## 0. 术语与节奏基线（已对代码核实）

- **P0/P1/P2/P3 是重设计批次编号，不是运行时页面**（`DESIGN.md:191-192`、`docs\ui-consistency-pass.md:198-203`）。运行时主窗口是单页纵向仪表盘（`Settings.cs` + `Settings.V2.cs`），底部 footer（版本 + 悬浮窗/设置/更新/赞助/退出，`Settings.V2.cs:750-825`）。
- **节奏常量（实测，非 46px 假设）**：
  - 标题行 `D(26)`（`Settings.cs:2222/2244/2261`）；分段行 `D(34)`（`Settings.cs:2211-2212/2232-2233`）；灯控行 `D(32)`（`Settings.V2.cs:248`）；卡片 `D(56)/D(66)/D(94)`（`Settings.cs:2268/2227/2270`）；footer `D(44)` 面板 + `D(51)` 行（`Settings.cs:2274/2380`）；遥测行 `D(18)`（`Settings.V2.cs:22`）。
  - 间距 token：`Space.Xs=4 / Sm=8 / Md=12 / Lg=16 / Xl=24`（`UiVisualStyle.cs:34-41`）。**12 逻辑 px 为标准 gutter**。
  - ⚠️ 注意：`Settings.cs:26-29` 明确记录分段/标题/卡片高度是**故意不缩放的字面量**——历史上转成 `D(…)` 曾引发 248 文本裁剪 + 288 父容器溢出（`docs\ui-consistency-pass.md:288-297`）。二级窗口内新增行高沿用各窗口现有实测值，不引入新的 46px 标准。
- **调色板**：`Palette` 16 字段（`UiVisualStyle.cs:43-59`）；Night（`:62-80`）/ Day（`:82-100`）双主题；`Danger/Ok/Warn` 已按 4.5:1 对比度校准（`:75-77/:96-99` 注释）。`ui-ux-pro-max` 推荐的 "Dark tech + status green" 调色板（背景 `#0F172A`、状态绿 accent）与本项目 NightPalette（Window `#0B1220`、Ok `#35C46B`）同族——**结论：不引入外部色值，全部沿用现有 token**。
- **字体**：`TypeScale` 五档 8.5/9/9.5/12/15pt（`UiVisualStyle.cs:21-28`），基准 "Microsoft YaHei UI"（`:8`）；数值读数用 Consolas（`docs\ui-consistency-pass.md:162-165`）。`ui-ux-pro-max` "Fira Code/Fira Sans — dashboard/data/technical" 字体对印证：**数据读数用等宽、正文用无衬线**的既有决策正确，保持不变。
- **UX 准则引用（ui-ux-pro-max / ux-guidelines.csv）**：
  - `Contrast Readability`（High）：正文对比 ≥4.5:1 → 状态文本一律用 `Text/Muted/Ok/Warn/Danger` token。
  - `Focus States`（High）：模态内控件需可见焦点环 → RButton `ShowFocusCues=false`（`RButton.cs:81`）是已知缺口，见 §4.1。
  - `Target Size (Minimum)`（High）/ `Touch Spacing`（Medium）：可点目标 ≥24 逻辑 px、相邻间距 ≥8px。
  - `Empty States`（Medium）：空内容给提示，不留白板。
  - `Submit Feedback`（High）：提交后必须有 loading→成功/失败反馈。
  - `Compact Control Semantics`（Critical）：可交互 chip 必须是真按钮语义（对应 `mini-chip` 分支 `UiVisualStyle.cs:307-319`）。
  - `Form Labels`（High）：输入必须有可见标签，不允许仅占位符。

---

## 1. 界面清单（Inventory，含打开方式与嵌套深度）

```
SettingsForm（主窗口，P0/P1）
├─ SettingsDialog        二级  Settings.V2.cs:726（footer ⚙，V2.cs:816 → OpenSettingsDialog V2.cs:708-744）
│   └─ (宿主控件来自 SettingsForm，无更深子窗)
├─ CustomModeForm        二级  Settings.cs:1833（buttonCustomMode.Click :1831，Show :1843，非模态）
│   └─ FanCurveForm      三级  CustomModeForm.cs:384（Show :387，非模态，AddOwnedForm :385）
├─ RgbForm               二级  Settings.cs:368（OpenRgbForm :366，Show :372；入口 AddAction "键盘灯效" :3250）
│   └─ RColorPicker      三级  RgbForm.cs:453（ShowDialog，模态）
├─ LightForm ×2          二级  Settings.cs:355/356（OpenLightForm :353，Show :361；入口 Settings.V2.cs:459 灯条 / :519 Logo）
│   └─ RColorPicker      三级  LightForm.cs:227（ShowDialog，模态）
├─ ColorCalibrationForm  二级  Settings.cs:798（calibBtn.Click :796，ShowAdjacentTo :800）
├─ UpdateForm            二级  Settings.cs:3320（ShowUpdateDialog :3314-3327，模态；入口 footer ↻ V2.cs:796 + Settings.cs:1883）
│   └─ (反馈 → MessageBox QQ，Settings.cs:3306-3311)
├─ DonateForm            二级  Helpers\DonateControl.cs:89（footer ❤ Click :83-94；按钮 Settings.cs:2037）
│   └─ (右键菜单 LiquidCoolingLightMenu 同类：CustomContextMenu，DonateControl.cs:44-79)
├─ FirstRunGuideForm     二级  Settings.cs:2494（ShowFirstRunGuideIfNeeded，模态，onboarding_version 一次性）
├─ RColorPicker          三级  Settings.cs:1309（水冷灯色）/ Settings.cs:3489（aura 键盘色）
├─ LiquidCoolingLightMenu 一级下拉 Settings.cs:1237（水冷灯效 ContextMenuStrip）
├─ ToastForm             OSD   Program.cs:707（RunToast：Settings.cs:4156、ModeControl.cs:170、GPUModeControl.cs:176 等）
└─ HardwareOverlay       OSD   Program.cs:709（StartOverlay：Settings.cs:4148/4167、Program.cs:846）
```

全部 WinForms 对话框继承 `RForm : Form`（`UI\RForm.cs:6`），统一 `InitTheme(true)` + `UiVisualStyle.ApplyWindow` + `ResponsiveLayout.ScaleFrom96`（DPI 集中处理，175% 下经 `UiDpi.LayoutScale` ≥1 缩放，`UI\UiDpi.cs:41-44`）。OSD 两窗（ToastForm/Overlay）继承 `OSDNativeForm`，自绘、豁免 token（见 §4.4）。

| # | 界面 | 尺寸（逻辑 px） | 边框/模态 | 优先级 | 工作量 |
|---|---|---|---|---|---|
| 1 | SettingsDialog | 470×420（`SettingsDialog.cs:22`） | FixedSingle，非模态，关闭=Hide（`:28-35`） | P2 | M |
| 2 | CustomModeForm | 820×780，Min 520×280（`CustomModeForm.cs:41-42`） | Sizable，非模态 | P2 | L |
| 3 | FanCurveForm | ≤1100×≤920 按工作区钳制（`FanCurveForm.cs:42`） | FixedSingle，非模态 | P2 | M |
| 4 | RgbForm | 520×440，Min 360×280（`RgbForm.cs:50-51`） | Sizable，非模态 | P2 | L |
| 5 | LightForm ×2 | 420×240（`LightForm.cs:48`） | FixedSingle，非模态 | P2 | S |
| 6 | RColorPicker | ≈412×330 手排（`RColorPicker.cs:141`） | FixedDialog，模态，TopMost（`:61-66`） | P2 | M |
| 7 | ColorCalibrationForm | 500×190（`ColorCalibrationForm.cs:35`） | FixedSingle，非模态 | P2 | S |
| 8 | UpdateForm | 520×380（`UpdateForm.cs:40`） | FixedSingle，模态 | P3 | S |
| 9 | DonateForm | 760×560，Min 520×300（`DonateForm.cs:17-18`） | Sizable，非模态 | P3 | S |
| 10 | FirstRunGuideForm | 580×390，Min 540×330（`FirstRunGuideForm.cs:19-20`） | FixedDialog，模态 | P3 | S |
| 11 | ToastForm | 运行时 100 高（`ToastForm.cs:145-146`） | OSD 自绘 | P3 | S |
| 12 | HardwareOverlay | 运行时按模式/缩放（`HardwareOverlay.cs:989-1007`） | OSD 自绘 | P3 | M |
| 13 | LiquidCoolingLightMenu | 自动（ContextMenuStrip） | 下拉 | P3 | S |
| 14 | Stubs.cs 死窗体 | — | 从未显示 | P3 | S |

---

## 2. 按影响排序的缺陷总表（Top 5 加粗）

| 严重度 | 缺陷 | 位置 | 影响 |
|---|---|---|---|
| ★★★ | **RColorPicker 界面文本为英文硬编码（"Color"/"Hex"/"OK"/"Cancel"），且选中描边用遗留静态色 `RForm.colorStandard/borderMain`** | `RColorPicker.cs:60,102,125-126,247,306` | 每次取色都会看到的三级窗，与全中文 UI 冲突；日间模式描边色不随主题 |
| ★★★ | **DonateControl 右键菜单英文未翻译（"❤ Already donated"/"Not interested"/"Cancel"），且 "Cancel" 项无任何 Click 处理（死控件）** | `DonateControl.cs:52,59,66-67` | 用户可见英文 + 死菜单项 |
| ★★★ | **RgbForm/LightForm 硬编码 `Color.White` 与 `FromArgb(160,160,160)`，日间模式下白字落在白底上不可读** | `RgbForm.cs:45,74,132,167,377,402`；`LightForm.cs:43,69,151` | 日间模式直接破相 |
| ★★★ | **FanCurveForm 自绘用 `Brushes.LightGray/Black/White` + 硬编码 Segoe UI 字体，日间模式坐标轴/拖拽提示不可读** | `FanCurveForm.cs:397,407,431,432,335-336` | 日间模式破相 |
| ★★☆ | **ColorCalibrationForm 约 40% 窗口是空的**（Percent-100 填充行 `:97`，主开关移除后未缩窗） | `ColorCalibrationForm.cs:97` | 浪费空间，违反紧凑性硬约束 |
| ★★☆ | 死代码/死控件：`RgbForm.MakeStaticZonesCombo`（`RgbForm.cs:602-613`，已接线但从未调用）；`ToastForm.ReadText`（`ToastForm.cs:118-125`，唯一调用点已注释 `:155`）；`HardwareOverlay` FPS/图表代码硬编码 false（`:967,970` 及 `:97-103,554,575,583-584,653-654,993,997`）、`SetDragKey/_dragKey` 失效（`:866-871` vs `:418-421` 强制 true）；`Stubs.cs` 的 `AsusMouseSettings`/`Updates` 空窗体（`:46-55`） | 多处 | 维护噪音，审计工具会扫到 |
| ★★☆ | FirstRunGuideForm 徽章文字硬编码 `FromArgb(8,25,22)`（非 token）；描述 `MaximumSize=(485,0)` 固定像素宽，175% DPI 下可能裁剪 | `FirstRunGuideForm.cs:157,177` | 日间/高 DPI 风险 |
| ★☆☆ | CustomModeForm/RgbForm/LightForm 的 ComboBox/Button/NumericUpDown 全部是原生 WinForms 控件，未用 RComboBox/RButton/RNumericUpDown（R* 控件库已存在但五个窗体零使用 RComboBox/RButton/RNumericUpDown） | `CustomModeForm.cs:219-279` 等 | 视觉不一致（边框/箭头/禁用态与主窗不同） |
| ★☆☆ | RButton 无可见焦点环（`ShowFocusCues=false`，`RButton.cs:81`），违反 `Focus States`（High） | `RButton.cs:81` | 键盘可达性 |
| ★☆☆ | DESIGN.md §0.5 写 SettingsDialog 宽 460，代码是 470（`SettingsDialog.cs:22`）——文档漂移 | `DESIGN.md:73` | 文档失真 |
| ★☆☆ | DonateForm QR 图片缺失时 PictureBox 是空 `SurfaceRaised` 色块，无提示（`Empty States` 违例） | `DonateForm.cs:77-91` | 空白窗 |
| ★☆☆ | ToastForm 全局静态字段 `toastText/toastIcon/timer`（`ToastForm.cs:62-65`），并发 toast 相互覆盖 | `ToastForm.cs:62-65` | 反馈丢失 |

---

## 3. 逐界面设计规格

> 通用约定（适用于所有规格）：所有颜色只用 `UiVisualStyle` token；字体只用 `TypeScale` 五档；行高沿用各窗实测值；新增控件必须映射到真实功能（下文每项标注驱动的 API）；日间模式必须可读。

### 3.1 SettingsDialog（设置弹窗）— P2 / M

> **⚠️ 本节目标布局已过时（2026-09-13 用户裁剪后更新）**：局部调光（局部背光）功能已整体移除；
> 弹窗内的「悬浮窗」开关已移除（footer ⊙ 是唯一入口，`Settings.V2.cs:737`）；
> 「自动刷新率」已移入主窗「屏幕」行头右侧（`Settings.cs:651-676`）。
> 弹窗实际行 = 界面（主题）+ 显示（响应加速 + 屏幕校色）+ 系统（官方控制台），与代码一致。

**现状**：470 宽 FixedSingle AutoScroll（`SettingsDialog.cs`），宿主 界面/显示/系统 三节，全部 token 化，无死控件。footer ⚙ 打开，关闭=Hide 以便复开。

**缺陷**：
1. 宽度文档漂移：DESIGN.md:73 写 460，代码 470。
2. 420 固定高度在内容少时底部留白 >100 逻辑 px。

**目标布局**（保持 470 宽，高度按内容 AutoSize 收缩，钳制工作区）：

```
┌──────────────────────────────────────┐ 470 逻辑px
│ 界面                                 │ ← 节标题 Label，TypeScale.Caption，Muted
│   [日间] [夜间]  ← themePanel        │    行高沿用 CompactThemeModeLogicalHeight=44
│ 显示                                 │
│   响应加速        [RCheckBox]        │    （LCD Overdrive；局部调光行已随功能移除）
│   屏幕校色        [mini-chip →]      │    RButton Tag="mini-chip"（UiVisualStyle.cs:307-319）
│ 系统                                 │
│   官方控制台隔离   [状态行]           │    officialPanel
└──────────────────────────────────────┘
```

**改动清单**（Wave B 已落地）：
- MOVE：无（结构已正确；自动刷新率/悬浮窗/局部调光三行已随功能裁剪消失，不再回填）。
- ADD：无（悬浮窗状态反馈随该开关移除而取消）。
- REMOVE：无死控件；构造参数已从 7 个减到 4 个（`SettingsDialogLifetimeTests` 注释）。
- 修正 DESIGN.md §0.5 宽度为 470（文档侧）。
- 高度收紧：root 改 Dock=Top + AutoSize，`ShrinkToContent()` 在 OnShown/ShowAdjacentTo 按实测内容高收缩（钳制工作区）。

**DoD**：
- [x] 高度按内容收缩（`SettingsDialog_HasNoStaleRows` 断言 ≤340 逻辑 px）。
- [x] 弹窗内无已移除功能的陈旧行（源码扫描测试钉住）。
- [x] 日间模式截图通过 UiAuditRunner。
- [x] DESIGN.md 宽度改为 470。

### 3.2 CustomModeForm（自定义性能模式）— P2 / L

**现状**：820×780 Sizable（`CustomModeForm.cs:41`），全 token 化、无死控件、无英文残留（agent 审计确认）。9 个滑杆行（Label + RSlider + 原生 NumericUpDown）、4 个勾选行、2 个原生 ComboBox、4 个原生 Button 档位、底部 恢复/风扇曲线 + 提示（`:337-397`）。所有控件已接线（`:120,173,180,197,222,260,356,381`）。

**缺陷**：
1. **控件代差**：档位按钮、电源计划/睿频 ComboBox、9 个 NumericUpDown 全是原生控件（`:104-126,219-279,284-331`），与主窗的 RButton/RComboBox 观感不一致（边框、箭头、禁用态）。`ApplyTree` 会兜底重刷（`UiVisualStyle.cs:307-361`），但原生 ComboBox 的下拉项高、禁用态仍与 RComboBox（44 逻辑 px 行高，`RComboBox.cs:40-47`）不同。
2. 820×780 在 2560×1600@175% 下接近工作区高度上限，`ConstrainToWorkingArea` 会钳制（`ResponsiveLayout.cs:39-63`），但初始 780 逻辑 px 偏高——参数表 9 行 + 4 勾选 + 2 下拉共 15 行，可压缩。
3. `_powerWallStatus` 空时隐藏（`:83,474`）——正确，保持。

**目标布局**（目标高度 ≤700 逻辑 px）：

```
┌────────────────────────────────────────────────────┐ 820×≤700
│ 自定义性能模式            [状态] [功耗墙判定]      │ 26 逻辑px 标题行
│ [自定义1][自定义2][自定义3][自定义4]  ← RButton 分段 │ 34 逻辑px（对齐 D(34) 分段行）
│ ┌────────────────────────────────────────────┐    │
│ │ 电源计划   [RComboBox──────────]           │ 32 │
│ │ 睿频模式   [RComboBox──────────]           │ 32 │
│ │ PL1        [RSlider────] [值 Consolas]     │ 32 │ ×9 参数行
│ │ …（PL2/PL4/TCC/TGP/DB/风扇切换/GPU偏移×2）  │    │
│ └────────────────────────────────────────────┘    │
│ [恢复当前档默认] [风扇曲线]   参数保存到当前选中档 │ 34 底行
└────────────────────────────────────────────────────┘
```

**改动清单**：
- REPLACE（视觉等价替换，不改行为）：
  - 4 个档位 Button → `RButton` 分段组（`ApplySegmentGroup`，`UiVisualStyle.cs:218-229`），选中态 = Accent 内嵌块（`RButton.cs:245-293`），驱动 `Program.service.SwitchCustomProfile`（`:499`）不变。
  - `_planCombo`/`_boostCombo` → `RComboBox`（44 逻辑 px 项高），驱动 `WinPowerPlan.SetActivePlan/SetBoost`（`:227/265`）不变。
  - 9 个 NumericUpDown → `RNumericUpDown`（`RNumericUpDown.cs:12-24` 已有主题化），驱动 `SetCustomDetail`（`:533`）不变。
- REMOVE：无。
- ADD：无新功能控件（现有 15 个控件已覆盖全部真实功能）。

**交互/状态规则**：
- 初始：`OnCustomChanged` 回填（`:412,656`）；未选档位前参数行 `Enabled=false`（若当前无自定义档激活——沿用现有 `ActivateProfileAsync` 逻辑判断）。
- 禁用态：RSlider/RCheckBox 已有禁用外观（`RSlider.cs:178,202`；`RCheckBox.cs:119,202,216`）；RNumericUpDown 需补禁用灰（见 §4.3）。
- 错误/确认：功耗墙判定超限时 `_powerWallStatus` 用 `Warn/Danger` token（已实现 `:74-85`）；"恢复当前档默认" 需二次确认文案 "恢复当前档默认参数？"（破坏性写 EC，走 `RESTORE_OPERATING_MODE_DETAIL`，`:363-364`）。

**DoD**：
- [ ] 窗口内不再有原生 ComboBox/NumericUpDown/普通 Button（除分段组）。
- [ ] 高度 ≤700 逻辑 px 且 175% 下无裁剪（UiAuditRunner 视口用例）。
- [ ] 恢复按钮有确认文案。
- [ ] 全部控件 handler 保留（§5 死控件审计通过）。

### 3.3 FanCurveForm（风扇曲线，三级）— P2 / M

**现状**：≤1100×≤920（`FanCurveForm.cs:42`），FixedSingle，双 CurvePanel 自绘（`:333`）。控件全接线（`:107,120,436,451,465`）。

**缺陷**：
1. 自绘硬编码色：`Brushes.LightGray`（`:397,407` 网格/轴）、`Brushes.Black/White`（`:431,432` 拖拽数值提示）——日间模式黑底白字/白底黑字错乱。
2. 硬编码字体 `new("Segoe UI", 9.5F/11F, Bold)`（`:335-336`）——违反五档字体规范（`docs\ui-consistency-pass.md:120-129`）。
3. 底部 `btnSave` 原生 Button 90×32（`:99-105`）→ 应为 RButton 主按钮。

**目标布局**（结构不变，仅换皮）：

```
┌──────────────────────────────────────────┐
│ 风扇曲线（当前自定义档）        [状态]   │ 26 标题行
│ ┌──────────────┐ ┌──────────────┐       │
│ │ CPU 曲线面板  │ │ GPU 曲线面板  │       │ 填满剩余
│ │ 网格=Muted    │ │              │       │
│ │ 轴文字=Muted  │ │              │       │
│ │ 提示泡=Surface│ │              │       │
│ │ +Border 描边  │ │              │       │
│ └──────────────┘ └──────────────┘       │
│ [保存(主按钮)] [☑风扇独立控制] 独立提示  │ 34 底行
└──────────────────────────────────────────┘
```

**改动清单**：
- REPLACE：CurvePanel.OnPaint 内 `Brushes.LightGray` → `UiVisualStyle.Track`（网格）/`Muted`（轴文字）；`Brushes.Black/White` 提示泡 → `SurfaceRaised` 底 + `Text` 字 + `Border` 1px 描边；字体 → `TypeScale.Body/Caption`（`UiVisualStyle.cs:21-28`）。
- REPLACE：`btnSave` → `ApplyPrimaryButton`（`UiVisualStyle.cs:195-204`），驱动 `Program.hw.SetFanCurve`（`:303-304`）不变。
- REMOVE：无。

**交互/状态规则**：
- 初始：Shown 时 `GET_FAN_SPEED_CURVE_SETTING` 回读（`:202`）。
- 禁用态：设备丢失（`DeviceLost`）时保存按钮与曲线拖拽禁用，状态 Label 用 `Danger`。
- 反馈（`Submit Feedback` High）：保存后 `_status` 显示 "已保存" 1.5s（现有 `_saveTimer` 150ms 防抖 `:79` 之外加回显）。

**DoD**：
- [ ] OnPaint 无任何 `Brushes.*` 系统刷与硬编码字体。
- [ ] 日间模式曲线图可读（网格/轴/提示泡对比 ≥4.5:1）。
- [ ] 保存有可见回显。

### 3.4 RgbForm（键盘灯效）— P2 / L

**现状**：520×440 Sizable（`RgbForm.cs:50`），10 种 HID 模式参数面板动态重建（`:351`）。控件全接线。

**缺陷**：
1. 硬编码色：`Color.White`（`:45,74,132,377,402`）、`FromArgb(160,160,160)` 状态灰（`:167`）——日间模式白字不可读。
2. 死方法 `MakeStaticZonesCombo`（`:602-613`）——已接线但从未调用（被 `TierCombo` `:505` 取代）。
3. 模式下拉首项 "── 自定义 (软件渲染) ──"（`:94`）是分隔符式文案混在数据项里，语义混乱（`Compact Label Semantics`：状态与选项不应混用）。
4. `_lblStatus` 固定 38px 行（`:161-168`）+ 按钮行 42px（`:172-189`）——可接受，保留。

**目标布局**（结构不变）：

```
┌────────────────────────────────────┐ 520×440
│ 模式     [RComboBox────────────]   │ 32
│ 睡眠时间 [RComboBox────────────]   │ 32
│ ☐ 离电自动关闭全部灯效             │ 28
│ ┌ HID 参数面板（按模式重建）────┐  │
│ │ 速度 [RSlider──] 平滑 [RSlider]│  │ MinHeight 120（:151）
│ │ 颜色 [色块][色块]…             │  │
│ └───────────────────────────────┘  │
│ [状态：Muted/Ok/Danger]            │ 38
│ [开启灯效(主)] [停止灯效(次)]      │ 42
└────────────────────────────────────┘
```

**改动清单**：
- REPLACE：所有 `Color.White` → `UiVisualStyle.Text`；`FromArgb(160,160,160)` → `UiVisualStyle.Muted`；状态语义色：正常 `Muted`、成功 `Ok`、设备丢失 `Danger`（对比度已校准，`UiVisualStyle.cs:75-77`）。
- REPLACE：`_comboMode`/`_comboCloseTimer`/各 TierCombo → `RComboBox`；`btnStart` → 主按钮（`ApplyPrimaryButton` 已在 `:258` 调用，保留）。
- REMOVE：`MakeStaticZonesCombo` 方法整体删除（`:602-613`）。
- ADD：无（10 模式参数已全覆盖真实 HID 功能，`KeyboardRgb.StartMode`）。

**交互/状态规则**：
- 初始：`_rgb.DeviceLost` → 参数面板 `Enabled=false` + 状态 "设备未连接"（`Danger`），开启/停止按钮禁用（`:222,262` 已有事件，补 UI 态）。
- 禁用态：RSlider/RCheckBox 现成；RComboBox 需补禁用绘制（§4.3）。
- 确认：无破坏性操作。

**DoD**：
- [ ] 文件内无 `Color.White`/`FromArgb(160`（用户色 swatch 除外）。
- [ ] `MakeStaticZonesCombo` 删除。
- [ ] 设备丢失态完整（面板禁用 + 状态文案）。
- [ ] 日间模式截图通过。

### 3.5 LightForm（灯条/Logo，×2 实例）— P2 / S

**现状**：420×240 FixedSingle（`LightForm.cs:48`），5 行参数表。控件全接线（`:155,194,207,217,225`）。

**缺陷**：`Color.White` 三处（`:43,69,151`）——日间模式不可读。其余结构良好（Percent-100 填充行 `:239` 仅小量留白，可接受）。

**改动清单**：
- REPLACE：`Color.White` → `UiVisualStyle.Text`（3 处）。
- REPLACE：`comboMode`/`comboSpeed` → `RComboBox`；`colorBtn`（28×24 色块 `:224`）→ `RColorButton`（`RColorButton.cs:33-53`，双 swatch 能力正好匹配"单色颜色"语义），驱动 `RColorPicker` 打开（`:227-234`）不变。
- REMOVE/ADD：无。

**交互/状态规则**：初始回读 `GETSTATUS`（`:248`）；灯关时模式/速度/颜色行 `Enabled=false`（真实状态：`SetLightPower` off）。

**DoD**：[ ] 无 `Color.White`；[ ] 灯关时参数行禁用；[ ] 日间可读。

### 3.6 RColorPicker（取色器，三级模态）— P2 / M

**现状**：≈412×330 手排（`RColorPicker.cs:141`），`AutoScaleMode.None` + 手动 `S()` 缩放（`:67,71`），TopMost（`:66`）。SV 面板/色相条/预览/Hex 框/28+14 色板/OK/Cancel（`:87-135`）。

**缺陷**：
1. **英文硬编码**："Color"（`:60`）、"Hex"（`:102`）、"OK"/"Cancel"（`:125-126`）、RGB 标签（`:247`）——全中文 UI 中的三级窗，每次取色可见。
2. 选中描边用遗留静态 `RForm.colorStandard/borderMain`（`:306`）——不随主题。
3. `rgbLabel.BackColor = Color.Transparent`（`:107`）——当前父级是纯色所以安全，但违反 "tokens only / no Color.Transparent" 规范（`docs\ui-consistency-pass.md:145-149`）。
4. `AutoScaleMode.None` 偏离 RForm 的 Dpi 模式（有意为之但需文档化）。

**目标布局**：

```
┌──────────────────────────────┐ ≈412×330
│ 颜色                    [×]  │ 标题（中文）
│ ┌SV 200×200┐ ┌色相 18×200┐  │
│ └──────────┘ └───────────┘  │
│ 预览 150×110   十六进制 [RTextBox]│
│ RGB 值（Muted，Consolas）        │
│ [默认色板 14×2]                  │
│ [自定义色板 14]                  │
│ [随机]        [确定] [取消]      │ 84×28 RButton
└──────────────────────────────┘
```

**改动清单**：
- REPLACE：文本 "Color"→"颜色"、"Hex"→"十六进制"、"OK"→"确定"、"Cancel"→"取消"（或走 `Properties.Strings` 资源，与 `Strings.ThankYou` 同机制，`DonateControl.cs:99`）。
- REPLACE：`:306` 遗留静态色 → `UiVisualStyle.Accent`（选中描边）+ `Border`（未选描边）。
- REPLACE：`Color.Transparent` → 直接用父级 `UiVisualStyle.Input`。
- REMOVE：无。
- ADD：无（Hex 框/随机按钮已映射真实功能：`aura_color_custom` 持久化 `:22,209`）。

**交互/状态规则**：
- 初始：传入当前色；Cancel 恢复原色（`:79-82` 已实现）。
- 拖动实时预览、松手提交（`:196-211` 已实现，符合 `Submit Feedback`）。
- Hex 输入非法时框描边 `Danger` + 不提交（新增校验态，驱动现有 hex 解析路径）。

**DoD**：[ ] 无英文硬编码；[ ] 无遗留静态色/Transparent；[ ] 非法 Hex 有错误态；[ ] 175% 下无裁剪（手动 S() 已覆盖，回归验证）。

### 3.7 ColorCalibrationForm（屏幕校色）— P2 / S

**现状**：500×190（`ColorCalibrationForm.cs:35`），4 个模式按钮 88×34（`:62-83`）+ 两行说明 + Percent-100 填充行（`:97`）。主开关已移除（`:56-59` 注释），**窗口比内容高约 40%**。

**目标布局**：

```
┌──────────────────────────────┐ 500×~150（缩 40 逻辑px）
│ 屏幕校色            [状态]   │ 26
│ [默认][sRGB][P3][AdobeRGB]   │ 34（RButton 分段组）
│ 说明两行（Muted，Caption）   │ AutoSize
└──────────────────────────────┘
```

**改动清单**：
- REMOVE：Percent-100 填充行（`:97`）；ClientSize 190→~150。
- REPLACE：4 个 Button → `RButton` 分段组（`ApplySegmentGroup`），选中 = 当前 `GetColorCalibrationMode()` 回读（`:150`），驱动 `Program.service.SetColorCalibration`（`:143`）不变。
- ADD：无。

**交互/状态规则**：
- 初始：`_echoTimer` 1500ms 回读同步（`:100-102`）；不支持的模式按钮 `Enabled=false` + tooltip "当前机型不支持"。
- 应用中：状态 Label "应用中…"（`Submit Feedback`）；失败 `Danger`。

**DoD**：[ ] 无 >16 逻辑 px 的空白填充行；[ ] 分段组选中态与设备回读一致；[ ] 不支持模式有禁用态。

### 3.8 UpdateForm（更新）— P3 / S

**现状**：520×380 模态（`UpdateForm.cs:40`），全 token 化、4 个 RButton 全接线（`:106,113,119,125`）、ProgressBar + 状态 Label。**基本达标**。

**残余缺陷**：
1. `_notes` TextBox 只读多行（`:67-77`）——无更新说明时是空框（`Empty States` 违例）：无内容时隐藏该行并显示 Muted "暂无更新说明"。
2. 下载中按钮无禁用/进度互斥：下载进行时 4 个按钮应全部禁用（防重复触发 `UpdateInstaller.DownloadAsync`，`:216-295`）。

**改动**：ADD 空态文案；下载中禁用全部按钮 + `_status` 显示百分比（ProgressBar 已有）。**DoD**：[ ] 空说明有文案；[ ] 下载中按钮互斥禁用。

### 3.9 DonateForm + DonateControl（赞助）— P3 / S

**现状**：DonateForm 760×560 Sizable（`DonateForm.cs:17`），双 QR PictureBox（`:67-91`），纯展示。DonateControl 右键菜单（`DonateControl.cs:44-79`）。

**缺陷**：
1. **菜单英文未翻译**："❤ Already donated"/"Not interested"/"Cancel"（`:52,59,66`）→ "已赞助 ❤"/"不再提醒"/"取消"。
2. **"Cancel" 项无 Click 处理（死控件）**（`:66-67`）→ 删除该项（菜单外点击即取消，无需菜单项）。
3. 菜单边框用遗留 `RForm.colorTurbo`（`:29`）→ `UiVisualStyle.Border`。
4. QR 缺失时空色块（`:77-91`）→ 显示 Muted "二维码资源缺失" 文案（`Empty States`）。
5. 760×560 偏大：双 QR 并排各 ~350px 合理，但标题+副标题两行可合并为一行，高度可到 ~500。

**DoD**：[ ] 菜单全中文；[ ] 无死菜单项；[ ] QR 缺失有文案；[ ] 高度 ≤520。

### 3.10 FirstRunGuideForm（首次引导）— P3 / S

**现状**：580×390 模态（`FirstRunGuideForm.cs:19`），3 步骤行 + 3 按钮，全接线（`:112,123-124`）。

**缺陷**：
1. 徽章文字硬编码 `FromArgb(8,25,22)`（`:157`）→ `UiVisualStyle.AccentText`（token 已存在，`:71/:91`）。
2. 描述 `MaximumSize=(485,0)` 固定像素（`:177`）→ 改为按 `ResponsiveLayout.LogicalToDevice` 计算或用 Anchor/Percent，175% 下防裁剪。

**DoD**：[ ] 无硬编码色；[ ] 175% 截图无裁剪。

### 3.11 ToastForm（OSD 提示）— P3 / S

**现状**：OSDNativeForm 自绘黑底白字（`ToastForm.cs:77-78`），**OSD 惯例，豁免 token**（`:74-75` 注释已声明）。2s 自动隐藏（`:166-171`）。

**缺陷**：
1. `ReadText` TTS 死代码（`:118-125`，调用点已注释 `:155`）→ 删除。
2. 全局静态字段并发覆盖（`:62-65`）→ 改实例字段（每 toast 一实例），或排队显示。
3. 宽度公式 `100 + text.Length*22`（`:145`）对中文偏窄 → 按 `TextRenderer.MeasureText` 计算。

**DoD**：[ ] ReadText 删除；[ ] 连续两次 toast 不互相覆盖；[ ] 长文本不截断。

### 3.12 HardwareOverlay（悬浮监控）— P3 / M

**现状**：OSD 自绘，色值可配置（`overlay_color_gpu/cpu/alpha`，`:928-958`），黑底豁免 token。交互：点击循环模式（`:318-324`）、拖动、Ctrl+Shift+Alt+滚轮缩放（`:263-269`）。

**缺陷（均为死代码，非视觉）**：
1. FPS/图表代码硬编码 false（`:967,970`）及其布局分支（`:97-103,554,575,583-584,653-654,993,997`）→ 删除。
2. `SetDragKey/_dragKey` 失效（`:866-871`；`Tick` 强制 `keysDown=true` `:418-421`）→ 删除或恢复语义。
3. `DesktopApps` 白名单硬编码英文进程名（`:216-227`）→ 移到 AppConfig 可配置（真实功能：游戏模式前台判定 `:509-519`）。

**DoD**：[ ] 死分支删除；[ ] 白名单可配置；[ ] 绘制行为不变（截图对比）。

### 3.13 LiquidCoolingLightMenu（水冷灯效菜单）— P3 / S

**现状**：已达标——专用 Renderer 全 token（`LiquidCoolingLightMenu.cs:68-157`），分组头禁用项是刻意语义（`:28-37`），v8 新增（`docs\ui-consistency-pass.md:471-477`）。**无需改动**，仅登记为"已符合规范"基线。

### 3.14 Stubs.cs 死窗体 — P3 / S

`AsusMouseSettings : Form`（`Stubs.cs:46-50`）与 `Updates : Form`（`:52-55`）从未显示（仅 `TopMost`/`InitTheme` 被调用，Settings.cs:4752）。**REMOVE**：连同 `Settings.cs:4752` 的引用一起删除（ASUS 遗留，Mechrevo 硬件无此功能）。**DoD**：[ ] 编译通过；[ ] UiAuditRunner 不再扫到。

---

## 4. 横切规则

### 4.1 可达性（ui-ux-pro-max：`Focus States` High / `Target Size` High / `Touch Spacing` Medium）
- RButton 补可见焦点环（`ShowFocusCues=false`，`RButton.cs:81`）：绘制 2px `Accent` 圆角描边当 `Focused && ShowFocusCues`，与 RSlider 焦点环（`RSlider.cs:206-212`）、RCheckBox（`RCheckBox.cs:220-226`）对齐。
- 所有新增/替换控件高度 ≥24 逻辑 px（现有 32/34 行高满足）；相邻可点元素间距 ≥8 逻辑 px（`Space.Sm`）。

### 4.2 紧凑性预算
- **任何改动不得增加主窗口高度**：本批全部改动都在二级/三级窗内，主窗 `CompactDashboardLogicalClientSize=(420,529)`（v6，`docs\ui-consistency-pass.md:426-443`）不动。
- 二级窗内：删除 ColorCalibrationForm 填充行（-40 逻辑 px）、DonateForm 标题合并（-40）、CustomModeForm 行距收紧（目标 -80）。SettingsDialog 高度改按内容收缩。
- 禁止新增 >16 逻辑 px 的纯填充行（现有唯一违例即 ColorCalibrationForm `:97`）。

### 4.3 R* 控件库补齐（不新增控件类型，只补禁用态）
- `RComboBox`：补 `Enabled=false` 绘制（箭头与文字用 `Muted`，边框 `Track`）——当前无禁用绘制（`RComboBox.cs` 全文无 Enabled 分支）。
- `RNumericUpDown`：补禁用灰（同上，`RNumericUpDown.cs:15-24` 只有主题化无禁用态）。
- `RTextBox`：禁用态同补（`RTextBox.cs:17-22`）。
- 三者均为**现有控件的状态补全**，不是新控件。

### 4.4 日间模式（DayPalette，`UiVisualStyle.cs:82-100`）
- 每个界面改动后必须过 UiAuditRunner 日间用例（`UiAuditRunner.cs:244` 已有 SettingsForm 日间；扩展到全部 14 窗）。
- 禁止 `Color.Transparent` 于自绘容器（规范 `docs\ui-consistency-pass.md:145-149`）；本批清除 `RColorPicker.cs:107`。
- 状态文本对比 ≥4.5:1：只用 `Text/Muted/Ok/Warn/Danger`（Night `Danger=#F0584E`、Day `Ok=#16783F` 均已校准，`UiVisualStyle.cs:75-77,96-99`）。
- OSD 两窗（ToastForm/HardwareOverlay）黑底白字为**声明豁免**（`ToastForm.cs:74-75`），不迁移。

### 4.5 无死控件规则 + 测试方法
- **规则**：任何可见可交互控件（Button/CheckBox/ComboBox/Slider/NumericUpDown/TextBox/MenuItem 及 R* 等价物）必须有非空事件处理器，或显式标注 `Tag="static"`（如 DonateForm 的 QR PictureBox）。
- **测试缝**：扩展 `UiAuditRunner`（`UI\UiAuditRunner.cs`，现有 6 项检查 `:360-419`）新增第 7 项检查 **`AuditDeadControls`**：对每个实例化的窗递归 `Controls`，对 `ButtonBase/CheckBox/ComboBox/TrackBar/numeric/TextBox/ToolStripMenuItem` 反射断言已订阅相应事件（检查 `Events` 键或委托非空），MenuItem 检查 `Click` 委托；`Tag=="static"` 豁免。输出并入 `ui-audit.json`（`:311-347` 现有管道）。当前已知违例：`DonateControl.cs:66-67`（Cancel 项）、`RgbForm.cs:602-613`（死方法，删除后消失）。

### 4.6 文档同步
- DESIGN.md §0.5 SettingsDialog 宽度 460→470（`SettingsDialog.cs:22` 为准）。
- 本文档作为 P2/P3 批次的 DoD 来源；完成后在 `docs\ui-consistency-pass.md` 追加 v9 记录。

---

## 5. 执行顺序建议（按影响 × 依赖）

1. **P2-S 快赢**：LightForm 换色（S）→ ColorCalibrationForm 缩窗+分段组（S）→ SettingsDialog 高度收缩+overlay 状态（M 的前半）。
2. **P2-M**：RColorPicker 中文化+去遗留色（M）→ FanCurveForm 自绘换 token（M）。
3. **P2-L**：CustomModeForm 控件代差替换（L）→ RgbForm 换色+删死方法（L）。
4. **P3**：UpdateForm 空态/互斥 → DonateForm/DonateControl → FirstRunGuideForm → ToastForm/HardwareOverlay 死代码 → Stubs 删除 → R* 禁用态补齐 → UiAuditRunner `AuditDeadControls`。

---

## 6. 汇总表

| 界面 | 优先级 | 工作量 | 核心动作 |
|---|---|---|---|
| SettingsDialog | P2 | M | 高度收缩、overlay 状态反馈、文档对齐 470 |
| CustomModeForm | P2 | L | 原生控件→R*（分段/下拉/数值）、恢复确认、压高 |
| FanCurveForm | P2 | M | 自绘去硬编码色/字体、保存主按钮、保存回显 |
| RgbForm | P2 | L | 去 Color.White/灰、删 MakeStaticZonesCombo、设备丢失态 |
| LightForm | P2 | S | 去 Color.White、RComboBox/RColorButton、关灯禁用 |
| RColorPicker | P2 | M | 中文化、去遗留色/Transparent、Hex 校验态 |
| ColorCalibrationForm | P2 | S | 缩窗 40px、分段组、不支持禁用 |
| UpdateForm | P3 | S | 空态文案、下载互斥禁用 |
| DonateForm/Control | P3 | S | 菜单中文化、删死项、QR 空态、缩高 |
| FirstRunGuideForm | P3 | S | 徽章 token、DPI 自适应宽度 |
| ToastForm | P3 | S | 删 ReadText、并发安全、宽度测量 |
| HardwareOverlay | P3 | M | 删 FPS/图表死代码、白名单可配置 |
| LiquidCoolingLightMenu | P3 | S | 无改动（基线） |
| Stubs 死窗体 | P3 | S | 删除 AsusMouseSettings/Updates |
