using System.Collections.Concurrent;
using System.Diagnostics;
using MechrevoLite;
using MechrevoLite.Helpers;

public static class Logger
{
    /// <summary>
    /// 日志目录重定向开关（环境变量）。
    ///
    /// Logger 是静态类，直写 %AppData%\MechrevoLite\log.txt。测试进程里所有走 Logger 的
    /// 代码——包括用假对象驱动的模式切换、显卡切换、液冷灯效——都会把合成数据写进
    /// 用户的生产日志：实测一次全量测试产生约 900 行噪音，还会触发 TrimLogIfNeeded，
    /// 把用户真实的排查记录挤掉。而「让用户把 log.txt 发过来」正是本项目的支持方式。
    ///
    /// 设置该环境变量即可把日志改写到别处；测试程序集在模块初始化阶段指向临时目录。
    /// </summary>
    public const string LogDirectoryOverrideVariable = "LMECHREVO_LOG_DIR";

    /// <summary>测试宿主进程名。命中任一项就绝不写生产日志。</summary>
    static readonly string[] TestHostProcessNames = { "testhost", "vstest.console", "dotnet-test" };

    public static readonly string appPath = ResolveAppPath();

    public static readonly string logFile = Path.Combine(appPath, "log.txt");

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

    private const long MaxLogBytes = 2 * 1024 * 1024;
    private const int RetainedLogBytes = 1024 * 1024;
    private static readonly ConcurrentQueue<string> Queue = new();
    private static readonly AutoResetEvent Pending = new(false);
    private static readonly ConcurrentDictionary<string, long> ThrottleTicks = new();
    private static readonly ConcurrentDictionary<string, string> LastMessages = new();
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

    public static void WriteLine(string logMessage)
    {
        Debug.WriteLine($"{DateTime.Now}: {logMessage}");
        if (Volatile.Read(ref _closed) != 0) return;

        Queue.Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}: {logMessage}");
        Pending.Set();
    }

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
    /// 日志上限 2 MB 且会截断保留尾部，这类刷屏会把启动过程、异常、确认失败等真正需要
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

    private static void WriterLoop()
    {
        try
        {
            Directory.CreateDirectory(appPath);
            TrimLogIfNeeded();
            using var stream = new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 64 * 1024);
            using var writer = new StreamWriter(
                stream,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                64 * 1024)
            {
                AutoFlush = false,
            };

            while (Volatile.Read(ref _closed) == 0 || !Queue.IsEmpty)
            {
                Pending.WaitOne(500);
                bool wrote = DrainQueue(writer);
                if (wrote) writer.Flush();
            }

            DrainQueue(writer);
            writer.Flush();
        }
        catch
        {
            // Logging must never affect the control path.
        }
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

    public static void Cleanup()
    {
        // Rotation is performed before opening the shared writer. Runtime logging stays append-only.
    }

    private static void TrimLogIfNeeded()
    {
        try
        {
            var file = new FileInfo(logFile);
            if (!file.Exists || file.Length <= MaxLogBytes) return;

            using var input = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            input.Seek(-Math.Min(input.Length, RetainedLogBytes), SeekOrigin.End);
            using var reader = new StreamReader(input);
            if (input.Position > 0) reader.ReadLine();
            string retained = reader.ReadToEnd();
            File.WriteAllText(logFile, retained, new System.Text.UTF8Encoding(false));
        }
        catch { }
    }

    public static void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Pending.Set();
        try { WriterThread.Join(2000); } catch { }
        Pending.Dispose();
    }
}
