# 交付索引（run-5 批次）— 2026-09-14

本文件汇总 run5 批次（二级/三级界面收尾 + 更多开关组 + 灯效空闲休眠重构）的**完成状态、证据位置、门禁数字与遗留项**，便于归档与逐项复核。
体例沿用 `docs/run4-delivery-index.md`。所有状态均由本索引编写者**读码/读证据文件**核验；仓库内找不到证据文件的数字一律标 **UNVERIFIED**，不采信口头交接。

## 一、run5 交付项

| # | 交付 | 状态 | 关键实现（file:line） | 证据 |
|---|---|---|---|---|
| 1 | CustomModeForm 重做（原生 spinner 隐藏、± chips、固定行节奏）+ 内容实测压缩 + 「切换未确认」瞬态修复 | ✅ | `UI\RNumericUpDown.cs:19-23`（HideNativeSpinButtons 隐藏原生箭头子控件）；`CustomModeForm.cs:204-225,254-255`（MakeSpinChip ± 微芯片）；`:587-607`（宽高按内容实测，MinimumSize 为下限）；`:18`（SwitchUnconfirmedText）+ `:662-670`（宽限期等硬件上报目标档，等不到才报失败） | 测试 `CustomModeFormTests.cs:40,54,225,237`（HideNativeSpinButtons / 固定行节奏 / 待定文案中性 / 失败措辞需超时+无硬件确认）；截图 `artifacts\run5-custommode\custommode-dialog-compact.png`；探针脚本 `artifacts\run5-custommode\probe-geometry.ps1` 等。**真机约 880×871 设备像素：UNVERIFIED**（仓库内无持久化尺寸记录，仅截图） |
| 2 | RgbForm 重做（模式单一真相源、睡眠选择器迁出、状态行动态占行）+ 内容实测压缩 + idle caption「自定义效果（HID 直连）」删除 | ✅ | `RgbForm.cs:251-253`（不再有自己的模式下拉，跟随 `_rgb.KbHidMode`）；`Settings.V2.cs:601`（睡眠时间选择器从 RgbForm 迁出）；`RgbForm.cs:104-106,143-154`（状态行 0/D(25) 动态、高度跟随内容表）；src 全量检索「自定义效果」0 命中 | 截图 `artifacts\run5-rgb-fancurve\rgb-dialog-compact.png`；审计 `artifacts\ui-audit-run5-rgb-fancurve\ui-audit.md`（476 张 / 几何 20，均为既有 comboLightingSleepTimer 与 KeyboardRgb 项）。**真机约 603×389 设备像素：UNVERIFIED**（同上，无持久化数字） |
| 3 | FanCurveForm 压缩（内容实测尺寸）+ ad-hoc 内边距统一到 D()/token | ✅ | `FanCurveForm.cs:196-206`（宽=max(标题行实测, 曲线最小宽+内距)，高=标题行+2×曲线最小高+底行）；`:69,95-96,101,116,140,149,155`（Padding 全走 D()） | 测试 `CustomModeFormTests.cs:169`（FanCurveForm_IsContentDerivedAndCurveStillEditable）；截图 `artifacts\run5-rgb-fancurve\fancurve-dialog-compact.png`。**真机约 661×842 设备像素：UNVERIFIED**。**未做部分**：双图仍纵向 50/50（`FanCurveForm.cs:165-167`）、btnSave 仍原生 Button（`:98`），见 `docs/run5-secondary-ui-redesign.md` §3 |
| 4 | 更多开关组新增「深度睡眠」「仅关闭显示器」 | ✅ | `Settings.cs:494`（"deepsleep" 深度睡眠，BIOS/EC 侧）、`:503`（"monitoroff" 仅关闭显示器，WM_SYSCOMMAND/SC_MONITORPOWER 广播）、`:589-618`（深度睡眠状态回读与提示） | 测试 `MoreSwitchesRun5Tests.cs:72,98,139,172`（关屏广播到所有顶层窗口 / 执行后回弹 Off / 回读硬件态 / pending 态显示）；真机截图 `artifacts\run5-moreswitches\deepsleep-on-dialog.png`、`deepsleep-off-dialog.png`、`moreswitches-after.png`；几何 `measure-before/after.json`（21 开关、行高 28、ParentOverflow 0） |
| 5 | 更多开关组新增「开机自启动」（用户级计划任务，非注册表 Run 键）+「静音狂暴模式」按机型支持三态门控 | ✅ | `Helpers\Startup.cs:3`（Microsoft.Win32.TaskScheduler）、`:45-52`（读写 seam）、`:112,132`（IsScheduled/用户任务查找）、`:238-243`（用户级任务计划 + TaskRunLevel.LUA）；`Settings.cs:506`（"startup" 开机自启动）、`:2765`（静音狂暴仅在确认支持时显示，Unknown/Unsupported 一律隐藏） | 测试 `Run5AutostartAndTurboGateTests.cs:30-203`（路径保空格去引号 / 幂等 / 单一命名条目建删 / 失败回滚 / 三态 Supported-Unsupported-Unknown / Unsupported 不渲染）；**真机自测** `artifacts\run5-autostart\autostart-selftest.txt`（off×2→False、on×2→True 幂等往返）；审计 `artifacts\ui-audit-run5-autostart-verify\ui-audit.md`（476/20） |
| 6 | 液冷组摘要手动档改百分比（与下拉措辞一致，含「最大」） | ✅ | `Hardware\LiquidCoolingDisplayPolicy.cs:21-22`（GearLabel：顶档→「最大」）、`:28-29`（泵 45/60/90%、扇 40/50/60/90%）、`:36-40`（GearSummaryLabel 摘要拼接） | 测试 `Run5LcCalibTests.cs:55`（LcSummary_ShowsTheSavedGearPerChannel，7 例含混搭）+ `:62`（顶档读「最大」与下拉一致）；真机自动态截图 `artifacts\run5-lc-calib\screen-row-lc-summary-auto.png`（「已连接 · 泵自动 · 风扇自动」）。**手动态真机摘要：UNVERIFIED**（桌面锁定无法切档；单测已覆盖） |
| 7 | 屏幕校色迁入屏幕行头圆角下拉（仅 默认/sRGB）；ColorCalibrationForm 与 P3/AdobeRGB 路径删除 | ✅ | `Settings.cs:851`（决策注释）、`:858-859`（comboColorCalibration）；`src\MechrevoLiteWin\ColorCalibrationForm.cs` 已不存在（目录清单核实） | 测试 `Run5LcCalibTests.cs:74,90,116,123,142,154`（仅两档 / 回显 / 索引映射 / 行头位置 / 写入走既有 service 路径 / 被删色域保持删除）+ MechrevoHardwareTests SetColorCalibration 6 条；真机几何 `artifacts\run5-lc-calib\geometry-uia.txt`（与自动刷新率开关同行、30px 间距、无重叠）+ `README-evidence.md`。**sRGB↔默认 活体写往返：UNVERIFIED**（测试时桌面锁定，注入点击被丢弃） |
| 8 | 灯效空闲休眠重构：三开关状态单一真相、单一到期时钟、每周期单次恢复 | ✅ | `Hardware\LightingState.cs:12-19`（ResolveIdleAction 单一判定）、`:26-27`（IsTemporarilySuspendEd 统一三通道）；`Program.cs:1287-1361`（SuspendLightingTemporarilyAsync 一次决策一次调用；「灯效恢复（单次应用）」） | 测试 `LightingSleepConsistencyTests.cs:152,199,235`（三开关同映暗设备 / 外置通道每周期恰好一次 / KeyboardRgb 不再自有 per-device 计时器）；**真机日志** `artifacts\run5-lightsleep\evidence.md`（三通道熄灭跨度 380ms 同一调用；恢复每通道 SetLightPower×1、SetLightEffect×1）+ `app-log-lightsleep.txt`。**物理 LED 实际发光：UNVERIFIED**（日志只能证明 GCU 回读一致） |
| 9 | 灯光行头睡眠下拉（comboLightingSleepTimer 迁入灯光行头） | ✅ | `Settings.V2.cs:601-624`（选择器迁出 RgbForm，写 `lighting_idle_seconds`）、`:659`（LightingCloseTimerOptions） | 截图 `artifacts\run5-light-header\light-header.png`、`light-collapsed.png`、`light-expanded.png`、`kb-dialog.png`；驱动脚本 `drive.ps1`/`prove-follow.ps1`。审计 `artifacts\ui-audit-run5-lightsleep\ui-audit.md`（476/20） |

