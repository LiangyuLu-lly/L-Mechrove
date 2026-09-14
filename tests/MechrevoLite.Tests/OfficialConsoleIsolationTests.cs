using MechrevoLite.Helpers;

namespace MechrevoLite.Tests;

public class OfficialConsoleIsolationTests
{
    [Fact]
    public void IsolatedConnectedStatus_DescribesGcuOnlyRuntime()
    {
        string detail = OfficialConsoleIsolation.DescribeStatus(
            isolated: true,
            uiRunning: false,
            installed: true,
            gcuRunning: true);

        Assert.Equal("已隔离 · 仅 GCU 后台运行", detail);
    }

    [Fact]
    public void IsolatedStartingStatus_DoesNotClaimGcuIsStopped()
    {
        string detail = OfficialConsoleIsolation.DescribeStatus(
            isolated: true,
            uiRunning: false,
            installed: true,
            gcuRunning: false);

        Assert.Equal("已隔离 · GCU 后台待连接", detail);
    }

    [Fact]
    public void IsolatedWithOfficialUiRunning_ReportsIsolationFailure()
    {
        string detail = OfficialConsoleIsolation.DescribeStatus(
            isolated: true,
            uiRunning: true,
            installed: true,
            gcuRunning: true);

        Assert.Equal("隔离异常 · 官方界面仍在运行", detail);
    }

    [Fact]
    public void InstalledOfficialConsole_ExplainsThatUninstallIsNotRequired()
    {
        string detail = OfficialConsoleIsolation.DescribeStatus(
            isolated: false,
            uiRunning: true,
            installed: true,
            gcuRunning: true);

        Assert.Equal("无需卸载 · 点击右侧按钮即可隔离", detail);
    }
}
