namespace MechrevoLite.Hardware;

internal enum GpuSwitchRoute
{
    NoChange,
    Direct,
    HotSwitch,
    Restart,
}

internal readonly record struct GpuSwitchPlan(
    GpuSwitchRoute Route,
    bool RequiresDgpuProcessPreflight);

internal static class GpuSwitchPolicy
{
    internal static bool ShouldDeferUntilGcuConnection(
        bool hardwareCreated,
        bool gcuConnected,
        bool serviceAvailable) =>
        hardwareCreated && (!gcuConnected || !serviceAvailable);

    internal static GpuSwitchPlan Resolve(
        int reportedMode,
        int automaticRuntime,
        int targetMode,
        bool supportsHotSwap,
        bool currentStateFresh = true)
    {
        int currentMode = ResolveCurrentMode(reportedMode, automaticRuntime);
        if (currentMode == targetMode && currentStateFresh)
            return new(GpuSwitchRoute.NoChange, false);

        // 所有GPU模式切换都强制要求重启，确保100%切换成功
        return new(GpuSwitchRoute.Restart, false);
    }

    static int ResolveCurrentMode(int reportedMode, int automaticRuntime) =>
        reportedMode != MechrevoService.GpuAuto ? reportedMode : automaticRuntime switch
        {
            1 => MechrevoService.GpuStandard,
            2 => MechrevoService.GpuIGpu,
            _ => -1,
        };
}
