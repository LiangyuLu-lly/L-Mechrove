using MechrevoLite.Battery;
using MechrevoLite.Hardware;
using MechrevoLite.Helpers;
using MechrevoLite.UI;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// 充电上限显示真源修复（run5 final）。
///
/// 用户报告：上限在硬件上生效了，但（a）切换后数值会回退/变回，（b）界面不再显示数字，只显示「—」。
///
/// 根因：
/// <list type="number">
/// <item>周期刷新（<c>_quickSwitchStatusTimer</c>，3s）此前只渲染 <c>AppConfig.Get("charge_limit")</c>：
///   配置缺失时它就是 -1 哨兵 → 显示 <c>—</c>，把任何刚显示出来、尚未落配置的数值覆盖回去。</item>
/// <item>显示从不读 EC 的实际阈值（<c>EcChargeLimit.ReadPercent()</c> 全仓零调用）——硬件明明有限制值，
///   界面却因为「配置里没有」而显示未知。</item>
/// <item>写入当刻的回读可能因 EC 总线时序失败 → 不落配置、不改界面，但硬件其实已改。</item>
/// </list>
///
/// 修复：显示真源改为「EC 实际阈值优先，其次持久化值，两者都不可信才未知」；周期刷新走这条真源；
/// 写入回读失败时再读一次 EC，若已是请求值就按成功持久化 + 显示。
/// </summary>
public class BatteryChargeLimitDisplayFixTests
{
    sealed class Harness : IDisposable
    {
        readonly Func<TimeSpan>? _previousIdle;
        readonly Func<int, (bool Success, int AppliedPercent)>? _previousTrySet;
        readonly Func<int>? _previousRead;
        readonly string? _previousForce;
        readonly string? _previousStored;
        readonly bool _previousAudit;

        public List<int> Writes { get; } = new();
        public TimeSpan Idle { get; set; }

        /// <summary>TrySet 接缝是否成功。false 时模拟「写后回读失败」。</summary>
        public bool TrySetSucceeds { get; set; } = true;

        /// <summary>ReadPercent 接缝报告的实际阈值（-1 = 读不到）。写入成功时同步为写入值。</summary>
        public int EcPercent { get; set; } = -1;

        public Harness(TimeSpan idle)
        {
            _previousIdle = NativeMethods.IdleTimeProvider;
            _previousTrySet = EcChargeLimit.TrySetOverride;
            _previousRead = EcChargeLimit.ReadPercentOverride;
            _previousForce = AppConfig.GetString("ec_charge_limit");
            _previousStored = AppConfig.GetString("charge_limit");
            _previousAudit = Program.UiAuditMode;

            Program.UiAuditMode = true;              // 不启动 3s 周期定时器（测试不泵消息）
            BatteryControl.ResetChargeLimitGesture();
            AppConfig.Set("ec_charge_limit", "1");   // 强制走已实测机型通道（CI 不一定是 YAOSHI）
            AppConfig.Remove("charge_limit");        // 模拟用户从未成功写入过

            Idle = idle;
            NativeMethods.IdleTimeProvider = () => Idle;
            EcChargeLimit.TrySetOverride = percent =>
            {
                Writes.Add(percent);
                if (!TrySetSucceeds) return (false, -1);
                EcPercent = percent;
                return (true, percent);
            };
            EcChargeLimit.ReadPercentOverride = () => EcPercent;
        }

        public void WaitForWrite(int ms = 3000)
        {
            WaitUntil(() => Writes.Count > 0, ms);
        }

        public void WaitForStored(int expected, int ms = 3000)
        {
            WaitUntil(() => AppConfig.Get("charge_limit") == expected, ms);
        }

        static void WaitUntil(Func<bool> condition, int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms && !condition()) Thread.Sleep(10);
        }

