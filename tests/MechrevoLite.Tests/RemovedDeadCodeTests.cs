namespace MechrevoLite.Tests;

/// <summary>
/// beta17 死代码删除的防回归护栏（docs/beta17-wave-plan.md T2.1）：
/// 已证死的能力模型（OfficialConsoleCatalog / DeviceCapabilitySnapshot）、
/// 孤立 WPF 原型（src/MechrevoLite/）与未被引用的资源（favicon_backup.ico）
/// 一旦回归就红——删除必须可被机器看见，否则半年后没人记得为什么删。
/// </summary>
public class RemovedDeadCodeTests
{
    static readonly string[] ForbiddenCapabilityModelNames =
    {
        "OfficialConsoleCatalog",
        "DeviceCapabilitySnapshot",
    };

    [Fact]
    public void RemovedCapabilityModel_DoesNotReappearInSource()
    {
        var violations = new List<string>();
        foreach (string path in SourceFiles())
        {
            string relative = Path.GetRelativePath(RepoRoot, path);
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
                foreach (string needle in ForbiddenCapabilityModelNames)
                {
                    if (!lines[i].Contains(needle, StringComparison.Ordinal)) continue;
                    violations.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
        }

        Assert.True(violations.Count == 0,
            "已删除的死代码不得回归（OfficialConsoleCatalog / DeviceCapabilitySnapshot）：\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void ArchivedPrototypeAndUnreferencedIcon_DoNotExist()
    {
        Assert.False(Directory.Exists(PrototypeRoot),
            "孤立 WPF 原型 src/MechrevoLite/ 已删除（其 DEPRECATED.md 声明不参与构建与发布），不得回归。");
        Assert.False(File.Exists(BackupIcon),
            "未被任何脚本/安装器引用的 favicon_backup.ico 已删除，不得回归。");
    }

    static string PrototypeRoot => Path.Combine(RepoRoot, "src", "MechrevoLite");

    static string BackupIcon => Path.Combine(RepoRoot, "src", "MechrevoLiteWin", "favicon_backup.ico");

    static IEnumerable<string> SourceFiles()
    {
        string root = Path.Combine(RepoRoot, "src");
        Assert.True(Directory.Exists(root), "找不到 src");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    static string RepoRoot
    {
        get
        {
            _repoRoot ??= FindRepoRoot();
            return _repoRoot!;
        }
    }

    static string? _repoRoot;

    static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
