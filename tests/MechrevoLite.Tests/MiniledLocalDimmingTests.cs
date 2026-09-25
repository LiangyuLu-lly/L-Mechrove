using System.Windows.Forms;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// Mini-LED follows <see cref="MechrevoHw.SupportsLocalDimming"/> and MQTT
/// <c>SwitchLocalDimming</c>. ACPI <c>ToogleMiniled</c> cannot re-hide the button.
/// Settings dialog still must not grow a <c>checkLocalDimming</c> checkbox.
/// </summary>
public class MiniledLocalDimmingTests
{
    static IDisposable SwapHardware(MechrevoHw? hardware, bool audit)
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = audit;
        Program.hw = hardware!;
        return new Restore(previousHardware, previousAudit);
    }

    sealed class Restore(MechrevoHw? hardware, bool audit) : IDisposable
    {
        public void Dispose()
        {
            Program.hw = hardware!;
            Program.UiAuditMode = audit;
        }
    }

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
    public void SupportsLocalDimmingShowsTheMiniledButton()
    {
        using var _ = SwapHardware(new MechrevoHw(null, new MechrevoDeviceCapabilities { LocalDimming = true }), audit: false);
        using var form = ShowForm();
        Assert.True(form.MiniledOffered);
    }

    [Fact]
    public void MissingLocalDimmingHidesTheMiniledButtonEvenWhenAcpiReportsMiniled()
    {
        using var _ = SwapHardware(new MechrevoHw(null, new MechrevoDeviceCapabilities { LocalDimming = false }), audit: false);
        using var form = ShowForm();
        form.VisualiseScreen(
            screenEnabled: true, screenAuto: false, frequency: 60, maxFrequency: 60,
            overdrive: 0, overdriveSetting: false, miniled1: 1, miniled2: -1,
            hdr: false, acm: false, fhd: -1, hdrControl: -1);
        Application.DoEvents();

        Assert.False(form.MiniledOffered, "ACPI miniled1 must not re-show Mini-LED when SupportsLocalDimming is false.");
    }

    [Fact]
    public void TheMiniledClickUsesSwitchLocalDimmingNotAcpiToggle()
    {
        string source = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Settings.cs");
        int start = source.IndexOf("void ButtonMiniled_Click", StringComparison.Ordinal);
        int end = source.IndexOf("public void VisualiseScreen", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string body = source[start..end];

        Assert.Contains("SwitchLocalDimming", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ToogleMiniled", body, StringComparison.Ordinal);
        Assert.Contains("LOCALDIMMING", GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Hardware", "MechrevoService.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshDeviceCapabilitiesOwnsMiniledVisibilityFromSupportsLocalDimming()
    {
        string source = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Settings.cs");
        int start = source.IndexOf("public void RefreshDeviceCapabilities()", StringComparison.Ordinal);
        int end = source.IndexOf("void ApplyUnsupportedModelNotice", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string body = source[start..end];
        Assert.Contains("LocalDimmingSeen", body, StringComparison.Ordinal);
        Assert.Contains("buttonMiniled", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SupportsLocalDimming ??", body, StringComparison.Ordinal);
    }
}
