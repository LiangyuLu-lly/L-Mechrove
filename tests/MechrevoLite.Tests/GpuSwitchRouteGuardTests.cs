using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 显卡重启切换路由的空序列守卫。
///
/// 真机证据（40 系，project IDY/IDC，两台都已处于独显直连 mode=2）：从独显直连切回
/// 核显/标准时，日志出现 `GPU restart route payloads [] sent for target=0/1`，但代码随后
/// 仍然发布 `DGPU_DIRECT_CONNECT_RESTART`，机器白重启一次、模式不变。
///
/// 这里锁定两条契约：
/// 1) 计算出的路由为空时，绝不发布重启指令，并向 UI 报告 Unsupported（据此给出诚实提示）；
/// 2) 完整路由时，目标写入必须齐全，且重启指令恰好出现一次、位于最后。
/// </summary>
public class GpuSwitchRouteGuardTests
{
    /// <summary>独显直连已落地（mode=2）+ 核显直通可用，与真机上报一致。</summary>
    const string DirectStatus =
        "{\"DiscreteGpuDirectConnectionSwitch_Status\":\"DGPU_DIRECT_CONNECT_TOGGLE_ON\"," +
        "\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_OFF\"}";

    static MechrevoHw NewHardware(List<string> actions) =>
        new((topic, payload) =>
        {
            if (topic == "Setting/Control" && payload is IDictionary<string, object> values &&
                values.TryGetValue("Action", out object? action))
                actions.Add(action?.ToString() ?? "");
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true, DgpuDirect = true });

    /// <summary>路由为空：必须放弃重启（不发布 DGPU_DIRECT_CONNECT_RESTART），并报告 Unsupported。</summary>
    [Fact]
    public async Task EmptyRestartRoute_NeverPublishesRestart_AndReportsUnsupported()
    {
        var actions = new List<string>();
        Func<int, bool, IReadOnlyList<Dictionary<string, object>>> empty =
            (_, _) => Array.Empty<Dictionary<string, object>>();
        try
        {
            MechrevoService.GpuRestartRouteOverride = empty;
            using MechrevoHw hardware = NewHardware(actions);
            hardware.HandleMessage("Setting/Status", DirectStatus);
            var service = new MechrevoService(hardware);

            GpuRestartRequestOutcome outcome =
                await service.RequestGpuModeRestartOutcomeAsync(MechrevoService.GpuIGpu);

            Assert.Equal(GpuRestartRequestOutcome.Unsupported, outcome);
            Assert.Empty(actions);
            Assert.False(await service.RequestGpuModeRestartAsync(MechrevoService.GpuIGpu));
        }
        finally { MechrevoService.GpuRestartRouteOverride = null; }
    }

