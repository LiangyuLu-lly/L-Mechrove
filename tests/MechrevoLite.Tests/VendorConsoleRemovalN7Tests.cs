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
    [InlineData("uninstall", "机械革命电竞控制台")]
    [InlineData("uninstall", "Mechrevo Gaming Center")]
    [InlineData("directory", @"C:\Program Files\OEM\ControlCenter")]
    [InlineData("directory", @"C:\Program Files\CCUWinUI")]
    [InlineData("directory", @"C:\Program Files (x86)\GamingCenter")]
    [InlineData("shortcut", @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\GamingCenter\GamingCenter.lnk")]
    [InlineData("autostart", "CCUWinUI.exe")]
    [InlineData("autostart", "SystrayComponent.exe")]
    [InlineData("process", "CCUWinUI")]
    [InlineData("process", "SystrayComponent")]
    // beta21: every vendor console generation is the same Inno product (one AppId) ...
    [InlineData("uninstallkey", "{6ea3ce12-b991-4b65-9f8d-b148eaaecd87}_is1")]
    [InlineData("uninstall", "Control Center")]
    [InlineData("uninstall", "GamingCenterU")]
    [InlineData("uninstall", "机械革命控制中心")]
    // ... and the 30/40-series UWP package identity is ControlCenter3.
    [InlineData("package", "ControlCenter3")]
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
    [InlineData("directory", @"C:\Program Files\L-Mechrevo\GCU")]
    [InlineData("directory", @"C:\Program Files\L-Mechrevo\GCU\payload")]
    [InlineData("directory", @"C:\Program Files\L-Mechrevo\GCU\AiStoneService")]
    [InlineData("directory", @"C:\Program Files\L-Mechrevo\GCU\UniwillService")]
    [InlineData("directory", @"C:\Program Files\L-Mechrevo\GCU\UWACPIDriver")]
    [InlineData("uninstall", "NVIDIA Platform Controllers and Framework")]
    [InlineData("uninstall", "Realtek Ethernet Controller Driver")]
    [InlineData("uninstall", "360 Wargaming Game Center")]
    [InlineData("shortcut", @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\MVS\User Manual.lnk")]
    [InlineData("autostart", "SecurityHealth")]
    [InlineData("autostart", "RtkAudUService")]
    [InlineData("autostart", "GameViewer")]
    [InlineData("process", "explorer")]
    [InlineData("process", "L-Mechrevo")]
    // beta21: the old bare "Mechrevo" marker matched our own uninstall entry and other Mechrevo apps.
    [InlineData("uninstall", "L-Mechrevo 0.289.0-beta18")]
    [InlineData("uninstall", "L-Mechrevo GCU 0.289.0-beta20")]
    [InlineData("uninstall", "机械革命电子保修卡")]
    [InlineData("uninstall", "Intel(R) Graphics Command Center")]
    [InlineData("uninstallkey", "{8F4E2C71-9B3A-4D6E-A1C2-7E5B9D0F3A64}_is1")]
    // An uninstall key without a DisplayName must classify, not abort the removal with a binding error.
    [InlineData("uninstall", "")]
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
        Assert.Contains("Get-AppxPackage -AllUsers", script, StringComparison.Ordinal);
        Assert.Contains("QuietUninstallString", script, StringComparison.Ordinal);
        Assert.Contains("UninstallString", script, StringComparison.Ordinal);
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
        Assert.Contains("vendor-console removal failed (non-fatal)", script, StringComparison.Ordinal);
        Assert.Contains("Get-NotePropertyString", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$props.DisplayName", script, StringComparison.Ordinal);
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
