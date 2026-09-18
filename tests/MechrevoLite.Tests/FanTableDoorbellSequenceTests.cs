using System.Text.Json;
using MechrevoLite.Fan;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T15（Wave C）happy 路径：门铃时序**只读校验**——规格资产逐行对照厂商常量（模式值、0xFD/0xC9、
/// 500 ms x 10、读回 duty=Data/2、写回 &gt;100 -&gt; 0xFF 否则 duty*2），服务所需资产齐备，
/// 且我们解析出的目录/规格类与服务会选中的一致。**不实现门铃写序列，不写 EC。**
/// 失败/边界断言见 <see cref="FanTableNoEcWriteFailTests"/>。
/// </summary>
public class FanTableDoorbellSequenceTests
{
    static readonly string SpecPath = FanTableAssetHarness.RepoPath(
        Path.Combine("src", "MechrevoLiteWin", "Resources", "fan-table-doorbell.spec.json"));

    static readonly string AppRoot = FanTableAssetHarness.RepoPath(FanTableAssetHarness.AppTreeRelative);
    static readonly ModelRegistryData Registry = ModelRegistryData.Load();

    [Fact]
    public void TheSpecAssetIsEveryRowSourcedAndTheExpectedLength()
    {
        using JsonDocument spec = LoadSpec();
        JsonElement operations = spec.RootElement.GetProperty("operations");

        Assert.Equal(7, operations.GetArrayLength());
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            string source = operation.GetProperty("source").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(source), "every doorbell row must carry a non-empty source");
            Assert.Contains("FanTable_Manager1p5", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheDoorbellOperationSequenceMatchesTheVendorConstants()
    {
        using JsonDocument spec = LoadSpec();
        (int Step, string Operation, int? Address, string? Value)[] expected =
        {
            (1, "Set_APExistToEC", null, "false"),
            (2, "Write", 3935, "mode"),
            (3, "Write", 3933, "253"),
            (4, "Write", 3934, "201"),
            (5, "PollUntilReady", null, null),
            (6, "ReadTable", null, null),
            (7, "Set_APExistToEC", null, "true"),
        };

        JsonElement[] actual = spec.RootElement.GetProperty("operations").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, actual.Length);

        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Step, actual[index].GetProperty("step").GetInt32());
            Assert.Equal(expected[index].Operation, actual[index].GetProperty("operation").GetString());
            Assert.Equal(expected[index].Address, ReadNullableInt(actual[index], "address"));
            Assert.Equal(expected[index].Value, ReadNullableString(actual[index], "value"));
        }
    }

    [Fact]
    public void TheModeValuesMatchTheVendorWrites()
    {
        using JsonDocument spec = LoadSpec();
        JsonElement modes = spec.RootElement.GetProperty("modeValues");

        Assert.Equal(2, modes.GetProperty("Gaming").GetInt32());
        Assert.Equal(3, modes.GetProperty("Office").GetInt32());
        Assert.Equal(1, modes.GetProperty("Turbo").GetInt32());
    }

    [Fact]
    public void ThePollingAndReadbackRulesMatchTheVendorConstants()
    {
        using JsonDocument spec = LoadSpec();
        JsonElement root = spec.RootElement;

        JsonElement poll = root.GetProperty("poll");
        Assert.Equal(500, poll.GetProperty("intervalMs").GetInt32());
        Assert.Equal(10, poll.GetProperty("maxTries").GetInt32());
        Assert.Equal(253, poll.GetProperty("notReadyStatus1").GetInt32());
        Assert.Equal(201, poll.GetProperty("notReadyStatus2").GetInt32());

        JsonElement readback = root.GetProperty("readback");
        Assert.Equal(16, readback.GetProperty("pointCount").GetInt32());
        Assert.Equal(2, readback.GetProperty("dutyDivisor").GetInt32());
        Assert.Equal(3840, readback.GetProperty("cpu").GetProperty("upT").GetInt32());
        Assert.Equal(3856, readback.GetProperty("cpu").GetProperty("downT").GetInt32());
        Assert.Equal(3872, readback.GetProperty("cpu").GetProperty("duty").GetInt32());
        Assert.Equal(3888, readback.GetProperty("gpu").GetProperty("upT").GetInt32());
        Assert.Equal(3904, readback.GetProperty("gpu").GetProperty("downT").GetInt32());
        Assert.Equal(3920, readback.GetProperty("gpu").GetProperty("duty").GetInt32());

        JsonElement writeback = root.GetProperty("writeback");
        Assert.Equal(100, writeback.GetProperty("threshold").GetInt32());
        Assert.Equal(255, writeback.GetProperty("saturated").GetInt32());
        Assert.Equal(2, writeback.GetProperty("multiplier").GetInt32());
    }

    [Fact]
    public void TheAssetsTheServiceNeedsArePresent()
    {
        Assert.True(Directory.Exists(AppRoot), $"app fan-table tree is missing: {AppRoot}");
        Assert.Equal(FanTableAssetHarness.PerModelFileCount, FanTableAssetHarness.CountPerModelFiles(AppRoot));
        Assert.Empty(FanTableAssetHarness.TopLevelJsonFiles(AppRoot));
    }

    [Fact]
    public void TheResolvedDirectoryAndSpecClassMatchWhatTheServiceWillPick()
    {
        foreach (var code in Registry.PlatformCodes)
        {
            ModelIdentity identity = new(code.Code, 0, "NA", ModelSource.Ec);
            FanTableResolution resolution = FanTableResolver.Resolve(AppRoot, identity, Registry.PlatformCodeSet);

            Assert.Equal(FanTableResolutionKind.Directory, resolution.Kind);
            Assert.Equal(code.Code, Path.GetFileName(resolution.Directory));
        }

        // 规格类：bit6 置位 -> 1p5（商用机型走逐机型目录）；清零 -> legacy RamFan1；RamFan2 不可达。
        Assert.Equal(FanSpecClass.RamFan1p5, FanSpecSelector.Select(ramFan1p5Capability: true));
        Assert.Equal(FanSpecClass.RamFan1, FanSpecSelector.Select(ramFan1p5Capability: false));
        Assert.False(FanSpecSelector.IsReachable(FanSpecClass.RamFan2));

        // 枚举成员没有目录的两个代号：服务回退 EC 默认值，不跨机型。
        foreach (string orphan in new[] { "PH6TQxx", "PH6AGxx" })
        {
            ModelIdentity identity = new(orphan, 0, "NA", ModelSource.Ec);
            FanTableResolution resolution = FanTableResolver.Resolve(AppRoot, identity, Registry.PlatformCodeSet);
            Assert.Equal(FanTableResolutionKind.EcdDefaults, resolution.Kind);
            Assert.Null(resolution.Directory);
        }
    }

    static JsonDocument LoadSpec()
    {
        Assert.True(File.Exists(SpecPath), $"doorbell spec asset is missing: {SpecPath}");
        return JsonDocument.Parse(File.ReadAllText(SpecPath));
    }

    static int? ReadNullableInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    static string? ReadNullableString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
