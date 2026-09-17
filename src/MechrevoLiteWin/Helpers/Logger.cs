using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using MechrevoLite;
using MechrevoLite.Helpers;

public static class Logger
{
    /// <summary>
    /// 日志目录重定向开关（环境变量）。
    ///
    /// Logger 是静态类，直写 %AppData%\MechrevoLite\log.txt。测试进程里所有走 Logger 的
    /// 代码——包括用假对象驱动的模式切换、显卡切换、液冷灯效——都会把合成数据写进
    /// 用户的生产日志：实测一次全量测试产生约 900 行噪音，还会触发日志截断，
    /// 把用户真实的排查记录挤掉。而「让用户把 log.txt 发过来」正是本项目的支持方式。
    ///
    /// 设置该环境变量即可把日志改写到别处；测试程序集在模块初始化阶段指向临时目录。
    /// </summary>
    public const string LogDirectoryOverrideVariable = "LMECHREVO_LOG_DIR";

    /// <summary>
    /// 日志级别覆盖开关（环境变量）。空/未设置时回落到 AppConfig 的 log_level。
    /// 测试与 UI 审计用它强制打开日志，不受用户配置（默认 OFF）影响。
    /// </summary>
    public const string LevelOverrideVariable = "LMECHREVO_LOG_LEVEL";

    /// <summary>测试宿主进程名。命中任一项就绝不写生产日志。</summary>
    static readonly string[] TestHostProcessNames = { "testhost", "vstest.console", "dotnet-test" };

    public static readonly string appPath = ResolveAppPath();

    public static readonly string logFile = Path.Combine(appPath, "log.txt");

    /// <summary>
    /// 崩溃现场文件：进程异常退出时把内存环形缓冲里的最近日志追加到这里。
    /// 即使日志级别为 OFF，这份证据也必须留下——否则用户发来的日志永远是空的。
    /// </summary>
    public static readonly string crashFile = Path.Combine(appPath, "crash.txt");

    /// <summary>
    /// 解析日志目录，优先级：
    /// 1. <see cref="LogDirectoryOverrideVariable"/> 环境变量（CI 或自定义位置）；
    /// 2. 检测到测试宿主 → 落到测试输出目录下的 test-logs\；
    /// 3. 正常运行 → %AppData%（或 SYSTEM 身份下的 %ProgramData%）\MechrevoLite。
    ///
    /// 第 2 条是确定性兜底，不依赖任何时序。只用环境变量是不够的：appPath 是
    /// static readonly，第一次触碰 Logger 就固化，而在测试宿主里「谁先触碰 Logger」
    /// 与「谁先设好环境变量」之间存在竞争——实测同一份代码连续两次运行，
    /// 一次隔离成功、一次仍然写进了用户的生产日志。
    /// </summary>
    internal const string TestLogDirectoryName = "test-logs";

    internal static string ResolveAppPath() => ResolveAppPath(
        Environment.GetEnvironmentVariable(LogDirectoryOverrideVariable),
        IsTestHost(),
        AppContext.BaseDirectory,
        Path.Combine(
            Environment.GetFolderPath(ProcessHelper.IsRunningAsSystem()
                ? Environment.SpecialFolder.CommonApplicationData
                : Environment.SpecialFolder.ApplicationData),
            "MechrevoLite"));

    /// <summary>
    /// 目录决策的纯函数形式，便于把三条分支都测到（在测试宿主里没法把自己变成非测试宿主）。
    /// </summary>
    internal static string ResolveAppPath(string? overridden, bool isTestHost, string baseDirectory, string defaultDirectory)
    {
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            try { return Path.GetFullPath(overridden); }
            catch { /* 路径非法则继续往下走，日志绝不能因此中断 */ }
        }

        if (isTestHost)
        {
            try { return Path.Combine(baseDirectory, TestLogDirectoryName); }
            catch { /* 同上 */ }
        }