        public void Dispose()
        {
            EcChargeLimit.TrySetOverride = _previousTrySet;
            EcChargeLimit.ReadPercentOverride = _previousRead;
            NativeMethods.IdleTimeProvider = _previousIdle;
            BatteryControl.ResetChargeLimitGesture();
            Program.UiAuditMode = _previousAudit;
            if (_previousForce is null) AppConfig.Remove("ec_charge_limit");
            else AppConfig.Set("ec_charge_limit", _previousForce);
            if (_previousStored is null) AppConfig.Remove("charge_limit");
            else AppConfig.Set("charge_limit", _previousStored);
        }
    }

    static Label LimitValue(SettingsForm form) =>
        form.Controls.Find("labelBatteryLimitValue", true).OfType<Label>().Single();

    /// <summary>真实手势设置 → EC 写入 → 持久化 → 显示数值（不再显示「—」）。</summary>
    [Fact]
    public async Task GenuineGesture_ReachesTheWrite_and_DoesNotPersistAConfirmedLimit()
    {
        using var h = new Harness(TimeSpan.Zero);
        using var form = new SettingsForm();
        form.CreateControl();

        Assert.True(BatteryControl.CaptureChargeLimitGesture(), "新鲜输入下应捕获到手势凭证。");
        h.Idle = TimeSpan.FromSeconds(3);   // 去抖计时器 + EC 写入把 500ms 窗口烧掉
        await Task.Delay(250);

        Assert.True(BatteryControl.ApplyChargeLimitFromUserGesture(78));
        h.WaitForWrite();
        Thread.Sleep(150);

        Assert.Equal(new[] { 78 }, h.Writes);
        Assert.NotEqual(78, AppConfig.Get("charge_limit"));
        Assert.False(BatteryControl.chargeFull);
        form.RefreshDeviceCapabilities();
        Assert.Equal(EcChargeLimit.UnverifiedLimitLabel, LimitValue(form).Text);
        Assert.NotEqual("78%", LimitValue(form).Text);
    }

    /// <summary>连续多次刷新回显不得把刚设的值回退——EC 真值优先于（缺失/陈旧的）配置。</summary>
    [Fact]
    public void RepeatedRefreshTicks_DoNotRevertTheValue()
    {
        using var h = new Harness(TimeSpan.FromSeconds(5));
        h.EcPercent = 66;                    // 硬件实际阈值
        using var form = new SettingsForm();
        form.CreateControl();
        Label value = LimitValue(form);

        Assert.Equal(-1, AppConfig.Get("charge_limit"));   // 配置里没有（写入未落配置的极端）
        for (int tick = 0; tick < 5; tick++)
        {
            form.VisualiseBatteryTitle(BatteryControl.ResolveDisplayLimitPercent());
            Assert.Equal("66%", value.Text);
        }

        // 即便配置里有一个陈旧值，也应回显 EC 真值而不是被配置覆盖。
        AppConfig.Set("charge_limit", 92);
        for (int tick = 0; tick < 5; tick++)
        {
            form.VisualiseBatteryTitle(BatteryControl.ResolveDisplayLimitPercent());
            Assert.Equal("66%", value.Text);
        }
        Assert.Equal(66, BatteryControl.ResolveDisplayLimitPercent());
    }

    /// <summary>「—」只在 EC 与持久化值都不可信时出现，绝不作为「只是没存下来」的替身。</summary>
    [Fact]
    public void Unknown_OnlyWhenNeitherEcNorStoredIsKnown()
    {
        using var h = new Harness(TimeSpan.FromSeconds(5));
        using var form = new SettingsForm();
        form.CreateControl();
        Label value = LimitValue(form);

        // EC 读不到 + 配置缺失 → 真未知。
        h.EcPercent = -1;
        Assert.Equal(-1, BatteryControl.ResolveDisplayLimitPercent());
        form.VisualiseBatteryTitle(BatteryControl.ResolveDisplayLimitPercent());
        Assert.Equal(SettingsForm.BatteryLimitUnknownText, value.Text);
        Assert.DoesNotContain("-1", value.Text);

        // EC 读不到但有持久化值 → 回退到持久化值，不是未知。
        AppConfig.Set("charge_limit", 73);
        form.VisualiseBatteryTitle(BatteryControl.ResolveDisplayLimitPercent());
        Assert.Equal("73%", value.Text);

        // EC 可读但配置缺失 → EC 真值胜出，不是未知。
        AppConfig.Remove("charge_limit");
        h.EcPercent = 88;
        form.VisualiseBatteryTitle(BatteryControl.ResolveDisplayLimitPercent());
        Assert.Equal("88%", value.Text);
        Assert.DoesNotContain("-1", value.Text);
    }

    /// <summary>
    /// 写后即时回读失败（硬件其实已改）时：不应停留在未知，而应再次读 EC 确认实际阈值并持久化 + 显示。
    /// </summary>
    [Fact]
    public void ImmediateReadbackFailure_EvenWhenEcMatches_IsNotPersisted()
    {
        using var h = new Harness(TimeSpan.FromSeconds(5));
        h.TrySetSucceeds = false;   // TrySet 报告失败
        h.EcPercent = 75;           // 但 EC 实际阈值已是 75（写落硬件，回读时序没跟上）

        Assert.True(BatteryControl.SetBatteryChargeLimit(75));   // 重新应用已保存值是程序化路径，不受手势门禁
        h.WaitForWrite();
        Thread.Sleep(150);

        Assert.Equal(-1, AppConfig.Get("charge_limit"));
        Assert.False(BatteryControl.chargeFull);
        Assert.False(EcChargeLimit.ReadbackProvesChargingStopped);
    }

    /// <summary>写后即时回读失败且 EC 阈值也不是请求值 → 保持原值，不伪造成功。</summary>
    [Fact]
    public void FailedWrite_WithDifferentEcValue_IsNotPersisted()
    {
        using var h = new Harness(TimeSpan.FromSeconds(5));
        h.TrySetSucceeds = false;
        h.EcPercent = 60;   // EC 真实阈值不是请求的 75

        Assert.True(BatteryControl.SetBatteryChargeLimit(75));
        h.WaitForWrite();
        Thread.Sleep(150);

        Assert.Equal(-1, AppConfig.Get("charge_limit"));                 // 未持久化
        Assert.Equal(60, BatteryControl.ResolveDisplayLimitPercent());   // 显示仍是 EC 真值
    }

    /// <summary>机型未验证时不读 EC（寄存器布局未知），退回持久化值。</summary>
    [Fact]
    public void UnverifiedMachine_DoesNotReadEc_FallsBackToStored()
    {
        using var h = new Harness(TimeSpan.FromSeconds(5));
        string? previous = AppConfig.GetString("ec_charge_limit");
        AppConfig.Set("ec_charge_limit", "0");   // 禁用 EC 通道
        try
        {
            h.EcPercent = 80;   // 即便接缝会给值，也不该被读取
            AppConfig.Set("charge_limit", 73);
            Assert.Equal(73, BatteryControl.ResolveDisplayLimitPercent());

            using var form = new SettingsForm();
            form.CreateControl();
            form.VisualiseBatteryTitle(BatteryControl.ResolveDisplayLimitPercent());
            Assert.Equal("73%", LimitValue(form).Text);
        }
        finally
        {
            if (previous is null) AppConfig.Remove("ec_charge_limit");
            else AppConfig.Set("ec_charge_limit", previous);
        }
    }
}