    /// <summary>
    /// 自动重启路径（<see cref="MechrevoService.SwitchGpuMode"/> 的 <c>autoRestart</c> 分支）
    /// 过去绕过空路由守卫，直接发布 DGPU_DIRECT_CONNECT_RESTART：真机上这就是「空路由白重启」。
    /// 与手动路径同一条契约——空路由必须放弃重启、报告失败，绝不发布重启指令。
    /// </summary>
    [Fact]
    public async Task EmptyRestartRoute_AutoRestartPath_NeverPublishesRestart()
    {
        var actions = new List<string>();
        Func<int, bool, IReadOnlyList<Dictionary<string, object>>> empty =
            (_, _) => Array.Empty<Dictionary<string, object>>();
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Setting/Control" && payload is IDictionary<string, object> values &&
                values.TryGetValue("Action", out object? action))
            {
                string name = action?.ToString() ?? "";
                actions.Add(name);
                if (name == "IGPU_ONLY_CONNECT_RB_ON")
                    hardware!.HandleMessage("Setting/Status",
                        "{\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_ON\"}");
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true, DgpuDirect = false });

        try
        {
            MechrevoService.GpuRestartRouteOverride = empty;
            using (hardware)
            {
                hardware.HandleMessage("Setting/Status",
                    "{\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_OFF\"}");
                var service = new MechrevoService(hardware);

                bool confirmed = await service.SwitchGpuMode(MechrevoService.GpuIGpu, autoRestart: true);

                Assert.False(confirmed);
                Assert.DoesNotContain("DGPU_DIRECT_CONNECT_RESTART", actions);
            }
        }
        finally { MechrevoService.GpuRestartRouteOverride = null; }
    }

    /// <summary>完整路由：目标写入齐全，重启指令恰好一次且位于最后。</summary>
    [Fact]
    public async Task CompleteRestartRoute_PublishesTargetPayloadsThenExactlyOneRestart()
    {
        var actions = new List<string>();
        using MechrevoHw hardware = NewHardware(actions);
        hardware.HandleMessage("Setting/Status", DirectStatus);
        var service = new MechrevoService(hardware);

        Assert.True(await service.RequestGpuModeRestartAsync(MechrevoService.GpuIGpu));

        Assert.Contains("DGPU_DIRECT_CONNECT_TOGGLE_IGPU", actions);
        Assert.Equal("DGPU_DIRECT_CONNECT_RESTART", actions[^1]);
        Assert.Equal(1, actions.Count(a => a == "DGPU_DIRECT_CONNECT_RESTART"));
    }

    /// <summary>
    /// 进入独显直连（target=2）的路由必须与今天完全一致：官方进入直连的三连击
    /// （TOGGLE_ON → RB_OFF[SetToWMIEC=OK] → TOGGLE_ON）后再重启。本次修复不得改动它。
    /// </summary>
    [Fact]
    public async Task EnteringDirect_KeepsTheOfficialThreeActionRouteUnchanged()
    {
        var actions = new List<string>();
        using MechrevoHw hardware = NewHardware(actions);
        hardware.HandleMessage("Setting/Status", DirectStatus);
        var service = new MechrevoService(hardware);

        Assert.True(await service.RequestGpuModeRestartAsync(MechrevoService.GpuDgpu));

        Assert.Equal(
            new[]
            {
                "DGPU_DIRECT_CONNECT_TOGGLE_ON",
                "IGPU_ONLY_CONNECT_RB_OFF",
                "DGPU_DIRECT_CONNECT_TOGGLE_ON",
                "DGPU_DIRECT_CONNECT_RESTART",
            },
            actions);
    }

    /// <summary>
    /// 反向（离开独显直连）的指令序列必须与官方 console 一致：
    /// 官方 <c>CCUWinUI.decompiled.cs:86477-86515</c> 对「目标=混合/标准」只发一条
    /// <c>DGPU_DIRECT_CONNECT_TOGGLE_OFF</c>，对「目标=核显-only」只发一条
    /// <c>DGPU_DIRECT_CONNECT_TOGGLE_IGPU</c>，随后 800ms 再发 <c>DGPU_DIRECT_CONNECT_RESTART</c>。
    /// 仓库里没有别的反向词汇，因此这里只锁协议一致性，不发明新的指令。
    /// </summary>
    [Theory]
    [InlineData(MechrevoService.GpuStandard, "DGPU_DIRECT_CONNECT_TOGGLE_OFF")]
    [InlineData(MechrevoService.GpuIGpu, "DGPU_DIRECT_CONNECT_TOGGLE_IGPU")]
    public async Task LeavingDirect_UsesTheOfficialSingleActionThenRestart(int targetMode, string expectedAction)
    {
        var actions = new List<string>();
        using MechrevoHw hardware = NewHardware(actions);
        hardware.HandleMessage("Setting/Status", DirectStatus);
        var service = new MechrevoService(hardware);

        Assert.True(await service.RequestGpuModeRestartAsync(targetMode));

        Assert.Equal(new[] { expectedAction, "DGPU_DIRECT_CONNECT_RESTART" }, actions);
    }
}
