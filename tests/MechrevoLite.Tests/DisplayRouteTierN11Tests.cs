using MechrevoLite.Gpu;

namespace MechrevoLite.Tests;

/// <summary>
/// N11 (owner correction): the earlier conclusion that ControlCenter_5.17.49.19 lacking IGPU_ONLY_*
/// while 5.17.51.27 has it was "version evolution" is WRONG. They are consoles for two DIFFERENT
/// KINDS of 40-series machines: one WITH 双显三模 (hybrid / dGPU-direct / iGPU) and one WITHOUT.
/// That is why the owner supplied two 40-series consoles.
///
/// So "generation" alone is an insufficient axis: within the 40-series there are two capability
/// tiers, and the correct discriminator is the service-written ItemSupport capability bits, not a
/// hard-coded per-model table.
/// </summary>
public class DisplayRouteTierN11Tests
{
    [Fact]
    public void TheFortySeriesRowIsSplitIntoTwoCapabilityTiers()
    {
        IReadOnlyList<GenerationRouteFacts> rows = DisplayRouteMatrix.Rows;
        // 30 / 40-with-3-mode / 40-without-3-mode / 50.
        Assert.Contains(rows, r => r.Generation == DgpuGenerationKind.Gen30);
        Assert.Contains(rows, r => r.Generation == DgpuGenerationKind.Gen40);
        Assert.Contains(rows, r => r.Generation == DgpuGenerationKind.Gen50);
        Assert.True(rows.Count(r => r.Generation == DgpuGenerationKind.Gen40) >= 2,
            "the 40-series must have two rows: with and without 双显三模");
    }

    /// <summary>
    /// 三模档的核显是 NVRAM 目标（TOGGLE_IGPU + 重启）。IGPU_ONLY_CONNECT_RB_* 在 S40 只写
    /// MySetting\IGPUonly 注册表、不落硬件（死路径）——不在我方词汇内，事实格标 PROVEN_ABSENT。
    /// </summary>
    [Fact]
    public void TheThreeModeFortyTierCarriesTheIgpuMuxTargetButNotTheRegistryOnlyRbPath()
    {
        GenerationRouteFacts tier = DisplayRouteMatrix.FindTier(DgpuGenerationKind.Gen40, threeMode: true)!;
        Assert.Contains(DisplayRouteMatrix.ToggleIgpu, tier.Actions);
        Assert.DoesNotContain(DisplayRouteMatrix.IgpuOnlyOn, tier.Actions);
        Assert.DoesNotContain(DisplayRouteMatrix.IgpuOnlyOff, tier.Actions);
        Assert.Equal(EvidenceMark.ProvenAbsent, tier.IgpuOnly.Mark);
        Assert.Contains("注册表", tier.IgpuOnly.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNonThreeModeFortyTierHasOnlyTheDirectConnectPair()
    {
        GenerationRouteFacts tier = DisplayRouteMatrix.FindTier(DgpuGenerationKind.Gen40, threeMode: false)!;
        Assert.DoesNotContain(DisplayRouteMatrix.IgpuOnlyOn, tier.Actions);
        Assert.DoesNotContain(DisplayRouteMatrix.IgpuOnlyOff, tier.Actions);
        Assert.DoesNotContain(DisplayRouteMatrix.ToggleIgpu, tier.Actions);
        Assert.Equal(new[] { DisplayRouteMatrix.ToggleOn, DisplayRouteMatrix.ToggleOff }, tier.Actions);
    }

    [Fact]
    public void TheTierIsDecidedByTheServiceWrittenCapabilityNotByGeneration()
    {
        // The discriminator is the service-written ItemSupport bit, not a per-model table.
        Assert.True(DisplayRouteMatrix.TierFromCapability(igpuModeOnlySupport: 1));
        Assert.False(DisplayRouteMatrix.TierFromCapability(igpuModeOnlySupport: 0));
    }

    [Fact]
    public void TheMatrixNoLongerClaimsTheFortySplitIsVersionEvolution()
    {
        string source = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Gpu", "DisplayRouteMatrix.cs");
        // The old wording attributed the difference to a newer console version.
        Assert.DoesNotContain("版本演进", source, StringComparison.Ordinal);
        Assert.Contains("双显三模", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FortyNeverAllowsIgpuOnlyRbActionsOnEitherTier()
    {
        Assert.False(DisplayRoutePolicy.AllowsAction(
            DgpuGenerationKind.Gen40, DisplayRouteMatrix.IgpuOnlyOn, threeMode: false));
        Assert.False(DisplayRoutePolicy.AllowsAction(
            DgpuGenerationKind.Gen40, DisplayRouteMatrix.IgpuOnlyOff, threeMode: false));
        Assert.True(DisplayRoutePolicy.AllowsAction(
            DgpuGenerationKind.Gen40, DisplayRouteMatrix.ToggleOn, threeMode: false));
        Assert.False(DisplayRoutePolicy.AllowsAction(
            DgpuGenerationKind.Gen40, DisplayRouteMatrix.IgpuOnlyOn, threeMode: true));
        Assert.True(DisplayRoutePolicy.AllowsAction(
            DgpuGenerationKind.Gen40, DisplayRouteMatrix.ToggleIgpu, threeMode: true));
        Assert.False(DisplayRoutePolicy.AllowsAction(
            DgpuGenerationKind.Gen40, DisplayRouteMatrix.ToggleIgpu, threeMode: false));
    }
}
