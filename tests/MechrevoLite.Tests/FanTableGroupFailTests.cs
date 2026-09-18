namespace MechrevoLite.Tests;

/// <summary>
/// T13（Wave C）失败/边界面：结构性校验必须**拒绝**组并集之外的键、点数不足或字段缺失的表；
/// 分组必须由数据派生——给两份不同键集就必须是 2 组，不得为凑 4 组放宽。
/// </summary>
public class FanTableGroupFailTests
{
    [Fact]
    public void AFileWithAKeyOutsideItsDeclaredGroupUnionIsRejected()
    {
        using var tree = new TempStructureTree();
        string directory = tree.CreateModel("PH4TRX1");
        tree.Write(directory, "M1T1", Keys("PL1", "PL2", "CPU", "GPU"), Points());
        tree.Write(directory, "M1T2", Keys("PL1", "PL2", "CPU", "GPU", "CTGP"), Points());

        IReadOnlyList<string> violations =
            FanTableStructureHarness.SubsetViolations(directory, new[] { "PL1", "PL2", "CPU", "GPU" });

        Assert.Single(violations);
        Assert.Contains("PH4TRX1/M1T2.json", violations[0], StringComparison.Ordinal);
        Assert.Contains("CTGP", violations[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithFewerThanSixteenPointsIsRejected()
    {
        using var tree = new TempStructureTree();
        string directory = tree.CreateModel("PH4TRX1");
        string file = tree.Write(directory, "M1T1", Keys("PL1", "PL2", "CPU", "GPU"), Points(pointCount: 15));

        IReadOnlyList<string> violations = FanTableStructureHarness.StructureViolations(file);

        Assert.Contains(violations, violation => violation.Contains("CPU", StringComparison.Ordinal) &&
                                                 violation.Contains("16", StringComparison.Ordinal));
    }

    [Fact]
    public void APointMissingOneOfTheFourFieldsIsRejected()
    {
        using var tree = new TempStructureTree();
        string directory = tree.CreateModel("PH4TRX1");
        string file = tree.Write(directory, "M1T1", Keys("PL1", "PL2", "CPU", "GPU"), Points(dropLastCpuDuty: true));

        IReadOnlyList<string> violations = FanTableStructureHarness.StructureViolations(file);

        Assert.Contains(violations, violation => violation.Contains("Duty", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoDistinctKeySetsDeriveTwoGroupsNotAForcedFour()
    {
        using var tree = new TempStructureTree();
        string first = tree.CreateModel("PH4TRX1");
        tree.Write(first, "M1T1", Keys("PL1", "CPU", "GPU"), Points());
        string second = tree.CreateModel("PH6PRxx");
        tree.Write(second, "M1T1", Keys("PL1", "CTGP", "CPU", "GPU"), Points());

        IReadOnlyDictionary<string, SortedSet<string>> groups =
            FanTableStructureHarness.DeriveGroups(new[] { first, second });

        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { 1, 1 }, groups.Values.Select(group => group.Count).OrderBy(count => count).ToArray());
    }

    static string Keys(params string[] keys) =>
        "{\"Activated\":true,\"Name\":\"M1T1\"," + string.Concat(keys.Select(key => $"\"{key}\":1,")).TrimEnd(',') + "}";

    static string Points(int pointCount = 16, bool dropLastCpuDuty = false)
    {
        string cpu = PointArray(pointCount, dropLastCpuDuty);
        string gpu = PointArray(pointCount, dropLastCpuDuty: false);
        return $"{{\"CPU\":{cpu},\"GPU\":{gpu}}}";
    }

    static string PointArray(int pointCount, bool dropLastCpuDuty)
    {
        var points = new List<string>();
        for (int index = 0; index < pointCount; index++)
        {
            bool dropDuty = dropLastCpuDuty && index == pointCount - 1;
            points.Add(dropDuty
                ? $"{{\"ID\":{index},\"UpT\":0,\"DownT\":0}}"
                : $"{{\"ID\":{index},\"UpT\":0,\"DownT\":0,\"Duty\":0}}");
        }
        return "[" + string.Join(",", points) + "]";
    }

    sealed class TempStructureTree : IDisposable
    {
        readonly string _root = Path.Combine(
            Path.GetTempPath(), "L-Mechrevo-tests", "fantable-structure-" + Guid.NewGuid().ToString("N"));

        public TempStructureTree() => Directory.CreateDirectory(_root);

        public string CreateModel(string model)
        {
            string directory = Path.Combine(_root, model);
            Directory.CreateDirectory(directory);
            return directory;
        }

        public string Write(string directory, string table, string keys, string points)
        {
            string file = Path.Combine(directory, table + ".json");
            File.WriteAllText(file, MergeKeysAndPoints(keys, points));
            return file;
        }

        static string MergeKeysAndPoints(string keys, string points) =>
            keys.TrimEnd('}') + "," + points.TrimStart('{');

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }
}
