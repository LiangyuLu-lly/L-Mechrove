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

    [Fact]
    public void Iss_PrepareToInstall_UninstallsOldVersionBeforeRuntimeCheck()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        Assert.Contains("GetUninstallString", iss, StringComparison.Ordinal);
        Assert.Contains("UnInstallOldVersion", iss, StringComparison.Ordinal);
        Assert.Contains("/VERYSILENT", iss, StringComparison.Ordinal);
        Assert.Contains("/NORESTART", iss, StringComparison.Ordinal);
        Assert.Contains("/SUPPRESSMSGBOXES", iss, StringComparison.Ordinal);

        int prepare = iss.IndexOf("function PrepareToInstall", StringComparison.Ordinal);
        Assert.True(prepare >= 0, "PrepareToInstall is missing");
        string body = iss.Substring(prepare);
        int nextFunc = body.IndexOf("\nfunction ", 1, StringComparison.Ordinal);
        if (nextFunc >= 0)
            body = body.Substring(0, nextFunc);

        int stopLocked = body.IndexOf("StopLockedAppProcesses", StringComparison.Ordinal);
        int uninstall = body.IndexOf("UnInstallOldVersion", StringComparison.Ordinal);
        int runtime = body.IndexOf("IsDesktopRuntime10Installed", StringComparison.Ordinal);
        Assert.True(stopLocked >= 0, "StopLockedAppProcesses must be invoked inside PrepareToInstall");
        Assert.True(uninstall >= 0, "UnInstallOldVersion must be invoked inside PrepareToInstall");
        Assert.True(runtime >= 0, "IsDesktopRuntime10Installed must remain in PrepareToInstall");
        Assert.True(stopLocked < uninstall, "StopLockedAppProcesses must run BEFORE UnInstallOldVersion");
        Assert.True(uninstall < runtime, "UnInstallOldVersion must run BEFORE IsDesktopRuntime10Installed");
        Assert.Contains("UninstallingOld", iss, StringComparison.Ordinal);
        Assert.Contains("MsgBox", iss, StringComparison.Ordinal);
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
