namespace MechrevoLite.Gpu;

/// <summary>事实格的可信度标记。**UNKNOWN 不等于 INFERRED，更不等于 PROVEN**——未证实的能力不得被门控放行。</summary>
public enum EvidenceMark
{
    /// <summary>没有可引用的证据。</summary>
    Unknown,

    /// <summary>有证据链但未逐方法反编译确认（跨代推断 / 字符串证据 / .NET Native 元数据与 PDB 符号级证据）。</summary>
    Inferred,

    /// <summary>逐方法反编译或实测确认存在。</summary>
    Proven,

    /// <summary>逐方法反编译或字符串全量检索确认**不存在**（或确认只是不落硬件的死路径）。</summary>
    ProvenAbsent,
}

/// <summary>一个事实格：标记 + 说明 + 出处。出处不得为空（验收要求逐格带出处）。</summary>
public sealed record RouteCell(EvidenceMark Mark, string Detail, string Source);

/// <summary>
/// 厂商服务侧 <c>OemDisplayMode</c>（NVRAM/固件变量）的编码。**仅记录厂商行为**：
/// 控制台不写这个变量，只发 MQTT 请求厂商服务写（见 Scope 的 option b）。
/// 两套编码恰好相反，故不得据平台猜一个值写下去。
/// </summary>
public sealed record OemDisplayModeEncoding(string Platform, int Direct, int Hybrid, int Igpu, string Source);

/// <summary>
/// 一个代际的显示路由事实。证据**拆成两列**，不允许互相顶替：
/// <list type="bullet">
/// <item><see cref="ConsoleProtocol"/>——**控制台侧协议**：控制台在哪个 MQTT 主题上发哪些动作
/// （由逐方法反编译，或对 .NET Native 程序集的元数据/PDB 符号级证实）。</item>
/// <item><see cref="ServiceWritePath"/>——**服务侧 / 硬件行为**：厂商服务拿到动作后到底怎么写
/// 硬件（NVRAM/EC/DLL）。控制台只发 MQTT，故这一列独立于控制台侧。</item>
/// </list>
/// <see cref="ConsoleActions"/> 是**我方可以对这一代发的动作词汇**：厂商词汇里被证实是死路径的动作
/// （例如 40 系三模档的 <c>IGPU_ONLY_CONNECT_RB_*</c>）不在其中。<c>DGPU_DIRECT_CONNECT_RESTART</c>
/// 不属于任何一行：能不能用服务的重启由服务档位决定（见 <see cref="DisplayRoutePolicy"/>）。
/// </summary>
public sealed record GenerationRouteFacts(
    DgpuGenerationKind Generation,
    RouteCell ConsoleProtocol,
    RouteCell ServiceWritePath,
    RouteCell IgpuOnly,
    RouteCell Restart,
    RouteCell HotSwap,
    IReadOnlyList<string> ConsoleActions,
    IReadOnlyList<OemDisplayModeEncoding> OemDisplayMode)
{
    /// <summary>
    /// N11: within the 40-series there are two capability tiers. <c>true</c> = the machine has
    /// 双显三模 (hybrid / dGPU-direct / iGPU); <c>false</c> = it does not. <c>null</c> for
    /// generations that have no such split (10/20, 30, 50).
    /// </summary>
    public bool? ThreeMode { get; init; }

    /// <summary>控制台动作词汇（N11 测试与消费方使用的别名）。</summary>
    public IReadOnlyList<string> Actions => ConsoleActions;
}

