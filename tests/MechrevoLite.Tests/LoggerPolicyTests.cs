using System.IO.Compression;
using System.Text;
using MechrevoLite.Diagnostics;

namespace MechrevoLite.Tests;

/// <summary>
/// 日志策略（Wave 1）的契约：三级过滤（OFF / ERROR_ONLY / ALL）、10 MB 截断、
/// 崩溃环形缓冲落盘，以及诊断包在 OFF 级别下仍然可取到证据。
///
/// 过滤与分类抽成纯函数先钉死语义；过滤与落盘再用真实 Logger 静态写入线程做一次端到端验证
/// （文件级断言，不看实现细节）。测试套件已禁用并行，共享的静态 Logger 状态安全。
/// </summary>
public class LoggerPolicyTests
{
    // ---------- 级别解析（纯函数）----------

    [Theory]
    [InlineData(null, Logger.LogLevel.Off)]
    [InlineData("", Logger.LogLevel.Off)]
    [InlineData("   ", Logger.LogLevel.Off)]
    [InlineData("off", Logger.LogLevel.Off)]
    [InlineData("OFF", Logger.LogLevel.Off)]
    [InlineData("none", Logger.LogLevel.Off)]
    [InlineData("error", Logger.LogLevel.ErrorOnly)]
    [InlineData("Error_Only", Logger.LogLevel.ErrorOnly)]
    [InlineData("erroronly", Logger.LogLevel.ErrorOnly)]
    [InlineData("all", Logger.LogLevel.All)]
    [InlineData("ALL", Logger.LogLevel.All)]
    [InlineData("verbose", Logger.LogLevel.All)]
    public void ParseLevel_MapsKnownValuesAndDefaultsToOff(string? raw, Logger.LogLevel expected)
    {
        // 任何无法识别的值都必须退化成 OFF：日志绝不能因为一个坏配置就开始刷盘。
        Assert.Equal(expected, Logger.ParseLevel(raw));
    }

    [Theory]
    [InlineData("all", "off", Logger.LogLevel.All)]          // 环境变量优先级最高
    [InlineData("error", "all", Logger.LogLevel.ErrorOnly)]
    [InlineData(null, "all", Logger.LogLevel.All)]           // 无覆盖时用配置值
    [InlineData("", "error", Logger.LogLevel.ErrorOnly)]
    [InlineData(null, null, Logger.LogLevel.Off)]            // 两者都没有 -> OFF
    public void ResolveLevel_EnvOverrideWinsOverConfig(string? envOverride, string? configValue, Logger.LogLevel expected)
    {
        Assert.Equal(expected, Logger.ResolveLevel(envOverride, configValue));
    }

    // ---------- 过滤判定（纯函数）----------

    [Theory]
    [InlineData(Logger.LogLevel.Off, Logger.LogSeverity.Info, false)]
    [InlineData(Logger.LogLevel.Off, Logger.LogSeverity.Error, false)]
    [InlineData(Logger.LogLevel.ErrorOnly, Logger.LogSeverity.Info, false)]
    [InlineData(Logger.LogLevel.ErrorOnly, Logger.LogSeverity.Error, true)]
    [InlineData(Logger.LogLevel.All, Logger.LogSeverity.Info, true)]
    [InlineData(Logger.LogLevel.All, Logger.LogSeverity.Error, true)]
    public void ShouldWrite_IsTheLevelPolicy(Logger.LogLevel level, Logger.LogSeverity severity, bool expected)
    {
        Assert.Equal(expected, Logger.ShouldWrite(level, severity));
    }

    // ---------- 失败行分类（纯函数）----------

