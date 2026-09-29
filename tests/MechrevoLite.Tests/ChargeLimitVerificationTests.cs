using System.Windows.Forms;
using MechrevoLite.Battery;
using MechrevoLite.Hardware;
using MechrevoLite.Helpers;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// 充电上限的「效果取证」契约。
///
/// 旧设计把滑条永久禁用（因为寄存器回读一致不能证明充电受控），结果是一张点不动的死卡片。
/// 现在的设计：本机提供（服务 + 驱动 + 未判无效）就可以设置；写入回读一致后记下上限并提示
/// 「等待确认」；之后由 Windows 报告的真实充电状态给出结论——
/// 插电、电量到达上限后停止充电 = 已确认生效；插电、电量高于上限仍持续充电 = 无效，
/// 自动恢复满充并在本机关闭入口。
/// </summary>
public class ChargeLimitVerificationTests
{
    static readonly DateTime T0 = new(2026, 9, 28, 12, 0, 0);

    [Fact]
    public void TheGateNoLongerTakesAnUnusedFeatureMatrix()
    {
        string source = ChargeLimitGatingTests.SourceFile("Hardware", "EcChargeLimit.cs");
        Assert.DoesNotContain("IsSupportedMachine(SupportDecision support, FeatureMatrix matrix)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadbackProvesChargingStopped", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HeldAtTheLimitWhilePluggedIn_IsVerifiedAfterThreeSpacedSamples()
    {
        var evidence = new ChargeLimitEvidence();
        Assert.Null(evidence.Observe(80, onAc: true, charging: false, percent: 80, T0));
        Assert.Null(evidence.Observe(80, onAc: true, charging: false, percent: 80, T0.AddSeconds(5)));   // 间隔不足，不计
        Assert.Null(evidence.Observe(80, onAc: true, charging: false, percent: 80, T0.AddSeconds(21)));
        Assert.Equal(ChargeLimitVerdict.Verified,
            evidence.Observe(80, onAc: true, charging: false, percent: 79, T0.AddSeconds(42)));
    }

    [Fact]
    public void ChargingAboveTheLimitForThreeMinutes_IsIneffective()
    {
        var evidence = new ChargeLimitEvidence();
        Assert.Null(evidence.Observe(80, onAc: true, charging: true, percent: 83, T0));
        Assert.Null(evidence.Observe(80, onAc: true, charging: true, percent: 84, T0.AddMinutes(2)));
        Assert.Equal(ChargeLimitVerdict.Ineffective,
            evidence.Observe(80, onAc: true, charging: true, percent: 86, T0.AddMinutes(3)));
    }

    [Fact]
    public void AFullBatteryThatStoppedCharging_IsNotEvidence()
    {
        // 电池充满本来就会停充：98% 以上不能证明上限起了作用。
        var evidence = new ChargeLimitEvidence();
        for (int i = 0; i < 10; i++)
            Assert.Null(evidence.Observe(80, onAc: true, charging: false, percent: 100, T0.AddSeconds(30 * i)));
    }

    [Fact]
    public void UnpluggedOrFullLimit_ProducesNoVerdict_and_ResetsTheClock()
    {
        var evidence = new ChargeLimitEvidence();
        Assert.Null(evidence.Observe(80, onAc: true, charging: true, percent: 90, T0));
        Assert.Null(evidence.Observe(80, onAc: false, charging: false, percent: 90, T0.AddMinutes(2)));   // 拔电：清零
        Assert.Null(evidence.Observe(80, onAc: true, charging: true, percent: 90, T0.AddMinutes(4)));
        Assert.Null(evidence.Observe(80, onAc: true, charging: true, percent: 90, T0.AddMinutes(6)));     // 距重新计时只有 2 分钟
        Assert.Null(evidence.Observe(100, onAc: true, charging: true, percent: 90, T0.AddMinutes(20)));  // 满充档无可证
    }

    [Fact]
    public void NormalChargingBelowTheLimit_IsNotEvidence()
    {
        var evidence = new ChargeLimitEvidence();
        for (int i = 0; i < 20; i++)
            Assert.Null(evidence.Observe(80, onAc: true, charging: true, percent: 50 + i, T0.AddMinutes(i)));
    }

    [Fact]
    public void AnIneffectiveVerdict_RestoresFullCharge_and_ClosesTheChannel()
    {
        using var force = ChargeLimitGatingTests.Force(null);
        using var driver = ChargeLimitGatingTests.Driver(present: true);
        var writes = new List<int>();
        var previousSet = EcChargeLimit.TrySetOverride;
        var previousState = ChargeLimitMonitor.ChargeStateOverride;
        (bool, bool, int)? state = (true, true, 90);
        EcChargeLimit.TrySetOverride = percent => { writes.Add(percent); return (true, percent); };
        ChargeLimitMonitor.ChargeStateOverride = () => state;
        ChargeLimitVerdict? announced = null;
        void OnVerdict(ChargeLimitVerdict v) => announced = v;
        ChargeLimitMonitor.VerdictChanged += OnVerdict;
        try
        {
            BatteryControl.ResetForTests();
            ChargeLimitMonitor.Arm(80);
            ChargeLimitMonitor.Tick(T0);
            ChargeLimitMonitor.Tick(T0.AddMinutes(1));
            ChargeLimitMonitor.Tick(T0.AddMinutes(2));
            Assert.Null(announced);
            ChargeLimitMonitor.Tick(T0.AddMinutes(3).AddSeconds(5));

            Assert.Equal(ChargeLimitVerdict.Ineffective, announced);
            Assert.Equal(ChargeLimitVerdict.Ineffective, EcChargeLimit.Verdict);
            Assert.Equal(new[] { EcChargeLimit.MaximumPercent }, writes);
            Assert.Equal(EcChargeLimit.MaximumPercent, AppConfig.Get("charge_limit"));
            Assert.False(EcChargeLimit.IsSupportedMachine(ChargeLimitGatingTests.Supported));
        }
        finally
        {
            ChargeLimitMonitor.VerdictChanged -= OnVerdict;
            EcChargeLimit.TrySetOverride = previousSet;
            ChargeLimitMonitor.ChargeStateOverride = previousState;
            BatteryControl.ResetForTests();
            AppConfig.Remove("charge_limit");
        }
    }

    [Fact]
    public void AVerifiedVerdict_IsPersisted_and_Announced()
    {
        using var force = ChargeLimitGatingTests.Force(null);
        using var driver = ChargeLimitGatingTests.Driver(present: true);
        var previousState = ChargeLimitMonitor.ChargeStateOverride;
        ChargeLimitMonitor.ChargeStateOverride = () => (true, false, 80);
        ChargeLimitVerdict? announced = null;
        void OnVerdict(ChargeLimitVerdict v) => announced = v;
        ChargeLimitMonitor.VerdictChanged += OnVerdict;
        try
        {
            BatteryControl.ResetForTests();
            ChargeLimitMonitor.Arm(80);
            ChargeLimitMonitor.Tick(T0);
            ChargeLimitMonitor.Tick(T0.AddSeconds(25));
            ChargeLimitMonitor.Tick(T0.AddSeconds(50));
            Assert.Equal(ChargeLimitVerdict.Verified, announced);
            Assert.Equal(ChargeLimitVerdict.Verified, EcChargeLimit.Verdict);
            Assert.True(EcChargeLimit.IsSupportedMachine(ChargeLimitGatingTests.Supported));
        }
        finally
        {
            ChargeLimitMonitor.VerdictChanged -= OnVerdict;
            ChargeLimitMonitor.ChargeStateOverride = previousState;
            BatteryControl.ResetForTests();
        }
    }

    [Fact]
    public void AnEchoedWrite_IsShownAsAPercent_and_AnnouncedAsPending()
    {
        using var failures = new FailureProbe();
        using var notices = new NoticeProbe();
        using var harness = new EchoHarness();
        bool previousAudit = Program.UiAuditMode;
        SettingsForm? previousForm = Program.settingsForm;
        Program.UiAuditMode = true;
        AppConfig.Remove("charge_limit");
        using var form = new SettingsForm { ClientSize = SettingsForm.CompactDashboardLogicalClientSize };
        form.CreateControl();
        form.Show();
        Program.settingsForm = form;
        try
        {
            Label value = form.Controls.Find("labelBatteryLimitValue", true).OfType<Label>().Single();
            RSlider slider = form.Controls.Find("sliderBattery", true).OfType<RSlider>().Single();
            Assert.True(slider.Enabled, "the channel is offered; the effect is proven afterwards.");

            Assert.True(BatteryControl.SetBatteryChargeLimit(80));
            harness.WaitUntil(() =>
            {
                Application.DoEvents();
                return value.Text == "80%" && notices.Messages.Count > 0;
            });

            Assert.Empty(failures.Messages);
            Assert.Equal(EcChargeLimit.PendingNotice, Assert.Single(notices.Messages));
            Assert.Equal(80, AppConfig.Get("charge_limit"));
            Assert.Equal("80%", value.Text);
            Assert.Equal(80, slider.Value);
            Assert.Equal(EcChargeLimit.PendingNotice, form.ChargeLimitSliderReason);
        }
        finally
        {
            Program.settingsForm = previousForm!;
            Program.UiAuditMode = previousAudit;
            for (int i = 0; i < 20; i++) Application.DoEvents();
        }
    }

    [Fact]
    public void AFullChargeRequest_IsCommittedAfterTheEcho()
    {
        using var failures = new FailureProbe();
        using var notices = new NoticeProbe();
        using var harness = new EchoHarness();
        bool previousAudit = Program.UiAuditMode;
        SettingsForm? previousForm = Program.settingsForm;
        bool previousFull = BatteryControl.chargeFull;
        Program.UiAuditMode = true;
        AppConfig.Remove("charge_limit");
        BatteryControl.chargeFull = false;
        using var form = new SettingsForm { ClientSize = SettingsForm.CompactDashboardLogicalClientSize };
        form.CreateControl();
        form.Show();
        Program.settingsForm = form;
        try
        {
            Label value = form.Controls.Find("labelBatteryLimitValue", true).OfType<Label>().Single();
            BatteryControl.SetBatteryLimitFull();
            harness.WaitUntil(() =>
            {
                Application.DoEvents();
                return AppConfig.Get("charge_limit") == 100 && value.Text == "100%";
            });

            Assert.True(BatteryControl.chargeFull);
            Assert.Equal(100, AppConfig.Get("charge_limit"));
            Assert.Equal("100%", value.Text);
            Assert.Empty(failures.Messages);
            Assert.Empty(notices.Messages);   // 满充档没有需要取证的上限
        }
        finally
        {
            Program.settingsForm = previousForm!;
            Program.UiAuditMode = previousAudit;
            BatteryControl.chargeFull = previousFull;
            for (int i = 0; i < 20; i++) Application.DoEvents();
        }
    }

    sealed class FailureProbe : IDisposable
    {
        readonly Action<string>? _previous = ToastForm.FailureCallback;
        public List<string> Messages { get; } = new();
        public FailureProbe() => ToastForm.FailureCallback = Messages.Add;
        public void Dispose() => ToastForm.FailureCallback = _previous;
    }

    sealed class NoticeProbe : IDisposable
    {
        readonly Action<string>? _previous = ToastForm.NoticeCallback;
        public List<string> Messages { get; } = new();
        public NoticeProbe() => ToastForm.NoticeCallback = Messages.Add;
        public void Dispose() => ToastForm.NoticeCallback = _previous;
    }

    sealed class EchoHarness : IDisposable
    {
        readonly Func<int, (bool Success, int AppliedPercent)>? _previousTrySet = EcChargeLimit.TrySetOverride;
        readonly Func<int>? _previousRead = EcChargeLimit.ReadPercentOverride;
        readonly string? _previousForce = AppConfig.GetString("ec_charge_limit");
        int _ec = -1;

        public EchoHarness()
        {
            AppConfig.Set("ec_charge_limit", "1");
            BatteryControl.ResetForTests();
            EcChargeLimit.TrySetOverride = percent => { _ec = percent; return (true, percent); };
            EcChargeLimit.ReadPercentOverride = () => _ec;
        }

        public void WaitUntil(Func<bool> condition, int ms = 3000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms && !condition()) Thread.Sleep(10);
        }

        public void Dispose()
        {
            EcChargeLimit.TrySetOverride = _previousTrySet;
            EcChargeLimit.ReadPercentOverride = _previousRead;
            BatteryControl.ResetForTests();
            if (_previousForce is null) AppConfig.Remove("ec_charge_limit");
            else AppConfig.Set("ec_charge_limit", _previousForce);
        }
    }
}
