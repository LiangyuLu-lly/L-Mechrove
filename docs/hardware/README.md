# 机械革命/同方 硬件映射表

这个目录是**全机型硬件适配的数据底座**，也是将来脱离厂商 GCU 服务、直连 EC 的前置资料。
所有内容由 `scripts/Extract-HardwareMap.ps1` 从本仓库内已有的三个官方控制台离线提取，
不依赖联网，也不依赖访问设备。

重新生成：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Extract-HardwareMap.ps1
```

## 为什么这些数据能被提取出来

官方 `GCUService.exe` 被 ConfuserEx 的 anti-tamper 处理过，**4,574 处方法体全部被替换成
`throw new Exception("Runtime exception")`**。但混淆只毁掉了方法体——`const`、`enum`、
`struct` 的**声明原样保留**。因此：

- **拿得到**：EC 寄存器名与地址、机型枚举、GPU SKU 枚举、状态/控制字节的位定义、
  功耗表结构布局、IOCTL 编号。
- **拿不到**：选择逻辑。即"这台机器该用哪个地址含义"、"写入前要不要先解锁"、
  "写完要不要发确认命令"——这些都在被销毁的方法体里。

这一条决定了后面所有工作的性质：**不是逆向未知协议，而是给已知的表消歧。**

## 文件清单

| 文件 | 内容 | 来源 |
|---|---|---|
| `ec-registers.json` | 258 个 EC 寄存器槽（含所属 spec 类、地址、别名、歧义状态）、33 个 IOCTL、387 个结构字段 | `_decompiled/GCUService/GCUService.decompiled.cs` |
| `project-ids.json` | `ProjectID` 45 项（含族/变体树）、`BIOS_PROJECT_ID` 37 项、`GN20_GPU_SKU` 9 项、`GN21_GPU_SKU` 19 项 | 同上 |
| `ec-bit-flags.json` | 10 个位标志枚举，给出 EC 状态/控制字节的逐位含义 | 同上 |
| `keyboard-zones.json` | 22 种键盘/灯带分区布局，标注各控制台版本是否包含 | `ControlCenter_*/RGBKeyboard.reg` |
| `fan-table-defaults.json` | 24 个机型 × 6 张出厂风扇表（16 点升温/回落/占空比 + PL1/PL2 + DC 版本 + TCC） | `ControlCenter_*/UserFanTables/` |

三个数据源版本：`5.56.60.26`、`5.17.51.34`、`5.17.49.19`。三者的 `UserFanTables` 都覆盖同样的
24 个机型；键盘分区上 `5.17.49.19` 只有 18 种，另两个各 22 种（新增的是 `MEZone_3p1nd_*` 系列）。

## 机型标识：两套 ID，不要混用

**`ProjectID`** 是机箱/项目代号，采用**族 + 变体**编码：族取小整数，变体是 `(族 << 8) | 序号`。

```
PHxAxxx = 23 (0x17)  ->  PH4ARxx=0x1701  PH4AUxx=0x1702  PH4AXxx=0x1703
                         PH6AQxx=0x1704  PH6ARxx=0x1705  PH6AGxx=0x1706
                         PH4AUxf=0x1707
PHxPxxx = 24 (0x18)  ->  PH4PRxx=0x1801  PH4PUxx=0x1802  PH4PGx1=0x1803
                         PH4PGx2=0x1804  PH6PRxx=0x1805  PH6PGEx=0x1806
                         PH6PG0x=0x1807  PH6PG3x=0x1808  PH6PG7x=0x1809
                         PH4AQE3=0x180A  PH6PG0x150W=0x180B
                         PH6PG3x150W=0x180C  PH6PG7x150W=0x180D
