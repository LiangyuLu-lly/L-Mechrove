using MechrevoLite.Gpu;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T17 失败/边界路径：代际判不出时不得发出任何代际路由；空路由必须 fail-closed；
/// 发布失败必须上抛为失败；厂商重试常量逐项对齐。happy 路径见 <see cref="GpuSwitchCommandTests"/>。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class GpuSwitchCommandFailTests
{
    static MechrevoHw NewHardware(
        List<(string Topic, Dictionary<string, object> Payload)> published,
        MechrevoDeviceCapabilities capabilities)
    {
        return new MechrevoHw((topic, payload) =>
        {
            published.Add((topic, (Dictionary<string, object>)payload));
            return Task.CompletedTask;
        }, capabilities);
    }

    static GpuRouteContext Context(DgpuGenerationKind generation,
        GcuServiceTier tier = GcuServiceTier.Modern12, bool mux = true, bool threeMode = true,
        bool hotSwap = true, bool igpuMuxTarget = true) =>
        new(generation, tier, mux, threeMode, hotSwap, igpuMuxTarget);

    [Fact]
    public void UnresolvedGenerationEmitsNoRestartOrSwitchCommand()
    {
        Assert.Empty(GpuRouteCommandLayer.BuildRestartCommands(
            MechrevoService.GpuStandard, Context(DgpuGenerationKind.Unknown)));
        Assert.Empty(GpuRouteCommandLayer.BuildRestartCommands(
            MechrevoService.GpuIGpu, Context(DgpuGenerationKind.NoDgpu)));
        Assert.Empty(MechrevoService.CreateGpuRestartRoute(
            MechrevoService.GpuStandard, Context(DgpuGenerationKind.Unknown)).Payloads);
        Assert.Empty(MechrevoService.CreateGpuRestartRoute(
            MechrevoService.GpuIGpu, Context(DgpuGenerationKind.NoDgpu)).Payloads);
        Assert.Null(GpuRouteCommandLayer.BuildHotSwitchCommand(
            MechrevoService.GpuIGpu, Context(DgpuGenerationKind.Unknown)));
        Assert.Null(GpuRouteCommandLayer.BuildHotSwitchCommand(
            MechrevoService.GpuStandard, Context(DgpuGenerationKind.NoDgpu)));
    }

    [Fact]
    public void AnUnknownServiceTierEmitsNothingEvenOnAResolvedGeneration()
    {
        Assert.Empty(GpuRouteCommandLayer.BuildRestartCommands(
            MechrevoService.GpuStandard, Context(DgpuGenerationKind.Gen40, GcuServiceTier.Unknown)));
        Assert.Empty(GpuRouteCommandLayer.BuildRestartCommands(
            MechrevoService.GpuDgpu, Context(DgpuGenerationKind.Gen50, GcuServiceTier.Unknown)));
        Assert.Null(GpuRouteCommandLayer.BuildHotSwitchCommand(
            MechrevoService.GpuIGpu, Context(DgpuGenerationKind.Gen50, GcuServiceTier.Unknown)));
    }

    [Fact]
    public void NoMuxEmitsNoRestartRoute()
    {
        Assert.Empty(GpuRouteCommandLayer.BuildRestartCommands(
            MechrevoService.GpuIGpu, Context(DgpuGenerationKind.Gen50, mux: false)));
        Assert.Empty(GpuRouteCommandLayer.BuildRestartCommands(
            MechrevoService.GpuStandard, Context(DgpuGenerationKind.Gen40, mux: false)));
    }

    [Theory]
    [InlineData(DgpuGenerationKind.Gen40, "SOMETHING_INVENTED")]
    [InlineData(DgpuGenerationKind.Gen50, "")]
    [InlineData(DgpuGenerationKind.Gen40, "GPU_HOTSWAP_ON")]
    public void AnActionOutsideTheGenerationsVocabularyIsRejected(DgpuGenerationKind generation, string action)
    {
        Assert.False(GpuRouteCommandLayer.IsAllowedByGeneration(generation, action));
    }

    [Fact]
    public void TheVendorRetryAndRestartConstantsMatchTheConsole()
    {
        Assert.Equal(800, GpuRouteCommandLayer.RestartDelayMilliseconds);
        Assert.Equal(2000, GpuRouteCommandLayer.RetryIntervalMilliseconds);
        Assert.Equal(4, GpuRouteCommandLayer.RetryEveryPolls);
    }

    [Fact]
    public void TheRestartCommandCarriesOnlyTheActionField()
    {
        IReadOnlyList<GpuRouteCommand> commands =
            GpuRouteCommandLayer.BuildRestartCommands(MechrevoService.GpuStandard, Context(DgpuGenerationKind.Gen40));
        GpuRouteCommand restart = commands[^1];

        Assert.Equal("DGPU_DIRECT_CONNECT_RESTART", restart.Action);
        Assert.Equal("Setting/Control", restart.Topic);
        Assert.Single(restart.Payload);
        Assert.Equal("DGPU_DIRECT_CONNECT_RESTART", restart.Payload["Action"]);
    }

    /// <summary>厂商服务（Foreign）：只发 TOGGLE_ON/OFF，绝不发服务的 RESTART——重启由我方发起。</summary>
    [Theory]
    [InlineData(MechrevoService.GpuDgpu, "DGPU_DIRECT_CONNECT_TOGGLE_ON")]
    [InlineData(MechrevoService.GpuStandard, "DGPU_DIRECT_CONNECT_TOGGLE_OFF")]
    public void AForeignServiceNeverGetsTheServiceRestart(int mode, string expected)
    {
        IReadOnlyList<GpuRouteCommand> commands = GpuRouteCommandLayer.BuildRestartCommands(
            mode, Context(DgpuGenerationKind.Gen40, GcuServiceTier.Foreign));

        Assert.Equal(new[] { expected }, commands.Select(command => command.Action).ToArray());
        Assert.Empty(GpuRouteCommandLayer.BuildRestartCommands(
            MechrevoService.GpuIGpu, Context(DgpuGenerationKind.Gen40, GcuServiceTier.Foreign)));
    }

    [Fact]
    public async Task APublishFailureSurfacesAsAFailedOutcome()
    {
        try
        {
            GpuGenerationProvider.Override = () => new DgpuIdentity(
                DgpuGenerationKind.Gen40, DgpuProbeSource.MarketingName, true, "RTX 4060", "2882");
            using var hardware = new MechrevoHw((_, _) => throw new IOException("broker down"),
                new MechrevoDeviceCapabilities { ProfileAvailable = true, DgpuDirect = true });
            var service = new MechrevoService(hardware);

            GpuRestartRequestOutcome outcome =
                await service.RequestGpuModeRestartOutcomeAsync(MechrevoService.GpuDgpu);

            Assert.Equal(GpuRestartRequestOutcome.Failed, outcome);
        }
        finally
        {
            GpuGenerationProvider.Override = null;
        }
    }

    /// <summary>30 系没有核显目标：集显方向是空路由，报 Unsupported，一条指令都不发。</summary>
    [Fact]
    public async Task AnEmptyRouteIsUnsupportedAndPublishesNothing()
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        try
        {
            GpuGenerationProvider.Override = () => new DgpuIdentity(
                DgpuGenerationKind.Gen30, DgpuProbeSource.MarketingName, true, "RTX 3050", "25A2");
            using MechrevoHw hardware = NewHardware(published,
                new MechrevoDeviceCapabilities { ProfileAvailable = true, DgpuDirect = true, IgpuOnly = true });
            var service = new MechrevoService(hardware);

            GpuRestartRequestOutcome outcome =
                await service.RequestGpuModeRestartOutcomeAsync(MechrevoService.GpuIGpu);

            Assert.Equal(GpuRestartRequestOutcome.Unsupported, outcome);
            Assert.Empty(published);
        }
        finally
        {
            GpuGenerationProvider.Override = null;
        }
    }

    /// <summary>厂商服务：目标照发，但不发 RESTART，结果是 RequiresAppRestart（界面在用户确认下重启 Windows）。</summary>
    [Fact]
    public async Task AForeignServiceRouteAsksTheAppToRestart()
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        Func<GcuServiceTier>? previousTier = GcuServiceTierProbe.Override;
        try
        {
            GpuGenerationProvider.Override = () => new DgpuIdentity(
                DgpuGenerationKind.Gen30, DgpuProbeSource.MarketingName, true, "RTX 3050", "25A2");
            GcuServiceTierProbe.Override = static () => GcuServiceTier.Foreign;
            using MechrevoHw hardware = NewHardware(published,
                new MechrevoDeviceCapabilities { ProfileAvailable = true, DgpuDirect = true });
            var service = new MechrevoService(hardware);

            GpuRestartRequestOutcome outcome =
                await service.RequestGpuModeRestartOutcomeAsync(MechrevoService.GpuDgpu);

            Assert.Equal(GpuRestartRequestOutcome.RequiresAppRestart, outcome);
            Assert.Equal(new[] { "DGPU_DIRECT_CONNECT_TOGGLE_ON" },
                published.Select(entry => entry.Payload["Action"] as string).ToArray());
        }
        finally
        {
            GcuServiceTierProbe.Override = previousTier;
            GpuGenerationProvider.Override = null;
        }
    }

    [Fact]
    public void ThePolicyIsTheSingleGateForGenerationBoundedActions()
    {
        // 30 系只保留 TOGGLE_ON/OFF；其余已知动作全部拒绝。
        Assert.True(GpuRouteCommandLayer.IsAllowedByGeneration(DgpuGenerationKind.Gen30, "DGPU_DIRECT_CONNECT_TOGGLE_ON"));
        Assert.True(GpuRouteCommandLayer.IsAllowedByGeneration(DgpuGenerationKind.Gen30, "DGPU_DIRECT_CONNECT_TOGGLE_OFF"));
        Assert.False(GpuRouteCommandLayer.IsAllowedByGeneration(DgpuGenerationKind.Gen30, "DGPU_DIRECT_CONNECT_RESTART"));
        Assert.False(GpuRouteCommandLayer.IsAllowedByGeneration(DgpuGenerationKind.Gen30, "IGPU_ONLY_CONNECT_RB_ON"));
        Assert.False(GpuRouteCommandLayer.IsAllowedByGeneration(DgpuGenerationKind.Gen30, "DGPU_DIRECT_CONNECT_TOGGLE_IGPU"));
    }

    /// <summary>40 两模档（5.17.49.19 服务只声明 TOGGLE_ON/OFF）：没有核显目标，也没有热切换。</summary>
    [Fact]
    public void FortyWithoutThreeModeCannotEmitIgpuCommands()
    {
        GpuRouteContext twoMode = Context(DgpuGenerationKind.Gen40, threeMode: false, igpuMuxTarget: false);
        Assert.Null(GpuRouteCommandLayer.BuildHotSwitchCommand(MechrevoService.GpuIGpu, twoMode));
        Assert.Empty(GpuRouteCommandLayer.BuildRestartCommands(MechrevoService.GpuIGpu, twoMode));
        Assert.False(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.Gen40, DisplayRouteMatrix.ToggleIgpu, threeMode: false));
    }

    /// <summary>40 三模档：RB_* 在 S40 只写注册表（死路径），任何档位都不放行；集显只走 TOGGLE_IGPU + 重启。</summary>
    [Fact]
    public void FortyThreeModeNeverEmitsTheRegistryOnlyRbPath()
    {
        GpuRouteContext threeMode = Context(DgpuGenerationKind.Gen40);
        Assert.Null(GpuRouteCommandLayer.BuildHotSwitchCommand(MechrevoService.GpuIGpu, threeMode));
        Assert.Null(GpuRouteCommandLayer.BuildHotSwitchCommand(MechrevoService.GpuStandard, threeMode));
        Assert.Equal(new[] { "DGPU_DIRECT_CONNECT_TOGGLE_IGPU", "DGPU_DIRECT_CONNECT_RESTART" },
            GpuRouteCommandLayer.BuildRestartCommands(MechrevoService.GpuIGpu, threeMode).Select(c => c.Action).ToArray());
        foreach (GcuServiceTier tier in Enum.GetValues<GcuServiceTier>())
        {
            Assert.False(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.Gen40, DisplayRouteMatrix.IgpuOnlyOn, true, tier, hotSwap: true));
            Assert.False(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.Gen40, DisplayRouteMatrix.IgpuOnlyOff, true, tier, hotSwap: true));
        }
    }
}
