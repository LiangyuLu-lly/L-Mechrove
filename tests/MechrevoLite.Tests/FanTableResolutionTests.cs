using MechrevoLite.Fan;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T12（Wave C）happy 路径：机型目录**严格**由厂房规则解析——
/// <c>Enum.GetName(ProjectID, GetProject2ExID(GetProjectIdFromEC()))</c> 就是目录名，
/// 该名字由 <see cref="ModelRegistry"/> 从 EC 产出（注入的 <see cref="ModelIdentity"/>）。
/// 目录缺失一律回退"读 EC 默认值"，**绝不**落到别的机型目录。
/// 失败/边界断言见 <see cref="FanTableResolutionFailTests"/>。
/// </summary>
public class FanTableResolutionTests
{
    static readonly ModelRegistryData Registry = ModelRegistryData.Load();
    static readonly string AppRoot = FanTableAssetHarness.RepoPath(FanTableAssetHarness.AppTreeRelative);

    [Fact]
    public void EveryPlatformCodeResolvesToItsOwnDirectory()
    {
        Assert.True(Directory.Exists(AppRoot), $"app fan-table tree is missing: {AppRoot}");

        foreach (var code in Registry.PlatformCodes)
        {
            FanTableResolution resolution = Resolve(code.Code);

            Assert.Equal(FanTableResolutionKind.Directory, resolution.Kind);
            Assert.True(resolution.InSupportedSet, $"'{code.Code}' must be in the 24-code set");
            Assert.NotNull(resolution.Directory);
            Assert.Equal(code.Code, Path.GetFileName(resolution.Directory));
            Assert.Equal(Path.Combine(AppRoot, code.Code), resolution.Directory);
        }
    }

    [Fact]
    public void TheVendorEnumRuleCannotProduceTheOrphanDirectoryName()
    {
        // PH4AQxx 有风扇表目录，但不是厂商 ProjectID 枚举成员（T2 已登记）——按 EC 规则永远不会产出它。
        Assert.False(ModelRegistry.IsKnownProjectName("PH4AQxx"));
        Assert.Null(Registry.PlatformCodes.Single(code => code.Code == "PH4AQxx").ProjectId);
        Assert.Equal(23, Registry.PlatformCodes.Count(code => code.ProjectId is not null));
    }

    [Fact]
    public void EnumMembersWithoutADirectoryFallBackToEcDefaults()
    {
        // PH6TQxx(22) 与 PH6AGxx(5894) 是枚举成员但没有目录：走 EC 默认值，绝不跨机型。
        foreach (string code in new[] { "PH6TQxx", "PH6AGxx" })
        {
            FanTableResolution resolution = Resolve(code);

            Assert.Equal(FanTableResolutionKind.EcdDefaults, resolution.Kind);
            Assert.Null(resolution.Directory);
            Assert.False(resolution.InSupportedSet, $"'{code}' is not one of the 24 fan-table directories");
            Assert.False(string.IsNullOrWhiteSpace(resolution.Reason));
        }
    }

    [Fact]
    public void AnUnparsableIdentityNeverYieldsADirectory()
    {
        FanTableResolution resolution = FanTableResolver.Resolve(AppRoot, ModelIdentity.Unknown, Registry.PlatformCodeSet);

        Assert.Equal(FanTableResolutionKind.Unparsable, resolution.Kind);
        Assert.Null(resolution.Directory);
        Assert.False(resolution.InSupportedSet);
        Assert.Equal(ModelIdentity.UnknownName, resolution.Model);
    }

    [Fact]
    public void AnInSetCodeWithAMissingDirectoryUsesEcDefaultsAtItsOwnRoot()
    {
        using var tree = new TempTree();
        tree.CreateDirectory("PH4TRX1");

        FanTableResolution resolution = FanTableResolver.Resolve(tree.Root, Identity("PH6PRxx"), Registry.PlatformCodeSet);

        Assert.Equal(FanTableResolutionKind.EcdDefaults, resolution.Kind);
        Assert.Null(resolution.Directory);
        Assert.True(resolution.InSupportedSet);
    }

    static FanTableResolution Resolve(string projectName) =>
        FanTableResolver.Resolve(AppRoot, Identity(projectName), Registry.PlatformCodeSet);

    static ModelIdentity Identity(string projectId) => new(projectId, 0, "NA", ModelSource.Ec);

    sealed class TempTree : IDisposable
    {
        public TempTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", "fantable-resolve-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void CreateDirectory(string name) => Directory.CreateDirectory(Path.Combine(Root, name));

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }
    }
}
