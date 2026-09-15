using MechrevoLite.Helpers;
using MechrevoLite;
using Microsoft.Win32.TaskScheduler;
using System.Security.Principal;

internal readonly record struct StartupTaskPlan(
    TimeSpan TriggerDelay,
    int RestartCount,
    TimeSpan RestartInterval);

public class Startup
{

    static string taskName = "LMechrevo";
    static string chargeTaskName = taskName + "Charge";
    static readonly string strExeFilePath = ResolvePersistentExecutablePath();
    // WindowsIdentity.GetCurrent().User 是可空的。过去在静态字段初始化器里直接 .Value
    // 解引用，为 null 时会抛 TypeInitializationException，之后 Startup 的**所有**静态成员
    // （IsScheduled / StartupCheck / Schedule）都不可用。主项目的 Nullable 只开 annotations，
    // 编译器不会就此报警，所以必须显式兜底。
    static readonly string currentUserSid = ResolveCurrentUserSid();
    static string userTaskName = taskName + "_" + currentUserSid;
    static readonly string[] legacyTaskNames = ["GHelper", "GHelperCharge", "GHelper_" + currentUserSid];

    static string ResolveCurrentUserSid()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string? sid = identity.User?.Value;
            if (!string.IsNullOrWhiteSpace(sid)) return sid;
            Logger.WriteLine("Current identity has no user SID; falling back to the account name for task naming.");
            return SanitizeTaskNameFragment(identity.Name);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Can't resolve the current user SID: " + ex.Message);
            return "unknown";
        }
    }

    // ---- 「开机自启动」快捷开关的接缝 ----
    // 快捷开关通过这两个成员读写系统自启动状态。生产实现走本类现有的用户级计划任务
    // （TaskRunLevel.LUA，不需要管理员）；测试注入替身，绝不读/写真实计划任务。
    internal static Func<bool?> ReadScheduledState { get; set; } = ReadScheduledStateFromSystem;
    internal static Func<bool, bool> WriteScheduledState { get; set; } = WriteScheduledStateToSystem;

    /// <summary>
    /// 应用期望的自启动状态。已经是目标态就不再重复写：Task Scheduler 用同名任务覆盖注册，
    /// 这里再挡一道，保证幂等、永不产生第二条自启动项。
    /// </summary>
    internal static bool ApplyScheduledState(bool enabled)
    {
        if (ReadScheduledState() == enabled) return true;
        return WriteScheduledState(enabled);
    }

    static bool? ReadScheduledStateFromSystem()
    {
        try { return IsScheduled(); }
        catch (Exception ex) { Logger.WriteLine("Can't read autostart state: " + ex.Message); return null; }
    }

    static bool WriteScheduledStateToSystem(bool enabled) => enabled ? Schedule() : UnSchedule();

    /// <summary>
    /// 计划任务动作里的可执行路径。Task Scheduler 用结构化字段保存路径（不需要、也不应手工加引号），
    /// 这里只 Trim 并剥掉两端可能附带的成对引号——路径中的空格原样保留。单文件发布时
    /// <c>Application.ExecutablePath</c> 返回的是宿主真实 exe 路径（不是解压目录），所以路径稳定、可跨更新复用。
    /// </summary>
    internal static string NormalizeExecutablePath(string? exePath) =>
        (exePath ?? string.Empty).Trim().Trim('"');

    /// <summary>
    /// 计划任务动作指向的**持久**宿主 exe。单文件发布下 <c>Assembly.Location</c> 为空，不能用来
    /// 定位镜像；<c>Environment.ProcessPath</c> 是官方文档化的、自解压安全的来源（宿主真实 exe），
    /// 缺失时退回 <c>Application.ExecutablePath</c>（内部即 GetModuleFileName(NULL)）。自更新只原地
    /// 替换该路径的 exe，任务因此跨更新保持有效。
    /// </summary>
    internal static string ResolvePersistentExecutablePath() =>
        ResolvePersistentExecutablePath(Environment.ProcessPath, Application.ExecutablePath);

    internal static string ResolvePersistentExecutablePath(string? processPath, string? executablePath)
    {
        string resolved = NormalizeExecutablePath(processPath);
        return resolved.Length > 0 ? resolved : NormalizeExecutablePath(executablePath);
    }

    /// <summary>当前注册动作实际使用的路径（测试用；等价于运行镜像的持久路径）。</summary>
    internal static string ScheduledExecutablePath => strExeFilePath;

    /// <summary>
    /// 运行镜像是否位于临时目录（单文件自解压 / 更新器暂存都落在 <c>%TEMP%</c>）。这类路径随时
    /// 会被系统清理，绝不能让持久自启动项指向它——否则清理后开机自启静默失效。判定为纯函数，
    /// 便于测试。
    /// </summary>
    internal static bool IsTransientExecutablePath(string? exePath) =>
        IsTransientExecutablePath(exePath, Path.GetTempPath());

    internal static bool IsTransientExecutablePath(string? exePath, string? tempRoot)
    {
        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(tempRoot)) return false;
        try
        {
            string root = Path.GetFullPath(tempRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(NormalizeExecutablePath(exePath));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>计划任务名不能含路径分隔符，account name 形如 DOMAIN\User。</summary>
    internal static string SanitizeTaskNameFragment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var buffer = new System.Text.StringBuilder(value.Length);
        foreach (char c in value)
            buffer.Append(char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_');
        return buffer.ToString();
    }

    static bool IsOwnedTask(Microsoft.Win32.TaskScheduler.Task? task)
    {
        try
        {
            var action = task?.Definition.Actions.OfType<ExecAction>().FirstOrDefault();
            if (action is null) return false;
            string path = Environment.ExpandEnvironmentVariables(action.Path).Trim('"');
            string fileName = Path.GetFileNameWithoutExtension(path);
            return Path.GetFullPath(path).Equals(Path.GetFullPath(strExeFilePath), StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("L-Mechrevo", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("L-Mechrevo-beta", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static void RemoveOwnedLegacyTasks(TaskService taskService)
    {
        foreach (string name in legacyTaskNames)
        {
            try
            {
                var task = taskService.GetTask(name);
                if (IsOwnedTask(task)) taskService.RootFolder.DeleteTask(name, false);
            }
            catch (Exception ex) { Logger.WriteLine($"Can't remove legacy task {name}: {ex.Message}"); }
        }
    }

    static Microsoft.Win32.TaskScheduler.Task? GetUserTask(TaskService taskService)
    {
        try
        {
            var task = taskService.GetTask(userTaskName);
            if (IsOwnedTask(task)) return task;
            foreach (string legacyName in legacyTaskNames)
            {
                task = taskService.GetTask(legacyName);
                if (IsOwnedTask(task)) return task;
            }
        }
        catch (Exception e)
        {
            Logger.WriteLine("Can't read startup task: " + e.Message);
        }

        return null;
    }

    public static bool IsScheduled()
    {
        try
        {
            using (TaskService taskService = new TaskService())
                return GetUserTask(taskService) != null;
        }
        catch (Exception e)
        {
            Logger.WriteLine("Can't check startup task status: " + e.Message);
            return false;
        }
    }

    public static void ReScheduleAdmin()
    {
        if (ProcessHelper.IsUserAdministrator() && IsScheduled())
        {
            UnSchedule();
            Schedule();
        }
    }

    public static void StartupCheck()
    {
        RunStartupCheckSafely(StartupCheckCore,
            ex => Logger.WriteLine("Startup task check failed: " + ex.Message));
    }

    internal static bool RunStartupCheckSafely(System.Action check, System.Action<Exception> report)
    {
        try
        {
            check();
            return true;
        }
        catch (Exception ex)
        {
            try { report(ex); } catch { }
            return false;
        }
    }

    static void StartupCheckCore()
    {
        // 从临时副本运行时绝不碰持久自启动项：当前镜像不是用户真正会启动的那个 exe，
        // 拿它重新注册只会把任务指向随时会被清理的 %TEMP%，清理后开机自启静默失效。
        if (IsTransientExecutablePath(strExeFilePath))
        {
            Logger.WriteLine("Skipping startup task check: the running image is transient: " + strExeFilePath);
            return;
        }

        bool restoreMissingTask = false;
        using (TaskService taskService = new TaskService())
        {
            var task = GetUserTask(taskService);
            if (task != null)
            {
                try
                {
                    bool needsReschedule = !MatchesCurrentUserStartupTask(task.Definition);

                    if (ShouldAutoRepairStartupTask(needsReschedule, ProcessHelper.IsUserAdministrator()))
                    {
                        Logger.WriteLine("Rescheduling to: " + strExeFilePath);
                        UnSchedule();
                        Schedule();
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"Can't check startup task: {ex.Message}");
                }

                if (taskService.RootFolder.AllTasks.FirstOrDefault(t => t.Name == chargeTaskName) == null)
                {
                    if (ProcessHelper.IsUserAdministrator())
                        ScheduleCharge();
                    else
                        Logger.WriteLine("Charge limit task is missing; skipping automatic repair because administrator rights are required.");
                }

            }
            else
            {
                restoreMissingTask = ShouldRestoreEnabledStartupTask(
                    taskMissing: true,
                    startupEnabled: AppConfig.Is("startup_enabled"));
            }
        }

        if (restoreMissingTask)
        {
            Logger.WriteLine("Startup task is missing; restoring the user-enabled task.");
            if (!Schedule()) Logger.WriteLine("Startup task restore failed.");
        }
    }

    /// <summary>
    /// 用户级自启动任务是否需要自动重建。刻意不看管理员标志：用户任务是 LUA 级别，
    /// 当前用户就能注册，没有理由因为缺少管理员权限而放弃修复一个失效的自启动项。
    ///
    /// 过去这个设计有个危险的副作用：重建走 UnSchedule() → Schedule()，而 UnSchedule()
    /// 会无条件删除 SYSTEM 充电任务，ScheduleCharge() 又在非管理员时直接 return，
    /// 于是非提权下的一次自动修复会静默删掉充电任务且不再重建。
    /// 修复方式是把危害挪走而不是放弃修复：UnSchedule() 现在只在有管理员权限
    /// （也就是有能力重建）时才删除充电任务。
    /// </summary>
    public static bool ShouldAutoRepairStartupTask(bool needsReschedule, bool _) => needsReschedule;

    internal static bool ShouldRestoreEnabledStartupTask(bool taskMissing, bool startupEnabled) =>
        taskMissing && startupEnabled;

    internal static StartupTaskPlan GetUserStartupTaskPlan() => new(
        TimeSpan.FromSeconds(10),
        RestartCount: 3,
        RestartInterval: TimeSpan.FromMinutes(1));

    internal static TaskRunLevel GetUserStartupTaskRunLevel() => TaskRunLevel.LUA;

    static bool MatchesCurrentUserStartupTask(TaskDefinition definition)
    {
        var exec = definition.Actions.OfType<ExecAction>().FirstOrDefault();
        string action = Environment.ExpandEnvironmentVariables(exec?.Path ?? "").Trim('"');
        string arguments = exec?.Arguments?.Trim() ?? "";
        return Path.GetFullPath(strExeFilePath).Equals(Path.GetFullPath(action), StringComparison.OrdinalIgnoreCase)
            && arguments.Equals("startup", StringComparison.OrdinalIgnoreCase)
            && MatchesUserStartupPlan(definition);
    }

    internal static bool MatchesUserStartupPlan(TaskDefinition definition)
    {
        StartupTaskPlan plan = GetUserStartupTaskPlan();
        bool logonDelay = definition.Triggers.OfType<LogonTrigger>()
            .Any(trigger => trigger.Delay >= plan.TriggerDelay);
        bool consoleDelay = definition.Triggers.OfType<SessionStateChangeTrigger>()
            .Any(trigger => trigger.StateChange == TaskSessionStateChangeType.ConsoleConnect && trigger.Delay >= plan.TriggerDelay);
        return logonDelay && consoleDelay &&
            definition.Principal.LogonType == TaskLogonType.InteractiveToken &&
            definition.Principal.RunLevel == GetUserStartupTaskRunLevel() &&
            definition.Settings.RestartCount >= plan.RestartCount &&
            definition.Settings.RestartInterval >= plan.RestartInterval &&
            definition.Settings.StartWhenAvailable;
    }

    internal static void ConfigureUserStartupTask(TaskDefinition definition, string userName)
    {
        StartupTaskPlan plan = GetUserStartupTaskPlan();
        definition.RegistrationInfo.Description = "L-Mechrevo Auto Start";
        definition.Triggers.Add(new LogonTrigger { UserId = userName, Delay = plan.TriggerDelay });
        // ConsoleConnect covers a local console reconnect without repeatedly firing on every unlock.
        definition.Triggers.Add(new SessionStateChangeTrigger
        {
            StateChange = TaskSessionStateChangeType.ConsoleConnect,
            UserId = userName,
            Delay = plan.TriggerDelay,
        });
        definition.Actions.Add(strExeFilePath, "startup");

        definition.Principal.UserId = userName;
        definition.Principal.LogonType = TaskLogonType.InteractiveToken;
        definition.Principal.RunLevel = GetUserStartupTaskRunLevel();

        definition.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
        definition.Settings.StartWhenAvailable = true;
        definition.Settings.RestartCount = plan.RestartCount;
        definition.Settings.RestartInterval = plan.RestartInterval;
    }

    public static void UnscheduleCharge()
    {
        using (TaskService taskService = new TaskService())
        {
            try
            {
                taskService.RootFolder.DeleteTask(chargeTaskName);
            }
            catch (Exception e)
            {
                Logger.WriteLine("Can't remove charge limit task: " + e.Message);
            }
        }
    }

    public static void ScheduleCharge()
    {

        if (strExeFilePath is null) return;
        if (!ProcessHelper.IsUserAdministrator())
        {
            Logger.WriteLine("Skipping charge limit task registration: administrator rights are required.");
            return;
        }
        // 这个任务以 SYSTEM + 最高权限在开机时执行磁盘上的镜像。如果镜像所在目录
        // 普通用户可写，替换 EXE 就等于拿到 SYSTEM —— 无需 UAC 且持久化。
        // 便携式解压运行时拒绝注册：代价只是「开机阶段不预先应用充电限制」，
        // 登录后的用户级自启动任务仍会应用，属于可接受的降级。
        if (!ExecutableTrust.IsCurrentImageInProtectedLocation(out string trustReason))
        {
            Logger.WriteLine(
                "Refusing to register the SYSTEM charge limit task: " + trustReason +
                ". Install into a directory that only administrators can write (for example Program Files).");
            return;
        }
        Logger.WriteLine("Charge limit task location check passed: " + trustReason);

        using (TaskDefinition td = TaskService.Instance.NewTask())
        {
            td.RegistrationInfo.Description = "L-Mechrevo Charge Limit";
            td.Triggers.Add(new BootTrigger());
            td.Triggers.Add(new EventTrigger
            {
                Subscription = "<QueryList><Query Id='0' Path='System'><Select Path='System'>*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and EventID=27]]</Select></Query></QueryList>"
            }); 
            td.Actions.Add(strExeFilePath, "charge");

            td.Principal.UserId = "SYSTEM";
            td.Principal.LogonType = TaskLogonType.ServiceAccount;
            td.Principal.RunLevel = TaskRunLevel.Highest;

            td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
            td.Settings.StopIfGoingOnBatteries = false;
            td.Settings.DisallowStartIfOnBatteries = false;
            td.Settings.ExecutionTimeLimit = TimeSpan.FromSeconds(30);

            try
            {
                TaskService.Instance.RootFolder.RegisterTaskDefinition(chargeTaskName, td);
                RemoveOwnedLegacyTasks(TaskService.Instance);
                Logger.WriteLine("Charge limit task scheduled: " + strExeFilePath);
            }
            catch (Exception e)
            {
                Logger.WriteLine("Can't create a charge limit task: " + e.Message);
            }
        }
    }

    /// <summary>
    /// 只注册用户级自启动任务（LUA 级别，当前用户即可注册），不涉及 SYSTEM 充电任务。
    /// </summary>
    internal static bool ScheduleUserTaskOnly()
    {
        // 同 StartupCheckCore：拒绝把持久自启动项注册到临时目录下的镜像路径。
        if (IsTransientExecutablePath(strExeFilePath))
        {
            Logger.WriteLine("Refusing to register the startup task from a transient location: " + strExeFilePath);
            return false;
        }

        try
        {
            using TaskService taskService = new();
            using TaskDefinition td = taskService.NewTask();
            ConfigureUserStartupTask(td, WindowsIdentity.GetCurrent().Name);
            taskService.RootFolder.RegisterTaskDefinition(userTaskName, td);
            var registered = GetUserTask(taskService);
            if (registered is null || !MatchesCurrentUserStartupTask(registered.Definition))
            {
                Logger.WriteLine("Startup task registration could not be verified.");
                return false;
            }
            RemoveOwnedLegacyTasks(taskService);
            Logger.WriteLine("Startup task scheduled: " + strExeFilePath);
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Can't create startup task: " + ex.Message);
            return false;
        }
    }

    public static bool Schedule()
    {
        if (!ScheduleUserTaskOnly()) return false;
        ScheduleCharge();
        return true;
    }

    public static bool UnSchedule()
    {
        bool removed = true;
        using (TaskService taskService = new TaskService())
        {
            try
            {
                if (taskService.GetTask(userTaskName) != null)
                    taskService.RootFolder.DeleteTask(userTaskName, false);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Can't remove startup task: " + ex.Message);
                removed = false;
            }
            RemoveOwnedLegacyTasks(taskService);
        }

        // 充电任务是 SYSTEM 级别，只有管理员能重建。非提权时删掉它就再也回不来，
        // 所以只在有权限重建的情况下才删。
        if (ProcessHelper.IsUserAdministrator()) UnscheduleCharge();
        else Logger.WriteLine("Keeping the SYSTEM charge limit task: it cannot be re-registered without administrator rights.");
        return removed;
    }
}
