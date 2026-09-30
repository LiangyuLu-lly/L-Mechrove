using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 切换路由（G8）：直连、NVRAM 核显目标一律重启；标准 ↔ 集显只有 50 系热切换机型能不重启。
/// 过去的「Direct」路由（非热切换机型上直接发 RB_*、按服务回显判成功）已删除：
/// 40 系三模档上那条路只改服务寄存器，硬件什么都没变。
/// </summary>
public class GpuSwitchPolicyTests
{
    [Theory]
    [InlineData(MechrevoService.GpuStandard, 1, MechrevoService.GpuIGpu, true, "HotSwitch")]
    [InlineData(MechrevoService.GpuStandard, 1, MechrevoService.GpuIGpu, false, "Restart")]
    [InlineData(MechrevoService.GpuAuto, 1, MechrevoService.GpuIGpu, true, "HotSwitch")]
    [InlineData(MechrevoService.GpuAuto, 2, MechrevoService.GpuStandard, true, "HotSwitch")]
    [InlineData(MechrevoService.GpuIGpu, 2, MechrevoService.GpuStandard, false, "Restart")]
    [InlineData(MechrevoService.GpuDgpu, -1, MechrevoService.GpuIGpu, true, "Restart")]
    [InlineData(MechrevoService.GpuDgpu, -1, MechrevoService.GpuStandard, true, "Restart")]
    [InlineData(MechrevoService.GpuStandard, 1, MechrevoService.GpuDgpu, true, "Restart")]
    public void GpuRoute_FollowsTheOfficialLayouts(
        int currentMode, int automaticRuntime, int targetMode, bool hotSwap, string expected)
    {
        Assert.Equal(expected, GpuSwitchPolicy.Resolve(
            currentMode, automaticRuntime, targetMode, hotSwap).Route.ToString());
    }

    [Fact]
    public void GpuRoute_HasNoDirectRouteAnyMore()
    {
        Assert.DoesNotContain("Direct", Enum.GetNames<GpuSwitchRoute>());
    }

    /// <summary>断开独显前要先看谁占着它（RB_ON 断不开被占用的独显）；恢复独显和重启路由不需要。</summary>
    [Fact]
    public void OnlyTheHotSwitchIntoIgpuNeedsTheDgpuProcessPreflight()
    {
        Assert.True(GpuSwitchPolicy.Resolve(
            MechrevoService.GpuStandard, 1, MechrevoService.GpuIGpu, supportsHotSwap: true).RequiresDgpuProcessPreflight);
        Assert.False(GpuSwitchPolicy.Resolve(
            MechrevoService.GpuIGpu, 2, MechrevoService.GpuStandard, supportsHotSwap: true).RequiresDgpuProcessPreflight);
        Assert.False(GpuSwitchPolicy.Resolve(
            MechrevoService.GpuStandard, 1, MechrevoService.GpuDgpu, supportsHotSwap: true).RequiresDgpuProcessPreflight);
    }

    [Theory]
    [InlineData(MechrevoService.GpuStandard, 1, MechrevoService.GpuStandard, true, true, "NoChange")]
    [InlineData(MechrevoService.GpuStandard, 1, MechrevoService.GpuStandard, false, true, "HotSwitch")]
    [InlineData(MechrevoService.GpuIGpu, 2, MechrevoService.GpuIGpu, false, true, "HotSwitch")]
    [InlineData(MechrevoService.GpuStandard, 1, MechrevoService.GpuStandard, false, false, "Restart")]
    [InlineData(MechrevoService.GpuDgpu, -1, MechrevoService.GpuDgpu, false, true, "Restart")]
    public void CachedGpuStatus_NeverTurnsARequestedSwitchIntoANoOp(
        int currentMode, int automaticRuntime, int targetMode, bool fresh, bool hotSwap, string expected)
    {
        Assert.Equal(expected, GpuSwitchPolicy.Resolve(
            currentMode, automaticRuntime, targetMode, supportsHotSwap: hotSwap,
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
