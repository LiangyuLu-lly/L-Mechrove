namespace MechrevoLite.Tests;

/// <summary>
/// Field bug: the beta18 installer does not detect an already-installed .NET 10 runtime, so users
/// who DO have it are blocked. Two independent defects were found in the [Code] detection:
///
/// 1. <c>RegGetSubkeyNames</c> reads SUBKEY names, but the .NET installer records installed
///    versions as VALUE names (e.g. a value named <c>10.0.8</c>). The key has no subkeys at all,
///    so the loop never ran and detection always returned False.
/// 2. The key was read from <c>HKLM64</c>. On this machine the 64-bit view of
///    <c>...\x64\sharedfx\Microsoft.WindowsDesktop.App</c> is EMPTY; the populated key lives in the
///    32-bit view. Reading only HKLM64 therefore also returns False.
///
/// The detection is Inno Pascal, which cannot be unit-tested directly, so the decision is mirrored
/// in a PowerShell predicate (<c>installer\Test-DotNetDesktopRuntime.ps1</c>) that the installer
/// smoke check and these tests both exercise. The predicate takes an explicit registry-view switch
/// so the false case can be proven without uninstalling anything.
/// </summary>
public class RuntimeDetectionTests
{
    const string Predicate = @"installer\Test-DotNetDesktopRuntime.ps1";

    [Fact]
    public void DetectsTheInstalledDesktopRuntime10OnThisMachine()
    {
        PsResult result = GcuInstallerHarness.RunScript(Predicate);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("DETECTED", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("Microsoft.WindowsDesktop.App", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsTheDetectedVersionSoTheUserCanSeeWhatWasFound()
    {
        PsResult result = GcuInstallerHarness.RunScript(Predicate);
        Assert.Equal(0, result.ExitCode);
        // A concrete 10.x version must be named, not just a boolean.
        Assert.Matches(@"10\.\d+", result.StdOut);
    }

    [Fact]
    public void MissingRuntimeIsReportedAsMissing()
    {
        // FALSE case, proven without touching the machine: point the predicate at a key that
        // cannot exist, with the directory fallback disabled so the registry decision is isolated.
        PsResult result = GcuInstallerHarness.RunScript(Predicate,
            "-KeyPath", @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\NoSuchFrameworkForTests",
            "-NoDirectoryFallback");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("MISSING", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyKeyIsMissingNotDetected()
    {
        // A key that exists but carries no 10.x value must not be treated as installed.
        PsResult result = GcuInstallerHarness.RunScript(Predicate,
            "-KeyPath", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            "-NoDirectoryFallback");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("MISSING", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDirectoryFallbackAloneCanDetectTheRuntime()
    {
        // The fallback is a real acceptance path, not decoration: with the registry pointed at a
        // nonexistent key, the on-disk shared framework must still produce DETECTED.
        PsResult result = GcuInstallerHarness.RunScript(Predicate,
            "-KeyPath", @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\NoSuchFrameworkForTests");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("DETECTED", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("shared framework directory", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstallerReadsValueNamesNotSubkeyNames()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        Assert.Contains("RegGetValueNames", iss, StringComparison.Ordinal);
        Assert.DoesNotContain("RegGetSubkeyNames", iss, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstallerReadsBothRegistryViews()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        // The populated key is in the 32-bit view on this machine and in the 64-bit view on
        // others, so both must be consulted.
        Assert.Contains("HKLM32", iss, StringComparison.Ordinal);
        Assert.Contains("HKLM64", iss, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstallerNamesTheDesktopRuntimeAndItsDownloadPage()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        Assert.Contains("Microsoft.WindowsDesktop.App", iss, StringComparison.Ordinal);
        Assert.Contains("dotnet.microsoft.com/download/dotnet/10.0", iss, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstallerKeepsADirectoryFallback()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        // Independent corroboration: the shared framework on disk.
        Assert.Contains(@"dotnet\shared\Microsoft.WindowsDesktop.App", iss, StringComparison.Ordinal);
    }
}
