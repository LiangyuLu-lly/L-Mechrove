using System.Windows.Forms;
using MechrevoLite.Hardware;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// 两处此前无覆盖的行为：
/// 1. LhmMonitor 的重开退避——它每次尝试都会去注册 LibreHardwareMonitor 的内核驱动服务，
///    非管理员运行时必然失败，必须有次数上限，否则 Overlay 常开就是无限重试。
/// 2. UiDpi 的 DPI 归一化——UI 审计能否跨机器复现完全取决于它。
/// </summary>
public class LocalMonitoringAndDpiTests
{
    // ---------- LHM 重开退避 ----------

    [Fact]
    public void ReopenDelay_GrowsExponentiallyAndStopsGrowingAtCap()
    {
        Assert.Equal(3000, LhmMonitor.ReopenDelayMs(0));
        Assert.Equal(6000, LhmMonitor.ReopenDelayMs(1));
        Assert.Equal(12000, LhmMonitor.ReopenDelayMs(2));
        Assert.Equal(24000, LhmMonitor.ReopenDelayMs(3));
        Assert.Equal(48000, LhmMonitor.ReopenDelayMs(4));
    }

    [Fact]
    public void ReopenDelay_ClampsOutOfRangeAttemptCounts()
    {
        // 负数与超界都不应产生 0 延迟（等于取消节流）或溢出。
        Assert.Equal(3000, LhmMonitor.ReopenDelayMs(-5));
        Assert.Equal(LhmMonitor.ReopenDelayMs(LhmMonitor.MaxReopenAttempts - 1),
            LhmMonitor.ReopenDelayMs(LhmMonitor.MaxReopenAttempts + 50));
    }

    [Fact]
    public void ShouldAttemptReopen_WaitsForTheBackoffWindow()
    {
        Assert.False(LhmMonitor.ShouldAttemptReopen(attempts: 0, gaveUp: false, sinceLastAttemptMs: 2999));
        Assert.True(LhmMonitor.ShouldAttemptReopen(attempts: 0, gaveUp: false, sinceLastAttemptMs: 3000));

        // 第二次尝试要等更久，不能仍然用 3 秒。
        Assert.False(LhmMonitor.ShouldAttemptReopen(attempts: 1, gaveUp: false, sinceLastAttemptMs: 3000));
        Assert.True(LhmMonitor.ShouldAttemptReopen(attempts: 1, gaveUp: false, sinceLastAttemptMs: 6000));
    }

    [Fact]
    public void ShouldAttemptReopen_StopsAtTheAttemptCap()
    {
        Assert.True(LhmMonitor.ShouldAttemptReopen(
            attempts: LhmMonitor.MaxReopenAttempts - 1, gaveUp: false, sinceLastAttemptMs: long.MaxValue / 2));
        Assert.False(LhmMonitor.ShouldAttemptReopen(
            attempts: LhmMonitor.MaxReopenAttempts, gaveUp: false, sinceLastAttemptMs: long.MaxValue / 2));
    }

    [Fact]
    public void ShouldAttemptReopen_NeverRetriesAfterGivingUp()
    {
        Assert.False(LhmMonitor.ShouldAttemptReopen(attempts: 0, gaveUp: true, sinceLastAttemptMs: long.MaxValue / 2));
    }

    [Fact]
    public void ReopenBudget_IsBoundedInTotalWallClockTime()
    {
        // 整个会话最多等这么久就彻底放弃，不会无限期地反复尝试注册内核驱动。
        long total = 0;
        for (int i = 0; i < LhmMonitor.MaxReopenAttempts; i++) total += LhmMonitor.ReopenDelayMs(i);
        Assert.Equal(93000, total);
    }

    // ---------- UiDpi：布局与绘制必须分开 ----------

    [Fact]
    public void Layout_IgnoresAuditOverrideSoRelativeScalingKeepsWorking()
    {
        // 这是本次修复里最关键的一条不变式。
        // 审计用 form.Scale(viewportDpi / hostDpi) 做**相对**缩放，布局必须在真实宿主 DPI
        // 下完成。若布局也读被覆盖的视口 DPI，同一比例会被乘两遍，几何全错——
        // 实测表现为 731 个 text-clipping / parent-overflow 误报。
        using var control = new Control();
        int real = UiDpi.Layout(control);

        using var _ = new AuditDpiScope(real == 192 ? 96 : 192);
        Assert.Equal(real, UiDpi.Layout(control));
    }

    [Fact]
    public void LayoutScale_IgnoresAuditOverrideAndNeverGoesBelowOne()
    {
        using var control = new Control();
        float real = UiDpi.LayoutScale(control);

        using var _ = new AuditDpiScope(48);   // 低于基线的荒谬值
        Assert.Equal(real, UiDpi.LayoutScale(control));
        Assert.True(UiDpi.LayoutScale(control) >= 1F);
    }

    [Fact]
    public void Paint_Control_PinsToAuditViewportDpi()
    {
        // 绘制发生在 form.Scale 之后，而 WinForms 不会因为 Control.Scale 改变上报的 DPI。
        // 固定到视口 DPI 之后，圆角与描边才不再随运行审计的机器变化。
        using var control = new Control();
        using var _ = new AuditDpiScope(192);
        Assert.Equal(192F, UiDpi.Paint(control));
    }

    [Fact]
    public void Paint_ReturnsToRealDpiAfterAuditOverrideIsCleared()
    {
        using var control = new Control();
        using (var _ = new AuditDpiScope(240)) Assert.Equal(240F, UiDpi.Paint(control));
        Assert.Equal(UiDpi.Layout(control), UiDpi.Paint(control));
    }

    [Fact]
    public void Paint_Graphics_PinsToAuditViewportDpi()
    {
        using var control = new Control();
        using var graphics = control.CreateGraphics();

        using (var _ = new AuditDpiScope(120)) Assert.Equal(120F, UiDpi.Paint(graphics));
        using (var _ = new AuditDpiScope(192)) Assert.Equal(192F, UiDpi.Paint(graphics));
    }

    [Fact]
    public void Paint_FloorsAtBaselineWhenNotAuditing()
    {
        using var control = new Control();
        using var _ = new AuditDpiScope(0);
        Assert.True(UiDpi.Paint(control) >= UiDpi.Baseline);
    }

    [Fact]
    public void AuditOverride_IsIgnoredWhenNotPositive()
    {
        using var control = new Control();
        float real = UiDpi.Paint(control);

        using (var _ = new AuditDpiScope(0)) Assert.Equal(real, UiDpi.Paint(control));
        using (var _ = new AuditDpiScope(-96)) Assert.Equal(real, UiDpi.Paint(control));
    }

    /// <summary>确保测试之间不会泄漏 AuditDpi 覆盖。</summary>
    private sealed class AuditDpiScope : IDisposable
    {
        private readonly int _previous;

        public AuditDpiScope(int dpi)
        {
            _previous = UiDpi.AuditDpi;
            UiDpi.AuditDpi = dpi;
        }

        public void Dispose() => UiDpi.AuditDpi = _previous;
    }
}