/// <summary>
/// 逐代（**轴 2**）显示路由事实表。代际由运行时探测（<see cref="DgpuGenerationProbe"/>），
/// **不由平台代号推导**。
///
/// <para>本表只记录"厂商控制台会发什么 / 厂商服务怎么写"，本产品**只发 MQTT**：
/// 控制台不写 <c>OemDisplayMode</c>、不写任何固件变量、没有写固件变量的接缝。</para>
///
/// <para><b>关键分界（两列，逐代交代依据）</b>——证据只能降级、不得升级
/// （<c>UNKNOWN 可降为 INFERRED，INFERRED 不得升为 PROVEN</c>）；<c>PROVEN</c> 仅授予**代码级**证据：</para>
/// <list type="bullet">
/// <item><b>10/16/20 系</b>：GamingCenterU 1.1.0.49 + GCUService 1.0.2.47。没有 MUX，官方唯一的显卡选项是
/// NVIDIA 控制面板的全局首选图形处理器（<c>NV_CTRL_PANEL_AUTOSELECT/_HIGHPERFORMANCE</c>）。控制台方法体被
/// ConfuserEx 破坏，动作名来自声明与资源（INFERRED）；服务经 <c>NVControlSetting.dll!SetNVCtrlPanel</c>
/// 写 NVIDIA DRS（字节扫描，INFERRED）。</item>
/// <item><b>30 系</b>：控制台侧 <c>INFERRED</c>——程序集是 .NET Native（无 IL），依据为
/// **元数据标识符堆 + 完整 PDB 符号表 + 全载荷 0 命中**的符号级证据，无 C# 可读，故不得升为 PROVEN
/// （见 <c>.omo\evidence\g30-console-decompile.md</c>）。服务侧 <c>UNKNOWN</c>
/// （30 系服务 MySettingManager 未反编译）。</item>
/// <item><b>40 系（两档，N11 订正）</b>：服务侧 <c>PROVEN</c>（<c>MySettingManager.cs</c>/<c>WMIEC.cs</c> 有真实方法体）。
/// 三模档（5.17.51.27 / 51.34）：直连 / 混合 / 核显全部是 NVRAM 目标 + 重启；<c>IGPU_ONLY_CONNECT_RB_*</c>
/// 在该服务里只写 <c>MySetting\IGPUonly</c> 注册表、不落硬件，是死路径，**不在我方词汇内**。
/// 两模档（5.17.49.19）：服务只声明 <c>TOGGLE_ON/OFF</c>，没有核显目标。</item>
/// <item><b>50 系</b>：控制台侧 <c>PROVEN</c>——厂商控制台是真实反编译的 C# 代码
/// （<c>CCUWinUI.decompiled.cs</c>）；服务侧 <c>UNKNOWN</c>（50 系服务 IL 混淆）。</item>
/// </list>
/// <para><c>ProvenAbsent</c> 表示**符号/标识符表级**确证不存在，或方法体确证只是死路径；它不是代码级 PROVEN，
/// 也不得据此把对应能力放行。</para>
/// </summary>
public static class DisplayRouteMatrix
{
    // 控制台动作词汇（逐字节精确，与厂商 MQTT Action 一致）。
    public const string ToggleOn = "DGPU_DIRECT_CONNECT_TOGGLE_ON";
    public const string ToggleOff = "DGPU_DIRECT_CONNECT_TOGGLE_OFF";
    public const string ToggleIgpu = "DGPU_DIRECT_CONNECT_TOGGLE_IGPU";
    public const string Restart = "DGPU_DIRECT_CONNECT_RESTART";
    public const string IgpuOnlyOn = "IGPU_ONLY_CONNECT_RB_ON";
    public const string IgpuOnlyOff = "IGPU_ONLY_CONNECT_RB_OFF";
    public const string IgpuOnlyAuto = "IGPU_ONLY_CONNECT_RB_AUTO";
    public const string HotSwapOn = "GPU_HOTSWAP_ON";
    public const string HotSwapOff = "GPU_HOTSWAP_OFF";
    public const string NvCtrlPanelAutoSelect = "NV_CTRL_PANEL_AUTOSELECT";
    public const string NvCtrlPanelHighPerformance = "NV_CTRL_PANEL_HIGHPERFORMANCE";

