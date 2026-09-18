using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// N15 #15 (field, 星耀14 ai9 365): the charge threshold does not take effect, and the UI renders a
/// bare dash where a number should be. Two defects:
///
/// 1. The dash is an unexplained placeholder - the user cannot tell whether the value is unknown,
///    unsupported, or broken. It must say something readable.
/// 2. The write gate requires `matrix.ProfileAvailable`, which is false when the vendor service has
///    not written `ItemSupport` yet - the same "sampled too early" class as N15 #12. A machine whose
///    service is served must be able to set the limit.
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
        // A readable placeholder: the user must learn that the value could not be read.
        Assert.Contains("无法读取", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AServiceServedMachineCanSetTheChargeLimit()
    {
        // The gate must not require the profile to be available when the service is served: the
        // profile is written by the service and may lag the first sample.
        SupportDecision served = SupportDecision.Supported("PH6TRX1");
        FeatureMatrix noProfile = FeatureMatrix.FromValues(new Dictionary<string, object?>());

        Assert.True(EcChargeLimit.IsSupportedMachine(served, noProfile));
    }

    [Fact]
    public void AnUnservedMachineStillCannotSetTheChargeLimit()
    {
        SupportDecision unserved = SupportDecision.NotInSet("GK7NXXR");
        FeatureMatrix noProfile = FeatureMatrix.FromValues(new Dictionary<string, object?>());

        Assert.False(EcChargeLimit.IsSupportedMachine(unserved, noProfile));
    }
}
