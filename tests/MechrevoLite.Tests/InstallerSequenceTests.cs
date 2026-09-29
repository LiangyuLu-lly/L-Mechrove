namespace MechrevoLite.Tests;

/// <summary>
/// T23: the GCU install is uninstall-first. A previously registered GCUBridge service (and any
/// legacy registration that shares the name - every bundled vendor payload registers GCUBridge)
/// is removed before the new payload is copied, so the two generations cannot coexist and TCP
/// 13688 has a single owner. The runtime-copied {app}\GCU\<ServiceDir> and {app}\GCU\UWACPIDriver
/// are not Inno-managed, so they are added to [UninstallDelete].
/// </summary>
public class InstallerSequenceTests
{
    const string InstallScript = @"installer\Install-Gcu.ps1";
    const string UninstallScript = @"installer\Uninstall-Gcu.ps1";
    const string Iss = @"installer\L-Mechrevo.iss";

    /// <summary>
    /// beta21: upgrades are overlay installs. Running the previous uninstaller first deleted the GCU
    /// service's user state ({app}\GCU\...\UserPofiles, user fan curves) on every update.
    /// </summary>
    [Fact]
    public void Iss_OverlayInstall_NeverRunsThePreviousUninstaller()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        Assert.DoesNotContain("UnInstallOldVersion", iss, StringComparison.Ordinal);
        Assert.DoesNotContain("GetUninstallString", iss, StringComparison.Ordinal);
        Assert.DoesNotContain("UninstallingOld", iss, StringComparison.Ordinal);

        int prepare = iss.IndexOf("function PrepareToInstall", StringComparison.Ordinal);
        Assert.True(prepare >= 0, "PrepareToInstall is missing");
        string body = iss.Substring(prepare);
        int nextFunc = body.IndexOf("\nfunction ", 1, StringComparison.Ordinal);
        if (nextFunc >= 0)
            body = body.Substring(0, nextFunc);