## 二、门禁数字

| 项 | 数字 | 证据 | 判定 |
|---|---|---|---|
| 测试基线推进 | 1392 → 1396 → 1403 → 1420 → 1436（当前 1436 通过 / 0 失败 / 1 跳过） | **仓库内未找到持久化的测试运行产物**（artifacts 与 tests 目录均无 run5 测试汇总 txt/trx；`docs/ui-consistency-pass.md` 止于 v8，未记 v9） | **UNVERIFIED**（数字来自编排者交接；测试类本身已核实存在，见 §一 各行） |
| UI 审计截图数 | 476 → 458（减少的 18 张为已删除的校色对话框 ColorCalibration-*） | `artifacts\ui-audit-run5-rgb-fancurve\ui-audit.md:3`（476）→ `artifacts\ui-audit-run5-hardening\ui-audit.md:3` 与 `artifacts\ui-audit-run5-lc-calib\ui-audit.md:3`（458）；ColorCalibrationForm.cs 已删 | 截图数 ✅ 已核实；「18 张=校色对话框」为推断（476−458=18 吻合，未逐张比对清单） |
| UI 审计几何问题 | 最终审计 **20 条**（非 0）：comboLightingSleepTimer parent-overflow ×6 + KeyboardRgb docked-root-overflow ×14 | `artifacts\ui-audit-run5-hardening\ui-audit.md:7-25`、`artifacts\ui-audit-run5-lc-calib\ui-audit.md`（同 20 条）；中途 `ui-audit-run5-clip` 曾 470/0、`ui-audit-run5-custommode` 476/25 | ✅ 已核实；20 条为**已知遗留**，不是本批回归清零目标 |
| 构建 | Release x64 可运行（真机证据均出自 `bin\x64\Release\...\L-Mechrevo.exe`） | `artifacts\run5-lc-calib\README-evidence.md:3`、`artifacts\run5-lightsleep\evidence.md:3` | 运行态 ✅；**0 错误/警告数：UNVERIFIED**（无 run5 编译日志入库存档） |

