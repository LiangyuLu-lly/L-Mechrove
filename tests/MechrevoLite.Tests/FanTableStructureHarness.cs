using System.Text.Json;

namespace MechrevoLite.Tests;

/// <summary>
/// T13（Wave C）的风扇表结构校验：键集分组**从实测数据派生**，16 点表结构逐字段校验。
/// 只读——不改任何表。
/// </summary>
internal static class FanTableStructureHarness
{
    /// <summary>不算进键集的元数据键（厂商 <c>CustomizeTable</c> 的 Activated/Name）。</summary>
    internal static readonly string[] MetadataKeys = { "Activated", "Name" };

    /// <summary>每个点必须齐备的字段。</summary>
    internal static readonly string[] PointFields = { "ID", "UpT", "DownT", "Duty" };

    internal const int PointCount = 16;

    /// <summary>键集签名：排序后以一符分隔，键集相同即同组。</summary>
    internal static string Signature(IEnumerable<string> keys) =>
        string.Join("\n", keys.OrderBy(key => key, StringComparer.Ordinal));

    /// <summary>一个表的顶层键（去掉元数据键）。</summary>
    internal static SortedSet<string> ReadKeys(string file)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in Parse(file).EnumerateObject())
            if (!MetadataKeys.Contains(property.Name, StringComparer.Ordinal))
                keys.Add(property.Name);
        return keys;
    }

    /// <summary>一个机型目录里 6 张表的顶层键并集。</summary>
    internal static SortedSet<string> UnionKeys(string directory)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(directory, "*.json"))
            foreach (string key in ReadKeys(file))
                keys.Add(key);
        return keys;
    }

    /// <summary>由数据派生分组：键集签名 -&gt; 机型集合。绝不硬编码组数或成员。</summary>
    internal static IReadOnlyDictionary<string, SortedSet<string>> DeriveGroups(IEnumerable<string> directories)
    {
        var groups = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (string directory in directories)
        {
            string signature = Signature(UnionKeys(directory));
            if (!groups.TryGetValue(signature, out SortedSet<string>? models))
                groups[signature] = models = new SortedSet<string>(StringComparer.Ordinal);
            models.Add(Path.GetFileName(directory));
        }
        return groups;
    }

    /// <summary>单文件的 16 点结构校验；返回空清单即合规。</summary>
    internal static IReadOnlyList<string> StructureViolations(string file)
    {
        string name = Path.GetFileName(file);
        JsonElement root = Parse(file);
        var violations = new List<string>();

        foreach (string side in new[] { "CPU", "GPU" })
        {
            if (!root.TryGetProperty(side, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            {
                violations.Add($"{name}: {side} is missing or not an array");
                continue;
            }

            int count = array.GetArrayLength();
            if (count != PointCount)
                violations.Add($"{name}: {side} must have {PointCount} points, got {count}");

            int index = 0;
            foreach (JsonElement point in array.EnumerateArray())
            {
                if (point.ValueKind != JsonValueKind.Object)
                {
                    violations.Add($"{name}: {side}[{index}] is not an object");
                    index++;
                    continue;
                }

                foreach (string field in PointFields)
                    if (!point.TryGetProperty(field, out _))
                        violations.Add($"{name}: {side}[{index}] is missing '{field}'");

                if (point.TryGetProperty("ID", out JsonElement id) &&
                    id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out int value) && value != index)
                    violations.Add($"{name}: {side}[{index}] has ID {value}");

                index++;
            }
        }

        return violations;
    }

    /// <summary>单文件键集必须是所属组并集的子集；返回空清单即合规。</summary>
    internal static IReadOnlyList<string> SubsetViolations(string directory, IReadOnlyCollection<string> groupUnion)
    {
        string model = Path.GetFileName(directory);
        var union = new HashSet<string>(groupUnion, StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (string file in Directory.GetFiles(directory, "*.json"))
        {
            string[] outside = ReadKeys(file).Where(key => !union.Contains(key)).ToArray();
            if (outside.Length > 0)
                violations.Add($"{model}/{Path.GetFileName(file)}: keys outside the group union: {string.Join(", ", outside)}");
        }

        return violations;
    }

    static JsonElement Parse(string file)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
        return document.RootElement.Clone();
    }
}
