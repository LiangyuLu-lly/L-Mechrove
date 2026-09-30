namespace MechrevoLite.Hardware;

internal enum GpuSwitchRoute
{
    NoChange,

    /// <summary>50 系热切换机型上的集显 ↔ 标准：RB_ON / RB_OFF，不重启，设备在位回读确认。</summary>
    HotSwitch,

    /// <summary>MUX 目标（直连 / 混合 / NVRAM 核显）+ 重启。</summary>
    Restart,
}

internal readonly record struct GpuSwitchPlan(
    GpuSwitchRoute Route,
    bool RequiresDgpuProcessPreflight);

/// <summary>
/// 切换路由判定（§7 / G8）：涉及直连或 NVRAM 核显目标的一律重启；标准 ↔ 集显只有热切换机型能不重启。
/// 过去的「Direct」路由（非热切换机型上直接发 RB_*、按服务回显判成功）已删除——40 系三模档上
/// 那条路只改服务寄存器，硬件什么都没变。
/// </summary>
internal static class GpuSwitchPolicy
{
    internal static bool ShouldDeferUntilGcuConnection(
        bool hardwareCreated,
        bool gcuConnected,
        bool serviceAvailable) =>
        hardwareCreated && (!gcuConnected || !serviceAvailable);

    /// <param name="currentMode">当前模式：优先用硬件回读出的实际路由，读不到时用服务上报。</param>
    /// <param name="automaticRuntime">服务自动档的运行态（1 混合 / 2 核显），仅当前为自动档时使用。</param>
    /// <param name="supportsHotSwap">本机是否可提供热切换（<see cref="MechrevoHw.CanOfferGpuHotSwap"/>）。</param>
    internal static GpuSwitchPlan Resolve(
        int currentMode,
        int automaticRuntime,
        int targetMode,
        bool supportsHotSwap,
        bool currentStateFresh = true)
    {
        int current = ResolveCurrentMode(currentMode, automaticRuntime);
        if (current == targetMode)
        {
            if (currentStateFresh) return new(GpuSwitchRoute.NoChange, false);
            // 状态不新鲜时不能当成「已经是」：按目标重新应用一次，走能到达它的最轻路径。
            if (supportsHotSwap && targetMode is MechrevoService.GpuStandard or MechrevoService.GpuIGpu)
                return new(GpuSwitchRoute.HotSwitch, targetMode == MechrevoService.GpuIGpu);
            return new(GpuSwitchRoute.Restart, false);
        }

        if (current == MechrevoService.GpuDgpu || targetMode == MechrevoService.GpuDgpu)
            return new(GpuSwitchRoute.Restart, false);

        if (supportsHotSwap && IsHotPair(current, targetMode))
            return new(GpuSwitchRoute.HotSwitch, targetMode == MechrevoService.GpuIGpu);

        return new(GpuSwitchRoute.Restart, false);
    }

    static bool IsHotPair(int current, int target) =>
        (current == MechrevoService.GpuStandard && target == MechrevoService.GpuIGpu) ||
        (current == MechrevoService.GpuIGpu && target == MechrevoService.GpuStandard);

    static int ResolveCurrentMode(int reportedMode, int automaticRuntime) =>
        reportedMode != MechrevoService.GpuAuto ? reportedMode : automaticRuntime switch
        {
            1 => MechrevoService.GpuStandard,
            2 => MechrevoService.GpuIGpu,
            _ => -1,
        };
}