## 三、真机未验证项（需解锁桌面后目视/操作确认）

| 项 | 说明 | 软件侧已覆盖 |
|---|---|---|
| CustomModeForm 尺寸验收 | 真机 ~880×871 设备像素为交接数字，仓库无持久化记录 | 截图 `artifacts\run5-custommode\custommode-dialog-compact.png`；内容实测逻辑有单测 |
| 灯效空闲休眠目视 | 日志证明 GCU 电源回读一致，但灯带/Logo/键盘**实际发光**未做物理采样 | `artifacts\run5-lightsleep\evidence.md` 末节自述 |
| 「仅关闭显示器」一次性行为 | 关屏广播有单测（MoreSwitchesRun5Tests.cs:98），真机一键关屏后不立刻被电源计划唤醒回来的一次性表现未目视 | 单测 + `artifacts\run5-moreswitches\verify-notice-dialog.ps1` |
| 色域下拉活体往返 | sRGB↔默认 写往返未在真机驱动（桌面锁定，注入点击被丢弃）；注册表回读与下拉显示一致性已核实 | `artifacts\run5-lc-calib\README-evidence.md`；写路径 6 条单测 + Run5LcCalibTests.ColorCalibrationCombo_WritesThroughTheExistingServicePath |
| 液冷手动档摘要 | 真机手动态（切档后摘要显示 45%/60%/90%…「最大」）未目视 | Run5LcCalibTests.LcSummary_ShowsTheSavedGearPerChannel（7 例）+ 真机自动态截图 |

## 四、阻塞项

| 项 | 阻塞点 | 在谁 |
|---|---|---|
| GCU 真机安装 | 等作者在 A/B/C 三个安装方案中拍板（方案细节见 `docs\gcu-dependency-matrix.md` 与 run4 索引 §一.3/4 的载荷与安装器产物） | 作者（仓库内无 A/B/C 决策记录文件，本行为交接信息） |
| GitHub Releases 托管 | 更新分发需要公开仓库才能挂 Releases；当前无公开仓库 | 作者 |

## 五、关键文档与产物索引

- 设计/文档：`docs\run5-secondary-ui-redesign.md`（本批重定基）· `docs\run4-p2p3-design-spec.md` · `docs\ui-consistency-pass.md`（v2–v8，v9 未追加）· `docs\gcu-dependency-matrix.md` · 本文件
- 证据目录：`artifacts\run5-custommode\` · `run5-rgb-fancurve\` · `run5-moreswitches\` · `run5-autostart\` · `run5-light-header\` · `run5-lightsleep\` · `run5-lc-calib\`
- 审计目录：`artifacts\ui-audit-run5-custommode\` · `-rgb-fancurve\` · `-moreswitches\` · `-autostart\` · `-autostart-verify\` · `-lightsleep\` · `-hardening\` · `-lc-calib\` · `-clip\`
