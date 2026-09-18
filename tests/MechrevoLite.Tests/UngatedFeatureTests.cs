using System.Text.Json;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T8（Wave B）happy 路径：E1-E8 的取证结论与矩阵门控。取证只接受枚举值
/// （<c>outcome</c> ∈ {CONFIRMED,REFUTED}、<c>action</c> ∈ {MATRIX_GATED,KEPT_WITH_REASON}），
/// 由本类解析校验。失败断言见 <see cref="UngatedFeatureFailTests"/>。
/// </summary>
public class UngatedFeatureTests
{
    [Fact]
    public void TheForensicsFileCoversEveryUngatedFeature()
    {
        IReadOnlyList<T8Evidence.Entry> items = T8Evidence.Load();

        Assert.Equal(new[] { "E1", "E2", "E3", "E4", "E5", "E6", "E7", "E8" },
            items.Select(item => item.Item).ToArray());
        Assert.All(items, item => Assert.NotEmpty(item.Lines));
    }

    [Fact]
    public void E1ForcedGpuModeNoLongerMatchesAModelString()
    {
        string? previous = AppConfig.GetString("gpu_mode_force_set");
        try
        {
            AppConfig.Remove("gpu_mode_force_set");
            Assert.False(AppConfig.IsForceSetGPUMode());

            string source = T8Evidence.SourceFile("AppConfig.cs");
            Assert.DoesNotContain("503", source);
        }
        finally
        {
            if (previous is null) AppConfig.Remove("gpu_mode_force_set");
            else AppConfig.Set("gpu_mode_force_set", previous);
        }
    }

    [Fact]
    public void E4TheDefaultGpuPowerIsUnknownWithoutARuntimeReport()
    {
        Assert.Equal(-1, NvidiaSmi.GetDefaultMaxGPUPower());
        Assert.Equal(140, NvidiaSmi.GetDefaultMaxGPUPower(140));

        string source = T8Evidence.SourceFile("Gpu", "NVidia", "NvidiaSmi.cs");
        Assert.DoesNotContain("GU605", source);
        Assert.DoesNotContain("return 175", source);
    }

    [Fact]
    public void E6BuiltInCurvesAreGatedByTheFanSettingsProfileBit()
    {
        Assert.False(MechrevoHw.ShouldApplyBuiltInCurveDefaults(
            new MechrevoDeviceCapabilities { ProfileAvailable = true, FanSettings = false }));
        Assert.True(MechrevoHw.ShouldApplyBuiltInCurveDefaults(
            new MechrevoDeviceCapabilities { ProfileAvailable = true, FanSettings = true }));
        // 画像缺失（测试/审计/首次启动）保留内置回退。
        Assert.True(MechrevoHw.ShouldApplyBuiltInCurveDefaults(new MechrevoDeviceCapabilities()));
    }

    [Fact]
    public void E8TheAlwaysRestartPolicyIsRecordedAsVerifiedIntentional()
    {
        T8Evidence.Entry item = T8Evidence.Load().Single(entry => entry.Item == "E8");

        Assert.Equal("CONFIRMED", item.Outcome);
        Assert.Equal("KEPT_WITH_REASON", item.Action);
        // 取证事实：显式 supportsHotSwap=true 时策略层仍返回 Restart。
        Assert.Equal("Restart", GpuSwitchPolicy.Resolve(
            MechrevoService.GpuStandard, automaticRuntime: 1, MechrevoService.GpuIGpu,
            supportsHotSwap: true).Route.ToString());
    }

    [Fact]
    public void E2AndE3AreRecordedForTheCleanupTask()
    {
        IReadOnlyList<T8Evidence.Entry> items = T8Evidence.Load();

        Assert.Equal("KEPT_WITH_REASON", items.Single(entry => entry.Item == "E2").Action);
        Assert.Equal("KEPT_WITH_REASON", items.Single(entry => entry.Item == "E3").Action);
    }
}

/// <summary>T8 取证文件 <c>.omo/evidence/t8-evidence.json</c> 的解析与校验。</summary>
internal static class T8Evidence
{
    internal sealed record Entry(string Item, string Outcome, string Action, IReadOnlyList<int> Lines);

    internal static readonly string[] ExpectedItems = { "E1", "E2", "E3", "E4", "E5", "E6", "E7", "E8" };
    internal static readonly string[] Outcomes = { "CONFIRMED", "REFUTED" };
    internal static readonly string[] Actions = { "MATRIX_GATED", "KEPT_WITH_REASON" };

    internal static IReadOnlyList<Entry> Load() => Parse(File.ReadAllText(EvidencePath()));

    internal static IReadOnlyList<Entry> Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("items", out JsonElement array) ||
            array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("t8 evidence: 'items' array is missing");

        var result = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement entry in array.EnumerateArray())
        {
            string item = entry.GetProperty("item").GetString() ?? "";
            string outcome = entry.GetProperty("outcome").GetString() ?? "";
            string action = entry.GetProperty("action").GetString() ?? "";
            if (!ExpectedItems.Contains(item))
                throw new InvalidDataException($"t8 evidence: unknown item '{item}'");
            if (!Outcomes.Contains(outcome))
                throw new InvalidDataException($"t8 evidence: out-of-range outcome '{outcome}' for {item}");
            if (!Actions.Contains(action))
                throw new InvalidDataException($"t8 evidence: out-of-range action '{action}' for {item}");
            if (!seen.Add(item))
                throw new InvalidDataException($"t8 evidence: duplicate item '{item}'");

            var lines = new List<int>();
            foreach (JsonElement line in entry.GetProperty("lines").EnumerateArray())
                lines.Add(line.GetInt32());
            if (lines.Count == 0)
                throw new InvalidDataException($"t8 evidence: {item} has no line numbers");
            result.Add(new Entry(item, outcome, action, lines));
        }

        foreach (string expected in ExpectedItems)
            if (!seen.Contains(expected))
                throw new InvalidDataException($"t8 evidence: missing item '{expected}'");
        return result;
    }

    internal static string SourceFile(params string[] tail) => File.ReadAllText(
        Path.Combine(RepoRoot(), "src", "MechrevoLiteWin", Path.Combine(tail)));

    internal static string EvidencePath() => Path.Combine(RepoRoot(), ".omo", "evidence", "t8-evidence.json");

    internal static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("repo root not found");
        return directory.FullName;
    }
}
