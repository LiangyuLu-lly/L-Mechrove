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

        if (currentMode == MechrevoService.GpuDgpu || targetMode == MechrevoService.GpuDgpu)
            return new(GpuSwitchRoute.Restart, false);

        if (currentMode == MechrevoService.GpuStandard &&
            targetMode == MechrevoService.GpuIGpu &&
            supportsHotSwap)
            return new(GpuSwitchRoute.HotSwitch, false);

        return new(GpuSwitchRoute.Direct, false);
    }

    static int ResolveCurrentMode(int reportedMode, int automaticRuntime) =>
        reportedMode != MechrevoService.GpuAuto ? reportedMode : automaticRuntime switch
        {
            1 => MechrevoService.GpuStandard,
            2 => MechrevoService.GpuIGpu,
            _ => -1,
        };
}
