# gcu-30-50-model-adaptation - Work Plan

## TL;DR (For humans)

- **What you'll get**：控制台从"一套逻辑 + 22 处硬编码机型假设"改为**按机型驱动**：识别单一真源 → 功能矩阵 → 24 机型风扇表 → 逐代显卡/模式差异 → 安装器只带 50 系 GCU。
- **两条正交轴（脊柱，勿合并）**
  - **轴 1 = 平台/机箱代号**（`PH4*`/`PH6*`，24 个，**跨代共享**）→ 决定风扇表目录、EC PL 默认值、规格类（EC 1934 bit6）。
  - **轴 2 = dGPU 代际**（30/40/50）→ **运行时探测**（GPU 营销名 / NVIDIA PCI device-id 高字节；`BIOS_PROJECT_ID`**仅佐证、不作判据**），**绝不**由平台代号推导 → 决定显卡切换载体+编码、服务载荷变体。
  - 依据：`PH4*` 横跨 30/40/50 三代控制台（4.17.47.13、5.17.49.19、5.17.51.34、本仓库）→ 三源覆盖**同一 24 代号**（`docs\hardware\README.md`）；`UserFanTables`（144）与 `release\GCU-only\AiStoneService\MyControlCenter\UserFanTables`（144 per-model）**SHA256 逐一相同**。
  - `installer\Select-GcuPayload.ps1:98-107` 的 `PH6*=50 / PH4*=40` 启发式**原理上就错**（把轴 1 当轴 2）。
  - 平台代号→代际 = **INFERRED，非厂商验证**（矛盾：`docs\upgrade-from-openrevo.md:140` vs `docs\gcu-dependency-matrix.md:88`）；**无可引用的平台代号→GN2x 映射**，不臆造（GN20/GN21 枚举见 T2）。