```

独立项目（无变体）：`PH4TRX1=18`、`PH4TUX1=19`、`PH4TQx1=20`、`PH6TRX1=21`、`PH6TQxx=22`，
以及一批旧世代：`GI/GJ/GK/GICN/GJCN/GK5CN_X/GK7CN_S/GK7CPCS_GK5CQ7Z/PF/GK5CP_4X_5X_6X/
IDP/IDY_6Y/IDY_7Y/PF4MU_PF4MN_PF5MU/CML_Gaming/GK7NXXR/GM5MU1Y`。

**`BIOS_PROJECT_ID`** 是 BIOS 里写死的短代号（`IDR/IDX/IDV/IDO/IDP/IDS/IDY/...`共 37 项），
可以直接从注册表 `HKLM\SOFTWARE\OEM\GamingCenter2\ItemSupport\BIOS_PROJECT_ID` 读到，
**这是运行时唯一可靠的机型判据**。开发机读到的是 `IDY`。

`ProjectID` 与 `BIOS_PROJECT_ID` 之间的映射关系存在于被销毁的方法体里，**尚未建立**。
`UserFanTables` 的目录名用的是 `ProjectID` 风格代号，所以两套 ID 的对应表是后续需要补的一环。

## 直连硬件的访问通路（已实测）

厂商已经提供了全部必要条件，**不需要自己写或签内核驱动**：

```
驱动    C:\Windows\System32\drivers\UWACPIDriver.sys   46,352 B
服务    UWACPIDriver   Start=3(按需)  Type=1(内核驱动)
签名    CN=Microsoft Windows Hardware Compatibility Publisher   (WHQL)
签发    Microsoft Windows Third Party Component CA 2012
设备    \\.\ACPIDriver
```

`ec-registers.json` 里的 33 个 IOCTL 覆盖：端口级 `READ/WRITE_PORT_UCHAR/USHORT/ULONG`，
ACPI 级 `CM/EC/MM/PE/IO/INIO/TMP` 各自的读写，以及 `SMAPCTABLE`、`CUSTOMCTL`。
读取实现已在 `src/Probe/EcProbe.cs`（`IOCTL_GPD_ACPI_ECREAD = 2621482120`）。

功耗/温度墙有结构化通路，布局完整可读：

```csharp
struct SMAPCTABLE_STRUCT {
    byte PL1, PL2, PL4, TccOffset, ConfigurableTGP,
         DynamicBoost, TargetTemperature, DefaultTGP, DynamicBoostCpuTdp;
}
```

### 两个必须知道的风险

1. **该驱动允许任意非管理员进程读写。** 实测在普通用户权限下以 `GENERIC_READ|GENERIC_WRITE`
   打开 `\\.\ACPIDriver` 成功。配合 `WRITE_PORT_*` 这就是任意 I/O 端口写入，属于典型的本地提权
   原语。这是厂商侧的问题，但如果本项目主动依赖它，等于把这个漏洞变成产品运行的必要条件。

2. **驱动签名证书已于 2026-02-19 过期。** 内核驱动校验签名时间戳而非当前有效期，所以现有驱动仍能
   加载；但厂商不会再用这张证书发新版本，一旦 Windows 收紧策略，整条通路会一起失效。

## 验证状态

**这批数据目前是"声明级可信、抽样级验证"。** 已做的验证是只读 EC 采样 + 与 MQTT 上报值交叉比对。
写入方面：2026-09-11 起充电阈值 `0x7B9/0x7D0`（上限/复充下限）与模式位 `DBAP(0x7A6)` 有写入实测记录
（见"充电上限"一节），其余寄存器**仍然只读、未做过写入验证**。

| 寄存器 | 地址 | 实测读值 | 交叉验证 | 结论 |
|---|---|---|---|---|
| `ecBt1RSOC` | `0x4AB` | 100 | 应用显示电池 100% | ✅ 一致 |
| `ADDR_PL1_SETTING_VALUE` | `0x783` | 210 | `Fan/Status` 报 PL1=210（Max 210） | ✅ 一致 |
| `ADDR_PL2_SETTING_VALUE` | `0x784` | 210 | 同上 | ✅ 一致 |
| `ADDR_PL4_SETTING_VALUE` | `0x785` | 210 | 同上 | ✅ 一致 |
| `ADDR_EC_MAIN_FAN_RPM_BYTE1/2` | `0x464/0x465` | 0x09/0x44 → 2372 | 量级合理，未精确对表 | 🟡 待精确核对 |
| `ADDR_ConfigurableTGP_VALUE` | `0x744` | 70 | `Fan/Status` 报 TGP=150 | ❌ **不一致，见下** |
| `AP_SSDTempr` | `0x7D1` | 0 | — | 🟡 本机未填充或含义不同 |
| `ecBt1Temperature` | `0x4A2` | 8 | — | 🟡 需换算，不是直接摄氏度 |
| `ecPowSource` | `0x490` | 7 | 位域，AC 在位 | 🟡 位含义待对 `ec-bit-flags.json` |

`0x744` 的不一致正是歧义问题的现场证据：该地址同时被声明为 `ADDR_ConfigurableTGP_VALUE` 和
`ADDR_MYFAN2_L2_PWM`。本机读到 70 而 TGP 实际是 150，说明在 `BIOS_PROJECT_ID=IDY` 上
这个地址**不是** TGP。**照着常量名直接写会写错寄存器。**

## 内存风扇 1.5（`RamFan1p5Support`）没有 MQTT 通路

排查功能缺口时确认了一条边界，记在这里免得后来再找一遍：

`RamFan1p5Support` **不是**一个用户可调项，而是 GCUService 内部选择 **EC 寄存器布局变体**的开关。
证据有三条：

1. `RamFan1p5_ECSpec` 相比 `RamFan1_ECSpec` 只多三个寄存器
   —— `ADDR_RAMFAN1P5_TABLE_STATUS1 = 0xF5D`、`STATUS2 = 0xF5E`、`TABLE_CTRL = 0xF5F`，
   其余六个风扇表基址完全相同。它描述的是"这台机器的风扇表怎么排"。
2. 官方 UI（`CCUWinUI`）里 `MyRamFan1p5` / `RamFanMode1p5` 是**混淆器改名后的
   `Fan/Status` DTO 类与动作枚举**，不是内存风扇功能对象。`RamFanMode1p5.SET_OPERATING_MODE_DETAIL`
   这种写法就是证据：它是所有自定义模式参数共用的那个动作。
3. 遍历 `CCUWinUI` 全部 `SET_OPERATING_MODE_DETAIL` 载荷，字段只有
   `PL1/PL2/PL4`、`CpuTccOffset(Switch)`、`CpuAmdSPL/SPPT/FPPT/CoreFreqOC/CoreVoltageOC/TccTarget`、
   `GpuConfigurableTGPTarget`、`GpuDynamicBoost(Switch)`、`GpuCoreClockOffsetOC`、
   `GpuMemoryClockOffsetOC`、`OverClockingSwitch`、`CPUPerformanceAndOverClockMenuSwitch_ON/OFF`、
   `FanSwitchSpeed`、`FanSwitchSpeedEnabled`。**没有任何内存风扇字段。**

结论：内存风扇的调速只能走 EC 直连，属于本文档"下一步"里的阶段 2，
在阶段 0（全量只读快照）与阶段 1（双写对照）完成前不实现。

**这颗风扇的转速与占空比也读不到。** 这一点起初判断错了，写过"已经通过
`System/FanInfo` 的第三风扇字段接上了"——那句话是错的，依据如下：

| 检查项 | 结果 |
| --- | --- |
| 官方 `System/FanInfo` 处理逻辑读哪些字段 | 只有 `CpuFanDuty`、`GpuFanDuty`、`CpuFanRpm`、`GpuFanRpm` 四个 |
| 官方 58 个 MQTT 主题里与风扇有关的 | 只有 `System/FanInfo` 与 `System/FanErrorInfo` |
| 官方界面显示几颗风扇 | 两颗（界面全机型共用，所以没有机型会报第三颗） |
| `RamFan1p5_ECSpec` 里的内存风扇寄存器 | 只有 `TABLE_STATUS1`(`0xF5D`)、`TABLE_STATUS2`(`0xF5E`)、`TABLE_CTRL`(`0xF5F`)，**全是风扇表寄存器，没有转速/占空比** |
| 内存风扇由谁驱动 | GCUService 内部随风扇表自动管理（`MyFanTableCtrl.SetFanControlByRamFan1p5`、`LiquidHWOC.RamFan1P5SetMode`），官方界面从不下发任何 RamFan 字段 |

顺带纠正两个容易混淆的命名：

- **`RamFan2_ECSpec` 的 `TABLE_F1/F2/F3` 是三套风扇表，不是三颗风扇。**
  每套表里都有 CPU 段和 GPU 段（`TABLE_Fn_CPU_TEMP_UP0` / `TABLE_Fn_GPU_TEMP_UP0`），
  也就是两颗风扇 × 三套曲线。
- **`ADDR_MYFAN3_*` 里的 "MyFan3" 是风扇管理代次**（MyFan1 / MyFan2 / MyFan3），
  不是第三颗风扇。整个 `MYFAN3` 前缀下只有 `CPU_TAU`(`0x738`) 与 `GPU_SETTING`(`0x78B`) 两项。

因此「中置风扇」（`HardwareControl.midFan`）是从 g-helper 继承的华硕概念，
在这套协议上没有对应物：`MidFanSeen` 恒 `false`、`midFan` 恒 `null`、
`AsusACPI.IsMidFanSupported()` 恒 `false`。
要让这块读数活起来，前提是先拿到**真实存在的**字段名或 EC 寄存器。

同一批排查里还发现两个官方从不下发、因此我们也不实现的字段：
`FanSettings.SkipSafetyAbnormalProtection`（跳过风扇异常保护）与 `FAN_SafetyProtectNotify`。
前者是安全保护旁路，官方 UI 没有任何入口去写它。

## 显示色彩模式与 `NV_CTRL_PANEL`：协议里有，官方 5.56 已弃用

GCUService 里有一整套显示色彩模式命令，`Setting/Status` 也有对应的十九个字段。
但**官方 5.56 客户端一条都不发、一个都不读**：

| 检查项 | 结果 |
| --- | --- |
| `DISPLAY_STANDARD_MODE` / `_GAMING_` / `_VIDEO_` / `_READ_` / `_CUSTOMIZED_` | 只在动作枚举声明里，**零发送点** |
| `DISPLAY_*_MODE_VALUE_SAVE` | 同上，**零发送点** |
| `NV_CTRL_PANEL_AUTOSELECT` / `_HIGHPERFORMANCE` | 同上，**零发送点** |
| `DisplayMode` / `GamingBrightness` / `CutomizedRed` / `ReadColorTemp` … | **零读取点**（唯一的 `.DisplayMode` 命中是 WinUI 的 `NavigationView.DisplayMode`，与本协议无关） |
| `DISPLAY_FEATURE_STATUS_ON` | **有 5 处发送点**，全都在 ICC 校色流程里——这一条我们已经在用 |

两个命名陷阱记在这里：

1. **状态字段与命令名拼写不一致。** 状态里是 `Cutomized*`（少一个 s），命令是 `Customized_*`。
   照命令名去读状态会一个都读不到。
2. **两个二进制里的动作名也不一致。** GCUService 是 `DISPLAY_GAMING_MODE_VALUE`，
   CCUWinUI 的枚举是 `DISPLAY_GAMING_MODE_VALUE_SAVE`。既然两边都没有发送点，
   到底哪个是服务端真正接受的名字无从判断。

所以这一族**只解析、不下发**（`MechrevoHw.DisplayColorMode` / `DisplayFeatureOn` /
`NvControlPanelPreference` / `DisplayColorParameters`）。下发缺三个前提：
没有参考载荷、动作名有歧义、`_VALUE` 命令的载荷形状未知（六个参数是一条发完还是逐条发）。

`NV_CTRL_PANEL_*`（NVIDIA 全局首选显卡）另有一条不做的理由：这台机器的显卡模式
已经由本程序自己的 Eco / 标准 / 独显直连管理，再叠一层全局首选项会让两边状态互相打架。
读出来展示是有价值的（排查显卡模式问题时的诊断信息），写就不必了。

各模式的参数集是**不对称**的，这是官方的分法而非遗漏：

| 模式 | 参数 |
| --- | --- |
| 游戏 | 亮度、R、G、B、色温、对比度 |
| 影音 | 亮度、色温 |
| 护眼 | 亮度、蓝光、色温 |
| 自定义 | 亮度、R、G、B、色温、对比度 |

出厂默认值（GCUService 常量）：对比度 50、色温 4200 K、RGB 各 128。

## 充电阈值：写 `0x7B9`(上限) + `0x7D0`(复充下限) 这一对（2026-09-11 两次真机迭代定案）

**结论先行**：真正生效的是**官方服务常量里的那一对**——`ADDR_BATTERY_CHARGE_LIMIT_UP = 1977 = 0x7B9`
（充到该值停止）与 `ADDR_BATTERY_CHARGE_LIMIT_DOWN = 2000 = 0x7D0`（掉到该值以下才恢复充电）。

| 环节 | 实测结果 |
| --- | --- |
| 官方 UI 下发 | `BatteryProtection/Control` + `PERFORMANCEDMODE`/`BALANCEDMODE`/`HEALTHYMODE`（`CCUWinUI:73996-74022`，与我们原实现逐字一致） |
| GCU 实际写 EC | 只写模式位 **`DBAP` @ `0x7A6`**（性能 `0x08` / 平衡 `0x18` / 健康 `0x28`）。该字节的位含义随机型而变，社区工具明确注明"暂不修改" |
| 阈值寄存器 | 三档切换全程**不动** `0x7B9/0x7D0` |

**一次返工（教训）**：初版按本机 DSDT 的 `ECMG` 字段表写了 `CGLM @ 0x78F`（字段名就是
Charge LiMit、8 位单字节、可写、回读一致），但**它不控制充电**——用户真机实测：设 80% 后电量
一路涨到 86%；把上限抬到 100 或降回 86 都不改变充电状态（EC `0x4AB` 电量寄存器与 Windows
百分比同源同单位，排除单位错位）。**"能写能回读" ≠ "功能生效"**，寄存器类改动必须验效果。

**旁证（社区项目）**：`CR1MSEN/MechrevoBatteryManager`（.NET Framework，同方模具，作者称在
翼龙/旷世真机通过）**只写 `0x7B9` + `0x7D0` 两个地址**，强制 `0 ≤ 下限 < 上限 ≤ 100`，
写前读原值、写后回读，并明确不碰 `0x07A6`；其 README 称生效后 **Windows 电池图标会变为
"智能充电"**。它通过厂商 `ACPIDriverDll.dll`（`…\OEM\…\AiStoneService\MyControlCenter\`）的导出
`ReadEC/WriteEC` 访问，我们走 `\\.\ACPIDriver` + IOCTL——同一个 EC 地址空间（我们的读数与
官方服务常量逐条吻合）。

**EC 直写通路（含驱动反汇编）**：厂商驱动 `\\.\ACPIDriver`（UWACPIDriver.sys）

| 操作 | IOCTL | 入参（唯一正确布局） |
| --- | --- | --- |
| 读 | `IOCTL_GPD_ACPI_ECREAD` = `0x9C40A488` | `[u32 地址]`（4 B） |
| 写 | `IOCTL_GPD_ACPI_ECWRITE` = `0x9C40A48C` | **`[u32 地址][u8 值]`（5 B）** |

写处理器（`0x1400025B8`）分两次 memcpy（4 B + 1 B）把参数送进 ACPI 方法 `ECRW`；
3/4 字节布局会被错位解析成 `0x5007B9` 这类地址——**`DeviceIoControl` 仍返回成功、寄存器纹丝不动**，
是最容易误判成"写进去了"的坑。出参缓冲还必须非空，否则直接 `ERROR_INVALID_PARAMETER(87)`。

**实测写入**：阈值寄存器 `0x7B9`/`0x7D0` 写 `0x64/0x5F/0x50/0x3C/0x28/0x14/0x00` 全部原值读回（不钳位），
模式位寄存器 `0x7A6` 同样可写可读。

**产品实现**（用户 2026-09-11 决策）：

- 充电阈值**只走 EC 直写**（`Hardware/EcChargeLimit.cs`：写上限 `0x7B9` + 复充下限 `0x7D0` 一对，
  100% 档写 `0/0` = 出厂无上限，下限 = 上限 − 5% 迟滞；写入或回读不符时回滚并如实弹回，不谎报）；
- **官方三档流程整体摘除**（`BatteryControl.ApplyBatteryProtection` 与其调用点删除；
  `MechrevoHw.SetBatteryProtection` 作为协议方法保留，仅诊断使用）；
- 界面恢复**连续滑条 40..100%，步长 1**（`Slider.Value` 按 `Step` 取整，Designer 原来是 5，
  会停在 5 的倍数上；真机点击实测 73% / 54% 原值进 EC）；
- **机型门禁**：EC 字段布局随机型而变，只有实测过的机型（`EcChargeLimit.IsSupportedMachine`，
  本机 `YAOSHI`）允许写；其它机型记一行日志后忽略，配置项 `ec_charge_limit=1/0` 可强制开关。

**风险（必须记住）**：这条通路依赖厂商驱动——`\\.\ACPIDriver` 允许**任意非管理员进程读写**
（"直连硬件的访问通路"一节记过它是本地提权原语），且签名证书已于 2026-02-19 过期；
一旦 Windows 收紧或厂商换驱动，整条通路失效（此时充电上限功能会退化为"忽略请求 + 记日志"）。

## 字段名交叉比对：一次抓出八处「代码里的名字与服务端发的名字对不上」

这是 beta11 全轮审计里最有效的一步，方法值得固化下来。

前提是先把**真机原始报文**转储出来（`FunctionVerifier` 的 `--verify-functions` 会在报告里
附带 `Fan/Status` / `Setting/Status` / `GPUDevice/Status` / `Keyboard/Status` /
`Settings/DeviceSwitchItemStatus` 的完整原始载荷）。有了它就可以做一件之前做不到的事：
把 `MechrevoHw` 各 `case` 块里引用的每一个字符串字面量，与实测载荷的字段名做集合比对，
输出两份差集——「代码引用了但载荷里没有」和「载荷里有但代码从没引用」。

这两份差集各有各的用处：

- **第一份找拼错。** 注意有噪声：多写法容错的别名列表（`OptionalInt(o, x, "A", "B", "C")`）
  会把没命中的别名也报进来，那是设计如此。真正的问题是**没有别名兜底的单名引用**落在差集里。
- **第二份找漏实现。** 服务端一直在发、我们从来没读的字段。`BatteryLogo_Status`
  就是这么找出来的。

抓到的八处偏差（全部已修）：

| 我们写的 | 服务端实际发的 | 后果 |
| --- | --- | --- |
| `CPUPerformanceAndOverClockMenuSwitch` | `CPU_PerformanceAndOverClockMenuSwitch` | 能力判定恒假，整项界面上从未出现 |
| `PowerLightSwitch` 用布尔解析器读 | 值是状态串 `PowerLight_ON` | 开关恒显示为关 |
| `USB_CHARGER_STATUS_ON` 精确匹配 | 官方判据是 `!Contains("OFF")` | 固件换写法就永久判成关 |
| `WINKEY_STATUS_LOCK` / `FNKEY_LOCK` / `NUMPAD_LOCK` 精确匹配 | 官方判据是 `!Contains("UNLOCK")` | 同上 |
| `OSD_HIDDEN_ON` 精确匹配 | 官方判据是 `!Contains("OFF")` | 同上 |
| `LCDOverdriveSupport` 从不读 | 实测 `NotSupport` | 暴露一个本机不具备的入口 |
| `BatteryLogo_Status` 从不读 | 实测 `BATTERYLOGO_TOGGLE_OFF` | 整个功能缺失 |
| `GameWhitelistSwitch` 当成 Action 后缀 | 值在载荷字段里、主题是 `Fan/Control` | 点了没反应 |

三个容易踩的命名陷阱（写测试时又踩了一次）：

1. `Settings/DeviceSwitchItemStatus` 的字段名是 `TochpadEnable`（**少一个 u**）、
   `WIFIEnable`、`BTEnable`（全大写缩写）。写成「看起来对」的 `TouchPadEnable` /
   `WifiEnable` 不会报错，只会让那几项被静默跳过——测试照样绿，但什么都没验证。
2. 灯带的**回读**键是 `powerStatus`（驼峰，值 `"On"`/`"Off"`），**下发**键是
   `powerstatus`（全小写，值数字 1/0）。这是两个不同的键，同一条报文里可能都在
   （所以 `ConvertFrom-Json` 会因重复键报错）。
3. `Fan/Status` 里带 `CPU_` 前缀的字段有 21 个，但 `CPU_PerformanceAndOverClockMenuSwitch`
   在服务端内部结构体里叫的是**没有下划线**的 `CPUPerformanceAndOverClockMenuSwitch`。
   反编译源里两个名字都能搜到，只有实测载荷能判断哪个是真的发出来的。

## `GPU_WhisperModeSwitch` / `_Setting`：官方只有属性声明，没有下发点

`Fan/Status` 把这一族**完整**上报（开发机实测）：

```
GPU_WhisperModeSupport=True     GPU_WhisperModeSwitch=0        GPU_WhisperModeSetting=0
GPU_WhisperModeMinFps_QUIETER=30   _QUIET=40   _BALANCED=60
GPU_WhisperModeMinFpsMaximum=60    Minimum=30
```

看着像个能做的功能，但 `CCUWinUI` 里 `GpuWhisperModeSwitch` 与 `GpuWhisperModeSetting`
**只有属性声明与数据绑定，零发送点**——拿不到命令的真实形状。

按枚举名猜过一个形状（`Fan/Control` + `SET_OPERATING_MODE_DETAIL` + `GPU_WhisperModeSetting`），
真机验证的结论是**下发后回读毫无变化**。而且那次实现还把两个字段搞混了：
`Switch` 是开关、`Setting` 是静音档位（对应上面三档 `MinFps`），不是一回事。

所以这一族**只解析、不下发**（`MechrevoHw.WhisperMode` / `WhisperModeLevel` /
`SupportsWhisperMode`），界面入口已撤。`SupportsWhisperMode` 为真只表示「能读到这一族状态」，
不表示我们能写。

这是同一个坑踩的第二次（第一次是第三颗风扇）。纪律：**按枚举名猜命令形状，两次都被真机证伪。**

## `GPU_POWERSAVEINGMODE`：命令是对的，故意不接界面

官方下发点确认存在（`CCUWinUI` 的 GPU 设置页「应用」按钮，`Setting/Control` + 单个 Action、
无额外字段），我们的 `MechrevoService.SwitchGpuPowerSaving` 与它逐字一致。

不接界面的理由：它是一次性动作、**没有对应的状态字段可回读**，而效果是触发一次独显工作模式
切换——与本程序已有的显卡模式面板语义重叠。多一个按钮只会让用户在两处切显卡，
而显卡切换失败可能连显示输出都没有。方法保留供诊断与将来接线。

同一族的另外两条**已经接上界面**，因为它们是有状态可回读的布尔开关：

| 命令 | 载荷 | 状态字段（`GPUDevice/Status`） |
| --- | --- | --- |
| `GPU_DISCONNECTMONITOR` | `Enable`（布尔） | `DisconnectMonitor` |
| `GPU_DC_ONCE` | `Enable`（布尔） | `DC_Once` |
| `GPU_DC_HZ` | `Enable`（布尔） | `DC_HZ` |

## 切模式必须同时发 `LCHWOC/Control` 的运行标记

官方每次切模式发**两条**命令，顺序固定：

| 模式 | `Fan/Control` | 紧跟 `LCHWOC/Control` |
| --- | --- | --- |
| 游戏 | `OPERATING_GAMING_MODE`, `ProfileIndex=0` | `IsNormalRun=1` |
| 增强 | `OPERATING_TURBO_MODE`, `ProfileIndex=0` | `IsNormalRun=2` |
| 办公 | `OPERATING_OFFICE_MODE`, `ProfileIndex=0` | `IsNormalRun=0` |
| 自定义 | `OPERATING_CUSTOM_MODE`, `ProfileIndex=<档号>` | `IsCustomRun=true` |

两个类型细节：

- `ProfileIndex` 是 **JSON 数字**（官方 ViewModel 里 `CustomProfileIndex` 是 `int`）。
  注意状态 DTO 里接收它用的是 `string`——收发类型不同。
- 刷新率的 `Hz` 相反，官方发的是**字符串**（下发命令的参数类型就是 `string?`）。

只发第一条会留下一个隐蔽后果：从自定义模式切回普通模式时，超频通道还留在
「自定义运行」状态，硬件那边的超频参数不复位。

## `System_ON` / `System_OFF` 是遥测推流开关

不是「系统开关」。官方的用法：

- 窗口激活、5 秒定时器 tick、导航到需要传感器的页面 → 发 `System_ON`
- 窗口失活（`Deactivated`）、窗口关闭、`Cleanup()`、导航离开 → 发 `System_OFF`（共 7 处）

服务端收到 `OFF` 后停止采集与推送。本程序常驻托盘，窗口失活时**不该**停推流（托盘还要显示
温度），但**进程退出前必须发一次**——否则进程都结束了，服务端还在按 5 秒周期采集。

## MQTT 连接与状态解析的几条硬约束

这些是 beta13 那轮底层审计的结论。它们的共同点是**违反了也不会立刻出错**，
只在特定时序或特定固件写法下暴露，所以值得单独记下来。

### 连接层

| 约束 | 为什么 |
| --- | --- |
| 订阅必须全部 `await` 完才能发 GETSTATUS | `SubscribeAsync` 的 Task 完成意味着已收到 SUBACK。改成 fire-and-forget 的话首帧状态会静默丢失，而症状是间歇性的 |
| 必须检查 SUBACK 的每个结果码 | 失败码（`0x80`）不会让 `SubscribeAsync` 抛异常。broker 做 ACL 拒绝时会静默降级成「连上了但那类状态永远收不到」 |
| `System_ON` 排在握手**第一条** | 它才是让 GCU 打开周期性传感器推送的开关。排在末尾的话前面那批 GETSTATUS 是在采集模块可能还没启动时发的 |
| `KeepAlive` 必须显式设置且短于发布超时 | `IsConnected` 只在收到 FIN/RST 或 keepalive 超时时翻转。默认值下，GCUService 被冻结但 socket 没关时有十几到二十几秒的假连接窗口 |
| 短命辅助进程必须用不同的 `client id` | 按 MQTT 3.1.1 §3.1.4，broker 收到同 id 的新连接必须踢掉已有会话 → 两个进程互踢 |
| 退出前的清理命令用 QoS0 | QoS2 要四步握手，而退出只等几百毫秒；`CleanSession=true` 让未完成的 PUBREL 被永久丢弃 |
| `Dispose` 要先请下 `ConnectAsync` | 否则退出时正好在重连中，会一边订阅一边被拆掉 client，最坏情况是发出 `System_ON` 之后进程才退出 |

两个**看起来可疑但正确**的地方，记下来免得重复审：

- `BatteryProtection/Control` 发到一个没订阅的主题不是遗漏——回读走 `System/BatteryProtection`，
  被 `System/#` 覆盖。请求主题和回应主题不在同一族。
