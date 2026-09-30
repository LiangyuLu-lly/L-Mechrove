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
/// 显卡切换命令层（MQTT-only）。动作集按 <see cref="DisplayRouteMatrix"/> 的逐代事实表 × 服务档位收窄：
/// 代际判不出（Unknown/NoDgpu）或服务档位未知时一条代际动作都不发。
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

    /// <summary>
    /// 重启路由命令序列：MUX 目标载荷，服务档位能重启时再加 800 ms 后的 RESTART。
    /// 无可用载荷时返回空序列（调用方 fail closed，绝不单发 RESTART）。
    /// </summary>
    public static IReadOnlyList<GpuRouteCommand> BuildRestartCommands(int mode, GpuRouteContext context)
    {
        GpuRestartRoute route = MechrevoService.CreateGpuRestartRoute(mode, context);
        if (route.Payloads.Count == 0) return Array.Empty<GpuRouteCommand>();

        var commands = new List<GpuRouteCommand>(route.Payloads.Count + 1);
        foreach (Dictionary<string, object> payload in route.Payloads)
            commands.Add(new GpuRouteCommand(MqttTopics.SettingControl, ActionOf(payload), payload, 0));

        if (route.ServiceRestart)
        {
            commands.Add(new GpuRouteCommand(
                MqttTopics.SettingControl,
                DisplayRouteMatrix.Restart,
                new Dictionary<string, object> { ["Action"] = DisplayRouteMatrix.Restart },
                RestartDelayMilliseconds));
        }
        return commands;
    }

    /// <summary>
    /// 热切换命令（集显 = RB_ON、标准 = RB_OFF，均带 <c>SetToWMIEC=OK</c>；自动 = RB_AUTO）。
    /// 只有 50 系热切换机型 + 我方 1.2 服务放行，其余返回 <c>null</c>（调用方 fail closed）。
    /// </summary>
    public static GpuRouteCommand? BuildHotSwitchCommand(int mode, GpuRouteContext context)
    {
        if (mode is not (MechrevoService.GpuIGpu or MechrevoService.GpuStandard or MechrevoService.GpuAuto)) return null;
        Dictionary<string, object> payload = MechrevoService.CreateGpuModePayload(mode);
        string action = ActionOf(payload);
        return DisplayRoutePolicy.AllowsAction(context.Generation, action, context.ThreeMode, context.Tier, context.HotSwap)
            ? new GpuRouteCommand(MqttTopics.SettingControl, action, payload, 0)
            : null;
    }

    /// <summary>该动作是否在该代际词汇内（转发 <see cref="DisplayRoutePolicy"/>，不看服务档位）。</summary>
    public static bool IsAllowedByGeneration(DgpuGenerationKind generation, string action) =>
        DisplayRoutePolicy.AllowsAction(generation, action);

    /// <summary>Tier-aware vocabulary gate: <paramref name="threeMode"/> is MQTT iGPU-only support, never defaulted true.</summary>
    public static bool IsAllowedByGeneration(DgpuGenerationKind generation, string action, bool threeMode) =>
        DisplayRoutePolicy.AllowsAction(generation, action, threeMode);

    static string ActionOf(Dictionary<string, object> payload) =>
        payload.TryGetValue("Action", out object? value) ? value?.ToString() ?? "" : "";
}
