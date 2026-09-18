namespace MechrevoLite.Tests;

/// <summary>
/// N6: the post-install/first-run self-check plus the VISIBLE fallback is the safety net that
/// replaces the retired G0 gate. It must be automatic, testable with fakes (no hardware), and its
/// failure message must be user-visible and actionable.
///
/// Honest limitation, asserted here so it cannot silently become a lie: this repo has NO download
/// server, so the fallback is "tell the user exactly what to fetch and where" - never a one-click
/// cloud download.
/// </summary>
public class GcuSelfCheckN6Tests
{
    const string InstallScript = @"installer\Install-Gcu.ps1";

    static PsResult SelfCheck(string serviceNames, string listenerPids, string itemSupportPresent, string serviceReady)
        => GcuInstallerHarness.RunDotSourcedArgs(InstallScript,
            "-StagingRoot 'x' -TargetDir 'y'",
            "Test-GcuPostInstall -ServiceNames @(" + serviceNames + ") -ListenerPids @(" + listenerPids + ") "
            + "-ItemSupportPresent $" + itemSupportPresent + " -ServiceReady " + serviceReady
            + " | ConvertTo-Json -Depth 5");

    [Fact]
    public void AHealthyInstallPassesEveryCheck()
    {
        PsResult result = SelfCheck("'GCUBridge'", "4242", "true", "1");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"Ok\":  true", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingServiceFailsTheSelfCheck()
    {
        PsResult result = SelfCheck("", "", "true", "1");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"Ok\":  false", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("single-service", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void AServiceThatNeverBecameReadyFailsTheSelfCheck()
    {
        PsResult result = SelfCheck("'GCUBridge'", "4242", "true", "0");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("service-ready", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("\"Ok\":  false", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyItemSupportFailsTheSelfCheck()
    {
        PsResult result = SelfCheck("'GCUBridge'", "4242", "false", "1");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("item-support", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("\"Ok\":  false", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFallbackNamesTheExactPayloadToFetchAndWhereItLives()
    {
        PsResult result = GcuInstallerHarness.RunDotSourcedArgs(InstallScript,
            "-StagingRoot 'x' -TargetDir 'y'",
            "Get-GcuFallbackGuidance | ConvertTo-Json -Depth 5");
        Assert.Equal(0, result.ExitCode);
        // The user must be told exactly which tree to fetch and where it lives in the repo.
        Assert.Contains("GCU-40-51751", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("GCU-40-51749", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("release", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFallbackDoesNotPretendADownloadServerExists()
    {
        PsResult result = GcuInstallerHarness.RunDotSourcedArgs(InstallScript,
            "-StagingRoot 'x' -TargetDir 'y'",
            "Get-GcuFallbackGuidance | ConvertTo-Json -Depth 5");
        Assert.Equal(0, result.ExitCode);
        // Honest limitation: no cloud download. The guidance must say so.
        Assert.Contains("no download server", result.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSelfCheckFailureIsSurfacedNotSwallowed()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        // A failed self-check must write a visible status and exit non-zero.
        Assert.Contains("Write-GcuInstallStatus", script, StringComparison.Ordinal);
        Assert.Contains("FATAL: post-install verification failed", script, StringComparison.Ordinal);
        Assert.Contains("Get-GcuFallbackGuidance", script, StringComparison.Ordinal);
    }
}
