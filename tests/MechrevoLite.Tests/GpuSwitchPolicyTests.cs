using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

public class GpuSwitchPolicyTests
{
    [Theory]
    [InlineData(MechrevoService.GpuStandard, 1, MechrevoService.GpuIGpu, true, "HotSwitch")]
    [InlineData(MechrevoService.GpuStandard, 1, MechrevoService.GpuIGpu, false, "Direct")]
    [InlineData(MechrevoService.GpuAuto, 1, MechrevoService.GpuIGpu, true, "HotSwitch")]
    [InlineData(MechrevoService.GpuAuto, 2, MechrevoService.GpuStandard, true, "Direct")]
    [InlineData(MechrevoService.GpuDgpu, -1, MechrevoService.GpuIGpu, true, "Restart")]
    [InlineData(MechrevoService.GpuDgpu, -1, MechrevoService.GpuStandard, true, "Restart")]
    [InlineData(MechrevoService.GpuStandard, 1, MechrevoService.GpuDgpu, true, "Restart")]
    public void GpuRoute_FollowsTheVendorResolveTable(
        int reportedMode, int automaticRuntime, int targetMode, bool supported, string expected)
    {
        // D4: mux/Dgpu → Restart; Standard→iGPU + hotswap → HotSwitch; else Direct.
        // 旧断言（一切模式变更 Restart）编码的是被推翻的恒重启契约，故更新为厂商表。
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

        Assert.False(plan.RequiresDgpuProcessPreflight);
    }

    [Theory]
    [InlineData(MechrevoService.GpuStandard, 1, MechrevoService.GpuStandard, true, "NoChange")]
    [InlineData(MechrevoService.GpuStandard, 1, MechrevoService.GpuStandard, false, "Direct")]
    [InlineData(MechrevoService.GpuIGpu, 2, MechrevoService.GpuIGpu, false, "Direct")]
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
