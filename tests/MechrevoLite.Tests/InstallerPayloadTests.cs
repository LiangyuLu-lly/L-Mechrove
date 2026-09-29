using System.Text.Json;

namespace MechrevoLite.Tests;

/// <summary>
/// T22 / N6: the installer carries a single GCU payload (release\GCU-only) instead of one tree per
/// GPU generation. The old G0 gate is superseded (owner): the newest GCU is backward compatible to
/// 30-series, the vendor ships one GCU/console for all 24 platform codes, and
/// release\GCU-only\...\UserFanTables carries all 24 per-model chassis dirs + the 23 flat files
/// while the 40-series payloads carry zero per-model dirs - so the newest payload is the superset.
/// Single payload is now the ONLY mode; there is no switch and no define. These tests pin the
/// resolver AND its fail-loud contract.
/// </summary>
public class InstallerPayloadSingleTests
{
    const string Selector = @"installer\Select-GcuPayload.ps1";
    const string Iss = @"installer\L-Mechrevo.iss";

    /// <summary>
    /// beta21 (owner decision): GTX 10/16 + RTX 20 keep the GamingCenterU legacy service, so the
    /// bundle is the newest payload plus that one legacy tree - never the retired 40-series trees.
    /// </summary>
    [Fact]
    public void PayloadBundle_StagesTheNewestPayloadAndTheLegacy1020Service()
    {
        PsResult result = GcuInstallerHarness.RunDotSourced(Selector,
            "ConvertTo-Json -InputObject @(Get-GcuPayloadBundle) -Depth 4");
        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut.Trim());
        JsonElement root = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(2, root.GetArrayLength());
        Assert.Equal("release\\GCU-only", root[0].GetProperty("RepoPayload").GetString());
        Assert.Equal("release\\GCU-1020", root[1].GetProperty("RepoPayload").GetString());
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 2060", "PCI\\VEN_10DE&DEV_1F15")]
    [InlineData("NVIDIA GeForce GTX 1660 Ti", "PCI\\VEN_10DE&DEV_2191")]
    [InlineData("NVIDIA GeForce GTX 1060", "PCI\\VEN_10DE&DEV_1C20")]
    public void Legacy1020Generations_SelectTheGamingCenterUServiceWithItsOwnDriver(string gpuName, string deviceId)
    {
        PsResult result = GcuInstallerHarness.RunScript(Selector,
            "-GpuName", gpuName, "-DeviceId", deviceId, "-AsJson");
        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut.Trim());
        JsonElement r = doc.RootElement;
        Assert.Equal("1020", r.GetProperty("Variant").GetString());
        Assert.Equal("release\\GCU-1020", r.GetProperty("RepoPayload").GetString());
        Assert.Equal("payload\\1020", r.GetProperty("StagedDir").GetString());
        Assert.Equal("UniwillService", r.GetProperty("ServiceDir").GetString());
        Assert.Equal("ACPIDriver", r.GetProperty("DriverDir").GetString());
        Assert.Equal("ACPIDriver.inf", r.GetProperty("DriverInf").GetString());
        Assert.True(r.GetProperty("Legacy").GetBoolean());
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3080 Laptop GPU", "DEV_249C", "30")]
    [InlineData("NVIDIA GeForce RTX 4090 Laptop GPU", "DEV_2717", "40")]
    [InlineData("NVIDIA GeForce RTX 5080 Laptop GPU", "DEV_2C19", "50")]
    public void SingleMode_SelectsGcuOnlyForEverySupportedGeneration(string gpuName, string deviceId, string expectedGeneration)
    {
        PsResult result = GcuInstallerHarness.RunScript(Selector,
            "-GpuName", gpuName, "-DeviceId", deviceId, "-AsJson");
        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut.Trim());
        Assert.Equal("50", doc.RootElement.GetProperty("Variant").GetString());
        Assert.Equal("release\\GCU-only", doc.RootElement.GetProperty("RepoPayload").GetString());
        Assert.Equal("payload\\50", doc.RootElement.GetProperty("StagedDir").GetString());
        Assert.Equal("AiStoneService", doc.RootElement.GetProperty("ServiceDir").GetString());
        Assert.Equal(expectedGeneration, doc.RootElement.GetProperty("Generation").GetString());
    }

    [Fact]
    public void SingleMode_AutoVariantIsAccepted()
    {
        PsResult result = GcuInstallerHarness.RunScript(Selector,
            "-Variant", "Auto", "-GpuName", "NVIDIA GeForce RTX 4070 Laptop GPU", "-AsJson");
        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut.Trim());
        Assert.Equal("50", doc.RootElement.GetProperty("Variant").GetString());
    }

    [Fact]
    public void Iss_StagesTheNewestAndTheLegacy1020TreesOnly()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        int files = iss.IndexOf("[Files]", StringComparison.Ordinal);
        Assert.True(files >= 0, "L-Mechrevo.iss has no [Files] section");
        string tail = iss.Substring(files);
        int next = tail.IndexOf("\n[", 1, StringComparison.Ordinal);
        string block = next >= 0 ? tail.Substring(0, next) : tail;

        string[] sources = block.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("Source:", StringComparison.Ordinal))
            .ToArray();

        Assert.Contains(sources, l => l.Contains("release\\GCU-only", StringComparison.Ordinal) && l.Contains("payload\\50", StringComparison.Ordinal));
        Assert.Contains(sources, l => l.Contains("release\\GCU-1020", StringComparison.Ordinal) && l.Contains("payload\\1020", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, l => l.Contains("GCU-40-", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, l => l.Contains("GCU-common", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildInstaller_ValidatesTheBundleItShips()
    {
        string build = GcuInstallerHarness.Read("installer", "Build-Installer.ps1");
        // The build validates the same bundle the installer ships, and hard-fails when it is missing.
        Assert.Contains("Assert-GcuPayloadDirs -Root $root", build, StringComparison.Ordinal);
        Assert.Contains("release\\GCU-only", build, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertGcuPayloadDirs_MissingGcuOnly_FailsTheBuild()
    {
        string temp = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", "payload-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            PsResult result = GcuInstallerHarness.RunDotSourced(Selector,
                "try { Assert-GcuPayloadDirs -Root '" + temp.Replace("'", "''") + "' | Out-Null; Write-Output 'NO-THROW'; exit 0 } "
                + "catch { Write-Output ('CAUGHT: ' + $_.Exception.Message); exit 3 }");
            Assert.Equal(3, result.ExitCode);
            Assert.Contains("CAUGHT:", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("GCU-only", result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }
}

/// <summary>
/// T22: there is one shipped payload. Unknown GPU / BIOS-only still installs it.
/// Never fall back to a 40-series tree. A retired /GCUVARIANT override must warn and
/// continue as Auto (user-visible contract change recorded in installer\README.md).
/// </summary>
public class InstallerPayloadNoFallbackFailTests
{
    const string Selector = @"installer\Select-GcuPayload.ps1";

    [Fact]
    public void SingleMode_UnknownGpu_StillInstallsTheShippedPayload()
    {
        PsResult result = GcuInstallerHarness.RunScript(Selector,
            "-GpuName", "Intel(R) Graphics", "-DeviceId", "DEV_7D67", "-AsJson");
        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut.Trim());
        Assert.Equal("50", doc.RootElement.GetProperty("Variant").GetString());
        Assert.Equal("release\\GCU-only", doc.RootElement.GetProperty("RepoPayload").GetString());
        Assert.DoesNotContain("40-51751", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleMode_BiosProjectIdAloneStillInstallsTheShippedPayload()
    {
        PsResult result = GcuInstallerHarness.RunScript(Selector, "-BiosProjectId", "PH6TRX1", "-AsJson");
        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut.Trim());
        Assert.Equal("50", doc.RootElement.GetProperty("Variant").GetString());
        Assert.Equal("release\\GCU-only", doc.RootElement.GetProperty("RepoPayload").GetString());
    }

    [Fact]
    public void SingleMode_VariantOverrideIsRefused()
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
}
