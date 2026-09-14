using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// PL4（瞬时功耗墙）与风扇转换灵敏度的协议契约。
///
/// 这两项过去都只解析了一半：PL4 只读 CPU_PL4_Double_Flag 而不读值也不能写，
/// 灵敏度的两个字段全库都没有解析。补齐时最容易错的是 PL4 的半瓦换算——
/// 官方对 CPU_PL4_Double_Flag 机型在回读时乘 2、下发时折半，
/// 漏掉任何一侧都会让显示或写入差一倍。
/// </summary>
public class NumericSettingTests
{
    static MechrevoHw NewHardware(List<Dictionary<string, object>>? written = null) =>
        new((_, payload) =>
        {
            if (written is not null && payload is IDictionary<string, object> values)
                written.Add(new Dictionary<string, object>(values));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities());

    // ---- PL4 解析 ----

    /// <summary>
    /// 没有 Double_Flag 的机型：值与范围原样呈现。
    /// </summary>
    [Fact]
    public void Pl4IsReadBackVerbatimWhenTheModelDoesNotUseHalfWattUnits()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Fan/Status", """
            {
              "CPU_PL4": 140,
              "CPU_PL4Minimum": 10,
              "CPU_PL4Maximum": 210
            }
            """);

        Assert.False(hardware.Pl4Double);
        Assert.Equal(140, hardware.Pl4);
        Assert.Equal(10, hardware.Pl4Minimum);
        Assert.Equal(210, hardware.Pl4Maximum);
        Assert.True(hardware.Pl4Adjustable);
    }

    /// <summary>
    /// Double_Flag 机型：值与上下限都要乘 2 才是面向用户的瓦数。
    /// 退回「照原样显示」会让整条 PL4 显示差一倍。
    /// </summary>
    [Fact]
    public void Pl4AndItsRangeAreDoubledWhenTheModelReportsHalfWattUnits()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Fan/Status", """
            {
              "CPU_PL4_Double_Flag": "1",
              "CPU_PL4": 105,
              "CPU_PL4Minimum": 5,
              "CPU_PL4Maximum": 105
            }
            """);

        Assert.True(hardware.Pl4Double);
        Assert.Equal(210, hardware.Pl4);
        Assert.Equal(10, hardware.Pl4Minimum);
        Assert.Equal(210, hardware.Pl4Maximum);
    }

    /// <summary>
    /// 换算不能被自己的输出污染：公开属性存的是乘过 2 的值，
    /// 如果拿它当下一帧解析的 fallback，第二帧就会变成 4 倍。
    /// </summary>
    [Fact]
    public void RepeatedStatusFramesDoNotCompoundTheHalfWattScaling()
    {
        using MechrevoHw hardware = NewHardware();
        const string status = """
            {"CPU_PL4_Double_Flag":"1","CPU_PL4":105,"CPU_PL4Minimum":5,"CPU_PL4Maximum":105}
            """;

        hardware.HandleMessage("Fan/Status", status);
        hardware.HandleMessage("Fan/Status", status);
        // 不带 PL4 字段的帧也不该改变已知值。
        hardware.HandleMessage("Fan/Status", "{\"CPU_PL1\":45}");

        Assert.Equal(210, hardware.Pl4);
        Assert.Equal(10, hardware.Pl4Minimum);
        Assert.Equal(210, hardware.Pl4Maximum);
    }

    [Fact]
    public void Pl4RangeStaysUnknownUntilTheModelReportsIt()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Fan/Status", "{\"CPU_PL1\":45,\"CPU_PL2\":65}");

        Assert.Equal(-1, hardware.Pl4);
        Assert.Equal(-1, hardware.Pl4Minimum);
        Assert.Equal(-1, hardware.Pl4Maximum);
        Assert.False(hardware.Pl4Adjustable);
    }

    // ---- PL4 写入 ----

    /// <summary>下发的必须是折半后的线上值，不是界面上的瓦数。</summary>
    [Fact]
    public async Task Pl4WriteHalvesTheValueOnHalfWattModels()
    {
        var written = new List<Dictionary<string, object>>();
        using MechrevoHw hardware = NewHardware(written);
        hardware.HandleMessage("Fan/Status", """
            {"CPU_PL4_Double_Flag":"1","CPU_PL4":105,"CPU_PL4Minimum":5,"CPU_PL4Maximum":105}
            """);

        await hardware.SetPl4(200);

        Dictionary<string, object> command = Assert.Single(
            written, entry => entry.ContainsKey("PL4"));
        Assert.Equal("SET_OPERATING_MODE_DETAIL", command["Action"]);
        Assert.Equal("100", command["PL4"]);
    }

    [Fact]
    public async Task Pl4WriteSendsTheRequestedWattsWhenUnitsAreWhole()
    {
        var written = new List<Dictionary<string, object>>();
        using MechrevoHw hardware = NewHardware(written);
        hardware.HandleMessage("Fan/Status", """
            {"CPU_PL4":140,"CPU_PL4Minimum":10,"CPU_PL4Maximum":210}
            """);

        await hardware.SetPl4(150);

        Dictionary<string, object> command = Assert.Single(
            written, entry => entry.ContainsKey("PL4"));
        Assert.Equal("150", command["PL4"]);
    }

    /// <summary>
    /// 范围校验必须用运行时上报的范围。这里刻意用一台上限只有 60 W 的机型，
    /// 退回 AsusACPI 里那套 210 W 的安全边界常量就会放行。
    /// </summary>
    [Fact]
    public async Task Pl4WriteIsRejectedOutsideTheRangeReportedByThisModel()
    {
        var written = new List<Dictionary<string, object>>();
        using MechrevoHw hardware = NewHardware(written);
        hardware.HandleMessage("Fan/Status", """
            {"CPU_PL4":45,"CPU_PL4Minimum":15,"CPU_PL4Maximum":60}
            """);

        Assert.False(await hardware.SetPl4(120));
        Assert.False(await hardware.SetPl4(10));
        Assert.DoesNotContain(written, entry => entry.ContainsKey("PL4"));
    }

    [Fact]
    public async Task Pl4WriteIsRejectedBeforeTheModelReportsARange()
    {
        var written = new List<Dictionary<string, object>>();
        using MechrevoHw hardware = NewHardware(written);

        Assert.False(await hardware.SetPl4(100));
        Assert.DoesNotContain(written, entry => entry.ContainsKey("PL4"));
    }

    /// <summary>
    /// 半瓦机型上奇数瓦会在折半时被截断，回读只能是偶数。
    /// 确认目标必须是量化后的值，否则一次成功的写入会被判成失败。
    /// </summary>
    [Fact]
    public void Pl4QuantisationReportsTheValueThatIsActuallyReachable()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Fan/Status", """
            {"CPU_PL4_Double_Flag":"1","CPU_PL4":105,"CPU_PL4Minimum":5,"CPU_PL4Maximum":105}
            """);

        Assert.Equal(200, hardware.Pl4Effective(201));
        Assert.Equal(100, hardware.Pl4ToWire(201));
    }

    // ---- 风扇转换灵敏度 ----

    /// <summary>
    /// 官方状态用 FAN_ 前缀，控制载荷用不带前缀的键名。两种拼写都要认。
    /// </summary>
    [Theory]
    [InlineData("FAN_FanSwitchSpeedEnabled", "FAN_FanSwitchSpeed")]
    [InlineData("FanSwitchSpeedEnabled", "FanSwitchSpeed")]
    public void FanSwitchSpeedIsParsedUnderBothKnownFieldSpellings(string enabledKey, string valueKey)
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Fan/Status", $"{{\"{enabledKey}\":\"1\",\"{valueKey}\":1700}}");

        Assert.True(hardware.SupportsFanSwitchSpeed);
        Assert.True(hardware.FanSwitchSpeedEnabled);
        Assert.Equal(1700, hardware.FanSwitchSpeed);
    }

    /// <summary>
    /// 判据是「服务端报过这一项」。没报过就不能暴露入口——静态能力位里没有对应的键，
    /// 拿机型名单去猜等于给不支持的机器造一个假开关。
    /// </summary>
    [Fact]
    public void FanSwitchSpeedStaysUnsupportedUntilTheModelReportsIt()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Fan/Status", "{\"CPU_PL1\":45}");

        Assert.False(hardware.SupportsFanSwitchSpeed);
        Assert.Equal(-1, hardware.FanSwitchSpeed);
    }

    /// <summary>只上报开关、没上报数值时也算支持这一项。</summary>
    [Fact]
    public void FanSwitchSpeedSupportCanBeEstablishedByTheToggleAlone()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Fan/Status", "{\"FAN_FanSwitchSpeedEnabled\":\"0\"}");

        Assert.True(hardware.SupportsFanSwitchSpeed);
        Assert.False(hardware.FanSwitchSpeedEnabled);
    }

    /// <summary>
    /// 默认范围按 EC 字段宽度推导：单字节、单位 100 ms。
    /// </summary>
    [Fact]
    public void FanSwitchSpeedFallsBackToTheRangeImpliedByTheEcFieldWidth()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Fan/Status", "{\"FAN_FanSwitchSpeed\":1700}");

        Assert.Equal(100, hardware.FanSwitchSpeedMinimum);
        Assert.Equal(25500, hardware.FanSwitchSpeedMaximum);
    }

    /// <summary>固件真的上报范围时，上报值优先于推导值。</summary>
    [Fact]
    public void AReportedFanSwitchSpeedRangeOverridesTheDerivedOne()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Fan/Status", """
            {
              "FAN_FanSwitchSpeed": 1700,
              "FAN_FanSwitchSpeedMinimum": 500,
              "FAN_FanSwitchSpeedMaximum": 3000
            }
            """);

        Assert.Equal(500, hardware.FanSwitchSpeedMinimum);
        Assert.Equal(3000, hardware.FanSwitchSpeedMaximum);
    }

    /// <summary>
    /// EC 以 100 ms 为一档存这个值，非整档入参会被固件截断。
    /// 不先对齐就会拿一个回读不到的值去比对，把成功的写入判成失败。
    /// </summary>
    [Theory]
    [InlineData(1700, 1700)]
    [InlineData(1749, 1700)]
    [InlineData(1799, 1700)]
    [InlineData(50, 100)]        // 低于最小档，夹到最小值
    [InlineData(99999, 25500)]   // 超过 EC 单字节上限，夹到最大值
    public void FanSwitchSpeedIsAlignedToTheHundredMillisecondEcStep(int requested, int expected)
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Fan/Status", "{\"FAN_FanSwitchSpeed\":1700}");

        Assert.Equal(expected, hardware.QuantiseFanSwitchSpeed(requested));
    }

    /// <summary>
    /// 灵敏度是自定义模式的风扇调参，不是系统快捷开关。
    /// 混进 QuickSwitches 会在快捷开关面板里留下一个没有动作映射的幽灵项。
    /// </summary>
    [Fact]
    public void FanSwitchSpeedIsNotExposedAsAQuickSwitch()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Fan/Status", "{\"FAN_FanSwitchSpeedEnabled\":\"1\",\"FAN_FanSwitchSpeed\":1700}");

        Assert.False(hardware.SupportsQuickSwitch("fanswitchspeed"));
        Assert.DoesNotContain("fanswitchspeed", hardware.QuickSwitches.Keys);
    }
}
