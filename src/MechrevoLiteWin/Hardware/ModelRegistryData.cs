using System.Text.Json;

namespace MechrevoLite.Hardware;

/// <summary>注册表数据不可用或违反 schema。<see cref="ModelRegistryData.Parse"/> 对任何越界/缺项都抛它，绝不静默放行。</summary>
public sealed class ModelRegistryDataException : Exception
{
    public ModelRegistryDataException(string message) : base(message) { }
}

/// <summary>一个平台（机箱）代号及其厂商 ProjectID 值。<c>ProjectId</c> 为 null 仅见 <c>PH4AQxx</c>（见文件的 _comment）。</summary>
public sealed record PlatformCodeIdentity(string Code, int? ProjectId);

/// <summary>风扇表键集分组：同组机型的 6 张表顶层键并集（去掉 <c>Activated</c>/<c>Name</c> 两个元数据键）相同。</summary>
public sealed record FanTableGrouping(string Id, IReadOnlyList<string> Keys, IReadOnlyList<string> Models);

/// <summary>
/// <c>Resources/model-registry.json</c> 的类型化视图。**只含身份数据**：
/// 24 个平台代号 + EC 1856/1868 原始值映射 + 5 张厂商名单 + 风扇表键集分组 + 轴 2 代际取值域。
///
/// <para>能力位不在这里（T6 运行时派生）；"平台代号 -&gt; 代际"也不在这里（轴 2 是运行时 GPU 探测，
/// 登记为未决）；<c>BIOS_PROJECT_ID</c> 只是另一套 ID 空间，绝不是代际来源。</para>
/// </summary>
public sealed class ModelRegistryData
{
    /// <summary>嵌入资源名（MechrevoLite.csproj 的 LogicalName）。</summary>
    public const string ResourceName = "LMechrevo.model-registry.json";

    public const int SupportedSchemaVersion = 1;

    /// <summary>24 个平台代号 = <c>UserFanTables</c> 的 24 个目录名。</summary>
    public const int PlatformCodeCount = 24;