- 退出路径上的 `.Wait(700ms)` 不会死锁，虽然形状像经典的 sync-over-async。
  因为发布路径全程 `ConfigureAwait(false)`，没有捕获界面线程的同步上下文。
  **将来谁去掉某个 `ConfigureAwait(false)` 就会变成挂死。**

### 状态解析

三类问题反复出现过，每类都有正确样板可抄：

1. **`*Seen` 只在真解析出值时置位**。这些标志决定界面是否暴露入口。
   按「字段存在」置位的话，固件写了个没见过的记法时值会静默变成 false 而标志照样置位——
   界面上就是一个点了没反应的开关，确认逻辑也永远等不到回读变化。
   正确样板：`if (OptionalBool(o, key) is bool value) { ...; XxxSeen = true; }`

2. **只 latch 可用值**。大量字段用「没报就保持旧值」的解析（`OptionalInt(o, 上次的值, key)`），
   这对部分帧是必要的，但 0 一旦被记住就再也回不到「未知」，而下游的 `>= 0`
   守卫会让它通过。真实的功耗墙/TGP/转速不可能是 0。
   正确样板：`OptionalUsablePowerValue` / `FirstUsablePowerValue`（只接受 `> 0`）

3. **状态串判定要跟官方逐字对齐，并统一忽略大小写**。官方读的是「串里有没有那个否定词」
   （`!Contains("OFF")` / `!Contains("UNLOCK")`），不是拿整串比常量。
   例外是局部调光与屏幕响应加速——那两个动作名本身不含否定词，官方也是精确匹配。

