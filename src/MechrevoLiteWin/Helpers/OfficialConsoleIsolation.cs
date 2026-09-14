using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MechrevoLite.Helpers;

public static class OfficialConsoleIsolation
{
    private const int SnapshotVersion = 1;

    /// <summary>被我们禁用的启动目录文件所加的后缀。恢复校验依赖它，必须只有一处定义。</summary>
    internal const string DisabledStartupSuffix = ".lmechrevo-disabled";

    /// <summary>
    /// 不允许触碰的计划任务路径前缀。过去采集阶段遍历 service.AllTasks（全机所有任务，
    /// 含 Microsoft\Windows\*），只要任务名或 ExecAction 的参数里出现过标记字符串就会被禁用，
    /// 以管理员运行时可以关掉系统维护任务。
    /// </summary>
    static readonly string[] ProtectedTaskFolders =
    {
        @"\Microsoft\",
    };

    /// <summary>该计划任务是否允许被我们启用/禁用。</summary>
    internal static bool IsManageableTaskPath(string? taskPath)
    {
        if (string.IsNullOrWhiteSpace(taskPath)) return false;
        string normalized = taskPath.Replace('/', '\\');
        if (!normalized.StartsWith('\\')) normalized = "\\" + normalized;
        foreach (string protectedFolder in ProtectedTaskFolders)
            if (normalized.StartsWith(protectedFolder, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
    private const string PackageAumid = "CCU.WinUI_wrbgcf7aesyd8!App";
    private static readonly string[] UiProcessNames =
    {
        "CCUWinUI", "SystrayComponent", "ControlCenterU", "GamingCenterU", "GCUUI",
    };
    // 标记必须足够具体。过去这里有 "CCUWinUI" 和 "SystrayComponent" 两个裸名，
    // 配合「整个文件字节按 ASCII+UTF16 解码后子串匹配」和「遍历全机所有计划任务并匹配参数」，
    // 很容易误删无关的开机启动项、误禁无关的任务。
    // 现在统一要求 .exe 后缀或包族名：真实的启动项/任务动作指向的就是可执行文件。
    private static readonly string[] OfficialMarkers =
    {
        "CCUWinUI.exe", "SystrayComponent.exe", "ControlCenterU.exe", "GamingCenterU.exe",
        "GCUUI.exe", "CCU.WinUI_", "CCU.WinUI_wrbgcf7aesyd8",
    };
    private static readonly string[] RunKeyPaths =
    {
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    private static readonly object StateLock = new();
    private static readonly object InstallationProbeLock = new();
    private static System.Threading.Timer? guardTimer;
    private static int guardRunning;
    private static bool? installedCache;
    private static long installedCacheUntil;

    public sealed record IsolationStatus(
        bool Isolated,
        bool OfficialUiRunning,
        bool OfficialUiInstalled,
        bool GcuRunning,
        string Detail);

    public sealed record OperationResult(bool Success, string Message, int AffectedItems);

    private sealed class IsolationSnapshot
    {
        public int Version { get; set; } = SnapshotVersion;
        public bool Isolated { get; set; }
        public bool OfficialUiWasRunning { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        public List<RegistryStartupEntry> RegistryEntries { get; set; } = new();
        public List<ScheduledTaskEntry> ScheduledTasks { get; set; } = new();
        public List<StartupFileEntry> StartupFiles { get; set; } = new();
    }

    private sealed class RegistryStartupEntry
    {
        public string Hive { get; set; } = string.Empty;
        public RegistryView View { get; set; }
        public string KeyPath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public RegistryValueKind Kind { get; set; } = RegistryValueKind.String;
    }

    private sealed class ScheduledTaskEntry
    {
        public string Path { get; set; } = string.Empty;
        public bool WasEnabled { get; set; }
    }

    private sealed class StartupFileEntry
    {
        public string OriginalPath { get; set; } = string.Empty;
        public string DisabledPath { get; set; } = string.Empty;
    }

    public static string SnapshotPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "L-Mechrevo", "OfficialConsoleState.json");

    public static IsolationStatus GetStatus(bool runtimeGcuConnected = false)
    {
        bool isolated = TryReadSnapshot()?.Isolated == true;
        bool uiRunning = IsOfficialUiRunning();
        bool installed = IsOfficialUiInstalled();
        bool gcuRunning = runtimeGcuConnected ||
            (IsProcessRunning("GCUBridge") && IsProcessRunning("GCUService"));
        string detail = DescribeStatus(isolated, uiRunning, installed, gcuRunning);
        return new IsolationStatus(isolated, uiRunning, installed, gcuRunning, detail);
    }

    internal static string DescribeStatus(
        bool isolated,
        bool uiRunning,
        bool installed,
        bool gcuRunning)
        => isolated
            ? uiRunning ? "隔离异常 · 官方界面仍在运行"
                : gcuRunning ? "已隔离 · 仅 GCU 后台运行"
                : "已隔离 · GCU 后台待连接"
            : uiRunning || installed ? "无需卸载 · 点击右侧按钮即可隔离"
                : "未检测到官方控制台";

    public static OperationResult Enable()
    {
        EnsureAdministrator();
        lock (StateLock)
        {
            SnapshotReadResult existing = ReadSnapshot();

            // 快照存在但读不出来时**绝不能**继续：CaptureSnapshot 会在官方启动项已被删除的
            // 情况下采到空集合，随后覆盖掉唯一一份原始状态记录，用户的原状态就永久丢失了。
            if (existing.Status == SnapshotReadStatus.Unreadable)
            {
                Logger.WriteLine("Refusing to isolate: the existing snapshot can't be read and must not be overwritten.");
                return new OperationResult(
                    false,
                    $"隔离状态文件无法读取，已中止以避免覆盖原始记录。请检查或删除：{SnapshotPath}",
                    0);
            }

            if (existing.Status == SnapshotReadStatus.Loaded && existing.Snapshot?.Isolated == true)
            {
                int stopped = StopOfficialUiProcesses();
                return new OperationResult(true, "官方控制台已经处于隔离状态。", stopped);
            }

            IsolationSnapshot snapshot = CaptureSnapshot();
            SaveSnapshot(snapshot);
            int affected = 0;
            try
            {
                affected += DisableRegistryEntries(snapshot.RegistryEntries);
                affected += DisableScheduledTasks(snapshot.ScheduledTasks);
                affected += DisableStartupFiles(snapshot.StartupFiles);
                affected += StopOfficialUiProcesses();
                snapshot.Isolated = true;
                snapshot.UpdatedUtc = DateTime.UtcNow;
                SaveSnapshot(snapshot);
                Logger.WriteLine($"Official console isolated. Changed items: {affected}");
                return new OperationResult(true, "官方控制台已隔离，仅保留 GCU 硬件后台。", affected);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Official console isolation failed: " + ex);
                // 回滚失败时也必须把状态落盘。过去 SaveSnapshot 在 RestoreSnapshot 抛异常后
                // 被跳过，磁盘上留下的是 Isolated=false 的快照，而系统实际处于「部分禁用」，
                // GetStatus 会报「未隔离」、守护不再运行，用户再点一次隔离就落入空快照覆盖。
                bool rolledBack = false;
                try
                {
                    RestoreOutcome rollback = RestoreSnapshot(snapshot);
                    rolledBack = !rollback.IsSilentNoOp && rollback.Failed == 0;
                    Logger.WriteLine(
                        $"Isolation rollback: restored={rollback.Restored} skipped={rollback.Skipped} failed={rollback.Failed}");
                }
                catch (Exception rollbackEx)
                {
                    Logger.WriteLine("Official console isolation rollback failed: " + rollbackEx);
                }

                try
                {
                    // 回滚不彻底时保持 Isolated=true：这样守护和状态显示都还认为处于隔离态，
                    // 用户可以再点「恢复」继续收拾，而不是被告知「无需恢复」。
                    snapshot.Isolated = !rolledBack;
                    snapshot.UpdatedUtc = DateTime.UtcNow;
                    SaveSnapshot(snapshot);
                }
                catch (Exception saveEx)
                {
                    Logger.WriteLine("Can't persist the isolation rollback state: " + saveEx.Message);
                }

                return new OperationResult(
                    false,
                    rolledBack
                        ? "隔离失败，已恢复原状态：" + ex.Message
                        : "隔离失败且回滚不完整，请点击「恢复官方控制台」重试：" + ex.Message,
                    affected);
            }
        }
    }

    public static OperationResult Restore()
    {
        EnsureAdministrator();
        lock (StateLock)
        {
            SnapshotReadResult read = ReadSnapshot();
            if (read.Status == SnapshotReadStatus.Missing)
                return new OperationResult(true, "没有需要恢复的隔离状态。", 0);
            if (read.Status == SnapshotReadStatus.Unreadable || read.Snapshot is null)
            {
                return new OperationResult(
                    false,
                    $"隔离状态文件无法读取，无法自动恢复。请手动检查：{SnapshotPath}",
                    0);
            }

            IsolationSnapshot snapshot = read.Snapshot;
            try
            {
                RestoreOutcome outcome = RestoreSnapshot(snapshot);
                Logger.WriteLine(
                    $"Official console restore: restored={outcome.Restored} skipped={outcome.Skipped} " +
                    $"failed={outcome.Failed} expected={outcome.Expected}");

                // 过去只要不抛异常就返回 true 和「已恢复」，即使实际恢复项数为 0 —— 静默假成功。
                if (outcome.IsSilentNoOp)
                {
                    return new OperationResult(
                        false,
                        $"没有任何一项被恢复（共 {outcome.Expected} 项，跳过 {outcome.Skipped}，失败 {outcome.Failed}）。隔离状态保持不变。",
                        0);
                }

                if (outcome.Failed > 0)
                {
                    // 部分失败：保持 Isolated=true，让守护继续工作、用户可以重试，
                    // 而不是留下「半恢复 + 状态显示未隔离」的组合。
                    snapshot.UpdatedUtc = DateTime.UtcNow;
                    SaveSnapshot(snapshot);
                    return new OperationResult(
                        false,
                        $"部分项目恢复失败（成功 {outcome.Restored}，失败 {outcome.Failed}），隔离状态保持不变，可再次点击恢复。",
                        outcome.Restored);
                }

                snapshot.Isolated = false;
                snapshot.UpdatedUtc = DateTime.UtcNow;
                SaveSnapshot(snapshot);
                return new OperationResult(true, "官方控制台启动状态已恢复。", outcome.Restored);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Official console restore failed: " + ex);
                return new OperationResult(false, "恢复失败：" + ex.Message, 0);
            }
        }
    }

    public static async System.Threading.Tasks.Task<OperationResult> SetIsolationAsync(bool isolate)
    {
        if (IsAdministrator())
            return await System.Threading.Tasks.Task.Run(() => isolate ? Enable() : Restore());

        try
        {
            using Process? helper = Process.Start(new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                Arguments = isolate ? "--official-isolate" : "--official-restore",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (helper is null)
                return new OperationResult(false, "无法启动管理员操作。", 0);

            await helper.WaitForExitAsync();
            bool success = helper.ExitCode == 0;
            return new OperationResult(success,
                success
                    ? isolate ? "官方控制台已隔离，仅保留 GCU 硬件后台。" : "官方控制台启动状态已恢复。"
                    : "管理员操作未完成，请查看日志。",
                0);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new OperationResult(false, "已取消管理员授权。", 0);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Official console elevated action failed: " + ex);
            return new OperationResult(false, "管理员操作失败：" + ex.Message, 0);
        }
    }

    public static void StartGuardIfNeeded()
    {
        StopGuard();
        if (TryReadSnapshot()?.Isolated != true) return;
        StopOfficialUiProcesses();
        guardTimer = new System.Threading.Timer(_ =>
        {
            if (Interlocked.Exchange(ref guardRunning, 1) != 0) return;
            try
            {
                if (TryReadSnapshot()?.Isolated == true)
                    StopOfficialUiProcesses();
            }
            finally
            {
                Volatile.Write(ref guardRunning, 0);
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    public static void StopGuard()
    {
        guardTimer?.Dispose();
        guardTimer = null;
    }

    internal static async Task<bool> WaitForUiSettledAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (TryReadSnapshot()?.Isolated != true) return true;

        long deadline = Environment.TickCount64 + Math.Max(250, (long)timeout.TotalMilliseconds);
        int quietPasses = 0;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsOfficialUiRunning())
            {
                quietPasses = 0;
                StopOfficialUiProcesses();
            }
            else if (++quietPasses >= 2)
            {
                return true;
            }

            await System.Threading.Tasks.Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return !IsOfficialUiRunning();
    }

    public static bool LaunchOfficialUi()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "shell:AppsFolder\\" + PackageAumid,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Can't launch packaged official console: " + ex.Message);
        }

        try
        {
            string oemRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OEM");
            foreach (string fileName in new[] { "ControlCenterU.exe", "GamingCenterU.exe" })
            {
                string? path = Directory.Exists(oemRoot)
                    ? Directory.EnumerateFiles(oemRoot, fileName, SearchOption.AllDirectories).FirstOrDefault()
                    : null;
                if (path is null) continue;
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Can't launch legacy official console: " + ex.Message);
        }
        return false;
    }

    private static IsolationSnapshot CaptureSnapshot()
    {
        var snapshot = new IsolationSnapshot
        {
            OfficialUiWasRunning = IsOfficialUiRunning(),
        };
        CaptureRegistryEntries(snapshot.RegistryEntries);
        CaptureScheduledTasks(snapshot.ScheduledTasks);
        CaptureStartupFiles(snapshot.StartupFiles);
        return snapshot;
    }

    private static void CaptureRegistryEntries(List<RegistryStartupEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((RegistryHive hive, string hiveName) in new[]
        {
            (RegistryHive.CurrentUser, "HKCU"),
            (RegistryHive.LocalMachine, "HKLM"),
        })
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (string path in RunKeyPaths)
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? key = baseKey.OpenSubKey(path, writable: true);
                if (key is null) continue;
                foreach (string name in key.GetValueNames())
                {
                    object? raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (raw is not string value || !IsOfficialReference(name + " " + value)) continue;
                    string identity = $"{hiveName}|{view}|{path}|{name}";
                    if (!seen.Add(identity)) continue;
                    entries.Add(new RegistryStartupEntry
                    {
                        Hive = hiveName,
                        View = view,
                        KeyPath = path,
                        Name = name,
                        Value = value,
                        Kind = key.GetValueKind(name),
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Can't inspect official startup key {hiveName} {path}: {ex.Message}");
            }
        }
    }

    private static void CaptureScheduledTasks(List<ScheduledTaskEntry> entries)
    {
        try
        {
            using var service = new Microsoft.Win32.TaskScheduler.TaskService();
            foreach (Microsoft.Win32.TaskScheduler.Task task in service.AllTasks)
            {
                try
                {
                    // 系统自带的任务目录无条件排除，避免以管理员运行时关掉 Windows 维护任务。
                    if (!IsManageableTaskPath(task.Path)) continue;
                    string actions = string.Join(" ", task.Definition.Actions
                        .OfType<ExecAction>()
                        .Select(a => a.Path + " " + a.Arguments));
                    if (!IsOfficialReference(task.Name + " " + actions)) continue;
                    entries.Add(new ScheduledTaskEntry { Path = task.Path, WasEnabled = task.Enabled });
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"Can't inspect scheduled task {task.Path}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Can't enumerate official scheduled tasks: " + ex.Message);
        }
    }

    private static void CaptureStartupFiles(List<StartupFileEntry> entries)
    {
        foreach (string folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
        }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder)) continue;
            foreach (string path in Directory.EnumerateFiles(folder))
            {
                try
                {
                    if (!IsOfficialStartupFile(path)) continue;
                    entries.Add(new StartupFileEntry
                    {
                        OriginalPath = path,
                        DisabledPath = path + DisabledStartupSuffix,
                    });
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"Can't inspect startup file {path}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>只对这些「内容里就写着目标路径」的容器扫描文件内容。</summary>
    static readonly string[] ContentScannableStartupExtensions = { ".lnk", ".url", ".cmd", ".bat", ".vbs", ".ps1" };

    /// <summary>内容扫描的大小上限。启动项的快捷方式/脚本都很小。</summary>
    const long MaxScannedStartupFileBytes = 1 * 1024 * 1024;

    /// <summary>
    /// 启动目录里的这个文件是否引用了官方控制台。
    ///
    /// 过去的做法是把**任意**文件的全部字节同时按 ASCII 和 UTF-16 解码再做子串匹配，
    /// 既没有大小上限（一个大 EXE 会产生 3-5 倍文件大小的临时字符串），
    /// 也会因为可执行文件里恰好出现过标记字符串而误伤。
    /// 现在：文件名总是参与匹配；只有快捷方式和脚本这类「内容里就写着目标路径」的容器
    /// 才扫描内容，并且限制大小。
    /// </summary>
    internal static bool IsOfficialStartupFile(string path)
    {
        if (IsOfficialReference(Path.GetFileName(path))) return true;

        string extension = Path.GetExtension(path);
        if (!Array.Exists(ContentScannableStartupExtensions,
                allowed => string.Equals(allowed, extension, StringComparison.OrdinalIgnoreCase)))
            return false;

        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxScannedStartupFileBytes) return false;

        byte[] bytes = File.ReadAllBytes(path);
        return IsOfficialReference(Encoding.ASCII.GetString(bytes)) ||
               IsOfficialReference(Encoding.Unicode.GetString(bytes));
    }

    // 三个 Disable* 都改成逐项容错：过去任何一项抛 UnauthorizedAccessException / IOException
    // 都会中断整批，让后面的条目完全得不到处理，而调用方只看到一个异常。

    private static int DisableRegistryEntries(IEnumerable<RegistryStartupEntry> entries)
    {
        int changed = 0;
        foreach (RegistryStartupEntry entry in entries)
        {
            if (!IsRestorableRegistryEntry(entry.Hive, entry.KeyPath, entry.Name, entry.Kind))
            {
                Logger.WriteLine($"Skipping an out-of-policy registry entry: {entry.Hive}\\{entry.KeyPath}\\{entry.Name}");
                continue;
            }
            try
            {
                RegistryHive hive = entry.Hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, entry.View);
                using RegistryKey? key = baseKey.OpenSubKey(entry.KeyPath, writable: true);
                if (key?.GetValue(entry.Name) is null) continue;
                key.DeleteValue(entry.Name, throwOnMissingValue: false);
                changed++;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Can't disable registry entry {entry.Hive}\\{entry.KeyPath}\\{entry.Name}: {ex.Message}");
            }
        }
        return changed;
    }

    private static int DisableScheduledTasks(IEnumerable<ScheduledTaskEntry> entries)
    {
        int changed = 0;
        try
        {
            using var service = new Microsoft.Win32.TaskScheduler.TaskService();
            foreach (ScheduledTaskEntry entry in entries)
            {
                if (!IsManageableTaskPath(entry.Path))
                {
                    Logger.WriteLine("Skipping a protected scheduled task: " + entry.Path);
                    continue;
                }
                try
                {
                    Microsoft.Win32.TaskScheduler.Task? task = service.GetTask(entry.Path);
                    if (task is null || !task.Enabled) continue;
                    task.Enabled = false;
                    changed++;
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"Can't disable scheduled task {entry.Path}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Can't open the task scheduler: " + ex.Message);
        }
        return changed;
    }

    private static int DisableStartupFiles(IEnumerable<StartupFileEntry> entries)
    {
        int changed = 0;
        string[] startupFolders = StartupFolders();
        foreach (StartupFileEntry entry in entries)
        {
            if (!IsRestorableStartupFile(entry.OriginalPath, entry.DisabledPath, startupFolders))
            {
                Logger.WriteLine("Skipping an out-of-policy startup file: " + entry.OriginalPath);
                continue;
            }
            try
            {
                if (!File.Exists(entry.OriginalPath) || File.Exists(entry.DisabledPath)) continue;
                File.Move(entry.OriginalPath, entry.DisabledPath);
                changed++;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Can't disable startup file {entry.OriginalPath}: {ex.Message}");
            }
        }
        return changed;
    }

    // ---- 恢复前的白名单校验 ----
    //
    // 采集阶段只扫 Run / RunOnce，但恢复阶段过去对快照字段零校验：
    // CreateSubKey(entry.KeyPath).SetValue(entry.Name, entry.Value, entry.Kind) 可以写 HKLM
    // 任意键（服务 ImagePath、IFEO、Winlogon…），File.Move 是一次管理员级的任意文件移动。
    // 只要能改写磁盘上的快照文件，用户点一次「恢复」就等于替攻击者执行提权写入。
    // 这些校验器是纯函数，便于直接测试。

    /// <summary>恢复时允许写入的注册表值类型：启动项就是字符串。</summary>
    internal static bool IsAllowedRegistryValueKind(RegistryValueKind kind) =>
        kind is RegistryValueKind.String or RegistryValueKind.ExpandString;

    internal static bool IsAllowedRegistryHive(string? hive) =>
        hive is "HKLM" or "HKCU";

    /// <summary>键路径必须精确等于采集阶段扫过的 Run / RunOnce 之一。</summary>
    internal static bool IsAllowedRunKeyPath(string? keyPath) =>
        !string.IsNullOrWhiteSpace(keyPath) &&
        Array.Exists(RunKeyPaths, allowed => string.Equals(allowed, keyPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>值名不能为空，也不能借由分隔符跳到别的键。</summary>
    internal static bool IsAllowedRegistryValueName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        !name.Contains('\\', StringComparison.Ordinal) &&
        !name.Contains('/', StringComparison.Ordinal);

    internal static bool IsRestorableRegistryEntry(
        string? hive, string? keyPath, string? name, RegistryValueKind kind) =>
        IsAllowedRegistryHive(hive) &&
        IsAllowedRunKeyPath(keyPath) &&
        IsAllowedRegistryValueName(name) &&
        IsAllowedRegistryValueKind(kind);

    /// <summary>
    /// 启动目录文件的恢复必须是「把我们自己改名的那个文件改回去」这一件事：
    /// 原路径要落在两个启动目录之内，禁用路径必须正好是原路径加上我们的后缀。
    /// </summary>
    internal static bool IsRestorableStartupFile(string? originalPath, string? disabledPath, IEnumerable<string> startupFolders)
    {
        if (string.IsNullOrWhiteSpace(originalPath) || string.IsNullOrWhiteSpace(disabledPath)) return false;
        if (!string.Equals(disabledPath, originalPath + DisabledStartupSuffix, StringComparison.Ordinal)) return false;

        string fullOriginal;
        try { fullOriginal = Path.GetFullPath(originalPath); }
        catch { return false; }

        foreach (string folder in startupFolders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            string fullFolder;
            try { fullFolder = Path.GetFullPath(folder); }
            catch { continue; }
            // 必须是该目录的直接子项，不接受子目录穿越。
            string? parent = Path.GetDirectoryName(fullOriginal);
            if (parent is not null &&
                string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar), fullFolder.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static string[] StartupFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
    ];

    /// <summary>
    /// 恢复结果。过去 RestoreSnapshot 只返回成功计数，且任何一项抛异常就中断整批：
    /// 第一个注册表项失败会让后面的计划任务和启动文件永远得不到恢复，
    /// 而快照仍保持 Isolated=true，守护定时器继续每 2 秒杀官方进程。
    /// </summary>
    internal readonly record struct RestoreOutcome(int Restored, int Skipped, int Failed, int Expected)
    {
        /// <summary>快照里有条目却一项都没恢复成功 —— 不能报成功。</summary>
        internal bool IsSilentNoOp => Expected > 0 && Restored == 0;
    }

    private static RestoreOutcome RestoreSnapshot(IsolationSnapshot snapshot)
    {
        int restored = 0;
        int skipped = 0;
        int failed = 0;
        int expected = snapshot.RegistryEntries.Count + snapshot.ScheduledTasks.Count + snapshot.StartupFiles.Count;

        foreach (RegistryStartupEntry entry in snapshot.RegistryEntries)
        {
            if (!IsRestorableRegistryEntry(entry.Hive, entry.KeyPath, entry.Name, entry.Kind))
            {
                Logger.WriteLine(
                    $"Refusing to restore an out-of-policy registry entry: {entry.Hive}\\{entry.KeyPath}\\{entry.Name} ({entry.Kind})");
                skipped++;
                continue;
            }
            try
            {
                RegistryHive hive = entry.Hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, entry.View);
                using RegistryKey? key = baseKey.OpenSubKey(entry.KeyPath, writable: true);
                if (key is null)
                {
                    skipped++;
                    continue;
                }
                // 只在值确实缺失时才写回（与文件分支的 File.Exists 检查对称）。
                // 无条件 SetValue 会复活已被 Windows 消费掉的 RunOnce 项，
                // 也会覆盖用户在隔离期间主动修改或删除的值。
                if (key.GetValue(entry.Name) is not null)
                {
                    skipped++;
                    continue;
                }
                key.SetValue(entry.Name, entry.Value, entry.Kind);
                restored++;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Can't restore registry entry {entry.Hive}\\{entry.KeyPath}\\{entry.Name}: {ex.Message}");
                failed++;
            }
        }

        try
        {
            using var service = new Microsoft.Win32.TaskScheduler.TaskService();
            foreach (ScheduledTaskEntry entry in snapshot.ScheduledTasks)
            {
                if (!IsManageableTaskPath(entry.Path))
                {
                    Logger.WriteLine("Refusing to restore an out-of-policy scheduled task: " + entry.Path);
                    skipped++;
                    continue;
                }
                try
                {
                    Microsoft.Win32.TaskScheduler.Task? task = service.GetTask(entry.Path);
                    if (task is null || task.Enabled == entry.WasEnabled)
                    {
                        skipped++;
                        continue;
                    }
                    task.Enabled = entry.WasEnabled;
                    restored++;
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"Can't restore scheduled task {entry.Path}: {ex.Message}");
                    failed++;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Can't open the task scheduler for restore: " + ex.Message);
            failed += snapshot.ScheduledTasks.Count;
        }

        string[] startupFolders = StartupFolders();
        foreach (StartupFileEntry entry in snapshot.StartupFiles)
        {
            if (!IsRestorableStartupFile(entry.OriginalPath, entry.DisabledPath, startupFolders))
            {
                Logger.WriteLine(
                    $"Refusing to restore an out-of-policy startup file: {entry.DisabledPath} -> {entry.OriginalPath}");
                skipped++;
                continue;
            }
            try
            {
                if (!File.Exists(entry.DisabledPath) || File.Exists(entry.OriginalPath))
                {
                    skipped++;
                    continue;
                }
                File.Move(entry.DisabledPath, entry.OriginalPath);
                restored++;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Can't restore startup file {entry.OriginalPath}: {ex.Message}");
                failed++;
            }
        }

        return new RestoreOutcome(restored, skipped, failed, expected);
    }

    /// <summary>当前交互会话 ID。只终结同会话内的官方界面进程。</summary>
    static int CurrentSessionId
    {
        get
        {
            try
            {
                using Process current = Process.GetCurrentProcess();
                return current.SessionId;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Can't read the current session id: " + ex.Message);
                return -1;
            }
        }
    }

    private static int StopOfficialUiProcesses()
    {
        int stopped = 0;
        foreach (string name in UiProcessNames)
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == Environment.ProcessId || process.HasExited) continue;
                        // Process.GetProcessesByName 是全机范围。提权运行时会命中其他用户会话里
                        // 的同名进程，而我们只应该管自己这个交互会话。
                        if (process.SessionId != CurrentSessionId) continue;
                        if (process.CloseMainWindow() && process.WaitForExit(800))
                        {
                            stopped++;
                            continue;
                        }
                        // 不再使用 entireProcessTree：官方 UI 可能启动了更新程序/安装器，
                        // 连带杀掉它们既超出授权范围，也可能损坏正在进行的安装。
                        process.Kill();
                        // 只统计确认退出的进程。过去无条件 stopped++，返给 UI 的数字会虚高。
                        if (!process.WaitForExit(1500))
                        {
                            Logger.WriteLine($"Official UI process did not exit in time: {name} ({process.Id})");
                            continue;
                        }
                        stopped++;
                        Logger.WriteLine($"Stopped official UI process: {name} ({process.Id})");
                    }
                    catch (Exception ex)
                    {
                        Logger.WriteLine($"Can't stop official UI process {name} ({process.Id}): {ex.Message}");
                    }
                }
            }
        }
        if (stopped > 0) RefreshTrayArea();
        return stopped;
    }

    private static bool IsOfficialUiRunning()
    {
        // 与 StopOfficialUiProcesses 保持同样的会话范围：否则另一个用户会话里的同名进程
        // 会让状态显示成「隔离异常 · 官方界面仍在运行」，而我们既不该也不会去终结它。
        int sessionId = CurrentSessionId;
        foreach (string name in UiProcessNames)
        {
            Process[] processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Any(p => !p.HasExited && p.SessionId == sessionId)) return true;
            }
            catch (Exception ex)
            {
                Logger.WriteLineThrottled(
                    "official-ui-probe", $"Can't inspect official UI process {name}: {ex.Message}", 10000);
            }
            finally
            {
                foreach (Process process in processes) process.Dispose();
            }
        }
        return false;
    }

    private static bool IsOfficialUiInstalled()
    {
        long now = Environment.TickCount64;
        lock (InstallationProbeLock)
        {
            if (installedCache.HasValue && now < installedCacheUntil)
                return installedCache.Value;
        }

        bool installed = ProbeOfficialUiInstalled();
        lock (InstallationProbeLock)
        {
            installedCache = installed;
            installedCacheUntil = Environment.TickCount64 + (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
        }
        return installed;
    }

    private static bool ProbeOfficialUiInstalled()
    {
        try
        {
            string windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (Directory.Exists(windowsApps) && Directory.EnumerateDirectories(windowsApps, "CCU.WinUI_*").Any())
                return true;
        }
        catch { }

        try
        {
            string oem = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OEM");
            return Directory.Exists(oem)
                && Directory.EnumerateFiles(oem, "ControlCenterU.exe", SearchOption.AllDirectories).Any();
        }
        catch { return false; }
    }

    private static bool IsOfficialReference(string value)
        => OfficialMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsProcessRunning(string name)
    {
        Process[] processes = Process.GetProcessesByName(name);
        // GetProcessesByName only returns live processes. Reading HasExited on a
        // SYSTEM-owned GCU process can fail for a standard user and cause a false
        // "not running" result even though the process is present.
        try { return processes.Length > 0; }
        finally { foreach (Process process in processes) process.Dispose(); }
    }

    /// <summary>
    /// 快照读取的三种结果。过去只有「有 / null」两种，任何异常（损坏、被占用、权限不足、
    /// JSON 截断）都返回 null，与「文件不存在」无法区分。而 Enable() 只看 Isolated==true，
    /// 于是一次读失败就会走全新流程：此时官方启动项**已经被删除**，CaptureSnapshot 采到空集合，
    /// 紧接着把唯一一份原始状态记录覆盖成空快照 —— 用户的原状态永久丢失且 Restore 静默假成功。
    /// </summary>
    private enum SnapshotReadStatus
    {
        Missing,
        Loaded,
        Unreadable,
    }

    private readonly record struct SnapshotReadResult(SnapshotReadStatus Status, IsolationSnapshot? Snapshot);

    private static SnapshotReadResult ReadSnapshot()
    {
        try
        {
            if (!File.Exists(SnapshotPath)) return new SnapshotReadResult(SnapshotReadStatus.Missing, null);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Can't probe the official console snapshot: " + ex.Message);
            return new SnapshotReadResult(SnapshotReadStatus.Unreadable, null);
        }

        try
        {
            IsolationSnapshot? snapshot =
                JsonSerializer.Deserialize<IsolationSnapshot>(File.ReadAllText(SnapshotPath), JsonOptions);
            if (snapshot is null)
            {
                Logger.WriteLine("Official console snapshot deserialized to null.");
                return new SnapshotReadResult(SnapshotReadStatus.Unreadable, null);
            }
            // 版本号过去写入但从不校验。未来格式变化时必须显式拒绝而不是按当前结构硬读。
            if (snapshot.Version != SnapshotVersion)
            {
                Logger.WriteLine(
                    $"Official console snapshot version {snapshot.Version} is not supported (expected {SnapshotVersion}).");
                return new SnapshotReadResult(SnapshotReadStatus.Unreadable, null);
            }
            return new SnapshotReadResult(SnapshotReadStatus.Loaded, snapshot);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Can't read official console snapshot: " + ex.Message);
            return new SnapshotReadResult(SnapshotReadStatus.Unreadable, null);
        }
    }

    /// <summary>仅用于「是否处于隔离状态」这类只读判断；读不出来一律按未隔离处理。</summary>
    private static IsolationSnapshot? TryReadSnapshot() => ReadSnapshot().Snapshot;

    private static void SaveSnapshot(IsolationSnapshot snapshot)
    {
        string? directory = Path.GetDirectoryName(SnapshotPath);
        if (directory is null) throw new InvalidOperationException("Invalid isolation snapshot path.");
        EnsureSnapshotDirectory(directory);
        // 临时文件名带进程号，避免两个提权子进程用同一个 .tmp 互相踩。
        string temporary = $"{SnapshotPath}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(temporary, SnapshotPath, overwrite: true);
    }

    /// <summary>
    /// 创建快照目录并显式收紧 ACL：只有 Administrators 和 SYSTEM 可写，Users 只读。
    /// 快照内容会驱动管理员级的注册表写入与文件移动（见 RestoreSnapshot），
    /// 而 ProgramData 的默认继承权限允许普通用户在其下创建子目录并成为 CREATOR OWNER，
    /// 抢先创建这个目录就能控制快照内容。
    /// </summary>
    private static void EnsureSnapshotDirectory(string directory)
    {
        bool existed = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            security.AddAccessRule(new FileSystemAccessRule(
                administrators, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                localSystem, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                users, FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
            if (!existed) Logger.WriteLine("Created the isolation snapshot directory with an administrators-only ACL.");
        }
        catch (Exception ex)
        {
            // 收紧失败不阻断功能，但必须留痕：此时快照可能是可被篡改的。
            Logger.WriteLine("Can't tighten the isolation snapshot directory ACL: " + ex.Message);
        }
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void EnsureAdministrator()
    {
        if (!IsAdministrator())
            throw new UnauthorizedAccessException("Official console isolation requires administrator rights.");
    }

    private static void RefreshTrayArea()
    {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        IntPtr pager = FindWindowEx(notify, IntPtr.Zero, "SysPager", null);
        RefreshToolbar(FindWindowEx(pager, IntPtr.Zero, "ToolbarWindow32", "Notification Area"));
        RefreshToolbar(FindWindowEx(pager, IntPtr.Zero, "ToolbarWindow32", "User Promoted Notification Area"));
        IntPtr overflow = FindWindow("NotifyIconOverflowWindow", null);
        RefreshToolbar(FindWindowEx(overflow, IntPtr.Zero, "ToolbarWindow32", "Overflow Notification Area"));
    }

    private static void RefreshToolbar(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !GetClientRect(handle, out RECT rect)) return;
        for (int x = 0; x < rect.Right; x += 8)
        for (int y = 0; y < rect.Bottom; y += 8)
            SendMessage(handle, 0x0200, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xffff)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
