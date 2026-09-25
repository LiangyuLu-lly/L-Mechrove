using MechrevoLite.Fan;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// N8 (field-verified): on a real 30-series machine (`Taitan Series GM7TG0M`, decoded project code
/// `GK7NXXR`) the app showed "not in the support list, read-only mode", yet the owner confirmed the
/// vendor's official console works there (GPU mode switching, fans, temperature). Our read-only lock
/// was therefore STRICTER than the vendor - a parity failure, not a safety feature.
///
/// Root cause: the support criterion was "must be one of the 24 PH4*/PH6* fan-table codes". The
/// vendor's criterion is "the service is up and reporting capabilities" (the service-written
/// ItemSupport). The gate is widened to the vendor's criterion, with the 23 generic flat fan tables
/// as the fallback when there is no per-model directory.
///
/// Both directions are pinned: a service-served machine is usable; a machine the service does NOT
/// serve still degrades to read-only.
/// </summary>
public class ModelSupportN8Tests
{
    static readonly IReadOnlySet<string> TwentyFourCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "PH4TRX1", "PH4TUX1", "PH4TQx1", "PH6TRX1", "PH6TQxx",
        "PH4ARxx", "PH4AUxx", "PH4AXxx", "PH6AQxx", "PH6ARxx", "PH6AGxx", "PH4AUxf",
        "PH4PRxx", "PH4PUxx", "PH4PGx1", "PH4PGx2", "PH6PRxx", "PH6PGEx",
        "PH6PG0x", "PH6PG3x", "PH6PG7x", "PH4AQE3", "PH6PG0x150W", "PH6PG3x150W",
    };

    static ModelIdentity Identity(string projectId) =>
        new(projectId, 16, "NA", ModelSource.Ec);

    // ---- the vendor's criterion: service-served => usable --------------------

    [Fact]
    public void AServiceServedMachineOutsideTheTwentyFourCodesIsSupported()
    {
        // GK7NXXR is a real 30-series code the vendor console serves; it is NOT one of the 24.
        SupportDecision decision = ModelSupport.Determine(
            Identity("GK7NXXR"), TwentyFourCodes, serviceServed: true);

        Assert.True(decision.IsSupported);
        Assert.Equal(SupportReason.Ok, decision.Reason);
    }

    [Theory]
    [InlineData("GK7NXXR")]
    [InlineData("GK")]
    [InlineData("GM5MU1Y")]
    [InlineData("GK5CN_X")]
    [InlineData("CML_Gaming")]
    public void EveryServiceServedLegacyCodeIsSupported(string projectId)
    {
        Assert.True(ModelSupport.Determine(Identity(projectId), TwentyFourCodes, serviceServed: true).IsSupported);
    }

    [Fact]
    public void AMachineInTheTwentyFourCodesIsStillSupported()
    {
        Assert.True(ModelSupport.Determine(Identity("PH6TRX1"), TwentyFourCodes, serviceServed: true).IsSupported);
    }

    // ---- the retained D1 case: service does NOT serve => read-only -----------

    [Fact]
    public void AMachineTheServiceDoesNotServeStaysReadOnly()
    {
        SupportDecision decision = ModelSupport.Determine(
            Identity("GK7NXXR"), TwentyFourCodes, serviceServed: false);

        Assert.False(decision.IsSupported);
        Assert.Equal(SupportReason.NotInSet, decision.Reason);
    }

    [Fact]
    public void AnUnparsableIdentityIsAcceptedWhenTheServiceIsServing()
    {
        // yilong15 Pro GM5HG0A: GCU 已连接、EC 身份失败 → 不得只读。
        SupportDecision decision = ModelSupport.Determine(
            ModelIdentity.Unknown, TwentyFourCodes, serviceServed: true);

        Assert.True(decision.IsSupported);
        Assert.Equal(SupportReason.Ok, decision.Reason);
        Assert.Equal("GCU", decision.ProjectId);
    }

    // ---- the generic flat-table fallback ------------------------------------

    [Fact]
    public void AMachineWithoutItsOwnDirectoryFallsBackToTheGenericFlatTables()
    {
        string root = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", "n8-flat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // The 23 generic flat files, no per-model directory.
            foreach (string name in new[]
                     {
                         "DefaultFanTable_Gaming.json", "DefaultFanTable_Office.json", "DefaultFanTable_Turbo.json",
                         "M1T1.json", "M1T2.json", "M1T3.json", "M1T4.json", "M1T5.json",
                         "M2T1.json", "M2T2.json", "M2T3.json", "M2T4.json", "M2T5.json",
                         "M3T1.json", "M3T2.json", "M3T3.json", "M3T4.json", "M3T5.json",
                         "M4T1.json", "M4T2.json", "M4T3.json", "M4T4.json", "M4T5.json",
                     })
            {
                File.WriteAllText(Path.Combine(root, name), "{}");
            }

            FanTableResolution resolution = FanTableResolver.Resolve(root, Identity("GK7NXXR"), TwentyFourCodes);

            Assert.Equal(FanTableResolutionKind.GenericFlatTables, resolution.Kind);
            Assert.NotNull(resolution.Directory);
            Assert.True(Directory.Exists(resolution.Directory));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void AMachineWithItsOwnDirectoryStillUsesThatDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", "n8-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "PH6TRX1"));
        try
        {
            FanTableResolution resolution = FanTableResolver.Resolve(root, Identity("PH6TRX1"), TwentyFourCodes);
            Assert.Equal(FanTableResolutionKind.Directory, resolution.Kind);
            Assert.EndsWith("PH6TRX1", resolution.Directory!, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void WithNoFlatTablesEitherTheResolutionIsUnavailableNotGuessed()
    {
        string root = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", "n8-none-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            FanTableResolution resolution = FanTableResolver.Resolve(root, Identity("GK7NXXR"), TwentyFourCodes);
            Assert.Equal(FanTableResolutionKind.EcdDefaults, resolution.Kind);
            Assert.Null(resolution.Directory);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---- the decode itself --------------------------------------------------

    [Fact]
    public void TheGk7nxxrDecodeIsCorrect()
    {
        // Project byte 16 -> "GK7NXXR"; 16 is not 23/24, so GetProject2ExID returns it unchanged.
        Assert.Equal("GK7NXXR", ModelRegistry.ProjectIdNameFor(16));
        Assert.Equal(16, ModelRegistry.ExpandProjectIdForTest(16, _ => 0));
    }
}