### 版本号与事件

- **每个 case 只能有一个 release 点**：版本号必须在该 case 的所有字段赋值完成之后才自增。
  在中段自增的话，按版本号轮询的调用方会看到「新版本号 + 旧字段值」，
  表现为「明明落地了却确认失败」的偶发误判。
- **事件必须逐订阅者隔离，而且放在 case 末尾**。放中段的话订阅者抛异常会把后面所有字段
  与三个尾部通知一起丢掉，而那些通知是所有 `WaitForStateAsync` 的唯一唤醒来源——
  一次异常等于本轮所有确认全部超时。
- **解析路径上不要做阻塞 I/O**。`Setting/Status` 里读注册表那处必须单独包 try：
  它跑在 MQTT 接收线程上，位置又在中段，一抛就丢整帧。

### 同一概念只能有一份映射

这一条是 beta13 抓到的最典型的缺陷：灯带主题到状态键的映射在服务层写了两份，
`SupportsLightTopic` 正确区分四条灯带，而 `SetLightPower` 的确认只区分 Logo、
把铰链与同步都算成主灯带。于是开关铰链灯带时确认看的是主灯带的状态——
命令生效了、确认失败、界面回滚勾选，用户看到「点了跳回去」。

判定顺序也有个隐含依赖：三条子灯带的主题都以 `HidLightbar_` 开头，
所以必须**先判子灯带、最后才能落到主灯带**。

