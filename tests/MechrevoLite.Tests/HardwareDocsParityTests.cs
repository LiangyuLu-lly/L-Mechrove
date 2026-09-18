using System.Text.Json;
using MechrevoLite.Gpu;
using MechrevoLite.Hardware;
using MechrevoLite.Mode;

namespace MechrevoLite.Tests;

/// <summary>
/// T30 (Wave F) happy path: <c>docs/hardware/model-support-matrices.md</c> is the single reference
/// for future model additions and is kept in sync with the code by validating its machine-readable
/// block against <see cref="ModelRegistryData"/>, <see cref="DisplayRouteMatrix"/> and the two mode
/// enums. Failure assertions live in <see cref="HardwareDocsParityFailTests"/>.
/// </summary>
public class HardwareDocsParityTests
{
    [Fact]
    public void TheDocCoversAllTwentyFourPlatformCodesAndVendorLists()
    {
        HardwareDocsParity parity = HardwareDocsParity.Load();

        Assert.Equal(24, parity.PlatformCodes.Count);
        Assert.Equal(28, parity.VendorListCounts["commercial"]);
        Assert.Equal(14, parity.VendorListCounts["commercialHave20Db"]);
        Assert.Equal(9, parity.VendorListCounts["singleColorKeyboard"]);
        Assert.Equal(3, parity.VendorListCounts["nonNumpad"]);
        Assert.Equal(3, parity.VendorListCounts["idpIdy"]);
    }

    [Fact]
    public void TheFanTableGroupSizesMatchTheDerivedKeySets()
    {
        HardwareDocsParity parity = HardwareDocsParity.Load();

        Assert.Equal(5, parity.FanTableGroupCounts["k9"]);
        Assert.Equal(8, parity.FanTableGroupCounts["k11"]);
        Assert.Equal(5, parity.FanTableGroupCounts["k12"]);
        Assert.Equal(6, parity.FanTableGroupCounts["k16"]);
        Assert.Equal(24, parity.FanTableGroupCounts.Values.Sum());
    }

    [Fact]
    public void TheGenerationTableMarksMatchTheCode()
    {
        HardwareDocsParity parity = HardwareDocsParity.Load();

        Assert.Equal("UNKNOWN", parity.GenerationMark("30", "serviceWritePath"));
        Assert.Equal("PROVEN_ABSENT", parity.GenerationMark("30", "igpuOnly"));
        Assert.Equal("PROVEN_ABSENT", parity.GenerationMark("30", "restart"));
        Assert.Equal("PROVEN", parity.GenerationMark("40", "serviceWritePath"));
        Assert.Equal("UNKNOWN", parity.GenerationMark("50", "serviceWritePath"));
        Assert.Equal("PROVEN", parity.GenerationMark("50", "igpuOnly"));
    }

    [Fact]
    public void TheModeEnumsMatchTheCode()
    {
        HardwareDocsParity parity = HardwareDocsParity.Load();

        Assert.Equal(1, parity.VendorSysPowerModes["Performance"]);
        Assert.Equal(2, parity.VendorSysPowerModes["Balanced"]);
        Assert.Equal(3, parity.VendorSysPowerModes["BatterySaver"]);
        Assert.Equal(4, parity.VendorSysPowerModes["Benchmark"]);
        Assert.Equal(0, parity.ConsoleOperatingModes["Office"]);
        Assert.Equal(1, parity.ConsoleOperatingModes["Gaming"]);
        Assert.Equal(2, parity.ConsoleOperatingModes["Turbo"]);
        Assert.Equal(3, parity.ConsoleOperatingModes["Customize"]);
    }

    [Fact]
    public void TheDocDeclaresPlatformCodeToGenerationAsInferredWithBothSources()
    {
        HardwareDocsParity parity = HardwareDocsParity.Load();

        Assert.Equal("INFERRED", parity.PlatformCodeToGeneration);
        Assert.Contains("docs\\upgrade-from-openrevo.md:140", parity.RawText);
        Assert.Contains("docs\\gcu-dependency-matrix.md:88", parity.RawText);
    }
}