    static readonly IReadOnlyDictionary<string, int> ExpectedVendorListCounts = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["commercial"] = 28,
        ["commercialHave20Db"] = 14,
        ["singleColorKeyboard"] = 9,
        ["nonNumpad"] = 3,
        ["idpIdy"] = 3,
    };

    static readonly IReadOnlyDictionary<string, int> ExpectedGpuSkuCounts = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["gn20"] = 10,
        ["gn21"] = 19,
    };

    /// <summary>拒绝出现的字段名（小写）；身份注册表不含能力位，也不含逐代号代际。</summary>
    static readonly HashSet<string> ForbiddenFieldNames = new(StringComparer.Ordinal)
    {
        "capabilities", "capability", "capabilitybits", "generation", "dgpugeneration", "generationsource",
    };

    static readonly int[] ExpectedDgpuGenerations = { 30, 40, 50 };

    ModelRegistryData(
        IReadOnlyList<PlatformCodeIdentity> platformCodes,
        IReadOnlyDictionary<int, string> biosProjectBytes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> vendorLists,
        IReadOnlyDictionary<string, IReadOnlyList<string>> gpuSkus,
        IReadOnlyList<FanTableGrouping> fanTableGroups,
        IReadOnlyList<int> dgpuGenerations)
    {
        PlatformCodes = platformCodes;
        BiosProjectBytes = biosProjectBytes;
        VendorLists = vendorLists;
        GpuSkus = gpuSkus;
        FanTableGroups = fanTableGroups;
        DgpuGenerations = dgpuGenerations;
        PlatformCodeSet = new HashSet<string>(platformCodes.Select(code => code.Code), StringComparer.Ordinal);
    }

    public IReadOnlyList<PlatformCodeIdentity> PlatformCodes { get; }
    public IReadOnlyDictionary<int, string> BiosProjectBytes { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> VendorLists { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GpuSkus { get; }
    public IReadOnlyList<FanTableGrouping> FanTableGroups { get; }
    public IReadOnlyList<int> DgpuGenerations { get; }

    /// <summary>F3 的支持集合（轴 1 口径）：24 个可解析平台代号。</summary>
    public IReadOnlySet<string> PlatformCodeSet { get; }

    /// <summary>读嵌入资源并校验；缺失或违约即抛 <see cref="ModelRegistryDataException"/>。</summary>
    public static ModelRegistryData Load()
    {
        using Stream? stream = typeof(ModelRegistryData).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            throw new ModelRegistryDataException($"embedded resource '{ResourceName}' is missing");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>解析并校验一份注册表文档。任何缺项、越界或禁止字段都抛 <see cref="ModelRegistryDataException"/>。</summary>
    public static ModelRegistryData Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ModelRegistryDataException("registry is not valid JSON: " + ex.Message);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ModelRegistryDataException("registry root must be an object");

            RejectForbiddenFields(root, "$");

            int version = RequireInt(root, "schemaVersion");
            if (version != SupportedSchemaVersion)
                throw new ModelRegistryDataException($"unsupported schemaVersion {version} (want {SupportedSchemaVersion})");

            return new ModelRegistryData(
                ReadPlatformCodes(root),
                ReadBiosProjectBytes(root),
                ReadStringLists(root, "vendorLists", ExpectedVendorListCounts),
                ReadStringLists(root, "gpuSkus", ExpectedGpuSkuCounts),
                ReadFanTableGroups(root),
                ReadDgpuGenerations(root));
        }
    }

    /// <summary>机型身份（轴 1）是否落在 24 个平台代号内。名字来自厂商枚举的展开结果。</summary>
    public bool IsPlatformCode(string code) => PlatformCodeSet.Contains(code);

    static void RejectForbiddenFields(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string lower = property.Name.ToLowerInvariant();
                bool biosGenerationSource = lower.Contains("bios") && lower.Contains("generation");
                if (ForbiddenFieldNames.Contains(lower) || biosGenerationSource)
                    throw new ModelRegistryDataException(
                        $"forbidden field '{property.Name}' at {path}: the identity registry carries no capability bits and no per-code generation");
                RejectForbiddenFields(property.Value, path + "." + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray()) RejectForbiddenFields(item, path + "[]");
        }
    }

    static List<PlatformCodeIdentity> ReadPlatformCodes(JsonElement root)
    {
        JsonElement array = Require(root, "platformCodes", JsonValueKind.Array);
        var result = new List<PlatformCodeIdentity>();
        var seenCodes = new HashSet<string>(StringComparer.Ordinal);
        var seenProjectIds = new HashSet<int>();

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ModelRegistryDataException("platformCodes entries must be objects");

            string? code = item.TryGetProperty("code", out JsonElement codeElement) && codeElement.ValueKind == JsonValueKind.String
                ? codeElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(code))
                throw new ModelRegistryDataException("platformCodes entry has no 'code'");
            if (!seenCodes.Add(code!))
                throw new ModelRegistryDataException($"duplicate platform code '{code}'");

            int? projectId = null;
            if (item.TryGetProperty("projectId", out JsonElement idElement) && idElement.ValueKind != JsonValueKind.Null)
            {
                if (idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt32(out int id))
                    throw new ModelRegistryDataException($"platformCodes '{code}' has a non-integer projectId");
                if (!seenProjectIds.Add(id))
                    throw new ModelRegistryDataException($"duplicate projectId {id}");
                projectId = id;
            }
            result.Add(new PlatformCodeIdentity(code!, projectId));
        }

        if (result.Count != PlatformCodeCount)
            throw new ModelRegistryDataException($"platformCodes must cover the {PlatformCodeCount} fan-table codes, got {result.Count}");
        return result;
    }

    static IReadOnlyDictionary<int, string> ReadBiosProjectBytes(JsonElement root)
    {
        JsonElement array = Require(root, "biosProjectBytes", JsonValueKind.Array);
        var result = new Dictionary<int, string>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ModelRegistryDataException("biosProjectBytes entries must be objects");
            if (!item.TryGetProperty("masked", out JsonElement maskedElement) ||
                maskedElement.ValueKind != JsonValueKind.Number || !maskedElement.TryGetInt32(out int masked))
                throw new ModelRegistryDataException("biosProjectBytes entry has no integer 'masked'");

            string? name = item.TryGetProperty("name", out JsonElement nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
                throw new ModelRegistryDataException($"biosProjectBytes entry {masked} has no 'name'");
            if (!result.TryAdd(masked, name!))
                throw new ModelRegistryDataException($"duplicate biosProjectBytes masked value {masked}");
        }
        if (result.Count == 0)
            throw new ModelRegistryDataException("biosProjectBytes is empty");
        return result;
    }

    static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadStringLists(
        JsonElement root, string name, IReadOnlyDictionary<string, int> expectedCounts)
    {
        JsonElement container = Require(root, name, JsonValueKind.Object);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach ((string key, int expected) in expectedCounts)
        {
            if (!container.TryGetProperty(key, out _))
                throw new ModelRegistryDataException($"{name}.{key} is missing");
            List<string> values = ReadStringArray(container, key, name);
            if (values.Count != expected)
                throw new ModelRegistryDataException($"{name}.{key} must have {expected} members, got {values.Count}");
            result[key] = values;
        }
        return result;
    }

    static List<FanTableGrouping> ReadFanTableGroups(JsonElement root)
    {
        JsonElement array = Require(root, "fanTableGroups", JsonValueKind.Array);
        var result = new List<FanTableGrouping>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        int totalModels = 0;

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ModelRegistryDataException("fanTableGroups entries must be objects");
            string? id = item.TryGetProperty("id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                throw new ModelRegistryDataException("fanTableGroups entry has no 'id'");
            if (!seenIds.Add(id!))
                throw new ModelRegistryDataException($"duplicate fanTableGroups id '{id}'");

            List<string> keys = ReadStringArray(item, "keys", $"fanTableGroups[{id}]");
            List<string> models = ReadStringArray(item, "models", $"fanTableGroups[{id}]");
            if (keys.Count == 0 || models.Count == 0)
                throw new ModelRegistryDataException($"fanTableGroups[{id}] must have keys and models");
            if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
                throw new ModelRegistryDataException($"fanTableGroups[{id}] has duplicate keys");

            totalModels += models.Count;
            result.Add(new FanTableGrouping(id!, keys, models));
        }

        if (result.Count == 0)
            throw new ModelRegistryDataException("fanTableGroups is empty");
        if (totalModels != PlatformCodeCount)
            throw new ModelRegistryDataException($"fanTableGroups models must sum to {PlatformCodeCount}, got {totalModels}");
        return result;
    }

    static IReadOnlyList<int> ReadDgpuGenerations(JsonElement root) =>
        ReadIntArray(root, "dgpuGenerations");

    static IReadOnlyList<int> ReadIntArray(JsonElement root, string name)
    {
        JsonElement array = Require(root, name, JsonValueKind.Array);
        var result = new List<int>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int value))
                throw new ModelRegistryDataException($"{name} must contain only integers");
            result.Add(value);
        }
        if (!result.OrderBy(value => value).SequenceEqual(ExpectedDgpuGenerations))
            throw new ModelRegistryDataException(
                $"{name} must be the axis-2 domain {{{string.Join(",", ExpectedDgpuGenerations)}}}, got {{{string.Join(",", result)}}}");
        return result;
    }

    static List<string> ReadStringArray(JsonElement parent, string name, string context)
    {
        JsonElement array = Require(parent, name, JsonValueKind.Array);
        var result = new List<string>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ModelRegistryDataException($"{context}.{name} must contain only strings");
            string? value = item.GetString();
            if (string.IsNullOrWhiteSpace(value))
                throw new ModelRegistryDataException($"{context}.{name} contains an empty entry");
            result.Add(value!);
        }
        return result;
    }

    static int RequireInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int value))
            throw new ModelRegistryDataException($"'{name}' is missing or not an integer");
        return value;
    }

    static JsonElement Require(JsonElement parent, string name, JsonValueKind kind)
    {
        if (!parent.TryGetProperty(name, out JsonElement element))
            throw new ModelRegistryDataException($"'{name}' is missing");
        if (element.ValueKind != kind)
            throw new ModelRegistryDataException($"'{name}' must be a {kind}, got {element.ValueKind}");
        return element;
    }
}
