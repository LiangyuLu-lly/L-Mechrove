using MechrevoLite.Gpu;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T17 失败/边界路径：代际判不出时**不得**误伤已有路由；空路由必须 fail-closed；
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

    [Fact]
    public void AnUnknownGenerationDoesNotBlockTheKnownRoute()
    {
        IReadOnlyList<GpuRouteCommand> commands =
            GpuRouteCommandLayer.BuildRestartCommands(MechrevoService.GpuStandard, true, DgpuGenerationKind.Unknown);

        Assert.Equal(
            new[] { "DGPU_DIRECT_CONNECT_TOGGLE_OFF", "DGPU_DIRECT_CONNECT_RESTART" },
            commands.Select(command => command.Action).ToArray());
    }

    [Fact]
    public void NoDgpuDoesNotBorrowGen30Restrictions()
    {
        IReadOnlyList<GpuRouteCommand> commands =
            GpuRouteCommandLayer.BuildRestartCommands(MechrevoService.GpuIGpu, true, DgpuGenerationKind.NoDgpu);

        Assert.Equal(
            new[] { "DGPU_DIRECT_CONNECT_TOGGLE_IGPU", "DGPU_DIRECT_CONNECT_RESTART" },
            commands.Select(command => command.Action).ToArray());
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
            GpuRouteCommandLayer.BuildRestartCommands(MechrevoService.GpuStandard, true, DgpuGenerationKind.Gen40);
        GpuRouteCommand restart = commands[^1];

        Assert.Equal("DGPU_DIRECT_CONNECT_RESTART", restart.Action);
        Assert.Equal("Setting/Control", restart.Topic);
        Assert.Single(restart.Payload);
        Assert.Equal("DGPU_DIRECT_CONNECT_RESTART", restart.Payload["Action"]);
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
                await service.RequestGpuModeRestartOutcomeAsync(MechrevoService.GpuIGpu);

            Assert.Equal(GpuRestartRequestOutcome.Failed, outcome);
        }
        finally
        {
            GpuGenerationProvider.Override = null;
        }
    }

    [Fact]
    public async Task AnEmptyRouteIsUnsupportedAndPublishesNothing()
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        try
        {
            GpuGenerationProvider.Override = () => new DgpuIdentity(
                DgpuGenerationKind.Gen30, DgpuProbeSource.MarketingName, true, "RTX 3050", "25A2");
            using MechrevoHw hardware = NewHardware(published,
                new MechrevoDeviceCapabilities { ProfileAvailable = true, DgpuDirect = true });
            var service = new MechrevoService(hardware);

            GpuRestartRequestOutcome outcome =
                await service.RequestGpuModeRestartOutcomeAsync(MechrevoService.GpuStandard);

            Assert.Equal(GpuRestartRequestOutcome.Unsupported, outcome);
            Assert.Empty(published);
        }
        finally
        {
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
}
