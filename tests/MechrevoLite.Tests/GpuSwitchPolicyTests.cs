using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

public class GpuSwitchPolicyTests
{
    [Theory]
    [InlineData(MechrevoService.GpuStandard, 1, MechrevoService.GpuIGpu, true, "Restart")]
    [InlineData(MechrevoService.GpuStandard, 1, MechrevoService.GpuIGpu, false, "Restart")]
    [InlineData(MechrevoService.GpuAuto, 1, MechrevoService.GpuIGpu, true, "Restart")]
    [InlineData(MechrevoService.GpuAuto, 2, MechrevoService.GpuStandard, true, "Restart")]
    [InlineData(MechrevoService.GpuDgpu, -1, MechrevoService.GpuIGpu, true, "Restart")]
    [InlineData(MechrevoService.GpuDgpu, -1, MechrevoService.GpuStandard, true, "Restart")]
    public void GpuRoute_AlwaysRestartsForAnyModeChange(
        int reportedMode, int automaticRuntime, int targetMode, bool supported, string expected)
    {
        // 本机固件不做可靠的实时切换（RB 热载荷重启后也不套用），所有模式变更一律重启路径。
        Assert.Equal(expected, GpuSwitchPolicy.Resolve(
            reportedMode, automaticRuntime, targetMode, supported).Route.ToString());
    }

    [Fact]
    public void GpuRoute_NeverRequiresDgpuProcessPreflight()
    {
        GpuSwitchPlan plan = GpuSwitchPolicy.Resolve(
            MechrevoService.GpuStandard,
            automaticRuntime: 1,
            MechrevoService.GpuIGpu,
            supportsHotSwap: true);

        // 热切换路径已整体移除，不再有杀独显进程的前置流程。
        Assert.False(plan.RequiresDgpuProcessPreflight);
    }

    [Theory]
    [InlineData(MechrevoService.GpuStandard, 1, MechrevoService.GpuStandard, true, "NoChange")]
    [InlineData(MechrevoService.GpuStandard, 1, MechrevoService.GpuStandard, false, "Restart")]
    [InlineData(MechrevoService.GpuIGpu, 2, MechrevoService.GpuIGpu, false, "Restart")]
    public void CachedGpuStatus_NeverTurnsARequestedSwitchIntoANoOp(
        int reportedMode, int automaticRuntime, int targetMode, bool fresh, string expected)
    {
        Assert.Equal(expected, GpuSwitchPolicy.Resolve(
            reportedMode, automaticRuntime, targetMode, supportsHotSwap: true,
            currentStateFresh: fresh).Route.ToString());
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void AutomaticGpuMode_DefersOnlyWhileTheCreatedGcuPathIsUnavailable(
        bool hardwareCreated,
        bool gcuConnected,
        bool serviceAvailable,
        bool expected)
    {
        Assert.Equal(expected, GpuSwitchPolicy.ShouldDeferUntilGcuConnection(
            hardwareCreated, gcuConnected, serviceAvailable));
    }
}