    /// <summary>全部代际行（10/20、30、40 三模、40 两模、50）。</summary>
    public static IReadOnlyList<GenerationRouteFacts> Rows { get; } = new[]
    {
        new GenerationRouteFacts(
            DgpuGenerationKind.Gen1020,
            new RouteCell(EvidenceMark.Inferred,
                "控制台侧（INFERRED——GamingCenterU 1.1.0.49 方法体被 ConfuserEx 破坏，动作名来自声明与资源）：" +
                "MQTT `Setting/Control`；设置页「独立显卡」开关只发 NV_CTRL_PANEL_HIGHPERFORMANCE（开）/ NV_CTRL_PANEL_AUTOSELECT（关）",
                "UC UWP_Refactor.Models/SettingAciton.cs:19-20 / US Define/ServCMD.cs:242,244 / US MyControlCenter/Topic.cs:43"),
            new RouteCell(EvidenceMark.Inferred,
                "服务侧 NVControlSetting.dll!SetNVCtrlPanel(0=AUTOSELECT,1=HIGHPERFORMANCE)；DLL 内含 NVIDIA DRS " +
                "SHIM_MCCOMPAT/SHIM_RENDERING_MODE/SHIM_RENDERING_OPTIONS 设置 id（0x10F9DC80/81/84）→ 驱动全局首选图形处理器",
                "US MyControlCenter/NVControlSetting.cs:14-21 / docs/hardware/gpu-modes-implementation-plan.md §2 字节扫描"),
            new RouteCell(EvidenceMark.ProvenAbsent,
                "1.0.2.47 服务与 GamingCenterU 载荷里 IGPU_ONLY_* / DGPU_DIRECT_* 字面量 0 命中；没有 MUX，也没有核显-only",
                "docs/hardware/gpu-modes-implementation-plan.md §2（L1020 字节扫描）"),
            new RouteCell(EvidenceMark.ProvenAbsent,
                "没有 *_RESTART 动作；首选 GPU 是驱动配置，对之后启动的程序生效，不需要重启",
                "docs/hardware/gpu-modes-implementation-plan.md §2 / §5.1"),
            new RouteCell(EvidenceMark.ProvenAbsent,
                "没有 GPU_HOTSWAP_* / IGPU_ONLY_CONNECT_RB_*",
                "docs/hardware/gpu-modes-implementation-plan.md §2（L1020 字节扫描）"),
            new[] { NvCtrlPanelAutoSelect, NvCtrlPanelHighPerformance },
            Array.Empty<OemDisplayModeEncoding>()),

        new GenerationRouteFacts(
            DgpuGenerationKind.Gen30,
            new RouteCell(EvidenceMark.Inferred,
                "控制台侧（INFERRED——程序集为 .NET Native 无 IL，依据元数据标识符堆 + 完整 PDB 符号表 + 全载荷 0 命中）：" +
                "MQTT `Setting/Control`；动作词汇仅 DGPU_DIRECT_CONNECT_TOGGLE_ON/_OFF；" +
                "DgpuSwitchView.Toggle_PointerPressed(d__18) + SettingViewModel.DGpuDirectConnectionSwitch；" +
                "DGPU_DIRECT_CONNECT_TOGGLE_IGPU 不在枚举内。载荷字段名为家族推断",
                ".omo/evidence/g30-console-decompile.md（cc-41747-13/decompiled/30-series-console-protocol.md）"),
            new RouteCell(EvidenceMark.Unknown,
                "NvramVariable.SetFwVars(\"OemDisplayMode\") 字符串在，但 30 系服务 MySettingManager 未反编译，写路径形态未知",
                "gpu-mode-matrix.md A (30 行, UNKNOWN#5)"),
            new RouteCell(EvidenceMark.ProvenAbsent,
                "30 系 .NET Native 元数据枚举堆里 DGPU_DIRECT_CONNECT_* 只有 _ON/_OFF；IGPU_ONLY_* / IGPUonly/* 全载荷 0 命中，无 PDB 符号",
                "g30-console-decompile.md / cc-41747-13/scans/absence.txt"),
            new RouteCell(EvidenceMark.ProvenAbsent,
                "30 系载荷 *_RESTART 0 命中，无 PDB 符号（唯一 restart 命中是 BCL FileSystemWatcher.Restart）；官方由用户手动重启",
                "g30-console-decompile.md / cc-41747-13/scans/absence.txt"),
            new RouteCell(EvidenceMark.ProvenAbsent,
                "30 系载荷 HOTSWAP 0 命中（大小写不敏感），无 PDB 符号",
                "g30-console-decompile.md / cc-41747-13/scans/absence.txt"),
            new[] { ToggleOn, ToggleOff },
            Array.Empty<OemDisplayModeEncoding>()),

        // N11: 40-series WITH 双显三模 (hybrid / dGPU-direct / iGPU) - the 5.17.51.27 / 51.34 consoles.
        new GenerationRouteFacts(
            DgpuGenerationKind.Gen40,
            new RouteCell(EvidenceMark.Proven,
                "服务侧词汇（PROVEN——S40 逐方法反编译；40 控制台本身未反编译）：MQTT `Setting/Control`；" +
                "DGPU_DIRECT_CONNECT_TOGGLE_ON/OFF/IGPU、DGPU_DIRECT_CONNECT_RESTART、GETSTATUS",
                "S40 MyControlCenter/Topic.cs:51,123-125 / MyControlCenter/MqttClientCtrl.cs:59-65,92-113 / MySettingManager.cs:663-785"),
            new RouteCell(EvidenceMark.Proven,
                "服务侧 SetFwVars(\"OemDisplayMode\")：直连/混合/核显三个 NVRAM 目标，重启后生效；" +
                "状态串在写 NVRAM 之前就改（Setting/Status 只是目标值）",
                "S40 MySettingManager.cs:1229-1271 / :1593-1628"),
            new RouteCell(EvidenceMark.ProvenAbsent,
                "IGPU_ONLY_CONNECT_RB_ON/OFF/AUTO 只进 UserSetIGgpuONLYConnectionSwitch 写 MySetting\\IGPUonly 注册表，" +
                "InitIGPUSetting 为空方法，WMI 写入只在没人订阅的定时器里——死路径，不发。核显目标用 TOGGLE_IGPU + 重启",
                "S40 MySettingManager.cs:582-649,1281-1322,83-116 / gpu-switching-per-generation.md §7"),
            new RouteCell(EvidenceMark.Proven,
                "DGPU_DIRECT_CONNECT_RESTART → shutdown /r /t 0（立即无条件重启）",
                "S40 MySettingManager.cs:492-495,1326-1335"),
            new RouteCell(EvidenceMark.ProvenAbsent,
                "40 系无热切换动作词汇（GPU_HOTSWAP_* 0 命中）",
                "gpu-mode-matrix.md A"),
            new[] { ToggleOn, ToggleOff, ToggleIgpu },
            new[]
            {
                new OemDisplayModeEncoding("AMD", 1, 0, 2, "MySettingManager.cs:1229-1271"),
                new OemDisplayModeEncoding("Intel", 2, 4, 1, "MySettingManager.cs:1229-1271"),
            })
        { ThreeMode = true },

        // N11: 40-series WITHOUT 双显三模 - the 5.17.49.19 console. Only TOGGLE_ON/OFF are declared.
        new GenerationRouteFacts(
            DgpuGenerationKind.Gen40,
            new RouteCell(EvidenceMark.Proven,
                "控制台侧（PROVEN——ControlCenter_5.17.49.19 控制台反编译）：MQTT `Setting/Control`；动作词汇 " +
                "DGPU_DIRECT_CONNECT_TOGGLE_ON/OFF、GETSTATUS；配套服务（S40B）也只声明 TOGGLE_ON/OFF——" +
                "**无 TOGGLE_IGPU、无 RESTART、无 IGPU_ONLY_***（不带双显三模档）",
                "ControlCenter_5.17.49.19 控制台反编译 / S40B Define/ServCMD.cs:353,355 / MyControlCenter/MySettingManager.cs:260-263"),
            new RouteCell(EvidenceMark.Proven,
                "服务侧 SetFwVars(\"OemDisplayMode\")（仅 NVRAM，无 WMI iGPU-only 路径）",
                "MySettingManager.cs:1229-1271 / GCUService.decompiled.cs:45504-45532"),
            new RouteCell(EvidenceMark.ProvenAbsent,
                "不带双显三模档：IGPU_ONLY_* 与 TOGGLE_IGPU 在该档服务声明中 0 命中；BIOS 是否接受核显目标未知，不提供",
                "S40B Define/ServCMD.cs / gpu-mode-matrix.md A (40B)"),
            new RouteCell(EvidenceMark.ProvenAbsent,
                "该档服务未声明 DGPU_DIRECT_CONNECT_RESTART；重启由用户或我方发起",
                "S40B Define/ServCMD.cs:353,355"),
            new RouteCell(EvidenceMark.ProvenAbsent,
                "40 系无热切换动作词汇（GPU_HOTSWAP_* 0 命中）",
                "gpu-mode-matrix.md A"),
            new[] { ToggleOn, ToggleOff },
            new[]
            {
                new OemDisplayModeEncoding("AMD", 1, 0, 2, "MySettingManager.cs:1229-1271"),
                new OemDisplayModeEncoding("Intel", 2, 4, 1, "MySettingManager.cs:1229-1271"),
            })
        { ThreeMode = false },

        new GenerationRouteFacts(
            DgpuGenerationKind.Gen50,
            new RouteCell(EvidenceMark.Proven,
                "控制台侧（PROVEN——真实反编译的 C# 代码）：MQTT `Setting/Control`；动作词汇 DGPU_DIRECT_CONNECT_TOGGLE_ON/OFF/IGPU、" +
                "IGPU_ONLY_CONNECT_RB_ON/OFF/AUTO（仅热切换卡片）、DGPU_DIRECT_CONNECT_RESTART、GPU_HOTSWAP_ON/OFF",
                "CCUWinUI.decompiled.cs:86477-86515,53527-53729,139137-139174"),
            new RouteCell(EvidenceMark.Unknown,
                "50 系服务 IL 混淆，实际写路径（NVRAM/EC/DLL）不可静态确定；2026-09-10 本机实测 RB_ON 只翻转服务寄存器、独显未断开",
                "gpu-mode-matrix.md A (50 行) / UNKNOWN#1 / APP Hardware/MechrevoService.cs（热切换实测注记）"),
            new RouteCell(EvidenceMark.Proven,
                "IGPU_ONLY_CONNECT_RB_ON/OFF（热切换卡片，IsIGPUHotSwap 为真时才显示），每 2 s 重发、count>60 放弃、每第 4 次额外重发",
                "CCUWinUI.decompiled.cs:53527-53580 / GpuSettingPageViewModel.cs:1091-1135"),
            new RouteCell(EvidenceMark.Proven,
                "DGPU_DIRECT_CONNECT_RESTART 在 Task.Delay(800) 后发出",
                "CCUWinUI.decompiled.cs:86511-86515"),
            new RouteCell(EvidenceMark.Inferred,
                "GPU_HOTSWAP_ON/OFF 枚举存在，但处理器是 log-only no-op，wire 用法未证（我方从不发送）",
                "CCUWinUI.decompiled.cs:53420-53435,139141-139142"),
            new[] { ToggleOn, ToggleOff, ToggleIgpu, IgpuOnlyOn, IgpuOnlyOff, IgpuOnlyAuto, HotSwapOn, HotSwapOff },
            Array.Empty<OemDisplayModeEncoding>()),
    };

