using System.Text.Json;

namespace MechrevoLite.Tests;

/// <summary>
/// T22: the installer carries a single GCU payload (release\GCU-only) instead of one tree per
/// GPU generation. G0 (real 30/40 hardware proving the 1.2.0.0 payload serves them) is NOT
/// passed, so the single-payload path is implemented behind an explicit -SinglePayload switch /
/// #ifdef SingleGcuPayload define and stays OFF by default; the deletion of the 40-series trees
/// is BLOCKED-HW on G0. These tests pin the ready path AND its fail-loud contract.
/// </summary>
public class InstallerPayloadSingleTests
{
    const string Selector = @"installer\Select-GcuPayload.ps1";
    const string Iss = @"installer\L-Mechrevo.iss";

    [Fact]
    public void SinglePayloadBundle_StagesOnlyGcuOnly()
    {
        PsResult result = GcuInstallerHarness.RunDotSourced(Selector,
            "ConvertTo-Json -InputObject @(Get-GcuPayloadBundle -SinglePayload) -Depth 4");
        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut.Trim());
        JsonElement root = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(1, root.GetArrayLength());
        JsonElement only = root[0];
        Assert.Equal("release\\GCU-only", only.GetProperty("RepoPayload").GetString());
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3080 Laptop GPU", "DEV_249C", "30")]
    [InlineData("NVIDIA GeForce RTX 4090 Laptop GPU", "DEV_2717", "40")]
    [InlineData("NVIDIA GeForce RTX 5080 Laptop GPU", "DEV_2C19", "50")]
    public void SingleMode_SelectsGcuOnlyForEverySupportedGeneration(string gpuName, string deviceId, string expectedGeneration)
    {
        PsResult result = GcuInstallerHarness.RunScript(Selector,
            "-SinglePayload", "-GpuName", gpuName, "-DeviceId", deviceId, "-AsJson");
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
            "-SinglePayload", "-Variant", "Auto", "-GpuName", "NVIDIA GeForce RTX 4070 Laptop GPU", "-AsJson");
        Assert.Equal(0, result.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.StdOut.Trim());
        Assert.Equal("50", doc.RootElement.GetProperty("Variant").GetString());
    }

    [Fact]
    public void Iss_SinglePayloadDefine_StagesOnlyTheGcuOnlyTree()
    {
        string iss = GcuInstallerHarness.Read("installer", "L-Mechrevo.iss");
        const string marker = "#ifdef SingleGcuPayload";
        string? single = null;
        string? multi = null;
        for (int at = iss.IndexOf(marker, StringComparison.Ordinal);
             at >= 0;
             at = iss.IndexOf(marker, at + 1, StringComparison.Ordinal))
        {
            int elseIndex = iss.IndexOf("#else", at, StringComparison.Ordinal);
            int endIf = iss.IndexOf("#endif", elseIndex, StringComparison.Ordinal);
            Assert.True(elseIndex > at && endIf > elseIndex, "SingleGcuPayload must be an #ifdef/#else/#endif block");
            string candidate = iss.Substring(at, elseIndex - at);
            if (candidate.Contains("Source:", StringComparison.Ordinal))
            {
                single = candidate;
                multi = iss.Substring(elseIndex, endIf - elseIndex);
                break;
            }
        }
        Assert.True(single is not null && multi is not null,
            "L-Mechrevo.iss must gate the GCU [Files] payload list on #ifdef SingleGcuPayload");

        int singleSources = single.Split('\n').Count(line => line.TrimStart().StartsWith("Source:", StringComparison.Ordinal));
        Assert.Equal(1, singleSources);
        Assert.Contains("release\\GCU-only", single, StringComparison.Ordinal);
        Assert.DoesNotContain("GCU-40-", single, StringComparison.Ordinal);
        Assert.DoesNotContain("GCU-common", single, StringComparison.Ordinal);

        Assert.Contains("release\\GCU-40-51749", multi, StringComparison.Ordinal);
        Assert.Contains("release\\GCU-40-51751", multi, StringComparison.Ordinal);
        Assert.Contains("release\\GCU-common", multi, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstaller_SinglePayload_WiresDefineAndBundleValidation()
    {
        string build = GcuInstallerHarness.Read("installer", "Build-Installer.ps1");
        Assert.Contains("[switch]$SinglePayload", build, StringComparison.Ordinal);
        // The build validates the same bundle the installer ships.
        Assert.Contains("Assert-GcuPayloadDirs -Root $root -SinglePayload:$SinglePayload", build, StringComparison.Ordinal);
        // -SinglePayload must reach ISCC as the SingleGcuPayload define.
        Assert.Contains("/DSingleGcuPayload=1", build, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertGcuPayloadDirs_MissingGcuOnly_FailsForSinglePayload()
    {
        string temp = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", "payload-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            PsResult result = GcuInstallerHarness.RunDotSourced(Selector,
                "try { Assert-GcuPayloadDirs -Root '" + temp.Replace("'", "''") + "' -SinglePayload | Out-Null; Write-Output 'NO-THROW'; exit 0 } "
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
/// T22 failure surface: single-payload selection must fail loud when the GPU generation is not
/// determinable and must never fall back to a 40-series payload. It must also reject the retired
/// /GCUVARIANT override (user-visible contract change recorded in installer\README.md).
/// </summary>
public class InstallerPayloadNoFallbackFailTests
{
    const string Selector = @"installer\Select-GcuPayload.ps1";

    [Fact]
    public void SingleMode_UnknownGpu_FailsWithoutFallback()
    {
        PsResult result = GcuInstallerHarness.RunScript(Selector,
            "-SinglePayload", "-GpuName", "Intel(R) Graphics", "-DeviceId", "DEV_7D67");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("fallback", result.Combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("40-51751", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleMode_BiosProjectIdAloneIsNotAGenerationJudgement()
    {
        // Axis 1 (platform code) must not decide axis 2 (dGPU generation); a BIOS code alone
        // cannot justify installing a GPU service.
        PsResult result = GcuInstallerHarness.RunScript(Selector, "-SinglePayload", "-BiosProjectId", "PH6TRX1");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("generation", result.Combined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SingleMode_VariantOverrideIsRefused()
    {
        PsResult result = GcuInstallerHarness.RunScript(Selector,
            "-SinglePayload", "-Variant", "40-51749", "-GpuName", "NVIDIA GeForce RTX 4090 Laptop GPU");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Variant", result.Combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("single-payload", result.Combined, StringComparison.OrdinalIgnoreCase);
    }
}
