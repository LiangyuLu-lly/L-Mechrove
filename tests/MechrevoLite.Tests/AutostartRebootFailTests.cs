using MechrevoLite.Helpers;

namespace MechrevoLite.Tests;

/// <summary>
/// T36 失败/边界路径：临时镜像不得注册；写失败必须如实返回；共存提示不得声称"已替你删除"。
/// happy 路径见 <see cref="AutostartRebootTests"/>。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class AutostartRebootFailTests
{
    [Fact]
    public void ATransientImagePathIsNeverRegistered()
    {
        Assert.True(Startup.IsTransientExecutablePath(Path.Combine(Path.GetTempPath(), "build", "L-Mechrevo.exe")));
        Assert.True(Startup.IsTransientExecutablePath(Path.Combine(Path.GetTempPath(), "L-Mechrevo.exe")));
        Assert.False(Startup.IsTransientExecutablePath(@"C:\Program Files\L-Mechrevo\L-Mechrevo.exe"));
    }

    [Fact]
    public void AFailedWriteIsReportedAsFailure()
    {
        Func<bool?> previousRead = Startup.ReadScheduledState;
        Func<bool, bool> previousWrite = Startup.WriteScheduledState;
        try
        {
            Startup.ReadScheduledState = static () => false;
            Startup.WriteScheduledState = static _ => false;

            Assert.False(Startup.ApplyScheduledState(true));
        }
        finally
        {
            Startup.ReadScheduledState = previousRead;
            Startup.WriteScheduledState = previousWrite;
        }
    }

    [Fact]
    public void AnUnreadableScheduledStateStillAttemptsTheWrite()
    {
        bool wrote = false;
        Func<bool?> previousRead = Startup.ReadScheduledState;
        Func<bool, bool> previousWrite = Startup.WriteScheduledState;
        try
        {
            Startup.ReadScheduledState = static () => null;   // 读不到：不能当成"已生效"
            Startup.WriteScheduledState = _ => { wrote = true; return true; };

            Assert.True(Startup.ApplyScheduledState(true));
            Assert.True(wrote);
        }
        finally
        {
            Startup.ReadScheduledState = previousRead;
            Startup.WriteScheduledState = previousWrite;
        }
    }

    [Fact]
    public void TheRemovalPromptNeverClaimsToHaveDeletedTheVendorConsole()
    {
        foreach (GcuCoexistenceKind kind in new[]
                 { GcuCoexistenceKind.ForeignService, GcuCoexistenceKind.PortOwner, GcuCoexistenceKind.Both })
        {
            string prompt = GcuCoexistence.BuildConsoleRemovalPrompt(kind);
            Assert.Contains("请", prompt);
            Assert.DoesNotContain("已删除", prompt);
            Assert.DoesNotContain("已为你卸载", prompt);
        }
    }

    [Fact]
    public void NoForeignGcuHasNoPromptText()
    {
        Assert.False(GcuCoexistence.RequiresConsoleRemovalPrompt(GcuCoexistenceKind.None));
    }

    // ---- 权限根因回归（T36 现场 bug）----------------------------------------
    // 旧实现的自启动自检是 UnSchedule(); Schedule();：删除成功、重建失败（无管理员权限 /
    // 任务被占用）时，用户直接失去自启动项，而失败只写进默认关闭的日志 = 静默失败。
    // 新接口只有"什么都不做"与"就地覆盖注册"两种动作，结构上不存在删除窗口。

    [Theory]
    [InlineData(true, true, false, false)]    // 任务存在且与计划一致 -> 不重写
    [InlineData(true, false, false, true)]    // 任务存在但已失效     -> 就地覆盖
    [InlineData(true, false, true, true)]     // 任务存在但已失效     -> 就地覆盖
    [InlineData(false, false, true, true)]    // 任务缺失且用户已启用 -> 注册
    [InlineData(false, false, false, false)]  // 任务缺失且用户未启用 -> 不注册
    public void TheRepairDecisionOnlyRegistersInPlaceAndNeverDeletes(
        bool taskExists, bool matchesPlan, bool startupEnabled, bool expectRegister)
    {
        int registrations = 0;
        bool report = Startup.RunStartupTaskCheck(
            taskExists, matchesPlan, startupEnabled, () => { registrations++; return true; });

        Assert.Equal(expectRegister ? 1 : 0, registrations);
        Assert.False(report);   // 注册成功就不上报
    }

    [Fact]
    public void AFailedRegistrationIsReportedWhenTheUserEnabledAutostart()
    {
        bool report = Startup.RunStartupTaskCheck(
            taskExists: true, matchesPlan: false, startupEnabled: true, register: () => false);

        Assert.True(report);
    }

    [Fact]
    public void AFailedRegistrationIsNotReportedWhenTheUserNeverEnabledAutostart()
    {
        bool report = Startup.RunStartupTaskCheck(
            taskExists: false, matchesPlan: false, startupEnabled: false, register: () => false);

        Assert.False(report);
    }

    [Fact]
    public void AutostartFailureIsSurfacedThroughTheSinkInsteadOfOnlyTheLog()
    {
        Action<string> previous = Startup.AutostartFailureSink;
        try
        {
            List<string> seen = new();
            Startup.AutostartFailureSink = seen.Add;

            Startup.ReportAutostartFailure("cannot register the autostart task");

            Assert.Equal(new[] { "cannot register the autostart task" }, seen);
        }
        finally
        {
            Startup.AutostartFailureSink = previous;
        }
    }
}
