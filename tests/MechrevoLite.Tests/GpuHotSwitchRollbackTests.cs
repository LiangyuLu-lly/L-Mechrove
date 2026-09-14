using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 热切换会改写 MechrevoService.HotSwitchStatusPollLimit 这个可注入静态（仅测试用），
/// 与 SwitchConcurrencyTests 共享该接缝的测试必须串行。
/// </summary>
[CollectionDefinition(nameof(SerialGpuSwitchCollection), DisableParallelization = true)]
public sealed class SerialGpuSwitchCollection;

/// <summary>
/// 热切换（标准→核显）确认超时后的回滚。真机事故（2026-09-10）：切换到核显 122 秒
/// 确认超时后，服务层正确地判定了失败，但没有人回滚、没有人提示——GCU 寄存器停留
/// 在 RB_ON，界面按回显显示「已切到核显」，而下一次重启机器会以这个从未真正生效的
/// 模式启动。官方 IgpuOnlyOnCommand 的超时分支（CCUWinUI L53562-53580）发
/// IGPUONLYCONNECTIONSWITCH_STATUS 把开关退回切换前的值，这里逐项钉住该行为。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class GpuHotSwitchRollbackTests
{
    static MechrevoHw NewHardware(
        List<(string Topic, Dictionary<string, object> Payload)> published,
        MechrevoDeviceCapabilities capabilities,
        string statusEcho)
    {
        MechrevoHw hw = null!;
        hw = new MechrevoHw((topic, payload) =>
        {
            var dict = (Dictionary<string, object>)payload;
            published.Add((topic, dict));
            if (topic == "Setting/Control" && dict["Action"] as string == "GETSTATUS")
                hw.HandleMessage("Setting/Status", statusEcho);
            return Task.CompletedTask;
        }, capabilities, null);
        return hw;
    }

    const string StandardEcho = """{"IGpuOnlyConnectionSwitch_Status":"IGPU_ONLY_CONNECT_RB_OFF","DiscreteGpuDirectConnectionSwitch_Status":"DGPU_DIRECT_CONNECT_TOGGLE_OFF"}""";
    const string AutoEcho = """{"IGpuOnlyConnectionSwitch_Status":"IGPU_ONLY_CONNECT_RB_AUTO"}""";

    [Theory]
    [InlineData(MechrevoService.GpuStandard, 0)]   // 标准 → RB_OFF = 0
    [InlineData(MechrevoService.GpuAuto, 2)]       // 自动 → RB_AUTO = 2
    public async Task Rollback_SendsOfficialStatusActionWithPreSwitchValue(int preMode, int expectedStatus)
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        MechrevoHw hw = NewHardware(published,
            new MechrevoDeviceCapabilities { IgpuOnly = true },
            preMode == MechrevoService.GpuAuto ? AutoEcho : StandardEcho);
        var service = new MechrevoService(hw);

        await service.RollbackFailedHotSwitchAsync(preMode, CancellationToken.None);

        var rollback = published.Single(p => p.Payload["Action"] as string == "IGPUONLYCONNECTIONSWITCH_STATUS");
        Assert.Equal(expectedStatus, rollback.Payload["Status"]);
        Assert.Contains(published, p => p.Payload["Action"] as string == "GETSTATUS");
    }

    [Fact]
    public async Task SwitchGpuMode_HotSwitchTimeout_TriggersOfficialRollbackAndReportsFailure()
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        MechrevoHw hw = NewHardware(published,
            new MechrevoDeviceCapabilities { NvidiaGpu = true, GpuHotSwap = true, IgpuOnly = true, DgpuDirect = true },
            StandardEcho);
        var service = new MechrevoService(hw);

        hw.HandleMessage("Setting/Status", StandardEcho);
        Assert.Equal(MechrevoService.GpuStandard, service.CurrentGpuMode);

        int originalLimit = MechrevoService.HotSwitchStatusPollLimit;
        MechrevoService.HotSwitchStatusPollLimit = 1;   // 把 122 秒的确认轮询压到 1 次
        try
        {
            bool confirmed = await service.SwitchGpuMode(MechrevoService.GpuIGpu);

            Assert.False(confirmed);   // 运行时结果从未到 2：切换必须被判失败
        }
        finally
        {
            MechrevoService.HotSwitchStatusPollLimit = originalLimit;
        }

        Assert.Contains(published, p => p.Payload["Action"] as string == "IGPU_ONLY_CONNECT_RB_ON");
        Assert.Contains(published, p => p.Payload["Action"] as string == "IGPUONLYCONNECTIONSWITCH_STATUS");
    }

    /// <summary>
    /// 手动路径（keepRegisterOnHotSwitchTimeout=true）超时后必须保留寄存器：
    /// 回滚决定权在 UI 的「重启生效」询问，服务层不能抢先把 RB_ON 洗掉，
    /// 否则「重启生效」这条 100% 兜底路径就断了。
    /// </summary>
    [Fact]
    public async Task SwitchGpuMode_KeepRegisterOnTimeout_DoesNotRollBack()
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        MechrevoHw hw = NewHardware(published,
            new MechrevoDeviceCapabilities { NvidiaGpu = true, GpuHotSwap = true, IgpuOnly = true, DgpuDirect = true },
            StandardEcho);
        var service = new MechrevoService(hw);

        hw.HandleMessage("Setting/Status", StandardEcho);
        Assert.Equal(MechrevoService.GpuStandard, service.CurrentGpuMode);

        int originalLimit = MechrevoService.HotSwitchStatusPollLimit;
        MechrevoService.HotSwitchStatusPollLimit = 1;
        try
        {
            bool confirmed = await service.SwitchGpuMode(
                MechrevoService.GpuIGpu, keepRegisterOnHotSwitchTimeout: true);

            Assert.False(confirmed);
        }
        finally
        {
            MechrevoService.HotSwitchStatusPollLimit = originalLimit;
        }

        Assert.Contains(published, p => p.Payload["Action"] as string == "IGPU_ONLY_CONNECT_RB_ON");
        Assert.DoesNotContain(published, p => p.Payload["Action"] as string == "IGPUONLYCONNECTIONSWITCH_STATUS");
    }
}
