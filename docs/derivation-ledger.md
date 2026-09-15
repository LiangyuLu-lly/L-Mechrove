# 派生代码重写台账（derivation-ledger）

## 目的

本仓库当前以 A 方案发布：保留 GPL 许可声明与 GPLv3 §6(b) 的对应源码提供义务，因此本台账**不阻塞任何发布**。它存在的意义是把"将源自 G-Helper 的代码重写为完全原创"这项长期工作变成可量化、可中断、可交接的任务清单：每重写一个文件就更新一行，进度随时可见，工作随时可以停下并在下次接续。

## 基准与核对协议

- 上游基准：`seerge/g-helper`，固定提交 `2ea4c5c59a575835bd167ecb71ae1c25558e3068`（下称"基准提交"）。所有比对均针对该提交的 `app/` 目录，通过 GitHub API / raw.githubusercontent.com 获取。本地 `g-helper-main\` 目录已被清空到只剩 LICENSE，不能作为比对参考。
- 判定方法（本次重建清单时使用）：对每个本地文件与其上游对应文件做逐行最长公共子序列（LCS）比对（忽略 using/namespace 头部与空行）。共享行占本地文件 ≥80% 记为"逐字"（含少量本地改动），有明确共享区域但低于 80% 记为"部分"，低于约 15% 记为"轻微重叠"。
- **重写完成判定协议**：重写一个文件后，必须重新执行与基准提交的上游比对，判定必须变为"独立"或"轻微重叠"（共享行占比降到噪声水平），并且行为等价测试全部通过，才允许把状态改为"已完成"。只改了注释、改名、调整顺序不算重写。
- **不阻塞规则**：只要 A 方案的许可声明仍在位，本台账上的任何一项都不得阻塞版本发布。重写是长期增量工作，做多少算多少。

## 现存派生文件清单

调用点数量为"类名在其他源文件中出现的次数"（排除自身文件与 obj 目录），是近似值而非 AST 精确统计。规模为当前文件总行数（含空行与注释）。风险列标注该文件是否涉及硬件（SMU/EC/ACPI）、显示、GPU、注册表或进程提权。

### 逐字派生（29 个，全部待重写）

| 文件 | 上游对应 | 判定 | 当前状态 | 调用点数量 | 规模（行） | 风险 | 备注 |
|---|---|---|---|---|---|---|---|
| Display\DisplayNative.cs | app/Display/DisplayNative.cs | 逐字 | 待重写 | 35 | 491 | 显示 | 424/424 行与上游一致，100% |
| Display\ScreenNative.cs | app/Display/ScreenNative.cs | 逐字 | 待重写 | 9 | 199 | 显示 | 165/166 行一致，99.4% |
| UI\ControlHelper.cs | app/UI/ControlHelper.cs | 逐字 | 待重写 | 9 | 343 | 无 | 280/282 行一致，99.3% |
| UI\CustomContextMenu.cs | app/UI/CustomContextMenu.cs | 逐字 | 待重写 | 6 | 110 | 无 | 91/91 行一致，100% |
| UI\RTextBox.cs | app/UI/RTextBox.cs | 逐字 | 待重写 | 4 | 58 | 无 | 49/49 行一致，100% |
| Gpu\IGpuControl.cs | app/Gpu/IGpuControl.cs | 逐字 | 待重写 | 2 | 14 | GPU | 10/10 行一致，100%，纯接口 |
| Helpers\RestrictedProcessHelper.cs | app/Helpers/RestrictedProcessHelper.cs | 逐字 | 待重写 | 1 | 222 | 进程提权 | 181/181 行一致，100% |
| Helpers\MemoryHelper.cs | app/Helpers/MemoryHelper.cs | 逐字 | 待重写 | 1 | 38 | 无 | 30/30 行一致，100% |
| Pawn\CpuInfo.cs | app/Pawn/CpuInfo.cs | 逐字 | 待重写 | 27 | 67 | SMU/硬件 | 49/49 行一致，100% |
| UI\RColorButton.cs | app/UI/RColorButton.cs | 逐字 | 待重写 | 8 | 95 | 无 | 78/79 行一致，98.7% |
| UI\RComboBox.cs | app/UI/RComboBox.cs | 逐字 | 待重写 | 31 | 325 | 无 | 273/278 行一致，98.2% |
| Helpers\DynamicLightingHelper.cs | app/Helpers/DynamicLightingHelper.cs | 逐字 | 待重写 | 4 | 150 | 硬件（灯效） | 121/123 行一致，98.4% |
| Helpers\DeviceHelper.cs | app/Helpers/DeviceHelper.cs | 逐字 | 待重写 | 1 | 65 | 无 | 51/52 行一致，98.1% |
| Helpers\ToastForm.cs | app/Helpers/ToastForm.cs | 逐字 | 待重写 | 2 | 162 | 无 | 124/127 行一致，97.6% |
| Properties\Resources.Designer.cs | app/Properties/Resources.Designer.cs | 逐字 | 待重写 | 不适用 | 935 | 无 | 设计器生成文件，810/838 行一致，96.7% |
| Properties\Strings.Designer.cs | app/Properties/Strings.Designer.cs | 逐字 | 待重写 | 不适用 | 2605 | 无 | 设计器生成文件，2292/2313 行一致，99.1% |
| UI\RBadgeButton.cs | app/UI/RBadgeButton.cs | 逐字 | 待重写 | 4 | 66 | 无 | 53/55 行一致，96.4% |
| Pawn\RyzenSmu.cs | app/Pawn/RyzenSmu.cs | 逐字 | 待重写 | 1 | 590 | SMU/硬件 | 475/501 行一致，94.8%，SMU 驱动通信核心 |
| Display\AmdDisplay.cs | app/Display/AmdDisplay.cs | 逐字 | 待重写 | 3 | 104 | 显示 | 78/83 行一致，94% |
| Display\ScreenControl.cs | app/Display/ScreenControl.cs | 逐字 | 待重写 | 23 | 294 | 显示 | 227/244 行一致，93% |
| Mode\Modes.cs | app/Mode/Modes.cs | 逐字 | 待重写 | 43 | 190 | 硬件（性能模式） | 151/161 行一致，93.8% |
| Gpu\NVidia\NvmlHelper.cs | app/Gpu/NVidia/NvmlHelper.cs | 逐字 | 待重写 | 5 | 111 | GPU | 92/98 行一致，93.9% |
| Helpers\OSDBase.cs | app/Helpers/OSDBase.cs | 逐字 | 待重写 | 13 | 497 | 显示（OSD 窗口） | 432/466 行一致，92.7%，声明 OSDNativeForm 基类 |
| UI\RColorPicker.cs | app/UI/RColorPicker.cs | 逐字 | 待重写 | 8 | 454 | 无 | 337/375 行一致，89.9%，含少量本地改动 |
| Mode\PowerNative.cs | app/Mode/PowerNative.cs | 逐字 | 待重写 | 8 | 450 | 硬件（电源） | 318/365 行一致，87.1% |
| Overlay\HardwareOverlay.cs | app/Overlay/HardwareOverlay.cs | 逐字 | 待重写 | 3 | 1147 | 显示 | 829/995 行一致，83.3%，含本地改动 |
| Display\ScreenCCD.cs | app/Display/ScreenCCD.cs | 逐字 | 待重写 | 2 | 453 | 显示 | 321/393 行一致，81.7%，含本地改动 |
| Settings.Designer.cs | app/Settings.Designer.cs | 逐字 | 待重写 | 不适用 | 1482 | 无 | 设计器生成文件，1402/1474 行一致，95.1%，与 Settings.cs 同为 SettingsForm 的 partial |
| Helpers\TempHelper.cs | app/Helpers/TempHelper.cs | 逐字 | 待重写 | 2 | 17 | 硬件（温度） | 仅 13 行有效代码，11 行一致，84.6% |

### 部分派生（21 个，全部待重写）

| 文件 | 上游对应 | 判定 | 当前状态 | 调用点数量 | 规模（行） | 风险 | 备注（可定位的共享区域） |
|---|---|---|---|---|---|---|---|
| AppConfig.cs | app/AppConfig.cs | 部分 | 待重写 | 341 | 655 | 注册表/配置 | 361/553 行与上游共享（65.3%）；配置键读写、默认值表结构沿用上游 |
| NativeMethods.cs | app/NativeMethods.cs | 部分 | 待重写 | 36 | 160 | Win32 互操作 | 81/127 行共享（63.8%）；P/Invoke 声明块大量沿用 |
| Mode\ModeControl.cs | app/Mode/ModeControl.cs | 部分 | 待重写 | 8 | 710 | 硬件（模式切换） | 418/579 行共享（72.2%）；模式切换主流程沿用 |
| Pawn\PawnIOWrapper.cs | app/Pawn/PawnIOWrapper.cs | 部分 | 待重写 | 4 | 140 | SMU/驱动 | 上游 87 行全部包含于本地（100%），本地另加约 26 行；上游代码原样在内 |
| Gpu\GPUModeControl.cs | app/Gpu/GPUModeControl.cs | 部分 | 待重写 | 10 | 620 | GPU/硬件 | 234/516 行共享（45.3%）；GPU 模式切换流程沿用 |
| Gpu\NVidia\NvidiaGpuControl.cs | app/Gpu/NVidia/NvidiaGpuControl.cs | 部分 | 待重写 | 9 | 565 | GPU | 254/466 行共享（54.5%）；超频/功耗控制路径沿用 |
| Gpu\NVidia\NvidiaSmi.cs | app/Gpu/NVidia/NvidiaSmi.cs | 部分 | 待重写 | 1 | 85 | GPU | 46/69 行共享（66.7%）；nvidia-smi 输出解析沿用 |
| Helpers\DonateControl.cs | app/Helpers/DonateControl.cs | 部分 | 待重写 | 2 | 106 | 无 | 59/87 行共享（67.8%）；捐赠弹窗逻辑沿用 |
| Helpers\ProcessHelper.cs | app/Helpers/ProcessHelper.cs | 部分 | 待重写 | 31 | 428 | 进程 | 185/374 行共享（49.5%）；提权启动、进程查找工具方法沿用 |
| Helpers\Startup.cs | app/Helpers/Startup.cs | 部分 | 待重写 | 19 | 426 | 注册表 | 129/378 行共享（34.1%）；开机自启注册表路径沿用 |
| Battery\BatteryControl.cs | app/Battery/BatteryControl.cs | 部分 | 待重写 | 12 | 202 | 电池/EC | 77/178 行共享（43.3%）；充电阈值逻辑沿用 |
| Display\ScreenBrightness.cs | app/Display/ScreenBrightness.cs | 部分 | 待重写 | 12 | 84 | 显示 | 39/73 行共享（53.4%）；上游 42 行中 39 行被包含（92.9%），本地新增约 31 行。注意：早期清单曾把此文件记为逐字，现文件已含大量本地新增，按当前实测改判为部分 |
| UI\RForm.cs | app/UI/RForm.cs | 部分 | 待重写 | 49 | 154 | 无 | 71/120 行共享（59.2%）；窗体基类样式逻辑沿用 |
| UI\RNumericUpDown.cs | app/UI/RNumericUpDown.cs | 部分 | 待重写 | 6 | 51 | 无 | 上游 20 行全部包含（100%），本地另加约 21 行 |
| UI\RButton.cs | app/UI/RButton.cs | 部分 | 待重写 | 99 | 297 | 无 | 93/262 行共享（35.5%）；是全应用通用按钮控件，调用点最多（99 处） |
| Settings.cs | app/Settings.cs | 部分 | 待重写 | 31 | 5073 | 配置/UI | 上游 1879 行中 1086 行被复用（57.8%）；主窗体骨架、大量控件接线沿用，本地已扩到 5073 行 |
| Program.cs | app/Program.cs | 部分 | 待重写 | 入口（0） | 2063 | 入口 | 上游 428 行中 290 行被复用（67.8%）；启动序列、单实例、异常处理骨架沿用 |
| AsusACPI.cs | app/AsusACPI.cs | 部分 | 待重写 | 108 | 269 | ACPI/WMI/EC | 仅 55 行共享（本地 23.4%，上游 7.3%）；WMI 设备接口与设备 ID 常量沿用，重叠度低 |
| HardwareControl.cs | app/HardwareControl.cs | 部分 | 待重写 | 82 | 177 | EC/硬件 | 仅 26 行共享（17%）；风扇/性能接口签名沿用，重叠度低 |
| Properties\Settings.Designer.cs | app/Properties/Settings.Designer.cs | 部分 | 待重写 | 不适用 | 28 | 无 | 仅 21 行，11 行一致（52.4%）；设计器生成的小文件 |
| Stubs.cs | 无直接对应（签名对齐上游 InputDispatcher.cs / Aura.cs / AutoUpdateControl.cs / PeripheralsProvider.cs / AsusLampArray.cs 等） | 部分 | 待重写 | 50 | 154 | UI/灯效存根 | 本地存根文件，成员签名对齐上游原文件；重写方向是连同调用点一起删除而非重写 |

### 轻微重叠（2 个，基本独立，可选重写）

| 文件 | 上游对应 | 判定 | 当前状态 | 调用点数量 | 规模（行） | 风险 | 备注 |
|---|---|---|---|---|---|---|---|
| UI\RCheckBox.cs | app/UI/RCheckBox.cs | 轻微重叠 | 待重写（低优先） | 25 | 266 | 无 | 仅 19/227 行共享（8.4%），主体已是本地实现 |
| Helpers\Logger.cs | app/Helpers/Logger.cs | 轻微重叠 | 待重写（低优先） | 772 | 240 | 无 | 仅 21/207 行共享（10.1%），主体已是本地实现；调用点极多但共享面极小 |

### 已删除（去派生）

以下文件曾为逐字派生，已从源码树删除（无引用），视为已完成的去派生处理，不计入现存总数：

| 文件 | 上游对应 | 处理方式 |
|---|---|---|
| UI\Slider.cs | app/UI/Slider.cs | 已删除（去派生） |
| UI\NumericUpDownWithUnit.cs | app/UI/NumericUpDownWithUnit.cs | 已删除（去派生） |
| ColorCalibrationForm.cs | 上游对应未核实（文件已删除，无法比对） | 已删除（去派生） |

## 建议重写顺序

按依赖数量与风险升序排列，硬件相关（SMU/EC/显示/GPU）放最后。每阶段一行理由：

1. **第一阶段（纯 UI 小件与工具，无硬件）**：TempHelper → MemoryHelper → DeviceHelper → RestrictedProcessHelper → ToastForm → CustomContextMenu → RTextBox → RBadgeButton → RColorButton → RCheckBox → Logger → RNumericUpDown → RForm → RColorPicker → RComboBox → ControlHelper → DynamicLightingHelper → Startup → DonateControl → ProcessHelper → AppConfig → Stubs → NativeMethods。理由：调用点少、无硬件路径，重写风险最低，适合用来磨合流程和验证协议。
2. **第二阶段（通用控件 RButton，提前做）**：RButton。理由：它是全应用通用按钮控件（99 处调用点），虽然调用点最多，但早重写能解锁后续所有 UI 文件的独立化，避免每个 UI 文件都拖着同一个派生基类；代价是这一步的回归测试面最大，需要集中验证。
3. **第三阶段（模式与资源骨架）**：Modes → ModeControl → Settings.Designer.cs → Resources.Designer.cs → Strings.Designer.cs → Properties\Settings.Designer.cs。理由：模式枚举与资源文件是中等耦合的结构性文件，先于硬件核心处理可以减少后续重写时的接口牵连。
4. **第四阶段（显示族，硬件相关，靠后）**：ScreenBrightness → ScreenNative → DisplayNative → ScreenControl → ScreenCCD → AmdDisplay → OSDBase → HardwareOverlay。理由：显示路径涉及系统级 API 与 CCD/亮度控制，行为等价验证成本高，放在 UI 与模式层稳定之后。
5. **第五阶段（硬件核心，最后）**：CpuInfo → PawnIOWrapper → RyzenSmu → PowerNative → BatteryControl → HardwareControl → AsusACPI → NvidiaSmi → NvmlHelper → IGpuControl → NvidiaGpuControl → GPUModeControl。理由：SMU/EC/ACPI/GPU 直接触碰硬件，重写错误可能导致硬件状态异常，必须放在最后并有充分的行为等价测试兜底。
6. **第六阶段（两个大文件收尾）**：Settings.cs → Program.cs。理由：两者分别是最大的窗体文件与程序入口，依赖前面几乎所有层，最后重写可以把牵连面降到最低。

## 进度统计

| 日期 | 现存派生文件 | 待重写 | 进行中 | 已完成（重写） | 已删除（去派生） | 备注 |
|---|---|---|---|---|---|---|
| 2026-09-14 | 52 | 52 | 0 | 0 | 3 | 台账建立，全量重比对基准提交 |
| （日期） | （总数） | （待重写） | （进行中） | （已完成） | （已删除） | （备注） |

计数口径：现存派生文件 = 逐字 29 + 部分 21 + 轻微重叠 2；已删除（去派生）的 3 个文件单独计数，不计入现存总数。每完成一个文件的重写并通过核对协议，把该文件状态改为"已完成"，并在上表追加一行。

## 无法确定的事项

以下内容本次未能核实，列出以待后续确认，不做猜测：

1. 早期清单的逐行计数口径与本台账不同（例如 Slider.cs 记为约 157/158 行，本台账按物理总行数计），两套数字不能直接对照；早期清单数据未存档，无法逐项复核。
2. ScreenBrightness.cs、PawnIOWrapper.cs、RColorPicker.cs、ScreenCCD.cs、HardwareOverlay.cs 等文件与早期"逐字"判定存在小幅出入（实测共享行 53% 至 90% 不等），无法确定是早期清单口径不同，还是这些文件在早期清单之后被本地修改过。
3. AsusACPI.cs、HardwareControl.cs、Program.cs、Settings.cs 这四个低重叠部分派生文件中，具体哪些上游方法/区域仍然存活，需要逐方法人工 diff 才能定位，本次只给出共享行比例。
4. 调用点数量是类名出现次数的近似值：Program.cs 的原始命中（544 处）大部分是 "Program Files" 之类的路径字符串，已按入口文件记为 0；OSDBase.cs 的子类继承关系未完全追踪，其真实依赖面可能略高于表中数字。
5. ColorCalibrationForm.cs 的上游对应文件无法核实（文件已删除，且上游对应关系未在输入信息中给出）。
6. Stubs.cs 中各存根类型（InputDispatcher、Aura、PeripheralsProvider 等）的调用点分布在哪些具体路径，未逐一追踪；其备注中"连同调用点一起删除"的工作量无法从本台账直接得出。
7. 基准提交 `2ea4c5c` 是否与早期清单使用的提交完全一致：本台账按任务给定的提交执行，未做额外确认。
