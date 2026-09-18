using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T13（Wave C）happy 路径：分组**由实测键集派生**——每机型取 6 文件顶层键并集（去掉
/// <c>Activated</c>/<c>Name</c>），键集相同者同组；4 组、5/8/5/6、Σ == 24。每组另校验
/// 单文件 `键集 ⊆ 组并集` 且两个 16 点表（CPU/GPU）的 <c>{ID,UpT,DownT,Duty}</c> 完备。
/// 禁止为凑 24 放宽断言。失败/边界断言见 <see cref="FanTableGroupFailTests"/>。
/// </summary>
public class FanTableGroupTests
{
    static readonly string AppRoot = FanTableAssetHarness.RepoPath(FanTableAssetHarness.AppTreeRelative);
    static readonly ModelRegistryData Registry = ModelRegistryData.Load();

    static IEnumerable<string> ModelDirectories() =>
        FanTableAssetHarness.ModelNames.Select(model => Path.Combine(AppRoot, model));

    [Fact]
    public void TheActualKeySetsDeriveFourGroupsOfFiveFiveSixEight()
    {
        IReadOnlyDictionary<string, SortedSet<string>> groups = FanTableStructureHarness.DeriveGroups(ModelDirectories());

        Assert.Equal(4, groups.Count);
        Assert.Equal(new[] { 5, 5, 6, 8 }, groups.Values.Select(group => group.Count).OrderBy(count => count).ToArray());
        Assert.Equal(24, groups.Values.Sum(group => group.Count));
    }

    [Fact]
    public void TheDerivedGroupsMatchTheRegistryGroupsExactly()
    {
        IReadOnlyDictionary<string, SortedSet<string>> derived = FanTableStructureHarness.DeriveGroups(ModelDirectories());
        Assert.Equal(4, Registry.FanTableGroups.Count);

        foreach (FanTableGrouping group in Registry.FanTableGroups)
        {
            string signature = FanTableStructureHarness.Signature(group.Keys);
            Assert.True(derived.TryGetValue(signature, out SortedSet<string>? models),
                $"registry group '{group.Id}' key set was not derived from disk");
            Assert.Equal(
                group.Models.OrderBy(model => model, StringComparer.Ordinal),
                models!.OrderBy(model => model, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void TheThreeModelsTheNotesMissedLandInTheExpectedGroups()
    {
        IReadOnlyDictionary<string, SortedSet<string>> derived = FanTableStructureHarness.DeriveGroups(ModelDirectories());
        string k9 = FanTableStructureHarness.Signature(Registry.FanTableGroups.Single(group => group.Id == "k9").Keys);
        string k12 = FanTableStructureHarness.Signature(Registry.FanTableGroups.Single(group => group.Id == "k12").Keys);

        Assert.Contains("PH4PUxx", derived[k9]);
        Assert.Contains("PH4PGx1", derived[k12]);
        Assert.Contains("PH4PGx2", derived[k12]);
        Assert.Contains("PH4AQE3", derived[k12]);
        Assert.Contains("PH6PGEx", derived[k12]);
    }

    [Fact]
    public void EveryOneOfThe144FilesCarriesTwoCompleteSixteenPointTables()
    {
        var violations = new List<string>();
        foreach (string directory in ModelDirectories())
            foreach (string file in Directory.GetFiles(directory, "*.json"))
                violations.AddRange(FanTableStructureHarness.StructureViolations(file));

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void EveryFilesKeySetIsASubsetOfItsDeclaredGroupUnion()
    {
        var violations = new List<string>();
        foreach (string model in FanTableAssetHarness.ModelNames)
        {
            FanTableGrouping group = Registry.FanTableGroups.Single(candidate => candidate.Models.Contains(model));
            violations.AddRange(
                FanTableStructureHarness.SubsetViolations(Path.Combine(AppRoot, model), group.Keys));
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Ph6PrxxGroupsByItsSixFileUnionNotByItsSmallestFile()
    {
        string directory = Path.Combine(AppRoot, "PH6PRxx");
        string k12 = FanTableStructureHarness.Signature(Registry.FanTableGroups.Single(group => group.Id == "k12").Keys);

        // M1T1 是 k9 子集（无 CTGP/DB/WM），但 M2 含 CTGP——按并集归 k12。
        SortedSet<string> m1 = FanTableStructureHarness.ReadKeys(Path.Combine(directory, "M1T1.json"));
        Assert.True(m1.IsSubsetOf(FanTableStructureHarness.ReadKeys(Path.Combine(directory, "M2T1.json"))));
        Assert.Equal(k12, FanTableStructureHarness.Signature(FanTableStructureHarness.UnionKeys(directory)));
    }
}
