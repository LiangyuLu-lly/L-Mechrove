using MechrevoLite.Gpu;

namespace MechrevoLite.Hardware;

/// <summary>
/// 一条显示路由命令：主题 + MQTT Action + 载荷 + 发这条之前要等的毫秒数。
/// 主题恒为 <c>Setting/Control</c>——显示路由**只走 MQTT**，不碰固件变量、不碰 EC。
/// </summary>
public sealed record GpuRouteCommand(
    string Topic,
    string Action,
    Dictionary<string, object> Payload,
    int DelayBeforeMilliseconds);

/// <summary>
/// 显卡切换命令层（MQTT-only）。动作集按 <see cref="DisplayRouteMatrix"/> 的逐代事实表收窄：
/// 30 系不出现 iGPU-only / RESTART；代际判不出（Unknown/NoDgpu）时没有事实行，一条代际动作都不发。
///
/// <para>本层只构造与判据，不做任何 EC/固件写；发布由调用方经 <c>MechrevoHw.Publish</c> 完成。</para>
/// </summary>
public static class GpuRouteCommandLayer
{
    /// <summary>厂商在发 DGPU_DIRECT_CONNECT_RESTART 前等 800 ms（CCUWinUI:86511-86515）。</summary>
    public const int RestartDelayMilliseconds = 800;

    /// <summary>iGPU-only 重发间隔 2 s（CCUWinUI:53550）。</summary>
    public const int RetryIntervalMilliseconds = 2000;

    /// <summary>每第 4 次轮询额外重发（CCUWinUI:53565）。</summary>
    public const int RetryEveryPolls = 4;

    /// <summary>构造重启路由命令序列（目标载荷 + 800 ms 后的 RESTART）；无可用载荷时返回空序列。</summary>
    public static IReadOnlyList<GpuRouteCommand> BuildRestartCommands(
        int mode, bool supportsDgpuDirect, DgpuGenerationKind generation)
    {
        IReadOnlyList<Dictionary<string, object>> payloads =
            MechrevoService.CreateGpuRestartTargetPayloads(mode, supportsDgpuDirect, generation);
        if (payloads.Count == 0) return Array.Empty<GpuRouteCommand>();

        var commands = new List<GpuRouteCommand>(payloads.Count + 1);
        foreach (Dictionary<string, object> payload in payloads)
            commands.Add(new GpuRouteCommand(MqttTopics.SettingControl, ActionOf(payload), payload, 0));

        commands.Add(new GpuRouteCommand(
            MqttTopics.SettingControl,
            DisplayRouteMatrix.Restart,
            new Dictionary<string, object> { ["Action"] = DisplayRouteMatrix.Restart },
            RestartDelayMilliseconds));
        return commands;
    }

    /// <summary>构造单条切换命令；该动作不在本代际动作词汇内时返回 <c>null</c>（调用方 fail-closed）。</summary>
    public static GpuRouteCommand? BuildSwitchCommand(
        int mode, bool supportsDgpuDirect, bool supportsIgpuOnly, bool useHotSwitch,
        DgpuGenerationKind generation)
    {
        Dictionary<string, object> payload =
            MechrevoService.CreateGpuSwitchPayload(mode, supportsDgpuDirect, supportsIgpuOnly, useHotSwitch);
        string action = ActionOf(payload);
        return IsAllowedByGeneration(generation, action, supportsIgpuOnly)
            ? new GpuRouteCommand(MqttTopics.SettingControl, action, payload, 0)
            : null;
    }

    /// <summary>该动作在该代际是否允许（转发 <see cref="DisplayRoutePolicy"/>）。</summary>
    public static bool IsAllowedByGeneration(DgpuGenerationKind generation, string action) =>
        DisplayRoutePolicy.AllowsAction(generation, action);

    /// <summary>Tier-aware gate: <paramref name="threeMode"/> is MQTT iGPU-only support, never defaulted true.</summary>
    public static bool IsAllowedByGeneration(DgpuGenerationKind generation, string action, bool threeMode) =>
        DisplayRoutePolicy.AllowsAction(generation, action, threeMode);

    static string ActionOf(Dictionary<string, object> payload) =>
        payload.TryGetValue("Action", out object? value) ? value?.ToString() ?? "" : "";
}
