# 性能模式与档位：厂商实现 + 真机实证

研究对象：厂商 GCUService 的模式/档位/参数下发链路。目的是回答一个问题——**内置模式（静音/平衡/狂暴）
能不能真正自定义**，以及要做成 G-Helper 那样的「每个模式都可调」该怎么落地。

## 证据来源

| 代号 | 路径（`_decompiled/`、`_extracted/` 均被 git 忽略） | 可读性 |
|---|---|---|
| `S40` | `_decompiled/gcu40-51751-27/` —— 控制台 5.17.51.27 内的 GCUService 1.0.2.70 | **真实方法体**，0 处 `Runtime exception` |
| `C50` | `_decompiled/ccuwinui/` —— 50 系官方控制台 CCUWinUI.dll 5.56.60.26 | **真实方法体** |
| `HW` | 本机实测：耀世 / BIOS_PROJECT_ID=IDY、Intel Core Ultra 9 275HX、RTX 5080 Laptop、运行 GCU 1.2.0.0 | 一手实验 |
| `JSON` | `C:\Program Files\L-Mechrevo\GCU\AiStoneService\MyControlCenter\{UserPofiles,UserFanTables}` | 服务端可读存档 |

`S40` 是本次唯一拿到真实方法体的 GCUService。50 系服务 1.2.0.0 是 IL 混淆，静态不可读；
但 `HW` 实验证明它的行为与 `S40` 一致，因此 `S40` 可当作 50 系服务的行为参照。

被选中的风扇管理器由 EC 决定：`MyFanCtrl.cs:31-72` 按 `EcCtrl.IsSuportRamFan1p5()`（EC 1934 bit6）
与 `customizeTarget`/`bridgeType` 分派；本机走 `MyFanManager_RamFan1p5`，下文行号均指该文件。

---

## 1. 模式切换做了什么

命令入口 `MyFanManager_RamFan1p5.cs:358-425`：`Fan/Control` 的
`OPERATING_{OFFICE,GAMING,TURBO,CUSTOM}_MODE`（可带 `ProfileIndex`）→ `TransferToName(mode, index)`
得到内部档名 `M1P1`…`M4P5` → `SetOperatingModeProfileIndexThread(name)`。

**串行化与合并（:3228-3268）**：该方法用信号量串行执行，每次执行后 `await Task.Delay(1500)`。
执行期间到达的新请求**不排队，只覆盖** `LastCommand3`，等当前这次跑完再补发最后一条。

> 这正是 beta20 在真机上抓到的「从内置模式第一次点自定义切不过去」的成因：
> 我们连发的 `OPERATING_CUSTOM_MODE` 落在上一次切换的 1.5 s 窗口内，被合并覆盖。
> 我们的对策（未确认就重发一次）与厂商这段实现是自洽的。

`SetOperatingModeProfileIndex`（:3161-3226）写 `m_Option.{Gaming,Office,Turbo,Custom}ProfileIndex`
与 `MainOption.json`，再调 `UserSet_Mode1..4()`。

| 函数 | 行 | 行为要点 |
|---|---|---|
| `UserSet_Mode1`（平衡/Gaming） | :874-898 | 临时 `SetGpuConfigurableTGPTarget(95, DefaultTGP)`；`OperatingMode=1`；**`UserSet_FanBoost(0)`**；`SetUserProfile(1)`；NVRAM `PowerMode=1`；最后关闭 cTGP 控制位 |
| `UserSet_Mode2`（静音/Office） | :899-911 | 同上，cTGP 临时值 95，`OperatingMode=0`，NVRAM `PowerMode=0` |
| `UserSet_Mode3`（狂暴/Turbo） | :912-924 | cTGP 临时值 **115**，`OperatingMode=2`，NVRAM `PowerMode=2`，额外 `SetDynamicBoostforTurboMode()`（DB 开 + 最大 TGP） |
| `UserSet_Mode4`（自定义） | :925-940 | `OperatingMode=3`；**`fromscancode:true` 时 `CustomProfileIndex=(idx+1)%5`**；`SetUserProfile(3)`；NVRAM `PowerMode=3` |

**每次内置模式切换都会强制关闭风扇增强**（`UserSet_FanBoost(0)`）；自定义模式切换不会。

### 1.1 `SetUserProfile(mode)`：决定成败的那个函数（:2295-2420）

