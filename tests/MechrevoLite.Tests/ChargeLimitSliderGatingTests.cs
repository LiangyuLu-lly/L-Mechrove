using System.Windows.Forms;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// Charge slider stays on the battery panel (N15 first-paint) but is disabled with a
/// reason when the EC limit channel is not available (Unparsable / NotInSet / force-off).
/// Sibling of <see cref="ChargeLimitGatingTests"/>.
/// </summary>
public class ChargeLimitSliderGatingTests
{
    static SettingsForm ShowForm()
    {
        var form = new SettingsForm { ClientSize = SettingsForm.CompactDashboardLogicalClientSize };
        form.CreateControl();
        form.Show();
        Application.DoEvents();
        form.RefreshDeviceCapabilities();
        Application.DoEvents();
        return form;
    }

    [Fact]
    public void ForceOffDisablesTheSliderWithAReasonAndKeepsThePanelVisible()
    {
        using var model = UnsupportedModelUiHappyTests.Model("PH4TRX1");
        using var force = ChargeLimitGatingTests.Force("0");
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null!;
        try
        {
            Assert.False(EcChargeLimit.IsAvailableOnThisMachine());
            using var form = ShowForm();
            Control slider = form.Controls.Find("sliderBattery", true).Single();
            Control panel = form.Controls.Find("panelBattery", true).Single();

            Assert.True(panel.Visible, "N15 first-paint: battery panel stays visible when the EC limit is force-off.");
            Assert.False(slider.Enabled, "force-off must disable the charge slider.");
            Assert.False(string.IsNullOrWhiteSpace(form.ChargeLimitSliderReason),
                "disabled slider must name the reason.");
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
        }
    }

    [Fact]
    public void AnUnparsableIdentityDisablesTheSliderAndKeepsThePanelVisible()
    {
        using var model = UnsupportedModelUiHappyTests.Model("not-a-real-project-id-xxx");
        using var force = ChargeLimitGatingTests.Force(null);
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null!;
        try
        {
            SupportDecision decision = RuntimeModelSupport.Current();
            Assert.False(decision.IsSupported);
            using var form = ShowForm();
            Control slider = form.Controls.Find("sliderBattery", true).Single();
            Control panel = form.Controls.Find("panelBattery", true).Single();
            Control banner = form.Controls.Find("panelUnsupportedModelNotice", true).Single();

            Assert.True(panel.Visible);
            Assert.False(slider.Enabled);
            Assert.True(banner.Visible, "unparsable identity must show the unsupported-model banner.");
            Assert.False(string.IsNullOrWhiteSpace(form.ChargeLimitSliderReason));
            Assert.Contains("机型无法识别", form.ChargeLimitSliderReason, StringComparison.Ordinal);
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
        }
    }

    [Fact]
    public void ANotInSetDecisionNamesTheDisabledReason()
    {
        using var _ = ChargeLimitGatingTests.Force(null);
        Assert.False(EcChargeLimit.IsSupportedMachine(ChargeLimitGatingTests.NotInSet));
        Assert.Contains("不支持", SettingsForm.ChargeLimitDisabledReason(ChargeLimitGatingTests.NotInSet), StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshDeviceCapabilitiesGatesTheSliderOnEcAvailability()
    {
        string source = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Settings.cs");
        Assert.Contains("EcChargeLimit.IsAvailableOnThisMachine()", source, StringComparison.Ordinal);
        Assert.Contains("ChargeLimitSliderReason", source, StringComparison.Ordinal);
    }
}
