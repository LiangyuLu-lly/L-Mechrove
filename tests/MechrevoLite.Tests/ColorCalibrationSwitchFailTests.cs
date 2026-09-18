using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T34 失败/边界路径：HDR / 未连接 / 读回不一致 / 越界档位都必须**明确失败且不空发**。
/// happy 路径见 <see cref="ColorCalibrationSwitchTests"/>。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class ColorCalibrationSwitchFailTests
{
    [Fact]
    public void HdrBlocksOnlyTheOnDirection()
    {
        Assert.Equal(ColorCalibrationDecision.HdrBlocked,
            ColorCalibrationSwitchPolicy.Decide(true, expectedOn: true, hdrEnabled: true, currentOn: false, currentMode: 1, targetMode: 2));
        Assert.Equal(ColorCalibrationDecision.Proceed,
            ColorCalibrationSwitchPolicy.Decide(true, expectedOn: false, hdrEnabled: true, currentOn: true, currentMode: 2, targetMode: 0));
    }

    [Fact]
    public async Task SwitchingToSrgbWithHdrOnFailsWithoutPublishingAnything()
    {
        var (hw, written) = ColorCalibrationSwitchTests.NewHardware("2");
        using (hw)
        {
            bool result = await ColorCalibrationSwitchTests.NewService(hw, initialOn: false, initialMode: 1, hdr: true)
                .SetColorCalibration(2);

            Assert.False(result);
            Assert.Empty(written);
        }
    }

    [Fact]
    public async Task AnUnsupportedMachineFailsWithoutPublishingAnything()
    {
        var (hw, written) = ColorCalibrationSwitchTests.NewHardware("2", supportsCalibration: false);
        using (hw)
        {
            bool result = await ColorCalibrationSwitchTests.NewService(hw, initialOn: false, initialMode: 1, hdr: false)
                .SetColorCalibration(2);

            Assert.False(result);
            Assert.Empty(written);
        }
    }

    [Fact]
    public async Task AReadBackThatDisagreesIsReportedAsFailure()
    {
        var (hw, _) = ColorCalibrationSwitchTests.NewHardware("3");   // 请求 sRGB=2，读回 3
        using (hw)
        {
            bool result = await ColorCalibrationSwitchTests.NewService(hw, initialOn: false, initialMode: 1, hdr: false)
                .SetColorCalibration(2);

            Assert.False(result);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(99)]
    public async Task AnOutOfRangeProfileIsRejectedWithoutPublishing(int mode)
    {
        var (hw, written) = ColorCalibrationSwitchTests.NewHardware("2");
        using (hw)
        {
            bool result = await ColorCalibrationSwitchTests.NewService(hw, initialOn: false, initialMode: 1, hdr: false)
                .SetColorCalibration(mode);

            Assert.False(result);
            Assert.Empty(written);
        }
    }

    [Fact]
    public void TheOffDirectionNeverMatchesWhenTheSwitchIsStillOn()
    {
        Assert.False(ColorCalibrationSwitchPolicy.StateMatches(expectedOn: false, actualOn: true, actualMode: 0, targetMode: 0));
    }
}
