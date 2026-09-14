using MechrevoLite.Battery;
using MechrevoLite.Hardware;
using MechrevoLite.Helpers;
using MechrevoLite.UI;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// 充电上限「电池限制电量」热修（run5 battery hotfix）。
///
/// 用户报告：电池限制电量显示成 <c>-1</c> 且怎么都设不上，重启后依旧。
///
/// 根因（file:line 见 artifacts\run5-battery-hotfix\README.md）：
/// <list type="number">
/// <item>读数显示的是 <c>AppConfig.Get("charge_limit")</c> 的**缺失键哨兵 -1**（AppConfig.cs:337），
///   根本不是硬件回读（<c>EcChargeLimit.ReadPercent()</c> 全仓零调用）。<c>VisualiseBatteryTitle</c>
///   原样把它写成 "-1%"，且 <c>_quickSwitchStatusTimer</c>（3s）反复回显，盖掉任何瞬时修正。</item>
/// <item>写入路径在区间外（含 -1 哨兵）/机型不支持时**静默 return**（BatteryControl.cs:89），
///   既不落配置也不修正界面 → 用户看到的就是永远 -1%。</item>
/// <item>滑条设置此前**没有任何用户手势守卫**：既做不到「真实用户必定生效」，也挡不住
///   UIA 拖拽/程序化写 Value 改写 EC。</item>
/// </list>
///
/// 修复复用显卡模式热修确立的「一次性用户手势凭证」机制（<see cref="UserGestureLease"/>，
/// 与 <see cref="SystemRestart"/> 的确认凭证同源）：真实滑条手势当刻捕获，动作消费一次；
/// 凭证只在新鲜真实输入下可捕获，程序化/自动化拿不到。同时未知上限一律显示为未知占位。
/// </summary>
public class BatteryChargeLimitHotfixTests
{
    sealed class Harness : IDisposable
    {
        readonly Func<TimeSpan>? _previousIdle;
        readonly Func<int, (bool Success, int AppliedPercent)>? _previousSeam;
        readonly string? _previousForce;
        readonly string? _previousStored;

        public List<int> Writes { get; } = new();
        public TimeSpan Idle { get; set; }

        public Harness(TimeSpan idle)
        {
            _previousIdle = NativeMethods.IdleTimeProvider;
            _previousSeam = EcChargeLimit.TrySetOverride;
            _previousForce = AppConfig.GetString("ec_charge_limit");
            _previousStored = AppConfig.GetString("charge_limit");

            BatteryControl.ResetChargeLimitGesture();
            AppConfig.Set("ec_charge_limit", "1");   // 强制走已实测机型通道（CI 不一定是 YAOSHI）
            AppConfig.Remove("charge_limit");        // 模拟用户从未成功写入过

            Idle = idle;
            NativeMethods.IdleTimeProvider = () => Idle;
            EcChargeLimit.TrySetOverride = percent => { Writes.Add(percent); return (true, percent); };
        }

        public bool WaitForWrite(int ms = 3000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms && Writes.Count == 0) Thread.Sleep(10);
            return Writes.Count > 0;
        }

