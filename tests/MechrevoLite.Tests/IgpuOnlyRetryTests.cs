using MechrevoLite.Gpu;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T18（Wave D）happy 路径：iGPU-only 重试/回滚语义逐项对齐厂商
/// （CCUWinUI <c>53527-53729</c>）：2 s 间隔、count&gt;60 放弃、每第 4 次额外重发、
/// 成功判据 ON=2 / OFF=1、AUTO 依赖 AC、超时回滚携带切换前开关值。
///
/// 失败路径见 <see cref="IgpuOnlyRetryFailTests"/>。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class IgpuOnlyRetryTests
{
    const string StandardEcho =
        """{"IGpuOnlyConnectionSwitch_Status":"IGPU_ONLY_CONNECT_RB_OFF","CheckDGpuStatusforIGpuOnlyOnSuccess":"1"}""";
    const string HotSwitchSuccessEcho =
        """{"IGpuOnlyConnectionSwitch_Status":"IGPU_ONLY_CONNECT_RB_ON","CheckDGpuStatusforIGpuOnlyOnSuccess":"2"}""";

    static MechrevoHw NewHardware(
        List<(string Topic, Dictionary<string, object> Payload)> published,
        MechrevoDeviceCapabilities capabilities,
        Func<string, string?> echoFor)
    {
        MechrevoHw hw = null!;
        hw = new MechrevoHw((topic, payload) =>
        {
            var dict = (Dictionary<string, object>)payload;
            published.Add((topic, dict));
            if (echoFor(dict["Action"] as string ?? "") is { } echo)
                hw.HandleMessage("Setting/Status", echo);
            return Task.CompletedTask;
        }, capabilities);
        return hw;
    }

    [Fact]
    public void TheVendorConstantsMatchTheConsole()
    {
        Assert.Equal(2000, IgpuOnlySemantics.PollIntervalMilliseconds);
        Assert.Equal(61, IgpuOnlySemantics.PollLimit);       // count > 60
        Assert.Equal(4, IgpuOnlySemantics.RetryEveryPolls);  // count % 4 == 0
        Assert.Equal(2, IgpuOnlySemantics.SuccessOn);
        Assert.Equal(1, IgpuOnlySemantics.SuccessOff);
    }

    [Fact]
    public void TheServiceUsesTheSemanticsAsItsSingleSource()
    {
        Assert.Equal(IgpuOnlySemantics.PollLimit, MechrevoService.HotSwitchStatusPollLimit);
        Assert.Equal(IgpuOnlySemantics.RetryEveryPolls, MechrevoService.HotSwitchStatusRetryEveryPolls);
        Assert.True(MechrevoService.ShouldRetryHotSwitchPoll(0));
        Assert.True(MechrevoService.ShouldRetryHotSwitchPoll(4));
        Assert.False(MechrevoService.ShouldRetryHotSwitchPoll(1));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(5, false)]
    public void OnlyEveryFourthPollResendsTheTarget(int poll, bool expected)
    {
        Assert.Equal(expected, IgpuOnlySemantics.ShouldResend(poll));
    }

    [Theory]
    [InlineData(2, true, true)]
    [InlineData(1, true, false)]
    [InlineData(1, false, true)]
    [InlineData(2, false, false)]
    public void TheSuccessCriterionIsTwoWhenTurningOnAndOneWhenTurningOff(int reported, bool turningOn, bool expected)
    {
        Assert.Equal(expected, IgpuOnlySemantics.IsSuccess(reported, turningOn));
    }

    [Fact]
    public void AutomaticRuntimeDependsOnAc()
    {
        Assert.Equal(1, IgpuOnlySemantics.AutomaticRuntime(plugged: true));
        Assert.Equal(2, IgpuOnlySemantics.AutomaticRuntime(plugged: false));
    }

    [Theory]
    [InlineData(MechrevoService.GpuAuto, 2)]
    [InlineData(MechrevoService.GpuIGpu, 1)]
    [InlineData(MechrevoService.GpuStandard, 0)]
    [InlineData(MechrevoService.GpuDgpu, 0)]
    public void TheRollbackStatusIsThePreSwitchSwitchValue(int modeBeforeSwitch, int expected)
    {
        Assert.Equal(expected, IgpuOnlySemantics.RollbackStatus(modeBeforeSwitch));
    }

    [Fact]
    public async Task ConfirmedHotSwitchSucceedsOnAResultOfTwoAndTheDeviceLeaving()
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        using MechrevoHw hardware = NewHardware(published,
            new MechrevoDeviceCapabilities { NvidiaGpu = true, GpuHotSwap = true, IgpuOnly = true, DgpuDirect = true },
            action => action == "GETSTATUS" ? HotSwitchSuccessEcho : null);
        var service = new MechrevoService(hardware);
        hardware.HandleMessage("Setting/Status", StandardEcho);
        Assert.Equal(MechrevoService.GpuStandard, service.CurrentGpuMode);

        using var route = TestGpuRoute.Use(() =>
            published.Any(entry => entry.Payload["Action"] as string == "IGPU_ONLY_CONNECT_RB_ON")
                ? TestGpuRoute.IgpuOnly
                : TestGpuRoute.Hybrid);
        bool confirmed = await service.SwitchGpuMode(MechrevoService.GpuIGpu);

        Assert.True(confirmed);
        Assert.Contains(published, entry => entry.Payload["Action"] as string == "IGPU_ONLY_CONNECT_RB_ON");
        Assert.DoesNotContain(published, entry => entry.Payload["Action"] as string == "IGPUONLYCONNECTIONSWITCH_STATUS");
    }

    [Fact]
    public async Task TimedOutHotSwitchRollsBackToThePreSwitchValue()
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        using MechrevoHw hardware = NewHardware(published,
            new MechrevoDeviceCapabilities { NvidiaGpu = true, GpuHotSwap = true, IgpuOnly = true, DgpuDirect = true },
            _ => null);
        var service = new MechrevoService(hardware);
        hardware.HandleMessage("Setting/Status", StandardEcho);

        int originalLimit = MechrevoService.HotSwitchStatusPollLimit;
        MechrevoService.HotSwitchStatusPollLimit = 1;
        try
        {
            bool confirmed = await service.SwitchGpuMode(MechrevoService.GpuIGpu);
            Assert.False(confirmed);
        }
        finally
        {
            MechrevoService.HotSwitchStatusPollLimit = originalLimit;
        }

        var rollback = published.Single(entry => entry.Payload["Action"] as string == "IGPUONLYCONNECTIONSWITCH_STATUS");
        Assert.Equal(0, rollback.Payload["Status"]);   // 切换前 Standard -> RB_OFF = 0
        Assert.Contains(published, entry => entry.Payload["Action"] as string == "GETSTATUS");
    }

    /// <summary>自动档插电：期望运行态 1（混合），独显在位即确认（默认回读 = 混合）。</summary>
    [Fact]
    public async Task AutomaticSwitchUsesTheAcDependentRuntime()
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        using MechrevoHw hardware = NewHardware(published,
            new MechrevoDeviceCapabilities { IgpuOnly = true, DgpuDirect = true, GpuHotSwap = true, NvidiaGpu = true },
            action => action == "GETSTATUS"
                ? """{"IGpuOnlyConnectionSwitch_Status":"IGPU_ONLY_CONNECT_RB_AUTO","CheckDGpuStatusforIGpuOnlyOnSuccess":"1"}"""
                : null);
        var service = new MechrevoService(hardware);
        hardware.HandleMessage("Setting/Status",
            """{"IGpuOnlyConnectionSwitch_Status":"IGPU_ONLY_CONNECT_RB_OFF"}""");

        bool confirmed = await service.SwitchAutomaticGpuMode(plugged: true);

        Assert.True(confirmed);
        Assert.Contains(published, entry => entry.Payload["Action"] as string == "IGPU_ONLY_CONNECT_RB_AUTO");
    }

    /// <summary>自动档只在 50 系热切换机型上有：没有热切换的机器一条指令都不发。</summary>
    [Fact]
    public async Task AutomaticSwitchIsRefusedWithoutAHotSwapMachine()
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        using MechrevoHw hardware = NewHardware(published,
            new MechrevoDeviceCapabilities { IgpuOnly = true, DgpuDirect = true },
            _ => null);
        var service = new MechrevoService(hardware);

        Assert.False(await service.SwitchAutomaticGpuMode(plugged: true));
        Assert.Empty(published);
    }
}
