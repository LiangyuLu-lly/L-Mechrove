using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T34（Wave G）happy 路径：调色切换**读回一致才算成功**（sRGB=2），HDR 开启时绝不盲发。
///
/// 缺陷（#7 耀世15pro4060）：手动切换 sRGB 等恒失败——下发后没有人核对读回，
/// 失败被静默吞掉。失败路径见 <see cref="ColorCalibrationSwitchFailTests"/>。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class ColorCalibrationSwitchTests
{
    internal static (MechrevoHw Hw, List<(string Topic, Dictionary<string, object> Payload)> Written) NewHardware(
        string echoMode, bool supportsCalibration = true)
    {
        var written = new List<(string Topic, Dictionary<string, object> Payload)>();
        MechrevoHw hw = null!;
        hw = new MechrevoHw((topic, payload) =>
        {
            var dict = (Dictionary<string, object>)payload;
            written.Add((topic, dict));
            string action = dict.TryGetValue("Action", out object? value) ? value?.ToString() ?? "" : "";
            if (topic == "Setting/Control" && action.StartsWith("COLOR_CALIBRATION", StringComparison.Ordinal))
            {
                string frame = action == "COLOR_CALIBRATION_OFF"
                    ? """{"ColorCalibrationSwitch":"ColorCalibrationSwitch_OFF","ColorCalibrationResultCode":"0"}"""
                    : $"{{\"ColorCalibrationMode\":\"{echoMode}\",\"ColorCalibrationResultCode\":\"0\"}}";
                hw.HandleMessage("Setting/Status", frame);
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ColorCalibration = supportsCalibration });
        return (hw, written);
    }

    internal static MechrevoService NewService(MechrevoHw hw, bool initialOn, int initialMode, bool hdr) =>
        new(hw,
            () => hw.ColorCalibrationSwitchSeen ? hw.ColorCalibrationSwitch : initialOn,
            () => initialMode,
            () => hdr);

    [Theory]
    [InlineData(true, false, 2, 2, true)]    // 开方向只认档位：sRGB=2 读回一致
    [InlineData(true, false, 3, 2, false)]   // 读回 3 != 请求 2 -> 失败
    [InlineData(true, false, 0, 2, false)]
    [InlineData(false, false, 9, 0, true)]   // 关方向只认开关
    [InlineData(false, true, 9, 0, false)]
    public void TheReadBackMustMatchTheRequest(bool expectedOn, bool actualOn, int actualMode, int targetMode, bool expected)
    {
        Assert.Equal(expected, ColorCalibrationSwitchPolicy.StateMatches(expectedOn, actualOn, actualMode, targetMode));
    }

    [Theory]
    [InlineData(false, true, false, false, 1, 2, (int)ColorCalibrationDecision.Unavailable)]
    [InlineData(true, true, true, false, 1, 2, (int)ColorCalibrationDecision.HdrBlocked)]
    [InlineData(true, true, false, true, 2, 2, (int)ColorCalibrationDecision.AlreadyApplied)]
    [InlineData(true, true, false, false, 1, 2, (int)ColorCalibrationDecision.Proceed)]
    [InlineData(true, false, false, true, 2, 0, (int)ColorCalibrationDecision.Proceed)]
    [InlineData(true, false, false, false, 1, 0, (int)ColorCalibrationDecision.AlreadyApplied)]
    public void TheDecisionOrderIsUnavailableThenHdrThenAlreadyApplied(
        bool connectedAndSupported, bool expectedOn, bool hdr, bool currentOn, int currentMode, int targetMode,
        int expected)
    {
        Assert.Equal((ColorCalibrationDecision)expected, ColorCalibrationSwitchPolicy.Decide(
            connectedAndSupported, expectedOn, hdr, currentOn, currentMode, targetMode));
    }

    [Fact]
    public async Task SwitchingToSrgbSendsTheVendorActionAndSucceedsOnReadBackTwo()
    {
        var (hw, written) = NewHardware("2");
        using (hw)
        {
            bool result = await NewService(hw, initialOn: false, initialMode: 1, hdr: false).SetColorCalibration(2);

            Assert.True(result);
            Assert.Contains(written, entry => entry.Payload["Action"] as string == "COLOR_CALIBRATION_ON_SRGB");
            Assert.DoesNotContain(written, entry => entry.Payload["Action"] as string == "COLOR_CALIBRATION_ON");
        }
    }

    [Fact]
    public async Task AnAlreadyAppliedProfileIsANoOp()
    {
        var (hw, written) = NewHardware("2");
        using (hw)
        {
            bool result = await NewService(hw, initialOn: true, initialMode: 2, hdr: false).SetColorCalibration(2);

            Assert.True(result);
            Assert.Empty(written);
        }
    }

    [Fact]
    public async Task SwitchingOffSendsOffWithTheCurrentFileNameAndSucceedsOnTheSwitch()
    {
        var (hw, written) = NewHardware("2");
        using (hw)
        {
            bool result = await NewService(hw, initialOn: true, initialMode: 2, hdr: false).SetColorCalibration(0);

            Assert.True(result);
            var off = written.Single(entry => entry.Payload["Action"] as string == "COLOR_CALIBRATION_OFF");
            Assert.Equal("sRGB", off.Payload["FileName"]);
        }
    }
}
