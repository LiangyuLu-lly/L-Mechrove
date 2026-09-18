using System.Text.Json;

namespace MechrevoLite.Tests;

/// <summary>
/// T26: after the service starts, the installer verifies four invariants - exactly one GCU service,
/// exactly one owner of TCP 13688, the service-written ItemSupport key, and ServiceReady == 1 (a
/// value the vendor service sets, never us). Failure surfaces explicitly because Inno [Run] has no
/// ignoreerrors: a non-zero exit is only logged and setup still reports success.
/// </summary>
public class PostInstallVerificationTests
{
    const string InstallScript = @"installer\Install-Gcu.ps1";

    [Fact]
    public void AllChecksPass_WhenFactsAreHealthy()
    {
        PsResult result = DotSource(
            "Test-GcuPostInstall -ServiceNames @('GCUBridge') -ListenerPids @(1234) -ItemSupportPresent $true -ServiceReady 1 | ConvertTo-Json -Depth 6");
        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut.Trim());
        Assert.True(doc.RootElement.GetProperty("Ok").GetBoolean());
        Assert.Equal(4, doc.RootElement.GetProperty("Checks").GetArrayLength());
        Assert.Equal(0, doc.RootElement.GetProperty("Failed").GetArrayLength());
    }

    [Fact]
    public void StatusFile_IsWrittenReady()
    {
        string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "L-Mechrevo-tests", "t26-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            PsResult result = DotSource(
                "Write-GcuInstallStatus -StatusDir '" + Esc(temp) + "' -Status 'ready' -Reason 'all checks passed' -Checks @() | Out-Null; Write-Output 'OK'");
            Assert.Equal(0, result.ExitCode);
            string statusFile = System.IO.Path.Combine(temp, "gcu-install-status.json");
            Assert.True(File.Exists(statusFile), "the post-install status file must be written");
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(statusFile));
            Assert.Equal("ready", doc.RootElement.GetProperty("Status").GetString());
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ServiceReady_IsReadNeverWrittenByUs()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        // The check must read the vendor key ...
        Assert.Contains("GamingCenter2", script, StringComparison.Ordinal);
        Assert.Contains("ServiceReady", script, StringComparison.Ordinal);
        // ... and no line may write ServiceReady.
        foreach (string line in script.Split('\n'))
        {
            if (line.Contains("ServiceReady", StringComparison.Ordinal))
                Assert.DoesNotContain("Set-ItemProperty", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InstallGcu_VerifiesAfterStartBeforeReportingSuccess()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        int verify = script.IndexOf("Test-GcuPostInstall", StringComparison.Ordinal);
        int success = script.IndexOf("Write-Log 'GCU install OK'", StringComparison.Ordinal);
        Assert.True(verify >= 0, "Install-Gcu.ps1 must run the post-install verification");
        Assert.True(success >= 0, "Install-Gcu.ps1 must report success");
        Assert.True(verify < success, "verification must run before success is reported");
    }

    static PsResult DotSource(string expression)
        => GcuInstallerHarness.RunDotSourcedArgs(InstallScript, "-StagingRoot 'x' -TargetDir 'y'", expression);

    static string Esc(string path) => path.Replace("'", "''");
}

/// <summary>
/// T26 failure surface: every broken invariant fails the post-install verification with an
/// actionable reason, and the false-success surface (Inno [Run] without ignoreerrors) is made
/// detectable through an explicit FATAL log line plus a failed status file.
/// </summary>
public class PostInstallVerificationFailTests
{
    const string InstallScript = @"installer\Install-Gcu.ps1";

    [Theory]
    [InlineData("@()", "@(1234)", "$true", "1", "single-service")]                       // no service
    [InlineData("@('GCUBridge','GCUBridge')", "@(1234)", "$true", "1", "single-service")] // duplicate
    [InlineData("@('GCUBridge')", "@()", "$true", "1", "single-13688-owner")]             // no listener
    [InlineData("@('GCUBridge')", "@(1,2)", "$true", "1", "single-13688-owner")]          // two listeners
    [InlineData("@('GCUBridge')", "@(1234)", "$false", "1", "item-support")]              // service did not write it
    [InlineData("@('GCUBridge')", "@(1234)", "$true", "0", "service-ready")]              // not ready
    [InlineData("@('GCUBridge')", "@(1234)", "$true", "-1", "service-ready")]             // missing
    public void BrokenInvariants_FailWithTheRightCheck(
        string services, string pids, string itemSupport, string ready, string expectedCheck)
    {
        PsResult result = DotSource(
            "Test-GcuPostInstall -ServiceNames " + services + " -ListenerPids " + pids
            + " -ItemSupportPresent " + itemSupport + " -ServiceReady " + ready + " | ConvertTo-Json -Depth 6");
        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut.Trim());
        Assert.False(doc.RootElement.GetProperty("Ok").GetBoolean());
        string[] failedNames = doc.RootElement.GetProperty("Failed").EnumerateArray()
            .Select(e => e.GetProperty("Name").GetString() ?? "").ToArray();
        Assert.Contains(expectedCheck, failedNames);
    }

    [Fact]
    public void FailureStatusFile_IsWrittenFailed()
    {
        string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "L-Mechrevo-tests", "t26-failed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            PsResult result = DotSource(
                "Write-GcuInstallStatus -StatusDir '" + Esc(temp) + "' -Status 'failed' -Reason 'service-ready: ServiceReady == 0' -Checks @() | Out-Null; Write-Output 'OK'");
            Assert.Equal(0, result.ExitCode);
            string statusFile = System.IO.Path.Combine(temp, "gcu-install-status.json");
            Assert.True(File.Exists(statusFile));
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(statusFile));
            Assert.Equal("failed", doc.RootElement.GetProperty("Status").GetString());
            Assert.Contains("ServiceReady", doc.RootElement.GetProperty("Reason").GetString()!, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void FailureIsSurfacedExplicitlyWithTheLogPath()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        Assert.Contains("FATAL: post-install verification failed", script, StringComparison.Ordinal);
        int fatal = script.IndexOf("FATAL: post-install verification failed", StringComparison.Ordinal);
        string tail = script.Substring(fatal, Math.Min(600, script.Length - fatal));
        Assert.Contains("$script:LogFile", tail, StringComparison.Ordinal);
        Assert.Contains("gcu-install", tail, StringComparison.OrdinalIgnoreCase);
    }

    static PsResult DotSource(string expression)
        => GcuInstallerHarness.RunDotSourcedArgs(InstallScript, "-StagingRoot 'x' -TargetDir 'y'", expression);

    static string Esc(string path) => path.Replace("'", "''");
}
