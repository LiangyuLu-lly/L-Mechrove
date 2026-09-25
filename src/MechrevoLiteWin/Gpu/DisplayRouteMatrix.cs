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

    /// <summary>逐方法反编译或字符串全量检索确认**不存在**。</summary>
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
    /// generations that have no such split (30/50).
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
/// <item><b>30 系</b>：控制台侧 <c>INFERRED</c>——程序集是 .NET Native（无 IL），依据为
/// **元数据标识符堆 + 完整 PDB 符号表 + 全载荷 0 命中**的符号级证据，无 C# 可读，故不得升为 PROVEN
/// （见 <c>.omo\evidence\g30-console-decompile.md</c>）。服务侧 <c>UNKNOWN</c>
/// （30 系服务 MySettingManager 未反编译）。</item>
/// <item><b>40 系（两档，N11 订正）</b>：控制台侧与服务侧均 <c>PROVEN</c>——两侧都有逐方法反编译的 C#
/// （控制台 <c>Topic.cs</c>/<c>MqttClientCtrl.cs</c>；服务 <c>MySettingManager.cs</c>/<c>WMIEC.cs</c>）。
/// <para><b>订正（amended by owner）</b>：此前把 <c>ControlCenter_5.17.49.19</c> 无 <c>IGPU_ONLY_*</c>、
/// <c>5.17.51.27</c> 有 —— 判为"后续版本新增了核显模式"是**错的**。二者是**两种机型**：40 系分两档，
/// 一档**带双显三模**（混合 / 独显直连 / 核显，对应 5.17.51.27，有 <c>IGPU_ONLY_*</c>），
/// 另一档**不带**（对应 5.17.49.19，无 <c>IGPU_ONLY_*</c>）。这正是业主给两个 40 系控制台的原因。
/// 载荷侧 <c>GCU-40-51749</c>(UniwillService) 与 <c>GCU-40-51751</c>(AiStoneService) 与两档对应。</para>
/// <para>因此"代际"单轴不够：档位判据必须是运行时 MQTT 上报的
/// <c>IGpuOnlyConnectionSwitch_Support</c>（叠加官方 UI 是否提供核显档），而不是
/// <c>ItemSupport.iGPUModeOnlySupport</c> 或按机型硬编码分档。</para></item>
/// <item><b>50 系</b>：控制台侧 <c>PROVEN</c>——厂商控制台是真实反编译的 C# 代码
/// （<c>CCUWinUI.decompiled.cs</c>，15 万行量级）；服务侧 <c>UNKNOWN</c>（50 系服务 IL 混淆）。</item>
/// </list>
/// <para><c>ProvenAbsent</c> 仅表示**符号/标识符表级**确证不存在（30 系 <c>_IGPU</c>/<c>_RESTART</c>/
/// <c>IGPU_ONLY_*</c>/<c>GPU_HOTSWAP_*</c> 在元数据堆、PDB 符号表与全载荷扫描中 0 命中）；它是"确证不存在"，
/// 不是代码级 PROVEN，也不得据此把对应能力放行。</para>
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

    /// <summary>全部代际行（30/40/50）。</summary>
    public static IReadOnlyList<GenerationRouteFacts> Rows { get; } = new[]
    {
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
                "30 系载荷 *_RESTART 0 命中，无 PDB 符号（唯一 restart 命中是 BCL FileSystemWatcher.Restart）",
                "g30-console-decompile.md / cc-41747-13/scans/absence.txt"),
            new RouteCell(EvidenceMark.ProvenAbsent,
                "30 系载荷 HOTSWAP 0 命中（大小写不敏感），无 PDB 符号",
                "g30-console-decompile.md / cc-41747-13/scans/absence.txt"),
            new[] { ToggleOn, ToggleOff },
            Array.Empty<OemDisplayModeEncoding>()),

        // N11: 40-series WITH 双显三模 (hybrid / dGPU-direct / iGPU) - the 5.17.51.27 console.
        new GenerationRouteFacts(
            DgpuGenerationKind.Gen40,
            new RouteCell(EvidenceMark.Proven,
                "控制台侧（PROVEN——逐方法反编译）：MQTT `Setting/Control`；动作词汇 DGPU_DIRECT_CONNECT_TOGGLE_ON/OFF/IGPU、" +
                "DGPU_DIRECT_CONNECT_RESTART、IGPU_ONLY_CONNECT_RB_ON/OFF（带双显三模档）、GETSTATUS",
                "MyControlCenter/Topic.cs:51,123-125 / MyControlCenter/MqttClientCtrl.cs:59-65,92-113 / MySettingManager.cs:1229-1271"),
            new RouteCell(EvidenceMark.Proven,
                "服务侧 SetFwVars(\"OemDisplayMode\")（NVRAM + WMI 0x30000000x iGPU-only）",
                "MySettingManager.cs:1229-1271 / WMIEC.cs:403-472 / GCUService.decompiled.cs:45504-45532"),
            new RouteCell(EvidenceMark.Proven,
                "IGPU_ONLY_CONNECT_RB_ON/OFF（WMI 0x300000001/0x300000000）",
                "WMIEC.cs:403-472 / gpu-mode-matrix.md A (40A)"),
            new RouteCell(EvidenceMark.Proven,
                "DGPU_DIRECT_CONNECT_RESTART → shutdown /r /t 0",
                "MySettingManager.cs:1292-1311 / gpu-mode-matrix.md A"),
            new RouteCell(EvidenceMark.Unknown,
                "40 系无热切换动作词汇（GPU_HOTSWAP_* 0 命中）",
                "gpu-mode-matrix.md A"),
            new[] { ToggleOn, ToggleOff, ToggleIgpu, IgpuOnlyOn, IgpuOnlyOff, Restart },
            new[]
            {
                new OemDisplayModeEncoding("AMD", 1, 0, 2, "MySettingManager.cs:1229-1271"),
                new OemDisplayModeEncoding("Intel", 2, 4, 1, "MySettingManager.cs:1229-1271"),
            })
        { ThreeMode = true },

        // N11: 40-series WITHOUT 双显三模 - the 5.17.49.19 console. No IGPU_ONLY_* vocabulary.
        new GenerationRouteFacts(
            DgpuGenerationKind.Gen40,
            new RouteCell(EvidenceMark.Proven,
                "控制台侧（PROVEN——逐方法反编译）：MQTT `Setting/Control`；动作词汇 DGPU_DIRECT_CONNECT_TOGGLE_ON/OFF、" +
                "DGPU_DIRECT_CONNECT_RESTART、GETSTATUS；**无 IGPU_ONLY_***（不带双显三模档）",
                "ControlCenter_5.17.49.19 控制台反编译 / gpu-mode-matrix.md A (40B)"),
            new RouteCell(EvidenceMark.Proven,
                "服务侧 SetFwVars(\"OemDisplayMode\")（仅 NVRAM，无 WMI iGPU-only 路径）",
                "MySettingManager.cs:1229-1271 / GCUService.decompiled.cs:45504-45532"),
            new RouteCell(EvidenceMark.ProvenAbsent,
                "不带双显三模档：IGPU_ONLY_* 在该档控制台载荷中 0 命中",
                "ControlCenter_5.17.49.19 控制台反编译 / gpu-mode-matrix.md A (40B)"),
            new RouteCell(EvidenceMark.Proven,
                "DGPU_DIRECT_CONNECT_RESTART → shutdown /r /t 0",
                "MySettingManager.cs:1292-1311 / gpu-mode-matrix.md A"),
            new RouteCell(EvidenceMark.Unknown,
                "40 系无热切换动作词汇（GPU_HOTSWAP_* 0 命中）",
                "gpu-mode-matrix.md A"),
            new[] { ToggleOn, ToggleOff, ToggleIgpu, Restart },
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
                "IGPU_ONLY_CONNECT_RB_ON/OFF/AUTO、DGPU_DIRECT_CONNECT_RESTART、GPU_HOTSWAP_ON/OFF",
                "CCUWinUI.decompiled.cs:86477-86515,53527-53729,139137-139174"),
            new RouteCell(EvidenceMark.Unknown,
                "50 系服务 IL 混淆，实际写路径（NVRAM/EC/DLL）不可静态确定",
                "gpu-mode-matrix.md A (50 行) / UNKNOWN#1"),
            new RouteCell(EvidenceMark.Proven,
                "IGPU_ONLY_CONNECT_RB_ON/OFF，每 2 s 重发、count>60 放弃、每第 4 次额外重发",
                "CCUWinUI.decompiled.cs:53527-53580"),
            new RouteCell(EvidenceMark.Proven,
                "DGPU_DIRECT_CONNECT_RESTART 在 Task.Delay(800) 后发出",
                "CCUWinUI.decompiled.cs:86511-86515"),
            new RouteCell(EvidenceMark.Inferred,
                "GPU_HOTSWAP_ON/OFF 枚举存在，但处理器是 log-only no-op，wire 用法未证",
                "CCUWinUI.decompiled.cs:53420-53435,139141-139142"),
            new[] { ToggleOn, ToggleOff, ToggleIgpu, IgpuOnlyOn, IgpuOnlyOff, IgpuOnlyAuto, Restart, HotSwapOn, HotSwapOff },
            Array.Empty<OemDisplayModeEncoding>()),
    };

    /// <summary>找一行；找不到返回 <c>null</c>（<c>Unknown</c>/<c>NoDgpu</c> 永远没有行）。</summary>
    public static GenerationRouteFacts? Find(DgpuGenerationKind generation) =>
        Rows.FirstOrDefault(row => row.Generation == generation);

    /// <summary>
    /// N11: find the row for a generation + capability tier. For generations without a tier split
    /// (30/50) the <paramref name="threeMode"/> argument is ignored.
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

    /// <summary>该代际的控制台是否有 iGPU-only 动作。30 系确证不存在（元数据枚举堆 + PDB 符号 + 全载荷 0 命中）。</summary>
    public static bool AllowsIgpuOnly(DgpuGenerationKind generation)
    {
        IReadOnlyList<string> actions = ConsoleActions(generation);
        return actions.Contains(IgpuOnlyOn, StringComparer.Ordinal) ||
               actions.Contains(IgpuOnlyOff, StringComparer.Ordinal) ||
               actions.Contains(IgpuOnlyAuto, StringComparer.Ordinal);
    }

    /// <summary>该代际的控制台是否有 RESTART 动作。30 系确证不存在（同上）。</summary>
    public static bool AllowsRestart(DgpuGenerationKind generation) =>
        ConsoleActions(generation).Contains(Restart, StringComparer.Ordinal);

    /// <summary>该代际的控制台是否有热切换动作。30 系确证不存在；仅 50 系出现该动作词汇（处理器 no-op，见事实表）。</summary>
    public static bool AllowsHotSwap(DgpuGenerationKind generation)
    {
        IReadOnlyList<string> actions = ConsoleActions(generation);
        return actions.Contains(HotSwapOn, StringComparer.Ordinal) ||
               actions.Contains(HotSwapOff, StringComparer.Ordinal);
    }

    /// <summary>该代际控制台的动作词汇；<c>Unknown</c>/<c>NoDgpu</c> 返回空。</summary>
    public static IReadOnlyList<string> ConsoleActions(DgpuGenerationKind generation) =>
        Find(generation)?.ConsoleActions ?? Array.Empty<string>();

    /// <summary>厂商控制台词汇表。成员资格不是放行：没判出代际时事实表没有行，一个动作都不发。</summary>
    static readonly IReadOnlySet<string> KnownActions = new HashSet<string>(StringComparer.Ordinal)
    {
        ToggleOn, ToggleOff, ToggleIgpu, Restart, IgpuOnlyOn, IgpuOnlyOff, IgpuOnlyAuto, HotSwapOn, HotSwapOff,
    };

    /// <summary>该动作是否属于厂商控制台词汇表。不是权限：权限见 <see cref="DisplayRoutePolicy.AllowsAction"/>。</summary>
    public static bool IsKnownAction(string action) =>
        !string.IsNullOrWhiteSpace(action) && KnownActions.Contains(action);
}

