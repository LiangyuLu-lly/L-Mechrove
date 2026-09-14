using MechrevoLite;
using MechrevoLite.Hardware;
using MechrevoLite.Helpers;

namespace MechrevoLite.Tests;

/// <summary>
/// 这些回归测试对应的是在真机运行日志里发现的缺陷，不是纸面推演。
/// 每一条都注明了当时的实测证据，避免以后有人「优化」回旧行为。
/// </summary>
public class RuntimeDefectRegressionTests
{
    // ---------- 未映射的 ASUS 设备码不能返回看起来合法的 0 ----------

    [Fact]
    public void UnmappedAsusDeviceCode_ReportsNotSupported()
    {
        // 实测证据：DeviceGet 返回 0 时，Settings.VisualiseScreen 的 `miniled1 >= 0`
        // 成立，在没有 miniled 面板的机器上显示出「多区背光」按钮，点击后
        // DeviceSet 是空实现，什么都不会发生。config.json 里还被写进 miniled=0。
        Assert.True(AsusACPI.NotSupported < 0,
            "调用方普遍用 >= 0 判断硬件存在，未映射设备码必须返回负值。");
    }

    [Theory]
    [InlineData(AsusACPI.ScreenMiniled1)]
    [InlineData(AsusACPI.ScreenMiniled2)]
    [InlineData(AsusACPI.ScreenHDRControl)]
    [InlineData(AsusACPI.ScreenOverdrive)]
    [InlineData(AsusACPI.ScreenOptimalBrightness)]
    [InlineData(AsusACPI.ScreenFHD)]
    [InlineData(AsusACPI.ChargerMode)]
    public void CapabilityProbe_TreatsNotSupportedAsAbsentHardware(uint deviceCode)
    {
        // 锁住调用方的判定约定：这些设备码在本机没有数据来源，返回值必须落在
        // 「硬件不存在」一侧，UI 才不会暴露无效控件。
        Assert.NotEqual(0, AsusACPI.NotSupported);
        Assert.False(AsusACPI.NotSupported >= 0, $"设备码 0x{deviceCode:X8} 不能被判定为存在。");
    }

    // ---------- 启动时不得报告不存在的电源切换 ----------

    [Fact]
    public void FirstPowerSourceObservation_IsBaselineNotAChange()
    {
        // 实测证据：currentSource 硬编码初始值为 Battery，插电的机器第一次
        // SetAutoModes 必然算出 sourceChanged=true，白白多发一轮模式命令与
        // Windows 电源方案写入。
        Program.PowerSource? unobserved = null;
        Assert.False(SourceChanged(unobserved, Program.PowerSource.Barrel),
            "没有上一次观测值时不存在变化，首次观测只建立基线。");
    }

    [Fact]
    public void SubsequentPowerSourceObservation_DetectsRealChange()
    {
        Assert.True(SourceChanged(Program.PowerSource.Barrel, Program.PowerSource.Battery));
        Assert.True(SourceChanged(Program.PowerSource.Battery, Program.PowerSource.USBC));
        Assert.False(SourceChanged(Program.PowerSource.Barrel, Program.PowerSource.Barrel));
    }

    // SetAutoModes 里的判定式，保持与实现一致。
    static bool SourceChanged(Program.PowerSource? last, Program.PowerSource detected) =>
        last.HasValue && detected != last.Value;

    [Fact]
    public void CurrentSource_DoesNotDefaultToBatteryWhenUnobserved()
    {
        // currentSource 现在在未观测时回落到实际供电方式，而不是无条件说「电池」。
        Program.PowerSource? saved = Program.lastObservedSource;
        try
        {
            Program.lastObservedSource = null;
            Assert.Equal(Program.ReadPowerSource(), Program.currentSource);

            Program.lastObservedSource = Program.PowerSource.USBC;
            Assert.Equal(Program.PowerSource.USBC, Program.currentSource);
        }
        finally { Program.lastObservedSource = saved; }
    }

    [Fact]
    public void ReapplyStillHappensOnStartupEvenWithoutSourceChange()
    {
        // 修掉假变化不能顺带丢掉启动时本应执行的那次施加：
        // 启动路径 wakeup=false，!wakeup 使其仍然成立。
        Assert.True(Program.ShouldReapplyPerformanceMode(powerChanged: false, wakeup: false, sourceChanged: false));
        // 唤醒且电源没变则保持当前模式，不打扰用户。
        Assert.False(Program.ShouldReapplyPerformanceMode(powerChanged: false, wakeup: true, sourceChanged: false));
        Assert.True(Program.ShouldReapplyPerformanceMode(powerChanged: false, wakeup: true, sourceChanged: true));
    }

    // ---------- 周期性状态日志必须去重 ----------

    [Fact]
    public void StatusLog_WritesOnlyWhenContentChanges()
    {
        // 实测证据：液冷状态行内容完全相同，每 6 秒重复一次。日志上限 2 MB 且截断保留
        // 尾部，这类刷屏会把启动过程与失败确认挤出去——而支持方式就是让用户发日志。
        string key = "test-" + Guid.NewGuid().ToString("N");
        Logger.ResetChangeTracking(key);

        var written = new List<string>();
        foreach (string message in new[] { "state=A", "state=A", "state=A", "state=B", "state=B", "state=A" })
            if (Logger.TryMarkChanged(key, message)) written.Add(message);

        Assert.Equal(new[] { "state=A", "state=B", "state=A" }, written);
    }

