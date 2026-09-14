using MechrevoLite.Gpu;

namespace MechrevoLite.Tests;

public class DgpuApplicationSafetyTests
{
    static readonly DateTime StableStartTime = new(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DgpuApplicationFilter_OnlyReturnsSafeInteractiveUserProcesses()
    {
        var applications = new[]
        {
            new DgpuApplication(101, "game", 1, StableStartTime, HasMainWindow: true),
            new DgpuApplication(102, "dwm", 1),
            new DgpuApplication(103, "worker", 0),
            new DgpuApplication(104, "L-Mechrevo", 1),
        };

        IReadOnlyList<DgpuApplication> closable = DgpuApplicationSafety.FilterClosable(
            applications, currentProcessId: 104, currentSessionId: 1);

        DgpuApplication application = Assert.Single(closable);
        Assert.Equal(101, application.ProcessId);
        Assert.Equal("game", application.ProcessName);
        Assert.Equal(1, application.SessionId);
        Assert.True(application.HasMainWindow);
    }

    [Theory]
    [InlineData("csrss")]
    [InlineData("winlogon")]
    [InlineData("nvcontainer")]
    [InlineData("explorer")]
    [InlineData("L-Mechrevo")]
    [InlineData("GCUService")]
    [InlineData("ControlCenter_5.17.51.34_Mechrevo")]
    public void DgpuApplicationFilter_ProtectsCriticalAndGraphicsShellProcesses(string processName)
    {
        var application = new DgpuApplication(101, processName, 1, StableStartTime);

        Assert.Empty(DgpuApplicationSafety.FilterClosable(
            new[] { application }, currentProcessId: 999, currentSessionId: 1));
    }

    [Fact]
    public void DgpuApplicationFilter_RequiresStableProcessIdentity()
    {
        var application = new DgpuApplication(101, "game", 1, HasMainWindow: true);

        Assert.Empty(DgpuApplicationSafety.FilterClosable(
            new[] { application }, currentProcessId: 999, currentSessionId: 1));
    }

    [Fact]
    public void DgpuApplicationSafety_StableSnapshotRemainsManageable()
    {
        var application = new DgpuApplication(101, "game", 1, StableStartTime, HasMainWindow: true);

        Assert.True(DgpuApplicationSafety.IsSafeCandidate(
            application, currentProcessId: 999, currentSessionId: 1));
    }
}