1. `SetFanMode(mode)`（:2537-2607）：写 EC `0x751`（1873）——平衡 `0x00`、静音 `0xA0`、狂暴 `0x10`、
   自定义 `0x00`（超频开时 `0x10`）；风扇增强叠加 bit6。随后
   **读改写 EC `0x726`（1830）：`mode==3` 置 bit7，其余模式清 bit7**（:2600-2606）。
2. 风扇表：`FanTable.SetFanTable(currentProfile.FAN.TableName)`；但 `m_FanSafetyProtect==1` 时
   强制改用 `DefaultFanTable_{Gaming,Office,Turbo}`（自定义模式用 Gaming 那张）。
3. **功耗墙：`if (mode == 3) { SetPL1/2/4Value(profile.PL…) } else { SetPL1/2/4Value(0) }`**
   （Intel :2365-2376，AMD :2333-2347）。也就是说**切到任何内置模式时，服务主动把
   EC `0x783/0x784/0x785` 写成 0**。
4. 温度墙（Tcc）：所有模式都按 `TccOffsetSwitch` 应用。
5. GPU：核心/显存偏移、目标温度所有模式都应用；狂暴强制开 Dynamic Boost。
6. `SetFanSwitchSpeedEnabled(...)`、内存超频开关（视 NVRAM 支持位）。

### 1.2 参数写入 `SET_OPERATING_MODE_DETAIL`（:1045-1311）

**整个方法没有任何模式判断。** 每个字段都写进 `currentProfile`、立刻下发硬件，
最后 `RefreshCurrentProfile(m_Option.OperatingMode)` 把**当前模式的档位 JSON** 落盘。
所以在平衡模式下改 PL1，改的是 `Mode1_Profile1.json`，并且真的写了 EC。

结构上有个必须注意的细节：`OverClockingSwitch`、`PL1`、`PL2`、`PL4`、`CpuAmdSPL/SPPT/FPPT`
是**独立 `if`**，从 `FanSwitchSpeed` 开始全是 **`else if`**——同一个包里带多个后段字段，
**只有第一个会生效**。这就是官方控制台每个字段单独发一包的原因
（`C50 CustomSettingPageViewModel.cs:744-1155`，逐字段各一次 `SendTopicToServer`）。

PL 还会被夹逼：`ReCheckCpuPLMinimumValueI/A`（:3424-3499）强制 `PL2≥PL1`、`PL4≥PL1/PL2`
（`g_SupportCPUDoubleFlag==1` 时按半值比较），然后写 EC 1923/1924/1925。

### 1.3 回读是「存档值」，不是硬件值

`UpdateStatusToClient`（:577-672）发布的 `Fan/Status` 约 90 个字段，全部来自 `currentProfile`
与 `_SmartApcTable`，**没有一个字段是重新读硬件得到的**。

> 结论：**`Fan/Status` 的回读只能证明服务端记住了这个值，不能证明硬件生效。**
> 这是本项目「不做伪功能」纪律的硬性依据。

---

## 2. 真机实证：内置模式下哪些参数是假的

方法：用 helper 槽位（clientId `UWPClient_3`，不挤掉正在运行的应用）发命令，
用独立手段测「是否真的生效」——CPU 功耗用 Windows `\Energy Meter(RAPL_Package0_PKG)\Power`
（满载 python busy-loop），显卡功耗墙用 `nvidia-smi enforced.power.limit`，
风扇用 GCU `System/FanInfo` 的 duty/rpm，EC 用只读 IOCTL 回读。每项测完立即还原。

| 项目 | 平衡模式（内置） | 自定义档 5（M4T5） |
|---|---|---|
| `PL1=PL2=40` | `Fan/Status` 回显 40/40；`Mode1_Profile1.json` 落盘 40；EC `0x783/0x784`=`0x28`。**满载功耗仍 82.8 W 均值（峰值 93.3 W）** | 同样的写入与回显；**满载功耗 40.1 W 均值（峰值 42.1 W）** |
| `GpuConfigurableTGPTarget=100` | 回显 100；**`nvidia-smi enforced.power.limit` 仍 150 W** | **`enforced.power.limit` 变成 100 W** |
| 风扇曲线占空比 | EC 表字节确实变了（`0xF21`-`0xF23`：`0x90`→`0x50`）；**风扇不跟随**：58 °C 时 72% 曲线与 40% 曲线的实际 duty 都是 32% | **精确跟随**：40% 表 → duty 40 / 2293 rpm；90% 表 → duty 87 / 4244 rpm |
| EC `0x726` bit7 | `0x00` | `0x80` |