    /// <summary>找一行；找不到返回 <c>null</c>（<c>Unknown</c>/<c>NoDgpu</c> 永远没有行）。</summary>
    public static GenerationRouteFacts? Find(DgpuGenerationKind generation) =>
        Rows.FirstOrDefault(row => row.Generation == generation);

    /// <summary>
    /// N11: find the row for a generation + capability tier. For generations without a tier split
    /// (10/20, 30, 50) the <paramref name="threeMode"/> argument is ignored.
    /// </summary>
    public static GenerationRouteFacts? FindTier(DgpuGenerationKind generation, bool threeMode) =>
        Rows.FirstOrDefault(row => row.Generation == generation && (row.ThreeMode is null || row.ThreeMode == threeMode));

    /// <summary>
    /// N11: the 40-series tier is decided by the service-written <c>ItemSupport</c> capability bit
    /// (<c>iGPUModeOnlySupport</c>), never by a hard-coded per-model table.
    /// </summary>
    public static bool TierFromCapability(int igpuModeOnlySupport) => igpuModeOnlySupport == 1;

    /// <summary>
    /// Cold-start overlay: MQTT bit wins when present; last-known candidate only while MQTT is null.
    /// </summary>
    public static bool TierFromCapability(int? mqttIgpuModeOnlySupport, bool persistedThreeModeCandidate) =>
        mqttIgpuModeOnlySupport is int bit ? bit == 1 : persistedThreeModeCandidate;

