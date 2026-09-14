# 交付索引（run-4 批次）— 2026-09-13

本文件汇总本批次（用户 10 项反馈 + 过程中发现的 2 个回归）的**完成状态、证据位置、门禁数字与遗留项**，便于归档与逐项复核。
所有"已完成"项均由编排者**独立复跑测试/像素测量/读码**核验，不采信子代理自述。

## 一、用户 10 项

| # | 需求 | 状态 | 关键实现（file:line） | 证据 |
|---|---|---|---|---|
| 1 | 键盘灯效开启后只亮几秒就灭 | ✅ 已修 | `Settings.V2.cs` `RunHidWhileFirmwarePowerBypassCompletesAsync`：固件电源落地后**重申** ReInit+StartMode（容错+代际守卫）；另加 `StopHidEffectForCurrentGeneration` 守卫迟到的 OFF | `artifacts\run4-kb-fix\`（RED/GREEN + 真机日志窗口）；复跑 Keyboard **53/53**。**⚠️ 物理 LED 需用户目视** |
| 2 | 深度探查 GCU 依赖面 | ✅ | — | `docs\gcu-dependency-matrix.md`（20 行矩阵：必需 12 / 可选 3 / 无关 5；安装面；代际判据；降级缺口） |
| 3 | 提取 40/50 系 GCU 并随安装包自动安装 | ✅ | `innounp 2.70.1` 解出两个 40 系 Inno 安装器内的 GCU（`UniwillService` / `AiStoneService`） | `release\GCU-40-51749\`（72 文件 37.4MB）· `release\GCU-40-51751\`（74 文件 53.5MB）· `release\GCU-common\UWACPIDriver\`（与 50 系逐字节相同）；各含 `SHA256SUMS.txt`；`artifacts\run4-gcu40\report.md` |
| 4 | 安装流程改造（参考 CSDN 文章） | ✅ | Inno Setup：per-machine + admin + x64 + **简中语言包** + 卸载项 + 四套 GCU 载荷入包 + 安装时按**显卡代际**自动选载荷 | `installer\`（`L-Mechrevo.iss` / `Select-GcuPayload.ps1` / `Install-Gcu.ps1` / `Uninstall-Gcu.ps1` / `Build-Installer.ps1` / `README.md`）；产物 `artifacts\run4-installer\L-Mechrevo-beta13-setup.exe`（129,155,107 B，SHA256 `09393BCC…42104229`）；代际自测 **8/8**；dry-run 签名校验通过（GCUBridge/GCUService = AISTONE；UWACPIDriver.sys = Microsoft WHCP）；编译日志 `iscc-compile.log` |
| 5 | P2 设置重排（去局部调光 / 去设置内悬浮窗 / 自动刷新率并入屏幕行头） | ✅ | `Settings.cs` 已**零** LocalDimming 引用；`Settings.V2.cs` 无 `_overlayToggle`（footer `buttonOverlay` 保留）；`BuildHeadRow(…, Control? status)` + `Settings.cs:713` 传入 `autoHzChk` | 测试 **97/97**；审计 `artifacts\ui-audit-run4-p2` = **430 张 / 几何 0**；`artifacts\run4-p2-layout\` 截图 |
| 6 | 日间模式完善 | ✅ | `[临时诊断]` glyph 路径移除；`DayPalette` Ok/Warn 压深至 ≥4.5:1（`UiVisualStyle.cs:82-86`）；`ComboThemeFor(IsNightMode)` 双模式 | 测试 **44/44**；审计 `artifacts\ui-audit-run4-day` = 430/0；像素测量 + 原图目视：浅色可读、无黑块 |
| 7 | P2/P3 二三级界面全部完善 | 🔄 见 §三 | 设计规格 + Wave A/B 实现 | `docs\run4-p2p3-design-spec.md`（14 面规格）；`artifacts\run4-ui-waveA\`；Wave B 见下 |
| 8 | 键盘灯高负载卡顿/闪烁 | ✅ | `Hardware\FramePacer.cs`（单调 `_nextTick` + 自旋预算 ≤ 半帧）＋ `KeyboardRgb.cs` 单飞锁外重连（`ReconnectProbe` seam、`ReconnectTimeoutMs=3000`） | 真机 A/B：帧间隔 **p95 47.3 → 33.3 ms**、max 48.1 → ~38.0，心跳保持 1801 帧/60s；复跑 Keyboard **53/53**；`artifacts\run4-kb-flicker\` |
| 9 | 推送更新全链路 | 🔄 客户端已修，服务端待用户放开上传 | 根因＝客户端发裸标签 `beta13`（服务器判非法）→ 改发完整 semver：`Program.ReleaseVersion`（`0.289.0-beta13`），`UpdateChecker.cs:92` | 复跑 Update **59/59**；E2E stub 复刻服务器版本规则（裸标签→`ok=false`），RED 真失败/GREEN 通过；`artifacts\run4-update-fix\`；**服务器侧**发布/删除往返、频道行为、`update_check` 契约均已实测（`artifacts\run4-e2e-update\`） |
| 10 | 液冷最大档显示「最大」而非 90% | ✅ | `LiquidCoolingDisplayPolicy.GearLabel(percent, profile, top)`（`:21-22`）＋ `Settings.cs:1097/1162` 接线 | 复跑 LiquidCooling **80/80**；真机截图 `artifacts\run4-lc-max\lc-row-crop-2x.png`（两下拉均显示"最大"，状态"泵速已确认：最大"） |

## 二、过程中发现并修复的两个回归（超出原始 10 项）

| 回归 | 根因 | 修复 | 证据 |
|---|---|---|---|
| **日↔夜切换后标签残留浅底（浅底浅字不可读）** | `ApplyTree` 的 `Label` 分支**只刷 ForeColor、从不刷 BackColor**；显式赋过 `BackColor` 的标签把构造期色板烤死（`Settings.cs:716/731`、`Settings.V2.cs:268`、`:68-69`）；容器重刷只涂容器；`RetintChrome` 只对自有弹窗调用 | `UiVisualStyle.cs:419` 通用 Label 分支补 `BackColor = Parent?.BackColor ?? Surface`；`:341` 修 Tag 字形标签分支（此前直接 break、从不重算）；`:375` footer-ghost 补 `FlatAppearance.BorderColor = button.BackColor` | 新增 `tests\…\ThemeSwitchIdempotencyTests.cs`（2 用例）；复跑 53/53；`artifacts\run4-theme-switch\before|after\`（各 19 张 + 两张联系表 + `label-rects.json`）；**夜间主窗口近白像素 8.5% → 0.9%，且 3 轮切换数值不漂移（幂等）** |
| **折叠组图标（液冷/灯光/更多开关）夜间仍为白底方块** | `RCollapseGroup.cs:86-91` 的组头图标**故意不登记 Tag** → 完全绕过主题机制；且 `ApplyTree` 的 PictureBox 分支与 `RenderGlyph` **只换位图、不重算 BackColor** | 组头图标纳入主题重刷（底色随卡片现算 + 位图按当前 Muted 重渲染） | 夜间真实审计截图目视：液冷图标"light-gray glyph directly on dark bg — **no box**"，整屏无残留（灯光/更多开关同代码路径） |

## 三、P2/P3 界面推进状态

| 波次 | 内容 | 状态 |
|---|---|---|
| 设计规格 | `docs\run4-p2p3-design-spec.md`：14 面（3.1 SettingsDialog … 3.14 死窗体）+ 6 条横切规则 + 执行顺序 | ✅ |
| Wave A | Top5 缺陷：RColorPicker 中文化+令牌 · DonateControl 菜单中文化+删死项 · RgbForm/LightForm 硬编码色 · FanCurveForm 自绘色/字体 · ColorCalibration 190→150 压缩 | ✅ 复跑 **31/31**；审计 430/0；`artifacts\run4-ui-waveA\`（12 张日夜 + 联系表） |
| Wave B（P2 五面） | CustomModeForm 原生控件→R*（零 `new ComboBox/NumericUpDown/Button`）· RgbForm 删死控件+**设备丢失态** · LightForm R*+关灯禁用 · RColorPicker **Hex 非法可见拒绝** · SettingsDialog 无残留行 | ✅ 逐项读码核验；复跑 **78/78**；审计 `artifacts\ui-audit-run4-waveB` = **420/0**（420 vs 430 已解释：CustomMode 由 top/middle/bottom 三张改为 `-full` 一张，5 配置共少 10 张） |
| Wave B2（死代码） | `ToastForm.ReadText` 删除 · `HardwareOverlay.SetDragKey`+`_dragKey` 字段+复位点删除 · `FirstRunGuideForm:157` 硬编码 `Color.FromArgb(8,25,22)` → `UiVisualStyle.AccentText` | ✅ 编译 0 错（8 条既有警告）；残留引用 0/0/0；复跑 **54/54** |
| Wave B2（剩余 P3） | UpdateForm 空态+下载互斥 · DonateForm QR 空态+缩高 · FirstRunGuideForm DPI 自适应宽度 · HardwareOverlay FPS/图表死分支 | ⏳ 未做（P3 最低优先级） |

**规格纠错**：`docs\run4-p2p3-design-spec.md` §3.14 建议"删 `Stubs.cs` 的 `AsusMouseSettings`/`Updates` 空窗体"——**该条作废**：`Stubs.cs` 文件头注释明确记录这两个类型仍有活调用点（开机/灯效路径），删除需连带重写那些路径，属另一件事。

## 四、门禁数字（均为编排者独立复跑）

- 构建：`dotnet build -c Debug -p:Platform=x64` → **0 错误**（8 条既有警告：CS8321 未使用局部函数 `D` ×2、CS0649 `_dashboardPageHost`、`_lightingActionTable`）
- 分组测试：LiquidCooling **80/80** · Keyboard **53/53** · Update **59/59** · Theme/UiStyle/DayMode/SettingsLayout **53/53** · WaveB/Theme/… **78/78** · 死代码后 **54/54**
- UI 审计（`--ui-audit`）：`ui-audit-run4-p2` / `-day` / `-theme` = **430 张 / 几何 0**；`-waveB` = **420 张 / 几何 0**（差异已解释）
- 全量套件（最终门禁）：**1372 通过 / 0 失败 / 1 跳过 / 总计 1373**（基线 1335 → 本批新增 37 条测试；耗时 2 分 20 秒）

## 五、需要用户侧动作 / 阻塞项

| 项 | 说明 |
|---|---|
| **源站 nginx 上传上限** | `client_max_body_size 200m;` + PHP `post_max_size/upload_max_filesize = 200M`。当前 48–64MB 之间（64MB → `413`），真实包 74.2MB 无法直传；放开后编排者可立刻真发一版跑端到端（收到→下载→SHA256→替换→重启） |
| **版本号格式（已由代码解决，需注意发版约定）** | 服务器只接受数字开头点分版本（`9.6.2`/`v9.6-beta`）。客户端现已发 `0.289.0-beta13`，**后台发布时版本号要写成同一体系**（如 `0.289.0-beta14`），否则比较不成立 |
| **目视验收** | ① 键盘灯是否常亮；② `日↔夜` 反复切换后标签与三个分区图标是否均无白块 |
| **子代理额度** | 账户余额不足导致委派全线失败（deepseek→glm→kimi→mimo 连环重试均 `Insufficient balance`）；P3 剩余项由编排者串行直做，或充值后并行 |

## 六、关键文档与产物索引

- 设计/文档：`docs\gcu-dependency-matrix.md` · `docs\run4-p2p3-design-spec.md` · `docs\ui-consistency-pass.md`（v5–v8）· 本文件
- 安装与载荷：`installer\*` · `release\GCU-only\` · `release\GCU-40-51749\` · `release\GCU-40-51751\` · `release\GCU-common\`
- 证据目录：`artifacts\run4-kb-diag\` · `run4-kb-fix\` · `run4-kb-flicker\` · `run4-lc-max\` · `run4-gcu40\` · `run4-installer\` · `run4-p2-layout\` · `run4-daymode\` · `run4-ui-waveA\` · `run4-ui-waveB\` · `run4-theme-switch\` · `run4-theme-icons\` · `run4-update-fix\` · `run4-e2e-update\` · 审计目录 `ui-audit-run4-*`
