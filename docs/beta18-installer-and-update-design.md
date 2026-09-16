# beta18 — 安装器与更新链设计

设计日期：2026-09-16。本文是**决策文档**：所有条目均为已定决策，不是备选项。依据来自
`installer\L-Mechrevo.iss`、`installer\Build-Installer.ps1`、`installer\Select-GcuPayload.ps1`、
`installer\Install-Gcu.ps1`、`installer\README.md`、`tools\publish-update-json.ps1`、
`tools\publish-server-metadata.ps1`、`src\MechrevoLiteWin\Updates\`（UpdateChecker /
UpdatePolicy / UpdateInstaller / UpdateForm）、`docs\beta17-gcu-variant-selection.md` 与
`CHANGELOG.md`。凡未在仓库中核实的事实一律标注 `【待确认】`。

## 1. 目标与不变量

beta18 要把"安装器"和"应用内更新"收拢成一条链：安装器是唯一的安装与升级入口，应用内
更新只负责"发现新版本 → 下载安装器 → 校验 → 拉起安装器"。

以下不变量在任何失败路径下都必须成立：

1. **升级永远不能把机器留在"没有可用的应用"的状态。** 这是"不预卸载、原地覆盖"决策
   （第 2 节）的根本理由：先卸载再安装的流程一旦中途失败，用户手里就什么都没有了；覆盖式
   升级失败时，旧文件大部分仍在，最坏情况也只是"新 exe 已就位但后续步骤失败"，应用仍可
   启动或按第 5 节的横幅引导重跑安装器。
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
| `HKLM\SOFTWARE\L-Mechrevo` | GCU 安装标记（Variant / ServiceDir / ServiceExe / InstalledUtc） |

**失败升级的行为。** 覆盖式安装下，Inno 的文件复制阶段失败会中止安装并回滚其自身事务
（Inno 在复制前对被覆盖文件有内部保护，失败时恢复原文件）【待确认：Inno 6.7.x 对
overwrite 失败的回滚语义细节，需在真机用中断安装验证】。即使回滚不完整，由于应用是
自包含单文件 exe，最坏残留是"新 exe + 旧 GCU 步骤未跑"，应用仍能启动；GCU 步骤本身
幂等（`Install-Gcu.ps1` 的 fast path / repair 语义），重跑安装器即可修复。卸载流程
（`Uninstall-Gcu.ps1` 先停服务删驱动再删文件）不受本决策影响。

## 3. 运行时依赖

**决策：.NET 10 Desktop Runtime 不打进安装包。** 安装器在安装时检测本机是否已有
.NET 10 Desktop Runtime；缺失则**下载并静默安装**；机器离线时只展示手动下载链接，
不阻塞安装流程的其他部分。

- **现状背景**：beta17 的发布形态是自包含单文件（`Build-Installer.ps1` 检测
  `singleFile`，`installer\README.md` 明确"no .NET download needed"）。beta18 采纳
  本决策意味着发布形态转向框架依赖（exe + 少量随附文件），安装器体积因此显著下降，
  与第 8 节的压缩决策叠加。发布形态切换是 beta18 的实施项（第 11 节步骤 2）。
- **"缺失"如何判定**：检测逻辑在安装器内完成（Inno 的 `[Code]` 段或安装前 PowerShell
  探测）。候选判据是运行时检测 API（`netcorecheck`/`dotnet --list-runtimes` 或注册表
  `HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64` 的 `InstallSuccess` 值）。
  【待确认：最终采用的检测 API 与精确的注册表键/值，需在实施时对照 .NET 官方
  "How to detect that the .NET Runtime is already installed" 文档定稿；仓库中目前
  没有任何检测代码可核实。】
- **静默安装**：下载 .NET 10 Desktop Runtime x64 离线安装器后以静默参数运行
  （`/install /quiet /norestart`）【待确认：以 .NET 10 官方文档的静默参数为准】。
  下载源用官方 Microsoft CDN【待确认：具体 URL 与是否需要固定版本号】。
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

- **"缺失或更旧"判定**：安装前读取 `HKLM\SOFTWARE\L-Mechrevo` 安装标记与目标
  `GCUService.exe` 的文件版本，与本次捆绑载荷的版本比较；**载荷版本 ≥ 已装版本时跳过
  GCU 步骤**（只保留 fast path 的验签），**已装版本更新时绝不覆盖（不降级）**。
  【待确认：版本比较的取值源——`GCUService.exe` 的 `FileVersionInfo` 与安装标记里的
  `GcuVariant` 是否足以判定"同一变体的新旧"；跨变体（如已装 51749、新包默认 51751）
  按 beta17 结论视为"无法判定谁更新"，此时**不自动换变体**，保持已装变体，仅当用户
  显式传 `/GCUVARIANT=` 时才切换。】
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
   zip 结构校验（`FindPackageExe` 等）对安装器包不再适用，替换为
   【待确认：对安装器 exe 本身的校验项——SHA-256 之外是否再验 Authenticode；在签名
   落地前（第 7 节）没有可验的签名，故 beta18 仅 SHA-256 + size】。
5. **安装**：拉起安装器。静默参数 `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`
   （沿用 `installer\README.md` 的既有约定）；更新场景默认静默，失败时退回带界面
   运行让用户看到原因。主程序在安装器成功启动后退出，安装器以
   `CloseApplications=yes` 处理残留句柄。
6. **回滚**：安装器退出码非 0 时，更新器报告失败并提示重跑/手动下载；由于是覆盖式
   安装（第 2 节），失败后旧安装大体完好。**不实现应用侧的文件级回滚**——回滚责任
   在安装器自身事务与"重跑安装器"这条恢复路径上。
7. **启动失败横幅**：更新后若应用启动失败（例如运行时检测误判、文件被杀软隔离），
   下次成功启动时在主界面显示一条横幅："上次更新可能未完成，请重新运行安装器"，
   并附下载页链接。判定依据【待确认：以何种持久化标记识别"更新后未正常退出/启动"
   （如更新器写入的 pending 标记 + 正常启动时清除），实施时定稿】。

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
| 4 | **覆盖安装中途失败留下混合状态**（新 exe + 旧 GCU 脚本，或反之） | 应用是单文件（或 exe+少量文件），混合窗口极小；GCU 步骤幂等，重跑安装器即修复；应用侧启动失败横幅（第 5 节第 7 步）引导用户重跑 |
| 5 | **SHA-256 不匹配**（OSS 对象被篡改、发布时哈希写错、CDN 缓存污染） | fail-closed：拒绝下载/安装、删临时包、给用户可读原因；绝不降级为结构校验。发布侧 `publish-update-json.ps1`/`publish-server-metadata.ps1` 均本地计算并回读校验哈希 |
| 6 | **`UsePreviousAppDir` 找不到先前目录**（用户手动删过目录但卸载项还在、或绿色拷贝到别处） | Inno 回退到 `DefaultDirName={autopf}\L-Mechrevo` 并在目录页展示；静默更新场景下若 `{app}` 缺失，安装器按默认目录全新安装，不会失败 |
| 7 | **杀软隔离新 exe / 误报**（未签名二进制的常见后果） | 发布说明写明加白方法；启动失败横幅引导重跑安装器；签名落地后此风险大幅下降（第 7 节） |
| 8 | **host 白名单过紧卡住合法镜像**（业主换下载源） | 白名单是代码常量（`UpdatePolicy.AllowedDownloadHosts`），加源需改代码发版——这是有意的 fail-closed 代价；发布侧换源时同步发一个白名单更新版本 |

## 10. 开放问题

1. **.NET 检测的最终判据**：`netcorecheck.exe`（官方检测工具，需随安装器分发）vs
   注册表 `InstallSuccess` vs `dotnet --list-runtimes` 解析。**建议**：注册表
   `HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost` 路径下的
   `InstallSuccess` 优先（无外部依赖、静默可查），不可读时回退
   `dotnet --list-runtimes`。实施时以官方检测文档定稿并真机验证。
2. **更新链的安装器静默参数与重启交互**：`/NORESTART` 下若 .NET 运行时安装要求重启
   （罕见），安装器退出码如何上报给更新器。**建议**：更新器把非零退出码一律按
   "安装未确认完成"处理，显示横幅引导重跑，不猜测退出码语义。
3. **GCU 跨变体升级语义**：已装 51749、新包默认 51751 时是否提示用户可选切换。
   **建议**：beta18 不自动切换、不提示（保持 beta17 的"宁可保守"），仅在发布说明
   重申 `/GCUVARIANT=` 用法；等机型映射数据出现再改。
4. **更新后启动失败标记的持久化位置**：`HKLM\SOFTWARE\L-Mechrevo`（机器级，与 GCU
   标记同处）vs `%ProgramData%\L-Mechrevo`。**建议**：注册表，与现有安装标记同键下
   加 `UpdatePending` 值，正常退出时清除。

## 11. 实施顺序

最小安全优先，每步独立可验证；每步列出必须产出的证据。

1. **压缩设置切换**：`L-Mechrevo.iss` 的 `Compression` 默认值改为 `lzma2/ultra64`。
   证据：`Build-Installer.ps1` 产物 `build-summary.json` 的 `InstallerBytes` 复现
   111,243,125 B（±少量字节，随文档微调），编译日志无错误。
2. **发布形态切换为框架依赖**：csproj 发布改为 framework-dependent（含
   `L-Mechrevo.dll` 随附文件），`Build-Installer.ps1` 的 `[Files]` 相应调整。
   证据：`singleFile=False` 的构建摘要；干净虚拟机上安装后应用可启动。
3. **.NET 10 检测 + 静默安装 + 离线回退**：安装器 `[Code]` 检测、下载、静默安装、
   离线链接。证据：三态真机验证——已装（跳过）、缺失在线（静默装好且应用可启动）、
   缺失离线（显示链接且安装不中止）。
4. **GCU "缺失或更旧 + 不降级" 判定**：读安装标记与文件版本，≥ 时跳过，< 已装时
   跳过并记日志。证据：`Install-Gcu.ps1` 日志三态——全新安装、同版本跳过、
   已装更新版跳过（不降级）；`/GCUVARIANT=` 覆盖仍生效。
5. **更新链切换为安装器包**：发布侧 `publish-update-json.ps1` /
   `publish-server-metadata.ps1` 的 `-PackagePath`/`-File` 指向安装器 exe（哈希、
   size 逻辑不变）；应用侧校验从 zip 结构校验改为安装器 exe 校验，拉起安装器替代
   `--apply-update` 换 exe。证据：端到端演练——旧版客户端检测到新版 → 下载 →
   SHA-256 通过 → 静默安装 → 新版启动；SHA-256 篡改用例被拒。
6. **启动失败横幅**：`UpdatePending` 标记 + 主界面横幅。证据：人为制造"安装器
   中途退出"用例，下次启动出现横幅，重跑安装器后横幅消失。
7. **测试警告清理**：测试项目 10 个既有警告（CS8625/CS8601/CS8602/xUnit1026，
   业主报告）清零。证据：测试项目编译 0 警告，全量测试通过数不低于 beta17 基线
   （1727 通过 / 0 失败 / 1 跳过）。