        return defaultDirectory;
    }

    /// <summary>当前进程是否是单元测试宿主。</summary>
    internal static bool IsTestHost()
    {
        try
        {
            if (IsTestHostProcessName(Process.GetCurrentProcess().ProcessName)) return true;
            // .NET 10's VSTest adapter can run inside a process named "dotnet".
            // Use loaded test-runner assemblies as the second signal instead of
            // treating every dotnet-hosted application as a test.
            return AppDomain.CurrentDomain.GetAssemblies()
                .Any(assembly => IsTestHostAssemblyName(assembly.GetName().Name));
        }
        catch { return false; }
    }

    internal static bool IsTestHostProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        foreach (string name in TestHostProcessNames)
            if (processName.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static bool IsTestHostAssemblyName(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName)) return false;
        return assemblyName.StartsWith("xunit.", StringComparison.OrdinalIgnoreCase) ||
            assemblyName.StartsWith("Microsoft.TestPlatform.", StringComparison.OrdinalIgnoreCase) ||
            assemblyName.Equals("testhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 日志级别策略。
    /// OFF：零磁盘磨损，什么都不写（崩溃环形缓冲仍留在内存）。
    /// ERROR_ONLY：只写被判定为错误的行——真实失败绝不静默丢弃。
    /// ALL：全量记录，排查问题用。
    /// </summary>
    public enum LogLevel
    {
        Off = 0,
        ErrorOnly = 1,
        All = 2,
    }

    /// <summary>单行的严重度。现有无级别调用按失败词识别，显式错误走 <see cref="WriteError"/>。</summary>
    public enum LogSeverity
    {
        Info = 0,
        Error = 1,
    }

    /// <summary>单文件上限 10 MB；写入线程在运行期截断，保留末尾 <see cref="RetainedLogBytes"/>。</summary>
    internal const long MaxLogBytes = 10L * 1024 * 1024;
    internal const int RetainedLogBytes = 1024 * 1024;

    /// <summary>崩溃环形缓冲保留的最近字节数。无论级别如何都在内存里滚动保留。</summary>
    internal const int CrashRingBufferBytes = 64 * 1024;

    static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>失败词识别表。现有 600 多处调用没有级别参数，ERROR_ONLY 靠它兜住真实失败。</summary>
    static readonly string[] ErrorMarkers =
    {
        "fail", "error", "exception", "unhandled", "unobserved", "cannot", "unable",
        "denied", "refused", "timeout", "失败", "错误", "异常", "无法", "拒绝", "超时", "损坏",
    };

    private static readonly ConcurrentQueue<string> Queue = new();
    private static readonly AutoResetEvent Pending = new(false);
    private static readonly ConcurrentDictionary<string, long> ThrottleTicks = new();
    private static readonly ConcurrentDictionary<string, string> LastMessages = new();
    private static readonly CrashRingBuffer Ring = new(CrashRingBufferBytes);
    private static readonly Thread WriterThread;
    private static int _closed;

    static Logger()
    {
        WriterThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "L-Mechrevo Logger",
            Priority = ThreadPriority.BelowNormal,
        };
        WriterThread.Start();
    }

    public static void WriteLine(string logMessage) => Write(logMessage, ClassifySeverity(logMessage));

    /// <summary>显式错误级别写入。ERROR_ONLY 下必定落盘；OFF 下仍进入崩溃环形缓冲。</summary>
    public static void WriteError(string logMessage) => Write(logMessage, LogSeverity.Error);

    public static void WriteLineThrottled(string key, string logMessage, int intervalMs = 5000)
    {
        long now = Environment.TickCount64;
        long previous = ThrottleTicks.GetOrAdd(key, long.MinValue / 2);
        if (now - previous < intervalMs) return;
        if (ThrottleTicks.TryUpdate(key, now, previous)) WriteLine(logMessage);
    }

    /// <summary>
    /// 只在内容相对上一次发生变化时才写入。
    ///
    /// 用于周期性状态回显（液冷、键盘灯、灯带、风扇表、超频支持位）。这些状态由 GCU
    /// 每几秒推一次，内容通常一模一样：<see cref="WriteLineThrottled"/> 只能限制频率、
    /// 不能消除重复，实测液冷一条状态行每 6 秒重复一次，一小时约 600 行完全相同的文本。
    /// 日志上限 10 MB 且会截断保留尾部，这类刷屏会把启动过程、异常、确认失败等真正需要
    /// 排查的内容挤出去——而本项目的支持方式就是让用户把日志发过来。
    ///
    /// 变化才记录，既保留了状态迁移的完整轨迹，又不会淹没其他内容。
    /// </summary>
    public static void WriteLineIfChanged(string key, string logMessage)
    {
        if (TryMarkChanged(key, logMessage)) WriteLine(logMessage);
    }

    /// <summary>
    /// 去重判定本身，与文件写入分离：返回 true 表示内容相对上一次有变化（并已记下新值）。
    /// 分开是为了让语义可测——测试不必真的往用户的 log.txt 里写行。
    /// </summary>
    internal static bool TryMarkChanged(string key, string logMessage)
    {
        if (LastMessages.TryGetValue(key, out string? previous) && previous == logMessage) return false;
        LastMessages[key] = logMessage;
        return true;
    }

    /// <summary>清除变化检测的记忆，让下一次写入无条件生效（重连后需要重新记录基线状态）。</summary>
    public static void ResetChangeTracking(string key) => LastMessages.TryRemove(key, out _);

    static void Write(string logMessage, LogSeverity severity)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}: {logMessage}";
        Debug.WriteLine(line);
        if (Volatile.Read(ref _closed) != 0) return;

        // 环形缓冲永远记录：级别只决定是否落盘，不决定是否留证。
        Ring.Append(line);
        if (!ShouldWrite(CurrentLevel, severity)) return;

        Queue.Enqueue(line);
        Pending.Set();
    }

    // ---------- 级别策略（纯函数，便于把每条分支都测到）----------

    internal static LogLevel CurrentLevel =>
        ResolveLevel(Environment.GetEnvironmentVariable(LevelOverrideVariable), TryGetConfiguredLevel());

    static string? TryGetConfiguredLevel()
    {
        try { return AppConfig.GetString("log_level"); }
        catch { return null; }
    }

    internal static LogLevel ResolveLevel(string? envOverride, string? configValue) =>
        ParseLevel(string.IsNullOrWhiteSpace(envOverride) ? configValue : envOverride);

    internal static LogLevel ParseLevel(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "all" or "verbose" => LogLevel.All,
        "error" or "error_only" or "erroronly" => LogLevel.ErrorOnly,
        _ => LogLevel.Off,
    };

    internal static bool ShouldWrite(LogLevel level, LogSeverity severity) => level switch
    {
        LogLevel.All => true,
        LogLevel.ErrorOnly => severity == LogSeverity.Error,
        _ => false,
    };

    internal static LogSeverity ClassifySeverity(string? message)
    {
        if (string.IsNullOrEmpty(message)) return LogSeverity.Info;
        foreach (string marker in ErrorMarkers)
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return LogSeverity.Error;
        return LogSeverity.Info;
    }

    // ---------- 写入线程 ----------

    private static void WriterLoop()
    {
        FileStream? stream = null;
        StreamWriter? writer = null;
        try
        {
            // 日志文件懒创建：OFF 级别下整条运行期不得产生任何磁盘写入（零磁盘磨损）。
            while (Volatile.Read(ref _closed) == 0 || !Queue.IsEmpty)
            {
                Pending.WaitOne(500);
                if (Queue.IsEmpty) continue;

                if (writer is not null && !File.Exists(logFile))
                {
                    // 外部清空了日志文件（用户排障时的常见操作）：丢弃旧句柄，重新创建。
                    writer.Dispose();
                    writer = null;
                    stream?.Dispose();
                    stream = null;
                }

                if (writer is null) (stream, writer) = OpenLogWriter();
                if (DrainQueue(writer!))
                {
                    writer!.Flush();
                    TruncateIfOversized(stream!, writer!, MaxLogBytes, RetainedLogBytes);
                }
            }

            if (!Queue.IsEmpty)
            {
                if (writer is null) (stream, writer) = OpenLogWriter();
                if (DrainQueue(writer!)) writer!.Flush();
            }
        }
        catch
        {
            // Logging must never affect the control path.
        }
        finally
        {
            try { writer?.Dispose(); } catch { }
            try { stream?.Dispose(); } catch { }
        }
    }

    static (FileStream Stream, StreamWriter Writer) OpenLogWriter()
    {
        Directory.CreateDirectory(appPath);
        var stream = new FileStream(logFile, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
        var writer = new StreamWriter(stream, Utf8NoBom, 64 * 1024) { AutoFlush = false };
        return (stream, writer);
    }

    private static bool DrainQueue(StreamWriter writer)
    {
        bool wrote = false;
        while (Queue.TryDequeue(out string? line))
        {
            writer.WriteLine(line);
            wrote = true;
        }
        return wrote;
    }

    /// <summary>
    /// 超过上限时把文件截成保留末尾 <paramref name="retainedBytes"/> 字节。
    /// 只在写入线程调用，流与 writer 都是该线程独占，不存在数据竞争，也不阻塞 UI 线程。
    /// </summary>
    internal static void TruncateIfOversized(
        FileStream stream, StreamWriter writer, long maxBytes, int retainedBytes)
    {
        if (stream.Length <= maxBytes) return;

        writer.Flush();
        string tail = ReadTail(stream, retainedBytes);
        stream.SetLength(0);
        stream.Position = 0;
        writer.Write(tail);
        writer.Flush();
    }

    static string ReadTail(FileStream stream, int maxBytes)
    {
        long start = Math.Max(0, stream.Length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[stream.Length - start];
        int read = 0;
        while (read < buffer.Length)
        {
            int count = stream.Read(buffer, read, buffer.Length - read);
            if (count <= 0) break;
            read += count;
        }

        string text = Encoding.UTF8.GetString(buffer, 0, read);
        if (start > 0)
        {
            // 从截断点开始可能落在半行中间，丢掉第一段不完整的行。
            int newline = text.IndexOf('\n');
            if (newline >= 0) text = text[(newline + 1)..];
        }
        return text;
    }

    // ---------- 崩溃证据 ----------

    /// <summary>把内存环形缓冲快照追加到崩溃文件（附带原因与当时的级别）。</summary>
    internal static void FlushCrashBuffer(string reason)
    {
        try
        {
            string header =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}: === crash: {reason} (log_level={CurrentLevel}) ===";
            string? directory = Path.GetDirectoryName(crashFile);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.AppendAllText(crashFile, header + Environment.NewLine, Utf8NoBom);
            Ring.FlushTo(crashFile);
        }
        catch
        {
            // 异常处理器里绝不能再抛。
        }
    }

    /// <summary>当前环形缓冲的文本快照（诊断包导出用，OFF 级别下也非空）。</summary>
    internal static string SnapshotRingBuffer() => Ring.Snapshot();

    public static void Cleanup()
    {
        // 运行期截断由写入线程在超过 10 MB 时完成；这里保留空实现以兼容既有调用点。
    }

    public static void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Pending.Set();
        try { WriterThread.Join(2000); } catch { }
        Pending.Dispose();
    }
}

