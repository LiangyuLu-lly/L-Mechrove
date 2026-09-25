namespace MechrevoLite.Tests;

/// <summary>
/// N6 (owner decision A): the installer ships ONLY the newest GCU payload
/// (<c>release\GCU-only</c>, which carries its own UWACPIDriver). The 40-series trees stop being
/// bundled; the source stays on disk untouched as the material for the visible fallback.
///
/// The old G0 gate is superseded: the newest GCU is backward compatible to 30-series, the vendor
/// ships one GCU/console for all 24 platform codes, and <c>release\GCU-only\...\UserFanTables</c>
/// carries all 24 per-model chassis dirs + the 23 flat files while the 40-series payloads carry
/// zero per-model dirs - so the newest payload is the superset.
///
/// The safety net that replaces G0 is a post-install/first-run self-check plus a VISIBLE fallback.
/// </summary>
public class SinglePayloadN6Tests
{
    const string Selector = @"installer\Select-GcuPayload.ps1";
    const string Iss = @"installer\L-Mechrevo.iss";
    const string Build = @"installer\Build-Installer.ps1";

    [Fact]
    public void TheDefaultBundleIsTheSingleNewestPayload()
    {
        // No -SinglePayload switch: the default must already be the single payload.
        PsResult result = GcuInstallerHarness.RunDotSourced(Selector,
            "ConvertTo-Json -InputObject @(Get-GcuPayloadBundle) -Depth 4");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("release\\\\GCU-only", result.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("GCU-40-51749", result.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("GCU-40-51751", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBundleNeverStagesTheRetiredTrees()
    {
        PsResult result = GcuInstallerHarness.RunDotSourced(Selector,
            "ConvertTo-Json -InputObject @(Get-GcuPayloadBundle) -Depth 4");
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("GCU-common", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIssStagesOnlyTheNewestPayload()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        int files = iss.IndexOf("[Files]", StringComparison.Ordinal);
        Assert.True(files >= 0, "L-Mechrevo.iss has no [Files] section");
        string tail = iss.Substring(files);
        int next = tail.IndexOf("\n[", 1, StringComparison.Ordinal);
        string block = next >= 0 ? tail.Substring(0, next) : tail;

        // Only Source: lines matter; comments may name the retired trees when explaining why.
        string[] sources = block.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("Source:", StringComparison.Ordinal))
            .ToArray();

        Assert.Contains(sources, l => l.Contains(@"release\GCU-only\*", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, l => l.Contains("GCU-40-51749", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, l => l.Contains("GCU-40-51751", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, l => l.Contains("GCU-common", StringComparison.Ordinal));
    }

    [Fact]
    public void TheIssNoLongerGatesThePayloadOnTheG0Define()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        Assert.DoesNotContain("SingleGcuPayload", iss, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSelectorNoLongerConsultsThePlatformCodeHeuristic()
    {
        string selector = GcuInstallerHarness.Read("installer", "Select-GcuPayload.ps1");
        // Axis 1 (platform code) must not decide axis 2 (dGPU generation).
        Assert.DoesNotContain("PH6", selector, StringComparison.Ordinal);
        Assert.DoesNotContain("PH4", selector, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSelectorResolvesEverySupportedGenerationToTheNewestPayload()
    {
        foreach (string gpu in new[]
                 {
                     "NVIDIA GeForce RTX 5080 Laptop GPU",
                     "NVIDIA GeForce RTX 4090 Laptop GPU",
                     "NVIDIA GeForce RTX 3080 Laptop GPU",
                 })
        {
            PsResult result = GcuInstallerHarness.RunScript(Selector, "-GpuName", gpu, "-AsJson");
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("release\\\\GCU-only", result.StdOut, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnUndeterminableGenerationStillInstallsTheShippedPayload()
    {
        PsResult result = GcuInstallerHarness.RunScript(Selector,
            "-GpuName", "Intel(R) Graphics", "-DeviceId", "PCI\\VEN_8086&DEV_7D67", "-AsJson");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("release\\\\GCU-only", result.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("GCU-40", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Iss_GcuStatusDoesNotMentionGenerationSelection()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        Assert.Contains("正在安装 GCU...", iss, StringComparison.Ordinal);
        Assert.Contains("Installing GCU...", iss, StringComparison.Ordinal);
        Assert.DoesNotContain("按显卡代际自动选择", iss, StringComparison.Ordinal);
        Assert.DoesNotContain("payload selected by GPU generation", iss, StringComparison.Ordinal);
        Assert.Contains("function NeedRestart", iss, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRetiredVariantOverrideIsRefused()
    {
        // Given Variant=40-51749 and RTX 4090
        // When the selector runs
        // Then exit 0, Combined contains Warning/retired, StdOut is still GCU-only, no 40-51749 payload path
        PsResult result = GcuInstallerHarness.RunScript(Selector,
            "-Variant", "40-51749", "-GpuName", "NVIDIA GeForce RTX 4090 Laptop GPU");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("retired", result.Combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GCU-only", result.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("GCU-40-51749", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuildHardFailsWhenTheNewestPayloadIsMissing()
    {
        string build = GcuInstallerHarness.Read("installer", "Build-Installer.ps1");
        Assert.Contains("Assert-GcuPayloadDirs", build, StringComparison.Ordinal);
        Assert.Contains("release\\GCU-only", build, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuildNoLongerWiresTheG0Define()
    {
        string build = GcuInstallerHarness.Read("installer", "Build-Installer.ps1");
        Assert.DoesNotContain("SingleGcuPayload", build, StringComparison.Ordinal);
        Assert.DoesNotContain("SinglePayload", build, StringComparison.Ordinal);
    }
}
