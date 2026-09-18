namespace MechrevoLite.Tests;

/// <summary>
/// N10 (owner): the first-use guide must stop telling users to download the official console. The
/// installer now DELETES the vendor console, so sending the user to reinstall it is a direct
/// contradiction - it would put back what we just removed.
///
/// The message must be that our console is self-sufficient (the installer already set up the GCU
/// service) and that the vendor console was cleaned up / replaced.
/// </summary>
public class FirstUseGuideN10Tests
{
    static string Guide() => GcuInstallerHarness.Read("src", "MechrevoLiteWin", "FirstRunGuideForm.cs");

    [Fact]
    public void TheGuideNoLongerTellsTheUserToInstallTheOfficialConsole()
    {
        string guide = Guide();
        // The old step 1 was "install the official 50-series console" and told the user not to
        // uninstall it. Both must be gone.
        Assert.DoesNotContain("安装官方", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("不要卸载", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuideNoLongerOffersAnOfficialDownloadButton()
    {
        string guide = Guide();
        Assert.DoesNotContain("官方下载", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenOfficialDownloads", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuideSaysOurConsoleIsSelfSufficient()
    {
        string guide = Guide();
        // The replacement message: the installer already set up the GCU service; nothing else needed.
        Assert.Contains("安装器", guide, StringComparison.Ordinal);
        Assert.Contains("GCU", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuideSaysTheVendorConsoleWasCleanedUp()
    {
        string guide = Guide();
        Assert.Contains("清理", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuideNoLongerTellsTheUserToIsolateTheOfficialUi()
    {
        string guide = Guide();
        // "isolate the official UI and tray" only made sense while the vendor console stayed.
        Assert.DoesNotContain("隔离官方界面", guide, StringComparison.Ordinal);
    }
}