/// <summary>
/// 崩溃环形缓冲：按字节上限滚动保留最近若干行日志，与日志级别无关。
/// 这是「OFF 默认」下仍能拿到现场证据的兜底——用户不必为了排障提前开日志。
/// </summary>
internal sealed class CrashRingBuffer
{
    readonly object _lock = new();
    readonly Queue<string> _lines = new();
    readonly int _maxBytes;
    long _bytes;

    internal CrashRingBuffer(int maxBytes) => _maxBytes = Math.Max(1, maxBytes);

    internal int ByteCount { get { lock (_lock) return (int)_bytes; } }

    internal int LineCount { get { lock (_lock) return _lines.Count; } }

    internal void Append(string line)
    {
        lock (_lock)
        {
            _lines.Enqueue(line);
            _bytes += SizeOf(line);
            // 至少保留最新一行：单行超过上限时也不能把自己挤掉。
            while (_bytes > _maxBytes && _lines.Count > 1)
                _bytes -= SizeOf(_lines.Dequeue());
        }
    }

    internal string Snapshot()
    {
        lock (_lock) return string.Join(Environment.NewLine, _lines);
    }

    internal void FlushTo(string path)
    {
        lock (_lock)
        {
            if (_lines.Count == 0) return;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.AppendAllText(path, string.Join(Environment.NewLine, _lines) + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    static int SizeOf(string line) => Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
}
