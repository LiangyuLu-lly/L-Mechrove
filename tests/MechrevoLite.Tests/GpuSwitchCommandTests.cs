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

    static GpuRouteContext Context(DgpuGenerationKind generation,
        GcuServiceTier tier = GcuServiceTier.Modern12, bool threeMode = true, bool hotSwap = false,
        bool igpuMuxTarget = true) =>
        new(generation, tier, SupportsDgpuDirect: true, threeMode, hotSwap, igpuMuxTarget);

    [Fact]
    public void EveryDisplayRouteCommandGoesOutOnSettingControl()
    {
        IReadOnlyList<GpuRouteCommand> commands =
            GpuRouteCommandLayer.BuildRestartCommands(MechrevoService.GpuDgpu, Context(DgpuGenerationKind.Gen40));

        Assert.NotEmpty(commands);
        Assert.All(commands, command => Assert.Equal("Setting/Control", command.Topic));
    }

    [Fact]
    public void RestartRouteMatchesTheVendorSequenceAndDelaysOnlyBeforeRestart()
    {
        IReadOnlyList<GpuRouteCommand> commands =
            GpuRouteCommandLayer.BuildRestartCommands(MechrevoService.GpuIGpu, Context(DgpuGenerationKind.Gen40));

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
            GpuRouteCommandLayer.BuildRestartCommands(MechrevoService.GpuDgpu, Context(DgpuGenerationKind.Gen50));

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

    /// <summary>30/40 系进入直连只发 TOGGLE_ON：三连里的 RB_OFF 是 50 系控制台自己的写法。</summary>
    [Theory]
    [InlineData(DgpuGenerationKind.Gen30)]
    [InlineData(DgpuGenerationKind.Gen40)]
    public void EnteringDirectOnThirtyAndFortyIsASingleToggle(DgpuGenerationKind generation)
    {
        IReadOnlyList<GpuRouteCommand> commands =
            GpuRouteCommandLayer.BuildRestartCommands(MechrevoService.GpuDgpu, Context(generation));

        Assert.Equal(new[] { "DGPU_DIRECT_CONNECT_TOGGLE_ON", "DGPU_DIRECT_CONNECT_RESTART" },
            commands.Select(command => command.Action).ToArray());
    }

    [Fact]
    public void TheHotSwitchPayloadsCarrySetToWmiecOk()
    {
        GpuRouteContext hotSwap = Context(DgpuGenerationKind.Gen50, hotSwap: true, igpuMuxTarget: false);
        GpuRouteCommand on = GpuRouteCommandLayer.BuildHotSwitchCommand(MechrevoService.GpuIGpu, hotSwap)!;
        GpuRouteCommand off = GpuRouteCommandLayer.BuildHotSwitchCommand(MechrevoService.GpuStandard, hotSwap)!;

        Assert.Equal("IGPU_ONLY_CONNECT_RB_ON", on.Action);
        Assert.Equal("OK", on.Payload["SetToWMIEC"]);
        Assert.Equal("IGPU_ONLY_CONNECT_RB_OFF", off.Action);
        Assert.Equal("OK", off.Payload["SetToWMIEC"]);
    }

    /// <summary>30 系：TOGGLE_ON/OFF + 服务重启（我方 1.2 服务）；集显方向与自动方向没有路由。</summary>
    [Fact]
    public void Gen30HasDirectAndHybridRoutesButNoIgpuRoute()
    {
        GpuRouteContext gen30 = Context(DgpuGenerationKind.Gen30, threeMode: false, igpuMuxTarget: false);
        Assert.Equal(new[] { "DGPU_DIRECT_CONNECT_TOGGLE_OFF", "DGPU_DIRECT_CONNECT_RESTART" },
            GpuRouteCommandLayer.BuildRestartCommands(MechrevoService.GpuStandard, gen30).Select(c => c.Action).ToArray());
        Assert.Empty(GpuRouteCommandLayer.BuildRestartCommands(MechrevoService.GpuIGpu, gen30));
        Assert.Empty(GpuRouteCommandLayer.BuildRestartCommands(MechrevoService.GpuAuto, gen30));
    }

    [Fact]
    public void OnlyTheFiftySeriesHotSwapMachineGetsTheHotSwitchAction()
    {
        Assert.Null(GpuRouteCommandLayer.BuildHotSwitchCommand(
            MechrevoService.GpuIGpu, Context(DgpuGenerationKind.Gen30, hotSwap: true)));
        Assert.Null(GpuRouteCommandLayer.BuildHotSwitchCommand(
            MechrevoService.GpuIGpu, Context(DgpuGenerationKind.Gen40, hotSwap: true)));
        Assert.Null(GpuRouteCommandLayer.BuildHotSwitchCommand(
            MechrevoService.GpuIGpu, Context(DgpuGenerationKind.Gen50, hotSwap: false)));
        Assert.Null(GpuRouteCommandLayer.BuildHotSwitchCommand(
            MechrevoService.GpuIGpu, Context(DgpuGenerationKind.Gen50, GcuServiceTier.Foreign, hotSwap: true)));
        Assert.NotNull(GpuRouteCommandLayer.BuildHotSwitchCommand(
            MechrevoService.GpuIGpu, Context(DgpuGenerationKind.Gen50, hotSwap: true)));
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
                new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true, DgpuDirect = true });
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

    /// <summary>同一请求在 Gen40 三模档发完整路由并在最后发 RESTART——换代际确实改变被门控行为。</summary>
    [Fact]
    public async Task E2EWiring_Gen40GenerationStillEmitsTheFullRestartRoute()
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        try
        {
            GpuGenerationProvider.Override = () => new DgpuIdentity(
                DgpuGenerationKind.Gen40, DgpuProbeSource.MarketingName, true, "RTX 4060", "2882");
            using MechrevoHw hardware = NewHardware(published,
                new MechrevoDeviceCapabilities { ProfileAvailable = true, DgpuDirect = true, IgpuOnly = true });
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

    /// <summary>热切换入口同一道门：Gen30 上 refused，且不发任何 iGPU-only 载荷。</summary>
    [Fact]
    public async Task E2EWiring_Gen30HotSwitchRefusesTheIgpuOnlyAction()
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        try
        {
            GpuGenerationProvider.Override = () => new DgpuIdentity(
                DgpuGenerationKind.Gen30, DgpuProbeSource.MarketingName, true, "RTX 3050", "25A2");
            using MechrevoHw hardware = NewHardware(published,
                new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true, DgpuDirect = true, GpuHotSwap = true, NvidiaGpu = true });
            hardware.SetIgpuOnlyStatusSupportForTests(true);
            var service = new MechrevoService(hardware);

            bool ok = await service.SwitchGpuMode(MechrevoService.GpuIGpu);

            Assert.False(ok);
            Assert.DoesNotContain(published, entry => entry.Payload["Action"] as string == "IGPU_ONLY_CONNECT_RB_ON");
        }
        finally
        {
            GpuGenerationProvider.Override = null;
        }
    }

    /// <summary>
    /// 本机回归：50 系热切换机型在混合模式下（寄存器 RB_OFF）点集显 → RB_ON，
    /// 服务回报成功 + NVIDIA 设备离开总线 → 确认。
    /// </summary>
    [Fact]
    public async Task E2EWiring_Gen50HotSwapMachineSwitchesToIgpuOnlyWhenTheDeviceLeaves()
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        MechrevoHw hardware = null!;
        hardware = new MechrevoHw((topic, payload) =>
        {
            var dict = (Dictionary<string, object>)payload;
            published.Add((topic, dict));
            if (dict["Action"] as string == "GETSTATUS" &&
                published.Any(entry => entry.Payload["Action"] as string == "IGPU_ONLY_CONNECT_RB_ON"))
                hardware.HandleMessage("Setting/Status",
                    """{"DiscreteGpuDirectConnectionSwitch_Status":"DGPU_DIRECT_CONNECT_TOGGLE_OFF","IGpuOnlyConnectionSwitch_Status":"IGPU_ONLY_CONNECT_RB_ON","CheckDGpuStatusforIGpuOnlyOnSuccess":"2"}""");
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true, DgpuDirect = true, GpuHotSwap = true, NvidiaGpu = true });
        using (hardware)
        using (TestGpuRoute.Use(() =>
            published.Any(entry => entry.Payload["Action"] as string == "IGPU_ONLY_CONNECT_RB_ON")
                ? TestGpuRoute.IgpuOnly
                : TestGpuRoute.Hybrid))
        {
            hardware.HandleMessage("Setting/Status",
                """{"DiscreteGpuDirectConnectionSwitch_Status":"DGPU_DIRECT_CONNECT_TOGGLE_OFF","IGpuOnlyConnectionSwitch_Status":"IGPU_ONLY_CONNECT_RB_OFF","CheckDGpuStatusforIGpuOnlyOnSuccess":"1"}""");
            var service = new MechrevoService(hardware);

            Assert.Equal(GpuRowLayout.HotSwap, hardware.GpuRowLayout);
            Assert.True(await service.SwitchGpuMode(MechrevoService.GpuIGpu));
            Assert.Equal(MechrevoService.GpuIGpu, hardware.IgpuOnlyRegister);
            Assert.DoesNotContain(published, entry => entry.Payload["Action"] as string == "DGPU_DIRECT_CONNECT_RESTART");
        }
    }
}
