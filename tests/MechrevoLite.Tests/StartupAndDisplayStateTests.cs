using MechrevoLite.Hardware;
using Microsoft.Win32.TaskScheduler;

namespace MechrevoLite.Tests;

public class StartupAndDisplayStateTests
{
    [Fact]
    public void UserStartupPlan_DelaysForDesktopReadinessAndRetriesFailures()
    {
        StartupTaskPlan plan = Startup.GetUserStartupTaskPlan();

        Assert.Equal(TimeSpan.FromSeconds(10), plan.TriggerDelay);
        Assert.Equal(3, plan.RestartCount);
        Assert.Equal(TimeSpan.FromMinutes(1), plan.RestartInterval);
    }

    [Theory]
    [InlineData("0.283.0-beta.7.1+build123", "beta7.1")]
    [InlineData("0.283.0-beta.8", "beta8")]
    [InlineData("1.0.0", "1.0.0")]
    public void ReleaseLabel_IsDerivedFromInformationalVersion(string version, string expected) =>
        Assert.Equal(expected, Program.NormalizeReleaseLabel(version));

    [Theory]
    [InlineData("startup", true)]
    [InlineData("STARTUP", true)]
    [InlineData("", false)]
    [InlineData("charge", false)]
    public void OnlyScheduledStartupArgumentStartsMinimized(string action, bool expected) =>
        Assert.Equal(expected, Program.IsStartupLaunch(action));

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(3, 3, true)]
    [InlineData(2, 0, false)]
    [InlineData(-1, 0, false)]
    public void DelayedConnectionReapplyRequiresTheOriginalModeIntent(
        int pendingMode, int selectedMode, bool expected) =>
        Assert.Equal(expected,
            Program.ShouldReapplyPendingPerformanceMode(pendingMode, selectedMode));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void StartupTaskRepair_RepairsStaleUserTaskWithoutElevation(
        bool needsReschedule, bool isAdministrator, bool expected) =>
        Assert.Equal(expected, Startup.ShouldAutoRepairStartupTask(needsReschedule, isAdministrator));

    [Fact]
    public void UserStartupTask_UsesLeastPrivilege()
    {
        Assert.Equal(Microsoft.Win32.TaskScheduler.TaskRunLevel.LUA,
            Startup.GetUserStartupTaskRunLevel());
    }

    [Fact]
    public void StartupCheckBoundary_ReportsTaskSchedulerFailuresWithoutThrowing()
    {
        Exception? reported = null;

        bool succeeded = Startup.RunStartupCheckSafely(
            () => throw new InvalidOperationException("Task Scheduler is unavailable."),
            ex => reported = ex);

        Assert.False(succeeded);
        Assert.IsType<InvalidOperationException>(reported);
    }

    [Fact]
    public void StartupCheckBoundary_CompletesSuccessfulChecks()
    {
        bool ran = false;

        bool succeeded = Startup.RunStartupCheckSafely(
            () => ran = true,
            _ => throw new Xunit.Sdk.XunitException("A successful check must not report an error."));

        Assert.True(succeeded);
        Assert.True(ran);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void MissingStartupTask_IsRestoredOnlyWhenTheUserPreviouslyEnabledIt(
        bool taskMissing, bool startupEnabled, bool expected) =>
        Assert.Equal(expected, Startup.ShouldRestoreEnabledStartupTask(taskMissing, startupEnabled));

    [Theory]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(false, false, false, true)]
    public void PerformanceAutoApply_PreservesCurrentModeForDisplayWakeWithoutPowerChange(
        bool powerChanged,
        bool wakeup,
        bool sourceChanged,
        bool expected) =>
        Assert.Equal(expected, Program.ShouldReapplyPerformanceMode(powerChanged, wakeup, sourceChanged));

    [Theory]
    [InlineData(false, false, MechrevoService.AdvancedColorState.Off)]
    [InlineData(true, false, MechrevoService.AdvancedColorState.Hdr)]
    [InlineData(false, true, MechrevoService.AdvancedColorState.Acm)]
    [InlineData(true, true, MechrevoService.AdvancedColorState.Hdr)]
    public void AdvancedColorState_DistinguishesHdrFromAcm(
        bool hdr,
        bool acm,
        MechrevoService.AdvancedColorState expected) =>
        Assert.Equal(expected, MechrevoService.ResolveAdvancedColorState(hdr, acm));

    [Fact]
    public void GpuRestartRoute_IgnoresStalePersistedDirectMode()
    {
        GpuSwitchPlan plan = GpuSwitchPolicy.Resolve(
            MechrevoService.GpuStandard,
            automaticRuntime: 1,
            MechrevoService.GpuIGpu,
            supportsHotSwap: true);

        Assert.Equal("Restart", plan.Route.ToString());
    }

    // ------------------------------------------------------------ 自启动动作路径解析

    /// <summary>
    /// 单文件发布下 Assembly.Location 为空，动作路径必须来自宿主 exe：ProcessPath 优先，
    /// 缺失时退回 Application.ExecutablePath。两者都没有时才退化为空串。
    /// </summary>
    [Theory]
    [InlineData(@"C:\Apps\L-Mechrevo.exe", @"C:\Other\L-Mechrevo.exe", @"C:\Apps\L-Mechrevo.exe")]
    [InlineData(null, @"C:\Apps\L-Mechrevo.exe", @"C:\Apps\L-Mechrevo.exe")]
    [InlineData("  ", @"C:\Apps\L-Mechrevo.exe", @"C:\Apps\L-Mechrevo.exe")]
    [InlineData("\"C:\\Apps\\L-Mechrevo.exe\"", null, @"C:\Apps\L-Mechrevo.exe")]
    [InlineData(null, null, "")]
    public void PersistentExecutablePath_PrefersProcessPathThenFallsBack(
        string? processPath, string? executablePath, string expected) =>
        Assert.Equal(expected, Startup.ResolvePersistentExecutablePath(processPath, executablePath));

    /// <summary>
    /// 运行镜像的实际解析结果就是当前宿主的持久 exe（单文件发布即那个 exe），非空、绝对路径，
    /// 且不位于会被清理的 %TEMP%（Assembly.Location 在单文件下为空，不参与解析；这里以
    /// 「== 运行宿主」和「非临时」双重锁定）。
    /// </summary>
    [Fact]
    public void ResolvedStartupExecutablePath_IsTheRunningHostExe_NotATempPath()
    {
        string resolved = Startup.ResolvePersistentExecutablePath();
        Assert.False(string.IsNullOrWhiteSpace(resolved));
        Assert.True(Path.IsPathRooted(resolved));
        Assert.False(Startup.IsTransientExecutablePath(resolved));

        string? host = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(host))
            Assert.Equal(Path.GetFullPath(host!), Path.GetFullPath(resolved));
    }

    /// <summary>
    /// 任务注册所用的路径（ScheduledExecutablePath）就是该持久路径：这是「动作指向用户真正
    /// 会启动的那个 exe」的回归锁。
    /// </summary>
    [Fact]
    public void ScheduledExecutablePath_IsThePersistentHostExe()
    {
        Assert.Equal(Startup.ResolvePersistentExecutablePath(), Startup.ScheduledExecutablePath);
        Assert.False(Startup.IsTransientExecutablePath(Startup.ScheduledExecutablePath));
    }

    /// <summary>
    /// 临时目录（单文件自解压 / 更新器暂存）必须被识别，绝不作为持久自启动动作路径；
    /// 正常安装目录保持「持久」。
    /// </summary>
    [Fact]
    public void TransientExecutablePath_FlagsTempCopiesAndKeepsInstalledPaths()
    {
        const string tempRoot = @"C:\Users\someone\AppData\Local\Temp";

        Assert.True(Startup.IsTransientExecutablePath(
            @"C:\Users\someone\AppData\Local\Temp\L-Mechrevo-update\updater\L-Mechrevo.exe", tempRoot));
        Assert.True(Startup.IsTransientExecutablePath(
            @"C:\Users\someone\AppData\Local\Temp\lm-b14\L-Mechrevo.exe", tempRoot));
        Assert.False(Startup.IsTransientExecutablePath(
            @"C:\Program Files\L-Mechrevo\L-Mechrevo.exe", tempRoot));
        Assert.False(Startup.IsTransientExecutablePath(null, tempRoot));
        Assert.False(Startup.IsTransientExecutablePath(@"C:\Program Files\L-Mechrevo\L-Mechrevo.exe", null));
    }

    /// <summary>
    /// 生产装配的计划任务定义必须满足既有判据（登录触发 + 10s 延时、交互式令牌、LUA、重试），
    /// 且动作指向持久路径并携带 startup 参数——这是「启用即建对任务」的回归锁。
    /// </summary>
    [Fact]
    public void UserStartupTaskDefinition_MatchesThePlanAndTargetsThePersistentExe()
    {
        using TaskDefinition definition = Microsoft.Win32.TaskScheduler.TaskService.Instance.NewTask();
        Startup.ConfigureUserStartupTask(definition, System.Security.Principal.WindowsIdentity.GetCurrent().Name);

        Assert.True(Startup.MatchesUserStartupPlan(definition));

        var action = definition.Actions.OfType<Microsoft.Win32.TaskScheduler.ExecAction>().Single();
        Assert.Equal(Startup.ScheduledExecutablePath, action.Path);
        Assert.Equal("startup", action.Arguments);
        Assert.Equal(Microsoft.Win32.TaskScheduler.TaskRunLevel.LUA, definition.Principal.RunLevel);
        Assert.Equal(Microsoft.Win32.TaskScheduler.TaskLogonType.InteractiveToken, definition.Principal.LogonType);
    }
}
