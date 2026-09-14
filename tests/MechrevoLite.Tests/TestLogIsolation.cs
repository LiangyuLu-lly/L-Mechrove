using System.Runtime.CompilerServices;

namespace MechrevoLite.Tests;

/// <summary>
/// 兜底的日志重定向。
///
/// 起因是真机排查时发现的问题：Logger 是静态类，直写
/// <c>%AppData%\MechrevoLite\log.txt</c>。测试里用假对象驱动模式切换、显卡切换、
/// 液冷灯效的代码同样走 Logger，于是一次全量测试就往用户的生产日志里灌进约 900 行
/// 合成数据（"WinPowerPlan SetActivePlan rejected invalid GUID: not-a-power-plan"、
/// "MechrevoHw parse fail ... JsonReaderException" 这类），并且会触发日志截断，
/// 把用户真实的排查记录挤掉。而本项目的支持方式就是让用户把这份日志发过来。
///
/// 真正的隔离由 <c>xunit.runsettings</c> 完成——环境变量在测试宿主进程启动前就设好。
/// 这里的模块初始化器只是兜底：直接运行测试 DLL、不走 runsettings 时仍尽量隔离。
/// 但它与「宿主里谁先触碰 Logger」存在竞争（实测同一份代码两次运行结果不同），
/// 所以不能只靠它，断言也不以它为准。
/// </summary>
internal static class TestLogIsolation
{
    internal const string FallbackDirectoryName = "test-logs-fallback";

    [ModuleInitializer]
    internal static void Redirect()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Logger.LogDirectoryOverrideVariable)))
            return;   // runsettings 已经设好，不覆盖

        Environment.SetEnvironmentVariable(
            Logger.LogDirectoryOverrideVariable,
            Path.Combine(Path.GetTempPath(), FallbackDirectoryName));
    }
}

public class TestLogIsolationTests
{
    [Fact]
    public void TestRunNeverWritesToTheProductionLog()
    {
        // 这是唯一真正重要的不变式：测试进程绝不能写进用户的生产日志。
        string production = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MechrevoLite"));

        Assert.NotEqual(
            production.TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(Logger.appPath).TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void LogPathStaysInsideTheTestOutputOrTheConfiguredOverride()
    {
        string? configured = Environment.GetEnvironmentVariable(Logger.LogDirectoryOverrideVariable);
        string expected = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "test-logs")
            : configured;

        Assert.Equal(
            Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(Logger.appPath).TrimEnd(Path.DirectorySeparatorChar));
        Assert.StartsWith(Logger.appPath, Logger.logFile);
    }

    [Fact]
    public void TestHostIsDetectedWithoutRelyingOnEnvironmentVariableTiming()
    {
        // 这是确定性兜底：不依赖任何时序。只靠环境变量不够——appPath 是 static readonly，
        // 第一次触碰 Logger 就固化，而「谁先触碰 Logger」与「谁先设好环境变量」之间
        // 存在竞争（实测连续两次运行结果不同）。
        Assert.True(Logger.IsTestHost(), "当前进程应当被识别为测试宿主。");
    }

    [Theory]
    [InlineData("testhost", true)]
    [InlineData("testhost.x86", true)]
    [InlineData("vstest.console", true)]
    [InlineData("dotnet-test", true)]
    [InlineData("TESTHOST", true)]
    [InlineData("L-Mechrevo", false)]
    [InlineData("explorer", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TestHostNameMatching_OnlyMatchesRealTestHosts(string? processName, bool expected)
    {
        // 不能把正常运行的 L-Mechrevo 误判成测试宿主，否则用户就再也拿不到日志了。
        Assert.Equal(expected, Logger.IsTestHostProcessName(processName));
    }

    [Theory]
    [InlineData("xunit.execution.dotnet", true)]
    [InlineData("xunit.core", true)]
    [InlineData("Microsoft.TestPlatform.CrossPlatEngine", true)]
    [InlineData("L-Mechrevo", false)]
    [InlineData("System.Runtime", false)]
    [InlineData(null, false)]
    public void TestRuntimeAssemblyMatchingDistinguishesDotnetTestHost(
        string? assemblyName, bool expected) =>
        Assert.Equal(expected, Logger.IsTestHostAssemblyName(assemblyName));

    const string Base = @"C:\app\bin";
    const string Default = @"C:\Users\me\AppData\Roaming\MechrevoLite";

    [Fact]
    public void OverrideWinsOverEverythingElse()
    {
        Assert.Equal(
            Path.GetFullPath(@"D:\custom-logs"),
            Logger.ResolveAppPath(@"D:\custom-logs", isTestHost: true, Base, Default));
        Assert.Equal(
            Path.GetFullPath(@"D:\custom-logs"),
            Logger.ResolveAppPath(@"D:\custom-logs", isTestHost: false, Base, Default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutOverride_TestHostGoesToTheTestOutputDirectory(string? overridden)
    {
        // 确定性兜底：即使环境变量没设上，测试宿主也绝不会碰生产日志。
        Assert.Equal(
            Path.Combine(Base, Logger.TestLogDirectoryName),
            Logger.ResolveAppPath(overridden, isTestHost: true, Base, Default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutOverride_NormalRunGoesToTheDefaultLocation(string? overridden)
    {
        // 正常运行必须仍然写到 %AppData%，否则用户就再也拿不到日志了。
        Assert.Equal(Default, Logger.ResolveAppPath(overridden, isTestHost: false, Base, Default));
    }

    [Fact]
    public void InvalidOverridePathFallsBackInsteadOfThrowing()
    {
        // 日志绝不能因为一个非法路径就把整个程序拖崩。
        Assert.Equal(Default, Logger.ResolveAppPath("\0invalid\0path", isTestHost: false, Base, Default));
        Assert.Equal(
            Path.Combine(Base, Logger.TestLogDirectoryName),
            Logger.ResolveAppPath("\0invalid\0path", isTestHost: true, Base, Default));
    }

    [Fact]
    public void RelativeOverrideIsResolvedToAnAbsolutePath()
    {
        string resolved = Logger.ResolveAppPath("relative-log-dir", isTestHost: false, Base, Default);
        Assert.True(Path.IsPathFullyQualified(resolved));
        Assert.EndsWith("relative-log-dir", resolved.TrimEnd(Path.DirectorySeparatorChar));
    }
}