/// <summary>
/// T30 failure path: a doc block that drifts from the code, loses its marker, or upgrades the
/// INFERRED platform-code→generation claim is rejected instead of passing silently.
/// </summary>
public class HardwareDocsParityFailTests
{
    [Fact]
    public void AMissingParityMarkerIsRejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            HardwareDocsParity.Parse("no markers here"));
    }

    [Fact]
    public void ADriftedVendorListCountIsRejected()
    {
        string doc = HardwareDocsParity.ReadDoc();
        string drifted = doc.Replace("\"commercial\": 28", "\"commercial\": 27");
        Assert.NotEqual(doc, drifted);
        Assert.Throws<InvalidDataException>(() => HardwareDocsParity.Parse(drifted));
    }

    [Fact]
    public void AnUpgradedGenerationMarkIsRejected()
    {
        string doc = HardwareDocsParity.ReadDoc();
        string drifted = doc.Replace("\"serviceWritePath\": \"UNKNOWN\"", "\"serviceWritePath\": \"PROVEN\"");
        Assert.Throws<InvalidDataException>(() => HardwareDocsParity.Parse(drifted));
    }

    [Fact]
    public void DeclaringPlatformCodeToGenerationAsProvenIsRejected()
    {
        string doc = HardwareDocsParity.ReadDoc();
        string drifted = doc.Replace("\"platformCodeToGeneration\": \"INFERRED\"", "\"platformCodeToGeneration\": \"PROVEN\"");
        Assert.NotEqual(doc, drifted);
        Assert.Throws<InvalidDataException>(() => HardwareDocsParity.Parse(drifted));
    }
}

