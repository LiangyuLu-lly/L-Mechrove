using System.Collections.Concurrent;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// run5 液冷「自动」不升速（高负载泵/风扇不跟随固件曲线）的回归锁。
///
/// 根因：3 秒状态定时器在自动意图下按温度挑一个**具体档位**周期下发（GCU 通道
/// LC_PumpCtrl / BLE 直连占空比），把厂商固件的 LC_CoolingAuto 曲线钉死在那一刻的读数上。
/// 修复后自动意图只补发「进入厂商自动」（LC_FanCtrl=4），定时刷新不再下发任何具体档位；
/// 且温度读不到/过期时绝不挑最低档。
/// </summary>
public class LiquidCoolingAutoTests
{
    /// <summary>
    /// 定时刷新决策的锁：只要用户把泵或风扇选了「自动」，刷新返回的只能是
    /// EnableVendorAuto（厂商自动命令）或 None，结构上不存在「写具体档位」这一项。
    /// 这就是「一次刷新都不可能再把自动覆盖成手动档」的保证。
    /// </summary>
    [Theory]
    [InlineData(true, true, 4, 0)]              // 已在厂商自动 → 什么都不做
    [InlineData(true, true, 3, 1)]              // 自动意图 + 未在自动 → 补发自动
    [InlineData(true, true, -1, 1)]
    [InlineData(false, true, 3, 1)]             // 仅风扇自动
    [InlineData(false, true, 4, 0)]
    [InlineData(true, false, 3, 1)]             // 仅泵自动也走厂商自动
    [InlineData(true, false, 4, 0)]
    public void AutoSelected_RefreshNeverRequestsAConcreteGear(
        bool pumpAuto, bool fanAuto, int reportedFanCtrl, int expected)
    {
        Assert.Equal((LiquidCoolingAutoPolicy.RefreshAction)expected,
            LiquidCoolingAutoPolicy.DecideGcuRefresh(pumpAuto, fanAuto, reportedFanCtrl));
    }

    /// <summary>两边都是手动档时刷新不下发任何命令——手动档只在用户选择那一刻写一次。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(-1)]
    public void ManualIntent_RefreshDoesNothing(int reportedFanCtrl)
    {
        Assert.Equal(LiquidCoolingAutoPolicy.RefreshAction.None,
            LiquidCoolingAutoPolicy.DecideGcuRefresh(pumpAuto: false, fanAuto: false, reportedFanCtrl));
    }

    /// <summary>选择「自动」必须经同一个 seam 判定为「下发厂商自动」，而不是写具体档位。</summary>
    [Fact]
    public void SelectingAuto_IsRecognisedAsVendorAuto()
    {
        Assert.True(LiquidCoolingAutoPolicy.ShouldTransmitVendorAuto(WaterCoolerBle.ProfileAutomatic));
        Assert.False(LiquidCoolingAutoPolicy.ShouldTransmitVendorAuto(0));
        Assert.False(LiquidCoolingAutoPolicy.ShouldTransmitVendorAuto(WaterCoolerBle.TopPumpProfile));
        Assert.False(LiquidCoolingAutoPolicy.ShouldTransmitVendorAuto(WaterCoolerBle.ProfileUnset));
    }

