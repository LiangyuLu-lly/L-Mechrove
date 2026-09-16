# beta18 — 安装器与更新链设计

设计日期：2026-09-16。本文是**决策文档**：所有条目均为已定决策，不是备选项。依据来自
`installer\L-Mechrevo.iss`、`installer\Build-Installer.ps1`、`installer\Select-GcuPayload.ps1`、
`installer\Install-Gcu.ps1`、`installer\README.md`、`tools\publish-update-json.ps1`、
`tools\publish-server-metadata.ps1`、`src\MechrevoLiteWin\Updates\`（UpdateChecker /
UpdatePolicy / UpdateInstaller / UpdateForm）、`docs\beta17-gcu-variant-selection.md` 与
`CHANGELOG.md`。凡未在仓库中核实的事实一律标注 `【待确认】`；2026-09-16 一轮已把 beta18 的五处候选判据逐条核实或定稿，标签相应改为 `【已核实 2026-09-16】` / `【推迟决策 2026-09-16】`。

## 1. 目标与不变量

beta18 要把"安装器"和"应用内更新"收拢成一条链：安装器是唯一的安装与升级入口，应用内
更新只负责"发现新版本 → 下载安装器 → 校验 → 拉起安装器"。

以下不变量在任何失败路径下都必须成立：

1. **升级永远不能把机器留在"没有可用的应用"的状态。** 这是"不预卸载、原地覆盖"决策
   （第 2 节）的根本理由：先卸载再安装的流程一旦中途失败，用户手里就什么都没有了；覆盖式
   升级失败时，旧文件大部分仍在，最坏情况是"新旧文件混合"（第 2 节：Inno 无原子性、不
   还原已覆盖文件），应用仍可启动或按第 5 节的横幅引导重跑安装器。
2. **校验不过就不装。** SHA-256 缺失、非法或不匹配一律 fail-closed（沿用
   `UpdatePolicy.IsValidSha256` / `UpdateInstaller.Verify` 的既有纪律），绝不降级成
   "只做结构校验"。
3. **绝不静默安装。** 安装只发生在用户明确点击之后（沿用 `UpdateForm` 的既有纪律）。
4. **绝不降级。** 客户端版本比较用本机版本（`UpdateChecker` 已如此），GCU 载荷不回退
   （第 4 节）。
5. **GCU 载荷四套全保留。** beta17 已定论（`docs\beta17-gcu-variant-selection.md`）：
   没有可靠的机型→控制台映射，删任何一套都会让部分机型失去可达载荷。

## 2. 安装模型

**决策：机器级安装，一次 UAC。** 现状已是如此且保持不变：`L-Mechrevo.iss` 里
`PrivilegesRequired=admin`、`DefaultDirName={autopf}\L-Mechrevo`（即
`%ProgramFiles%\L-Mechrevo`）。整个安装过程只出现一次 UAC 提权（安装器进程自身提权，
内部的 GCU PowerShell 步骤以 `runhidden` 在已提权上下文中运行，不再二次弹窗）。

**决策：不预卸载，原地覆盖升级。** 升级 = 用**同一个 AppId**
（`{{8F4E2C71-9B3A-4D6E-A1C2-7E5B9D0F3A64}`）重新运行安装器，配合
`UsePreviousAppDir`（Inno 默认行为：已装过则沿用上次的安装目录，不再询问路径）。
效果是纯粹的覆盖安装：文件被 `ignoreversion` 覆盖，注册表卸载项被新版本号替换，
`{app}\GCU` 下的载荷与脚本被刷新。理由见第 1 节不变量 1。

文件布局（现状，保持不变）：

| 位置 | 内容 |
|---|---|
| `{autopf}\L-Mechrevo` | `L-Mechrevo.exe`、四份随附文档 |
| `{app}\GCU` | `Install-Gcu.ps1`、`Uninstall-Gcu.ps1`、`Select-GcuPayload.ps1` |
| `{app}\GCU\payload\{50, 40-51749, 40-51751, common}` | 四套厂商载荷原样暂存 |
| `{app}\GCU\<ServiceDir>`、`{app}\GCU\UWACPIDriver` | 安装时按选择规则复制出的活动载荷 |
| `%ProgramData%\L-Mechrevo\logs` | GCU 安装/卸载日志 |
| `HKLM\SOFTWARE\L-Mechrevo` | GCU 安装标记（`GcuVariant` / `GcuServiceDir` / `GcuServiceExe` / `GcuInstalledUtc`，见 `installer\Install-Gcu.ps1:190-204`） |

**失败升级的行为（【已核实 2026-09-16】）。** 结论先行：**Inno 对覆盖式升级不承诺原子性，
也没有内建的"备份并恢复被覆盖文件"能力**，部分覆盖会真实残留。官方依据：
① FAQ "What exactly happens when the user clicks Cancel during an installation?"——取消/中止时
Setup 只是"以与卸载程序相同的方式"回退它已做的更改（撤掉自己新建的文件、目录、注册表项），
而卸载逻辑本来就不还原被替换文件的内容；② FAQ "Making Backups Before Replacing Files"——
Inno "目前没有备份被替换文件的功能"，要备份只能自己加一条 `external` 的 `[Files]` 条目把旧
文件抄到备份目录；③ `[Files]` 主题备注——替换被占用文件时最多重试 4 次（每次间隔 1 秒），
仍失败则报错；静默安装（`/VERYSILENT /SUPPRESSMSGBOXES`）下 Abort/Retry/Ignore 默认 Abort、
安装中止。来源：https://jrsoftware.org/isfaq.php 与
https://jrsoftware.org/ishelp/topic_filessection.htm。

本安装器的事实不改变上述语义：AppId `{8F4E2C71-…}` + `UsePreviousAppDir`（默认 yes）只决定
复用目录与卸载日志；`PrivilegesRequired=admin`、`CloseApplications=yes` 会在复制前用
Restart Manager 关闭占用 `{app}` 文件的应用（静默模式下自动关闭；`RestartApplications=no`
表示关闭后不代其重启），把"文件被占用"这一最常见的中途失败尽量前移消化；
`Compression=lzma2/max → ultra64` 只影响压缩流，不改变复制/覆盖语义。（`L-Mechrevo.iss`
的 `[Files]` 条目全部带 `ignoreversion`，Inno 因此无条件覆盖、连降级也不拦，"绝不降级"的
纪律由第 4/5 节在 Inno 之外执行。）

**缓解（设计采纳：接受部分覆盖 + 重跑恢复）。** 不追求安装器级原子性，把"新旧混合"当成可
恢复状态：① `[Files]` 之后才跑 GCU（`[Run]` 条目，`installer\L-Mechrevo.iss:129`），复制
阶段失败时 GCU 步骤不会执行，旧 GCU 保持可用；② GCU 步骤幂等（`Install-Gcu.ps1` 的 fast
path / repair 语义），重跑安装器即可修复；③ 第 5 节第 7 步的"重跑安装器"横幅 + pending
标记给出用户可见的恢复路径；④ **不做 copy-then-rename**：Inno 的 `[Files]` 引擎不提供跨
文件列表的原子交换（文档只在 `external` + `issigverify` 条目上描述"先写 `.tmp`、验签通过
再改名"，那是单文件内部步骤，不是事务），逐文件替换仍无法整体回滚，收益为零。若将来要求
真正的原子升级，只能改成"按版本装进独立目录 + 切换入口"的自管布局，超出 beta18 范围。
卸载流程（`Uninstall-Gcu.ps1` 先停服务删驱动再删文件）不受本决策影响。

## 3. 运行时依赖

**决策：.NET 10 Desktop Runtime 不打进安装包。** 安装器在安装时检测本机是否已有
.NET 10 Desktop Runtime；缺失则**下载并静默安装**；机器离线时只展示手动下载链接，
不阻塞安装流程的其他部分。

- **现状背景**：beta17 的发布形态是自包含单文件（`Build-Installer.ps1` 检测
  `singleFile`，`installer\README.md` 明确"no .NET download needed"）。beta18 采纳
  本决策意味着发布形态转向框架依赖（exe + 少量随附文件），安装器体积因此显著下降，
  与第 8 节的压缩决策叠加。发布形态切换是 beta18 的实施项（第 11 节步骤 2）。
- **"缺失"如何判定（【已核实 2026-09-16】）**：官方检测文档 "Check installed .NET versions on
  Windows, Linux, and macOS"
  （https://learn.microsoft.com/en-us/dotnet/core/install/how-to-detect-installed-versions）
  只给出 CLI 方法、不记录任何注册表键。定稿：**主判据**
  `%ProgramFiles%\dotnet\dotnet.exe --list-runtimes`，匹配输出里是否存在
  `Microsoft.WindowsDesktop.App 10.` 行——这是官方文档记录的输出格式（同页示例），不依赖
  PATH、不用随包分发工具，且与框架依赖应用的运行时解析位置一致。**次级兜底**（仅当
  `dotnet.exe` 不存在时）：读注册表
  `HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App`
  下是否存在 `10.` 开头的版本子键——该键由 .NET 安装器写入（社区实测随 Desktop Runtime
  安装/卸载增删），但**未见于任何官方文档**，属实现细节，只作兜底、不作主判据。
  **明确弃用 `sharedhost`**：它记录的是 host 的安装状态，社区（Microsoft Q&A）报告 Desktop
  Runtime 的安装/卸载不保证同步更新该键，不能代表 `Microsoft.WindowsDesktop.App` 是否在位；
  文档草案此前建议的 `sharedhost\InstallSuccess` 判据作废。`netcorecheck` 需要额外分发一个
  官方工具且不在上述官方文档里，不采用。
- **静默安装（【已核实 2026-09-16】）**：官方 "Install .NET on Windows"
  （https://learn.microsoft.com/en-us/dotnet/core/install/windows 的 Command-line options）
  确认 .NET 安装器 exe 的静默参数就是 `/install /quiet /norestart`；退出码 **0 = 成功、
  3010 = 成功但需要重启**、其它值按错误处理。3010 按第 6 节"建议重启、不强制"映射为
  "成功 + 完成页提示重启"，不算失败；更新器侧仍按"非零 = 未确认完成"（第 10 节第 2 条）。
- **下载源（【已核实 2026-09-16】）**：用 `builds.dotnet.microsoft.com` 上的**版本固定直链**。
  截至 2026-09-16，.NET 10 最新补丁是 **10.0.12（2026-09-08 发布）**，官方 release notes：
  https://github.com/dotnet/core/tree/main/release-notes/10.0 ，离线安装器即
  `https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.12/windowsdesktop-runtime-10.0.12-win-x64.exe`
  （同一 CDN/命名规则见官方下载页 https://dotnet.microsoft.com/download/dotnet/10.0 的
  "Direct link"），落地后用官方 SHA-512 清单
  `https://builds.dotnet.microsoft.com/dotnet/checksums/10.0.12-sha.txt` 校验。
  **不存在"永远最新"的官方安装器 aka.ms 直链**（官方文档化的 aka.ms 只有下载落地页
  `https://aka.ms/dotnet-core-download`），所以实施时**必须把 10.0.x 版本号钉进安装器**，
  随 .NET 补丁在发布时更新。
- **发布形态前提（后果保持可见）**：beta17 现网产物是自包含单文件（仓库核实：
  `installer\Build-Installer.ps1:138` 以 `L-Mechrevo.dll` 是否存在判定 `singleFile`；
  `installer\README.md` 写明自包含发布不需要 .NET 下载）。上述检测/静默安装/离线回退三步
  **只有在第 11 节步骤 2 把 csproj 切到框架依赖发布后才成立**；若仍按自包含发布，本节步骤
  必须整体跳过，否则会做一次无意义的运行时下载。
- **离线回退**：检测到无网络（下载失败）时，安装页展示官方下载页链接，用户可手动
  安装后继续；安装器不因此中止，GCU 与文档步骤照常完成，应用首次启动时若运行时
  仍缺失由 apphost 给出系统级提示。
- **一次 UAC 约束**：.NET 运行时安装器在安装器已提权的上下文中静默运行，不产生
  第二次 UAC 弹窗。

## 4. GCU 载荷策略

**决策：仅在缺失或更旧时安装；先停服务；绝不降级；保留代际选择与 `/GCUVARIANT=` 覆盖。**

现状（`Install-Gcu.ps1`）已具备的部分：

- **先停服务**：`Stop-GcuProcesses` 会停 `GCUBridge` 服务并强杀残留的
  `GCUService`/`GCUBridge` 进程。beta18 决策把它从"仅 fast-path 未命中时执行"改为
  **凡是要复制载荷就先停**（现状 fast path 命中且服务运行中时根本不复制，语义兼容；
  需要复制时现状也会先停，因此这条主要是把顺序固化为显式规则）。
- **幂等修复**：fast path（binPath 指向预期 exe + 三个关键文件在位 + 服务 Running）
  只重验 Authenticode 与防火墙规则；否则复制、验签、装驱动、重建服务、启动。
- **代际选择**：`Select-GcuPayload.ps1` 按 GPU 名称 / PCI device id / BIOS_PROJECT_ID
  判代际，50 系 → `payload\50`，40 系（含未知）→ `payload\40-51751`；
  `/GCUVARIANT=40-51749` 手工覆盖保持不变（beta17 决策，见
  `docs\beta17-gcu-variant-selection.md`）。

beta18 需要新增的部分：

- **"缺失或更旧"判定（取值源，【已核实 2026-09-16】）**：安装前读取
  `HKLM\SOFTWARE\L-Mechrevo` 安装标记与目标 `GCUService.exe` 的文件版本，与本次捆绑载荷
  的版本比较；**载荷版本 ≥ 已装版本时跳过 GCU 步骤**（只保留 fast path 的验签），
  **已装版本更新时绝不覆盖（不降级）**。取值源只读核实定稿：
  - 已装变体 = 标记值 `GcuVariant`（实际值名见 `installer\Install-Gcu.ps1:190-204` 的
    `Set-InstallMarker`，标记同时含 `GcuServiceDir` / `GcuServiceExe` / `GcuInstalledUtc`）。
  - 已装版本 = `{app}\GCU\<ServiceDir>\MyControlCenter\GCUService.exe` 的
    `VersionInfo.FileVersion`（文件缺失即"需要修复"，直接走复制路径，不参与版本比较）。
  - 捆绑版本 = 暂存载荷里同一文件的 `VersionInfo.FileVersion`。
  - **同一变体**才比较：`bundled > installed` 才复制；相等或读不到版本 → 跳过复制 + 记日志
    （fail-safe，绝不降级）。`GCUBridge.exe` 不能用：三套载荷里它都是 1.0.1.10（无区分度）。
    `GCUService.exe` 实测：50 系（`release\GCU-only`）= 1.2.0.0；40 系两套 = 1.0.2.70。
  - **跨变体不可比较**：两份 40 系载荷的 `GCUService.exe` 文件版本同为 1.0.2.70，但载荷
    内容不同（51751 多 `BIOS_OTA.exe` / `DisplayInfo.dll`，服务目录名也不同），文件版本
    无法排序两套控制台 → 维持 beta17 结论：**不自动换变体**，保持已装变体，仅当用户显式传
    `/GCUVARIANT=` 时才切换；"保持已装变体"时 fast path 与验签都按已装变体解析路径。
  - **已知残留限制**：厂商不保证每次构建都递增 `FileVersion`（51749/51751 两套同版本即为
    实证）。因此"同变体、内容不同、版本相同"的变化会被判为"不必复制"——只跳不换，宁可漏
    一次复制也绝不降级；修复路径是重跑安装器或显式覆盖，实施时把判定依据写进日志。安装
    时把该文件版本一并记入标记（新增 `GcuPayloadVersion`）便于审计。
- **失败语义**：GCU 步骤失败（`Install-Gcu.ps1` 退出码 1）不回滚应用本体——应用文件
  已复制完成；安装器记录失败并在完成页提示"GCU 组件安装失败，可重跑安装器修复"。
  GCU 是硬件增强路径，不是应用可用的前提。

## 5. 升级与更新链

**决策：更新走安装器，不走补丁器。** beta17 的应用内更新器是"下载 zip → 换 exe"；
beta18 起更新包就是**完整的安装器 exe**，应用内更新器只负责：检测 → 下载安装器 →
校验 → 拉起安装器（静默或带界面，见第 6 节）→ 退出自身。

链路各环节（沿用现有 fail-closed 纪律，替换"zip 换 exe"为"跑安装器"）：

1. **检测**：`UpdateChecker.CheckAsync` 读业主静态 OSS 对象
   `lmechrevo-oss/api/update_check.php`（基址
   `https://lmechrevo.oss-cn-hangzhou.aliyuncs.com/lmechrevo-oss`），6 小时节流，
   版本比较用本机版本，服务端报"有更新"但版本不比本机新时按无更新处理（不降级）。
   发布侧由 `tools\publish-update-json.ps1`（OSS 直写）或
   `tools\publish-server-metadata.ps1`（站点管理 API）写入，两者都会内嵌
   `sha256`/`size`。
2. **准入**：`UpdatePolicy.TryAcceptDownloadUrl` 强制 HTTPS（回环例外）+ host 白名单
   （`github.com`、`objects.githubusercontent.com`、
   `lmechrevo.oss-cn-hangzhou.aliyuncs.com`）；`IsValidSha256` 强制 64 位十六进制。
   白名单与 fail-closed 语义**原样保留**，只是包体从 zip 变为安装器 exe。
3. **下载**：`UpdateInstaller.DownloadAsync` 边下边算 SHA-256，400 MB 上限
   （`MaxPackageBytes`），独立 attempt 目录 + 残留清理（beta17 修复的语义保持）。
4. **校验**：SHA-256 必须与元数据严格匹配（fail-closed）；size 声明一致时一并核对。
   zip 结构校验（`FindPackageExe` 等）对安装器包不再适用，替换为"只验 SHA-256 + size"。
   （【推迟决策 2026-09-16】：Authenticode 校验项推迟到签名落地——签名落地前（第 7 节）
   没有可验的签名；签名可用后回到本项，在 `UpdatePolicy` 里固定签名者指纹并 fail-closed。）
5. **安装**：拉起安装器。静默参数 `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`
   （沿用 `installer\README.md` 的既有约定）；更新场景默认静默，失败时退回带界面
   运行让用户看到原因。主程序在安装器成功启动后退出，安装器以
   `CloseApplications=yes` 处理残留句柄。
6. **回滚**：安装器退出码非 0 时，更新器报告失败并提示重跑/手动下载；由于是覆盖式
   安装（第 2 节），失败后旧安装大体完好。**不实现应用侧的文件级回滚**——Inno 也不提供
   安装器级事务（第 2 节【已核实 2026-09-16】），恢复路径就是"重跑安装器"加第 7 步的
   pending 标记横幅。
7. **启动失败横幅（判定机制，【已核实 2026-09-16】定稿）**：更新后若应用启动失败（例如
   运行时检测误判、文件被杀软隔离），下次成功启动时在主界面显示一条横幅："上次更新可能
   未完成，请重新运行安装器"，并附下载页链接。机制：
   - **标记位置与名称**：`%ProgramData%\L-Mechrevo\update-pending.json`（机器级，与 GCU
     日志同目录）。**不放 HKLM**：应用以普通用户运行，写 HKLM 需要第二次提权，违反
     "一次 UAC"约束。
   - **内容**：`{ fromVersion, toVersion, installerSha256, installerPath(可空), writtenUtc }`。
   - **写入时机**：更新器在 SHA-256 校验通过之后、`Process.Start(安装器)` 之前写入
     （即应用退出让安装器替换文件之前），用"临时文件 + 原子改名"落盘；写失败不阻止更新，
     只记日志（该次更新退化为"无标记"）。
   - **删除时机**：① 更新器 `Process.Start` 抛异常（安装器根本没起来）→ 立即删除并报错；
     ② 应用任一启动路径判定 `当前版本 ≥ toVersion` 时删除（含用户手动重跑同版本安装器的
     "更新成功"路径）。
   - **横幅条件（三条同时满足）**：标记存在且可解析；**当前版本 < `toVersion`**；当前没有
     `L-Mechrevo-*-setup.exe` 进程在运行（避免安装进行中误导）。版本比较复用
     `UpdateChecker` 既有的版本比较实现。
   - **标记保留**：条件不满足（版本仍旧）时刻意保留标记，避免用户忽略一次横幅后永久失去
     提示；标记损坏/不可解析 → 删除并记日志（无法据此行动，避免死循环）。
   - **全新安装不误报**：标记只由应用内更新器写入，安装器/全新安装不写它；卸载时由
     `[UninstallDelete]` 一并清理（实施项）。
   - **边界**：更新被用户在安装器里取消（Inno 退出码 2/5）→ 标记保留，下次启动旧版本仍
     < `toVersion` → 出现横幅（符合"引导重跑"预期）；应用被强杀 → 同上；安装器运行期间
     用户手动启动应用 → 进程存在 → 抑制横幅、标记保留。
   - **实施验证**（第 11 节步骤 6）：人为在复制阶段杀掉安装器 → 下次启动出现横幅；重跑
     安装器成功启动 → 横幅消失（标记被删）；全新安装后启动 → 无横幅。

## 6. 重启与体验

**决策：重启提示为"建议、可推迟"。** 安装器完成页与更新链的收尾都提示"建议重启以
完成配置"，用户可以推迟；安装器不强制重启（`RestartApplications=no` 已是现状，
保持）。GCU 服务安装后即时生效，不需要重启；需要重启的场景只有驱动/服务在极端情况下
未就绪，此时提示而非强制。

进度 UI 期望：

- 安装器：Inno 标准向导进度（文件复制 + GCU 步骤的 `StatusMsg` 文案
  `GcuStatus` 已有中英双语）；.NET 运行时下载/安装阶段显示独立的状态行
  （"正在下载 .NET 运行时…"），离线时该行变为下载链接。
- 应用内更新：`UpdateForm` 的现有结构（标题/说明/进度条/按钮）保持；进度条语义从
  "zip 下载"变为"安装器下载"，安装阶段显示"正在安装，程序即将退出"。
- 全程不出现第二个 UAC 弹窗（第 2 节）。

## 7. 签名与信任

**决策：beta18 暂不签名，接受 SmartScreen 警告。**

- 后果（需写进发布说明）：首次运行安装器与更新拉起的安装器时，SmartScreen 会提示
  "Windows 已保护你的电脑"，用户需点"更多信息 → 仍要运行"。这是无签名二进制的
  已知代价，不是缺陷。
- 应用本体（自包含/框架依赖 exe）同样未签名，本地运行不受 SmartScreen 影响
  （SmartScreen 针对 downloaded 文件 Mark-of-the-Web）。
- 厂商 GCU 二进制与微软驱动已有 Authenticode 签名，`Install-Gcu.ps1` 的
  `Assert-SignedFile` 继续强制校验它们——这条与"我们自己的安装器未签名"互不冲突。
- **将来签名后改变什么**：安装器与 app exe 用项目证书签名后，SmartScreen 警告随
  信誉积累消失；第 5 节第 4 步的校验可增加 Authenticode 验签（签名者指纹固定写入
  `UpdatePolicy`，fail-closed）；`Build-Installer.ps1` 的产物摘要里已有 SHA-256，
  签名后发布流程需同时记录签名证书指纹。

## 8. 体积

**决策：压缩采用 `lzma2/ultra64`。** `L-Mechrevo.iss` 的 `#ifndef Compression` 默认值
从 `lzma2/max` 改为 `lzma2/ultra64`（`SolidCompression=yes`、
`LZMAUseSeparateProcess=yes` 保持）。业主实测（同一发布树）：

| 压缩设置 | 安装包体积 |
|---|---|
| `lzma2/max`（现状默认） | 128,930,733 B |
| `lzma2/ultra64`（beta18 采纳） | 111,243,125 B |
| 节省 | 17,687,249 B（13.72%） |

代价是编译时间变长（ultra64 字典 64 MB、单线程压缩更慢），可接受；`LZMAUseSeparateProcess`
保证大字典不拖垮 ISCC 主进程。

**决策：不删 `GCU-40-51749` 载荷。** 该方案可省 10.66%，但被**否决**：

- beta17 已定论（`docs\beta17-gcu-variant-selection.md`）：三套控制台覆盖同一批
  24 个 ProjectID 机型家族，没有可靠的机型→控制台映射（`SupportProjectIDs` 被混淆
  销毁、`BIOS_PROJECT_ID ↔ ProjectID` 映射未建立），删掉 51749 会让依赖 Baseline
  形态或独有机型码 `PH4PG5F` 的机器失去可达载荷。
- 体积收益有限的物理原因：三套载荷之间有约 22.64 MB **字节完全相同**的文件
  （`UWACPIDriver` 等共享件），但 LZMA 字典窗口无法同时横跨三套载荷的全部文件，
  固体压缩下这些重复内容大部分无法被去重，删掉一套省不了与它的原始体积成比例的
  字节。13.72%（换压缩设置）对 10.66%（删载荷 + 破坏兼容）的对比说明压缩设置
  是更优解。

## 9. 风险与失败模式

| # | 失败模式 | 缓解 |
|---|---|---|
| 1 | **更新包下载中途失败**（网络中断、代理断流）：残包留在临时目录 | 沿用 beta17 修复：独立 attempt 目录 + `CreateNew` + 显式句柄作用域 + 残留清理；SHA-256 边下边算，失败即删包重试；重试由用户手动发起，不自动循环 |
| 2 | **.NET 运行时静默安装失败**（下载源不可达、企业策略拦截 MSI/EXE 安装） | 安装器不中止：记录失败、完成页提示、展示官方手动下载链接；应用本体文件照常就位，用户装好运行时后即可启动 |
| 3 | **GCU 服务停止失败**（服务挂死、`Stop-Service` 超时、驱动占用文件） | `Stop-GcuProcesses` 已含 `Stop-Process -Force` 兜底；仍失败时 `Install-Gcu.ps1` 退出 1，安装器按第 4 节语义继续完成应用安装并提示重跑；不回滚应用本体 |
| 4 | **覆盖安装中途失败留下混合状态**（新 exe + 旧 GCU 脚本，或反之） | Inno 无原子性、不还原被覆盖文件（第 2 节【已核实 2026-09-16】）；复制阶段只有应用文件、失败即中止且 `[Files]` 之后的 GCU 步骤不执行；GCU 步骤幂等，重跑安装器即修复；应用侧启动失败横幅（第 5 节第 7 步）引导用户重跑 |
| 5 | **SHA-256 不匹配**（OSS 对象被篡改、发布时哈希写错、CDN 缓存污染） | fail-closed：拒绝下载/安装、删临时包、给用户可读原因；绝不降级为结构校验。发布侧 `publish-update-json.ps1`/`publish-server-metadata.ps1` 均本地计算并回读校验哈希 |
| 6 | **`UsePreviousAppDir` 找不到先前目录**（用户手动删过目录但卸载项还在、或绿色拷贝到别处） | Inno 回退到 `DefaultDirName={autopf}\L-Mechrevo` 并在目录页展示；静默更新场景下若 `{app}` 缺失，安装器按默认目录全新安装，不会失败 |
| 7 | **杀软隔离新 exe / 误报**（未签名二进制的常见后果） | 发布说明写明加白方法；启动失败横幅引导重跑安装器；签名落地后此风险大幅下降（第 7 节） |
| 8 | **host 白名单过紧卡住合法镜像**（业主换下载源） | 白名单是代码常量（`UpdatePolicy.AllowedDownloadHosts`），加源需改代码发版——这是有意的 fail-closed 代价；发布侧换源时同步发一个白名单更新版本 |

## 10. 开放问题

> 状态规则：每条只能是 **RESOLVED**（结论 + 来源）或 **OPEN**（写明阻塞原因）。
> 本轮（2026-09-16）关闭第 1–4 条，新增第 5 条为真机验证项。

1. **RESOLVED（.NET 检测判据，2026-09-16）**：主判据 =
   `%ProgramFiles%\dotnet\dotnet.exe --list-runtimes` 解析 `Microsoft.WindowsDesktop.App 10.*`
   （官方文档方法：learn.microsoft.com "Check installed .NET versions"）；`sharedhost`
   判据作废；注册表 `InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App` 仅作
   `dotnet.exe` 缺失时的次级兜底（未文档化的实现细节）。详见第 3 节。
2. **RESOLVED（静默参数与退出码，2026-09-16）**：.NET 安装器 =
   `/install /quiet /norestart`，退出码 0=成功、3010=需要重启（learn.microsoft.com
   "Install .NET on Windows"）；Inno 自身退出码 0=完成、2/5=用户取消或 Abort、4=安装阶段
   致命错误、其它非零=未完成（https://jrsoftware.org/ishelp/topic_setupexitcodes.htm）。
   决定：安装器把 .NET 的 3010 映射为"成功 + 建议重启"（第 6 节）；更新器对 Inno 非零
   一律按"安装未确认完成"处理并显示横幅，不猜测退出码语义。
3. **RESOLVED（GCU 跨变体，2026-09-16）**：不自动切换、不提示。新增证据：两套 40 系载荷的
   `GCUService.exe` 文件版本完全相同（1.0.2.70），文件版本无法判定谁更新；维持 beta17 的
   "宁可保守"，仅在发布说明重申 `/GCUVARIANT=` 用法，待机型映射数据出现再改。
4. **RESOLVED（启动失败标记位置，2026-09-16）**：改为机器级**文件**
   `%ProgramData%\L-Mechrevo\update-pending.json`（原"HKLM 注册表值"建议否决——应用以
   普通用户运行，写 HKLM 需要第二次提权；该文件与 GCU 日志同目录、卸载时可一并清理）。
   机制、条件与边界见第 5 节第 7 步。
5. **OPEN（真机验证项，阻塞 = 需要真机）**：① 中断升级后"已覆盖文件不回滚"的实际残留
   清单（官方文档只给语义，不给本发行版的具体文件分布）；② 非提权进程在
   `%ProgramData%\L-Mechrevo` 下创建/删除 `update-pending.json` 的权限实测；③ .NET 检测
   三态与 10.0.12 直链/校验清单的可达性。三者都是第 11 节步骤 3/6 的验收证据，不影响本轮
   已定稿的设计。

## 11. 实施顺序

最小安全优先，每步独立可验证；每步列出必须产出的证据。

1. **压缩设置切换**：`L-Mechrevo.iss` 的 `Compression` 默认值改为 `lzma2/ultra64`。
   证据：`Build-Installer.ps1` 产物 `build-summary.json` 的 `InstallerBytes` 复现
   111,243,125 B（±少量字节，随文档微调），编译日志无错误。
2. **发布形态切换为框架依赖**：csproj 发布改为 framework-dependent（含
   `L-Mechrevo.dll` 随附文件），`Build-Installer.ps1` 的 `[Files]` 相应调整。
   证据：`singleFile=False` 的构建摘要；干净虚拟机上安装后应用可启动。
3. **.NET 10 检测 + 静默安装 + 离线回退**（依赖步骤 2；若仍自包含发布则整体跳过）：
   安装器 `[Code]` 按第 3 节判据检测（`%ProgramFiles%\dotnet\dotnet.exe --list-runtimes`
   为主、注册表 `sharedfx` 子键为兜底）、下载版本固定直链（10.0.12 起，比对官方 SHA-512
   清单）、以 `/install /quiet /norestart` 静默安装（3010 = 成功 + 建议重启）、离线时显示
   链接。证据：三态真机验证——已装（跳过）、缺失在线（静默装好且应用可启动）、缺失离线
   （显示链接且安装不中止）；检测判据的原始输出与 SHA-512 校验结果进安装日志。
4. **GCU "缺失或更旧 + 不降级" 判定**：按第 4 节取值源实现——标记 `GcuVariant` 判同变体、
   两侧 `GCUService.exe` 文件版本判新旧（`GCUBridge.exe` 无区分度）；安装时把该版本号一并
   写入标记（新增 `GcuPayloadVersion`）。证据：`Install-Gcu.ps1` 日志四态——全新安装、
   同变体同版本跳过、同变体已装更新版跳过（不降级）、跨变体不自动切换；`/GCUVARIANT=`
   覆盖仍生效并使复制发生。
5. **更新链切换为安装器包**：发布侧 `publish-update-json.ps1` /
   `publish-server-metadata.ps1` 的 `-PackagePath`/`-File` 指向安装器 exe（哈希、
   size 逻辑不变）；应用侧校验从 zip 结构校验改为安装器 exe 校验，拉起安装器替代
   `--apply-update` 换 exe。证据：端到端演练——旧版客户端检测到新版 → 下载 →
   SHA-256 通过 → 静默安装 → 新版启动；SHA-256 篡改用例被拒。
6. **启动失败横幅**：按第 5 节第 7 步实现 `%ProgramData%\L-Mechrevo\update-pending.json`
   标记 + 主界面横幅（卸载时清理该文件）。证据：人为在复制阶段杀掉安装器 → 下次启动出现
   横幅；重跑安装器成功并启动 → 横幅消失（标记已删）；全新安装后启动 → 无横幅。
7. **测试警告清理**：测试项目 10 个既有警告（CS8625/CS8601/CS8602/xUnit1026，
   业主报告）清零。证据：测试项目编译 0 警告，全量测试通过数不低于 beta17 基线
   （1727 通过 / 0 失败 / 1 跳过）。