/// <summary>
/// 代际边界策略：把事实表翻译成"这个动作在这个代际能不能发"。
/// **只收紧、不发明**：表里没有的动作一律不许发。
/// <c>Unknown</c>/<c>NoDgpu</c> 没有事实行，因此一个代际动作都不放行——
/// 读不到代际不等于把 30/40/50 的动作并集都放开（那会发出事实表标成 ProvenAbsent 的重启/核显/热切）。
/// </summary>
public static class DisplayRoutePolicy
{
    /// <summary>该动作在该代际是否允许。没有事实行、或动作不在该行词汇内时返回 <c>false</c>。</summary>
    public static bool AllowsAction(DgpuGenerationKind generation, string action)
    {
        if (string.IsNullOrWhiteSpace(action)) return false;

        return DisplayRouteMatrix.ConsoleActions(generation).Contains(action, StringComparer.Ordinal);
    }

    /// <summary>
    /// Production GPU gate: use <see cref="DisplayRouteMatrix.FindTier"/>, never <see cref="DisplayRouteMatrix.Find"/>.
    /// <paramref name="threeMode"/> is <c>IgpuOnlyStatusSupport == true</c>; do not default it true.
    /// <c>Unknown</c>/<c>NoDgpu</c> have no tier row, so this returns <c>false</c>.
    /// </summary>
    public static bool AllowsAction(DgpuGenerationKind generation, string action, bool threeMode)
    {
        if (string.IsNullOrWhiteSpace(action)) return false;

        IReadOnlyList<string> actions =
            DisplayRouteMatrix.FindTier(generation, threeMode)?.ConsoleActions ?? Array.Empty<string>();
        return actions.Contains(action, StringComparer.Ordinal);
    }
}
