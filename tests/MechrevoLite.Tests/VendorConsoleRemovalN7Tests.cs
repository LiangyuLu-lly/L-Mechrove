namespace MechrevoLite.Tests;

/// <summary>
/// N7 (owner correction): the installer MUST remove the vendor's official console as well as the
/// existing GCU service, leaving only our console. The earlier "prompt the user, never delete"
/// rule was the orchestrator's invention and is withdrawn.
///
/// Removing a vendor application is destructive, so the scope is pinned by a testable predicate:
/// vendor-console + GCU artefacts ARE in the removal set; unrelated software is NOT.
/// </summary>
public class VendorConsoleRemovalN7Tests
{
    const string InstallScript = @"installer\Install-Gcu.ps1";

    static PsResult Classify(string kind, string value)
        => GcuInstallerHarness.RunDotSourcedArgs(InstallScript,
            "-StagingRoot 'x' -TargetDir 'y'",
            "Test-VendorArtefactRemovable -Kind '" + kind + "' -Value '" + value.Replace("'", "''") + "'");

    // ---- vendor artefacts ARE removable -------------------------------------

    [Theory]
    [InlineData("package", "CCU.WinUI_wrbgcf7aesyd8")]
    [InlineData("package", "GamingCenter3_Cross.UWP")]
    [InlineData("package", "ControlCenterU")]
    [InlineData("package", "GamingCenterU")]
    [InlineData("directory", @"C:\Program Files\L-Mechrevo\GCU")]
    [InlineData("directory", @"C:\Program Files\L-Mechrevo\GCU\AiStoneService")]
    [InlineData("directory", @"C:\Program Files\L-Mechrevo\GCU\UniwillService")]
    [InlineData("uninstall", "机械革命电竞控制台")]
    [InlineData("uninstall", "Mechrevo Gaming Center")]
    [InlineData("shortcut", @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\GamingCenter\GamingCenter.lnk")]
    [InlineData("autostart", "CCUWinUI.exe")]
    [InlineData("autostart", "SystrayComponent.exe")]
    [InlineData("process", "CCUWinUI")]
    [InlineData("process", "SystrayComponent")]
    public void VendorConsoleArtefactsAreRemovable(string kind, string value)
    {
        PsResult result = Classify(kind, value);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("REMOVABLE", result.StdOut, StringComparison.Ordinal);
    }

    // ---- unrelated software is NOT ------------------------------------------

    [Theory]
    [InlineData("package", "Microsoft.WindowsCalculator")]
    [InlineData("package", "Microsoft.BingWeather")]
    [InlineData("package", "38002AlexanderFrangos.TwinkleTray")]
    [InlineData("directory", @"C:\Program Files\NVIDIA Corporation")]
    [InlineData("directory", @"C:\Program Files\Realtek")]
    [InlineData("directory", @"C:\Program Files\Autodesk")]
    [InlineData("directory", @"C:\Program Files\Mechrevo\MRAfterSaleService")]
    [InlineData("uninstall", "NVIDIA Platform Controllers and Framework")]
    [InlineData("uninstall", "Realtek Ethernet Controller Driver")]
    [InlineData("uninstall", "360 Wargaming Game Center")]
    [InlineData("shortcut", @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\MVS\User Manual.lnk")]
    [InlineData("autostart", "SecurityHealth")]
    [InlineData("autostart", "RtkAudUService")]
    [InlineData("autostart", "GameViewer")]
    [InlineData("process", "explorer")]
    [InlineData("process", "L-Mechrevo")]
    public void UnrelatedSoftwareIsNotRemovable(string kind, string value)
    {
        PsResult result = Classify(kind, value);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("KEEP", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstallerRemovesTheVendorConsoleAndReportsEachStep()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        Assert.Contains("Remove-VendorConsole", script, StringComparison.Ordinal);
        Assert.Contains("Remove-AppxPackage", script, StringComparison.Ordinal);
        // Every step must be announced, not silent.
        Assert.Contains("found existing GCU service", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("found official console", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AVendorRemovalFailureIsReportedAndDoesNotAbortTheInstall()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        // A locked/reboot-pending vendor component must be reported and skipped, never fatal.
        Assert.Contains("could not be removed", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("continuing", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRemovalNeverTouchesTheReleaseTree()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        // release\ is the fallback material and must never be a removal target. The only mentions
        // of the retired trees are read-only guidance strings, so assert on the removal calls.
        Assert.DoesNotContain(@"Remove-Item -LiteralPath $release", script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"Remove-Item -LiteralPath (Join-Path $root 'release", script, StringComparison.Ordinal);
        // The allow-list is the only directory removal surface, and it never names release\.
        int allowList = script.IndexOf("VendorDirectoryAllowList = @(", StringComparison.Ordinal);
        Assert.True(allowList >= 0, "the vendor directory allow-list must exist");
        int end = script.IndexOf(")", allowList, StringComparison.Ordinal);
        string block = script.Substring(allowList, end - allowList);
        Assert.DoesNotContain("release", block, StringComparison.OrdinalIgnoreCase);
    }
}
