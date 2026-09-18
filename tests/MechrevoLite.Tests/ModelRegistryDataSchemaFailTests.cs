using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T2（Wave A）失败 / schema 路径：任何缺项、越界、重复或"禁止的身份外字段"都必须让
/// <see cref="ModelRegistryData.Parse"/> 抛 <see cref="ModelRegistryDataException"/>——校验失败即测试失败。
/// happy 断言见 <see cref="ModelRegistryDataTests"/>。
/// </summary>
public class ModelRegistryDataSchemaFailTests
{
    [Fact]
    public void ParseRejectsAnUnsupportedSchemaVersion() =>
        AssertRejected(Mutate("\"schemaVersion\": 1", "\"schemaVersion\": 2"));

    [Fact]
    public void ParseRejectsMalformedJson() =>
        AssertRejected("{ not json");

    [Fact]
    public void ParseRejectsACapabilityField() =>
        AssertRejected(Mutate("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"capabilities\": {},"));

    [Fact]
    public void ParseRejectsAPerCodeGenerationField() =>
        AssertRejected(Mutate(
            "{ \"code\": \"PH4AQE3\", \"projectId\": 6154 }",
            "{ \"code\": \"PH4AQE3\", \"projectId\": 6154, \"generation\": 40 }"));

    [Fact]
    public void ParseRejectsABiosProjectIdGenerationSourceField() =>
        AssertRejected(Mutate(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1, \"biosProjectIdGenerationSource\": \"EC1868\","));

    [Fact]
    public void ParseRejectsADuplicatePlatformCode() =>
        AssertRejected(Mutate(
            "{ \"code\": \"PH4AQE3\", \"projectId\": 6154 }",
            "{ \"code\": \"PH4AQxx\", \"projectId\": 6154 }"));

    [Fact]
    public void ParseRejectsADuplicateProjectId() =>
        AssertRejected(Mutate(
            "{ \"code\": \"PH4ARxx\", \"projectId\": 5889 }",
            "{ \"code\": \"PH4ARxx\", \"projectId\": 6154 }"));

    [Fact]
    public void ParseRejectsAChangedVendorListCount() =>
        AssertRejected(Mutate("\"PH6PG3x150W\", \"PH6PG7x150W\"", "\"PH6PG3x150W\""));

    [Fact]
    public void ParseRejectsAChangedGpuSkuCount() =>
        AssertRejected(Mutate(", \"X11\", \"X2R\"", ", \"X11\""));

    [Fact]
    public void ParseRejectsFanTableGroupsThatDoNotSumTo24() =>
        AssertRejected(Mutate("\"PH6PG7x\", \"PH6PG7x150W\" ]", "\"PH6PG7x\" ]"));

    [Fact]
    public void ParseRejectsAMissingVendorList() =>
        AssertRejected(Mutate("\"nonNumpad\":", "\"nonNumpadRenamed\":"));

    [Fact]
    public void ParseRejectsADgpuDomainThatIsNotThirtyFortyFifty() =>
        AssertRejected(Mutate("\"dgpuGenerations\": [ 30, 40, 50 ]", "\"dgpuGenerations\": [ 30, 40 ]"));

    static void AssertRejected(string json)
    {
        ModelRegistryDataException exception =
            Assert.Throws<ModelRegistryDataException>(() => ModelRegistryData.Parse(json));
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    static string Mutate(string from, string to)
    {
        string json = File.ReadAllText(ModelRegistryDataTests.RegistryFile());
        Assert.Contains(from, json);
        return json.Replace(from, to);
    }
}
