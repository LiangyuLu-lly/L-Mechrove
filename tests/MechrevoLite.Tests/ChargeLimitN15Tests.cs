using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// N15 #15 (field, 星耀14 ai9 365): the charge threshold does not take effect, and the UI renders a
/// bare dash where a number should be. Two defects:
///
/// 1. The dash is an unexplained placeholder - the user cannot tell whether the value is unknown,
///    unsupported, or broken. It must say something readable.
/// 2. Profile lag must not be treated as a charge-limit capability. Service-served only means the
///    vendor service is up; it does not prove 0x7B9/0x7D0 control charging.
/// </summary>
public class ChargeLimitN15Tests
{
    [Fact]
    public void TheUnknownPlaceholderIsReadableNotABareDash()
    {
        string source = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Settings.cs");
        // The placeholder must not be a bare dash: it has to name the state.
        Assert.DoesNotContain("BatteryLimitUnknownText = \"—\"", source, StringComparison.Ordinal);
        Assert.Contains("BatteryLimitUnknownText", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUnknownPlaceholderNamesTheReason()
    {
        string source = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Settings.cs");
        // The placeholder names the unread state through the language resource, not a bare dash.
        Assert.Contains("Properties.Strings.BatteryLimitUnknown", source, StringComparison.Ordinal);
        Assert.Equal("无法读取", Properties.Strings.ResourceManager.GetString(
            "BatteryLimitUnknown", System.Globalization.CultureInfo.GetCultureInfo("zh-CN")));
    }

    [Fact]
    public void AServiceServedMachineIsNotAChargeLimitCapability()
    {
        // Profile lag must not open this channel: there is no charge-limit capability bit,
        // and a register echo is not proof these addresses control charging.
        SupportDecision served = SupportDecision.Supported("PH6TRX1");

        Assert.False(EcChargeLimit.IsSupportedMachine(served));
        Assert.False(EcChargeLimit.ReadbackProvesChargingStopped);
    }

    [Fact]
    public void AnUnservedMachineStillCannotSetTheChargeLimit()
    {
        SupportDecision unserved = SupportDecision.NotInSet("GK7NXXR");

        Assert.False(EcChargeLimit.IsSupportedMachine(unserved));
    }
}
