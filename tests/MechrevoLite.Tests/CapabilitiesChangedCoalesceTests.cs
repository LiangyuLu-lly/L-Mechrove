using MechrevoLite.Helpers;

namespace MechrevoLite.Tests;

/// <summary>
/// CapabilitiesChanged 在十二个状态主题上连发；Program 侧必须把 BeginInvoke 收成一次
/// （清 queued 之后再来的一次可以再跑——永远不是十二次）。
/// </summary>
public class CapabilitiesChangedCoalesceTests
{
    [Fact]
    public void TwelveRaisesBeforePostedActionRuns_FireTheActionOnce()
    {
        var coalescer = new UiRefreshCoalescer();
        int fires = 0;
        Action? posted = null;

        for (int i = 0; i < 12; i++)
            coalescer.Request(action => posted = action, () => fires++);

        Assert.NotNull(posted);
        Assert.Equal(0, fires);
        posted();
        Assert.Equal(1, fires);
    }

    [Fact]
    public void RaiseAfterQueuedFlagClears_FiresASecondTimeNeverTwelve()
    {
        var coalescer = new UiRefreshCoalescer();
        int fires = 0;
        var posted = new List<Action>();

        void Raise() => coalescer.Request(action => posted.Add(action), () =>
        {
            fires++;
            if (fires == 1)
            {
                for (int i = 0; i < 12; i++) Raise();
            }
        });

        for (int i = 0; i < 12; i++) Raise();
        Assert.Single(posted);

        posted[0]();
        Assert.Equal(1, fires);
        Assert.Equal(2, posted.Count);

        posted[1]();
        Assert.Equal(2, fires);
        Assert.Equal(2, posted.Count);
    }

    [Fact]
    public void PostThrow_ReleasesQueuedFlagSoALaterRaiseCanFire()
    {
        var coalescer = new UiRefreshCoalescer();
        int fires = 0;
        Assert.Throws<InvalidOperationException>(() =>
            coalescer.Request(_ => throw new InvalidOperationException("post failed"), () => fires++));

        Action? posted = null;
        coalescer.Request(action => posted = action, () => fires++);
        Assert.NotNull(posted);
        posted();
        Assert.Equal(1, fires);
    }

    [Fact]
    public void ProgramCapabilitiesChangedHandlerUsesCoalescerAndKeepsFormGuards()
    {
        string source = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Program.cs");
        Assert.Contains("UiRefreshCoalescer _capabilitiesUiRefresh", source, StringComparison.Ordinal);

        int start = source.IndexOf("hw.CapabilitiesChanged +=", StringComparison.Ordinal);
        Assert.True(start >= 0, "CapabilitiesChanged handler missing");
        int end = source.IndexOf("settingsForm.RefreshDeviceCapabilities();",
            start + "hw.CapabilitiesChanged +=".Length, StringComparison.Ordinal);
        Assert.True(end > start, "handler terminator missing");
        string handler = source[start..end];
        Assert.Contains(".Request(", handler, StringComparison.Ordinal);
        Assert.Contains("settingsForm is not null", handler, StringComparison.Ordinal);
        Assert.Contains("!settingsForm.IsDisposed", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginInvoke(settingsForm.RefreshDeviceCapabilities)", handler, StringComparison.Ordinal);
    }
}