    [Fact]
    public void StatusLog_ResetMakesTheNextWriteUnconditional()
    {
        // 重连后要重新记录一份基线，否则内容与断连前相同就整段跳过，
        // 日志里看不出这一代连接读到了什么。
        string key = "test-" + Guid.NewGuid().ToString("N");
        Logger.ResetChangeTracking(key);

        Assert.True(Logger.TryMarkChanged(key, "state=A"));
        Assert.False(Logger.TryMarkChanged(key, "state=A"));
        Logger.ResetChangeTracking(key);
        Assert.True(Logger.TryMarkChanged(key, "state=A"));
    }

    [Fact]
    public void StatusLogKeys_CoverEveryDeduplicatedStatusLine()
    {
        // 连接就绪时按这张表重置。漏掉一个键 = 那条状态在重连后不再记录基线。
        Assert.Contains("lc-status", MechrevoLite.Hardware.MechrevoHw.StatusLogKeys);
        Assert.Contains("kb-status", MechrevoLite.Hardware.MechrevoHw.StatusLogKeys);
        Assert.Contains("hwoc-status", MechrevoLite.Hardware.MechrevoHw.StatusLogKeys);
        Assert.Contains("fan-table", MechrevoLite.Hardware.MechrevoHw.StatusLogKeys);
        Assert.Contains("lb-status-HidLightbar/Status", MechrevoLite.Hardware.MechrevoHw.StatusLogKeys);
        Assert.Contains("lb-status-HidLightbar_Logo/Status", MechrevoLite.Hardware.MechrevoHw.StatusLogKeys);
        Assert.Equal(MechrevoLite.Hardware.MechrevoHw.StatusLogKeys.Length,
            MechrevoLite.Hardware.MechrevoHw.StatusLogKeys.Distinct().Count());
    }

    [Fact]
    public void StatusLog_KeysAreIndependent()
    {
        // 不同状态用各自的键，一条变化不能顺带解锁另一条。
        string a = "test-a-" + Guid.NewGuid().ToString("N");
        string b = "test-b-" + Guid.NewGuid().ToString("N");

        Assert.True(Logger.TryMarkChanged(a, "same"));
        Assert.True(Logger.TryMarkChanged(b, "same"));
        Assert.False(Logger.TryMarkChanged(a, "same"));
        Assert.False(Logger.TryMarkChanged(b, "same"));
    }

    // ---------- MQTT 端口暴露必须有可执行的处置手段 ----------

    [Fact]
    public void MissingFirewallRuleWarning_TellsTheUserWhatToRun()
    {
        // 实测证据：厂商 broker 监听 0.0.0.0:13688 且凭据固定，程序检测到缺少阻断规则
        // 后只写一行英文警告，没有任何处置入口。警告必须可执行。
        string warning = MqttSecurity.MissingRuleWarning();

        Assert.Contains("13688", warning);
        Assert.Contains("--secure-mqtt", warning);
    }

    [Fact]
    public void FirewallRuleName_IsStableAcrossCreateCheckAndRemove()
    {
        // 创建、检测、删除必须用同一个规则名，否则会出现「创建成功但检测不到」
        // 或者删不掉的情况。删除只按精确名匹配，不能误伤其他规则。
        Assert.False(string.IsNullOrWhiteSpace(MqttSecurity.FirewallRuleName));
        Assert.Contains("L-Mechrevo", MqttSecurity.FirewallRuleName);
        Assert.Equal(13688, MqttSecurity.DefaultPort);
    }

    [Theory]
    [InlineData("13688", true)]
    [InlineData("13000-14000", true)]
    [InlineData("80,443,13688", true)]
    [InlineData("13687,13689", false)]
    [InlineData("13689-13700", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("any", false)]
    public void PortListMatching_HandlesSingleValuesRangesAndLists(string? ports, bool expected)
    {
        Assert.Equal(expected, MqttSecurity.PortListContains(ports, MqttSecurity.DefaultPort));
    }

    [Fact]
    public async Task FanRespective_IsConfirmedThroughTheFanTableRequest()
    {
        // 实测证据（2026-09-11，用户报告「风扇独立控制打不开」）：开关每次都被判未确认并弹回。
        // Probe 抓包：SET_FAN_CONTROL_RESPECTIVE 之后 GCU 只回显命令本身，既不推 Fan/Table；
        // 而 GETSTATUS 返回的 Fan/Status 里根本没有 FanControlRespective 字段，只有
        // GET_FAN_SPEED_CURVE_SETTING 换回的 Fan/Table 才带这个标志（false→true 双向实测生效）。
        var commands = new List<Dictionary<string, object>>();
        MechrevoHw hardware = null!;
        hardware = new MechrevoHw((topic, payload) =>
        {
            var values = new Dictionary<string, object>(
                Assert.IsAssignableFrom<IDictionary<string, object>>(payload));
            if (topic != "Fan/Control") return Task.CompletedTask;
            commands.Add(values);
            if (values["Action"]?.ToString() == "GET_FAN_SPEED_CURVE_SETTING")
                hardware.HandleMessage("Fan/Table", """{ "Name": "M4T1", "FanControlRespective": true }""");
            else if (values["Action"]?.ToString() == "GETSTATUS")
                hardware.HandleMessage("Fan/Status", """{ "OperatingMode": "3", "FAN_TableName": "M4T1" }""");
            return Task.CompletedTask;
        });
        using (hardware)
        {
            hardware.HandleMessage("Fan/Table", """{ "Name": "M4T1", "FanControlRespective": false }""");
            var service = new MechrevoService(hardware);

            Assert.True(await service.SwitchFanRespective(true));
            Assert.Equal(
                new[] { "SET_FAN_CONTROL_RESPECTIVE", "GET_FAN_SPEED_CURVE_SETTING" },
                commands.Select(command => command["Action"]?.ToString()).ToArray());
            Assert.Equal("M4T1", commands[0]["Name"]);
        }
    }
}
