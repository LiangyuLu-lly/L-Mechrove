using MechrevoLite.Hardware;
using MechrevoLite.Mode;

namespace MechrevoLite.Tests;

/// <summary>
/// T19（Wave D）happy 路径：厂商 <c>SysPowerModeIndex</c> 与控制台 <c>OperatingMode</c>
/// **分别建模**、显式命名转换；下发绝对量的键名/单位与厂商一致
/// （Tcc = TjMax − offset、PL4 double-flag 减半、PL 为字符串）。
///
/// 失败路径见 <see cref="PowerModeEnumFailTests"/>。
/// </summary>
public class PowerModeEnumTests
{
    [Fact]
    public void TheVendorEnumValuesAreTheConsoleIndexValues()
    {
        Assert.Equal(1, (int)VendorSysPowerMode.Performance);
        Assert.Equal(2, (int)VendorSysPowerMode.Balanced);
        Assert.Equal(3, (int)VendorSysPowerMode.BatterySaver);
        Assert.Equal(4, (int)VendorSysPowerMode.Benchmark);
    }

    [Fact]
    public void TheConsoleEnumValuesMatchWhatTheHardwareReports()
    {
        Assert.Equal(0, (int)ConsoleOperatingMode.Office);
        Assert.Equal(1, (int)ConsoleOperatingMode.Gaming);
        Assert.Equal(2, (int)ConsoleOperatingMode.Turbo);
        Assert.Equal(3, (int)ConsoleOperatingMode.Customize);
    }

    [Theory]
    [InlineData(MechrevoService.ModeGaming, ConsoleOperatingMode.Gaming)]
    [InlineData(MechrevoService.ModeTurbo, ConsoleOperatingMode.Turbo)]
    [InlineData(MechrevoService.ModeOffice, ConsoleOperatingMode.Office)]
    [InlineData(MechrevoService.ModeCustom, ConsoleOperatingMode.Customize)]
    public void TheVisualModeMapsExplicitlyOntoTheConsoleMode(int visualMode, ConsoleOperatingMode expected)
    {
        Assert.Equal(expected, PowerModeMapping.FromVisualMode(visualMode));
    }

    [Theory]
    [InlineData(ConsoleOperatingMode.Office, MechrevoService.ModeOffice)]
    [InlineData(ConsoleOperatingMode.Gaming, MechrevoService.ModeGaming)]
    [InlineData(ConsoleOperatingMode.Turbo, MechrevoService.ModeTurbo)]
    [InlineData(ConsoleOperatingMode.Customize, MechrevoService.ModeCustom)]
    public void TheConsoleModeMapsBackToTheVisualMode(ConsoleOperatingMode mode, int expected)
    {
        Assert.Equal(expected, PowerModeMapping.ToVisualMode(mode));
    }

    [Theory]
    [InlineData(ConsoleOperatingMode.Office, VendorSysPowerMode.Balanced)]
    [InlineData(ConsoleOperatingMode.Gaming, VendorSysPowerMode.Performance)]
    [InlineData(ConsoleOperatingMode.Turbo, VendorSysPowerMode.Benchmark)]
    public void TheConsoleModeHasAnExplicitVendorIndex(ConsoleOperatingMode mode, VendorSysPowerMode expected)
    {
        Assert.True(PowerModeMapping.TryToVendor(mode, out VendorSysPowerMode vendor));
        Assert.Equal(expected, vendor);
    }

    [Fact]
    public void CustomizeHasNoVendorIndex()
    {
        Assert.False(PowerModeMapping.TryToVendor(ConsoleOperatingMode.Customize, out _));
    }

    [Theory]
    [InlineData(100, 10, 90)]
    [InlineData(0, 10, 90)]      // TjMax 缺失按 100
    [InlineData(95, 20, 75)]
    [InlineData(100, 200, 0)]    // clamp 下界
    [InlineData(0, -50, 100)]    // clamp 上界
    public void TccOffsetIsTjMaxMinusTarget(int tjMax, int target, int expected)
    {
        Assert.Equal(expected, ModeDetailEncoding.TccOffset(tjMax, target));
    }

    [Theory]
    [InlineData(200, false, 2, 100)]
    [InlineData(200, true, 2, 200)]   // AMD 原样
    [InlineData(200, false, 1, 200)]  // 无 double-flag
    public void Pl4IsHalvedOnlyWhenTheDoubleFlagIsSet(int watts, bool amd, int scale, int expected)
    {
        Assert.Equal(expected, ModeDetailEncoding.Pl4WireWatts(watts, amd, scale));
    }

    [Fact]
    public void ThePowerLimitIsSentAsAString()
    {
        Assert.Equal("45", ModeDetailEncoding.Pl(45));
        Assert.IsType<string>(ModeDetailEncoding.Pl(45));
    }

    /// <summary>C5 端到端接线：视觉模式经命名转换后，下发帧里的控制台索引必须正确。</summary>
    [Fact]
    public async Task E2EWiring_SwitchModeSendsTheConsoleModeIndex()
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            published.Add((topic, (Dictionary<string, object>)payload));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities());
        hardware.HandleMessage("Fan/Status", """{"OperatingMode":"0"}""");
        var service = new MechrevoService(hardware);

        await service.SwitchMode(MechrevoService.ModeTurbo);

        var overclock = published.Single(entry => entry.Topic == "LCHWOC/Control" &&
                                                  entry.Payload.ContainsKey("IsNormalRun"));
        Assert.Equal((int)ConsoleOperatingMode.Turbo, overclock.Payload["IsNormalRun"]);
    }

    [Fact]
    public async Task E2EWiring_SetModeSendsTheConsoleModeIndex()
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            published.Add((topic, (Dictionary<string, object>)payload));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities());

        await hardware.SetMode(MechrevoService.ModeOffice);

        var overclock = published.Single(entry => entry.Topic == "LCHWOC/Control" &&
                                                  entry.Payload.ContainsKey("IsNormalRun"));
        Assert.Equal((int)ConsoleOperatingMode.Office, overclock.Payload["IsNormalRun"]);
    }
}
