using MechrevoLite.Fan;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T12（Wave C）失败/边界面：目录解析**不得**跨机型——目录缺失只回退 EC 默认值；
/// 不可解析身份与集合外代号不得产出任何目录；空入参必须显式拒绝。
/// </summary>
public class FanTableResolutionFailTests
{
    static readonly ModelRegistryData Registry = ModelRegistryData.Load();
    static readonly string AppRoot = FanTableAssetHarness.RepoPath(FanTableAssetHarness.AppTreeRelative);

    [Fact]
    public void AMissingDirectoryNeverFallsBackToAnotherModelDirectory()
    {
        string temp = NewTempRoot();
        try
        {
            string otherModel = Path.Combine(temp, "PH4TRX1");
            Directory.CreateDirectory(otherModel);

            FanTableResolution resolution = FanTableResolver.Resolve(temp, Identity("PH6PRxx"), Registry.PlatformCodeSet);

            Assert.Equal(FanTableResolutionKind.EcdDefaults, resolution.Kind);
            Assert.Null(resolution.Directory);
            Assert.True(resolution.InSupportedSet);
            Assert.DoesNotContain("PH4TRX1", resolution.Reason, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(temp, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void AnEnumMemberWithoutADirectoryIsNotInTheSupportedSet()
    {
        FanTableResolution resolution = FanTableResolver.Resolve(AppRoot, Identity("PH6AGxx"), Registry.PlatformCodeSet);

        Assert.Equal(FanTableResolutionKind.EcdDefaults, resolution.Kind);
        Assert.Null(resolution.Directory);
        Assert.False(resolution.InSupportedSet);
    }

    [Fact]
    public void AnUnparsableIdentityIsNeverADirectory()
    {
        FanTableResolution resolution = FanTableResolver.Resolve(AppRoot, ModelIdentity.Unknown, Registry.PlatformCodeSet);

        Assert.Equal(FanTableResolutionKind.Unparsable, resolution.Kind);
        Assert.Null(resolution.Directory);
        Assert.False(resolution.InSupportedSet);
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        ModelIdentity identity = Identity("PH4TRX1");

        Assert.Throws<ArgumentNullException>(() => FanTableResolver.Resolve(null!, identity, Registry.PlatformCodeSet));
        Assert.Throws<ArgumentNullException>(() => FanTableResolver.Resolve(AppRoot, null!, Registry.PlatformCodeSet));
        Assert.Throws<ArgumentNullException>(() => FanTableResolver.Resolve(AppRoot, identity, null!));
    }

    static ModelIdentity Identity(string projectId) => new(projectId, 0, "NA", ModelSource.Ec);

    static string NewTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", "fantable-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
