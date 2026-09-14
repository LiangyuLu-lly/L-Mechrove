using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 电源指示灯的开关与亮度。
///
/// 官方有三个动作：PowerLight_ON / PowerLight_OFF（开关，载荷同时带 Brightness）
/// 和 PowerLight_Brightness（只调亮度）。这两条契约以前都没落实：
/// 亮度借开关动作下发（会连带重写开关态），开关命令又不带亮度（服务端按 0 处理）。
/// </summary>
public class PowerLightTests
{
    static (MechrevoHw Hardware, MechrevoService Service, List<Dictionary<string, object>> Written) NewRig()
    {
        var written = new List<Dictionary<string, object>>();
        var hardware = new MechrevoHw((_, payload) =>
        {
            if (payload is IDictionary<string, object> values)
                written.Add(new Dictionary<string, object>(values));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities());
        return (hardware, new MechrevoService(hardware), written);
    }

    const string StatusOnAt60 = """
        {"PowerLightSwitch":true,"PowerLightBrightness":60}
        """;

    [Fact]
    public void PowerLightSwitchAndBrightnessAreBothParsed()
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status", StatusOnAt60);

            Assert.True(hardware.PowerLightSeen);
            Assert.True(hardware.QuickSwitches["powerlight"]);
            Assert.Equal(60, hardware.PowerLightBrightness);
            Assert.True(hardware.SupportsQuickSwitch("powerlight"));
            Assert.True(hardware.SupportsPowerLightBrightness);
        }
    }

    /// <summary>
    /// 报了开关但没报亮度的机型：开关可用，亮度入口必须关掉。
    /// 官方对这一项另有 IsPowerLightAdjustSupport 判定，两者不是一回事。
    /// </summary>
    [Fact]
    public void BrightnessStaysUnsupportedWhenOnlyTheSwitchIsReported()
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status", "{\"PowerLightSwitch\":true}");

            Assert.True(hardware.SupportsQuickSwitch("powerlight"));
            Assert.False(hardware.SupportsPowerLightBrightness);
        }
    }

    /// <summary>
    /// 亮度要用专用动作。借 PowerLight_ON/OFF 调亮度会在服务端连带重写开关态，
    /// 外部刚改过开关就会被这条命令覆盖回去。
    /// </summary>
    [Fact]
    public async Task BrightnessUsesTheDedicatedActionInsteadOfTheSwitchAction()
    {
        var (hardware, service, written) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status", StatusOnAt60);
            written.Clear();

            await service.SetPowerLightBrightness(80);

            // 确认阶段会补发 GETSTATUS 轮询，所以只看真正带亮度的那条命令。
            Dictionary<string, object> command = Assert.Single(
                written, entry => entry.ContainsKey("Brightness"));
            Assert.Equal("PowerLight_Brightness", command["Action"]);
            Assert.Equal(80, command["Brightness"]);
            Assert.DoesNotContain(written, entry =>
                Equals(entry["Action"], "PowerLight_ON") || Equals(entry["Action"], "PowerLight_OFF"));
        }
    }

    /// <summary>
    /// 开关命令必须带上当前亮度。只发 Action 的话服务端拿不到亮度会按 0 处理，
    /// 等于开灯的同时把它调暗到看不见。
    /// </summary>
    [Theory]
    [InlineData(true, "PowerLight_ON")]
    [InlineData(false, "PowerLight_OFF")]
    public async Task SwitchCommandCarriesTheCurrentBrightness(bool on, string expectedAction)
    {
        var (hardware, service, written) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status", StatusOnAt60);
            written.Clear();

            await service.SwitchQuick("powerlight", on);

            Dictionary<string, object> command = Assert.Single(
                written, entry => Equals(entry["Action"], expectedAction));
            Assert.Equal(60, command["Brightness"]);
        }
    }

    /// <summary>
    /// 别的快捷开关不该被顺带塞进 Brightness——那是电源灯专属的字段。
    /// </summary>
    [Fact]
    public async Task OtherQuickSwitchesDoNotGainABrightnessField()
    {
        var (hardware, service, written) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status", """
                {"PowerLightSwitch":true,"PowerLightBrightness":60,"WifiStatus":"ON"}
                """);
            written.Clear();

            await service.SwitchQuick("wifi", false);

            Assert.All(written, command => Assert.DoesNotContain("Brightness", command.Keys));
        }
    }

    [Fact]
    public async Task BrightnessWriteIsRejectedWhenTheModelNeverReportedIt()
    {
        var (hardware, service, written) = NewRig();
        using (hardware)
        {
            Assert.False(await service.SetPowerLightBrightness(50));
            Assert.Empty(written);
        }
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(150, 100)]
    public async Task BrightnessIsClampedToTheProtocolRange(int requested, int expected)
    {
        var (hardware, service, written) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status", StatusOnAt60);
            written.Clear();

            await service.SetPowerLightBrightness(requested);

            Dictionary<string, object> command = Assert.Single(
                written, entry => entry.ContainsKey("Brightness"));
            Assert.Equal(expected, command["Brightness"]);
        }
    }
}