/// <summary>Parses and validates the machine-readable block in the hardware matrices doc.</summary>
internal sealed record HardwareDocsParity(
    IReadOnlyList<string> PlatformCodes,
    IReadOnlyDictionary<string, int> VendorListCounts,
    IReadOnlyDictionary<string, int> FanTableGroupCounts,
    IReadOnlyDictionary<string, string> GenerationMarks,
    IReadOnlyDictionary<string, int> VendorSysPowerModes,
    IReadOnlyDictionary<string, int> ConsoleOperatingModes,
    string PlatformCodeToGeneration,
    string RawText)
{
    internal const string BeginMarker = "<!-- gcu-parity:begin -->";
    internal const string EndMarker = "<!-- gcu-parity:end -->";

    internal static HardwareDocsParity Load() => Parse(ReadDoc());

    internal static string ReadDoc() => File.ReadAllText(
        FanTableAssetHarness.RepoPath(Path.Combine("docs", "hardware", "model-support-matrices.md")));

    internal string GenerationMark(string generation, string field) => GenerationMarks[$"{generation}.{field}"];

    internal static HardwareDocsParity Parse(string docText)
    {
        int begin = docText.IndexOf(BeginMarker, StringComparison.Ordinal);
        int end = docText.IndexOf(EndMarker, StringComparison.Ordinal);
        if (begin < 0 || end < 0 || end <= begin)
            throw new InvalidDataException("hardware matrices doc: parity markers missing or out of order");
        string json = docText[(begin + BeginMarker.Length)..end];

        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidDataException("hardware matrices doc: block is not JSON: " + ex.Message); }

        using (document)
        {
            JsonElement root = document.RootElement;

            var platformCodes = root.GetProperty("platformCodes").EnumerateArray()
                .Select(e => e.GetString() ?? "").ToArray();
            IReadOnlySet<string> codeCodes = ModelRegistryData.Load().PlatformCodeSet;
            if (platformCodes.Length != 24 || !platformCodes.OrderBy(c => c, StringComparer.Ordinal)
                    .SequenceEqual(codeCodes.OrderBy(c => c, StringComparer.Ordinal)))
                throw new InvalidDataException("hardware matrices doc: platform code list drifted from the code registry");

            IReadOnlyDictionary<string, int> vendorCounts = ReadIntMap(root, "vendorListCounts");
            IReadOnlyDictionary<string, IReadOnlyList<string>> codeVendorLists = ModelRegistryData.Load().VendorLists;
            foreach ((string key, int expected) in vendorCounts)
            {
                if (!codeVendorLists.TryGetValue(key, out IReadOnlyList<string>? members) || members.Count != expected)
                    throw new InvalidDataException($"hardware matrices doc: vendorListCounts['{key}'] = {expected} drifted from the code");
            }

            IReadOnlyDictionary<string, int> fanCounts = ReadIntMap(root, "fanTableGroupCounts");
            IReadOnlyList<FanTableGrouping> codeGroups = ModelRegistryData.Load().FanTableGroups;
            foreach ((string key, int expected) in fanCounts)
            {
                FanTableGrouping? group = codeGroups.FirstOrDefault(g => g.Id == key);
                if (group is null || group.Models.Count != expected)
                    throw new InvalidDataException($"hardware matrices doc: fanTableGroupCounts['{key}'] = {expected} drifted from the code");
            }
            if (fanCounts.Values.Sum() != 24)
                throw new InvalidDataException("hardware matrices doc: fan table groups must sum to 24");

            var marks = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty generation in root.GetProperty("dgpuGenerations").EnumerateObject())
            {
                foreach (string field in new[] { "consoleProtocol", "serviceWritePath", "igpuOnly", "restart" })
                    marks[$"{generation.Name}.{field}"] = generation.Value.GetProperty(field).GetString() ?? "";
            }
            // N11: the 40-series has two capability tiers, so the doc carries a "40" row (with
            // 双显三模) and a "40-no3mode" row (without). Each matrix row is checked against its own
            // doc row; the tier is part of the key.
            foreach (GenerationRouteFacts row in DisplayRouteMatrix.Rows)
            {
                string key = row.Generation switch
                {
                    DgpuGenerationKind.Gen30 => "30",
                    DgpuGenerationKind.Gen40 => row.ThreeMode == false ? "40-no3mode" : "40",
                    DgpuGenerationKind.Gen50 => "50",
                    _ => ((int)row.Generation).ToString(),
                };
                AssertMark(marks, key, "consoleProtocol", row.ConsoleProtocol.Mark);
                AssertMark(marks, key, "serviceWritePath", row.ServiceWritePath.Mark);
                AssertMark(marks, key, "igpuOnly", row.IgpuOnly.Mark);
                AssertMark(marks, key, "restart", row.Restart.Mark);
            }

            IReadOnlyDictionary<string, int> vendorModes = ReadIntMap(root, "vendorSysPowerModes");
            foreach (VendorSysPowerMode mode in Enum.GetValues<VendorSysPowerMode>())
                if (!vendorModes.TryGetValue(mode.ToString(), out int value) || value != (int)mode)
                    throw new InvalidDataException($"hardware matrices doc: vendorSysPowerModes['{mode}'] drifted from the enum");
            IReadOnlyDictionary<string, int> consoleModes = ReadIntMap(root, "consoleOperatingModes");
            foreach (ConsoleOperatingMode mode in Enum.GetValues<ConsoleOperatingMode>())
                if (!consoleModes.TryGetValue(mode.ToString(), out int value) || value != (int)mode)
                    throw new InvalidDataException($"hardware matrices doc: consoleOperatingModes['{mode}'] drifted from the enum");

            string inferred = root.GetProperty("platformCodeToGeneration").GetString() ?? "";
            if (inferred != "INFERRED")
                throw new InvalidDataException(
                    $"hardware matrices doc: platformCodeToGeneration must stay INFERRED, got '{inferred}'");
            if (!docText.Contains("docs\\upgrade-from-openrevo.md:140", StringComparison.Ordinal) ||
                !docText.Contains("docs\\gcu-dependency-matrix.md:88", StringComparison.Ordinal))
                throw new InvalidDataException("hardware matrices doc: the INFERRED claim must cite both contradiction sources");

            return new HardwareDocsParity(platformCodes, vendorCounts, fanCounts, marks,
                vendorModes, consoleModes, inferred, docText);
        }
    }

    static void AssertMark(IReadOnlyDictionary<string, string> marks, string generation, string field, EvidenceMark mark)
    {
        string expected = mark switch
        {
            EvidenceMark.Proven => "PROVEN",
            EvidenceMark.Inferred => "INFERRED",
            EvidenceMark.ProvenAbsent => "PROVEN_ABSENT",
            _ => "UNKNOWN",
        };
        string key = $"{generation}.{field}";
        if (!marks.TryGetValue(key, out string? actual) || actual != expected)
            throw new InvalidDataException($"hardware matrices doc: {key} must be {expected}, got '{actual}'");
    }

    static IReadOnlyDictionary<string, int> ReadIntMap(JsonElement parent, string name)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonProperty property in parent.GetProperty(name).EnumerateObject())
            result[property.Name] = property.Value.GetInt32();
        return result;
    }
}