    /// <summary>该代际的"服务侧写路由"落地方式可信度。只有 40 系是 PROVEN。</summary>
    public static EvidenceMark ServiceWritePathMark(DgpuGenerationKind generation) =>
        Find(generation)?.ServiceWritePath.Mark ?? EvidenceMark.Unknown;

    /// <summary>路由落地是否已证实（仅 40 系）。</summary>
    public static bool IsDisplayRouteWritePathProven(DgpuGenerationKind generation) =>
        ServiceWritePathMark(generation) == EvidenceMark.Proven;

    /// <summary>该代际的我方词汇里是否有 iGPU-only 热切换动作（只有 50 系的热切换卡片）。</summary>
    public static bool AllowsIgpuOnly(DgpuGenerationKind generation)
    {
        IReadOnlyList<string> actions = ConsoleActions(generation);
        return actions.Contains(IgpuOnlyOn, StringComparer.Ordinal) ||
               actions.Contains(IgpuOnlyOff, StringComparer.Ordinal) ||
               actions.Contains(IgpuOnlyAuto, StringComparer.Ordinal);
    }

    /// <summary>
    /// 该代际的厂商控制台/服务是否有 RESTART 动作（事实，不是权限）。10/20、30 系确证不存在。
    /// 我方能不能发服务的 RESTART 看 <see cref="DisplayRoutePolicy.AllowsServiceRestart"/>。
    /// </summary>
    public static bool AllowsRestart(DgpuGenerationKind generation) =>
        Rows.Any(row => row.Generation == generation && row.Restart.Mark == EvidenceMark.Proven);