同类的历史案例还有：`MechrevoHw` 上曾有一整套与服务层平行的下发方法（只发不确认），
以及两份分叉的握手序列。判断标准很简单——同一件事有两处实现时，
迟早会有人只改一处。

## 继承自 g-helper 的华硕专有硬件：哪些不存在，哪些名字像但其实是别的

本项目 fork 自 g-helper（华硕控制中心），所以代码里长期带着一批华硕专有硬件的实现。
beta12 把它们清掉了，这里记录判断依据——**关键是「名字像华硕」不等于「是华硕的死代码」**。

### 机械革命全系不存在的（已删除）

| 族 | 华硕硬件 | 删除前为什么不可达 |
| --- | --- | --- |
| `AllyControl` / `Handheld` | ROG Ally 掌机 | 宿主面板 `panelAlly` 没被 Add 到窗体；判据 `ContainsModel("RC7")` |
| `AniMatrixControl` / `Matrix` / `SlashDevice` | 机盖点阵屏、Slash 灯条 | `InitMatrix` 首句 `if (!matrixControl.IsValid)` 就返回（存根恒 false） |
| `XGM` | XG Mobile 外置显卡坞 | `IsXGConnected()` 恒 false；`DeviceGet(GPUXG)` 恒 -1；`DeviceSet` 是 `=> 0` |
| `VisualControl` / `ColorProfileHelper` | Splendid（GameVisual）色域引擎 | 唯一执行入口 `AsusSplendid.exe` 只能经 ATK 驱动定位，ICC 只在 ASUS 目录下 |
| 机背灯（rear glow） | ROG Flow Z13 | `InitRearLight` 零调用方，门禁 `HasRearLight()` 就是 `IsZ13()` |

