using System.Text.Json;

namespace MechrevoLite.Tests;

/// <summary>
/// T25: running the installer twice must not produce side effects. The installed payload identity
/// (SHA256) is recorded in the install marker; when the bundled payload is already installed and
/// the service is healthy, the install degrades to verify-only (signatures + firewall + marker)
/// and never re-copies or re-registers.
/// </summary>
public class InstallerIdempotencyTests
{
    const string InstallScript = @"installer\Install-Gcu.ps1";

    [Fact]
    public void SamePayloadIdentity_AlreadyCurrent_IsVerifyOnly()
    {
        PsResult result = DotSource(
            "Get-InstallAction -AlreadyCurrent $true -InstalledSha256 'abc' -IncomingSha256 'abc'");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("VerifyOnly", result.StdOut.Trim());
    }

    [Fact]
    public void DifferentPayloadIdentity_AlreadyCurrent_IsInstall()
    {
        PsResult result = DotSource(
            "Get-InstallAction -AlreadyCurrent $true -InstalledSha256 'abc' -IncomingSha256 'def'");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Install", result.StdOut.Trim());
    }

    [Fact]
    public void NotAlreadyCurrent_IsInstall()
    {
        PsResult result = DotSource(
            "Get-InstallAction -AlreadyCurrent $false -InstalledSha256 'abc' -IncomingSha256 'abc'");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Install", result.StdOut.Trim());
    }

    [Fact]
    public void InstallGcu_ChecksMarkerAndDowngradeBeforeAnySystemChange()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        int marker = script.IndexOf("Get-InstalledMarker", StringComparison.Ordinal);
        int downgrade = script.IndexOf("Test-InstallerDowngrade", StringComparison.Ordinal);
        int uninstall = script.IndexOf("Invoke-PriorGenerationUninstall -TargetDir $TargetDir", StringComparison.Ordinal);
        Assert.True(marker >= 0, "Install-Gcu.ps1 must read the install marker");
        Assert.True(downgrade >= 0, "Install-Gcu.ps1 must evaluate the downgrade rule");
        Assert.True(uninstall >= 0, "Install-Gcu.ps1 must run the uninstall-first step");
        Assert.True(marker < uninstall, "the marker must be read before anything is changed");
        Assert.True(downgrade < uninstall, "the downgrade decision must precede any system change");
    }

    [Fact]
    public void Marker_RecordsInstallerVersionForDowngradeOrdering()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        Assert.Contains("GcuInstallerVersion", script, StringComparison.Ordinal);
        Assert.Contains("$InstallerVersion", script, StringComparison.Ordinal);
    }

    static PsResult DotSource(string expression)
        => GcuInstallerHarness.RunDotSourcedArgs(InstallScript, "-StagingRoot 'x' -TargetDir 'y'", expression);
}

/// <summary>
/// T25 failure surface: installing an older payload on top of a newer install is refused with a
/// clear reason, and the refusal happens before any service/driver/firewall change. Ordering uses
/// the installer/app version line (shared and monotonic), never the GCU service FileVersion.
/// </summary>
public class InstallerDowngradeFailTests
{
    const string InstallScript = @"installer\Install-Gcu.ps1";

    [Theory]
    [InlineData("", "0.289.0.0", "NoInstalled")]
    [InlineData("0.289.0.0", "", "UnknownIncoming")]
    [InlineData("0.300.0.0", "0.289.0.0", "Downgrade")]
    [InlineData("0.289.0.0", "0.289.0.0", "NotOlder")]
    [InlineData("0.288.0.0", "0.289.0.0", "NotOlder")]
    [InlineData("not-a-version", "0.289.0.0", "UnknownInstalled")]
    [InlineData("0.289.0.0", "not-a-version", "UnknownIncoming")]
    public void TestInstallerDowngrade_ClassifiesVersions(string installed, string incoming, string expected)
    {
        PsResult result = DotSource(
            "Test-InstallerDowngrade -InstalledVersion '" + installed + "' -IncomingVersion '" + incoming + "'");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut.Trim());
    }

    [Fact]
    public void RefusalPath_RunsBeforeCopyAndServiceRegistration()
    {
        string script = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        int refuse = script.IndexOf("refusing to downgrade", StringComparison.Ordinal);
        int copy = script.IndexOf("Copy-Tree -Source $serviceSource -Destination $serviceTarget", StringComparison.Ordinal);
        int service = script.IndexOf("Install-Service -ServiceExe $serviceExe", StringComparison.Ordinal);
        Assert.True(refuse >= 0, "Install-Gcu.ps1 must refuse downgrades with a clear reason");
        Assert.True(refuse < copy, "the downgrade refusal must precede the payload copy");
        Assert.True(refuse < service, "the downgrade refusal must precede service registration");
    }

    [Fact]
    public void Iss_PassesTheInstallerVersionToTheGcuStep()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        Assert.Contains("-InstallerVersion", iss, StringComparison.Ordinal);
        Assert.Contains("{#AppVersionNumeric}", iss, StringComparison.Ordinal);
    }

    static PsResult DotSource(string expression)
        => GcuInstallerHarness.RunDotSourcedArgs(InstallScript, "-StagingRoot 'x' -TargetDir 'y'", expression);
}
