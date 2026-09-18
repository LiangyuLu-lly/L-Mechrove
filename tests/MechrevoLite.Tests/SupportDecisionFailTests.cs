using MechrevoLite.Hardware;
using Probe;

namespace MechrevoLite.Tests;

/// <summary>
/// T3（Wave A）失败 / 边界路径：F3 必须 fail-closed——"读不到"与"读得到但不在 24 集合内"都不支持，
/// 且**没有**"默认为真"的分支。happy 断言见 <see cref="SupportDecisionTests"/>。
/// </summary>
public class SupportDecisionFailTests
{
    static readonly IReadOnlySet<string> Supported = ModelRegistryData.Load().PlatformCodeSet;

    sealed class UnreadableEc : IEcReadTransport
    {
        public int ReadByte(int address) => -1;
    }

    [Fact]
    public void AnEmptySupportSetSupportsNothing()
    {
        SupportDecision decision = ModelSupport.Determine(
            new ModelIdentity("PH4TRX1", 18, "IDY", ModelSource.Ec), new HashSet<string>());

        Assert.False(decision.IsSupported);
        Assert.Equal(SupportReason.NotInSet, decision.Reason);
    }

    [Fact]
    public void AnUnreadableEcIsUnparsableAndNotSupported()
    {
        SupportDecision decision = ModelSupport.Determine(ModelRegistry.Read(new UnreadableEc()), Supported);

        Assert.False(decision.IsSupported);
        Assert.Equal(SupportReason.Unparsable, decision.Reason);
        Assert.Equal(ModelIdentity.UnknownName, decision.ProjectId);
    }

    [Fact]
    public void AnOutOfEnumProjectByteIsUnparsableNotNotInSet()
    {
        SupportDecision decision = ModelSupport.Determine(
            ModelRegistry.Read(new ModelRegistryTests.FakeEc(new() { [1856] = 250 })), Supported);

        Assert.False(decision.IsSupported);
        Assert.Equal(SupportReason.Unparsable, decision.Reason);
    }

    [Fact]
    public void AnUnparsableIdentityIsNotRescuedByASetThatContainsUnknown()
    {
        var setWithUnknown = new HashSet<string>(Supported, StringComparer.Ordinal) { ModelIdentity.UnknownName };

        SupportDecision decision = ModelSupport.Determine(
            new ModelIdentity(ModelIdentity.UnknownName, -1, ModelIdentity.UnknownName, ModelSource.Unknown),
            setWithUnknown);

        Assert.False(decision.IsSupported);
        Assert.Equal(SupportReason.Unparsable, decision.Reason);
    }

    [Fact]
    public void AFamilyCodeThatDidNotExpandIntoTheSetIsNotInSet()
    {
        // PHxAxxx 是枚举成员（23）但不是 24 个机型之一：不得因为"像机型"就放行。
        SupportDecision decision = ModelSupport.Determine(
            new ModelIdentity("PHxAxxx", 23, "IDY", ModelSource.Ec), Supported);

        Assert.False(decision.IsSupported);
        Assert.Equal(SupportReason.NotInSet, decision.Reason);
    }

    [Fact]
    public void ACorroborationMismatchOnAnUnsupportedModelDoesNotMakeItSupported()
    {
        SupportDecision baseline = ModelSupport.Determine(ModelRegistry.Read(Project(22)), Supported);

        Assert.Equal(SupportReason.NotInSet, baseline.Reason);
        Assert.Equal(baseline, ModelSupport.Determine(ModelRegistry.Read(Project(22)), Supported, "PH4TRX1"));
        Assert.Equal(baseline, ModelSupport.Determine(ModelRegistry.Read(Project(22)), Supported, "IDR"));
    }

    [Fact]
    public void EveryNonOkReasonCarriesIsSupportedFalse()
    {
        SupportDecision unparsable = ModelSupport.Determine(ModelRegistry.Read(new UnreadableEc()), Supported);
        SupportDecision notInSet = ModelSupport.Determine(ModelRegistry.Read(Project(22)), Supported);

        Assert.Equal(SupportReason.Unparsable, unparsable.Reason);
        Assert.Equal(SupportReason.NotInSet, notInSet.Reason);
        Assert.False(unparsable.IsSupported);
        Assert.False(notInSet.IsSupported);
    }

    static ModelRegistryTests.FakeEc Project(int projectByte) => new(new() { [1856] = projectByte });
}