一个顺带发现值得记：`SettingsForm` 只把 **6 个**面板 Add 到窗体上
（性能 / GPU / 电池 / 版本 / 页脚 / 启动）。`panelMatrix`、`panelAlly`、`panelScreen`、
`panelKeyboard`、`panelGamma`、`panelRearLight`、`panelPeripherals` 全都没有被 Add，
所以那个 `legacy.Visible = false` 的隐藏循环是在给不在控件树里的控件设可见性。
这也是删掉两棵面板树之后 UI 审计截图基线一张不差的原因。

### 名字像华硕、实际是活代码（**不要按关键字删**）

| 符号 | 实际含义 | 误删后果 |
| --- | --- | --- |
| `RyzenSmu` 里的 `StrixPoint` / `StrixHalo` | **AMD Zen5 代号**（Ryzen AI 9 / Ryzen AI MAX），SMU 信箱路由 | 打断 AMD 机型的 TDP 与曲线调压 |
| `KeyboardRgb.ModeMatrix` / `MatrixSpeed` | 机械革命键盘 RGB 的固件灯效档位 | 键盘灯效少一档 |
| `AsusACPI` 类本身 | 活的适配层：把继承来的界面调用转成 `MechrevoHw` 操作 | 风扇曲线读写失效 |
| `AsusFan.CPU` / `.GPU` | 风扇曲线映射的入参 | 同上 |

