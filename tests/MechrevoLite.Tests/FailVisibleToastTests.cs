using MechrevoLite.Battery;
using MechrevoLite.Hardware;
using MechrevoLite.Helpers;
using MechrevoLite.Mode;

namespace MechrevoLite.Tests;

/// <summary>
/// 失败必须让用户看见：性能模式 / 充电上限 / 灯效 三条路径原先只写日志。
/// 对齐 GPU MQTT 切换失败的 <c>Program.toast?.RunToast</c> 一行中文。
/// 用户取消不得弹。
/// </summary>
public class FailVisibleToastTests
{
    sealed class ToastProbe : IDisposable
    {
        readonly Action<string>? _previous;

        public List<string> Messages { get; } = new();

        public ToastProbe()
        {
            _previous = ToastForm.FailureCallback;
            ToastForm.FailureCallback = Messages.Add;
        }

        public void Dispose() => ToastForm.FailureCallback = _previous;
    }

    sealed class ChargeLimitHarness : IDisposable
    {
        readonly Func<int, (bool Success, int AppliedPercent)>? _previousTrySet;
        readonly Func<int>? _previousRead;
        readonly string? _previousForce;
        readonly string? _previousStored;

        public bool TrySetSucceeds { get; set; } = true;
        public int EcPercent { get; set; } = -1;

        public ChargeLimitHarness()
        {
            _previousTrySet = EcChargeLimit.TrySetOverride;
            _previousRead = EcChargeLimit.ReadPercentOverride;
            _previousForce = AppConfig.GetString("ec_charge_limit");
            _previousStored = AppConfig.GetString("charge_limit");

            AppConfig.Set("ec_charge_limit", "1");
            AppConfig.Remove("charge_limit");
            EcChargeLimit.TrySetOverride = percent =>
            {
                if (!TrySetSucceeds) return (false, -1);
                EcPercent = percent;
                return (true, percent);
            };
            EcChargeLimit.ReadPercentOverride = () => EcPercent;
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
            if (_previousForce is null) AppConfig.Remove("ec_charge_limit");
            else AppConfig.Set("ec_charge_limit", _previousForce);
            if (_previousStored is null) AppConfig.Remove("charge_limit");
            else AppConfig.Set("charge_limit", _previousStored);
        }
    }

    [Fact]
    public void PerformanceMode_InvokesToastCallback_when_ApplyFails()
    {
        using var toast = new ToastProbe();
        ModeControl.ReportApplyFault(new InvalidOperationException("acpi"), 1);
        Assert.Equal("性能模式切换失败。", Assert.Single(toast.Messages));
    }

    [Fact]
    public void PerformanceMode_DoesNotInvokeToastCallback_when_Cancelled()
    {
        using var toast = new ToastProbe();
        ModeControl.ReportApplyFault(new OperationCanceledException(), 1);
        Assert.Empty(toast.Messages);
    }

    [Fact]
    public void PerformanceMode_DoesNotInvokeToastCallback_when_Success()
    {
        using var toast = new ToastProbe();
        ModeControl.NotifySwitchOutcome(success: true, cancelled: false);
        Assert.Empty(toast.Messages);
    }

    [Fact]
    public void ChargeLimit_InvokesToastCallback_when_WriteUnconfirmed()
    {
        using var toast = new ToastProbe();
        using var h = new ChargeLimitHarness { TrySetSucceeds = false, EcPercent = 60 };

        Assert.True(BatteryControl.SetBatteryChargeLimit(80));
        h.WaitUntil(() => toast.Messages.Count > 0);
        Assert.Equal("充电上限设置失败，已恢复原值。", Assert.Single(toast.Messages));
    }

    [Fact]
    public void ChargeLimit_Echo_IsNotConfirmation_and_IsNotPersisted()
    {
        using var toast = new ToastProbe();
        using var h = new ChargeLimitHarness { TrySetSucceeds = true };

        var notices = new List<string>();
        Action<string>? previousNotice = ToastForm.NoticeCallback;
        ToastForm.NoticeCallback = notices.Add;
        try
        {
            Assert.True(BatteryControl.SetBatteryChargeLimit(80));
            h.WaitUntil(() => notices.Count > 0 || AppConfig.Get("charge_limit") == 80);
            Thread.Sleep(50);
            Assert.Empty(toast.Messages);
            Assert.NotEqual(80, AppConfig.Get("charge_limit"));
            Assert.Equal(EcChargeLimit.UnverifiedWriteNotice, Assert.Single(notices));
        }
        finally
        {
            ToastForm.NoticeCallback = previousNotice;
        }
    }

    [Fact]
    public void ChargeLimit_DoesNotTreatADelayedEcho_AsConfirmation()
    {
        using var toast = new ToastProbe();
        using var h = new ChargeLimitHarness { TrySetSucceeds = false, EcPercent = 80 };
        var notices = new List<string>();
        Action<string>? previousNotice = ToastForm.NoticeCallback;
        ToastForm.NoticeCallback = notices.Add;
        try
        {
            Assert.True(BatteryControl.SetBatteryChargeLimit(80));
            h.WaitUntil(() => notices.Count > 0 || AppConfig.Get("charge_limit") == 80);
            Thread.Sleep(50);
            Assert.Empty(toast.Messages);
            Assert.NotEqual(80, AppConfig.Get("charge_limit"));
            Assert.Equal(EcChargeLimit.UnverifiedWriteNotice, Assert.Single(notices));
        }
        finally
        {
            ToastForm.NoticeCallback = previousNotice;
        }
    }

    [Fact]
    public void Lighting_InvokesToastCallback_when_ApplyRejected()
    {
        using var toast = new ToastProbe();
        LightForm.NotifyApplyOutcome(accepted: false, cancelled: false);
        Aura.NotifyApplyOutcome(success: false);
        Assert.Equal(new[] { "灯效设置失败。", "灯效设置失败。" }, toast.Messages);
    }

    [Fact]
    public void Lighting_DoesNotInvokeToastCallback_when_ApplyAccepted()
    {
        using var toast = new ToastProbe();
        LightForm.NotifyApplyOutcome(accepted: true, cancelled: false);
        Aura.NotifyApplyOutcome(success: true);
        Assert.Empty(toast.Messages);
    }

    [Fact]
    public void Lighting_DoesNotInvokeToastCallback_when_Cancelled()
    {
        using var toast = new ToastProbe();
        LightForm.NotifyApplyOutcome(accepted: false, cancelled: true);
        Assert.Empty(toast.Messages);
    }
}
