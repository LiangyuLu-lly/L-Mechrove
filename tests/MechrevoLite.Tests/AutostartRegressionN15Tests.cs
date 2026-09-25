namespace MechrevoLite.Tests;

/// <summary>
/// N15 #14 (field regression): `耀世16super4080` reports "开机自启动没有用", and the report notes the
/// beta16 installer autostarted fine. The regression is in the installer's task registration added
/// after beta16: it registers the action with NO arguments, while the app's own contract requires
/// the `startup` argument (Helpers\Startup.cs:329 compares `arguments == "startup"`, :360 registers
/// it). A task without that argument never matches the app's plan, so the app rewrites it and the
/// launch path is wrong.
/// </summary>
public class AutostartRegressionN15Tests
{
    const string InstallScript = @"installer\Install-Gcu.ps1";

    [Fact]
    public void TheInstallerRegistersTheStartupArgumentTheAppExpects()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        // The action must carry the `startup` argument, matching Startup.cs's own registration.
        Assert.Contains("-Argument 'startup'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstallerTaskMatchesTheAppsOwnPlan()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        string startup = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Helpers", "Startup.cs");
        // The app compares the action's arguments to "startup"; the installer must produce that.
        Assert.Contains("arguments.Equals(\"startup\"", startup, StringComparison.Ordinal);
        Assert.Contains("startup", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstallerTaskUsesTheSameTaskNameAsTheApp()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        string startup = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Helpers", "Startup.cs");
        // The app composes the per-user name as taskName + "_" + SID (Startup.cs:14,22); the
        // installer must produce the same shape so the app recognises its task.
        Assert.Contains("taskName + \"_\" + currentUserSid", startup, StringComparison.Ordinal);
        Assert.Contains("'LMechrevo_' + $sid", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstallerTaskKeepsTheHighestRunLevel()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        Assert.Contains("-RunLevel Highest", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AutostartIsRegisteredBeforeGcuVerificationCanAbort()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        int autostart = script.IndexOf("Register-AutostartTask -AppExe", StringComparison.Ordinal);
        int fatal = script.LastIndexOf("exit 1", StringComparison.Ordinal);
        Assert.True(autostart >= 0, "installer must register the autostart task");
        Assert.True(fatal >= 0, "installer still has a verification abort");
        Assert.True(autostart < fatal, "autostart must be registered before GCU verification can exit 1");
    }

    [Fact]
    public void MissingAppExeSkipsAutostartInsteadOfThrowing()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        Assert.Contains("skipping autostart task: app exe not found", script, StringComparison.Ordinal);
        Assert.DoesNotContain("autostart task target not found", script, StringComparison.Ordinal);
    }
}
