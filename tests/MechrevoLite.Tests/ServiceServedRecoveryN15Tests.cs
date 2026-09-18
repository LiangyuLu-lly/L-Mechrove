using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// N15 #12 (field): `24机械革命极光X` reports "什么功能都用不了" and it persists after a reboot. The
/// N8 widening made the support gate depend on the vendor service being served, but that signal is
/// sampled once: if the app starts before the GCU service connects (or the service is briefly down),
/// the machine is locked read-only and never recovers - the whole app is unusable.
///
/// The gate must therefore be re-evaluated when the service connection changes, not frozen at the
/// first sample.
/// </summary>
public class ServiceServedRecoveryN15Tests
{
    static readonly IReadOnlySet<string> Codes = new HashSet<string>(StringComparer.Ordinal) { "PH6TRX1" };

    static ModelIdentity Identity(string id) => new(id, 16, "NA", ModelSource.Ec);

    [Fact]
    public void AMachineLockedBeforeTheServiceConnectedBecomesUsableOnceItDoes()
    {
        // First sample: service not yet connected -> read-only.
        SupportDecision before = ModelSupport.Determine(Identity("GK7NXXR"), Codes, serviceServed: false);
        Assert.False(before.IsSupported);

        // The service connects later: the same machine must now be usable.
        SupportDecision after = ModelSupport.Determine(Identity("GK7NXXR"), Codes, serviceServed: true);
        Assert.True(after.IsSupported);
    }

    [Fact]
    public void TheSupportDecisionIsNotFrozenAtTheFirstSample()
    {
        // The decision must be a pure function of the current service state, so a later re-evaluation
        // can flip it. This pins that no caching is baked into the decision itself.
        Assert.NotEqual(
            ModelSupport.Determine(Identity("GK7NXXR"), Codes, serviceServed: false),
            ModelSupport.Determine(Identity("GK7NXXR"), Codes, serviceServed: true));
    }

    [Fact]
    public void TheChargeLimitSupportIsNotCachedForever()
    {
        string source = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Hardware", "EcChargeLimit.cs");
        // A Lazy<SupportDecision> freezes the read-only verdict for the process lifetime, so a
        // machine that was locked before the service connected can never recover.
        Assert.DoesNotContain("Lazy<SupportDecision>", source, StringComparison.Ordinal);
    }
}
