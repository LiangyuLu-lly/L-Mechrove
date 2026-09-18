using MechrevoLite.Gpu;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T18 失败/边界路径：越界的重试节奏与成功判据一律拒绝；热切换只有在**新鲜的**结果为 2 时
/// 才算成功，否则超时回滚。happy 路径见 <see cref="IgpuOnlyRetryTests"/>。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class IgpuOnlyRetryFailTests
{
    const string PreSwitchEcho =
        """{"IGpuOnlyConnectionSwitch_Status":"IGPU_ONLY_CONNECT_RB_OFF","CheckDGpuStatusforIGpuOnlyOnSuccess":"1"}""";
    const string StaleResultEcho =
        """{"IGpuOnlyConnectionSwitch_Status":"IGPU_ONLY_CONNECT_RB_ON","CheckDGpuStatusforIGpuOnlyOnSuccess":"1"}""";

    [Theory]
    [InlineData(-1)]
    [InlineData(-4)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void ANonFourthOrNegativePollNeverResends(int poll)
    {
        Assert.False(IgpuOnlySemantics.ShouldResend(poll));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(3, false)]
    public void AnOutOfDomainResultIsNeverASuccess(int reported, bool turningOn)
    {
        Assert.False(IgpuOnlySemantics.IsSuccess(reported, turningOn));
    }

    [Fact]
    public void AnUnknownPreSwitchModeFallsBackToTheOffRollbackStatus()
    {
        Assert.Equal(0, IgpuOnlySemantics.RollbackStatus(-1));
        Assert.Equal(0, IgpuOnlySemantics.RollbackStatus(99));
    }

    [Fact]
    public async Task AStaleResultOfOneNeverConfirmsTheOnSwitchAndRollsBack()
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        MechrevoHw hw = null!;
        hw = new MechrevoHw((topic, payload) =>
        {
            var dict = (Dictionary<string, object>)payload;
            published.Add((topic, dict));
            if (dict["Action"] as string == "GETSTATUS") hw.HandleMessage("Setting/Status", StaleResultEcho);
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { NvidiaGpu = true, GpuHotSwap = true, IgpuOnly = true, DgpuDirect = true });
        using (hw)
        {
            hw.HandleMessage("Setting/Status", PreSwitchEcho);
            var service = new MechrevoService(hw);
            Assert.Equal(MechrevoService.GpuStandard, service.CurrentGpuMode);

            int originalLimit = MechrevoService.HotSwitchStatusPollLimit;
            MechrevoService.HotSwitchStatusPollLimit = 1;
            try
            {
                bool confirmed = await service.SwitchGpuMode(MechrevoService.GpuIGpu);

                // 结果字段是 1（OFF 的成功值），不是 ON 的 2：绝不算成功。
                Assert.False(confirmed);
            }
            finally
            {
                MechrevoService.HotSwitchStatusPollLimit = originalLimit;
            }

            Assert.Contains(published, entry => entry.Payload["Action"] as string == "IGPUONLYCONNECTIONSWITCH_STATUS");
        }
    }

    [Fact]
    public void TheAutoRuntimeIsNeverAnythingButOneOrTwo()
    {
        Assert.Equal(1, IgpuOnlySemantics.AutomaticRuntime(true));
        Assert.Equal(2, IgpuOnlySemantics.AutomaticRuntime(false));
        Assert.NotEqual(1, IgpuOnlySemantics.AutomaticRuntime(false));
    }
}
