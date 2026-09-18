using System.Text.Json;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T2（Wave A）happy 路径：<c>Resources/model-registry.json</c> 的身份数据必须与厂商表和
/// <c>UserFanTables</c> 实况逐一相符，并且**只**含身份（无能力位、无逐代号代际）。
/// 失败/边界断言见 <see cref="ModelRegistryDataSchemaFailTests"/>。
/// </summary>
public class ModelRegistryDataTests
{
    static readonly ModelRegistryData Registry = ModelRegistryData.Load();

    [Fact]
    public void TheRegistryCoversExactlyThe24FanTablePlatformCodes()
    {
        string[] onDisk = Directory.GetDirectories(FanTablesRoot())
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] inRegistry = Registry.PlatformCodes
            .Select(code => code.Code)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(24, onDisk.Length);
        Assert.Equal(onDisk, inRegistry);
    }

    [Fact]
    public void EveryPlatformCodeHasAVendorEnumIdentityExceptTheDocumentedOrphan()
    {
        // 厂商 ProjectID 只有 PH6AQxx=5892；PH4AQxx 这个风扇表目录没有对应枚举成员（数据实况）。
        string[] withoutEnumIdentity = Registry.PlatformCodes
            .Where(code => code.ProjectId is null)
            .Select(code => code.Code)
            .ToArray();
        Assert.Equal(new[] { "PH4AQxx" }, withoutEnumIdentity);

        foreach (PlatformCodeIdentity code in Registry.PlatformCodes.Where(code => code.ProjectId is not null))
            Assert.True(ModelRegistry.IsKnownProjectName(code.Code), $"'{code.Code}' is not a vendor ProjectID member");
    }

    [Fact]
    public void TheBiosProjectByteMapMatchesTheVendorSwitch()
    {
        Assert.Equal(17, Registry.BiosProjectBytes.Count);
        Assert.Equal("IDR", Registry.BiosProjectBytes[0]);
        Assert.Equal("IDY", Registry.BiosProjectBytes[6]);
        Assert.Equal("ID2", Registry.BiosProjectBytes[16]);
    }

    [Fact]
    public void TheFiveVendorListsMatchTheVendorSources()
    {
        Assert.Equal(28, Registry.VendorLists["commercial"].Count);
        Assert.Equal(14, Registry.VendorLists["commercialHave20Db"].Count);
        Assert.Equal(9, Registry.VendorLists["singleColorKeyboard"].Count);
        Assert.Equal(3, Registry.VendorLists["nonNumpad"].Count);
        Assert.Equal(3, Registry.VendorLists["idpIdy"].Count);

        Assert.Contains("PH6AQxx", Registry.VendorLists["commercial"]);
        Assert.DoesNotContain("PH4AQxx", Registry.VendorLists["commercial"]);
        Assert.Equal(new[] { "PH4TRX1", "PH4TUX1", "PH4TQx1" }, Registry.VendorLists["nonNumpad"]);
        Assert.Equal(new[] { "IDP", "IDY_6Y", "IDY_7Y" }, Registry.VendorLists["idpIdy"]);
    }

    [Fact]
    public void TheGpuSkuListsMatchTheVendorEnums()
    {
        Assert.Equal(10, Registry.GpuSkus["gn20"].Count);
        Assert.Equal(19, Registry.GpuSkus["gn21"].Count);
        Assert.Equal(new[] { "NA", "E3", "E4", "E5", "MaxQ", "E7", "P0", "P1", "E6", "E8" }, Registry.GpuSkus["gn20"]);
        Assert.Equal("X2R", Registry.GpuSkus["gn21"][^1]);
    }

    [Fact]
    public void TheFanTableGroupsMatchTheActualKeySetsOnDisk()
    {
        var actual = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal); // signature -> models
        foreach (string directory in Directory.GetDirectories(FanTablesRoot()))
        {
            string model = Path.GetFileName(directory)!;
            SortedSet<string> keys = UnionKeys(directory);
            string signature = string.Join("\n", keys);
            if (!actual.TryGetValue(signature, out SortedSet<string>? models))
                actual[signature] = models = new SortedSet<string>(StringComparer.Ordinal);
            models.Add(model);
        }

        // 4 组、5/8/5/6、Σ == 24；分组必须由数据派生，不得为凑 24 放宽。
        Assert.Equal(4, actual.Count);
        Assert.Equal(new[] { 5, 5, 6, 8 }, actual.Values.Select(group => group.Count).OrderBy(count => count).ToArray());
        Assert.Equal(24, actual.Values.Sum(group => group.Count));

        foreach (FanTableGrouping group in Registry.FanTableGroups)
        {
            string signature = string.Join("\n", group.Keys.OrderBy(key => key, StringComparer.Ordinal));
            Assert.True(actual.TryGetValue(signature, out SortedSet<string>? models),
                $"registry group '{group.Id}' key set does not match any on-disk key set");
            Assert.Equal(models!.ToArray(), group.Models.OrderBy(model => model, StringComparer.Ordinal).ToArray());
        }
    }

    [Fact]
    public void TheDgpuGenerationDomainIsThirtyFortyFifty()
    {
        Assert.Equal(new[] { 30, 40, 50 }, Registry.DgpuGenerations.OrderBy(value => value).ToArray());
    }

    [Fact]
    public void TheEmbeddedRegistryMatchesTheOnDiskResource()
    {
        ModelRegistryData onDisk = ModelRegistryData.Parse(File.ReadAllText(RegistryFile()));

        Assert.Equal(Registry.PlatformCodes.Select(code => code.Code), onDisk.PlatformCodes.Select(code => code.Code));
        Assert.Equal(Registry.FanTableGroups.Select(group => group.Id), onDisk.FanTableGroups.Select(group => group.Id));
    }

    /// <summary>盘上的注册表**不得**含能力位字段、逐代号代际字段或 BIOS 代际来源字段。</summary>
    [Fact]
    public void TheOnDiskRegistryCarriesNoCapabilitiesAndNoPerCodeGeneration()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(RegistryFile()));
        JsonElement root = document.RootElement;

        foreach (JsonProperty property in root.EnumerateObject())
        {
            Assert.DoesNotContain("capab", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.False(
                property.Name.Contains("bios", StringComparison.OrdinalIgnoreCase) &&
                property.Name.Contains("generation", StringComparison.OrdinalIgnoreCase),
                $"'{property.Name}' looks like a BIOS-derived generation source");
        }

        foreach (JsonElement code in root.GetProperty("platformCodes").EnumerateArray())
            foreach (JsonProperty property in code.EnumerateObject())
                Assert.DoesNotContain("generation", property.Name, StringComparison.OrdinalIgnoreCase);
    }

    static SortedSet<string> UnionKeys(string directory)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(directory, "*.json"))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
                if (property.Name is not ("Activated" or "Name"))
                    keys.Add(property.Name);
        }
        return keys;
    }

    static string FanTablesRoot() =>
        RepoFile(Path.Combine("ControlCenterX_5.56.60.26_Mechrevo", "UserFanTables"));

    internal static string RegistryFile() =>
        RepoFile(Path.Combine("src", "MechrevoLiteWin", "Resources", "model-registry.json"));

    internal static string RepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, relativePath);
    }
}