`IsXGConnected()` 保留并恒 false，是**有意的显式否认**：机械革命全系没有这个接口，
恒 false 就是正确答案，删掉会让调用方失去语义锚点。同理 `AsusFan.Mid` 保留但恒不可用。

还有一处概念混淆：被删掉的 `sliderGamma` 调的是 Splendid 的 gamma **假调光**，
不是屏幕亮度。真实亮度走 `Settings/DeviceSwitchItemStatus` 的 `ScreenBrightness`
加 `BrightnessCommitQueue`，两者无关。

### 两个判据换成配置开关，而不是删掉

这两处的**功能语义**对机械革命有意义，只是判据是华硕专有的：

- `IsOLED()`：原判据是「机型串命中一张 30 多项的清单」或「ASUS OLEDCare 注册表里
  `EnablePixelRefresh` 非零」。机械革命确实有 OLED 屏机型，但**目前没有可靠判据来源**——
  GCU 状态帧里没有面板类型字段，注册表 `ItemSupport` 里也没有对应能力位。
  现在只保留配置开关（`oled`），拿到真实判据再接。消费者是 AMD 驱动侧的 OLED 省电优化。
- `IsDynamicLighting()`：原判据是「2024 款华硕机型」。动态照明是 Windows 11 的通用特性、
  不是华硕专有，同样换成配置开关（`dynamic_lighting`）。

