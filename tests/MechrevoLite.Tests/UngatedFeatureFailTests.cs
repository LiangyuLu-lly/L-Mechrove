using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T8（Wave B）失败 / 边界：取证文件缺项或枚举越界必须被拒；E1/E4 的机型串残留必须消失。
/// happy 断言见 <see cref="UngatedFeatureTests"/>。
/// </summary>
public class UngatedFeatureFailTests
{
    const string GoodBody = """
        {"item":"E1","outcome":"CONFIRMED","action":"MATRIX_GATED","lines":[547]}
        """;

    static string Evidence(string body) => "{\"items\":[" + body + "]}";

    [Fact]
    public void AnOutOfRangeOutcomeIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => T8Evidence.Parse(
            Evidence("""{"item":"E1","outcome":"MAYBE","action":"MATRIX_GATED","lines":[1]}""")));
    }

    [Fact]
    public void AnOutOfRangeActionIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => T8Evidence.Parse(
            Evidence("""{"item":"E1","outcome":"CONFIRMED","action":"DELETED","lines":[1]}""")));
    }

    [Fact]
    public void AnUnknownItemIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => T8Evidence.Parse(
            Evidence("""{"item":"E9","outcome":"CONFIRMED","action":"MATRIX_GATED","lines":[1]}""")));
    }

    [Fact]
    public void AMissingItemIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => T8Evidence.Parse(Evidence(GoodBody)));
    }

    [Fact]
    public void AnItemWithoutLineNumbersIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => T8Evidence.Parse(
            Evidence("""{"item":"E1","outcome":"CONFIRMED","action":"MATRIX_GATED","lines":[]}""")));
    }

    [Fact]
    public void E1NoBareModelSubstringRemainsInTheForcedGpuModePath()
    {
        string appConfig = T8Evidence.SourceFile("AppConfig.cs");
        Assert.DoesNotContain("ContainsModel(\"503\")", appConfig);

        string gpuMode = T8Evidence.SourceFile("Gpu", "GPUModeControl.cs");
        Assert.Contains("SupportsDgpuDirect", gpuMode);
        Assert.Contains("SupportsIgpuOnly", gpuMode);
    }

    [Fact]
    public void E4NoAsusModelPowerTableRemains()
    {
        string source = T8Evidence.SourceFile("Gpu", "NVidia", "NvidiaSmi.cs");
        foreach (string model in new[] { "GU605", "GA605", "GA403", "FA607" })
            Assert.DoesNotContain(model, source);
        Assert.False(MechrevoHw.ShouldApplyBuiltInCurveDefaults(
            new MechrevoDeviceCapabilities { ProfileAvailable = true, FanSettings = false }));
    }
}
