namespace MechrevoLite.Tests;

/// <summary>
/// N5: the installer is the single place where elevation is obtained. The autostart scheduled task
/// must be created by the elevated installer (with "run with highest privileges"), not by the app at
/// runtime - an unelevated app cannot create a highest-privileges task, which is the same permission
/// root cause as the original "reboot does not autostart" bug, only moved.
///
/// These tests read the installer scripts as text (they are external surfaces invoked by Inno [Run])
/// and dot-source them to exercise the pure decision helpers. Real task/ACL creation is BLOCKED-HW
/// and is covered by the first-machine runbook.
/// </summary>
public class InstallerPrivilegesTests
{
    const string InstallScript = @"installer\Install-Gcu.ps1";
    const string UninstallScript = @"installer\Uninstall-Gcu.ps1";
    const string Iss = @"installer\L-Mechrevo.iss";

    [Fact]
    public void InstallGcu_CreatesTheAutostartTaskWithHighestPrivileges()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        Assert.Contains("Register-ScheduledTask", script, StringComparison.Ordinal);
        Assert.Contains("-RunLevel Highest", script, StringComparison.Ordinal);
        Assert.Contains("LMechrevo", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallGcu_TaskActionIsTheExeWithNoArguments()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        // The action must be the app exe and must NOT carry an argument surface: an elevated task
        // whose action takes arguments is an injectable elevation primitive.
        Assert.Contains("New-ScheduledTaskAction", script, StringComparison.Ordinal);
        Assert.Contains("-Execute $AppExe", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-Argument", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallGcu_GrantsAclsForInstallConfigAndLogDirectories()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        Assert.Contains("Get-Acl", script, StringComparison.Ordinal);
        Assert.Contains("Set-Acl", script, StringComparison.Ordinal);
        Assert.Contains("FileSystemAccessRule", script, StringComparison.Ordinal);
        // Minimum rights that work: Modify (read/write/delete) for the app's own directories.
        Assert.Contains("Modify", script, StringComparison.Ordinal);
        // Never Everyone full control.
        Assert.DoesNotContain("Everyone", script, StringComparison.Ordinal);
        Assert.DoesNotContain("FullControl", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallGcu_IsInvokedByTheElevatedInstaller()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        Assert.Contains("Install-Gcu.ps1", iss, StringComparison.Ordinal);
        Assert.Contains("PrivilegesRequired=admin", iss, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallGcu_RemovesTheAutostartTask()
    {
        string script = GcuInstallerHarness.Read("installer", "Uninstall-Gcu.ps1");
        Assert.Contains("Unregister-ScheduledTask", script, StringComparison.Ordinal);
        Assert.Contains("LMechrevo", script, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallGcu_RemovesTheTaskAfterTheDryRunGuard()
    {
        string script = GcuInstallerHarness.Read("installer", "Uninstall-Gcu.ps1");
        int dryRun = script.IndexOf("if ($DryRun)", StringComparison.Ordinal);
        // The CALL site, not the function definition: the definition appears earlier in the file.
        int removeTask = script.IndexOf("\n    Remove-AutostartTask", StringComparison.Ordinal);
        Assert.True(dryRun >= 0, "Uninstall-Gcu.ps1 must keep its -DryRun guard");
        Assert.True(removeTask >= 0, "Uninstall-Gcu.ps1 must call Remove-AutostartTask");
        Assert.True(dryRun < removeTask, "the task removal must run after the dry-run guard");
    }

    [Fact]
    public void Iss_UninstallRun_InvokesTheUninstallScript()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        int section = iss.IndexOf("[UninstallRun]", StringComparison.Ordinal);
        Assert.True(section >= 0, "L-Mechrevo.iss has no [UninstallRun] section");
        string tail = iss.Substring(section);
        int next = tail.IndexOf("\n[", 1, StringComparison.Ordinal);
        string block = next >= 0 ? tail.Substring(0, next) : tail;
        Assert.Contains("Uninstall-Gcu.ps1", block, StringComparison.Ordinal);
    }
}

/// <summary>
/// N5 failure surface: the app-side path must be a no-op when the installer already created the
/// task, and must surface a visible error if it ever has to create it and cannot.
/// </summary>
public class InstallerPrivilegesFailTests
{
    [Fact]
    public void AppSidePath_IsANoOpWhenTheTaskAlreadyExists()
    {
        // The installer owns creation; the app only repairs a missing task. When the task exists
        // and matches the plan, the app must not register anything.
        int registrations = 0;
        bool report = Startup.RunStartupTaskCheck(
            taskExists: true, matchesPlan: true, startupEnabled: true, register: () => { registrations++; return true; });

        Assert.Equal(0, registrations);
        Assert.False(report);
    }

    [Fact]
    public void AppSidePath_ReportsWhenItCannotCreateTheTask()
    {
        bool report = Startup.RunStartupTaskCheck(
            taskExists: false, matchesPlan: false, startupEnabled: true, register: () => false);

        Assert.True(report);
    }

    [Fact]
    public void InstallGcu_DoesNotSwallowTaskCreationFailure()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        // The task step must be inside the main try/catch that exits non-zero, not a bare
        // "catch { }" that would leave the machine without an autostart entry and no signal.
        Assert.Contains("Register-AutostartTask", script, StringComparison.Ordinal);
        Assert.DoesNotContain("catch { }", script, StringComparison.Ordinal);
    }
}
