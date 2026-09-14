using MechrevoLite.Hardware;
using System.Drawing;

namespace MechrevoLite.Tests;

public class KeyboardRgbCompatibilityTests
{
    [Theory]
    [InlineData(0x600B, 0xFF03, 1, 65, 9)]
    [InlineData(0x7002, 0xFF03, 4, 65, 9)]
    [InlineData(0x7003, 0x0001, 1, 65, 9)]
    public void BetterRgbCompatibleInterfaces_AreAccepted(int productId, int usagePage, int interfaceNumber,
        int outputReportLength, int featureReportLength)
    {
        int score = KeyboardRgb.ScoreCandidate((ushort)productId, (ushort)usagePage, interfaceNumber,
            (ushort)outputReportLength, (ushort)featureReportLength);

        Assert.True(score > 0);
    }

    [Fact]
    public void UnrelatedIteInterface_IsRejected()
    {
        Assert.Equal(0, KeyboardRgb.ScoreCandidate(0x7001, 0x0001, 0, 65, 9));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 25)]
    [InlineData(2, 50)]
    [InlineData(3, 75)]
    [InlineData(4, 100)]
    [InlineData(5, 100)]
    public void HardwareBrightnessLevel_MapsToSoftwareBrightness(int level, int expected)
    {
        Assert.Equal(expected, KeyboardRgb.MapHardwareBrightnessLevel(level));
    }

    [Theory]
    [InlineData(73, 2, 73)]
    [InlineData(-1, 2, 50)]
    [InlineData(125, 1, 100)]
    [InlineData(4, -1, 100)]
    [InlineData(3, -1, 75)]
    [InlineData(-1, 75, 75)]
    [InlineData(-1, -1, -1)]
    public void ReportedHardwareBrightness_PrefersOfficialPercentAndFallsBackToLegacyLevel(
        int brightness, int legacyLevel, int expected)
    {
        Assert.Equal(expected, KeyboardRgb.MapReportedHardwareBrightness(brightness, legacyLevel));
    }

    [Fact]
    public void KeyboardConfig_RoundTripsZeroCustomBrightness()
    {
        var source = new KeyboardRgb { Brightness = 0 };
        var loaded = new KeyboardRgb();

        loaded.LoadConfigLines(source.SerializeConfig().Split(Environment.NewLine));

        Assert.Equal(0, loaded.Brightness);
    }

    [Fact]
    public void Known600bDescriptor_WinsTieAgainstGenericCompatibleDevice()
    {
        int known = KeyboardRgb.ScoreCandidate(0x600B, 0xFF03, 1, 65, 9);
        int generic = KeyboardRgb.ScoreCandidate(0x7002, 0xFF03, 1, 65, 9);

        Assert.True(known > generic);
    }

    [Fact]
    public void RainbowWheel_IsCenteredOnKKey()
    {
        var center = KeyboardRgb.GetWheelCenter();

        Assert.Equal(8, center.X);
        Assert.Equal(3, center.Y);
    }

    [Fact]
    public void KeyboardConfig_RoundTripsEveryCustomSpeed()
    {
        var source = new KeyboardRgb
        {
            CloseTimerMinutes = 45,
            KbHidMode = KeyboardRgb.ModeMatrix,
            BreathSpeed = 29,
            WaveSpeed = 19,
            SparkleSpeed = 18,
            WheelSpeed = 17,
            LightningSpeed = 16,
            FlameSpeed = 15,
            RainSpeed = 14,
            MatrixSpeed = 13,
        };
        string serialized = source.SerializeConfig();
        var loaded = new KeyboardRgb();

        loaded.LoadConfigLines(serialized.Split(Environment.NewLine));

        Assert.Equal(45, loaded.CloseTimerMinutes);
        Assert.Equal(KeyboardRgb.ModeMatrix, loaded.KbHidMode);
        Assert.Equal(29, loaded.BreathSpeed);
        Assert.Equal(19, loaded.WaveSpeed);
        Assert.Equal(18, loaded.SparkleSpeed);
        Assert.Equal(17, loaded.WheelSpeed);
        Assert.Equal(16, loaded.LightningSpeed);
        Assert.Equal(15, loaded.FlameSpeed);
        Assert.Equal(14, loaded.RainSpeed);
        Assert.Equal(13, loaded.MatrixSpeed);
    }

    [Fact]
    public void LegacyOfficialEffectConfigKeys_AreIgnoredOnLoad()
    {
        var keyboard = new KeyboardRgb();
        keyboard.LoadConfigLines(new[]
        {
            "kbType=0",
            "kbEffect=Aurora",
            "kbLight=3",
            "kbSpeed=2",
            "kbSingleColor=-1",
            "kbFirmwareSignature=Aurora|3|2|-1",
            "kbHidMode=5",
        });

        // 官方效果已移除：旧键静默忽略，不写入任何状态；HID 键照常生效。
        Assert.Equal(5, keyboard.KbHidMode);
    }

    [Fact]
    public void KeyboardPowerOff_PersistsAcrossApplicationRestart()
    {
        var source = new KeyboardRgb { KbPowerOn = false };
        var loaded = new KeyboardRgb();

        loaded.LoadConfigLines(source.SerializeConfig().Split(Environment.NewLine));

        Assert.False(loaded.KbPowerOn);
    }

    [Theory]
    [InlineData(KeyboardRgb.ModeWave, KeyboardRgb.ModeWave, false, true, false)]
    [InlineData(KeyboardRgb.ModeWave, KeyboardRgb.ModeWheel, false, true, true)]
    [InlineData(KeyboardRgb.ModeWave, KeyboardRgb.ModeWave, true, true, true)]
    [InlineData(KeyboardRgb.ModeWave, KeyboardRgb.ModeWave, false, false, true)]
    public void EffectRestart_OnlyRestartsWhenTheCurrentRendererCannotBeReused(
        int requestedMode,
        int activeMode,
        bool stopping,
        bool threadAlive,
        bool expected) =>
        Assert.Equal(expected, KeyboardRgb.ShouldRestartEffect(requestedMode, activeMode, stopping, threadAlive));

    [Fact]
    public async Task QueuedKeyboardConfig_IsPersistedWithoutClosingTheWindow()
    {
        string directory = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "rgb.cfg");
        var source = new KeyboardRgb(path) { KbHidMode = KeyboardRgb.ModeMatrix, Brightness = 80 };
        try
        {
            source.QueueSaveConfig();
            for (int attempt = 0; attempt < 20 && !File.Exists(path); attempt++)
                await Task.Delay(50);

            Assert.True(File.Exists(path));
            var loaded = new KeyboardRgb(path);
            loaded.LoadConfig();
            Assert.Equal(KeyboardRgb.ModeMatrix, loaded.KbHidMode);
            Assert.Equal(80, loaded.Brightness);
        }
        finally
        {
            source.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
