using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

public class LightingStateTests
{
    [Theory]
    [InlineData(0, 60_000, false, 0)]
    [InlineData(10, 9_999, false, 0)]
    [InlineData(10, 10_000, false, 1)]
    [InlineData(10, 10_000, true, 0)]
    [InlineData(10, 1_999, true, 2)]
    [InlineData(10, 2_000, true, 0)]
    public void IdleLightingPolicy_OnlyRestoresAfterFreshUserInput(
        int timeoutSeconds, long idleMilliseconds, bool suspended, int expected)
    {
        Assert.Equal((LightingIdleAction)expected,
            LightingState.ResolveIdleAction(timeoutSeconds, idleMilliseconds, suspended));
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void BatteryLightingPolicy_RequiresBothUserSettingAndBatteryPower(
        bool optionEnabled, bool onBattery, bool expected) =>
        Assert.Equal(expected, LightingState.ShouldSuspendForBattery(optionEnabled, onBattery));

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, true, true)]
    public void TemporarySuspendCoversBatteryAndIdle(
        bool offOnBattery, bool onBattery, bool idleSuspended, bool expected) =>
        Assert.Equal(expected, LightingState.IsTemporarilySuspended(offOnBattery, onBattery, idleSuspended));

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void SwitchReflectsTheLitDevice_NotUserIntent(
        bool channelPowerOn, bool temporarilySuspended, bool expected) =>
        Assert.Equal(expected, LightingState.IsLightSwitchOn(channelPowerOn, temporarilySuspended));

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, false)]
    public void TemporaryKeyboardPowerRestore_RespectsUserIntentAndStaleReadback(
        bool userWantsPower, bool temporaryPowerOff, bool cachedPowerOn, bool expected) =>
        Assert.Equal(expected, LightingState.ShouldRestoreKeyboardPower(
            userWantsPower, temporaryPowerOff, cachedPowerOn));

    [Fact]
    public void LightSettingsParser_ClampsManualValuesAndKeepsPowerState()
    {
        LightChannelSettings settings = LightingSettingsStore.ParseLines(new[]
        {
            "effect=Single",
            "light=9",
            "speed=-1",
            "color=-16711681",
            "power=0",
        }, "Wave");

        Assert.Equal("Single", settings.Effect);
        Assert.Equal(4, settings.Light);
        Assert.Equal(1, settings.Speed);
        Assert.False(settings.PowerOn);
    }
}
