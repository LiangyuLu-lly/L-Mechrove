using System.Windows.Forms;
using MechrevoLite.Battery;
using MechrevoLite.Hardware;
using MechrevoLite.Helpers;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// The charge-limit gate accepted a <see cref="FeatureMatrix"/> and never read it, and a matching
/// readback was presented as proof the limit took effect. There is no charge-limit capability bit
/// (vendor BatteryProtection2 has no ItemSupport flag). Service-served must not open the channel,
/// and an echo must not be persisted or painted as a normal active limit.
/// </summary>
public class ChargeLimitUnverifiedWriteTests
{
    [Fact]
    public void TheGateStopsAcceptingAnUnusedFeatureMatrix()
    {
        string source = ChargeLimitGatingTests.SourceFile("Hardware", "EcChargeLimit.cs");
        Assert.DoesNotContain(
            "IsSupportedMachine(SupportDecision support, FeatureMatrix matrix)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("矩阵 + F3", source, StringComparison.Ordinal);

        using var force = ChargeLimitGatingTests.Force(null);
        var served = new SupportDecision(true, SupportReason.Ok, "PH4TRX1");
        Assert.False(
            EcChargeLimit.IsSupportedMachine(served),
            "deleting the matrix parameter must not leave service-served as a charge-limit capability.");
        Assert.False(EcChargeLimit.ReadbackProvesChargingStopped);
    }

    [Fact]
    public void AForcedChannel_KeepsTheSliderDisabled_and_ShowsAPersistentUnverifiedLimit()
    {
        using var force = ChargeLimitGatingTests.Force("1");
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null!;
        try
        {
            using var form = new SettingsForm { ClientSize = SettingsForm.CompactDashboardLogicalClientSize };
            form.CreateControl();
            form.Show();
            Application.DoEvents();
            form.RefreshDeviceCapabilities();
            Application.DoEvents();

            Control slider = form.Controls.Find("sliderBattery", true).Single();
            Label value = form.Controls.Find("labelBatteryLimitValue", true).OfType<Label>().Single();
            Control full = form.Controls.Find("buttonBatteryFull", true).Single();
            Assert.False(slider.Enabled, "a forced write channel is not proof these addresses control charging.");
            Assert.False(full.Enabled, "the 100% button must not be a working control while the write is unconfirmed.");
            Assert.False(EcChargeLimit.ReadbackProvesChargingStopped);
            Assert.Equal(EcChargeLimit.UnverifiedLimitLabel, value.Text);
            Assert.DoesNotContain("%", value.Text, StringComparison.Ordinal);
            Assert.Equal(EcChargeLimit.UnverifiedWriteNotice, form.ChargeLimitSliderReason);
            Assert.Contains("不能证明", form.ChargeLimitSliderReason, StringComparison.Ordinal);
            Assert.DoesNotContain("已确认", form.ChargeLimitSliderReason, StringComparison.Ordinal);
            Assert.Equal(EcChargeLimit.UnverifiedWriteNotice, slider.AccessibleDescription);
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
        }
    }

    [Fact]
    public void AReadbackMatch_SurfacesTheUnverifiedNotice_and_LeavesTheLimitUncommitted()
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
            RSlider slider = form.Controls.Find("sliderBattery", true).OfType<RSlider>().Single();
            int sliderBefore = slider.Value;

            Assert.True(BatteryControl.SetBatteryChargeLimit(80));
            harness.WaitUntil(() =>
            {
                Application.DoEvents();
                return notices.Messages.Count > 0 || failures.Messages.Count > 0;
            });
            harness.WaitUntil(() =>
            {
                Application.DoEvents();
                return value.Text == EcChargeLimit.UnverifiedLimitLabel
                    || AppConfig.Get("charge_limit") == 80
                    || value.Text == "80%";
            });

            Assert.Empty(failures.Messages);
            Assert.Equal(EcChargeLimit.UnverifiedWriteNotice, Assert.Single(notices.Messages));
            Assert.DoesNotContain("已确认", notices.Messages[0], StringComparison.Ordinal);
            Assert.False(EcChargeLimit.ReadbackProvesChargingStopped);
            Assert.Equal(-1, AppConfig.Get("charge_limit"));
            Assert.False(BatteryControl.chargeFull);
            Assert.Equal(EcChargeLimit.UnverifiedLimitLabel, value.Text);
            Assert.NotEqual("80%", value.Text);
            Assert.Equal(sliderBefore, slider.Value);
        }
        finally
        {
            Program.settingsForm = previousForm!;
            Program.UiAuditMode = previousAudit;
            BatteryControl.chargeFull = previousFull;
            for (int i = 0; i < 20; i++) Application.DoEvents();
        }
    }

    [Fact]
    public void AFullChargeRequest_DoesNotShowOn_BeforeOrAfterAnEcho()
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
            Control full = form.Controls.Find("buttonBatteryFull", true).Single();
            Label value = form.Controls.Find("labelBatteryLimitValue", true).OfType<Label>().Single();

            BatteryControl.SetBatteryLimitFull();

            Assert.False(BatteryControl.chargeFull, "100% must not be persisted before the write is confirmed.");
            Assert.DoesNotContain("100% on", full.AccessibleName ?? "", StringComparison.Ordinal);

            harness.WaitUntil(() =>
            {
                Application.DoEvents();
                return notices.Messages.Count > 0 || failures.Messages.Count > 0 || AppConfig.Get("charge_limit") == 100;
            });

            Assert.False(BatteryControl.chargeFull);
            Assert.DoesNotContain("100% on", full.AccessibleName ?? "", StringComparison.Ordinal);
            Assert.NotEqual(100, AppConfig.Get("charge_limit"));
            Assert.Equal(EcChargeLimit.UnverifiedLimitLabel, value.Text);
            Assert.Empty(failures.Messages);
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
        readonly string? _previousForce = AppConfig.GetString("ec_charge_limit");

        public EchoHarness()
        {
            AppConfig.Set("ec_charge_limit", "1");
            EcChargeLimit.TrySetOverride = percent => (true, percent);
        }

        public void WaitUntil(Func<bool> condition, int ms = 3000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms && !condition()) Thread.Sleep(10);
        }

        public void Dispose()
        {
            EcChargeLimit.TrySetOverride = _previousTrySet;
            if (_previousForce is null) AppConfig.Remove("ec_charge_limit");
            else AppConfig.Set("ec_charge_limit", _previousForce);
        }
    }
}
