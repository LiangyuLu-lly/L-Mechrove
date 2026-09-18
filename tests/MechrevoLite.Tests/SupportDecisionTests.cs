using MechrevoLite.Hardware;
using Probe;

namespace MechrevoLite.Tests;

/// <summary>
/// T3（Wave A）happy 路径：F3 判定是**轴 1 集合口径**——身份可解析且展开后的平台代号落在
/// 24 机型集合内才算支持。身份只由 EC 1856/1868 决定，服务写入的 <c>BIOS_PROJECT_ID</c> 至多佐证。
/// 失败/边界断言见 <see cref="SupportDecisionFailTests"/>。
/// </summary>
public class SupportDecisionTests
{
    static readonly IReadOnlySet<string> Supported = ModelRegistryData.Load().PlatformCodeSet;

    [Fact]
    public void AParsedIdentityInsideThe24CodeSetIsSupported()
    {
        SupportDecision decision = ModelSupport.Determine(ModelRegistry.Read(Project(18)), Supported);

        Assert.True(decision.IsSupported);
        Assert.Equal(SupportReason.Ok, decision.Reason);
        Assert.Equal("PH4TRX1", decision.ProjectId);
    }

    [Fact]
    public void EveryCodeInTheRegistrySetIsSupported()
    {
        Assert.Equal(24, Supported.Count);

        foreach (string code in Supported)
        {
            SupportDecision decision = ModelSupport.Determine(
                new ModelIdentity(code, 0, "IDY", ModelSource.Ec), Supported);
            Assert.True(decision.IsSupported, code);
            Assert.Equal(SupportReason.Ok, decision.Reason);
        }
    }

    [Fact]
    public void TheSupportSetIsExactlyTheRegistrys24Codes()
    {
        string[] registry = ModelRegistryData.Load().PlatformCodes
            .Select(code => code.Code).OrderBy(code => code, StringComparer.Ordinal).ToArray();

        Assert.Equal(registry, Supported.OrderBy(code => code, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void AnEnumCodeWithoutAFanTableDirectoryIsNotInSet()
    {
        // EC 23 + sys=128 + rom=1 + module2=48 -> 5894 PH6AGxx：枚举成员，但没有风扇表目录。
        ModelIdentity identity = ModelRegistry.Read(new ModelRegistryTests.FakeEc(new()
        {
            [1856] = 23,
            [1110] = 0x80,
            [1905] = 1,
            [2003] = 0x30,
        }));
        Assert.Equal("PH6AGxx", identity.ProjectId);

        SupportDecision decision = ModelSupport.Determine(identity, Supported);

        Assert.False(decision.IsSupported);
        Assert.Equal(SupportReason.NotInSet, decision.Reason);
        Assert.Equal("PH6AGxx", decision.ProjectId);
    }

    [Fact]
    public void AStandaloneEnumCodeWithoutADirectoryIsNotInSet()
    {
        SupportDecision decision = ModelSupport.Determine(ModelRegistry.Read(Project(22)), Supported);

        Assert.Equal("PH6TQxx", decision.ProjectId);
        Assert.False(decision.IsSupported);
        Assert.Equal(SupportReason.NotInSet, decision.Reason);
    }

    [Fact]
    public void DetermineFromTheTransportReadsTheDecisionThroughTheSeam()
    {
        SupportDecision decision = ModelSupport.Determine(Project(18));

        Assert.True(decision.IsSupported);
        Assert.Equal("PH4TRX1", decision.ProjectId);
    }

    [Fact]
    public void TheCorroboratingBiosProjectIdNeverFlipsTheVerdict()
    {
        ModelIdentity unsupported = ModelRegistry.Read(Project(22));  // PH6TQxx -> NotInSet
        ModelIdentity supported = ModelRegistry.Read(Project(18));    // PH4TRX1 -> Ok
        SupportDecision baselineUnsupported = ModelSupport.Determine(unsupported, Supported);
        SupportDecision baselineSupported = ModelSupport.Determine(supported, Supported);

        foreach (string? bios in new[] { null, "IDY", "IDR", "PH4TRX1", "PH6TQxx" })
        {
            Assert.Equal(baselineUnsupported, ModelSupport.Determine(unsupported, Supported, bios));
            Assert.Equal(baselineSupported, ModelSupport.Determine(supported, Supported, bios));
        }
    }

    static ModelRegistryTests.FakeEc Project(int projectByte) => new(new() { [1856] = projectByte });
}
