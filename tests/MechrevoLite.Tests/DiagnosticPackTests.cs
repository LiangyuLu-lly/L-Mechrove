using System.IO.Compression;
using MechrevoLite.Diagnostics;

namespace MechrevoLite.Tests;

/// <summary>
/// 诊断包导出（「导出诊断包」功能）的核心契约：
/// 期望条目、缺失文件不致命、超大日志截断并如实记录、文件名带版本与时间戳、
/// 以及导出经线程池调度（不占用调用线程/UI 线程）。
///
/// 导出器是纯文件逻辑（无 UI、无网络），因此这些断言全部走真实 zip 文件。
/// </summary>
public class DiagnosticPackExporterTests : IDisposable
{
    const string Version = "0.289.0-beta15";
    const string Label = "beta15";

    readonly string _dir;

    public DiagnosticPackExporterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", "diag-pack", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    static string ReadEntry(ZipArchive zip, string name)
    {
        ZipArchiveEntry? entry = zip.GetEntry(name);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open());
        return reader.ReadToEnd();
    }

    static DiagnosticPackInputs Inputs(
        string systemInfo,
        IReadOnlyList<DiagnosticPackFile>? logs = null,
        IReadOnlyList<DiagnosticPackFile>? configs = null) =>
        new(Version, Label, systemInfo,
            logs ?? Array.Empty<DiagnosticPackFile>(),
            configs ?? Array.Empty<DiagnosticPackFile>());

    [Fact]
    public void Export_ProducesTheExpectedEntriesWithRealContent()
    {
        string logPath = Path.Combine(_dir, "log.txt");
        File.WriteAllText(logPath, "第一行 应用启动\n第二行 连接硬件\n");
        string configPath = Path.Combine(_dir, "config.json");
        File.WriteAllText(configPath, "{\"performance_mode\":0}");

        var inputs = Inputs("系统信息正文标记",
            logs: new[] { new DiagnosticPackFile(logPath, "日志/log.txt", "应用运行日志") },
            configs: new[] { new DiagnosticPackFile(configPath, "配置/config.json", "主配置") });

        string zipPath = Path.Combine(_dir, "pack.zip");
        DiagnosticPackResult result = DiagnosticPackExporter.Export(zipPath, inputs);

        Assert.True(File.Exists(zipPath));
        Assert.True(result.SizeBytes > 0);

        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).ToList();

        Assert.Contains("系统信息.txt", names);
        Assert.Contains("说明.txt", names);
        Assert.Contains("日志/", names);
        Assert.Contains("配置/", names);
        Assert.Contains("配置/README-配置.txt", names);
        Assert.Contains("日志/log.txt", names);
        Assert.Contains("配置/config.json", names);

        Assert.Equal(names, result.Entries);

        Assert.Contains("系统信息正文标记", ReadEntry(zip, "系统信息.txt"));
        Assert.Contains("第一行 应用启动", ReadEntry(zip, "日志/log.txt"));
        Assert.Contains("config.json", ReadEntry(zip, "配置/README-配置.txt"));
    }

    [Fact]
    public void Export_WithMissingLogAndConfig_StillSucceedsAndRecordsAbsenceInManifest()
    {
        var inputs = Inputs("系统信息正文",
            logs: new[] { new DiagnosticPackFile(Path.Combine(_dir, "missing-log.txt"), "日志/missing-log.txt", "应用运行日志") },
            configs: new[] { new DiagnosticPackFile(Path.Combine(_dir, "missing-config.json"), "配置/missing-config.json", "主配置") });

        string zipPath = Path.Combine(_dir, "missing.zip");
        DiagnosticPackResult result = DiagnosticPackExporter.Export(zipPath, inputs);   // 不得抛

        Assert.True(File.Exists(zipPath));

        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.DoesNotContain("日志/missing-log.txt", names);
        Assert.DoesNotContain("配置/missing-config.json", names);

        string manifest = ReadEntry(zip, "系统信息.txt");
        Assert.Contains("日志/missing-log.txt", manifest);
        Assert.Contains("配置/missing-config.json", manifest);
        Assert.Contains("缺失", manifest);
        Assert.Equal(names, result.Entries);
    }

    [Fact]
    public void Export_CapsOversizedLogAndSaysSoInTheManifest()
    {
        string logPath = Path.Combine(_dir, "big-log.txt");
        File.WriteAllText(logPath, new string('A', 4096) + "尾部标记");

        var inputs = Inputs("系统信息正文",
            logs: new[] { new DiagnosticPackFile(logPath, "日志/big-log.txt", "应用运行日志") });

        string zipPath = Path.Combine(_dir, "capped.zip");
        DiagnosticPackExporter.Export(zipPath, inputs, maxLogBytes: 1024);

        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry? entry = zip.GetEntry("日志/big-log.txt");
        Assert.NotNull(entry);
        Assert.True(entry!.Length <= 1024, $"日志条目 {entry.Length} 字节超过 1024 上限");

        string manifest = ReadEntry(zip, "系统信息.txt");
        Assert.Contains("截断", manifest);
    }

    [Fact]
    public void BuildFileName_ContainsVersionAndTimestamp()
    {
        string name = DiagnosticPackExporter.BuildFileName("0.289.0-beta15", new DateTime(2026, 9, 15, 8, 5, 9));

        Assert.Equal("L-Mechrevo-诊断包-0.289.0-beta15-20260915-080509.zip", name);
        Assert.StartsWith("L-Mechrevo-诊断包-", name);
        Assert.EndsWith(".zip", name);
    }

    [Fact]
    public void Export_GuideMentionsIssuesUrlVersionAndNoAutoUpload()
    {
        string zipPath = Path.Combine(_dir, "guide.zip");
        DiagnosticPackExporter.Export(zipPath, Inputs("正文"));

        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        string guide = ReadEntry(zip, "说明.txt");
        Assert.Contains(DiagnosticPackExporter.IssuesUrl, guide);
        Assert.Contains(Version, guide);
        Assert.Contains("不会自动上传", guide);
    }

    [Fact]
    public async Task ExportAsync_RunsOffTheCallingThread()
    {
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        Task<int> task = DiagnosticPackRunner.RunOffCallingThread(() =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return Environment.CurrentManagedThreadId;
        });

        // 工作不能在调用线程上同步跑完——否则意味着 UI 线程被阻塞。
        Assert.False(task.IsCompleted, "导出必须异步调度，而不是在调用线程上阻塞执行。");
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "工作未在调用线程之外的线程启动。");
        release.Set();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExportAsync_WritesTheZipOffTheUiThread()
    {
        string zipPath = Path.Combine(_dir, "async.zip");
        DiagnosticPackResult result = await DiagnosticPackRunner.ExportAsync(zipPath, Inputs("正文"));

        Assert.True(File.Exists(result.ZipPath));
        using ZipArchive zip = ZipFile.OpenRead(result.ZipPath);
        Assert.Contains("系统信息.txt", zip.Entries.Select(e => e.FullName));
    }
}
