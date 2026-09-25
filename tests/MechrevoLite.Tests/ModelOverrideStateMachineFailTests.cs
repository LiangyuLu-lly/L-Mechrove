using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T4（Wave A）失败 / 边界路径：非法手动值在任何模式下都不得生效、不得写盘；模式解析对未知值
/// fail-closed 回 <see cref="ModelOverrideMode.Auto"/>。happy 断言见 <see cref="ModelOverrideStateMachineTests"/>。
/// </summary>
public class ModelOverrideStateMachineFailTests
{
    [Fact]
    public void AnUnsupportedManualValueNeverBecomesEffectiveInAnyMode()
    {
        SupportDecision illegal = SupportDecision.NotInSet("PH6TQxx");

        foreach (ModelOverrideMode mode in new[]
                 { ModelOverrideMode.Auto, ModelOverrideMode.ManualPinned, ModelOverrideMode.ManualFallback })
        {
            ModelOverrideDecision decision = ModelOverrideStateMachine.Decide(
                mode, SupportDecision.NotInSet("PH6AGxx"), illegal);

            Assert.False(decision.ManualApplied, mode.ToString());
            Assert.NotEqual("PH6TQxx", decision.EffectiveModel);
        }
    }

    [Fact]
    public void AnAutomaticFailureWithAnIllegalManualValueHasNoEffectiveModel()
    {
        ModelOverrideDecision decision = ModelOverrideStateMachine.Decide(
            ModelOverrideMode.ManualFallback,
            SupportDecision.Unparsable(),
            SupportDecision.NotInSet("PH6TQxx"));

        Assert.Null(decision.EffectiveModel);
        Assert.False(decision.ManualApplied);
        Assert.Equal(ModelOverrideReason.ManualRejected, decision.Reason);
    }

    [Fact]
    public void TrySetManualWithAnIllegalCodeLeavesTheStoredValueUntouched()
    {
        ModelOverrideStateMachineTests.WithConfig(() =>
        {
            AppConfig.Set(ModelOverrideStateMachine.ModelKey, "PH4TRX1");

            Assert.False(ModelOverrideStateMachine.TrySetManual("NOTAMODEL", out SupportDecision decision));
            Assert.False(decision.IsSupported);
            Assert.Equal("PH4TRX1", AppConfig.GetString(ModelOverrideStateMachine.ModelKey));
        });
    }

    [Fact]
    public void AnIllegalEnvironmentOverrideDoesNotTakeEffect()
    {
        ModelOverrideStateMachineTests.WithEnvironment("NOTAMODEL", () =>
        {
            ModelOverrideDecision decision =
                ModelOverrideStateMachine.Evaluate(SupportDecision.Supported("PH4TRX1"));

            Assert.False(decision.ManualApplied);
            Assert.Equal("PH4TRX1", decision.EffectiveModel);
            Assert.Equal(ModelOverrideReason.ManualRejected, decision.Reason);
        });
    }

    [Fact]
    public void ParseModeIsFailClosedForUnknownValues()
    {
        Assert.Equal(ModelOverrideMode.Auto, ModelOverrideStateMachine.ParseMode(null));
        Assert.Equal(ModelOverrideMode.Auto, ModelOverrideStateMachine.ParseMode(""));
        Assert.Equal(ModelOverrideMode.Auto, ModelOverrideStateMachine.ParseMode("garbage"));
        Assert.Equal(ModelOverrideMode.ManualPinned, ModelOverrideStateMachine.ParseMode("ManualPinned"));
        Assert.Equal(ModelOverrideMode.ManualFallback, ModelOverrideStateMachine.ParseMode("manualfallback"));
    }

    [Fact]
    public void ABlankManualValueIsRejected()
    {
        Assert.False(ModelOverrideStateMachine.ValidateManual(null).IsSupported);
        Assert.False(ModelOverrideStateMachine.ValidateManual("   ").IsSupported);
        Assert.Equal(SupportReason.Unparsable, ModelOverrideStateMachine.ValidateManual("").Reason);
    }
}
