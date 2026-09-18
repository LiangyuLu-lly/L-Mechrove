namespace MechrevoLite.Tests;

/// <summary>
/// T11（Wave C）失败/边界面：比对工具必须**报告**每一处差异（内容不同、缺失、多出），
/// 且必须把 flat JSON 判为应用树越界；比对范围限定在逐机型 depth-2 文件，别层文件不参与。
/// </summary>
public class FanTableAssetParityFailTests
{
    [Fact]
    public void AContentDifferenceIsReported()
    {
        using var tree = new TempFanTableTree();
        tree.Add("PH4TRX1", "M1T1", """{"PL1":"40"}""");
        tree.AddOther("PH4TRX1", "M1T1", """{"PL1":"45"}""");

        IReadOnlyList<string> differences = FanTableAssetHarness.ComparePerModelFiles(tree.Left, tree.Right);

        Assert.Single(differences);
        Assert.Contains("content differs: PH4TRX1/M1T1.json", differences[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingFileIsReported()
    {
        using var tree = new TempFanTableTree();
        tree.Add("PH4TRX1", "M1T1", """{"PL1":"40"}""");
        tree.Add("PH4TRX1", "M1T2", """{"PL1":"40"}""");
        tree.AddOther("PH4TRX1", "M1T1", """{"PL1":"40"}""");

        IReadOnlyList<string> differences = FanTableAssetHarness.ComparePerModelFiles(tree.Left, tree.Right);

        Assert.Single(differences);
        Assert.Contains("missing on right: PH4TRX1/M1T2.json", differences[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AFlatJsonAtTheTopLevelIsNotAnAppAsset()
    {
        using var tree = new TempFanTableTree();
        tree.Add("PH4TRX1", "M1T1", """{"PL1":"40"}""");
        tree.AddTopLevel("DefaultFanTable_Gaming.json", """{"Name":"DefaultFanTable_Gaming"}""");

        IReadOnlyList<string> topLevel = FanTableAssetHarness.TopLevelJsonFiles(tree.Left);

        Assert.Equal(new[] { "DefaultFanTable_Gaming.json" }, topLevel);
        // depth-2 计数不受顶层 flat 文件影响。
        Assert.Equal(1, FanTableAssetHarness.CountPerModelFiles(tree.Left));
    }

    [Fact]
    public void FilesOutsidePerModelDirectoriesDoNotCount()
    {
        using var tree = new TempFanTableTree();
        tree.Add("PH4TRX1", "M1T1", """{"PL1":"40"}""");
        tree.AddDeep("PH4TRX1", "nested", "M1T1", """{"PL1":"40"}""");

        Assert.Equal(1, FanTableAssetHarness.CountPerModelFiles(tree.Left));
    }

    sealed class TempFanTableTree : IDisposable
    {
        readonly string _root = Path.Combine(
            Path.GetTempPath(), "L-Mechrevo-tests", "fantable-parity-" + Guid.NewGuid().ToString("N"));

        public TempFanTableTree()
        {
            Left = Path.Combine(_root, "left");
            Right = Path.Combine(_root, "right");
            Directory.CreateDirectory(Left);
            Directory.CreateDirectory(Right);
        }

        public string Left { get; }
        public string Right { get; }

        public void Add(string model, string table, string content) => Write(Left, model, table, content);
        public void AddOther(string model, string table, string content) => Write(Right, model, table, content);

        public void AddTopLevel(string fileName, string content) =>
            File.WriteAllText(Path.Combine(Left, fileName), content);

        public void AddDeep(string model, string middle, string table, string content)
        {
            string directory = Path.Combine(Left, model, middle);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, table + ".json"), content);
        }

        static void Write(string root, string model, string table, string content)
        {
            string directory = Path.Combine(root, model);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, table + ".json"), content);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }
}