- **Why**：① 厂商控制台每代**只发 MQTT、从不碰硬件**（155,805 行 0 命中 `ACPIDriver`/`DeviceIoControl`/`WinRing0`）；② 显卡路由 = **NVRAM `OemDisplayMode`**（非 EC 寄存器；由厂商服务写入，控制台经 MQTT 请求）；③ 风扇表由**服务**写 → 复刻厂商分层。
- **What it will NOT do**：不适配 20 系及更早（D1）· 不做**任何 EC 直写**（门铃只读；唯一例外 = 已证逐字节相同的限充）· **显示路由固件变量 `OemDisplayMode` 只走 MQTT、控制台不写**（唯一许可的非 EC 直写载体 = 既有 `Pawn\` SMU 路径，无新增特性）· 不改 UI 形态 · 不自研驱动 · 不动 `release\`。
- **Risk**：① **PL 默认瓦数住在 EC**（Gaming 1840-1843/Office 1844-1847/Turbo 1959-1962/Tcc 2008-2010），无真机取不到逐 SKU 真值 → "读不到即禁用"，不猜；② **50 系服务 IL 混淆**，写路径静态不可读 → 真机 QA（F3）。
- **Decisions**：D1 不支持机型 → 提示 + 只读降级 · D2 自动优先 + 手动覆盖 · F3 = 可解析且落在 24 机型集合内 · F4 = **TDD** · F5 = **显示路由 `OemDisplayMode` 只走 MQTT（控制台不写固件变量）；EC 直写仍禁（限充例外）；非 EC 直写仅限既有 `Pawn\` SMU 路径、无新增** —— amended by owner (option b) · D3 = **全部机型使用最新 50 系 GCU 载荷（owner 已决）**；G0 仅为**删旧载荷树（省体积）**的证据闸门，不作「是否用最新载荷」的门 —— amended by owner (round 5)。

## Scope

**IN**
- **轴 1**：24 个平台代号（30/40/50 共用）的识别与功能可用性矩阵。
- **轴 2**：dGPU 代际**运行时探测** + 逐代显卡切换（直连/混合/iGPU/iGPU-only/热切换）。
- 逐代性能模式：厂商 `SysPowerModeIndex`(Perf=1/Bal=2/Batt=3/Bench=4) 与我方 `OperatingMode`(Office/Gaming/Turbo/Customize) **分别建模**。
- 24 机型风扇表一一对应（4 个键集分组、EC 1934 bit6 规格类、门铃时序**只读校验**）。
- 载荷：exe **只含最新 50 系 GCU**（`release\GCU-only`）+ 驱动源、先卸后装、SHA256 判版本、防降级、选择器**失败即响**。
- 深度检查落地：84 分支点收敛、22 处硬编码清理、8 个未门控功能补门控、配置键按机型隔离。

**OUT / Must-NOT-Have（护栏，禁止扩展成需求）**
- **20 系及更早**（用户只要 30-50）；按 D1 降级。
- **EC 直写仍禁**（F5 修正，amended by owner）：唯一例外 = 已证与厂商逐字节相同的限充（`src\MechrevoLiteWin\Hardware\EcChargeLimit.cs`：0x7B9/0x7D0、写 IOCTL `0x9C40A48C` @`:47`/`:191`）；门铃只读（T15）；读 EC 不受限。
- **显示路由固件变量 = MQTT-only（option b）**：`OemDisplayMode` 是厂商服务持有的 NVRAM/固件变量；**控制台不写该固件变量、不得存在写固件变量的接缝**——控制台经 MQTT 请求厂商服务执行（同厂商控制台）。**理由（勿重开）**：`OemDisplayMode` 两套编码恰好相反（AMD `{direct=1, hybrid=0, igpu=2}` vs Intel `{igpu=1, direct=2, hybrid=4}`），误判平台时写入字节在另一编码下合法但语义相反，读回校验仍通过、回滚不触发，且变量为持久固件态 → 错误显示路由跨重启存活（潜在黑屏）；故 owner 选择保守方案(b)。
- **`src\MechrevoLiteWin\Pawn\`**（`PawnIOWrapper.cs`/`RyzenSmu.cs`/`CpuInfo.cs`/`RyzenSMU.bin`；消费方 `Diagnostics\DiagnosticSystemInfo.cs:90-91`、`Display\AmdDisplay.cs:8`）= **唯一许可的非 EC 直写载体**：**无新增工作、不新增 SMU 特性、不改实现**（N6）。
- **UI 形态改造**（WebView2/HTML）、自研内核驱动、Rust 重写；**修改/删除 `release\` 下任何文件**（只读）；修改 MQTT 之外的通讯方式。**服务接管 = owner-directed override（amended by owner (round 5)）**：安装器**接管 GCU 服务**（检测既有厂商 GCU → 卸载 → 装我方）现属 IN；****安装器自动卸载厂商「官方控制台」应用（含 UWP/MSIX 包、桌面 exe、其自启动项与快捷方式）属 IN**（amended by owner：此前「只提示、不删除」是编排者的臆造，已撤回；业主原话「检测到有官方控制台，安装器应该要把当前环境中的 gcu 和原本的控制台全都删了才对。然后只用我们的控制台」），删除范围由 Test-VendorArtefactRemovable 白名单钉死，且每步可见**。理由：只带最新 GCU 需清旧厂商服务；用户应用去留由用户决定。
- **注（T35）**：底栏重叠/DPI 缩放错位属缺陷修复（非 UI 形态改造），在范围内。

## Verification strategy

- **TDD（F4）**：新代码**先有失败测试**再实现；红→绿→重构。
- **门禁不变**：Debug/Release 构建 0 错 0 警 + 全量测试绿 + UI 审计门禁（`scripts\test-ui.ps1` → `ui-audit.json` 的 `IssueCount` 须为 0）。
- **表驱动**：4 个风扇表键集分组（实测 5/8/5/6 = 24，见 T13）× 5 张名单（28/14/9/3/3）× ~20 个能力位判据，各自成为可执行断言。
- **证据绑定（强制，防伪造）**：每个 QA = **可复跑命令 + 退出码 + 产物哈希 + `git rev-parse HEAD` + 干净 `git status --porcelain`**。QA 行 `T <Filter>` = `dotnet test tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj --filter FullyQualifiedName~<Filter> --logger "trx;LogFileName=<id>.trx"`，经 `pwsh -File scripts\run-qa.ps1 -Id <id> -Command "<cmd>"`（T31 落盘）→ `.omo\evidence\<id>.txt`（命令/退出码/UTC/git）+ 同名 `.sha256`。**`run-qa.ps1` 必须解析 `.trx`：`Skipped>0` 或 `Passed==0`（过滤器匹配不到用例）即 FAIL，绝不以退出码或 "0 failed" 单独放行**（R3-3：仅单元/TDD，见 T31(f)）；脚本自身 SHA256 固定进 `.omo\evidence\run-qa.sha256` 与 T31 提交正文。**缺 `.sha256`/HEAD 或 `git status` 不干净 = 未执行**；散文式不合格。
- **环境**：`bash` 可用（默认 PowerShell 5.1）；哈希/扫描用 `Get-FileHash`/`Select-String`；执行前 `New-Item -ItemType Directory -Force .omo\evidence`（T31 第一步）。
- **凡"机型/代际是变量"的项**：不接受人工判定，必须由表驱动测试或真机脚本产出证据。
- **残余限制（诚实声明）**：单元测试全走注入/fake（`LMECHREVO_MODEL_OVERRIDE` 在 `ModelRegistry` EC 解码**之后**进入）→ 24 代号为 fake 驱动、不覆盖出厂解码（仅 F3 真机覆盖）。F5 修正（option b）后：显示路由只走 MQTT、无固件变量写；T17/T18 只证 MQTT 协议与重试语义。
- **BLOCKED-HW 可判退出**：真机不可得的代际/机型写 `.omo\evidence\f3-generation-status.json` → F3 唯一凭据；每条含 `probe`（确切命令）+ `raw`（原始输出）+ `hash`（产物哈希），缺一即未完成；**其可接受性完全依赖下方链路证明 + 首真机 runbook**；BLOCKED-HW 条目永不满足 G0（T22）。
- **链路证明（强制，C1）**：无真机可接受，但每个硬件相关特性必须 (a) 在模拟/fake 下验证完整逻辑链；(b) 加**端到端接线断言**——证明产出的数据确实到达消费方（UI / 功能门 / 矩阵），非仅单测返回值；(c) 覆盖错误/失败路径；(d) 产出**首真机验证 runbook**（有序、可复制粘贴步骤 + 每步确切期望观察）。**BLOCKED-HW = 硬件执行延后（非终点）**。适用 T9/T16/T17/T18/T20/T22/T26/T32-T36。
- **接线缺口（强制，C5）**：身份/矩阵层必须被 UI 与功能门真实消费——T6/T7/T8/T10/T12/T16 各加验收项：**端到端接线测试证明身份/矩阵数据到达 UI 与功能门，且换机型确实改变被门控行为**（"符号存在"不算）。
- **终验**：F1 合规+证据绑定审计 · F2 代码质量 · F3 真机 QA · F4 范围忠实性；四项全 APPROVE 才算完成。

## Execution strategy

- **Wave A0（T31）是所有波次的前置**（I/O 接缝+机型注入+证据脚本+fake harness）；**T17/T18/T19 的单元 TDD 在 T31 落地后解除阻塞**；真实 broker 集成为独立可选步骤（R3-3）。B/C/D 依赖 A 的判定接口；E 可并行。
- 一个 todo 一个提交；实现 + 测试算**同一个** todo。
- 需真机而无对应机型：**验收标 BLOCKED-HW** + 落 T31 证据文件；不夹带 beta18/19 发布工作。

## Todos

- [ ] 31. **Wave A0（前置，先于 1-30）** 复用 EC 接缝 + 新增 MQTT 接缝 + 机型注入 + 证据脚本：**(a)** 复用 `src\Probe\EcSnapshot.cs:9` `IEcReadTransport`（只读）与 `:21-75` `AcpiDriverReadTransport`（`ReadByte :52-67`；`\\.\ACPIDriver`，IOCTL `0x9C40A488`）；写侧复用 `Hardware\EcChargeLimit.cs:56,61`（写 IOCTL `:47`，唯一使用 `:191`）。**接缝唯一选项**：`MechrevoLite.csproj` 引用 `..\Probe\Probe.csproj`（`net10.0-windows10.0.19041.0` 可消费 `net10.0`，反向不可；不采用"移入应用、Probe 反向引用"）。**(b)** `IMqttTransport` 只管 connect+subscribe；publish 走既有 `_publishOverride`（`MechrevoHw.cs:61,147-150,208,2619-2621`）或同一提交内统一并删旧路径（不得两套并存）；改造 `src\Probe` 静态路径（`MqttProbe.cs:12,19,27,37`、`GoldenCapture.cs:9-10`、`EcSnapshotReport.cs:235,244`）。**(c)** `OverrideCurrent`（`MechrevoDeviceCapabilities.cs:117-120`）+ `LMECHREVO_MODEL_OVERRIDE=<代号>`（`AssemblyInfo.cs:4` 已有 InternalsVisibleTo）。**(d)** 落盘 `scripts\run-qa.ps1`（断言见 Verification；自身 SHA256 进提交正文）+ `.omo\evidence\` 目录。**(e) 读 IOCTL 清点（R3-2：实为 3 份实现 + 1 份重复 interop，非 2 份）**：① `src\Probe\EcProbe.cs:7`（`2621482120u`；注释 `// 0x9C402108` **错误**，实为 `0x9C40A488`，**同一任务内改正**）+ `:9-13` 自带重复 interop → 读路径 **MERGE** 到 `IEcReadTransport`（2 字节入参=已知错误形状，`docs\ec-per-generation.md:85-88`；其位于 Probe 静态路径 → 同受接缝约束）；② `src\Probe\EcSnapshot.cs:23`（`0x9C40A488`）= 接缝真源/合并目标；③ `src\MechrevoLiteWin\Hardware\EcChargeLimit.cs:46`（读实现 `:166-181`）→ **EXEMPT_WITH_REASON**（唯一获批 EC 直写路径的同句柄事务读回、厂商逐字节等价；合并改变事务边界）；④ `src\Probe\EcSnapshotReport.cs:143` 只打印 IOCTL 形状 = 文档、非实现。逐项判定落 `.omo\evidence\t31-ioctl-inventory.json`。**(f) TDD/集成 harness（R3-3）**：`FakeMqttTransport`（进程内、无 broker/网络）为 T17/T18/T19 单元 TDD 既定路径，受 `.trx` `Skipped>0 || Passed==0 → FAIL` 约束；真实 broker 集成 = `LMECHREVO_RUN_INTEGRATION=1` 显式开启的**独立 QA 步骤**，跳过允许但记 BLOCKED-HW（独立证据+哈希），绝不记绿/静默。
  - References: `src\Probe\EcSnapshot.cs:9,21-75,52-67`；`src\Probe\EcProbe.cs:7,9-13`；`docs\ec-per-generation.md:85-88`；`src\MechrevoLiteWin\MechrevoLite.csproj:4-5`（无 ProjectReference）；`src\Probe\Probe.csproj:4-5`；`tests\MechrevoLite.Tests\MechrevoLite.Tests.csproj:36,38`；`src\MechrevoLiteWin\Hardware\EcChargeLimit.cs:46,47,56,61,166-181,191`；`src\MechrevoLiteWin\Hardware\MechrevoHw.cs:61,147-150,208,2619-2621`；`src\MechrevoLiteWin\Hardware\MechrevoDeviceCapabilities.cs:117-120`；`src\MechrevoLiteWin\Properties\AssemblyInfo.cs:4`；`%TEMP%\opencode\ulw-notes\metis-findings.md` #3。
  - Acceptance: EC 读/写与 MQTT 收发全部经接口、无绕过；接缝位置为上述唯一选项；`IEcReadTransport` 未被复制出第二个同类抽象；MQTT publish 只有 `_publishOverride` 一条路径；3 份读 IOCTL + `EcProbe.cs` 重复 interop 逐项有 MERGE/EXEMPT_WITH_REASON 判定（落 `t31-ioctl-inventory.json`），`EcProbe.cs:7` 错误注释已改正；`scripts\run-qa.ps1`（trx 断言 + git 绑定）与 `.omo\evidence\` 就位；fake MQTT harness 就位且集成路径受 `LMECHREVO_RUN_INTEGRATION` 门控；全量测试绿。
  - QA happy: `T TransportSeam` → `.omo/evidence/t31-h.txt`
  - QA failure: `T TransportSeamFail` → `.omo/evidence/t31-f.txt`
  - Commit: `refactor(io): reuse EC read seam and add injectable MQTT transport`

### Wave A - 机型识别层（单一真源）

- [ ] 1. 新建 `src/MechrevoLiteWin/Hardware/ModelRegistry.cs`：从 EC 1856（project byte）与 1868（OEM service project byte，掩码 15/63）读身份，等价实现 `GetProject2ExID` 展开，暴露 `ProjectId`/`RawProjectByte`/`BiosProjectId`/`Source`。
  - References: `%TEMP%\opencode\dl-gcuservice\full\GCUService.decompiled.cs:11789-11794,12077-12143,12216-12312`；`%TEMP%\opencode\cc-51751-27\decompiled\MyECIO\MyEcCtrl.cs:57,487`。
  - Acceptance: 注入 EC 字节 → `ProjectId` 与厂商枚举名一致；`null`/越界 → `Unknown` 不抛异常；EC 访问经 T31 的 `IEcReadTransport`。
  - QA happy: `T ModelRegistryTests` → `.omo/evidence/t1-h.txt`
  - QA failure: `T ModelRegistryFailTests` → `.omo/evidence/t1-f.txt`
  - Commit: `feat(model): add ModelRegistry with EC project-id decoding`

- [ ] 2. 落盘 `src/MechrevoLiteWin/Resources/model-registry.json`：**只含** identity（24 平台代号 + EC 1856/1868 原始值映射）+ 5 张厂商名单 + 风扇表键集分组 + **轴 2 代际取值域**（`{30,40,50}` ← GPU 营销名 / PCI device-id 高字节；**不含** `BIOS_PROJECT_ID`——它是平台码，不是代际判据）。**能力位不落盘**（T6 实时派生）；**不写"平台代号→代际"映射**（INFERRED，登记未决）。
  - References: `ControlCenterX_5.56.60.26_Mechrevo\UserFanTables`（24 目录；仓库根下一层）；`%TEMP%\opencode\dl-gcuservice\full\GCUService.decompiled.cs:13188-13306`（5 名单）；`_decompiled\GCUService\GCUService.decompiled.cs:1342-1354`（GN20 枚举 10 成员）/`:1355-1376`（GN21 19）；`docs\hardware\project-ids.json`（ProjectId 45 / BiosProjectId 37 / Gn20GpuSku 9 / Gn21GpuSku 19）。
  - Acceptance: 覆盖 24 代号；5 名单成员数与厂商源码逐一相等（28/14/9/3/3）；GN20/GN21 成员数按枚举实计（10/19）；分组与键集实测一致；**无** capability 字段、**无** per-code 代际字段、**无** `BIOS_PROJECT_ID` 代际来源字段；schema 校验失败即测试失败。
  - QA happy: `T ModelRegistryDataTests` → `.omo/evidence/t2-h.txt`
  - QA failure: `T ModelRegistryDataSchemaFailTests` → `.omo/evidence/t2-f.txt`
  - Commit: `feat(model): add per-model registry data derived from vendor tables`

- [ ] 3. 实现 F3 判定（**轴 1** 集合口径）：`IsSupported` 真当且仅当**身份可解析且展开后代号落在 24 机型集合内**；给出 `SupportReason`（Ok / Unparsable / NotInSet）。集合 = `UserFanTables` 的 24 目录名。身份**只**由 T1 的 EC 1856/1868 决定；服务写入的注册表 `BIOS_PROJECT_ID` 至多作佐证、绝不参与判定（不一致时不得翻转 F3）。
  - References: `.omo/drafts/gcu-30-50-model-adaptation.md`（F3）；`ControlCenterX_5.56.60.26_Mechrevo\UserFanTables`；`%TEMP%\opencode\ulw-notes\fan-tables.md` A（`PH6TQxx`/`PH6AGxx` 有枚举无目录）；`src\MechrevoLiteWin\Hardware\MechrevoDeviceCapabilities.cs:215`（`BIOS_PROJECT_ID` 现读法，仅佐证）。
  - Acceptance: 三种 `SupportReason` 均可构造触发；无"默认为真"分支；`PH6AGxx`（枚举 5894、无目录）判 `NotInSet`；佐证源与 EC 身份不一致时判定不变（用例锁定）。
  - QA happy: `T SupportDecisionTests` → `.omo/evidence/t3-h.txt`
  - QA failure: `T SupportDecisionFailTests` → `.omo/evidence/t3-f.txt`
  - Commit: `feat(model): add fail-closed support determination`

- [ ] 4. 手动覆盖逃生口（D2）**显式状态机**：`Auto`（自动成功，手动值不生效）/`ManualPinned`（显式保持 → 生效并跨重启保持）/`ManualFallback`（自动失败 → 生效，自动一恢复即让位）。切换提示风险；持久化值只在 F3 通过后接受。
  - References: `.omo/drafts/gcu-30-50-model-adaptation.md`（D2）；`src\MechrevoLiteWin\Hardware\MechrevoDeviceCapabilities.cs:117-120`（`OverrideCurrent`）；`src\MechrevoLiteWin\AppConfig.cs:35-59,231-241`（配置解析/加载 + `WriteAtomic` 原子替换，T4 的持久化只走这里）。
  - Acceptance: 三态转换均有测试（Auto→ManualPinned 生效；Auto 成功时 ManualFallback 不生效；ManualFallback→Auto 让位）；非法手动值被拒且不写盘；`LMECHREVO_MODEL_OVERRIDE` 与状态机一致。
  - QA happy: `T ModelOverrideStateMachineTests` → `.omo/evidence/t4-h.txt`
  - QA failure: `T ModelOverrideStateMachineFailTests` → `.omo/evidence/t4-f.txt`
  - Commit: `feat(model): add manual model override state machine`

- [ ] 5. 配置键按机型隔离：把 machine-global 键（`charge_limit`、`gpu_mode`、`gpu_auto`、`fan_profile_*_<mode>`、`lc_*`、`ec_charge_limit`）迁为机型作用域 + 一次性迁移（旧值归属到迁移时识别的机型）。
  - References: `%TEMP%\opencode\ulw-notes\console-coupling.md` D1-D5；`src\MechrevoLiteWin\AppConfig.cs:37-45,98-104,227-241`（config.json / ProgramData fallback / `.bak` 原子替换）；`src\MechrevoLiteWin\Battery\BatteryControl.cs:141`；`src\MechrevoLiteWin\Gpu\GPUModeControl.cs:43-50`。
  - Acceptance: 换机型不继承旧作用域值；迁移幂等；旧配置可回滚（`.bak`，`AppConfig.cs:238`）。
  - QA happy: `T ConfigScopeTests` → `.omo/evidence/t5-h.txt`
  - QA failure: `T ConfigScopeFailTests` → `.omo/evidence/t5-f.txt`
  - Commit: `refactor(config): scope machine-specific settings per model`

### Wave B - 机型 × 功能 可用性矩阵

- [ ] 6. 新建 `src/MechrevoLiteWin/Hardware/FeatureMatrix.cs`（**轴 1** 派生位 + 服务真源）。**单一真源（N5，唯一规则）**：服务写入的 `ItemSupport`（`GpuConfig` 同规则）是**唯一来源**，控制台**只读、不重推厂商判据**。缺值二处置：① EC/NVRAM 派生位 → **fail-closed**；② 厂商常量位 → **镜像默认**（`KeyboardSupport`=1、`SystemMonitorSupport`=1、`AcRecoverySwitchSupport`=1、`FanSettingsSupport`=非商用 1），不得因读失败被禁用。热切换判据 = `GpuHotSwapSwitchSupport`+`lgpuHotSwapSwitchStatus`（缺一不提供；见 T9）。
  - References: `%TEMP%\opencode\dl-gcuservice\full\GCUService.decompiled.cs:27164-27241`（全表；常量位 `:27220,27226,27231,27237`，其余为 EC/NVRAM 派生位）、`:27010-27036`、`:11905-11914`、`:11970-11979`、`:11992-12012`；`src\MechrevoLiteWin\Hardware\MechrevoDeviceCapabilities.cs:12-23`。
  - Acceptance: 每能力位有对照表（服务键 → 读法 → 缺失处置）；存在时不重算、缺值不重推（用例锁定）；仅 EC/NVRAM 位 fail-closed；常量位读失败仍为 1；热切换位缺失 → 不提供。 端到端接线测试（C5，见 Verification）。
  - QA happy: `T FeatureMatrixTests` → `.omo/evidence/t6-h.txt`
  - QA failure: `T FeatureMatrixFailTests` → `.omo/evidence/t6-f.txt`
  - Commit: `feat(matrix): add vendor-faithful per-model feature matrix`

- [ ] 7. 移除限充的 `YAOSHI` 硬编码（唯一 allowlist 项，`EcChargeLimit.cs:91-97`，字符串在 `:96`），改经 FeatureMatrix + F3 判定。
  - References: `src\MechrevoLiteWin\Hardware\EcChargeLimit.cs:91-97`；`src\MechrevoLiteWin\Battery\BatteryControl.cs:48-57,104,141`。
  - Acceptance: 24 机型均按矩阵判定；不再有机型名字符串匹配；`ec_charge_limit` 强制开关语义（"1"/"0"）保留。 端到端接线测试（C5，见 Verification）。
  - QA happy: `T ChargeLimitGatingTests` → `.omo/evidence/t7-h.txt`
  - QA failure: `T ChargeLimitGatingFailTests` → `.omo/evidence/t7-f.txt`
  - Commit: `fix(charge): gate charge limit by feature matrix, not a model string`

- [ ] 8. 补 8 个未按机型门控的功能（E1-E8）：`ContainsModel("503")`（`AppConfig.cs:547`，消费点 `GPUModeControl.cs:344`）、ASUS 谓词残留、`NoGpu()` 可见性、175W 默认（`Gpu\NVidia\NvidiaSmi.cs:9-14`）、风扇切换范围（`MechrevoHw.cs:474-482`）、一刀切曲线（`MechrevoHw.cs:1178-1210`）、OLED/DynamicLighting 假探测器（`AppConfig.cs:490,586-589`）、策略恒 Restart（`GpuSwitchPolicy.cs:23-36`，`:35`）。**E2/E3 = INFERRED → 先取证后改**（结论入本 todo 证据）。
  - References: `%TEMP%\opencode\ulw-notes\console-coupling.md` E1-E8、C7-C16、UNKNOWN#5#6；`src\MechrevoLiteWin\AppConfig.cs:490,545-548,553-556,586-589`；`src\MechrevoLiteWin\Gpu\GPUModeControl.cs:55,344`；`src\MechrevoLiteWin\Gpu\NVidia\NvidiaSmi.cs:9-14`；`src\MechrevoLiteWin\Hardware\MechrevoHw.cs:474-482,1178-1210`；`src\MechrevoLiteWin\Settings.cs:3998,4482`；`src\MechrevoLiteWin\Hardware\GpuSwitchPolicy.cs:23-36`。
  - Acceptance: 每项改为矩阵驱动；取证与处置**只接受枚举值**并落 `.omo/evidence/t8-evidence.json`：每项一条，`item`(E1-E8) + `outcome` ∈ {CONFIRMED, REFUTED} + `action` ∈ {MATRIX_GATED, KEPT_WITH_REASON} + `lines`（行号数组），由测试解析校验（缺项或越界枚举即失败）。 端到端接线测试（C5，见 Verification）。
  - QA happy: `T UngatedFeatureTests` → `.omo/evidence/t8-h.txt`
  - QA failure: `T UngatedFeatureFailTests` → `.omo/evidence/t8-f.txt`
  - Commit: `fix(gating): gate previously ungated features per model`

- [ ] 9. 清理 22 处硬编码机型假设（ITE VID/PID `Hardware\KeyboardRgb.cs:12-13`、HID usage page、`MaxCPU=210W`、`APVersionCheck>23`、175W 默认、一刀切曲线等），改为矩阵/能力位驱动或加显式注释说明为何可全局化。**`APVersionCheck>23` 是数值代理，删除后热切换由服务写入的 `GpuHotSwapSwitchSupport` + `lgpuHotSwapSwitchStatus` 决定**（现读法 `Hardware\MechrevoDeviceCapabilities.cs:252-253`；缺一即不提供热切换）。**一次性前置**：真机注册表追踪确认两值由厂商服务写入（服务启动前/后 `SOFTWARE\OEM\GamingCenter2\ItemSupport` 快照对比：前缺失、后出现），落 `.omo\evidence\t9-hotswap-values.json`+命令+哈希；无真机 = BLOCKED-HW；**确认前热切换门不得依赖两值**。
  - References: `%TEMP%\opencode\ulw-notes\console-coupling.md` C1-C22；`src\MechrevoLiteWin\Hardware\KeyboardRgb.cs:12-13`；`src\MechrevoLiteWin\Hardware\WaterCoolerBle.cs:80-81`（LCT22002）。
  - Acceptance: 清单逐条给出处置（矩阵化 / 保留并注释理由），无"未处置"；**验收断言矩阵查表结果**（机型+输入 → 期望输出），不以源码 grep 为通过条件；两替换值有真机写入证据，否则 BLOCKED-HW 且热切换门不启用（T31 证据绑定）。
  - QA happy: `T HardcodeMatrixLookupTests` → `.omo/evidence/t9-h.txt`
  - QA failure: `T HardcodeMatrixLookupFailTests` → `.omo/evidence/t9-f.txt`
  - Runbook: `docs\hardware\first-machine-runbook.md` §1 (registry-trace precondition)
  - Commit: `refactor(gating): remove hardcoded per-model assumptions`

- [ ] 10. D1 运行时表现：顶部横幅"当前机型不在支持列表（识别到 X）"、不支持功能置灰、设置页手动覆盖入口（配合 T4）。QA **脚本化 UI 断言**：以 `LMECHREVO_MODEL_OVERRIDE` 注入机型驱动 UI-audit 子进程。
  - References: `.omo/drafts/gcu-30-50-model-adaptation.md`（D1/D2）；`scripts\test-ui.ps1`（`--ui-audit` + `ui-audit.json`，`IssueCount != 0` 即 exit 1）；`src\MechrevoLiteWin\Hardware\MechrevoDeviceCapabilities.cs:107-111`。
  - Acceptance: 注入集合外代号（`PH6AGxx`）→ 横幅出现且所有写入入口不可达（脚本断言控件状态）；注入集合内代号（`PH4TRX1`）→ 无横幅、功能可用；两断言各有独立测试用例名。 端到端接线测试（C5，见 Verification）。
  - QA happy: `T UnsupportedModelUiHappyTests` → `.omo/evidence/t10-h.txt`
  - QA failure: `T UnsupportedModelUiTests` → `.omo/evidence/t10-f.txt`
  - Commit: `feat(ui): add unsupported-model notice and read-only degrade`

### Wave C - 风扇表一一对应（只读：资产 + 身份 + 校验）

- [ ] 11. 校验风扇表数据：24 目录 × 6 文件与 `release\GCU-only` 副本**逐文件一致**（实测 144/144 SHA256 相同）；并落定 23 个 flat 文件（`DefaultFanTable_{Gaming,Office,Turbo}.json` + `M1T1..M4T5.json`）**归属 = 载荷资产**（随服务分发）→ 应用树**不需要**，本任务只校验、**不改树**（N4）。
  - References: `%TEMP%\opencode\ulw-notes\fan-tables.md` A/C；`ControlCenterX_5.56.60.26_Mechrevo\UserFanTables`（24 目录/144 文件）；`release\GCU-only\AiStoneService\MyControlCenter\UserFanTables`（24 目录 + 23 flat = 167 文件）；`release\GCU-40-51749\UniwillService\MyControlCenter\UserFanTables` 与 `release\GCU-40-51751\AiStoneService\MyControlCenter\UserFanTables`（各 0 目录 + 23 flat）。
  - Acceptance: 144 文件逐一哈希一致（差异清单为空）；flat 归属写入 todo 证据（"40 系载荷仅 flat，50 系载荷 flat + 24 目录"）；应用树未被写入。
  - QA happy: `T FanTableAssetParityTests` → `.omo/evidence/t11-h.txt`
  - QA failure: `T FanTableAssetParityFailTests` → `.omo/evidence/t11-f.txt`
  - Commit: `chore(fantable): verify per-model fan table assets and flat-file ownership`

- [ ] 12. 机型（**轴 1**） → 风扇表目录解析：以 `Enum.GetName(ProjectID, GetProject2ExID(GetProjectIdFromEC()))` 为唯一规则；目录缺失或 `Enum.GetName` 返回 `null` 时回退"读 EC 默认值"，**不得**回退到别的机型目录，该机型按 F3 判 `NotInSet`。
  - References: `%TEMP%\opencode\cc-51751-27\decompiled\MyControlCenter.MyFan.FanTable\FanTable_Manager1p5_CML.cs:45-51`（ctor：`GetProject2ExID` + `Enum.GetName` + `m_path` 拼接；该目录内唯一 `Enum.GetName` 实现，`:24-28` 仅为字段声明不可引）、`MyFanTableCtrl.cs:25-40`（1p5/_CML 管理器选择）；`%TEMP%\opencode\dl-gcuservice\full\GCUService.decompiled.cs:73503,73527,73532-73534,73778-73787`、`:73550-73571`（缺表回退）。
  - Acceptance: 24 代号各自解析到正确目录；`PH6TQxx`/`PH6AGxx` → `null`/缺表 + `NotInSet`，绝不跨机型。 端到端接线测试（C5，见 Verification）。
  - QA happy: `T FanTableResolutionTests` → `.omo/evidence/t12-h.txt`
  - QA failure: `T FanTableResolutionFailTests` → `.omo/evidence/t12-f.txt`
  - Commit: `fix(fantable): resolve fan table strictly by model identity`

- [ ] 13. **分组由实测键集派生**（禁止为凑 24 放宽断言）：规则 = 每机型取 6 文件顶层键**并集**归一，键集相同者同组。实测 4 组 = **9-key(5) / 11-key(8) / 12-key 含 CTGP(5) / 16-key 含 CTGP2/DB2/PL*_S2(6)**，`Σ == 24`；单文件另校验 `键集 ⊆ 组并集` 且 16 点 `{ID,UpT,DownT,Duty}` 完备（144/144 通过）。
  - References: `ControlCenterX_5.56.60.26_Mechrevo\UserFanTables`（逐文件解析实测）；`%TEMP%\opencode\ulw-notes\fan-tables.md` A/E（其 5/8/2/6 漏掉 PH4PUxx/PH4PGx1/PH4PGx2 → 按实测更正为 5/8/5/6）。
  - Acceptance: 分组由数据派生且 `Σ == 24`；`PH4PUxx` 归 9-key、`PH4PGx1`/`PH4PGx2` 归 12-key 含 CTGP；`PH6PRxx` M1 为 9-key 子集而 M2 含 CTGP → 按并集归组、按子集校验。
  - QA happy: `T FanTableGroupTests` → `.omo/evidence/t13-h.txt`
  - QA failure: `T FanTableGroupFailTests` → `.omo/evidence/t13-f.txt`
  - Commit: `test(fantable): derive per-group structural validation from actual key sets`

- [ ] 14. 规格类选择器：以 **EC 1934 bit6** 为唯一判据在 `RamFan1`/`RamFan1p5` 间选择；`RamFan2` 无实现路径（`FanTable_Manager2` 在 5.17.51 反编译中从未实例化）→ **显式断言"不可达"**。只校验服务将选中的规格类，不写 EC。
  - References: `%TEMP%\opencode\cc-51751-27\decompiled\MyECIO\MyEcCtrl.cs:233-238`；`%TEMP%\opencode\cc-51751-27\decompiled\MyControlCenter.MyFan.FanTable\MyFanTableCtrl.cs:25-40`；`%TEMP%\opencode\ulw-notes\fan-tables.md` D。
  - Acceptance: bit6 置位/清零两条路径均被测；错规格类（1p5 表按 RamFan2 解释）被显式拒绝；无 EC 写。
  - QA happy: `T FanSpecSelectionTests` → `.omo/evidence/t14-h.txt`
  - QA failure: `T FanSpecSelectionFailTests` → `.omo/evidence/t14-f.txt`
  - Commit: `fix(fantable): select fan spec strictly by EC capability bit`

- [ ] 15. **只读校验**门铃时序（**不再实现**、不写 EC）：`Set_APExistToEC(false)`（`CML:603`）→ `W(3935,mode)`（Gaming=2/Office=3/Turbo=1，`CML:604,607,610`）→ `W(3933,0xFD)` → `W(3934,0xC9)`（`CML:640-642`）→ 500 ms×10 轮询至 `3933!=0xFD && 3934!=0xC9`（`CML:643-655`）→ 读表 → `Set_APExistToEC(true)`（`CML:620`）；读回 `duty=Data/2`（`CML:330,358`）、写回 `>100→0xFF` 否则 `duty×2`（`CML:381,404`）。本任务只做三件只读的事：① **数据驱动期望序列表**（逐行：操作/地址/值/轮询/读回 + `source`）落盘为规格资产，由测试与厂商常量逐行比对；② 断言**服务**所需资产齐备（T11 的 144 文件 + flat）；③ 断言解析目录/规格类与**服务**将选中的一致（T12/T14 同源）。不可代码断言处：`source` 非空 + 行数==期望步数（同测试断言）。
  - References: `%TEMP%\opencode\cc-51751-27\decompiled\MyControlCenter.MyFan.FanTable\FanTable_Manager1p5_CML.cs:603-655,330,358,381,404`（下称 `CML`）；`%TEMP%\opencode\cc-51751-27\decompiled\MyControlCenter.MyFan.FanTable\FanTable_Manager1p5.cs:883-917`；`%TEMP%\opencode\ulw-notes\fan-tables.md` C。
  - Acceptance: 期望序列表由测试逐行比对通过（操作序列/模式值/0xFD/0xC9/500 ms×10/读回与写回规则），表内无空 `source`；资产齐备断言通过；目录/规格类与服务规则一致；**零** EC 写由**反射级断言**保证（R3-5）：显式枚举 `MechrevoLiteWin`+`Probe` 两程序集，程序集数==2 且类型数>0，否则失败（防空扫描假绿）；除 `EcChargeLimit` 外**无类型对 EC 写**——覆盖设备中介(IOCTL)**与非设备中介(PORT I/O，含经 `Pawn\` 载体)**两路（非源码 grep；仅 EC 口径）；并**显式禁止**经 `Pawn\` 载体 EC 写、由断言锁定；意图 = 除既有已证明逐字节等价的限充外不新增任何 EC 写路径。
  - QA happy: `T FanTableDoorbellSequenceTests` → `.omo/evidence/t15-h.txt`
  - QA failure: `T FanTableNoEcWriteFailTests` → `.omo/evidence/t15-f.txt`
  - Commit: `test(fantable): verify doorbell understanding and service-side assets read-only`

### Wave D - 逐代显卡切换与模式

- [ ] 16. 落盘**逐代（轴 2）**显卡切换事实表（代际**运行时探测**，不由平台代号推导；探测源 = GPU 营销名/PCI device-id 高字节，`BIOS_PROJECT_ID` 仅佐证、GPU 信号缺失时不得使用），每格标 PROVEN/INFERRED/UNKNOWN。**代际解析结果持久化**（仅硬件变更时重解析）；`Unknown`（无法判定）与"无 dGPU"是两个独立状态、**绝不并入默认代际**：
  | 代际 | 构建 | 载体 | 已证动作 | iGPU-only | RESTART |
  |---|---|---|---|---|---|
  | 30 | `ControlCenter_4.17.47.13_Mechrevo.zip` | MQTT `Setting/Control` | `DGPU_DIRECT_CONNECT_TOGGLE_ON`/`_OFF` **仅此二** | **确证不存在**（`IGPU_ONLY_*` 0 命中） | **确证不存在**（`*_RESTART` 0 命中） |
  | 40 | 5.17.51.27 | MQTT | `..._TOGGLE_ON/OFF/IGPU`、`..._RESTART`、`IGPU_ONLY_CONNECT_RB_ON/OFF` | **有**（仅 40A） | `..._RESTART` → `shutdown /r /t 0 /c "DGPU Direct Connect Toggle Switch"` |
  | 50 | 5.56.60.26 | MQTT（服务侧写路径 **UNKNOWN**：IL 混淆） | 同 40 系集合 + `GPU_HOTSWAP_ON/OFF` | **有** | **有**（`Task.Delay(800)` 后发） |
  - 30 系 PROVEN：`g30svc.a.txt:5144`(`_OFF`)/`:5438`(`_ON`)、`g30svc.w.txt:7497`；服务侧写机制 **UNKNOWN**。
  - 30 系控制台侧为**符号级证据（INFERRED，不得升为 PROVEN）**（.NET Native 元数据标识符堆 + 完整 PDB 符号表；该程序集无 IL 可反编译）：`.omo\evidence\g30-console-decompile.md`；动作词汇确证仅 `DGPU_DIRECT_CONNECT_TOGGLE_ON/_OFF`，`_IGPU`/`_RESTART`/`IGPU_ONLY_*`/`GPU_HOTSWAP_*`/`SetToWMIEC` 全载荷 0 命中；服务侧写路径仍 **UNKNOWN**。
  - 40 系 PROVEN：`MySettingManager.cs:1229-1271`；WMI/EC 魔数 40A 独有（`WMIEC.cs:403-472`）；dGPU 状态 170=fail、85=OK（`:1292-1311`）。**caveat**：`SetToWMIEC` 被解析但**从未调用**（`writeToWMIEC` 仅声明 `:1281`）；`_ManagerTimersTimer_Elapsed_IGPUonlyON/OFF` **无计时器挂接** → 厂商控制台侧 EC 写可能死代码；**显示路由写入由厂商服务承担（NVRAM），我方只发 MQTT**。
  - **值表（厂商服务侧 `OemDisplayMode`，仅记录厂商行为；控制台不写）**：AMD {1=direct,0=hybrid,2=igpu}；Intel {2=direct,4=hybrid,1=igpu}（同上 `:1235-1270`）；两编码**恰好相反**（故只走 MQTT，见 Scope）；30/50 系 **UNKNOWN**（不升级）。
  - 50 系：`..._RESTART` 在 `Task.Delay(800)` 后发、toggle-ON **发两次**（`CCUWinUI.decompiled.cs:86511-86515`/`:86485-86489`）；`GPU_HOTSWAP_ON/OFF` 处理器 no-op（`:139141-139142`/`:53420-53435`）。**构建级差异**：`5.17.49.19` 只有 ON/OFF 且无 iGPU-only（`%TEMP%\ulw-20260917-210442.md:156`）→ **门控键在运行时能力探针，非版本号。**
  - References: `%TEMP%\opencode\console-compare\g30svc.a.txt:5144,5438`；`g30svc.w.txt:7497`；`%TEMP%\opencode\cc-51751-27\decompiled\MyControlCenter\{MySettingManager.cs:1229-1271,1281,1292-1311, WMIEC.cs:403-472}`；`_decompiled\CCUWinUI.decompiled.cs:86485-86489,86511-86515,139141-139142`；`%TEMP%\opencode\ulw-notes\gpu-mode-matrix.md` A/C。
  - Acceptance: 每格带出处；**显示路由载体逐代仅为 MQTT**（动作/主题；30/50 服务侧写路径 UNKNOWN 保持）；30 系 iGPU-only/RESTART 标"确证不存在"；门控只读运行时能力；探测源无 `BIOS_PROJECT_ID`；"无法判定"与"无 dGPU"各有独立状态测试；解析持久化（仅硬件变更重解析）；**控制台不写 `OemDisplayMode`/任何固件变量、无写固件变量接缝**（独立测试锁定）。 端到端接线测试（C5，见 Verification）。
  - QA happy: `T GpuGenerationMatrixTests` → `.omo/evidence/t16-h.txt`
  - QA failure: `T GpuGenerationMatrixFailTests` → `.omo/evidence/t16-f.txt`
  - Runbook: `docs\hardware\first-machine-runbook.md` §2 (generation probe + display-route matrix)
  - Commit: `feat(gpu): record per-generation display-route matrix with evidence marks`

- [ ] 17. 显卡切换命令层（**MQTT-only**）：MQTT 动作 `DGPU_DIRECT_CONNECT_TOGGLE_ON/OFF/IGPU`、`IGPU_ONLY_CONNECT_RB_ON/OFF`（载荷 `SetToWMIEC:"OK"`）、`DGPU_DIRECT_CONNECT_RESTART`（800 ms 后），含重试与状态回读（动作集按 T16）；**显示路由全部经 MQTT；控制台不写任何固件变量、无写固件变量接缝**；**禁新增 EC 写**。
  - References: `%TEMP%\opencode\ulw-notes\console-protocol.md` B/E；`_decompiled\CCUWinUI.decompiled.cs:86477-86515,53527-53729,139137-139174`；`%TEMP%\opencode\ulw-notes\gpu-mode-matrix.md` A（WMI 路径**仅作事实记录**，不进实现）。
  - Acceptance: 每动作的主题/动作名/载荷字段与厂商一致；**命令层 MQTT-only**；**零新增 EC 写、零固件变量写**；30 系不出现 iGPU-only/RESTART；单元 TDD 走 T31(f) fake 传输，真实 broker 为独立可选步骤。
  - QA happy: `T GpuSwitchCommandTests` → `.omo/evidence/t17-h.txt`
  - QA failure: `T GpuSwitchCommandFailTests` → `.omo/evidence/t17-f.txt`
  - Runbook: `docs\hardware\first-machine-runbook.md` §2 (dGPU-direct/iGPU-only over MQTT)
  - Commit: `feat(gpu): add per-generation display-route switching over MQTT`

- [ ] 18. iGPU-only 语义（**50 系实测值**）：`IGPU_ONLY_CONNECT_RB_ON/OFF` 每 **2 s** 重发，`count > 60`（≈122 s）放弃，**每第 4 次**（`count % 4 == 0`，≈8 s）额外重发；成功判据 `CheckDGpuStatusforIGpuOnlyOnSuccess == 2`(ON)/`== 1`(OFF)；AUTO 成功依赖 AC；超时置 `IGpuCannotBeSwitchNowVisibility` 并经 `IGPUONLYCONNECTIONSWITCH_STATUS` 携带 `Status = preIGPUOnlyConnectionSwitch` 回滚。
  - References: `_decompiled\CCUWinUI.decompiled.cs:53527-53580`（`:53550` `Task.Delay(2000)`、`:53561` `count > 60`、`:53565` `count % 4 == 0`、`:53573-53577` 超时+回滚、`:53551`/`:53615` 成功判据）；`%TEMP%\opencode\ulw-notes\console-protocol.md` E。
  - Acceptance: 重试次数/间隔/成功判据与厂商一致（2 s / 60 / 每 4 次 / ==2 / ==1）；回滚路径被测；AUTO 受 AC 影响；**MQTT-only 且零新增 EC 写、零固件变量写**；重试/回滚语义不得改变；harness 同 T31(f)。
  - QA happy: `T IgpuOnlyRetryTests` → `.omo/evidence/t18-h.txt`
  - QA failure: `T IgpuOnlyRetryFailTests` → `.omo/evidence/t18-f.txt`
  - Runbook: `docs\hardware\first-machine-runbook.md` §2 (iGPU-only retry/rollback)
  - Commit: `fix(gpu): match vendor iGPU-only retry and rollback semantics`

- [ ] 19. 模式层**两套枚举分别建模，不得共用一套索引**：厂商 `SysPowerModeIndex`（Performance=1 / Balanced=2 / BatterySaver=3 / Benchmark=4）与我方 `OperatingMode`（Office=0 / Gaming=1 / Turbo=2 / Customize=3）。下发 `SET_OPERATING_MODE_DETAIL` 的**绝对量**（PL1/PL2/PL4、Tcc、AmdSPL/SPPT/FPPT、TGP target、DynamicBoost、时钟偏移、FanSwitchSpeed）。
  - References: `%TEMP%\opencode\ulw-notes\gpu-mode-matrix.md` B（`SysPowerModeIndex` 40B:1483-1492）；`%TEMP%\opencode\ulw-notes\console-protocol.md` B/F（`OperatingMode` 0..3 + 字段表）；`%TEMP%\opencode\dl-gcuservice\full\GCUService.decompiled.cs:64062-64087`（NVRAM `PowerMode`）。
  - Acceptance: 两套枚举各有类型与**显式命名**的转换函数（禁止隐式同值转换）；转换表有测试；字段键名/单位与厂商一致；`Tcc = TjMax − offset`；`PL4 ÷ 2` 当 double-flag；PL 为字符串；harness 同 T31(f)（单元 fake + 可选集成）。
  - QA happy: `T PowerModeEnumTests` → `.omo/evidence/t19-h.txt`
  - QA failure: `T PowerModeEnumFailTests` → `.omo/evidence/t19-f.txt`
  - Runbook: `docs\hardware\first-machine-runbook.md` §3 (mode enums + Intel/AMD discriminator)
  - Commit: `feat(modes): separate vendor and console mode enums, send absolute detail`

- [ ] 20. PL/PL2/PL4 与 Tcc **默认值按 SKU 从 EC 读**：GAMING `1840-1843`、OFFICE `1844-1847`、**TURBO `1959-1962`**（`GetTurboPLDefaultValue`，**不是** BatterySaver）、TCC `2008-2010`；读不到**禁用该模式功耗编辑**，不猜。**验收标 BLOCKED-HW**。
  - References: `%TEMP%\opencode\dl-gcuservice\full\GCUService.decompiled.cs:65047-65063`（Gaming）、`:65064-65080`（Office）、`:65081-65096`（**Turbo**：`:65087-65090` 读 EC 1959/1960/1961/1962）、`:64109`（调用点）。
  - Acceptance: 三组默认值来自 EC 实测（真机）；命名一律厂商词（Turbo）；"读不到"分支禁用 UI 与写入、无硬编码瓦数；**验收状态 = BLOCKED-HW**（写 `.omo\evidence\f3-generation-status.json` + 探针输出）。
  - QA happy: `T PlDefaultTests` → `.omo/evidence/t20-h.txt`
  - QA failure: `T PlDefaultFailTests` → `.omo/evidence/t20-f.txt`
  - Runbook: `docs\hardware\first-machine-runbook.md` §3 (EC-sourced PL/temperature defaults)
  - Commit: `feat(modes): source per-SKU PL defaults from EC, fail-closed`

- [ ] 21. cTGP / DynamicBoost / FanSwitchSpeed / Tcc 的归属与门控：`0x751` 编码（base `0x00/0xA0/0x10` + bit6 增压）、`cTGP/DB` 控制位 1859-1862、门控走矩阵。
  - References: `%TEMP%\opencode\cc-51751-27\decompiled\MyControlCenter.MyFan\MyFanManager_RamFan1p5_NV.cs:1028-1104`；`%TEMP%\opencode\dl-gcuservice\full\GCUService.decompiled.cs:24815-24872,64513-64535`。
  - Acceptance: 编码/解码双向测试；非支持机型不可达；`0x751` bit6（增压）与 `GetFanMode` 读回（bit4+bit7）一致。
  - QA happy: `T FanBoostEncodingTests` → `.omo/evidence/t21-h.txt`
  - QA failure: `T FanBoostEncodingFailTests` → `.omo/evidence/t21-f.txt`
  - Commit: `fix(fan): align boost/ramp encoding and gate by model support`

### Wave E - 载荷与打包（单载荷 = 50 系 `release\GCU-only`）

- [ ] 22. **单载荷化（owner 已决：全部机型用最新 50 系 GCU；G0 仅作删旧载荷树的证据闸门）+ 枚举/暂存同任务改完**。**G0（删旧载荷树/省体积前置闸门，T23-T26 同受约束）**：`release\GCU-only\AiStoneService\MyControlCenter\GCUService.exe`=**1.2.0.0**，`release\GCU-40-51749`、`release\GCU-40-51751` 两载荷=**1.0.2.70**——非同产品线增量构建 → 删 40 系载荷的前提 = 30/40 各一台真机非-BLOCKED-HW 证据：(a) 服务启动且 Ready；(b) `ItemSupport` 非空且代际相符；(c) GPU 切换 ON/OFF 均完成且重启回到先前模式；(d) `OemDisplayMode` 读回一致；**(c)/(d) 经 MQTT-only 执行（控制台无直写路径）**，使证据归于厂商服务；(e) 机箱风扇表可服务；(f) 限充可写；(g) 卸载后 13688 仅剩一个服务。**G0 未过 = fail-loud 且不删旧载荷树（不影响使用最新载荷，已决）**；证据落 `.omo\evidence\t22-gate.json`（**R3-1**：每条目内嵌原始探针——(i) EC 1856/1868 原始字节、(ii) dGPU PCI device-id、(iii) `ItemSupport` 转储或其哈希——各带确切命令+产物哈希；**30 系与 40 系两条目的机器身份必须不同**（EC project byte 和/或 device-id），同身份即 gate 无效；**BLOCKED-HW 条目不得计入 G0**）。**用户可见契约变更**：`/GCUVARIANT=40-51749`（`installer\README.md:95`）单载荷后失效，README/发布说明须声明。G0 通过后：① 盘点产物载荷并落证据；② `Select-GcuPayload.ps1` 改单载荷解析器：删 `PayloadByVariant` 40 两条（`:53-57`）、`:44-45`/`:183` `ValidateSet`、`:191-201` `-Variant`、`:98-107` 启发式；探测失败**非零退出+可读原因、禁止回落**；③ `Build-Installer.ps1:148-153` 4→1（留 `:155`）；④ `L-Mechrevo.iss:107-115` 4→1（`release\GCU-only\*`，含 `UWACPIDriver\`）；⑤ `Uninstall-Gcu.ps1` 不变。
  - References: `installer\L-Mechrevo.iss:107-115`（4 条 `[Files]`；无 `[Code]`/`Check:` → 选择器不影响落盘）、`:129`；`installer\Select-GcuPayload.ps1:44-45,53-57,98-107,191-201,213-225`（`:213-225` else = `40-51751` 兜底；`:239-263` SelfTest 需同步改）；`installer\Install-Gcu.ps1:21,222-244`；`installer\Build-Installer.ps1:148-153,155`；`release\` 实况：`GCU-only` 276 文件（自带 `UWACPIDriver\`）、`GCU-40-51749` 73、`GCU-40-51751` 75、`GCU-common` 4。
  - Acceptance: **G0 通过后**产物只有 1 套服务载荷树（未过 = 保留 40 系载荷）；缺 `release\GCU-only` 时 build+ISCC 构建期失败；选择失败非零退出（无兜底）；`installer\README.md` 4 处失真 + `/GCUVARIANT=40-51749` 失效（用户可见契约变更）列为交付物改点（`README.md:4,19,50,40-41,71,95`）；**R3-4**：`MechrevoLiteWin→Probe.csproj` 使 `Probe`（`Probe.csproj:4,5,8`：Exe/`net10.0`/`IsPublishable=false`/无 `PlatformTarget`）进入 publish 与安装载荷 → 发布文件/体积清单须更新；应用入口仍为 `MechrevoLite.csproj:21` 的 `StartupObject=MechrevoLite.Program`，`Probe` 的 `Main`/CLI 不得成为入口；两项目 MQTTnet 5.2.0.1603 / Newtonsoft.Json 13.0.4 / NvAPIWrapper.Net 0.8.1.101 同版本（`:89-91`=`Probe.csproj:18-20`），检查防未来漂移。
  - QA happy: `T InstallerPayloadSingleTests` → `.omo/evidence/t22-h.txt`
  - QA failure: `T InstallerPayloadNoFallbackFailTests` → `.omo/evidence/t22-f.txt`
  - Runbook: `docs\hardware\first-machine-runbook.md` §4 (T22 + G0 payload gate)
  - Commit: `build(installer): ship only the newest 50-series GCU payload`

- [ ] 23. 安装器"先卸后装"：先删 `GCUBridge` 服务与 legacy 注册，再装新载荷；确保 13688 唯一。**接管流程（C3，owner 要求；只带最新 GCU）**：① 检测既有厂商 GCU 环境（`GCUBridge` 服务 / 厂商载荷布局 / 厂商 `ItemSupport` 写入者 / 13688 占用者）；② 发现即**先卸后装**，并用**卸载后复检**证明移除确实成功；③ 装我方并**验证服务已安装且已启动**；④ 若移除后我方安装失败 → **回滚/恢复**，绝不让机器无 GCU 服务；⑤ **提示用户自行移除官方控制台应用**（不静默删除）。**已知问题**：运行时复制的 `{app}\GCU\<ServiceDir>` 与 `{app}\GCU\UWACPIDriver` **未在 Inno 登记**（`L-Mechrevo.iss:136-138` 只清日志）→ 卸载残留；修复 = 在 `[UninstallDelete]` 增 `Type: filesandordirs; Name: "{app}\GCU\AiStoneService"` 与 `"{app}\GCU\UWACPIDriver"`，纳入本任务验收。
  - References: `installer\Install-Gcu.ps1:138-152`（旧服务存在则 stop + `sc.exe delete` + 重建；`:150-152` `New-Service`；**不是** `InstallUtil`、**不是** `sc create`）、`:155-173`；`installer\Uninstall-Gcu.ps1:37-56,67-79,81-92,106`；`installer\L-Mechrevo.iss:132-134,136-138`。
  - Acceptance: **受 G0 约束（见 T22；未过不得删 40 系载荷）**；两代服务不并存；装后 13688 唯一；卸载步骤失败则安装中止并给明确原因；`[UninstallDelete]` 覆盖运行时复制的两处目录。**接管护栏（C3）**：任何时刻不得让机器无可用 GCU 服务；每步破坏性操作写日志且幂等；删除厂商控制台须为**提示**（交互与静默安装路径均可见，或明确记为仅交互）。**harness 必须指明**（`-DryRun` 干跑或 VM）；无 harness 则标 BLOCKED-HW。
  - QA happy: `T InstallerSequenceTests` → `.omo/evidence/t23-h.txt`
  - QA failure: `T InstallerUninstallFailTests` → `.omo/evidence/t23-f.txt`
  - Commit: `feat(installer): uninstall prior generation service and clean runtime copies`

- [ ] 24. 版本判定一律按 **SHA256**；禁止以版本号判断（**不共享版本线**：`GCU-only` 服务=**1.2.0.0**，40 系两条=**1.0.2.70**；同名文件版本可同、哈希不同）；**受 G0 约束（见 T22）**。**哈希清单本任务内定义（N1：无"7 份载荷哈希表"）**：产物 `artifacts\gcu-payload-manifest.tsv`，列 = `Variant | RelPath | Bytes | SHA256`，生成命令 = `Get-ChildItem release\GCU-only -Recurse -File | Select-Object Variant,FullName,Length,@{n='SHA256';e={(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}} | Export-Csv -Delimiter "`t" artifacts\gcu-payload-manifest.tsv -NoTypeInformation`（自校验）。
  - References: `%TEMP%\opencode\ulw-notes\vendor-gates.md`；**存在**的厂商 manifest 样例 `release\GCU-40-51749\SHA256SUMS.txt`、`release\GCU-40-51751\SHA256SUMS.txt`、`release\GCU-common\SHA256SUMS.txt`；`release\GCU-only\` **无** `SHA256SUMS.txt`（有 `install.bat`/`NOTICE.txt`/`README.txt`/`uninstall.bat` 等）。
  - Acceptance: **受 G0 约束（见 T22）**；版本比较函数只接受哈希；"同版本号不同哈希"用例存在且判"不同"；manifest 列名与生成命令落 todo 证据。
  - QA happy: `T PayloadIdentityTests` → `.omo/evidence/t24-h.txt`
  - QA failure: `T PayloadIdentityFailTests` → `.omo/evidence/t24-f.txt`
  - Commit: `fix(installer): decide payload identity by SHA256, never by version`

- [ ] 25. 防降级与幂等：已装更新的载荷时不覆盖；重复运行安装器不产生副作用。
  - References: `installer\Install-Gcu.ps1:246-263`（已装且健康 → 只复验签名与防火墙）；`tools\publish-update-json.ps1`（只算哈希、不上传包）。
  - Acceptance: **受 G0 约束（见 T22）**；幂等（跑两次结果一致）；降级被拒并提示；拒绝路径不写服务/驱动/防火墙。
  - QA happy: `T InstallerIdempotencyTests` → `.omo/evidence/t25-h.txt`
  - QA failure: `T InstallerDowngradeFailTests` → `.omo/evidence/t25-f.txt`
  - Commit: `feat(installer): add anti-downgrade and idempotency`

- [ ] 26. 安装后校验：服务唯一、13688 唯一、`ItemSupport` 由服务重写（不写死）、`ServiceReady` 就绪。**静默失败护栏**：Inno `[Run]` 无 `ignoreerrors`（`L-Mechrevo.iss:129`）→ `Install-Gcu.ps1` 非零退出**只进日志、不回滚**，`/VERYSILENT` 下即"装成功但 GCU 缺席"；本任务使该失败面可检测。
  - References: `%TEMP%\opencode\dl-gcuservice\full\GCUService.decompiled.cs:11381,53152-53153,53351,54030-54044`（ServiceReady 门禁）；`%TEMP%\opencode\ulw-notes\vendor-gates.md` A（`ItemSupport` 由 `RefreshItemSupportReg` 写）；`installer\L-Mechrevo.iss:129`；`installer\Install-Gcu.ps1:303-312`（catch → `exit 1`）；`installer\Build-Installer.ps1:202,206`。
  - Acceptance: **受 G0 约束（见 T22）**；四项校验各有可执行断言；失败给可操作原因（含 `%ProgramData%\L-Mechrevo\logs\gcu-install-*.log` 的 `FATAL:` 行）；`ServiceReady` 由服务置 1 而非我们写；接管后服务/端口唯一性校验见 T23；+(链路证明+runbook)。
  - QA happy: `T PostInstallVerificationTests` → `.omo/evidence/t26-h.txt`
  - QA failure: `T PostInstallVerificationFailTests` → `.omo/evidence/t26-f.txt`
  - Runbook: `docs\hardware\first-machine-runbook.md` §5 (post-install verification)
  - Commit: `feat(installer): verify service readiness after install`

### Wave F - 收敛与清理

- [ ] 27. 84 分支点收敛到矩阵/识别层：逐条处置（矩阵化/归入识别层/保留并注释理由），产出对照表；`E1-E8`、`C1-C22`、84 站点**各自唯一指派**（见 `### Metis 缺口折叠` 的交叉对照表）。
  - References: `%TEMP%\opencode\ulw-notes\console-coupling.md:3`（84 = A:8 + B:41 + C:22 + D:5 + E:8）、A/B 两节（84 站点清单）。
  - Acceptance: 对照表覆盖 84/84；无未处置项；指派表无重复归属。
  - QA happy: `T BranchConvergenceTests` → `.omo/evidence/t27-h.txt`
  - QA failure: `T BranchConvergenceFailTests` → `.omo/evidence/t27-f.txt`
  - Commit: `refactor(gating): converge model branches onto the matrix`

- [ ] 28. `MechrevoDeviceCapabilities.Invalidate()`（`Hardware\MechrevoDeviceCapabilities.cs:110-114`）无调用者问题：明确能力快照生命周期（失效/重建时机），消除"配置改后仍用旧快照"。
  - References: `%TEMP%\opencode\ulw-notes\console-coupling.md` D4；`src\MechrevoLiteWin\Hardware\MechrevoDeviceCapabilities.cs:86-99,102-108,110-114,117-120`；`src\MechrevoLiteWin\Settings.cs:127`（表单生命周期自带 `Load()` 快照）。
  - Acceptance: 生命周期明确且有测试；服务重写 `ItemSupport` 后能刷新；`OverrideCurrent` 与 `Invalidate` 的交互有测试锁定。
  - QA happy: `T CapabilitySnapshotTests` → `.omo/evidence/t28-h.txt`
  - QA failure: `T CapabilitySnapshotFailTests` → `.omo/evidence/t28-f.txt`
  - Commit: `fix(caps): define and wire capability snapshot lifetime`

- [ ] 29. 卸载/异常路径健康：`NoGpu()` 反转可见性修正（**E3 = INFERRED → 先取证再改**）；ASUS 残留谓词在活跃路径的清理（**E2 = INFERRED → 先取证再改**）。取证结论入本 todo 证据。
  - References: `%TEMP%\opencode\ulw-notes\console-coupling.md` E2/E3、UNKNOWN#6；`src\MechrevoLiteWin\AppConfig.cs:553-556`（`NoGpu`）、`:500-613`（ASUS 谓词区）；`src\MechrevoLiteWin\Settings.cs:3998,4482`。
  - Acceptance: 取证与处置只接受枚举值并落 `.omo/evidence/t29-evidence.json`（`outcome` ∈ {CONFIRMED, REFUTED}、`action` ∈ {CORRECTED, KEPT_WITH_REASON}、`lines`），由测试解析校验；可见性语义与真实含义一致并被测试锁定；活跃路径不再引用 ASUS 谓词（保留项必须逐条 `KEPT_WITH_REASON`）。
  - QA happy: `T VisibilitySemanticsTests` → `.omo/evidence/t29-h.txt`
  - QA failure: `T VisibilitySemanticsFailTests` → `.omo/evidence/t29-f.txt`
  - Commit: `fix(ui): correct inverted visibility and drop stale predicates`

- [ ] 30. 文档与运维：把本次勘查的机型/功能/风扇表/显卡/模式矩阵写入 `docs\hardware\`，作为后续机型接入的唯一参考。
  - References: `%TEMP%\opencode\ulw-notes\{vendor-gates.md,console-coupling.md,fan-tables.md,console-protocol.md,gpu-mode-matrix.md}`；`docs\hardware\project-ids.json`；`docs\hardware\README.md`（已存在 → 本任务 = 扩展，非新建）。
  - Acceptance: 文档含 24 机型表、5 张名单、4 个风扇表分组（5/8/5/6）、逐代显卡表（PROVEN/INFERRED/UNKNOWN 标注、30 系 iGPU-only 缺失、平台代号→代际为 INFERRED 且列矛盾出处）、两套模式枚举对照；与代码同源（生成或校验脚本）。
  - QA happy: `T HardwareDocsParityTests` → `.omo/evidence/t30-h.txt`
  - QA failure: `T HardwareDocsParityFailTests` → `.omo/evidence/t30-f.txt`
  - Commit: `docs(hardware): publish per-model support matrices`

### Wave G - 现场缺陷修复（beta17 收集）

owner 依据 `docs\hardware\beta17-field-bugs.md` 新增；均为**当前未修复**缺陷；路径未写全者在 `src\MechrevoLiteWin\`。

- [ ] 32. **休眠唤醒后键盘灯效丢失**（#4 耀世16u；复现=休眠后唤醒）。resume 链 `Program.cs:1469→1474-1488→649`，RGB 重连 `Hardware\KeyboardRgb.cs:503,362`。使已启用灯效 resume 后恢复一次，关灯不得点亮。
  - References: `Program.cs:1451,1469,1474-1488,649`；`Hardware\KeyboardRgb.cs:503,134,362`。
  - Acceptance: Resume 后原开→恰一次恢复请求、原关→零请求；两用例独立命名；缺陷未修复。
  - QA happy: `T KeyboardLightingResumeTests` → `.omo/evidence/t32-h.txt`
  - QA failure: `T KeyboardLightingResumeFailTests` → `.omo/evidence/t32-f.txt`
  - Runbook: `docs\hardware\first-machine-runbook.md` §6 (keyboard lighting after resume)
  - Commit: `fix(keyboard): restore keyboard lighting effect after resume`

- [ ] 33. **屏幕无法熄屏**（#6 耀世15pro4060；复现=手动调整恒失败）。`Display\ScreenBlankController.cs:64` 仅 `Display\ScreenBrightness.cs:24` 成功时压黑，失败即放弃（`:73-93`）。失败须可检测提示，不得静默当已熄屏。
  - References: `Display\ScreenBlankController.cs:64,73-93,108`；`Display\ScreenBrightness.cs:24,53`；`Settings.cs:646,668`。
  - Acceptance: WMI 缺失/写失败→不置「已熄屏」且报可检测失败；可用→压黑并按输入/watchdog 恢复。
  - QA happy: `T ScreenBlankTests` → `.omo/evidence/t33-h.txt`
  - QA failure: `T ScreenBlankFailTests` → `.omo/evidence/t33-f.txt`
  - Runbook: `docs\hardware\first-machine-runbook.md` §7 (screen blank)
  - Commit: `fix(display): make screen blank fail loudly instead of silently`

- [ ] 34. **调色无法切换 sRGB 等**（#7 耀世15pro4060；复现=手动调整恒失败）。写入 `Hardware\MechrevoService.cs:612`（HDR 拒 `:622`）；UI `Settings.cs:909→935`；`Display\ScreenCCD.cs`/`Display\ScreenControl.cs` 仅色彩/HDR 控制，不含 sRGB 本体。切换须读回一致或明确失败。
  - References: `Hardware\MechrevoService.cs:612,622,703,784`；`Display\ScreenCCD.cs:11`。
  - Acceptance: 读回==请求（sRGB=2）方成功；HDR 开/未连接/不支持各独立失败用例且不写错档。
  - QA happy: `T ColorCalibrationSwitchTests` → `.omo/evidence/t34-h.txt`
  - QA failure: `T ColorCalibrationSwitchFailTests` → `.omo/evidence/t34-f.txt`
  - Runbook: `docs\hardware\first-machine-runbook.md` §8 (colour-profile sRGB switching)
  - Commit: `fix(display): make colour-profile switching take effect or fail visibly`

- [ ] 35. **底栏文字/图标重叠 + UI 缩放错位**（#8 无界15Xpro ai9H365；#10 耀世16u，beta8 后仍在）。底栏 `Settings.V2.cs:923,1019`；样式/自绘 `UI\UiVisualStyle.cs:317,325`/`UI\RButton.cs:194,291`；DPI `UI\RForm.cs:135`、`UI\UiDpi.cs:41,47`、`Settings.cs:3194`。缩放下不得重叠、DPI 重排正确。**缺陷修复，非 OUT 的 UI 形态改造。** **本轮为严格必做项（round 5，owner：strictly fix）——非可选、非推断。**
  - References: `Settings.V2.cs:923,1019`；`UI\UiVisualStyle.cs:291,317,325`；`UI\UiAuditRunner.cs:645,679`；`UI\UiDpi.cs:41,47`；`Settings.cs:3194`。
  - Acceptance: 各视口 `ui-audit.json` sibling/footer findings==0；底栏文字/图标矩形不相交；DPI 重排与倍率一致。**严格/必做（round 5，非可选、非推断）。** +(链路证明+runbook)。
  - QA happy: `T FooterOverlapDpiTests` → `.omo/evidence/t35-h.txt`
  - QA failure: `T FooterOverlapDpiFailTests` → `.omo/evidence/t35-f.txt`
  - Runbook: `docs\hardware\first-machine-runbook.md` §9 (status-bar overlap + DPI scaling)
  - Commit: `fix(ui): stop footer text/icon overlap and align DPI scaling`

- [ ] 36. **重启后无法自动启动**（#10 耀世16u；复现=重启）。自启=用户级计划任务 `Helpers\Startup.cs:52,214,416`（`:97` 拒临时镜像）。查未触发/未注册/路径失效真因并可靠自启（缺失但已启用→重建）。
  - References: `Helpers\Startup.cs:52,97,214,416`；`Settings.cs:510`。
  - Acceptance: 注册后重启被触发（真机；无则 BLOCKED-HW 写 F3 证据）；任务缺失且已启用→自动重建；临时镜像不注册。
  - Acceptance（外来 GCU 处置，C3 重定义）：检测外来 `GCUBridge` 服务/外来 13688 占用者/外来 `ItemSupport`（`HKLM\SOFTWARE\OEM\GamingCenter2\ItemSupport`）写入者；**交给安装器 T23 接管（卸载→装我方→回滚护栏），厂商「官方控制台」应用不静默删除而改为提示用户**。常量 `MechrevoHw.cs:34`、`MqttSecurity.cs:20,92`、`OfficialConsoleIsolation.cs:123`。**撤回**：先前「卸官方台报 SEVERE → 共存冲突」为误推，该列为示例、非真缺陷（round 5）。
  - QA happy: `T AutostartRebootTests` → `.omo/evidence/t36-h.txt`
  - QA failure: `T AutostartRebootFailTests` → `.omo/evidence/t36-f.txt`
  - Runbook: `docs\hardware\first-machine-runbook.md` §10 (reboot autostart + coexistence prompt)
  - Commit: `fix(startup): make reboot autostart reliable and warn on console coexistence`

#### 现场证据（全表 `docs\hardware\beta17-field-bugs.md`）
- 苍龙16ultra5090/24极光X/25耀世16u u9 70ti 同症状卡 iGPU/显示路由切换 → 30/50 系路由行证实前**保持 UNKNOWN**（T16/T17/T18）。
- Intel 14650HX（旷世16pro）PL1/PL2/PL4 读成 AMD 值 → 平台判定不可靠；T19/T20 权威源应为 **dGPU PCI vendor/device-id** 并测试。
- 耀世16u 报限充无效 → 佐证 T7。

## Final verification wave

- [ ] F1. 计划合规审计：五字段齐备、验收无需人工判定、TDD 可验证；证据绑定：每个已执行 QA 的 `.omo\evidence\<id>.txt` 存在、含可复跑命令与退出码、`.sha256` 一致（散文式不合格）。
- [ ] F2. 代码质量评审：分层是否清晰、有无巨型 switch 复制厂商判据；错误处理是否 fail-closed（且**仅**对 EC 派生位，常量位镜像厂商默认）。
- [ ] F3. 真机 QA：**代际冒烟**（≥1 台 30/40/50 系：机型识别、功能置灰、显卡切换、模式下发、限充）× **逐机箱表驱动测试**（24 平台代号，**不需真机**，注入 EC 字节驱动）。
  - **一台机器/代际无法验证 24 个平台代号**（机型只是轴 1）→ 24 代号验证由表驱动测试承担，真机只做代际冒烟。
  - 无真机的代际/机型：写 `.omo\evidence\f3-generation-status.json`（形如 `{"30":{"status":"BLOCKED-HW","probe":"<cmd>","raw":"<输出>","hash":"<SHA256>"},"40":{...},"50":{...}}`）→ **该文件即 F3 完成条件**；条目须含 `probe`+`raw`+`hash`，缺一即未完成；**BLOCKED-HW 条目永不满足 G0（T22）**。
- [ ] F4. 范围忠实性：未引入 20 系适配；**未新增 EC 写路径**（门铃只读；EC 写仅 `EcChargeLimit`）；**无固件变量写**；唯一许可的非 EC 直写载体 = 既有 `Pawn\` SMU 路径、无新增特性；**显示路由载体 = MQTT**；未改 UI 形态、未触碰 `release\`、未夹带发布工作。

### Metis 缺口折叠 - 绑定修正（本节优先于上文冲突处）

Metis 分析：`%TEMP%\opencode\ulw-notes\metis-findings.md`（28 条）——要点已就地写入 todo 正文，冲突以正文为准；round 3（R3-1..R3-5）见 T9/T15/T22/T31 与 `.omo\drafts\plan-r2-changelog.md`。唯一不可替代的绑定如下：
- 交叉对照表（唯一指派）：E1→T8；E2/E3→T8(取证)+T29(清理)；E4/E6/E7/E8→T8；E5→T8+T21；C1→T7；C2-C6/C17-C18/C21-C22→T9；C7-C13→T9（`ContainsModel("503")`→T8）；C14-C16→T8；C19→T21；C20→T8；84 站点→T27。
- 轴 1/轴 2 分离：24 平台代号不打代际标签；平台代号→代际 = INFERRED（矛盾 `docs\upgrade-from-openrevo.md:140` vs `docs\gcu-dependency-matrix.md:88`），登记未决。
- "84" 口径：`console-coupling.md:3` = A 8 + B 41 + C 22 + D 5 + E 8。
- **F5 修正（option b，详见 `## Scope`）**：EC 直写仍禁（例外 = 限充）；显示路由 `OemDisplayMode` 只走 MQTT、控制台不写固件变量；唯一许可的非 EC 直写载体 = 既有 `Pawn\` SMU 路径；Wave C 只读；约束 EC 写与固件变量写。

## Commit strategy

- 一个 todo 一个提交，提交信息用每个 todo 末尾的 `Commit:` 行。
- 每个提交自洽：实现 + 其测试 + 必要的数据文件。
- 波次内历史线性；不压多 todo 提交。
- 提交前跑该波次测试与构建门禁（0 错 0 警）。
- 严禁在提交中夹带 `release\` 下任何文件的变更。

## Success criteria

1. **两条正交轴**：24 平台代号（轴 1）可识别、单一真源、fail-closed；dGPU 代际（轴 2）仅运行时探测，无"平台代号→代际"推断。
2. **机型 × 功能矩阵**只读服务写入的 `ItemSupport`（仅 EC/NVRAM 位 fail-closed、常量位镜像默认、不重推），覆盖 ~20 能力位与 5 张名单；`YAOSHI` 已移除。
3. **24 机型风扇表**一一对应随载荷就位；规格类按 EC 1934 bit6；门铃**只读校验**；缺表走 EC 默认不跨机型；分组由实测键集派生且 `Σ == 24`。
4. **显卡切换**按逐代事实表（PROVEN/UNKNOWN）**MQTT-only**；不写任何固件变量；**零新增 EC 写、零固件变量写**；iGPU-only 重试/回滚与厂商一致；30 系不出现 iGPU-only/RESTART。
5. **模式下发**用绝对量；两套枚举分别建模；PL/Tcc 默认值来自 EC 实测（TURBO 命名），读不到即禁用而非猜测。
6. **exe 只含 50 系 GCU 载荷**（G0 通过后）且选择器失败即响；先卸后装；SHA256 判版本；幂等防降级；装后四项校验；卸载不留运行时复制目录。
7. 三门禁绿；新代码先测试（TDD）；每个 QA 的 `.omo\evidence\<id>.txt`+`.sha256` 可复跑。
8. 不支持机型提示 + 只读降级、零写入（D1）；F3 的 BLOCKED-HW 凭据落 `.omo\evidence\f3-generation-status.json`（含 `probe`/`raw`/`hash`）。

- **N6（owner 决定 A，2026-09-18）**：G0 已被业主推翻——最新 GCU 向下兼容至 30 系（10/20 系大概率不支持），厂商自己就是一个 GCU/控制台覆盖全部 24 个平台代号，且 elease\GCU-only\...\UserFanTables = 24 个 per-model 机箱目录 + 23 flat，而 40 系两套只有 flat、零 per-model 目录，故最新载荷是超集。安装器改为只打包 elease\GCU-only（自带 UWACPIDriver，故不再打包 elease\GCU-common）；elease\ 源目录一字未动，留作可见退路的材料。替代 G0 的安全网 = 装后自检 + 可见退路（无下载服务器，只告知取哪个载荷与位置）。
