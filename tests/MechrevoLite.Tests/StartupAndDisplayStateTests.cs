using MechrevoLite.Hardware;

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
}
