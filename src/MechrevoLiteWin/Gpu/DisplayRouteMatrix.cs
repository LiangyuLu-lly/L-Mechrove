namespace MechrevoLite.Gpu;

/// <summary>事实格的可信度标记。**UNKNOWN 不等于 INFERRED，更不等于 PROVEN**——未证实的能力不得被门控放行。</summary>
public enum EvidenceMark
{
    /// <summary>没有可引用的证据。</summary>
    Unknown,

    /// <summary>有证据链但未逐方法反编译确认（跨代推断 / 字符串证据）。</summary>
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

/// <summary>一个代际的显示路由事实。</summary>
public sealed record GenerationRouteFacts(
    DgpuGenerationKind Generation,
    RouteCell ConsoleCarrier,
    RouteCell ServiceWritePath,
    RouteCell IgpuOnly,
    RouteCell Restart,
    RouteCell HotSwap,
    IReadOnlyList<string> ConsoleActions,
    IReadOnlyList<OemDisplayModeEncoding> OemDisplayMode);

/// <summary>
/// 逐代（**轴 2**）显示路由事实表。代际由运行时探测（<see cref="DgpuGenerationProbe"/>），
/// **不由平台代号推导**。
///
/// <para>本表只记录"厂商控制台会发什么 / 厂商服务怎么写"，本产品**只发 MQTT**：
/// 控制台不写 <c>OemDisplayMode</c>、不写任何固件变量、没有写固件变量的接缝。</para>
///
/// <para><b>关键分界</b>：<c>ServiceWritePath</c>（路由到底怎么落地）只有 40 系 PROVEN；
/// 30/50 系保持 UNKNOWN（30 系未反编译 MySettingManager，50 系服务 IL 混淆）。</para>
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
            new RouteCell(EvidenceMark.Proven, "MQTT Setting/Control", "g30svc.w.txt:7468,7497 / g30svc.a.txt:5144,5438"),
            new RouteCell(EvidenceMark.Unknown,
                "NvramVariable.SetFwVars(\"OemDisplayMode\") 字符串在，但 30 系服务 MySettingManager 未反编译，写路径形态未知",
                "gpu-mode-matrix.md A (30 行, UNKNOWN#5)"),
            new RouteCell(EvidenceMark.ProvenAbsent,
                "30 系载荷里 IGPU_ONLY_* 0 命中",
                "gpu-mode-matrix.md A / console-compare g30* 全量字符串检索"),
            new RouteCell(EvidenceMark.ProvenAbsent,
                "30 系载荷里 *_RESTART 0 命中",
                "gpu-mode-matrix.md A / console-compare g30* 全量字符串检索"),
            new RouteCell(EvidenceMark.Unknown,
                "30 系载荷无 GPU_HOTSWAP_* 字符串，wire 用法未证",
                "gpu-mode-matrix.md A"),
            new[] { ToggleOn, ToggleOff },
            Array.Empty<OemDisplayModeEncoding>()),

        new GenerationRouteFacts(
            DgpuGenerationKind.Gen40,
            new RouteCell(EvidenceMark.Proven, "MQTT Setting/Control", "MySettingManager.cs:1229-1271 / GCUService.decompiled.cs:45504-45532"),
            new RouteCell(EvidenceMark.Proven,
                "服务侧 SetFwVars(\"OemDisplayMode\")（40A NVRAM + WMI 0x30000000x iGPU-only；40B 仅 NVRAM）",
                "MySettingManager.cs:1229-1271 / WMIEC.cs:403-472 / GCUService.decompiled.cs:45504-45532"),
            new RouteCell(EvidenceMark.Proven,
                "IGPU_ONLY_CONNECT_RB_ON/OFF（40A，WMI 0x300000001/0x300000000）",
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
            }),

        new GenerationRouteFacts(
            DgpuGenerationKind.Gen50,
            new RouteCell(EvidenceMark.Proven, "MQTT Setting/Control", "CCUWinUI.decompiled.cs:86477-86515,53527-53729,139137-139174"),
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

    /// <summary>该代际的"服务侧写路由"落地方式可信度。只有 40 系是 PROVEN。</summary>
    public static EvidenceMark ServiceWritePathMark(DgpuGenerationKind generation) =>
        Find(generation)?.ServiceWritePath.Mark ?? EvidenceMark.Unknown;

    /// <summary>路由落地是否已证实（仅 40 系）。</summary>
    public static bool IsDisplayRouteWritePathProven(DgpuGenerationKind generation) =>
        ServiceWritePathMark(generation) == EvidenceMark.Proven;

    /// <summary>该代际的控制台是否有 iGPU-only 动作。30 系确证不存在。</summary>
    public static bool AllowsIgpuOnly(DgpuGenerationKind generation)
    {
        IReadOnlyList<string> actions = ConsoleActions(generation);
        return actions.Contains(IgpuOnlyOn, StringComparer.Ordinal) ||
               actions.Contains(IgpuOnlyOff, StringComparer.Ordinal) ||
               actions.Contains(IgpuOnlyAuto, StringComparer.Ordinal);
    }

    /// <summary>该代际的控制台是否有 RESTART 动作。30 系确证不存在。</summary>
    public static bool AllowsRestart(DgpuGenerationKind generation) =>
        ConsoleActions(generation).Contains(Restart, StringComparer.Ordinal);

    /// <summary>该代际的控制台是否有热切换动作。仅 50 系出现该动作词汇（处理器 no-op，见事实表）。</summary>
    public static bool AllowsHotSwap(DgpuGenerationKind generation)
    {
        IReadOnlyList<string> actions = ConsoleActions(generation);
        return actions.Contains(HotSwapOn, StringComparer.Ordinal) ||
               actions.Contains(HotSwapOff, StringComparer.Ordinal);
    }

    /// <summary>该代际控制台的动作词汇；<c>Unknown</c>/<c>NoDgpu</c> 返回空。</summary>
    public static IReadOnlyList<string> ConsoleActions(DgpuGenerationKind generation) =>
        Find(generation)?.ConsoleActions ?? Array.Empty<string>();

    /// <summary>全部已知动作词汇（"没判出代际"时照旧放行的动作集合）。</summary>
    static readonly IReadOnlySet<string> KnownActions = new HashSet<string>(StringComparer.Ordinal)
    {
        ToggleOn, ToggleOff, ToggleIgpu, Restart, IgpuOnlyOn, IgpuOnlyOff, IgpuOnlyAuto, HotSwapOn, HotSwapOff,
    };

    /// <summary>该动作是否属于厂商控制台词汇表（用于"没判出代际"时不放行发明出来的动作）。</summary>
    public static bool IsKnownAction(string action) =>
        !string.IsNullOrWhiteSpace(action) && KnownActions.Contains(action);
}

/// <summary>
/// 代际边界策略：把事实表翻译成"这个动作在这个代际能不能发"。
/// **只收紧、不发明**：表里说某代际没有的动作一律不许发；<c>Unknown</c>/<c>NoDgpu</c>
/// 不套用任何代际的限制（读不到代际不等于某个已知代际）。
/// </summary>
public static class DisplayRoutePolicy
{
    /// <summary>该动作在该代际是否允许。动作名不在控制台词汇表内时返回 <c>false</c>。</summary>
    public static bool AllowsAction(DgpuGenerationKind generation, string action)
    {
        if (string.IsNullOrWhiteSpace(action)) return false;

        if (generation is DgpuGenerationKind.Unknown or DgpuGenerationKind.NoDgpu)
            return DisplayRouteMatrix.IsKnownAction(action);

        return DisplayRouteMatrix.ConsoleActions(generation).Contains(action, StringComparer.Ordinal);
    }
}
