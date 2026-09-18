using MechrevoLite.Display;
using MechrevoLite.Helpers;

namespace MechrevoLite.Tests;

/// <summary>
/// T33 失败/边界路径：读不到 / 写不下 / 未熄屏时绝不置「已熄屏」；失败原因可检测。
/// happy 路径见 <see cref="ScreenBlankTests"/>。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class ScreenBlankFailTests
{
    static void Reset()
    {
        ScreenBlankController.AutoPollEnabled = false;
        ScreenBlankController.RestoreImmediate();   // 清 _dimmed，避免跨用例污染
        ScreenBrightness.ReadOverride = null;
        ScreenBrightness.WriteOverride = null;
        ScreenBlankController.ExecutionStateOverride = null;
        ScreenBlankController.ClockOverride = null;
        ScreenBlankController.AutoPollEnabled = true;
        NativeMethods.IdleTimeProvider = null;
    }

    [Fact]
    public void AMissingWmiInstanceIsReportedAndNeverClaimedAsDimmed()
    {
        var flags = new List<uint>();
        var writes = new List<int>();
        try
        {
            ScreenBrightness.ReadOverride = () => null;
            ScreenBrightness.WriteOverride = writes.Add;
            ScreenBlankController.ExecutionStateOverride = flags.Add;
            ScreenBlankController.AutoPollEnabled = false;

            ScreenBlankOutcome outcome = ScreenBlankController.Dim();

            Assert.Equal(ScreenBlankOutcome.ReadFailed, outcome);
            Assert.NotEqual(ScreenBlankOutcome.Dimmed, outcome);
            Assert.False(ScreenBlankController.IsDimmed);
            Assert.Empty(writes);
            Assert.Empty(flags);
            Assert.False(string.IsNullOrWhiteSpace(ScreenBlankController.LastFailureReason));
        }
        finally { Reset(); }
    }

    [Fact]
    public void AThrowingReadIsReportedAsReadFailedNotAnUnhandledException()
    {
        try
        {
            ScreenBrightness.ReadOverride = () => throw new InvalidOperationException("root\\wmi unavailable");
            ScreenBlankController.ExecutionStateOverride = _ => { };
            ScreenBlankController.AutoPollEnabled = false;

            ScreenBlankOutcome outcome = ScreenBlankController.Dim();

            Assert.Equal(ScreenBlankOutcome.ReadFailed, outcome);
            Assert.False(ScreenBlankController.IsDimmed);
        }
        finally { Reset(); }
    }

    [Fact]
    public void AWriteThatCannotLandIsReportedAsWriteFailed()
    {
        var flags = new List<uint>();
        try
        {
            ScreenBrightness.ReadOverride = () => 50;
            ScreenBrightness.WriteOverride = _ => throw new IOException("no brightness method");
            ScreenBlankController.ExecutionStateOverride = flags.Add;
            ScreenBlankController.AutoPollEnabled = false;

            ScreenBlankOutcome outcome = ScreenBlankController.Dim();

            Assert.Equal(ScreenBlankOutcome.WriteFailed, outcome);
            Assert.False(ScreenBlankController.IsDimmed);
            Assert.Empty(flags);
            Assert.False(string.IsNullOrWhiteSpace(ScreenBlankController.LastFailureReason));
        }
        finally { Reset(); }
    }

    [Fact]
    public void RestoreWhenNotDimmedWritesNothing()
    {
        var writes = new List<int>();
        try
        {
            ScreenBrightness.WriteOverride = writes.Add;
            ScreenBlankController.ExecutionStateOverride = _ => { };
            ScreenBlankController.AutoPollEnabled = false;

            ScreenBlankController.Restore();
            ScreenBlankController.RestoreImmediate();

            Assert.Empty(writes);
            Assert.False(ScreenBlankController.IsDimmed);
        }
        finally { Reset(); }
    }

    [Fact]
    public void ASuccessfulDimClearsThePreviousFailureReason()
    {
        try
        {
            ScreenBrightness.ReadOverride = () => null;
            ScreenBlankController.AutoPollEnabled = false;
            Assert.Equal(ScreenBlankOutcome.ReadFailed, ScreenBlankController.Dim());
            Assert.False(string.IsNullOrWhiteSpace(ScreenBlankController.LastFailureReason));

            ScreenBrightness.ReadOverride = () => 25;
            ScreenBrightness.WriteOverride = _ => { };
            ScreenBlankController.ExecutionStateOverride = _ => { };

            Assert.Equal(ScreenBlankOutcome.Dimmed, ScreenBlankController.Dim());
            Assert.Null(ScreenBlankController.LastFailureReason);
        }
        finally { Reset(); }
    }
}
