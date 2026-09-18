using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T9（Wave B）happy 路径：22 处硬编码机型假设逐条有处置，且**验收断言矩阵/能力查表结果**
/// （机型+输入 → 期望输出），不以"旧字符串是否还在源码里"为通过条件。
/// 失败断言见 <see cref="HardcodeMatrixLookupFailTests"/>。
/// </summary>
public class HardcodeMatrixLookupTests
{
    static FeatureMatrix Matrix(params (string Key, object? Value)[] pairs)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, object? value) in pairs) values[key] = value;
        return FeatureMatrix.FromValues(values);
    }

    [Fact]
    public void TheDispositionTableCoversAllTwentyTwoHardcodes()
    {
        HardcodeTableValidator.Validate(HardcodeDispositions.Items);

        Assert.Equal(22, HardcodeDispositions.Items.Count);
        Assert.Equal(Enumerable.Range(1, 22).Select(index => "C" + index),
            HardcodeDispositions.Items.Select(entry => entry.Item));
    }

    [Fact]
    public void C1ChargeLimitIsDecidedByTheMatrixAndF3()
    {
        using var force = ChargeLimitGatingTests.Force(null);

        Assert.True(EcChargeLimit.IsSupportedMachine(new SupportDecision(true, SupportReason.Ok, "PH4TRX1"),
            Matrix(("KeyboardSupport", 1))));
        Assert.False(EcChargeLimit.IsSupportedMachine(SupportDecision.NotInSet("PH6AGxx"),
            Matrix(("KeyboardSupport", 1))));
    }

    [Fact]
    public void C6TheHotSwapLookupIgnoresTheVersionProxy()
    {
        FeatureMatrix via23 = Matrix(
            ("APVersionCheck", 23), ("GpuHotSwapSwitchSupport", 1), ("lgpuHotSwapSwitchStatus", 1));
        FeatureMatrix via24 = Matrix(
            ("APVersionCheck", 24), ("GpuHotSwapSwitchSupport", 1), ("lgpuHotSwapSwitchStatus", 1));

        Assert.True(via23.GpuHotSwapAvailable);
        Assert.True(via24.GpuHotSwapAvailable);

        // 与能力画像层同源：APVersionCheck 23/24 都不得改变结论。
        Assert.True(MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>
        {
            ["APVersionCheck"] = 23,
            ["GpuHotSwapSwitchSupport"] = 1,
            ["lgpuHotSwapSwitchStatus"] = 1,
        }).GpuHotSwap);
        Assert.True(MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>
        {
            ["APVersionCheck"] = 24,
            ["GpuHotSwapSwitchSupport"] = 1,
            ["lgpuHotSwapSwitchStatus"] = 1,
        }).GpuHotSwap);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(125)]
    [InlineData(140)]
    [InlineData(175)]
    public void C14ToC16TheGpuPowerComesFromTheRuntimeReport(int reported)
    {
        Assert.Equal(reported, NvidiaSmi.GetDefaultMaxGPUPower(reported));
        Assert.Equal(-1, NvidiaSmi.GetDefaultMaxGPUPower());
    }

    [Fact]
    public void C20BuiltInCurvesFollowTheFanSettingsProfileBit()
    {
        Assert.False(MechrevoHw.ShouldApplyBuiltInCurveDefaults(
            new MechrevoDeviceCapabilities { ProfileAvailable = true, FanSettings = false }));
        Assert.True(MechrevoHw.ShouldApplyBuiltInCurveDefaults(
            new MechrevoDeviceCapabilities { ProfileAvailable = true, FanSettings = true }));
    }

    [Fact]
    public void TheF3LookupDecidesModelSupport()
    {
        IReadOnlySet<string> codes = ModelRegistryData.Load().PlatformCodeSet;

        Assert.True(ModelSupport.Determine(
            new ModelIdentity("PH4TRX1", 18, "IDY", ModelSource.Ec), codes).IsSupported);
        Assert.Equal(SupportReason.NotInSet, ModelSupport.Determine(
            new ModelIdentity("PH6AGxx", 5894, "IDY", ModelSource.Ec), codes).Reason);
    }

    [Fact]
    public void TheBlockedHotSwapPreconditionCarriesProbeRawAndHash()
    {
        HotSwapEvidence evidence = HotSwapEvidence.Load();

        Assert.Equal("BLOCKED-HW", evidence.Status);
        Assert.False(string.IsNullOrWhiteSpace(evidence.Probe));
        Assert.False(string.IsNullOrWhiteSpace(evidence.Raw));
        Assert.Equal(64, evidence.Hash.Length);
        Assert.Equal(HotSwapEvidence.Sha256(evidence.Raw), evidence.Hash);
        Assert.Contains("GpuHotSwapSwitchSupport", evidence.Replacement);
        Assert.Contains("lgpuHotSwapSwitchStatus", evidence.Replacement);
    }
}

internal static class HardcodeTableValidator
{
    static readonly string[] ExpectedItems = Enumerable.Range(1, 22).Select(index => "C" + index).ToArray();

    internal static void Validate(IReadOnlyList<HardcodeEntry> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (HardcodeEntry entry in items)
        {
            if (!ExpectedItems.Contains(entry.Item))
                throw new InvalidDataException($"hardcode table: unknown item '{entry.Item}'");
            if (!seen.Add(entry.Item))
                throw new InvalidDataException($"hardcode table: duplicate item '{entry.Item}'");
            if (!Enum.IsDefined(entry.Disposition))
                throw new InvalidDataException($"hardcode table: undefined disposition for {entry.Item}");
            if (string.IsNullOrWhiteSpace(entry.Note))
                throw new InvalidDataException($"hardcode table: {entry.Item} has no disposition note");
            if (entry.Lines.Count == 0)
                throw new InvalidDataException($"hardcode table: {entry.Item} has no line numbers");
        }

        foreach (string expected in ExpectedItems)
            if (!seen.Contains(expected))
                throw new InvalidDataException($"hardcode table: missing item '{expected}'");
    }
}

internal sealed record HotSwapEvidence(string Status, string Probe, string Raw, string Hash, string Replacement)
{
    internal static HotSwapEvidence Load() => Parse(File.ReadAllText(Path.Combine(
        T8Evidence.RepoRoot(), ".omo", "evidence", "t9-hotswap-values.json")));

    internal static HotSwapEvidence Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        var evidence = new HotSwapEvidence(
            root.GetProperty("status").GetString() ?? "",
            root.GetProperty("probe").GetString() ?? "",
            root.GetProperty("raw").GetString() ?? "",
            root.GetProperty("hash").GetString() ?? "",
            root.GetProperty("replacement").GetString() ?? "");
        if (string.IsNullOrWhiteSpace(evidence.Probe)) throw new InvalidDataException("t9 evidence: probe missing");
        if (string.IsNullOrWhiteSpace(evidence.Raw)) throw new InvalidDataException("t9 evidence: raw missing");
        if (evidence.Hash.Length != 64) throw new InvalidDataException("t9 evidence: hash missing or not SHA256");
        return evidence;
    }

    internal static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
