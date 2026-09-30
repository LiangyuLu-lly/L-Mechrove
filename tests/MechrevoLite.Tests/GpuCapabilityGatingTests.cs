using MechrevoLite;
using MechrevoLite.Gpu;
using MechrevoLite.Hardware;


namespace MechrevoLite.Tests;

/// <summary>
/// 能力级门控（轴 1 机型支持 × 轴 2 代际事实）happy 路径：代际允许的动作照常提供。
/// 未判出代际不得借此放行，见 <see cref="GpuCapabilityGatingFailTests"/>。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class GpuCapabilityGatingTests
{
    [Fact]
    public void Gen40StillOffersTheCapabilitiesItsProfileClaims()
    {
        using var generation = GpuCapabilityGatingHarness.Generation(GpuCapabilityGatingHarness.Gen40);
        using MechrevoHw hardware = GpuCapabilityGatingHarness.Hardware(new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            IgpuOnly = true,
            DgpuDirect = true,
        });
        // D1: iGPU offer is MQTT IgpuOnlyStatusSupport == true only; the registry profile is not an offer bit.
        hardware.SetIgpuOnlyStatusSupportForTests(true);

        Assert.True(hardware.SupportsIgpuOnly);
        Assert.True(hardware.SupportsDgpuDirect);
        Assert.True(hardware.CanOfferIgpuOnly);
        Assert.True(hardware.CanOfferGpuModeSwitch);
    }

    /// <summary>本机这一类：50 系 + MUX + 热切换（GpuConfig 两键 + 核显模式 + NVIDIA）。</summary>
    [Fact]
    public void Gen50HotSwapMachineOffersTheHotSwapLayout()
    {
        using var generation = GpuCapabilityGatingHarness.Generation(GpuCapabilityGatingHarness.Gen50);
        using MechrevoHw hardware = GpuCapabilityGatingHarness.Hardware(new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            IgpuOnly = true,
            DgpuDirect = true,
            GpuHotSwap = true,
            NvidiaGpu = true,
        });
        hardware.SetIgpuOnlyStatusSupportForTests(true);

        Assert.True(hardware.SupportsIgpuOnly);
        Assert.True(hardware.CanOfferIgpuOnly);
        Assert.True(hardware.CanOfferGpuHotSwap);
        Assert.True(hardware.CanOfferGpuModeSwitch);
        // 官方热切换机型隐藏 NVRAM 的「核显」按钮：集显只走热切换。
        Assert.False(hardware.CanOfferIgpuMuxTarget);
        Assert.Equal(GpuRowLayout.HotSwap, hardware.GpuRowLayout);
    }

    /// <summary>50 系非热切换机型：集显是 NVRAM 目标（TOGGLE_IGPU + 重启），没有热切换。</summary>
    [Fact]
    public void Gen50WithoutHotSwapOffersTheThreeTargetMuxLayout()
    {
        using var generation = GpuCapabilityGatingHarness.Generation(GpuCapabilityGatingHarness.Gen50);
        using MechrevoHw hardware = GpuCapabilityGatingHarness.Hardware(new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            IgpuOnly = true,
            DgpuDirect = true,
            NvidiaGpu = true,
        });

        Assert.False(hardware.CanOfferGpuHotSwap);
        Assert.True(hardware.CanOfferIgpuMuxTarget);
        Assert.Equal(GpuRowLayout.Mux3, hardware.GpuRowLayout);
    }

    [Fact]
    public void AnUnknownGenerationIsNotOfferedAsIfItWereAResolvedConsole()
    {
        using var generation = GpuCapabilityGatingHarness.Generation(DgpuIdentity.Unknown);
        using MechrevoHw hardware = GpuCapabilityGatingHarness.Hardware(new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            IgpuOnly = true,
            DgpuDirect = true,
            GpuHotSwap = true,
        });
        hardware.SetIgpuOnlyStatusSupportForTests(true);

        Assert.False(hardware.CanOfferIgpuOnly);
        Assert.False(hardware.CanOfferGpuHotSwap);
        Assert.False(hardware.CanOfferGpuModeSwitch);
    }

    [Fact]
    public void ARegistryIgpuOnlyBitWithoutMqttStatusOffersIgpuOnly()
    {
        using var generation = GpuCapabilityGatingHarness.Generation(GpuCapabilityGatingHarness.Gen40);
        using MechrevoHw hardware = GpuCapabilityGatingHarness.Hardware(new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            IgpuOnly = true,
            DgpuDirect = true,
        });

        Assert.True(hardware.SupportsIgpuOnly);
        Assert.True(hardware.CanOfferIgpuOnly);
        Assert.True(hardware.CanOfferGpuModeSwitch);
    }
}

/// <summary>代际注入 + 硬件构造的共享接缝（仅限 <see cref="SerialGpuSwitchCollection"/> 串行使用）。</summary>
internal static class GpuCapabilityGatingHarness
{
    internal static readonly DgpuIdentity Gen30 =
        new(DgpuGenerationKind.Gen30, DgpuProbeSource.MarketingName, true, "RTX 3050", "25A2");
    internal static readonly DgpuIdentity Gen40 =
        new(DgpuGenerationKind.Gen40, DgpuProbeSource.MarketingName, true, "RTX 4060", "2882");
    internal static readonly DgpuIdentity Gen50 =
        new(DgpuGenerationKind.Gen50, DgpuProbeSource.MarketingName, true, "RTX 5090", "2C02");

    internal static IDisposable Generation(DgpuIdentity identity)
    {
        Func<DgpuIdentity>? previous = GpuGenerationProvider.Override;
        GpuGenerationProvider.Override = () => identity;
        return new Restore(previous);
    }

    internal static MechrevoHw Hardware(MechrevoDeviceCapabilities capabilities) =>
        new((_, _) => Task.CompletedTask, capabilities);

    sealed class Restore(Func<DgpuIdentity>? previous) : IDisposable
    {
        public void Dispose() => GpuGenerationProvider.Override = previous;
    }
}