        int stopLocked = body.IndexOf("StopLockedAppProcesses", StringComparison.Ordinal);
        int runtime = body.IndexOf("IsDesktopRuntime10Installed", StringComparison.Ordinal);
        Assert.True(stopLocked >= 0, "StopLockedAppProcesses must be invoked inside PrepareToInstall");
        Assert.True(runtime >= 0, "IsDesktopRuntime10Installed must remain in PrepareToInstall");
        Assert.True(stopLocked < runtime, "StopLockedAppProcesses must run BEFORE IsDesktopRuntime10Installed");
    }

    /// <summary>Same version = repair prompt; an older installer refuses to overwrite a newer install.</summary>
    [Fact]
    public void Iss_InitializeSetup_OffersRepairAndRefusesDowngrade()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        int init = iss.IndexOf("function InitializeSetup", StringComparison.Ordinal);
        Assert.True(init >= 0, "InitializeSetup is missing");
        string body = iss.Substring(init);
        int end = body.IndexOf("\nend;", StringComparison.Ordinal);
        body = body.Substring(0, end);

        Assert.Contains("GetInstalledAppVersion", body, StringComparison.Ordinal);
        Assert.Contains("AppVersionKey", body, StringComparison.Ordinal);
        Assert.Contains("DowngradeBlocked", body, StringComparison.Ordinal);
        Assert.Contains("SameVersionRepair", body, StringComparison.Ordinal);
        Assert.Contains("Result := False", body, StringComparison.Ordinal);
        // The ordering key: the beta number is part of the version (all 0.289.0 betas share AppVersionNumeric).
        Assert.Contains("DisplayVersion", iss, StringComparison.Ordinal);
        Assert.Contains("Beta := 9999", iss, StringComparison.Ordinal);
    }

    [Fact]
    public void Iss_ReenablesTheScheduledTasksItDisabledOnEveryExitPath()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        Assert.Contains("procedure DeinitializeSetup", iss, StringComparison.Ordinal);
        Assert.Contains("TasksDisabledBySetup := True", iss, StringComparison.Ordinal);
        Assert.Contains("Enable-ScheduledTask", iss, StringComparison.Ordinal);
    }

    [Fact]
    public void Iss_NeedRestartOnlyWhenTheGcuStepAskedForIt()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        int need = iss.IndexOf("function NeedRestart", StringComparison.Ordinal);
        Assert.True(need >= 0);
        string body = iss.Substring(need);
        body = body.Substring(0, body.IndexOf("\nend;", StringComparison.Ordinal));
        Assert.Contains("RebootRequired", body, StringComparison.Ordinal);
        Assert.Contains("Result := False", body, StringComparison.Ordinal);
        Assert.Contains("RegDeleteValue", body, StringComparison.Ordinal);
        // Install-Gcu.ps1 is the only writer of the flag.
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        Assert.Contains("'RebootRequired'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Iss_SilentInstallRelaunchesTheAppForTheOriginalUser()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        Assert.Contains("{param:RELAUNCH|0}", iss, StringComparison.Ordinal);
        string relaunch = iss.Split('\n').Single(l => l.Contains("ShouldRelaunchAfterSilentInstall", StringComparison.Ordinal)
            && l.TrimStart().StartsWith("Filename:", StringComparison.Ordinal));
        Assert.Contains("runasoriginaluser", relaunch, StringComparison.Ordinal);
        Assert.Contains("--after-update", relaunch, StringComparison.Ordinal);
        Assert.Contains("nowait", relaunch, StringComparison.Ordinal);
    }

    [Fact]
    public void Iss_RuntimeHashIsASha256AndMessagesUseInnoLineBreaks()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        var match = System.Text.RegularExpressions.Regex.Match(iss, "DotNetRuntimeSha256 = '([0-9A-Fa-f]+)'");
        Assert.True(match.Success, "DotNetRuntimeSha256 constant is missing");
        // DownloadTemporaryFile verifies SHA-256: a 128-digit SHA-512 here fails every download.
        Assert.Equal(64, match.Groups[1].Value.Length);
        // {break} is only valid in multi-string registry values; in messages it showed up literally.
        int custom = iss.IndexOf("[CustomMessages]", StringComparison.Ordinal);
        string messages = iss.Substring(custom, iss.IndexOf("\n[", custom + 1, StringComparison.Ordinal) - custom);
        Assert.DoesNotContain("{break}", messages, StringComparison.Ordinal);
        Assert.Contains("%n%n", messages, StringComparison.Ordinal);
    }

    [Fact]
    public void Iss_PrepareToInstall_StopsRunningAppBeforeFileCopy()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        Assert.Contains("CloseApplications=no", iss, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseApplications=yes", iss, StringComparison.Ordinal);
        Assert.Contains("procedure StopLockedAppProcesses", iss, StringComparison.Ordinal);
        Assert.Contains("/IM L-Mechrevo.exe /F /T", iss, StringComparison.Ordinal);
        Assert.Contains("LMechrevo*", iss, StringComparison.Ordinal);
        Assert.Contains("Disable-ScheduledTask", iss, StringComparison.Ordinal);
        Assert.Contains("Stop-ScheduledTask", iss, StringComparison.Ordinal);
    }

    [Fact]
    public void Iss_UninstallDelete_CoversRuntimeCopiedDirs()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        int section = iss.IndexOf("[UninstallDelete]", StringComparison.Ordinal);
        Assert.True(section >= 0, "L-Mechrevo.iss has no [UninstallDelete] section");
        string tail = iss.Substring(section);
        int next = tail.IndexOf("\n[", 1, StringComparison.Ordinal);
        string block = next >= 0 ? tail.Substring(0, next) : tail;

        Assert.Contains("filesandordirs", block, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("{app}\\GCU\\AiStoneService", block, StringComparison.Ordinal);
        Assert.Contains("{app}\\GCU\\UniwillService", block, StringComparison.Ordinal);
        Assert.Contains("{app}\\GCU\\UWACPIDriver", block, StringComparison.Ordinal);
        // The GamingCenterU legacy payload brings its own driver dir; the overlay install leaves a
        // transient user-state backup and registry snapshot behind only when something failed.
        Assert.Contains("{app}\\GCU\\ACPIDriver", block, StringComparison.Ordinal);
        Assert.Contains("{app}\\GCU\\state-backup", block, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallGcu_UninstallsPriorGenerationBeforeCopyingPayload()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        int uninstall = script.IndexOf("Invoke-PriorGenerationUninstall -TargetDir $TargetDir", StringComparison.Ordinal);
        int copy = script.IndexOf("Copy-Tree -Source $serviceSource -Destination $serviceTarget", StringComparison.Ordinal);
        Assert.True(uninstall >= 0, "Install-Gcu.ps1 must call Invoke-PriorGenerationUninstall");
        Assert.True(copy >= 0, "Install-Gcu.ps1 must copy the selected payload");
        Assert.True(uninstall < copy, "prior-generation removal must happen BEFORE the payload copy");
    }

    [Fact]
    public void InstallGcu_RemovesLegacyServiceViaUninstallScript()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        Assert.Contains("Uninstall-Gcu.ps1", script, StringComparison.Ordinal);
        Assert.Contains("-KeepDriver", script, StringComparison.Ordinal);
        Assert.Contains("prior-generation uninstall failed", script, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallGcu_RemovesCurrentAndLegacyServiceNames()
    {
        string script = GcuInstallerHarness.Read("installer", "Uninstall-Gcu.ps1");
        Assert.Contains("$script:LegacyServiceNames", script, StringComparison.Ordinal);
        Assert.Contains("'GCUBridge'", script, StringComparison.Ordinal);
        Assert.Contains("foreach", script, StringComparison.Ordinal);
        // Generation-blind: still removes the fixed firewall rule and the staged driver inf.
        Assert.Contains("L-Mechrevo - Block remote GCU MQTT", script, StringComparison.Ordinal);
        Assert.Contains("uwacpidriver", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UninstallGcu_UnregistersLMechrevoAutostartTaskBeforeOtherTeardown()
    {
        string script = GcuInstallerHarness.Read("installer", "Uninstall-Gcu.ps1");
        Assert.Contains("Unregister-ScheduledTask", script, StringComparison.Ordinal);
        Assert.Contains("LMechrevo_", script, StringComparison.Ordinal);

        int removeTask = script.IndexOf("\n    Remove-AutostartTask", StringComparison.Ordinal);
        int removeService = script.IndexOf("\n    Remove-GcuService", StringComparison.Ordinal);
        Assert.True(removeTask >= 0, "Uninstall-Gcu.ps1 must call Remove-AutostartTask");
        Assert.True(removeService >= 0, "Uninstall-Gcu.ps1 must call Remove-GcuService");
        Assert.True(removeTask < removeService,
            "Unregister-ScheduledTask / LMechrevo_ must run BEFORE other teardown");
    }

    [Fact]
    public void InstallGcu_PriorUninstallSuccess_InvokesUninstallScriptAndContinues()
    {
        string temp = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", "t23-ok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string stub = Path.Combine(temp, "stub-uninstall.ps1");
        File.WriteAllText(stub,
            "param([string]$TargetDir,[string]$LogDir,[switch]$KeepDriver,[switch]$DryRun)\r\n" +
            "Set-Content -LiteralPath (Join-Path $TargetDir 'uninstall-invoked.txt') -Value 'invoked' -Encoding ASCII\r\n" +
            "exit 0\r\n");
        string tint = temp.Replace("'", "''");
        try
        {
            PsResult result = GcuInstallerHarness.RunDotSourcedArgs(InstallScript,
                "-StagingRoot '" + tint + "' -TargetDir '" + tint + "'",
                "try { Invoke-PriorGenerationUninstall -TargetDir '" + tint + "' -UninstallScriptPath '" + stub.Replace("'", "''") + "' -DryRun | Out-Null; Write-Output 'OK' } "
                + "catch { Write-Output ('CAUGHT: ' + $_.Exception.Message) }");
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("OK", result.StdOut, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(temp, "uninstall-invoked.txt")),
                "the prior-generation uninstall script must actually be invoked");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }
}

/// <summary>
/// T23 failure surface: if the prior-generation uninstall fails (or is missing) the install must
/// abort with a clear reason rather than stacking a second service on top of a stale one.
/// </summary>
public class InstallerUninstallFailTests
{
    const string InstallScript = @"installer\Install-Gcu.ps1";
    const string UninstallScript = @"installer\Uninstall-Gcu.ps1";

    [Fact]
    public void InstallGcu_PriorUninstallFailure_AbortsInstall()
    {
        string temp = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", "t23-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string stub = Path.Combine(temp, "stub-failing-uninstall.ps1");
        File.WriteAllText(stub, "param([string]$TargetDir,[string]$LogDir,[switch]$KeepDriver,[switch]$DryRun)\r\nexit 9\r\n");
        string tint = temp.Replace("'", "''");
        try
        {
            PsResult result = GcuInstallerHarness.RunDotSourcedArgs(InstallScript,
                "-StagingRoot '" + tint + "' -TargetDir '" + tint + "'",
                "try { Invoke-PriorGenerationUninstall -TargetDir '" + tint + "' -UninstallScriptPath '" + stub.Replace("'", "''") + "' -DryRun | Out-Null; Write-Output 'NO-THROW' } "
                + "catch { Write-Output ('CAUGHT: ' + $_.Exception.Message) }");
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("CAUGHT:", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("aborting install", result.StdOut, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("9", result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void InstallGcu_MissingUninstallScript_AbortsInstall()
    {
        string temp = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", "t23-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string missing = Path.Combine(temp, "does-not-exist.ps1");
        string tint = temp.Replace("'", "''");
        try
        {
            PsResult result = GcuInstallerHarness.RunDotSourcedArgs(InstallScript,
                "-StagingRoot '" + tint + "' -TargetDir '" + tint + "'",
                "try { Invoke-PriorGenerationUninstall -TargetDir '" + tint + "' -UninstallScriptPath '" + missing.Replace("'", "''") + "' -DryRun | Out-Null; Write-Output 'NO-THROW' } "
                + "catch { Write-Output ('CAUGHT: ' + $_.Exception.Message) }");
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("CAUGHT:", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("not found", result.StdOut, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void UninstallGcu_DryRun_MakesNoChanges()
    {
        string temp = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", "t23-dry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            PsResult result = GcuInstallerHarness.RunScript(UninstallScript,
                "-TargetDir", temp, "-LogDir", temp, "-DryRun");
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("DRY RUN", result.StdOut, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }
}