### 顺手修掉的一处

电池滑条的可选值原来由 `IsChargeLimit6080()` 决定，那个谓词恒假，
所以滑条一直是连续的 40..100，用户能选到一个硬件到不了的百分比。
现在直接对齐到三档实际生效值（60 / 80 / 100）。

`IsChargeLimit6080()` 本身还有个隐蔽风险，值得作为反例记住：它的机型候选清单里有 `"H760"`，
命中时会在机械革命的三档对齐**之前**先按华硕那套改一遍上限
（华硕 `>85→100 / >=80→80 / <60→60`，我们 `>=95→100 / >=80→80 / else→60`）。
机型串一旦意外命中，就会走上一条从未被验证过的分支。
**按机型名匹配的谓词留在代码里，本身就是一种正确性风险**，不只是死代码。

## 需要按机型消歧的 15 个地址

跨 spec 类复用的 20 个地址已由"所属类型"结构性消解（例如风扇表基址 `0xF00` 在
`RamFan1_ECSpec` 和 `RamFan1p5_ECSpec` 里各自声明，含义相同）。剩下 15 个是同类内的真歧义：

| 地址 | 候选含义 | 建议消歧依据 |
|---|---|---|
| `0x743`–`0x746` | `ConfigurableTGP` / `DynamicBoost` 系列 **对** `MYFAN2_L1..L4_PWM` | MyFan 世代（`MAFAN_CONTROL_BYTE`/`MyFanCCI_Mode_Index`）+ `OcSettingsSupport` |
| `0x789`–`0x78B` | `L1..L3_PWM_DEFAULT_MYFAN2` **对** `L4/L5_PWM_DEFAULT_MYFAN3` / `MYFAN3_GPU_SETTING` | 同上，MyFan2 与 MyFan3 布局不同 |
| `0x78C` | `L4_PWM_DEFAULT_MYFAN2` / `SINGLEKBL_ENABLE` / `AP_EC_LOGO_CONFIRM` | `KeyboardType` + MyFan 世代 |
| `0x769`–`0x76B` | `RGBKB_LEVEL_R/G/B` **对** `AP_EC_LOGO_R/G/B` | `KeyboardType` 与 `BatteryLogoSupport` |
| `0x7AB` | `MyFanCCI_Mode_Index` **对** `AP_EC_LOGO` | 同上 |
| `0x727` | `CPU_DOUBLE_FLAG_SUPPORT` **对** `ESHUTTER_STATUS` | 机型是否有电子快门（摄像头挡板） |
| `0xC7` | `PDWarning_Event` **对** `TimAP_AC_Chg` | 这两个是 OSD 事件码不是 EC 地址，属于命名空间混用 |
| `0xFF03`（`ITE_SPEC`） | `USAGE_PAGE_ME_2ND` **对** `USAGE_PAGE_Ligbar` | HID usage page，键盘与灯带共用同一页，靠 interface 号区分（本项目 `KeyboardRgb.ScoreCandidate` 已实现） |

好消息是：**每一条的消歧依据都是能从注册表 `ItemSupport` 直接读到的能力位**，不需要猜。

## 下一步（按建议顺序）

1. **全量只读快照** —— 在不同模式/电源状态/负载下各采一份 `0x400`–`0x4FF`、`0x720`–`0x7FF`、
   `0xF00`–`0xFFF` 的完整 dump，用 MQTT 已知字段做锚点反推。这一步零风险，且能一次性钉死大部分歧义。
2. **双写对照** —— 保持 GCU 运行，经 MQTT 下发变更并同步 dump EC，观察哪些字节变化。
   这能把 15 个歧义地址逐个确证，全程不需要自己写 EC。
3. **建立 `ProjectID` ↔ `BIOS_PROJECT_ID` 映射** —— 目前缺失，是全机型适配的关键一环。
4. **影子写入** —— 只对能回读校验的项目开写：PL1（RAPL 回读）、风扇占空比（RPM 回读）、
   充电限制（RSOC 回读）。风扇必须先有"写入后 N 秒 RPM 未上升即回滚"的看门狗。

## 已知的覆盖上限

`5.17.x` 的 `GCUService` 二进制在 Inno Setup 的 zlib 压缩块里（`zlb\x1A`），本机没有 `innounp`
所以未能取出做跨版本比对。当前的寄存器表来自 `5.56.60.26`，而它的 `ProjectID` 枚举仍然包含
全部旧世代机型，因此**推断**它是超集——但这一点**尚未验证**。要闭合这个缺口需要解开
`5.17.x` 的安装包并对同名常量做差异比对。