    /// <summary>
    /// 「选择自动」在服务层的可观测结果：把厂商自动档下发到 BT_LC/Control
    /// （LC_FanCtrl=4 → 设备回读 LC_FanCtrl=4，进入 LC_CoolingAuto）。
    /// </summary>
    [Fact]
    public async Task SelectingAuto_TransmitsTheVendorAutoStateToTheDevice()
    {
        var actions = new ConcurrentQueue<string?>();
        string? transmittedFanCtrl = null;
        MechrevoHw hardware = null!;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic != "BT_LC/Control") return Task.CompletedTask;
            var values = Assert.IsAssignableFrom<IDictionary<string, object>>(payload);
            string? action = values.TryGetValue("Action", out object? value) ? value?.ToString() : null;
            actions.Enqueue(action);
            if (action == "LC_FanCtrl")
                transmittedFanCtrl = values.TryGetValue("FanCtrl", out object? ctrl) ? ctrl?.ToString() : null;
            if (action is "LC_FanCtrl" or "GETSTATUS")
                hardware.HandleMessage("BT_LC/Status", LiquidCoolingStatus(pump: -1, fan: 4));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        using (hardware)
        {
            hardware.HandleMessage("BT_LC/Status", LiquidCoolingStatus(pump: 0, fan: 3));
            var service = new MechrevoService(hardware);

            Assert.True(await service.SwitchLcFanAuto());
            Assert.Equal(LiquidCoolingDisplayPolicy.GcuFanAutoIndex.ToString(), transmittedFanCtrl);
        }
    }

    /// <summary>手动档写入与确认链路保持不变：写到具体档位、读回匹配才算成功。</summary>
    [Fact]
    public async Task ManualGear_StillWritesAndConfirms()
    {
        var actions = new ConcurrentQueue<string?>();
        string? pumpGear = null;
        string? fanGear = null;
        MechrevoHw hardware = null!;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic != "BT_LC/Control") return Task.CompletedTask;
            var values = Assert.IsAssignableFrom<IDictionary<string, object>>(payload);
            string? action = values.TryGetValue("Action", out object? value) ? value?.ToString() : null;
            actions.Enqueue(action);
            if (action == "LC_PumpCtrl")
                pumpGear = values.TryGetValue("PumpCtrl", out object? p) ? p?.ToString() : null;
            if (action == "LC_FanCtrl")
                fanGear = values.TryGetValue("FanCtrl", out object? f) ? f?.ToString() : null;
            if (action == "GETSTATUS")
                hardware.HandleMessage("BT_LC/Status", LiquidCoolingStatus(pump: 2, fan: 3));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        using (hardware)
        {
            hardware.HandleMessage("BT_LC/Status", LiquidCoolingStatus(pump: 0, fan: 0));
            var service = new MechrevoService(hardware);

            Assert.True(await service.SwitchLcPump(2));
            Assert.Equal("2", pumpGear);
            Assert.True(await service.SwitchLcFan(3));
            Assert.Equal("3", fanGear);
            Assert.Contains("GETSTATUS", actions);
        }
    }

    /// <summary>
    /// 温度读不到（0/负）或已过期时，自动档不得按 0°C 去挑最低档：
    /// 决策层直接判定温度不可用，BLE 客户端曲线因此保持现状。
    /// </summary>
    [Fact]
    public void UnknownOrStaleTemperature_DoesNotPinTheGearToMinimum()
    {
        Assert.False(LiquidCoolingAutoPolicy.TemperatureUsable(0));
        Assert.False(LiquidCoolingAutoPolicy.TemperatureUsable(-5));
        Assert.True(LiquidCoolingAutoPolicy.TemperatureUsable(45));

        // 过期读数一律折算成 0（不可用），即便两个传感器都留着旧的高温值。
        Assert.Equal(0, LiquidCoolingAutoPolicy.ResolveCoolingTemperature(cpuTemp: 92, gpuTemp: 88, fresh: false));
        Assert.Equal(92, LiquidCoolingAutoPolicy.ResolveCoolingTemperature(cpuTemp: 92, gpuTemp: 88, fresh: true));
        Assert.False(LiquidCoolingAutoPolicy.TemperatureUsable(
            LiquidCoolingAutoPolicy.ResolveCoolingTemperature(92, 88, fresh: false)));

        // 自动曲线本身不会在无效温度下给出档位（0 档 = 最低），而是保持现状。
        Assert.Null(WaterCoolerBle.SelectAutomaticPumpProfileIfUsable(0, current: -1));
        Assert.Null(WaterCoolerBle.SelectAutomaticFanProfileIfUsable(0, current: -1));
        Assert.Equal(0, WaterCoolerBle.SelectAutomaticPumpProfileIfUsable(30, current: -1));
    }

    /// <summary>温度「新鲜度」由每一条 System/CpuInfo / System/GpuInfo 的到达时间驱动。</summary>
    [Fact]
    public void TemperatureFreshness_TracksTheLastCpuOrGpuReport()
    {
        using var hardware = new MechrevoHw();

        Assert.False(hardware.IsTemperatureFresh(TimeSpan.FromSeconds(30)));

        hardware.HandleMessage("System/CpuInfo", """{ "CpuTemperature": 55 }""");
        Assert.True(hardware.IsTemperatureFresh(TimeSpan.FromSeconds(30)));
        // 同一时刻往前推 31 秒后，这份读数就不再算新鲜。
        Assert.False(hardware.IsTemperatureFresh(TimeSpan.FromSeconds(30),
            now: Environment.TickCount64 + 31_000));

        // GPU 单独上报同样算新鲜。
        long afterCpu = Environment.TickCount64;
        hardware.HandleMessage("System/GpuInfo", """{ "GpuTemperature": 60 }""");
        Assert.True(hardware.IsTemperatureFresh(TimeSpan.FromSeconds(30), now: afterCpu + 20_000));
    }

    /// <summary>
    /// 源码护栏：GCU 自动刷新必须走 LiquidCoolingAutoPolicy，且旧的「客户端挑具体泵速档」
    /// 实现（会周期性写 LC_PumpCtrl）不得复活。
    /// </summary>
    [Fact]
    public void GcuAutoRefresh_WiresThroughThePolicyAndHasNoConcreteGearWriter()
    {
        string source = File.ReadAllText(RepoRootPath("src", "MechrevoLiteWin", "Settings.cs"));

        Assert.Contains("LiquidCoolingAutoPolicy.DecideGcuRefresh", source);
        Assert.Contains("LiquidCoolingAutoPolicy.ShouldTransmitVendorAuto", source);
        Assert.DoesNotContain("ApplyGcuAutomaticPumpGearAsync", source);
    }

    static string LiquidCoolingStatus(int pump, int fan) => $$"""
        {
          "connected": true,
          "ConnectString": "Connected",
          "LC_action": true,
          "LC_PumpCtrl": "{{pump}}",
          "LC_FanCtrl": "{{fan}}"
        }
        """;

    static string RepoRootPath(params string[] tail)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, Path.Combine(tail));
    }
}