    [Theory]
    [InlineData("MechrevoHw connect fail: timeout", true)]
    [InlineData("Unhandled: System.InvalidOperationException", true)]
    [InlineData("Config write failed: access denied", true)]
    [InlineData("更新包下载失败：网络错误", true)]
    [InlineData("温度遥测恢复请求失败: 无法连接", true)]
    [InlineData("App launched: 0.289.0", false)]
    [InlineData("Config loaded from C:\\x\\config.json", false)]
    [InlineData("安静信息行-off-marker-2741", false)]
    public void ClassifySeverity_FlagsFailureTextAsError(string line, bool isError)
    {
        // 现有 600 多处调用走的是无级别的 WriteLine。ERROR_ONLY 不能因此把真实失败静默丢弃，
        // 所以对无级别行做保守的失败词识别，命中即按错误级别记录。
        Assert.Equal(isError ? Logger.LogSeverity.Error : Logger.LogSeverity.Info,
            Logger.ClassifySeverity(line));
    }

    // ---------- 崩溃环形缓冲（纯单元）----------

    [Fact]
    public void CrashRingBuffer_KeepsOnlyTheMostRecentBytes()
    {
        var ring = new CrashRingBuffer(1024);
        for (int i = 0; i < 100; i++)
            ring.Append($"line-{i:D3}-" + new string('x', 50));

        Assert.True(ring.ByteCount <= 1024, $"环形缓冲占用 {ring.ByteCount} 字节，超过 1024 上限。");
        string snapshot = ring.Snapshot();
        Assert.Contains("line-099", snapshot);          // 最新保留
        Assert.DoesNotContain("line-000", snapshot);    // 最早的被挤掉
    }

    [Fact]
    public void CrashRingBuffer_AlwaysKeepsAtLeastTheNewestLineEvenWhenItAloneExceedsTheCap()
    {
        var ring = new CrashRingBuffer(16);
        ring.Append(new string('z', 200));

        Assert.Contains(new string('z', 200), ring.Snapshot());
    }

    // ---------- 10 MB 截断（真实文件流，与运行期同一实现）----------

    [Fact]
    public void MaxLogBytes_IsTenMegabytes()
    {
        Assert.Equal(10L * 1024 * 1024, Logger.MaxLogBytes);
    }

