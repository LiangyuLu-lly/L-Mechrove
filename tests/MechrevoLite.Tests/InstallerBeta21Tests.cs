namespace MechrevoLite.Tests;

/// <summary>
/// beta21 installer contract: overlay install keeps the vendor service's user state, the payload's
/// own driver is installed, the privilege step runs on every path and no longer leaves user-write
/// grants on {app}, the legacy 10/20 service is verified by what it can actually report, and the
/// official-console removal identifies vendor products precisely.
/// </summary>
public class InstallerBeta21Tests
{
    const string InstallScript = @"installer\Install-Gcu.ps1";
    const string UninstallScript = @"installer\Uninstall-Gcu.ps1";

    static PsResult Install(string expression) =>
        GcuInstallerHarness.RunDotSourcedArgs(InstallScript, "-StagingRoot 'x' -TargetDir 'y'", expression);

    static PsResult Uninstall(string expression) =>
        GcuInstallerHarness.RunDotSourcedArgs(UninstallScript, "-TargetDir 'y'", expression);

    static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    // ---- official console removal: precise, never our own products ---------------------------

    [Theory]
    [InlineData("\"C:\\Program Files\\OEM\\Control Center\\unins000.exe\"", "",
        "\"C:\\Program Files\\OEM\\Control Center\\unins000.exe\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART")]
    [InlineData("MsiExec.exe /X{ABC}", "", "MsiExec.exe /X{ABC} /qn /norestart")]
    [InlineData("\"C:\\x\\unins000.exe\"", "\"C:\\x\\unins000.exe\" /VERYSILENT", "\"C:\\x\\unins000.exe\" /VERYSILENT")]
    public void VendorUninstallCommandLine_IsSilentForInnoAndMsiUninstallers(string normal, string quiet, string expected)
    {
        // A plain Inno UninstallString opens a confirmation dialog, which in a hidden window hangs.
        PsResult result = Install("Get-VendorUninstallCommandLine -Quiet " + Quote(quiet) + " -Normal " + Quote(normal));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut.Trim());
    }

    [Fact]
    public void VendorRemoval_SnapshotsAndRestoresTheGcuRegistryAndRescansDevices()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        int snapshot = script.IndexOf("$registrySnapshot = Save-GcuRegistrySnapshot", StringComparison.Ordinal);
        int uninstall = script.IndexOf("Invoke-VendorUninstallCommand -DisplayName $display", StringComparison.Ordinal);
        int restore = script.IndexOf("Restore-GcuRegistrySnapshot -File $registrySnapshot", StringComparison.Ordinal);
        Assert.True(snapshot >= 0 && uninstall > snapshot, "the GCU registry must be snapshotted before the first vendor uninstaller runs");
        Assert.True(restore > uninstall, "the snapshot must be restored after the vendor uninstallers ran");
        Assert.Contains("/scan-devices", script, StringComparison.Ordinal);
        Assert.Contains("'/reg:64'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyOnlyFastPath_FallsBackToTheFullPathWhileAVendorConsoleIsPresent()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        int verifyOnly = script.IndexOf("if ($action -eq 'VerifyOnly'", StringComparison.Ordinal);
        int nextStep = script.IndexOf("Write-Log '[1/8]", verifyOnly, StringComparison.Ordinal);
        string block = script.Substring(verifyOnly, nextStep - verifyOnly);
        Assert.Contains("Test-VendorConsolePresent", block, StringComparison.Ordinal);
        Assert.Contains("-not $vendorPresent", block, StringComparison.Ordinal);
    }

    // ---- overlay install: the vendor service's user state survives -------------------------

    [Fact]
    public void UserStateFiles_AreTheProfilesSettingsAndTopLevelFanCurvesOnly()
    {
        string temp = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", "b21-state-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(temp, "AiStoneService", "MyControlCenter");
        try
        {
            foreach (string rel in new[]
                     {
                         @"UserPofiles\Mode4_Profile1.json", @"UserFanTables\M4T1.json", @"SQLiteDB\GamdeMode.db",
                         @"UserFanTables\PH4AQE3\M1T1.json",          // vendor per-model data: never user state
                         @"UserFanTables\DefaultFanTable_Gaming.json", // vendor default: never user state
                         @"Command\CC.xml",                           // payload file
                     })
            {
                string path = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, rel);
            }

            PsResult result = Install("(Get-GcuUserStateFiles -ServiceDir " + Quote(Path.Combine(temp, "AiStoneService"))
                + " | Sort-Object) -join '|'");
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(@"SQLiteDB\GamdeMode.db|UserFanTables\M4T1.json|UserPofiles\Mode4_Profile1.json", result.StdOut.Trim());
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void UserState_SurvivesTheOverlayCopyOfTheFactoryPayload()
    {
        string temp = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", "b21-overlay-" + Guid.NewGuid().ToString("N"));
        string payload = Path.Combine(temp, "payload", "AiStoneService");
        string installed = Path.Combine(temp, "installed", "AiStoneService");
        try
        {
            foreach (string dir in new[] { payload, installed })
            {
                Directory.CreateDirectory(Path.Combine(dir, "MyControlCenter", "UserPofiles"));
                Directory.CreateDirectory(Path.Combine(dir, "MyControlCenter", "UserFanTables"));
            }
            File.WriteAllText(Path.Combine(payload, "MyControlCenter", "UserPofiles", "Mode4_Profile1.json"), "factory");
            File.WriteAllText(Path.Combine(payload, "MyControlCenter", "UserFanTables", "M4T1.json"), "factory");
            File.WriteAllText(Path.Combine(payload, "MyControlCenter", "GCUService.exe"), "new binary");
            File.WriteAllText(Path.Combine(installed, "MyControlCenter", "UserPofiles", "Mode4_Profile1.json"), "user tuned");
            File.WriteAllText(Path.Combine(installed, "MyControlCenter", "UserFanTables", "M4T1.json"), "user curve");
            File.WriteAllText(Path.Combine(installed, "MyControlCenter", "GCUService.exe"), "old binary");
            // robocopy compares size + timestamp; a real older install has older payload timestamps.
            foreach (string file in Directory.GetFiles(installed, "*", SearchOption.AllDirectories))
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));

            string backup = Path.Combine(temp, "installed", "state-backup");
            PsResult result = Install(
                "$s = Save-GcuUserState -ServiceDir " + Quote(installed) + " -BackupDir " + Quote(backup) + "; "
                + "Copy-Tree -Source " + Quote(payload) + " -Destination " + Quote(installed) + "; "
                + "Restore-GcuUserState -Saved $s -ServiceDir " + Quote(installed) + " | Out-Null; 'done'");
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("done", result.StdOut, StringComparison.Ordinal);

            Assert.Equal("user tuned", File.ReadAllText(Path.Combine(installed, "MyControlCenter", "UserPofiles", "Mode4_Profile1.json")));
            Assert.Equal("user curve", File.ReadAllText(Path.Combine(installed, "MyControlCenter", "UserFanTables", "M4T1.json")));
            Assert.Equal("new binary", File.ReadAllText(Path.Combine(installed, "MyControlCenter", "GCUService.exe")));
            Assert.False(Directory.Exists(backup), "the transient backup must be removed after the restore");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TheOverlayCopyIsWrappedBySaveAndRestore()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        int save = script.IndexOf("$savedState = if ($DryRun)", StringComparison.Ordinal);
        int copy = script.IndexOf("Copy-Tree -Source $serviceSource -Destination $serviceTarget", StringComparison.Ordinal);
        int restore = script.IndexOf("Restore-GcuUserState -Saved $savedState -ServiceDir $serviceTarget", StringComparison.Ordinal);
        Assert.True(save >= 0 && save < copy && copy < restore);
    }

    // ---- driver: the payload's own INF, and uninstall removes only what we added --------------

    [Fact]
    public void TheDriverComesFromTheSelectedPayload()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        Assert.Contains("$selection.DriverInf", script, StringComparison.Ordinal);
        Assert.Contains("Install-AcpiDriver -DriverDir $driverTarget -InfName $driverInfName", script, StringComparison.Ordinal);
        // pnputil 3010 = installed, reboot needed -> recorded for the Inno NeedRestart hook.
        Assert.Contains("$code -eq 3010", script, StringComparison.Ordinal);
        Assert.Contains("GcuDriverPublishedName", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumDriversIsParsedWithoutRelyingOnEnglishLabels()
    {
        // zh-CN pnputil prints localized labels; the old "Published Name:" regex never matched there.
        string published = "\u53D1\u5E03\u540D\u79F0";   // 发布名称
        string original = "\u539F\u59CB\u540D\u79F0";    // 原始名称
        string lines = string.Join(",", new[]
        {
            Quote(published + ":     oem12.inf"), Quote(original + ":     nvlti.inf"), "''",
            Quote(published + ":     oem153.inf"), Quote(original + ":     uwacpidriver.inf"), "''",
            Quote("Published Name:     oem7.inf"), Quote("Original Name:      ACPIDriver.inf"), "''",
            Quote("Published Name:     oem9.inf"), Quote("Original Name:      acpidriverx.inf"),
        });
        PsResult result = Uninstall("(Get-AcpiDriverPublishedNames -Lines @(" + lines + ")) -join ','");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("oem153.inf,oem7.inf", result.StdOut.Trim());
    }

    [Fact]
    public void UninstallRemovesOnlyTheRecordedDriverPackage()
    {
        string script = GcuInstallerHarness.Read("installer", "Uninstall-Gcu.ps1");
        Assert.Contains("'GcuDriverPublishedName'", script, StringComparison.Ordinal);
        Assert.Contains("$_ -ieq $recorded", script, StringComparison.Ordinal);
    }

    // ---- privileges -------------------------------------------------------------------------

    [Fact]
    public void ThePrivilegeStepRunsOnTheFastPathAndTheFullPath()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        int main = script.IndexOf("if ($MyInvocation.InvocationName -eq '.')", StringComparison.Ordinal);
        string body = script.Substring(main);
        int calls = System.Text.RegularExpressions.Regex.Matches(body, @"Invoke-PrivilegeStep -AppExe").Count;
        Assert.Equal(2, calls);
    }

    [Fact]
    public void TheInstallDirectoriesAreNoLongerGrantedToTheUser()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        // A per-user Modify ACE on {app} applies to the filtered token too: any user process could
        // replace the exe that the Highest / SYSTEM tasks run elevated.
        Assert.DoesNotContain("Grant-AppDirectoryAcl -Path (Split-Path -Parent $TargetDir)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Grant-AppDirectoryAcl -Path $TargetDir", script, StringComparison.Ordinal);
        Assert.Contains("Revoke-AppDirectoryUserAcl -Path (Split-Path -Parent $TargetDir)", script, StringComparison.Ordinal);
        Assert.Contains("Revoke-AppDirectoryUserAcl -Path $TargetDir", script, StringComparison.Ordinal);
        int revoke = script.IndexOf("Revoke-AppDirectoryUserAcl -Path (Split-Path -Parent $TargetDir)", script.IndexOf("downgrade check =", StringComparison.Ordinal), StringComparison.Ordinal);
        int firstCopy = script.IndexOf("Copy-Tree -Source $serviceSource", StringComparison.Ordinal);
        Assert.True(revoke >= 0 && revoke < firstCopy, "user grants must be revoked before anything is written below {app}");
    }

    [Fact]
    public void TheChargeTaskIsReenabledAndNotDeletedByTheServiceReplacement()
    {
        string install = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        string uninstall = GcuInstallerHarness.Read("installer", "Uninstall-Gcu.ps1");
        Assert.Contains("function Enable-LMechrevoTasks", install, StringComparison.Ordinal);
        Assert.Contains("$uninstallArgs += '-KeepTasks'", install, StringComparison.Ordinal);
        Assert.Contains("[switch]$KeepTasks", uninstall, StringComparison.Ordinal);
        Assert.Contains("if ($KeepTasks)", uninstall, StringComparison.Ordinal);
    }

    // ---- legacy GamingCenterU service --------------------------------------------------------

    [Fact]
    public void LegacyService_IsNotFailedForFactsItNeverReports()
    {
        PsResult legacy = Install("(Test-GcuPostInstall -ServiceNames @('GCUBridge') -ListenerPids @(1) -ItemSupportPresent $false -ServiceReady -1 -Legacy).Ok");
        PsResult modern = Install("(Test-GcuPostInstall -ServiceNames @('GCUBridge') -ListenerPids @(1) -ItemSupportPresent $false -ServiceReady -1).Ok");
        Assert.Equal("True", legacy.StdOut.Trim());
        Assert.Equal("False", modern.StdOut.Trim());
    }

    [Theory]
    [InlineData("@(58594, 13688)", "13688")]
    [InlineData("@(58594, 1883)", "1883")]
    [InlineData("@()", "0")]
    public void TheMqttPortIsTakenFromWhatTheBridgeListensOn(string ports, string expected)
    {
        PsResult result = Install("Select-GcuMqttPort -Ports " + ports);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut.Trim());
    }
}