    /// <summary>该代际的控制台是否有热切换动作。30 系确证不存在；仅 50 系出现该动作词汇（处理器 no-op，见事实表）。</summary>
    public static bool AllowsHotSwap(DgpuGenerationKind generation)
    {
        IReadOnlyList<string> actions = ConsoleActions(generation);
        return actions.Contains(HotSwapOn, StringComparer.Ordinal) ||
               actions.Contains(HotSwapOff, StringComparer.Ordinal);
    }

    /// <summary>该代际的动作词汇（三模档优先的那一行）；<c>Unknown</c>/<c>NoDgpu</c> 返回空。</summary>
    public static IReadOnlyList<string> ConsoleActions(DgpuGenerationKind generation) =>
        Find(generation)?.ConsoleActions ?? Array.Empty<string>();

    /// <summary>厂商控制台词汇表。成员资格不是放行：没判出代际时事实表没有行，一个动作都不发。</summary>
    static readonly IReadOnlySet<string> KnownActions = new HashSet<string>(StringComparer.Ordinal)
    {
        ToggleOn, ToggleOff, ToggleIgpu, Restart, IgpuOnlyOn, IgpuOnlyOff, IgpuOnlyAuto, HotSwapOn, HotSwapOff,
        NvCtrlPanelAutoSelect, NvCtrlPanelHighPerformance,
    };

    /// <summary>该动作是否属于厂商控制台词汇表。不是权限：权限见 <see cref="DisplayRoutePolicy.AllowsAction(DgpuGenerationKind, string, bool, GcuServiceTier, bool)"/>。</summary>
    public static bool IsKnownAction(string action) =>
        !string.IsNullOrWhiteSpace(action) && KnownActions.Contains(action);

    /// <summary>核显-only 热切换（RB）动作。</summary>
    public static bool IsIgpuOnlyAction(string action) =>
        action is IgpuOnlyOn or IgpuOnlyOff or IgpuOnlyAuto;

    /// <summary>NVIDIA 控制面板首选 GPU 动作（10/20 系）。</summary>
    public static bool IsNvControlPanelAction(string action) =>
        action is NvCtrlPanelAutoSelect or NvCtrlPanelHighPerformance;
}

