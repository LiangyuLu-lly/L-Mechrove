using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T28 (Wave F) happy path: the capability snapshot has an explicit lifetime. It is built once,
/// reused until invalidated, rebuilt on <see cref="MechrevoDeviceCapabilities.Refresh"/>, and the
/// service-(re)connect seam rebuilds it so a snapshot cannot outlive an ItemSupport rewrite.
/// Failure-path contract checks live in <see cref="CapabilitySnapshotFailTests"/>.
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class CapabilitySnapshotTests
{
    sealed class SnapshotScope : IDisposable
    {
        readonly Func<MechrevoDeviceCapabilities>? _previousFactory;
        public int Loads;

        public SnapshotScope(Func<MechrevoDeviceCapabilities> factory)
        {
            _previousFactory = MechrevoDeviceCapabilities.SnapshotFactoryOverride;
            MechrevoDeviceCapabilities.Invalidate();
            MechrevoDeviceCapabilities.SnapshotFactoryOverride = () => { Loads++; return factory(); };
        }

        public void Dispose()
        {
            MechrevoDeviceCapabilities.SnapshotFactoryOverride = _previousFactory;
            MechrevoDeviceCapabilities.OverrideCurrent(null);
        }
    }

    static MechrevoDeviceCapabilities WithProject(string project) =>
        new() { ProjectId = project, ProfileAvailable = true };

    [Fact]
    public void TheSnapshotIsBuiltOnceAndReusedUntilInvalidated()
    {
        using var scope = new SnapshotScope(() => WithProject("PH4TRX1"));

        MechrevoDeviceCapabilities first = MechrevoDeviceCapabilities.Current;
        MechrevoDeviceCapabilities second = MechrevoDeviceCapabilities.Current;

        Assert.Same(first, second);
        Assert.Equal(1, scope.Loads);

        MechrevoDeviceCapabilities.Invalidate();
        MechrevoDeviceCapabilities third = MechrevoDeviceCapabilities.Current;

        Assert.NotSame(first, third);
        Assert.Equal(2, scope.Loads);
    }

    [Fact]
    public void RefreshRebuildsTheSnapshotAndAdvancesTheRevision()
    {
        using var scope = new SnapshotScope(() => WithProject("PH6TRX1"));

        _ = MechrevoDeviceCapabilities.Current;
        long before = MechrevoDeviceCapabilities.SnapshotRevision;

        MechrevoDeviceCapabilities refreshed = MechrevoDeviceCapabilities.Refresh();

        Assert.True(MechrevoDeviceCapabilities.SnapshotRevision > before,
            "Refresh must produce a new snapshot revision");
        Assert.Same(refreshed, MechrevoDeviceCapabilities.Current);
        Assert.Equal(2, scope.Loads);
    }

    [Fact]
    public void TheServiceReconnectSeamRebuildsTheSnapshot()
    {
        using var scope = new SnapshotScope(() => WithProject("PH4TRX1"));

        _ = MechrevoDeviceCapabilities.Current;
        long before = MechrevoDeviceCapabilities.SnapshotRevision;
        Assert.Equal(1, scope.Loads);

        // The real seam the MQTT connect path invokes on every successful connection: the vendor
        // service may have rewritten ItemSupport while we were disconnected.
        MechrevoHw.OnServiceProfileMayHaveChanged();
        MechrevoDeviceCapabilities after = MechrevoDeviceCapabilities.Current;

        Assert.Equal(2, scope.Loads);
        Assert.True(MechrevoDeviceCapabilities.SnapshotRevision > before);
        Assert.Equal("PH4TRX1", after.ProjectId);
    }
}

/// <summary>
/// T28 failure-path contract: an override must not survive invalidation, and every rebuild must
/// advance the revision even when the underlying profile is unchanged.
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class CapabilitySnapshotFailTests
{
    sealed class SnapshotScope : IDisposable
    {
        readonly Func<MechrevoDeviceCapabilities>? _previousFactory;
        public int Loads;

        public SnapshotScope(Func<MechrevoDeviceCapabilities> factory)
        {
            _previousFactory = MechrevoDeviceCapabilities.SnapshotFactoryOverride;
            MechrevoDeviceCapabilities.Invalidate();
            MechrevoDeviceCapabilities.SnapshotFactoryOverride = () => { Loads++; return factory(); };
        }

        public void Dispose()
        {
            MechrevoDeviceCapabilities.SnapshotFactoryOverride = _previousFactory;
            MechrevoDeviceCapabilities.OverrideCurrent(null);
        }
    }

    static MechrevoDeviceCapabilities WithProject(string project) =>
        new() { ProjectId = project, ProfileAvailable = true };

    [Fact]
    public void AnInvalidatedOverrideIsNotResurrected()
    {
        using var scope = new SnapshotScope(() => WithProject("PH4TUX1"));

        MechrevoDeviceCapabilities.OverrideCurrent(WithProject("STALE"));
        Assert.Equal("STALE", MechrevoDeviceCapabilities.Current.ProjectId);

        MechrevoDeviceCapabilities.Invalidate();
        Assert.Equal("PH4TUX1", MechrevoDeviceCapabilities.Current.ProjectId);
        Assert.Equal(1, scope.Loads);
    }

    [Fact]
    public void RefreshRebuildsEvenWhenTheSnapshotWasOverridden()
    {
        using var scope = new SnapshotScope(() => WithProject("PH6AQxx"));

        MechrevoDeviceCapabilities.OverrideCurrent(WithProject("STALE"));
        MechrevoDeviceCapabilities refreshed = MechrevoDeviceCapabilities.Refresh();

        Assert.Equal("PH6AQxx", refreshed.ProjectId);
        Assert.Equal(1, scope.Loads);
    }

    [Fact]
    public void RevisionAdvancesOnEveryRebuild()
    {
        using var scope = new SnapshotScope(() => WithProject("PH4ARxx"));

        long start = MechrevoDeviceCapabilities.SnapshotRevision;
        MechrevoDeviceCapabilities.Refresh();
        MechrevoDeviceCapabilities.Refresh();

        Assert.Equal(start + 2, MechrevoDeviceCapabilities.SnapshotRevision);
        Assert.Equal(2, scope.Loads);
    }
}
