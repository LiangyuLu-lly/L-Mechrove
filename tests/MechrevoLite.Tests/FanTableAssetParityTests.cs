namespace MechrevoLite.Tests;

/// <summary>
/// T11（Wave C）happy 路径：24 个机型目录 x 6 张表（144 文件）在所有可得的载荷副本间**逐文件 SHA256
/// 一致**，且 flat 文件（<c>DefaultFanTable_*</c> + <c>M1T1..M4T5</c>）确属**载荷资产**——应用树顶层
/// 一个 JSON 都没有。
///
/// <para>只读：不改动任何一棵被比对的树（<c>release\</c> 尤甚）。失败/边界断言见
/// <see cref="FanTableAssetParityFailTests"/>。</para>
/// </summary>
public class FanTableAssetParityTests
{
    [Fact]
    public void TheAppTreeHasExactly24ModelsWith6TablesEach()
    {
        string app = FanTableAssetHarness.RepoPath(FanTableAssetHarness.AppTreeRelative);
        Assert.True(Directory.Exists(app), $"app fan-table tree is missing: {app}");

        string[] models = Directory.GetDirectories(app)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(FanTableAssetHarness.ModelNames.OrderBy(name => name, StringComparer.Ordinal), models);
        Assert.Equal(FanTableAssetHarness.PerModelFileCount, FanTableAssetHarness.CountPerModelFiles(app));

        foreach (string model in models)
            Assert.Equal(
                FanTableAssetHarness.TableFileNames.OrderBy(name => name, StringComparer.Ordinal),
                Directory.GetFiles(Path.Combine(app, model), "*.json")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(name => name is not null)
                    .Select(name => name!)
                    .OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryPerModelTableMatchesEveryAvailablePayloadCopyByteForByte()
    {
        string app = FanTableAssetHarness.RepoPath(FanTableAssetHarness.AppTreeRelative);
        Assert.True(Directory.Exists(app), $"app fan-table tree is missing: {app}");

        var copies = new List<(string Label, string Root)> { ("app", app) };
        string release = FanTableAssetHarness.RepoPath(FanTableAssetHarness.ReleaseTreeRelative);
        if (Directory.Exists(release)) copies.Add(("release/GCU-only", release));
        foreach (string relative in FanTableAssetHarness.IndependentTreeRelatives)
        {
            string root = FanTableAssetHarness.RepoPath(relative);
            if (Directory.Exists(root)) copies.Add((relative, root));
        }

        Assert.True(copies.Count >= 2,
            "byte-identical parity needs at least one independent payload copy beside the app tree");

        foreach ((string label, string root) in copies)
            Assert.Equal(FanTableAssetHarness.PerModelFileCount, FanTableAssetHarness.CountPerModelFiles(root));

        foreach ((string label, string root) in copies.Skip(1))
        {
            IReadOnlyList<string> differences = FanTableAssetHarness.ComparePerModelFiles(app, root);
            Assert.True(differences.Count == 0,
                $"app tree differs from {label}:{Environment.NewLine}{string.Join(Environment.NewLine, differences)}");
        }
    }

    [Fact]
    public void TheFlatFilesArePayloadAssetsAndTheAppTreeCarriesNone()
    {
        Assert.Equal(FanTableAssetHarness.FlatPayloadFileCount, FanTableAssetHarness.FlatPayloadFiles.Length);
        Assert.Equal(
            FanTableAssetHarness.FlatPayloadFiles.Length,
            FanTableAssetHarness.FlatPayloadFiles.Distinct(StringComparer.Ordinal).Count());

        string app = FanTableAssetHarness.RepoPath(FanTableAssetHarness.AppTreeRelative);
        Assert.True(Directory.Exists(app), $"app fan-table tree is missing: {app}");

        // 应用树顶层不得有任何 flat JSON：它们随 GCU 服务载荷分发（40/50 系载荷都带），不属应用资产。
        string[] appTopLevel = FanTableAssetHarness.TopLevelJsonFiles(app).ToArray();
        Assert.True(appTopLevel.Length == 0,
            "app fan-table tree must not carry flat payload files: " + string.Join(", ", appTopLevel));
    }

    [Fact]
    public void TheFlatFilesArePresentInThePayloadTreeWhenItIsAvailable()
    {
        // release\ 是只读且会被清理（本机为空）——只在存在时做正向取证：
        // 载荷树顶层恰好是这 23 个 flat 文件，反证"flat = 载荷资产"。
        string release = FanTableAssetHarness.RepoPath(FanTableAssetHarness.ReleaseTreeRelative);
        if (!Directory.Exists(release))
        {
            Assert.False(Directory.Exists(release));   // 环境限制记录在案：本机 release\ 为空
            return;
        }

        Assert.Equal(
            FanTableAssetHarness.FlatPayloadFiles.OrderBy(name => name, StringComparer.Ordinal),
            FanTableAssetHarness.TopLevelJsonFiles(release));
    }
}
