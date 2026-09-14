using System.Reflection;
using System.Text.RegularExpressions;

namespace MechrevoLite.Tests;

/// <summary>
/// 可空性配置的防回退守卫。
///
/// 背景：主项目此前是 Nullable=annotations——代码里到处写 <c>?</c> 和 <c>!</c>，
/// 但编译器一个 CS86xx 都不报，于是 <c>WindowsIdentity.GetCurrent().User.Value</c>
/// 这类必然抛 TypeInitializationException 的空引用能无声通过编译。
/// 现在改成 enable，核心层已清零，遗留 UI 文件用 <c>#nullable disable warnings</c>
/// 显式标注。这些测试保证有人不会把开关又拨回去，也保证核心层不会新增豁免。
/// </summary>
public class NullabilityPolicyTests
{
    /// <summary>沿目录向上找仓库根（含 MechrevoLite.slnx 的那一层）。</summary>
    static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!;
    }

    static string MainProjectDirectory() =>
        Path.Combine(RepositoryRoot().FullName, "src", "MechrevoLiteWin");

    /// <summary>
    /// 主项目必须开启完整的可空性检查，不能退回 annotations / disable。
    /// </summary>
    [Fact]
    public void MainProjectEnablesFullNullableChecking()
    {
        string csproj = File.ReadAllText(
            Path.Combine(MainProjectDirectory(), "MechrevoLite.csproj"));

        Match match = Regex.Match(csproj, @"<Nullable>\s*(?<value>[^<\s]+)\s*</Nullable>");

        Assert.True(match.Success, "csproj 里必须显式声明 <Nullable>");
        Assert.Equal("enable", match.Groups["value"].Value);
    }

    /// <summary>
    /// 核心层（硬件协议、Helpers、GPU）不允许豁免可空性警告。
    /// 这些是空引用会让硬件操作静默失败的地方，必须始终受检。
    /// </summary>
    [Theory]
    [InlineData("Hardware")]
    [InlineData("Helpers")]
    [InlineData("Gpu")]
    public void CoreLayersHaveNoNullabilityExemptions(string relativeFolder)
    {
        string folder = Path.Combine(MainProjectDirectory(), relativeFolder);
        Assert.True(Directory.Exists(folder), $"找不到核心目录：{folder}");

        var exempt = Directory
            .EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("#nullable disable", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(exempt.Count == 0,
            $"{relativeFolder} 下不应有可空性豁免，实际有：{string.Join(", ", exempt)}");
    }

    /// <summary>
    /// 待迁移文件只能关闭「警告」，不能连注解上下文一起关掉。
    /// 裸 <c>#nullable disable</c> 会让文件里已有的 <c>?</c> 全部报 CS8632，
    /// 而且会把该文件的类型签名对外暴露成「可空性未知」，污染调用方的推断。
    /// </summary>
    [Fact]
    public void PendingMigrationFilesOnlySuppressWarningsNotAnnotations()
    {
        var offenders = new List<string>();
        foreach (string path in Directory.EnumerateFiles(
            MainProjectDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            string text = File.ReadAllText(path);
            if (!text.Contains("#nullable disable", StringComparison.Ordinal)) continue;
            if (Regex.IsMatch(text, @"#nullable\s+disable(?!\s+warnings)"))
                offenders.Add(Path.GetFileName(path));
        }

        Assert.True(offenders.Count == 0,
            $"这些文件应改用 `#nullable disable warnings`：{string.Join(", ", offenders)}");
    }

    /// <summary>
    /// 豁免清单只应缩小不应扩大。这个上限是当前遗留 UI/原生互操作文件的数量，
    /// 迁移完一个就把它调低；想加新豁免时会先撞到这个断言。
    /// </summary>
    [Fact]
    public void NullabilityExemptionsDoNotGrow()
    {
        const int currentExemptFileCount = 15;

        int exempt = Directory
            .EnumerateFiles(MainProjectDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Count(path => File.ReadAllText(path).Contains("#nullable disable", StringComparison.Ordinal));

        Assert.True(exempt <= currentExemptFileCount,
            $"可空性豁免文件数从 {currentExemptFileCount} 涨到了 {exempt}；新代码不应豁免。");
    }
}