**判定**：固件只在「自定义模式标志位 EC `0x726` bit7 置位」时，才采用
功耗墙设置寄存器（`0x783`-`0x785`）、RAM 风扇表（`0xF00`-`0xF5F`）与 NVAPI 的 TGP 目标值。
而该 bit 只有 `SetUserProfile(3)` 会置位（§1.1 第 1 步）。代码侧还有第二重印证：
切内置模式时服务自己把 PL 写成 0（§1.1 第 3 步）。

> 所以「在平衡模式里调功耗墙/TGP/风扇曲线」这件事，**协议允许、回读通过、硬件不生效**。
> 只看 `Fan/Status` 的实现必然误报成功。beta20 把这些项挡在内置模式之外是对的，
> 但代价是内置模式没法真正自定义——这正是本轮要解决的问题。

### 2.1 内置模式下确实生效的项

这些项的代码路径不带模式判断，且不依赖 `0x726` bit7：

- 温度墙 Tcc（`SetCpuTccOffset`，所有模式都应用）——**本机未做热实测**，仅代码级结论。
- 显卡核心/显存频率偏移、目标温度（走 NVAPI，非 EC 门控）——狂暴模式官方自动超频 +105/+500
  就是这条路，beta20 已在本机实测通过。
- 风扇增强 `FAN_BOOST_ON/OFF`（EC `0x751` bit6，全局量；注意切内置模式会被清零）。
- 风扇转换灵敏度 `FanSwitchSpeed(Enabled)`。
- 应用侧的 Windows 电源模式覆盖层、处理器睿频、屏幕刷新率（与 GCU 无关）。

---

## 3. 档位、风扇表与默认值

- 档位数量（`RefreshCurrentProfile` :2085-2150）：平衡 2 档、静音 2 档、狂暴 2 档、
  **自定义 5 档**（`CustomizeCtrl.GetCustomId()==9` 的机型只有 3 档）。
  我们此前按「自定义 4 档」实现，**实际是 5 档**（`JSON` 里确有 `Mode4_Profile1..5`）。
- 档名（`MainOption.json` 的 4 个 `*ProfileIndex`）与模式正交：每个模式各自记住自己当前用哪一档。
- 风扇表名 `M{mode}T{profile}`，mode：1=平衡 2=静音 3=狂暴 4=自定义。
  16 个点，`{ID, UpT, DownT, Duty}`；`SET_FAN_SPEED_CURVE_SETTING` 的 `T0..T15`
  **只写 Duty**，温度点 `UpT/DownT` 由表文件决定，官方界面不提供改温度点的入口
  （另一个重载能改，但没有调用方：`FanTable_Manager1p5.cs:716-755`）。
- 本机 `M1T1`：`UpT = 0,48,52,56,60,64,68,72,76,80,85,255…`，有效点 11 个；
  EC 中 duty 字节 = 百分比 × 2（`0x90`=144 ↔ 72%）。
- 恢复默认（`RestoreDefaultFanTableAll` :341-384）：M1T*←`DefaultFanTable_Gaming`、
  M2T*←`Office`、M3T*←`Turbo`、**M4T*←`DefaultFanTable_Gaming`**。
- 各模式出厂参数来自 **EC 只读块**（`RestoreCurrentProfile` :2165-2294 → `LoadProfileAll` :1367）：

| 模式 | PL1/PL2/PL4/DState | Tcc | 本机实测值 |
|---|---|---|---|
| 平衡 Gaming | `0x730`-`0x733` | `0x7D8` | 75 / 85 / 145 / 1，Tcc 15 |
| 静音 Office | `0x734`-`0x737` | `0x7D9` | 45 / 45 / 145 / 1，Tcc 25 |
| 狂暴 Turbo | `0x7A7`-`0x7AA`（即 `BATTERYSAVER_*` 那一块） | `0x7DA` | 210 / 210 / 210 / 1，Tcc 7 |
| 自定义 | **复用 Gaming 的那一组** | — | 同平衡 |