/// <summary>
/// 代际 × 服务档位的放行策略：把事实表翻译成"这个动作在这台机器上能不能发"。
/// **只收紧、不发明**：表里没有的动作一律不许发。
/// <c>Unknown</c>/<c>NoDgpu</c> 没有事实行，因此一个代际动作都不放行——
/// 读不到代际不等于把各代动作并集都放开。
/// </summary>
public static class DisplayRoutePolicy
{
    /// <summary>该动作是否在该代际的词汇内（不看档位；事实表测试用）。</summary>
    public static bool AllowsAction(DgpuGenerationKind generation, string action)
    {
        if (string.IsNullOrWhiteSpace(action)) return false;

        return DisplayRouteMatrix.ConsoleActions(generation).Contains(action, StringComparer.Ordinal);
    }

    /// <summary>
    /// 该动作是否在该代际 + 40 系分档的词汇内（不看服务档位）。
    /// <paramref name="threeMode"/> is <c>IgpuOnlyStatusSupport == true</c>; do not default it true.
    /// </summary>
    public static bool AllowsAction(DgpuGenerationKind generation, string action, bool threeMode)
    {
        if (string.IsNullOrWhiteSpace(action)) return false;

        IReadOnlyList<string> actions =
            DisplayRouteMatrix.FindTier(generation, threeMode)?.ConsoleActions ?? Array.Empty<string>();
        return actions.Contains(action, StringComparer.Ordinal);
    }

    /// <summary>
    /// **生产门控**：代际词汇 × 服务档位。
    /// <list type="bullet">
    /// <item><c>Legacy1020</c>：只放 10/20 行的 <c>NV_CTRL_PANEL_*</c>；</item>
    /// <item><c>Modern12</c>：行词汇；<c>RESTART</c> 在行含 <c>TOGGLE_ON</c> 时放行；<c>IGPU_ONLY_CONNECT_RB_*</c>
    ///   只在 50 系热切换机型放行；<c>GPU_HOTSWAP_*</c> 与 <c>NV_CTRL_PANEL_*</c> 不放；</item>
    /// <item><c>Foreign</c>：行词汇 ∩ {<c>TOGGLE_ON</c>, <c>TOGGLE_OFF</c>}，重启由我方发起；</item>
    /// <item><c>Unknown</c>：一个都不放。</item>
    /// </list>
    /// </summary>
    public static bool AllowsAction(
        DgpuGenerationKind generation, string action, bool threeMode, GcuServiceTier tier, bool hotSwap)
    {
        if (string.IsNullOrWhiteSpace(action)) return false;
        GenerationRouteFacts? row = DisplayRouteMatrix.FindTier(generation, threeMode);
        if (row is null) return false;
        bool inRow = row.ConsoleActions.Contains(action, StringComparer.Ordinal);

        switch (tier)
        {
            case GcuServiceTier.Legacy1020:
                return generation == DgpuGenerationKind.Gen1020 && inRow &&
                       DisplayRouteMatrix.IsNvControlPanelAction(action);

            case GcuServiceTier.Modern12:
                // 1.2.0.0 里 NV_CTRL_PANEL_* 字面量在，但找不到 SetNVCtrlPanel 导入：行为未证实。
                if (generation == DgpuGenerationKind.Gen1020) return false;
                if (action == DisplayRouteMatrix.Restart)
                    return row.ConsoleActions.Contains(DisplayRouteMatrix.ToggleOn, StringComparer.Ordinal);
                if (DisplayRouteMatrix.IsIgpuOnlyAction(action))
                    return generation == DgpuGenerationKind.Gen50 && hotSwap && inRow;
                if (action is DisplayRouteMatrix.HotSwapOn or DisplayRouteMatrix.HotSwapOff) return false;
                if (DisplayRouteMatrix.IsNvControlPanelAction(action)) return false;
                return inRow;

            case GcuServiceTier.Foreign:
                return inRow && action is DisplayRouteMatrix.ToggleOn or DisplayRouteMatrix.ToggleOff;

            default:
                return false;
        }
    }

    /// <summary>重启能否交给服务（<c>DGPU_DIRECT_CONNECT_RESTART</c>）；否则由我方发起 Windows 重启。</summary>
    public static bool AllowsServiceRestart(DgpuGenerationKind generation, bool threeMode, GcuServiceTier tier) =>
        AllowsAction(generation, DisplayRouteMatrix.Restart, threeMode, tier, hotSwap: false);
}
