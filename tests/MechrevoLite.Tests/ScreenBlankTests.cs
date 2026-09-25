using MechrevoLite.Display;
using MechrevoLite.Helpers;

namespace MechrevoLite.Tests;

/// <summary>
/// T33（Wave G）happy 路径：息屏失败必须**可检测**，绝不静默当已熄屏。
///
/// 缺陷（#6 耀世15pro4060）：<c>ScreenBlankController.Dim</c> 在亮度读不到 / 写失败时直接 return，
/// 调用方拿到 void、界面照旧，用户以为已熄屏而屏幕没黑。
///
/// 失败路径见 <see cref="ScreenBlankFailTests"/>。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class ScreenBlankTests
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
    public void DimmingWritesTheBlankLevelAndHoldsTheSystemAwake()
    {
        var writes = new List<int>();
        var flags = new List<uint>();
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            ScreenBrightness.ReadOverride = () => 40;
            ScreenBrightness.WriteOverride = writes.Add;
            ScreenBlankController.ExecutionStateOverride = flags.Add;
            ScreenBlankController.ClockOverride = () => start;
            ScreenBlankController.AutoPollEnabled = false;

            ScreenBlankOutcome outcome = ScreenBlankController.Dim();

            Assert.Equal(ScreenBlankOutcome.Dimmed, outcome);
            Assert.True(ScreenBlankController.IsDimmed);
            Assert.Equal(new[] { ScreenBlankController.BlankLevel }, writes);
            Assert.Contains(ScreenBlankController.BlankExecutionState, flags);
            Assert.Null(ScreenBlankController.LastFailureReason);
        }
        finally { Reset(); }
    }

    [Fact]
    public void TheFirstRealInputRestoresTheSavedBrightness()
    {
        var writes = new List<int>();
        var flags = new List<uint>();
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            ScreenBrightness.ReadOverride = () => 40;
            ScreenBrightness.WriteOverride = writes.Add;
            ScreenBlankController.ExecutionStateOverride = flags.Add;
            ScreenBlankController.ClockOverride = () => start;
            ScreenBlankController.AutoPollEnabled = false;
            NativeMethods.IdleTimeProvider = () => TimeSpan.Zero;

            Assert.Equal(ScreenBlankOutcome.Dimmed, ScreenBlankController.Dim());
            ScreenBlankController.Poll(start.AddMilliseconds(ScreenBlankController.MinBlankBeforeRestoreMs + 200));

            Assert.False(ScreenBlankController.IsDimmed);
            Assert.Equal(40, writes[^1]);
            Assert.Contains(ScreenBlankController.ES_CONTINUOUS, flags);
        }
        finally { Reset(); }
    }

    /// <summary>
    /// 触发 Dim 的那次点击仍是 GetLastInputInfo 的「刚发生」输入。
    /// 800ms 轮询不得把它当成唤醒；否则黑屏会在第一轮 poll 被自己点亮。
    /// </summary>
    [Fact]
    public void Dim_DoesNotRestoreOnOriginatingClick()
    {
        var writes = new List<int>();
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            ScreenBrightness.ReadOverride = () => 40;
            ScreenBrightness.WriteOverride = writes.Add;
            ScreenBlankController.ExecutionStateOverride = _ => { };
            ScreenBlankController.ClockOverride = () => start;
            ScreenBlankController.AutoPollEnabled = false;
            NativeMethods.IdleTimeProvider = () => TimeSpan.Zero;

            Assert.Equal(800, ScreenBlankController.MinBlankBeforeRestoreMs);
            Assert.Equal(ScreenBlankOutcome.Dimmed, ScreenBlankController.Dim());
            Assert.True(ScreenBlankController.IsDimmed);

            ScreenBlankController.Poll(start.AddMilliseconds(ScreenBlankController.PollIntervalMs));
            Assert.True(ScreenBlankController.IsDimmed);

            ScreenBlankController.Poll(start.AddMilliseconds(ScreenBlankController.MinBlankBeforeRestoreMs));

            Assert.True(ScreenBlankController.IsDimmed,
                "800ms poll must not treat the click that triggered Dim as user-input restore.");
            Assert.Equal(new[] { ScreenBlankController.BlankLevel }, writes);
        }
        finally { Reset(); }
    }

    /// <summary>
    /// 真机事故锁：Dim 不得再走 HWND_BROADCAST / SC_MONITORPOWER，只压亮度 + 顶住执行状态。
    /// </summary>
    [Fact]
    public void Dim_NeverUsesMonitorPower()
    {
        var writes = new List<int>();
        var flags = new List<uint>();
        try
        {
            ScreenBrightness.ReadOverride = () => 40;
            ScreenBrightness.WriteOverride = writes.Add;
            ScreenBlankController.ExecutionStateOverride = flags.Add;
            ScreenBlankController.AutoPollEnabled = false;

            Assert.Equal(ScreenBlankOutcome.Dimmed, ScreenBlankController.Dim());

            Assert.Equal(new[] { ScreenBlankController.BlankLevel }, writes);
            Assert.Equal(new[] { ScreenBlankController.BlankExecutionState }, flags);

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MechrevoLite.slnx")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string source = File.ReadAllText(Path.Combine(
                dir!.FullName, "src", "MechrevoLiteWin", "Display", "ScreenBlankController.cs"));
            Assert.DoesNotMatch(@"\bSendMessage\s*\(", source);
            Assert.DoesNotMatch(@"\bPostMessage\s*\(", source);
            Assert.DoesNotMatch(@"\bTurnOffScreen\b", source);
        }
        finally { Reset(); }
    }

    [Fact]
    public void TheWatchdogRestoresAfterTheMaximumBlankDuration()
    {
        var writes = new List<int>();
        DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            ScreenBrightness.ReadOverride = () => 55;
            ScreenBrightness.WriteOverride = writes.Add;
            ScreenBlankController.ExecutionStateOverride = _ => { };
            ScreenBlankController.ClockOverride = () => start;
            ScreenBlankController.AutoPollEnabled = false;
            NativeMethods.IdleTimeProvider = () => TimeSpan.FromHours(2);

            Assert.Equal(ScreenBlankOutcome.Dimmed, ScreenBlankController.Dim());
            ScreenBlankController.Poll(start + ScreenBlankController.MaxBlankDuration + TimeSpan.FromSeconds(1));

            Assert.False(ScreenBlankController.IsDimmed);
            Assert.Equal(55, writes[^1]);
        }
        finally { Reset(); }
    }

    [Fact]
    public void ASecondDimWhileAlreadyDimmedIsReportedAndDoesNotRewrite()
    {
        var writes = new List<int>();
        try
        {
            ScreenBrightness.ReadOverride = () => 30;
            ScreenBrightness.WriteOverride = writes.Add;
            ScreenBlankController.ExecutionStateOverride = _ => { };
            ScreenBlankController.AutoPollEnabled = false;

            Assert.Equal(ScreenBlankOutcome.Dimmed, ScreenBlankController.Dim());
            Assert.Equal(ScreenBlankOutcome.AlreadyDimmed, ScreenBlankController.Dim());

            Assert.Equal(new[] { ScreenBlankController.BlankLevel }, writes);
        }
        finally { Reset(); }
    }

    [Fact]
    public void TrySetReportsWhetherTheWriteActuallyLanded()
    {
        try
        {
            ScreenBrightness.WriteOverride = _ => { };
            Assert.True(ScreenBrightness.TrySet(10));

            ScreenBrightness.WriteOverride = _ => throw new InvalidOperationException("no instance");
            Assert.False(ScreenBrightness.TrySet(10));
        }
        finally { Reset(); }
    }
}
