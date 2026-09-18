using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T27 (Wave F) happy path: the 84 model-dependent branch sites (A 8 + B 41 + C 22 + D 5 + E 8)
/// are converged into one ledger, each with exactly one disposition and exactly one owning todo.
/// Failure assertions live in <see cref="BranchConvergenceFailTests"/>.
/// </summary>
public class BranchConvergenceTests
{
    static int Count(BranchArea area) =>
        BranchConvergenceMap.Sites.Count(site => site.Area == area);

    [Fact]
    public void TheLedgerCoversAllEightyFourSites()
    {
        BranchConvergenceMap.Validate(BranchConvergenceMap.Sites);

        Assert.Equal(84, BranchConvergenceMap.Sites.Count);
        Assert.Equal(8, Count(BranchArea.IdentitySources));
        Assert.Equal(41, Count(BranchArea.FeatureGates));
        Assert.Equal(22, Count(BranchArea.Hardcodes));
        Assert.Equal(5, Count(BranchArea.Persistence));
        Assert.Equal(8, Count(BranchArea.UngatedConsumers));
    }

    [Fact]
    public void EverySiteIsAssignedToExactlyOneTask()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (BranchSite site in BranchConvergenceMap.Sites)
        {
            Assert.True(seen.Add(site.Id), $"duplicate branch site id '{site.Id}'");
            Assert.Contains(site.Owner, BranchConvergenceMap.KnownOwners);
        }
        Assert.Equal(84, seen.Count);
    }

    [Fact]
    public void NoSiteIsLeftUnhandled()
    {
        foreach (BranchSite site in BranchConvergenceMap.Sites)
        {
            Assert.False(string.IsNullOrWhiteSpace(site.Location), $"{site.Id} has no location");
            Assert.False(string.IsNullOrWhiteSpace(site.Note), $"{site.Id} has no disposition note");
            Assert.True(Enum.IsDefined(site.Disposition), $"{site.Id} has an undefined disposition");
        }
    }

    [Fact]
    public void EveryHardcodeAndUngatedConsumerIsPresent()
    {
        for (int i = 1; i <= 22; i++) Assert.Contains("C" + i, BranchConvergenceMap.Sites.Select(s => s.Id));
        for (int i = 1; i <= 8; i++) Assert.Contains("E" + i, BranchConvergenceMap.Sites.Select(s => s.Id));
    }

    /// <summary>C5 crosswalk: the C-area rows must agree with the T9 hardcode disposition table.</summary>
    [Fact]
    public void TheLedgerAgreesWithTheT9HardcodeTable()
    {
        foreach (HardcodeEntry entry in HardcodeDispositions.Items)
        {
            BranchSite site = BranchConvergenceMap.Sites.Single(s => s.Id == entry.Item);
            BranchDisposition expected = entry.Disposition == HardcodeDisposition.MatrixGated
                ? BranchDisposition.MatrixGated
                : BranchDisposition.KeptWithReason;
            Assert.Equal(expected, site.Disposition);
        }

        // Metis one-owner crosswalk (E1-E8 / C1-C22 -> exactly one todo).
        AssertOwner("C1", "T7");
        AssertOwner("C6", "T9");
        AssertOwner("C14", "T8");
        AssertOwner("C19", "T21");
        AssertOwner("C20", "T8");
        AssertOwner("E1", "T8");
        AssertOwner("E2", "T29");
        AssertOwner("E3", "T29");
        AssertOwner("E5", "T21");
    }

    void AssertOwner(string id, string owner) =>
        Assert.Equal(owner, BranchConvergenceMap.Sites.Single(site => site.Id == id).Owner);
}

/// <summary>
/// T27 failure path: every way the convergence ledger can be under-specified is rejected instead of
/// silently passing (missing site, duplicate id, unknown area/owner, undefined disposition).
/// </summary>
public class BranchConvergenceFailTests
{
    static List<BranchSite> Copy() => BranchConvergenceMap.Sites.ToList();

    [Fact]
    public void AMissingSiteIsRejected()
    {
        List<BranchSite> sites = Copy();
        sites.RemoveAt(0);
        Assert.Throws<BranchConvergenceException>(() => BranchConvergenceMap.Validate(sites));
    }

    [Fact]
    public void ADuplicateSiteIdIsRejected()
    {
        List<BranchSite> sites = Copy();
        sites[sites.Count - 1] = sites[0];
        Assert.Throws<BranchConvergenceException>(() => BranchConvergenceMap.Validate(sites));
    }

    [Fact]
    public void AnUnknownOwnerIsRejected()
    {
        List<BranchSite> sites = Copy();
        sites[0] = sites[0] with { Owner = "T999" };
        Assert.Throws<BranchConvergenceException>(() => BranchConvergenceMap.Validate(sites));
    }

    [Fact]
    public void AnUnknownAreaIsRejected()
    {
        List<BranchSite> sites = Copy();
        sites[0] = sites[0] with { Area = (BranchArea)999 };
        Assert.Throws<BranchConvergenceException>(() => BranchConvergenceMap.Validate(sites));
    }

    [Fact]
    public void AnUndefinedDispositionIsRejected()
    {
        List<BranchSite> sites = Copy();
        sites[0] = sites[0] with { Disposition = (BranchDisposition)999 };
        Assert.Throws<BranchConvergenceException>(() => BranchConvergenceMap.Validate(sites));
    }
}
