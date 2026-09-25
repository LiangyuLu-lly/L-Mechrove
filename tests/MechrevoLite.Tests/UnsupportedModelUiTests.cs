using System.Windows.Forms;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T10（Wave B）失败 / 边界：未知/越界注入同样降级；只读降级下手动覆盖入口必须可达
/// （设置弹窗的机型行存在且可点）；注入值不得触发任何配置写入。
/// happy 断言见 <see cref="UnsupportedModelUiHappyTests"/>。
/// </summary>
public class UnsupportedModelUiTests
{
    [Theory]
    [InlineData(SupportReason.Unparsable, true, false)]
    [InlineData(SupportReason.Unparsable, false, true)]
    [InlineData(SupportReason.NotInSet, true, false)]
    [InlineData(SupportReason.NotInSet, false, true)]
    [InlineData(SupportReason.Ok, true, false)]
    [InlineData(SupportReason.Ok, false, false)]
    public void FirstGcuConnectDoesNotLockTheDashboardReadOnly(
        SupportReason reason, bool connecting, bool expectedDegrade)
    {
        SupportDecision decision = reason switch
        {
            SupportReason.Ok => SupportDecision.Supported("GCU"),
            SupportReason.NotInSet => SupportDecision.NotInSet("GK7NXXR"),
            _ => SupportDecision.Unparsable(),
        };
        Assert.Equal(expectedDegrade, RuntimeModelSupport.ShouldDegradeToReadOnly(decision, connecting));
    }

    [Fact]
    public void ShouldDegradeToReadOnly_FirstConnectNotReconnecting_DoesNotLock()
    {
        // First MQTT handshake: hw is present, never connected (gen 0), reconnect
        // loop has not flipped IsReconnecting yet. EC/ItemSupport are empty → Unparsable.
        SupportDecision decision = SupportDecision.Unparsable();
        Assert.False(RuntimeModelSupport.ShouldDegradeToReadOnly(
            decision, RuntimeModelSupport.IsGcuFirstConnectInProgress(false, 0)));
    }

    [Fact]
    public void ApplyUnsupportedModelNotice_FirstConnect_NoUnparsableBanner()
    {
        string source = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Settings.cs");
        int start = source.IndexOf("public void RefreshDeviceCapabilities()", StringComparison.Ordinal);
        int end = source.IndexOf("void ApplyUnsupportedModelNotice", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string body = source[start..end];
        Assert.Contains("IsGcuFirstConnectInProgress", body, StringComparison.Ordinal);
        Assert.DoesNotContain("IsReconnecting: true, ConnectionGeneration: 0", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownInjectedCodeIsPositivelyUnsupported()
    {
        using var model = UnsupportedModelUiHappyTests.Model("NOT-A-CODE");

        SupportDecision decision = RuntimeModelSupport.Current();
        Assert.False(decision.IsSupported);
        Assert.Equal(SupportReason.NotInSet, decision.Reason);
    }

    [Fact]
    public void AnUnknownInjectedCodeAlsoDegradesTheDashboard()
    {
        using var model = UnsupportedModelUiHappyTests.Model("ZZZZZZ");
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null!;
        try
        {
            using var form = UnsupportedModelUiHappyTests.ShowForm();

            Assert.True(form.IsReadOnlyDegraded);
            Assert.True(form.Controls.Find("panelUnsupportedModelNotice", true).Single().Visible);
            Assert.False(form.Controls.Find("buttonEco", true).Single().Enabled);
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
        }
    }

    [Fact]
    public void TheManualOverrideEntryExistsAndStaysReachableWhileDegraded()
    {
        using var model = UnsupportedModelUiHappyTests.Model("PH6AGxx");
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null!;
        try
        {
            using var form = UnsupportedModelUiHappyTests.ShowForm();

            // 降级仍可达：横幅里的手动机型覆盖入口必须存在且可用。
            Assert.True(form.Controls.Find("textModelOverride", true).Single().Enabled);
            Assert.True(form.Controls.Find("buttonModelOverrideApply", true).Single().Enabled);
            Assert.True(form.Controls.Find("buttonModelOverrideAuto", true).Single().Enabled);
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
        }
    }

    [Fact]
    public void TheManualOverrideRejectsAnOutOfSetValueWithoutWriting()
    {
        string? previousMode = AppConfig.GetString(ModelOverrideStateMachine.ModeKey);
        string? previousModel = AppConfig.GetString(ModelOverrideStateMachine.ModelKey);
        try
        {
            AppConfig.Remove(ModelOverrideStateMachine.ModelKey);
            AppConfig.Remove(ModelOverrideStateMachine.ModeKey);

            // T6: PH6AGxx is ProjectIdNames 5894 and is now Supported.
            Assert.False(ModelOverrideStateMachine.TrySetManual("NOTAMODEL", out SupportDecision decision));
            Assert.Equal(SupportReason.NotInSet, decision.Reason);
            Assert.Null(AppConfig.GetString(ModelOverrideStateMachine.ModelKey));
        }
        finally
        {
            if (previousMode is null) AppConfig.Remove(ModelOverrideStateMachine.ModeKey);
            else AppConfig.Set(ModelOverrideStateMachine.ModeKey, previousMode);
            if (previousModel is null) AppConfig.Remove(ModelOverrideStateMachine.ModelKey);
            else AppConfig.Set(ModelOverrideStateMachine.ModelKey, previousModel);
        }
    }
}
