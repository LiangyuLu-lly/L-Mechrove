using MechrevoLite.Gpu;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T17（Wave D）happy 路径：逐代显示路由**命令层**——MQTT-only 的主题/动作/载荷，
/// 重启前置 800 ms，动作集按 T16 的逐代事实表收窄（30 系无 iGPU-only/RESTART）。
///
/// <para>端到端接线（C5）：代际数据必须真的到达服务边界并改变被门控行为。失败路径见
/// <see cref="GpuSwitchCommandFailTests"/>。</para>
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class GpuSwitchCommandTests
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
    public void EveryDisplayRouteCommandGoesOutOnSettingControl()
    {
        IReadOnlyList<GpuRouteCommand> commands =
            GpuRouteCommandLayer.BuildRestartCommands(MechrevoService.GpuDgpu, true, DgpuGenerationKind.Gen40);

        Assert.NotEmpty(commands);
        Assert.All(commands, command => Assert.Equal("Setting/Control", command.Topic));
    }

    [Fact]
    public void RestartRouteMatchesTheVendorSequenceAndDelaysOnlyBeforeRestart()
    {
        IReadOnlyList<GpuRouteCommand> commands =
            GpuRouteCommandLayer.BuildRestartCommands(MechrevoService.GpuIGpu, true, DgpuGenerationKind.Gen40);

        Assert.Equal(
            new[] { "DGPU_DIRECT_CONNECT_TOGGLE_IGPU", "DGPU_DIRECT_CONNECT_RESTART" },
            commands.Select(command => command.Action).ToArray());
        Assert.All(commands.Take(commands.Count - 1), command => Assert.Equal(0, command.DelayBeforeMilliseconds));
        Assert.Equal(800, commands[^1].DelayBeforeMilliseconds);
    }

    [Fact]
    public void EnteringDirectKeepsTheOfficialThreeActionRouteThenRestart()
    {
        IReadOnlyList<GpuRouteCommand> commands =
            GpuRouteCommandLayer.BuildRestartCommands(MechrevoService.GpuDgpu, true, DgpuGenerationKind.Gen50);

        Assert.Equal(
            new[]
            {
                "DGPU_DIRECT_CONNECT_TOGGLE_ON",
                "IGPU_ONLY_CONNECT_RB_OFF",
                "DGPU_DIRECT_CONNECT_TOGGLE_ON",
                "DGPU_DIRECT_CONNECT_RESTART",
            },
            commands.Select(command => command.Action).ToArray());
    }

    [Fact]
    public void TheIgpuOnlySwitchPayloadsCarrySetToWmiecOk()
    {
        GpuRouteCommand on = GpuRouteCommandLayer.BuildSwitchCommand(
            MechrevoService.GpuIGpu, false, true, false, DgpuGenerationKind.Gen40)!;
        GpuRouteCommand off = GpuRouteCommandLayer.BuildSwitchCommand(
            MechrevoService.GpuStandard, false, true, false, DgpuGenerationKind.Gen40)!;

        Assert.Equal("IGPU_ONLY_CONNECT_RB_ON", on.Action);
        Assert.Equal("OK", on.Payload["SetToWMIEC"]);
        Assert.Equal("IGPU_ONLY_CONNECT_RB_OFF", off.Action);
        Assert.Equal("OK", off.Payload["SetToWMIEC"]);
    }

    [Fact]
    public void Gen30HasNoRestartRouteAtAllBecauseRestartIsProvenAbsent()
    {
        // 30 系载荷里 IGPU_ONLY_* 与 *_RESTART 都是 0 命中（T16 事实表：ProvenAbsent）。
        // 重启路由的契约是"应用后重启"，缺 RESTART 就等于没有可用路由 -> fail closed。
        Assert.Empty(GpuRouteCommandLayer.BuildRestartCommands(MechrevoService.GpuStandard, true, DgpuGenerationKind.Gen30));
        Assert.Empty(GpuRouteCommandLayer.BuildRestartCommands(MechrevoService.GpuIGpu, true, DgpuGenerationKind.Gen30));
        Assert.Empty(GpuRouteCommandLayer.BuildRestartCommands(MechrevoService.GpuAuto, true, DgpuGenerationKind.Gen30));
    }

    [Fact]
    public void Gen30RefusesTheIgpuOnlySwitchActionOutright()
    {
        Assert.Null(GpuRouteCommandLayer.BuildSwitchCommand(
            MechrevoService.GpuIGpu, false, true, false, DgpuGenerationKind.Gen30));
        // 同一请求在 40 系是被允许的（负面对照）。
        Assert.NotNull(GpuRouteCommandLayer.BuildSwitchCommand(
            MechrevoService.GpuIGpu, false, true, false, DgpuGenerationKind.Gen40));
    }

    /// <summary>
    /// C5 端到端接线：Gen30 代际在**服务边界**把 iGPU-only 路由清空，结果是 fail-closed
    /// （Unsupported），一个字节都不发——不是只断言纯函数返回值。
    /// </summary>
    [Fact]
    public async Task E2EWiring_Gen30GenerationStopsTheIgpuOnlyRouteAtTheServiceBoundary()
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        try
        {
            GpuGenerationProvider.Override = () => new DgpuIdentity(
                DgpuGenerationKind.Gen30, DgpuProbeSource.MarketingName, true, "RTX 3050", "25A2");
            using MechrevoHw hardware = NewHardware(published,
                new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true });
            hardware.SetIgpuOnlyStatusSupportForTests(true);
            var service = new MechrevoService(hardware);

            GpuRestartRequestOutcome outcome =
                await service.RequestGpuModeRestartOutcomeAsync(MechrevoService.GpuIGpu);

            Assert.Equal(GpuRestartRequestOutcome.Unsupported, outcome);
            Assert.DoesNotContain(published, entry => entry.Payload["Action"] as string == "IGPU_ONLY_CONNECT_RB_ON");
            Assert.DoesNotContain(published, entry => entry.Payload["Action"] as string == "DGPU_DIRECT_CONNECT_RESTART");
        }
        finally
        {
            GpuGenerationProvider.Override = null;
        }
    }

    /// <summary>同一请求在 Gen40 仍然发完整路由并在最后发 RESTART——换代际确实改变被门控行为。</summary>
    [Fact]
    public async Task E2EWiring_Gen40GenerationStillEmitsTheFullRestartRoute()
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        try
        {
            GpuGenerationProvider.Override = () => new DgpuIdentity(
                DgpuGenerationKind.Gen40, DgpuProbeSource.MarketingName, true, "RTX 4060", "2882");
            using MechrevoHw hardware = NewHardware(published,
                new MechrevoDeviceCapabilities { ProfileAvailable = true, DgpuDirect = true });
            var service = new MechrevoService(hardware);

            GpuRestartRequestOutcome outcome =
                await service.RequestGpuModeRestartOutcomeAsync(MechrevoService.GpuIGpu);

            Assert.Equal(GpuRestartRequestOutcome.Requested, outcome);
            Assert.Contains(published, entry => entry.Payload["Action"] as string == "DGPU_DIRECT_CONNECT_TOGGLE_IGPU");
            Assert.Equal("DGPU_DIRECT_CONNECT_RESTART", published[^1].Payload["Action"] as string);
        }
        finally
        {
            GpuGenerationProvider.Override = null;
        }
    }

    /// <summary>SetGpuMode 同一道门：Gen30 上 refused，且不发任何 iGPU-only 载荷。</summary>
    [Fact]
    public async Task E2EWiring_Gen30SetGpuModeRefusesTheIgpuOnlyAction()
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        try
        {
            GpuGenerationProvider.Override = () => new DgpuIdentity(
                DgpuGenerationKind.Gen30, DgpuProbeSource.MarketingName, true, "RTX 3050", "25A2");
            using MechrevoHw hardware = NewHardware(published,
                new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true });
            hardware.SetIgpuOnlyStatusSupportForTests(true);

            bool ok = await hardware.SetGpuMode(MechrevoService.GpuIGpu);

            Assert.False(ok);
            Assert.DoesNotContain(published, entry => entry.Payload["Action"] as string == "IGPU_ONLY_CONNECT_RB_ON");
        }
        finally
        {
            GpuGenerationProvider.Override = null;
        }
    }
}
