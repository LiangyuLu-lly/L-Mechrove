using MechrevoLite.Helpers;

namespace MechrevoLite.Tests;

/// <summary>
/// 官方控制台隔离已删除：端口不得再暴露该类，灯效恢复不得再调用杀进程等待。
/// </summary>
public class OfficialConsoleIsolationRemovalTests
{
    [Fact]
    public void Port_NoLongerExposesOfficialConsoleIsolation()
    {
        var violations = new List<string>();

        if (typeof(GcuCoexistence).Assembly.GetType(
                "MechrevoLite.Helpers.OfficialConsoleIsolation", throwOnError: false) is not null)
            violations.Add("assembly still exposes MechrevoLite.Helpers.OfficialConsoleIsolation");

        string isolationFile = GcuInstallerHarness.Path(
            "src", "MechrevoLiteWin", "Helpers", "OfficialConsoleIsolation.cs");
        if (File.Exists(isolationFile))
            violations.Add("src/MechrevoLiteWin/Helpers/OfficialConsoleIsolation.cs still exists");

        foreach (string path in SourceFiles())
        {
            string relative = Path.GetRelativePath(GcuInstallerHarness.RepoRoot, path)
                .Replace('\\', '/');
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Contains("OfficialConsoleIsolation", StringComparison.Ordinal))
                    violations.Add($"{relative}:{i + 1}: still names OfficialConsoleIsolation");
                if (line.Contains("WaitForUiSettledAsync", StringComparison.Ordinal))
                    violations.Add($"{relative}:{i + 1}: live WaitForUiSettledAsync call");
                if (line.Contains("StopGuard(", StringComparison.Ordinal))
                    violations.Add($"{relative}:{i + 1}: live StopGuard call");
            }
        }

        Assert.True(violations.Count == 0,
            "官方控制台隔离已删除，端口不得再暴露：\n  " + string.Join("\n  ", violations));
    }

    static IEnumerable<string> SourceFiles()
    {
        string root = GcuInstallerHarness.Path("src");
        Assert.True(Directory.Exists(root), "找不到 src");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