    [Fact]
    public void TruncateIfOversized_KeepsTheTailAndReclaimsSpace()
    {
        string path = Path.Combine(Path.GetTempPath(), "lm-logger-trim-" + Guid.NewGuid().ToString("N") + ".txt");
        const int retained = 2048;
        try
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024) { AutoFlush = false })
            {
                writer.Write(new string('A', (int)Logger.MaxLogBytes + 4096));
                writer.Write("尾部标记TAIL");
                writer.Flush();

                Logger.TruncateIfOversized(stream, writer, Logger.MaxLogBytes, retained);
                writer.Flush();

                Assert.True(stream.Length <= retained,
                    $"截断后文件 {stream.Length} 字节，超过保留上限 {retained}。");
            }

            using var reopened = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(reopened);
            string text = reader.ReadToEnd();
            Assert.Contains("尾部标记TAIL", text);   // 保留的是尾部，不是头部
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    // ---------- 端到端：级别 -> 文件 ----------

    [Fact]
    public void OffLevel_WritesNothingToTheLogFile()
    {
        using var _ = WithLevel("off");
        Thread.Sleep(700);   // 先让前一个用例可能排队的行落盘
        DeleteQuietly(Logger.logFile);

        Logger.WriteLine("安静信息行-off-marker-2741");
        Thread.Sleep(700);

        Assert.False(File.Exists(Logger.logFile),
            "OFF 级别不得创建也不得写入日志文件（零磁盘磨损）。");
    }

    [Fact]
    public void AllLevel_WritesInformationalLinesToTheLogFile()
    {
        using var _ = WithLevel("all");
        DeleteQuietly(Logger.logFile);

        string marker = "全量标记-ALL-8823";
        Logger.WriteLine(marker);

        Assert.True(WaitForLogFileContent(marker, TimeSpan.FromSeconds(5)),
            "ALL 级别必须把普通信息行写入日志文件。");
    }

    [Fact]
    public void ErrorOnlyLevel_WritesErrorsButNotInformationalLines()
    {
        using var _ = WithLevel("error");
        DeleteQuietly(Logger.logFile);

        string info = "安静信息行-ERRONLY-5510";
        const string explicitError = "显式错误行-ERRONLY-5511";
        const string classifiedError = "更新失败：ERRONLY-CLASSIFIED-5512";

        Logger.WriteLine(info);                              // 普通信息 -> 丢弃
        Logger.WriteError(explicitError);                    // 显式错误 -> 保留
        Logger.WriteLine(classifiedError);                   // 现有失败措辞 -> 保守识别后保留

        Assert.True(WaitForLogFileContent("ERRONLY-CLASSIFIED-5512", TimeSpan.FromSeconds(5)),
            "ERROR_ONLY 必须记录被识别为失败的行。");

        string text = ReadLogFile();
        Assert.Contains(explicitError, text);
        Assert.DoesNotContain(info, text);
    }

    // ---------- 崩溃环形缓冲落盘 ----------

    [Fact]
    public void CrashFlush_KeepsRingBufferEvidenceEvenWhenLoggingIsOff()
    {
        using var _ = WithLevel("off");
        DeleteQuietly(Logger.crashFile);

        const string marker = "崩溃缓冲标记-CRASH-9911";
        Logger.WriteLine(marker);
        Logger.FlushCrashBuffer("unit-test");

        Assert.True(File.Exists(Logger.crashFile), "崩溃时即使日志级别为 OFF，也必须落下环形缓冲证据。");
        string text = File.ReadAllText(Logger.crashFile);
        Assert.Contains(marker, text);
        Assert.Contains("unit-test", text);
    }

    // ---------- 诊断包在 OFF 下的证据链 ----------

    [Fact]
    public void DiagnosticPack_CarriesRingBufferSnapshotAndActiveLevel()
    {
        var inputs = new DiagnosticPackInputs(
            "0.1.2", "beta17", "系统信息正文",
            Array.Empty<DiagnosticPackFile>(), Array.Empty<DiagnosticPackFile>(),
            CrashRingBufferText: "环形缓冲快照-RING-7733",
            LogLevel: "Off");

        string zipPath = Path.Combine(Path.GetTempPath(), "lm-logger-diag-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            DiagnosticPackExporter.Export(zipPath, inputs);

            using ZipArchive zip = ZipFile.OpenRead(zipPath);
            ZipArchiveEntry? ring = zip.GetEntry(DiagnosticPackExporter.RingBufferEntry);
            Assert.NotNull(ring);
            using (var reader = new StreamReader(ring!.Open()))
                Assert.Contains("环形缓冲快照-RING-7733", reader.ReadToEnd());

            ZipArchiveEntry? info = zip.GetEntry(DiagnosticPackExporter.SystemInfoEntry);
            Assert.NotNull(info);
            using (var reader = new StreamReader(info!.Open()))
                Assert.Contains("Off", reader.ReadToEnd());
        }
        finally
        {
            try { File.Delete(zipPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void DiagnosticPackCapture_PlansTheLogAndCrashFiles()
    {
        var files = DiagnosticPackCapture.BuildLogFiles();

        Assert.Contains(files, f => f.SourcePath == Logger.logFile);
        Assert.Contains(files, f => f.SourcePath == Logger.crashFile);
    }

    // ---------- 测试辅助 ----------

    sealed class LevelScope : IDisposable
    {
        readonly string? _previous;
        public LevelScope(string level)
        {
            _previous = Environment.GetEnvironmentVariable(Logger.LevelOverrideVariable);
            Environment.SetEnvironmentVariable(Logger.LevelOverrideVariable, level);
        }
        public void Dispose() =>
            Environment.SetEnvironmentVariable(Logger.LevelOverrideVariable, _previous);
    }

    static LevelScope WithLevel(string level) => new(level);

    static void DeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* writer 可能短暂持有句柄 */ }
    }

    static string ReadLogFile()
    {
        using var stream = new FileStream(Logger.logFile, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    static bool WaitForLogFileContent(string needle, TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                if (File.Exists(Logger.logFile) && ReadLogFile().Contains(needle, StringComparison.Ordinal))
                    return true;
            }
            catch (IOException) { /* 与写入线程竞争读，重试 */ }
            Thread.Sleep(50);
        }
        return false;
    }
}
