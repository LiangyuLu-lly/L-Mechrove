using System.Security.Cryptography;

namespace MechrevoLite.Tests;

/// <summary>
/// T11（Wave C）的风扇表资产比对工具：把 <c>UserFanTables</c> 的逐机型目录与所有可得的载荷副本
/// 按文件 SHA256 对照。只读——比对过程不写入任何一棵被比对的树。
///
/// <para>机型表只在**逐机型子目录**里（<c>&lt;Model&gt;/&lt;Table&gt;.json</c>）；顶层 flat JSON
/// （<c>DefaultFanTable_*.json</c> + <c>M1T1..M4T5.json</c>）是**服务载荷资产**，不属应用树，
/// 故比对与计数都只在 depth-2 文件上进行。</para>
/// </summary>
internal static class FanTableAssetHarness
{
    /// <summary>应用树（仓库根下一层）；T2 的实况测试也读这里。</summary>
    internal const string AppTreeRelative = @"ControlCenterX_5.56.60.26_Mechrevo\UserFanTables";

    /// <summary>50 系 GCU 载荷里的副本；<c>release\</c> 是只读且常被清理，存在时才校验。</summary>
    internal const string ReleaseTreeRelative = @"release\GCU-only\AiStoneService\MyControlCenter\UserFanTables";

    /// <summary>两份独立的厂商控制台副本。至少一份必须在场，否则"逐文件一致"无从谈起。</summary>
    internal static readonly string[] IndependentTreeRelatives =
    {
        @"ControlCenter_5.17.51.34_Mechrevo\ControlCenter_5.17.51.34_Mechrevo\UserFanTables",
        @"ControlCenter_5.17.49.19_Mechrevo\ControlCenter_5.17.49.19_Mechrevo\UserFanTables",
    };

    internal static readonly string[] ModelNames =
    {
        "PH4AQE3", "PH4AQxx", "PH4ARxx", "PH4AUxf", "PH4AUxx", "PH4AXxx",
        "PH4PGx1", "PH4PGx2", "PH4PRxx", "PH4PUxx", "PH4TQx1", "PH4TRX1",
        "PH4TUX1", "PH6AQxx", "PH6ARxx", "PH6PG0x", "PH6PG0x150W", "PH6PG3x",
        "PH6PG3x150W", "PH6PG7x", "PH6PG7x150W", "PH6PGEx", "PH6PRxx", "PH6TRX1",
    };

    internal static readonly string[] TableFileNames = { "M1T1", "M1T2", "M1T3", "M2T1", "M2T2", "M2T3" };

    internal const int PerModelFileCount = 144;   // 24 models x 6 tables

    /// <summary>
    /// flat 载荷资产（23 个 = 3 张 DefaultFanTable + M1T1..M4T5）；它们随 GCU 服务分发，
    /// **不属应用树**。本计数器只用于断言清单规模，不代表磁盘现状（本机 <c>release\</c> 为空）。
    /// </summary>
    internal const int FlatPayloadFileCount = 23;

    internal static readonly string[] FlatPayloadFiles = BuildFlatPayloadFiles();

    internal static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    internal static string RepoPath(string relativePath) => Path.Combine(RepoRoot(), relativePath);

    /// <summary>逐机型文件（depth-2 的 *.json）的相对路径 -> SHA256。</summary>
    internal static IReadOnlyDictionary<string, string> PerModelHashes(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Split('/').Length != 2) continue;
            map[relative] = Sha256(file);
        }
        return map;
    }

    /// <summary>比对两棵树的逐机型文件；返回差异清单（左缺/右缺/内容不同），空清单即逐文件一致。</summary>
    internal static IReadOnlyList<string> ComparePerModelFiles(string leftRoot, string rightRoot)
    {
        IReadOnlyDictionary<string, string> left = PerModelHashes(leftRoot);
        IReadOnlyDictionary<string, string> right = PerModelHashes(rightRoot);
        var differences = new List<string>();

        foreach ((string relative, string hash) in left.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!right.TryGetValue(relative, out string? other)) differences.Add($"missing on right: {relative}");
            else if (!string.Equals(hash, other, StringComparison.OrdinalIgnoreCase)) differences.Add($"content differs: {relative}");
        }
        foreach (string relative in right.Keys.OrderBy(name => name, StringComparer.Ordinal))
            if (!left.ContainsKey(relative)) differences.Add($"missing on left: {relative}");

        return differences;
    }

    internal static int CountPerModelFiles(string root) => PerModelHashes(root).Count;

    /// <summary>顶层 JSON = flat 载荷文件；应用树里出现任何一个都属越界。</summary>
    internal static IReadOnlyList<string> TopLevelJsonFiles(string root) =>
        Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    static string[] BuildFlatPayloadFiles()
    {
        var files = new List<string> { "DefaultFanTable_Gaming.json", "DefaultFanTable_Office.json", "DefaultFanTable_Turbo.json" };
        for (int mode = 1; mode <= 4; mode++)
            for (int profile = 1; profile <= 5; profile++)
                files.Add($"M{mode}T{profile}.json");
        return files.ToArray();
    }
}