        public void Dispose()
        {
            EcChargeLimit.TrySetOverride = _previousSeam;
            NativeMethods.IdleTimeProvider = _previousIdle;
            BatteryControl.ResetChargeLimitGesture();
            if (_previousForce is null) AppConfig.Remove("ec_charge_limit");
            else AppConfig.Set("ec_charge_limit", _previousForce);
            if (_previousStored is null) AppConfig.Remove("charge_limit");
            else AppConfig.Set("charge_limit", _previousStored);
        }
    }

    /// <summary>
    /// 真实手势捕获凭证后，即便去抖计时器/硬件耗时把 500ms 窗口烧掉（陈旧输入），写入照样生效。
    /// 这正是旧实现里「设了不生效」的镜像：守卫若在动作当刻才判新鲜度，用户永远设不上。
    /// </summary>
    [Fact]
    public async Task GenuineSliderGesture_AppliesTheLimit_EvenAfterTheFreshWindowIsBurned()
    {
        using var h = new Harness(TimeSpan.Zero);
        Assert.True(BatteryControl.CaptureChargeLimitGesture(), "新鲜输入下应捕获到手势凭证。");

        h.Idle = TimeSpan.FromSeconds(3);   // 去抖计时器 + EC 写入的耗时：500ms 窗口已过期
        await Task.Delay(250);

        Assert.True(BatteryControl.ApplyChargeLimitFromUserGesture(80));
        Assert.True(h.WaitForWrite(), "真实手势的写入必须到达硬件接缝。");
        Assert.Equal(new[] { 80 }, h.Writes);
    }

    /// <summary>程序化设置（无手势凭证）必须被拒，绝不写硬件。</summary>
    [Fact]
    public void ProgrammaticSet_WithoutGesture_IsRefused()
    {
        using var h = new Harness(TimeSpan.FromSeconds(5));

        Assert.False(BatteryControl.ApplyChargeLimitFromUserGesture(80));
        Assert.False(h.WaitForWrite(300));
        Assert.Empty(h.Writes);
    }

    /// <summary>陈旧输入拿不到凭证，随后的设置同样被拒。</summary>
    [Fact]
    public void StaleInput_CannotCaptureTheGesture_AndTheSetIsRefused()
    {
        using var h = new Harness(TimeSpan.FromSeconds(5));

        Assert.False(BatteryControl.CaptureChargeLimitGesture());
        Assert.False(BatteryControl.ApplyChargeLimitFromUserGesture(70));
        Assert.Empty(h.Writes);
    }

    /// <summary>凭证一次性：消费后不能再放行第二次设置。</summary>
    [Fact]
    public void GestureLease_IsConsumedOnce()
    {
        using var h = new Harness(TimeSpan.Zero);
        Assert.True(BatteryControl.CaptureChargeLimitGesture());

        h.Idle = TimeSpan.FromSeconds(3);
        Assert.True(BatteryControl.ApplyChargeLimitFromUserGesture(80));
        Assert.True(h.WaitForWrite());
        Assert.False(BatteryControl.ApplyChargeLimitFromUserGesture(70));

        Assert.Single(h.Writes);
        Assert.Equal(80, h.Writes[0]);
    }

    /// <summary>
    /// 明确区分两种调用：**重新应用已持久化的值**（开机/唤醒/电源切换）是程序化的、安全的，
    /// 不受手势守卫约束；只有「新设置一个值」才需要真实手势。否则重启后就不会重新限充了。
    /// </summary>
    [Fact]
    public void PersistedLimit_ProgrammaticReapply_IsNotGestureGated()
    {
        using var h = new Harness(TimeSpan.FromSeconds(5));
        AppConfig.Set("charge_limit", 92);

        Assert.True(BatteryControl.SetBatteryChargeLimit());
        Assert.True(h.WaitForWrite(), "重新应用已保存的上限必须到达硬件接缝。");
        Assert.Equal(new[] { 92 }, h.Writes);
    }

    /// <summary>
    /// 未知上限必须诚实显示：绝不能把 -1 哨兵当「-1%」显示，也不能静默钳成滑条下限（40）冒充上限。
    /// </summary>
    [Fact]
    public void UnknownStoredLimit_IsShownAsUnknown_NotMinusOneOrAFakeClamp()
    {
        using var h = new Harness(TimeSpan.Zero);
        Assert.Equal(-1, AppConfig.Get("charge_limit"));   // 缺失键哨兵

        using var form = new SettingsForm();
        form.CreateControl();
        var value = form.Controls.Find("labelBatteryLimitValue", true).OfType<Label>().Single();
        var slider = form.Controls.Find("sliderBattery", true).OfType<RSlider>().Single();
        int sliderBefore = slider.Value;

        form.VisualiseBatteryTitle(AppConfig.Get("charge_limit"));
        Assert.Equal(SettingsForm.BatteryLimitUnknownText, value.Text);
        Assert.DoesNotContain("-1", value.Text);

        form.VisualiseBattery(-1);
        Assert.Equal(SettingsForm.BatteryLimitUnknownText, value.Text);
        Assert.Equal(sliderBefore, slider.Value);   // 未知不搬动滑条，更不钳成 40
    }

    /// <summary>已知上限的显示契约不变。</summary>
    [Fact]
    public void KnownLimit_StillRendersThePercent()
    {
        using var h = new Harness(TimeSpan.Zero);
        using var form = new SettingsForm();
        form.CreateControl();
        var value = form.Controls.Find("labelBatteryLimitValue", true).OfType<Label>().Single();

        form.VisualiseBatteryTitle(73);
        Assert.Equal("73%", value.Text);

        form.VisualiseBattery(88);
        Assert.Equal("88%", value.Text);
    }
}
