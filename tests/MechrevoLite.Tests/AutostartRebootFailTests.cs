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
}
