namespace MechrevoLite.Hardware;

internal enum LightingIdleAction
{
    None,
    Suspend,
    Restore,
}

internal static class LightingState
{
    internal static LightingIdleAction ResolveIdleAction(int timeoutSeconds, long idleMilliseconds, bool suspended)
    {
        if (timeoutSeconds <= 0) return LightingIdleAction.None;
        long timeoutMilliseconds = TimeSpan.FromSeconds(timeoutSeconds).TotalMilliseconds >= long.MaxValue
            ? long.MaxValue
            : (long)TimeSpan.FromSeconds(timeoutSeconds).TotalMilliseconds;
        if (!suspended && idleMilliseconds >= timeoutMilliseconds) return LightingIdleAction.Suspend;
        return suspended && idleMilliseconds < 2_000 ? LightingIdleAction.Restore : LightingIdleAction.None;
    }

    internal static bool ShouldSuspendForBattery(bool optionEnabled, bool onBattery) =>
        optionEnabled && onBattery;

    /// <summary>临时熄灯判据：离电关灯或空闲休眠命中即三条通道一起进入临时熄灯。</summary>
    internal static bool IsTemporarilySuspended(bool offOnBatteryEnabled, bool onBattery, bool idleSuspended) =>
        ShouldSuspendForBattery(offOnBatteryEnabled, onBattery) || idleSuspended;

    /// <summary>
    /// 开关回显的单一语义：开关表示「这条灯现在亮着」，不是「用户曾启用过」。
    /// 临时熄灯（空闲休眠 / 离电关灯）期间三条通道实际都是暗的，开关必须一起回落到关，
    /// 否则会出现「设备已灭而开关仍显示开」。
    /// </summary>
    internal static bool IsLightSwitchOn(bool channelPowerOn, bool temporarilySuspended) =>
        channelPowerOn && !temporarilySuspended;

    internal static bool ShouldRestoreKeyboardPower(
        bool userWantsPower,
        bool temporaryPowerOff,
        bool cachedPowerOn) =>
        userWantsPower && (temporaryPowerOff || !cachedPowerOn);
}
