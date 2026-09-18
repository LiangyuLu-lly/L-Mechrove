using System.Text.Json;
using System.Text.RegularExpressions;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// T29 (Wave F) happy path: the panel-visibility semantics no longer depend on the inverted
/// <c>NoGpu()</c> predicate, and the E2/E3 forensics are recorded with valid enums and verified
/// line numbers. Failure assertions live in <see cref="VisibilitySemanticsFailTests"/>.
/// </summary>
public class VisibilitySemanticsTests
{
    [Fact]
    public void PanelVisibilityIsIndependentOfTheRetiredNoGpuPredicate()
    {
        Assert.False(ScreenPanelVisibility.PanelVisible(0));
        Assert.True(ScreenPanelVisibility.PanelVisible(60));
        Assert.False(ScreenPanelVisibility.RefreshTableVisible(60, 60));
        Assert.True(ScreenPanelVisibility.RefreshTableVisible(144, 60));
    }

    [Fact]
    public void TheEvidenceCoversE2AndE3WithValidEnums()
    {
        VisibilityEvidence evidence = VisibilityEvidence.Load();

        Assert.Contains("E2", evidence.Items.Select(item => item.Item));
        Assert.Contains("E3", evidence.Items.Select(item => item.Item));
        foreach (VisibilityEvidenceItem item in evidence.Items)
        {
            Assert.Contains(item.Outcome, new[] { "CONFIRMED", "REFUTED" });
            Assert.Contains(item.Action, new[] { "CORRECTED", "KEPT_WITH_REASON" });
        }
    }

    [Fact]
    public void TheEvidenceRecordsVerifiedLineNumbers()
    {
        VisibilityEvidence evidence = VisibilityEvidence.Load();
        foreach (VisibilityEvidenceItem item in evidence.Items)
        {
            Assert.NotEmpty(item.Lines);
            Assert.All(item.Lines, line => Assert.True(line > 0));
        }
    }

    [Fact]
    public void NoModelStringPredicateRemainsInAppConfig()
    {
        string source = VisibilityEvidence.ReadAppConfig();

        Assert.Empty(VisibilityEvidence.ScanForModelStringPredicates(source));
    }
}

/// <summary>
/// T29 failure path: an under-specified evidence file or a model-string predicate that creeps back
/// into <c>AppConfig</c> must fail loudly.
/// </summary>
public class VisibilitySemanticsFailTests
{
    [Fact]
    public void AnUnknownOutcomeEnumIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => VisibilityEvidence.Parse("""
            { "items": [ { "item": "E2", "outcome": "MAYBE", "action": "CORRECTED", "lines": [1] } ] }
            """));
    }

    [Fact]
    public void AMissingE3ItemIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => VisibilityEvidence.Parse("""
            { "items": [ { "item": "E2", "outcome": "CONFIRMED", "action": "CORRECTED", "lines": [1] } ] }
            """));
    }

    [Fact]
    public void AnUnknownActionEnumIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => VisibilityEvidence.Parse("""
            { "items": [
                { "item": "E2", "outcome": "CONFIRMED", "action": "CORRECTED", "lines": [1] },
                { "item": "E3", "outcome": "CONFIRMED", "action": "DELETED", "lines": [2] } ] }
            """));
    }

    [Fact]
    public void TheScannerFlagsAModelStringPredicate()
    {
        string[] violations = VisibilityEvidence.ScanForModelStringPredicates(
            "public static bool NoGpu() => ContainsModel(\"UX540\");");

        Assert.Single(violations);
        Assert.Contains("UX540", violations[0]);
    }
}

internal sealed record VisibilityEvidenceItem(string Item, string Outcome, string Action, IReadOnlyList<int> Lines);

internal sealed record VisibilityEvidence(IReadOnlyList<VisibilityEvidenceItem> Items)
{
    static readonly string[] KnownItems = { "E2", "E3" };
    static readonly string[] KnownOutcomes = { "CONFIRMED", "REFUTED" };
    static readonly string[] KnownActions = { "CORRECTED", "KEPT_WITH_REASON" };

    internal static readonly Regex ModelStringPredicate =
        new("ContainsModel\\s*\\(\\s*\"(?<model>[^\"]+)\"\\s*\\)", RegexOptions.Compiled);

    internal static VisibilityEvidence Load() => Parse(File.ReadAllText(
        FanTableAssetHarness.RepoPath(Path.Combine(".omo", "evidence", "t29-evidence.json"))));

    internal static string ReadAppConfig() =>
        File.ReadAllText(FanTableAssetHarness.RepoPath(Path.Combine("src", "MechrevoLiteWin", "AppConfig.cs")));

    internal static string[] ScanForModelStringPredicates(string source) =>
        ModelStringPredicate.Matches(
            string.Join("\n", source.Split('\n').Where(line =>
            {
                string trimmed = line.TrimStart();
                return !trimmed.StartsWith("//", StringComparison.Ordinal);
            })))
            .Select(match => match.Value)
            .ToArray();

    internal static VisibilityEvidence Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("items", out JsonElement items) ||
            items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("t29 evidence: 'items' array missing");

        var parsed = new List<VisibilityEvidenceItem>();
        foreach (JsonElement item in items.EnumerateArray())
        {
            string name = item.GetProperty("item").GetString() ?? "";
            string outcome = item.GetProperty("outcome").GetString() ?? "";
            string action = item.GetProperty("action").GetString() ?? "";
            if (!KnownItems.Contains(name)) throw new InvalidDataException($"t29 evidence: unknown item '{name}'");
            if (!KnownOutcomes.Contains(outcome)) throw new InvalidDataException($"t29 evidence: unknown outcome '{outcome}'");
            if (!KnownActions.Contains(action)) throw new InvalidDataException($"t29 evidence: unknown action '{action}'");
            if (!item.TryGetProperty("lines", out JsonElement lines) || lines.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"t29 evidence: {name} has no lines");
            var lineNumbers = lines.EnumerateArray().Select(line => line.GetInt32()).ToArray();
            if (lineNumbers.Length == 0) throw new InvalidDataException($"t29 evidence: {name} has empty lines");
            parsed.Add(new VisibilityEvidenceItem(name, outcome, action, lineNumbers));
        }

        foreach (string required in KnownItems)
            if (!parsed.Any(entry => entry.Item == required))
                throw new InvalidDataException($"t29 evidence: missing item '{required}'");

        return new VisibilityEvidence(parsed);
    }
}