> 注意 `GetTurboPLDefaultValue`（:2910-2939）读的是常量名为 `BATTERYSAVER_PL*` 的地址
> `1959-1962`（`0x7A7`-`0x7AA`）。常量名与用途不一致，照名字用会拿错值。
> 这三块 EC **只读即可**，不必切模式就能拿到每个内置模式的出厂 PL/Tcc——
> 正是「自定义某个内置模式」所需的种子值来源。

---

## 4. 50 系官方控制台怎么用这套协议（`C50`）

- 主页切模式（`HomePageViewModel.cs:373-432`）：`Fan/Control` 发 `OPERATING_*_MODE` +
  `ProfileIndex = 0`（**内置模式恒定发 0，从不发第二档**），紧跟 `LCHWOC/Control`
  发 `IsNormalRun = 0/1/2`（Office/Gaming/Turbo）；自定义发 `IsCustomRun = true`。
- 自定义页（`CustomSettingPageViewModel.cs`）：切档 `OPERATING_CUSTOM_MODE + ProfileIndex`
  → `IsCustomRun=true` → `await Task.Delay(100)` → 后续命令（:568-590）。
  档位命名 `SET_CUSTOM_PROFILE_OSD_STRING + ProfileName`（:506）。
  恢复默认 `RESTORE_OPERATING_MODE_DETAIL`（:532）。
  每个滑条/开关一次一个字段（:618-1155）。
- **官方界面只允许编辑自定义档**；静音/平衡/狂暴在界面上没有任何参数入口。
  这与 §2 的实测一致：官方自己就不提供，因为它在内置模式下不生效。

---

## 5. 对 L-Mechrevo 的设计结论

### (a) 内置模式下可以就地改、且真生效的

温度墙 Tcc（代码级，未热实测）、显卡核心/显存偏移与目标温度、风扇增强、风扇转换灵敏度，
以及纯应用侧的 Windows 电源模式 / 睿频 / 刷新率。

### (b) 内置模式下改了不生效的（协议接受但硬件忽略）

CPU 功耗墙 PL1/PL2/PL4（AMD 的 SPL/SPPT/FPPT 同理）、显卡 TGP（`ConfigurableTGPTarget`）、
Dynamic Boost、风扇曲线。判据是固件的 `0x726` bit7 门控，不是某个字段的白名单。

### (c) 推荐做法：**自定义过的模式，跑在固件自定义档上**

不要试图区分「哪个字段能在内置模式里改」——那需要逐字段热实测，而且随机型/固件而变。
改成一条统一规则：

1. 每个用户模式（静音/平衡/静音狂暴/狂暴/自定义 N）在应用侧都有完整参数集。
2. 模式**未被用户改过** → 按原样下发 `OPERATING_*_MODE`，完全保持官方行为（含官方出厂曲线、
   NVRAM `PowerMode`、模式专属的 Dynamic Boost 策略）。
3. 模式**被用户改过** → 切到一个固件自定义档承载它：
   - 参数种子 = 该模式的 EC 出厂值（§3 的表）+ 官方对应风扇表（`DefaultFanTable_*`），
     用户只覆盖他改过的项；
   - 固件只有 5 个自定义档，应用侧按「最近使用」把用户模式映射上去，
     映射命中时切换是即时的，需要重写参数时慢 1–2 s；
   - 代价要如实告知：这种模式下 NVRAM `PowerMode` 会记成 3（自定义），
     厂商 OSD 与托盘图标显示的是自定义模式。
4. 无论走哪条路，**每一项都要有独立于 `Fan/Status` 的生效判据**才报成功：
   PL 用 RAPL 功耗、TGP 用 NVAPI/nvidia-smi 回读、风扇用 duty/rpm 跟随、
   电源模式用 `PowerGetEffectiveOverlayScheme`。回读只等于「服务端记住了」。
5. 自定义档数量按机型取 5（`CustomId==9` 取 3），不再硬编码 4。

### (d) 其他必须跟着改的实现细节

- 切内置模式会清零风扇增强：切换后要把用户的风扇增强意图重新应用一次。
- 同一包里只能带一个「后段字段」：继续逐字段发，间隔 ≥120 ms。
- 1.5 s 合并窗口：连续切换要么等窗口，要么像 beta20 那样未确认就重发一次。
- Fn 物理模式键在自定义模式下会让固件自己把档位 `(idx+1)%5`；应用必须跟随
  `Fan/Status` 的 `CustomProfileIndex`，不能假设档位只由自己改。
