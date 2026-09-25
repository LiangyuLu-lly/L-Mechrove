using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T4（Wave A）happy 路径：手动覆盖（D2）的显式三态状态机。自动优先，手动只在
/// <see cref="ModelOverrideMode.ManualPinned"/> 或"自动失败时的 ManualFallback"下生效。
/// 非法手动值被拒且不写盘。失败/边界断言见 <see cref="ModelOverrideStateMachineFailTests"/>。
/// </summary>
public class ModelOverrideStateMachineTests
{
    const string Variable = "LMECHREVO_MODEL_OVERRIDE";

    [Fact]
    public void AutoFollowsTheAutomaticVerdictAndIgnoresAManualValue()
    {
        ModelOverrideDecision decision = ModelOverrideStateMachine.Decide(
            ModelOverrideMode.Auto,
            SupportDecision.Supported("PH4TRX1"),
            SupportDecision.Supported("PH4PUxx"));

        Assert.Equal(ModelOverrideMode.Auto, decision.Mode);
        Assert.Equal("PH4TRX1", decision.EffectiveModel);
        Assert.False(decision.ManualApplied);
        Assert.Equal(ModelOverrideReason.AutoOk, decision.Reason);
    }

    [Fact]
    public void AutoToManualPinnedAppliesAndKeepsTheManualValue()
    {
        ModelOverrideDecision decision = ModelOverrideStateMachine.Decide(
            ModelOverrideMode.ManualPinned,
            SupportDecision.Supported("PH4TRX1"),
            SupportDecision.Supported("PH4PUxx"));

        Assert.Equal(ModelOverrideMode.ManualPinned, decision.Mode);
        Assert.Equal("PH4PUxx", decision.EffectiveModel);
        Assert.True(decision.ManualApplied);
        Assert.Equal(ModelOverrideReason.ManualAccepted, decision.Reason);
    }

    [Fact]
    public void ManualFallbackDoesNotApplyWhileTheAutomaticVerdictSucceeds()
    {
        ModelOverrideDecision decision = ModelOverrideStateMachine.Decide(
            ModelOverrideMode.ManualFallback,
            SupportDecision.Supported("PH4TRX1"),
            SupportDecision.Supported("PH4PUxx"));

        Assert.Equal("PH4TRX1", decision.EffectiveModel);
        Assert.False(decision.ManualApplied);
        Assert.Equal(ModelOverrideReason.AutoOk, decision.Reason);
    }

    [Fact]
    public void ManualFallbackAppliesWhenAutoFailsAndYieldsWhenAutoRecovers()
    {
        ModelOverrideDecision applied = ModelOverrideStateMachine.Decide(
            ModelOverrideMode.ManualFallback,
            SupportDecision.NotInSet("PH6TQxx"),
            SupportDecision.Supported("PH4PUxx"));

        Assert.Equal("PH4PUxx", applied.EffectiveModel);
        Assert.True(applied.ManualApplied);
        Assert.Equal(ModelOverrideReason.ManualAccepted, applied.Reason);

        // ManualFallback -> 自动一恢复即让位
        ModelOverrideDecision yielded = ModelOverrideStateMachine.Decide(
            ModelOverrideMode.ManualFallback,
            SupportDecision.Supported("PH4AQE3"),
            SupportDecision.Supported("PH4PUxx"));

        Assert.Equal("PH4AQE3", yielded.EffectiveModel);
        Assert.False(yielded.ManualApplied);
        Assert.Equal(ModelOverrideReason.AutoOk, yielded.Reason);
    }

    [Fact]
    public void AnIllegalManualValueIsRejectedAndTheAutomaticVerdictIsFollowed()
    {
        ModelOverrideDecision decision = ModelOverrideStateMachine.Decide(
            ModelOverrideMode.ManualPinned,
            SupportDecision.Supported("PH4TRX1"),
            SupportDecision.NotInSet("PH6TQxx"));

        Assert.Equal("PH4TRX1", decision.EffectiveModel);
        Assert.False(decision.ManualApplied);
        Assert.Equal(ModelOverrideReason.ManualRejected, decision.Reason);
    }

    [Fact]
    public void ValidateManualAcceptsParseableProjectIdNames()
    {
        SupportDecision ag = ModelOverrideStateMachine.ValidateManual("PH6AGxx");
        Assert.True(ag.IsSupported);
        Assert.Equal(SupportReason.Ok, ag.Reason);
        Assert.Equal("PH6AGxx", ag.ProjectId);

        SupportDecision tq = ModelOverrideStateMachine.ValidateManual("PH6TQxx");
        Assert.True(tq.IsSupported);
        Assert.Equal(SupportReason.Ok, tq.Reason);
        Assert.Equal("PH6TQxx", tq.ProjectId);
    }

    [Fact]
    public void ValidateManualRejectsUnknownAndBlankValues()
    {
        Assert.Equal(SupportReason.Unparsable, ModelOverrideStateMachine.ValidateManual(null).Reason);
        Assert.Equal(SupportReason.Unparsable, ModelOverrideStateMachine.ValidateManual("").Reason);
        Assert.Equal(SupportReason.Unparsable, ModelOverrideStateMachine.ValidateManual("   ").Reason);

        SupportDecision unknown = ModelOverrideStateMachine.ValidateManual("NOTAMODEL");
        Assert.False(unknown.IsSupported);
        Assert.Equal(SupportReason.NotInSet, unknown.Reason);
    }

    [Fact]
    public void TrySetManualPersistsAValidCodeAndRejectsAnIllegalOne()
    {
        WithConfig(() =>
        {
            AppConfig.Remove(ModelOverrideStateMachine.ModelKey);

            Assert.True(ModelOverrideStateMachine.TrySetManual("PH4PUxx", out SupportDecision accepted));
            Assert.True(accepted.IsSupported);
            Assert.Equal("PH4PUxx", AppConfig.GetString(ModelOverrideStateMachine.ModelKey));

            Assert.True(ModelOverrideStateMachine.TrySetManual("PH6AGxx", out SupportDecision parseable));
            Assert.True(parseable.IsSupported);
            Assert.Equal("PH6AGxx", AppConfig.GetString(ModelOverrideStateMachine.ModelKey));

            Assert.False(ModelOverrideStateMachine.TrySetManual("NOTAMODEL", out SupportDecision rejected));
            Assert.False(rejected.IsSupported);
            Assert.Equal("PH6AGxx", AppConfig.GetString(ModelOverrideStateMachine.ModelKey));
        });
    }

    [Fact]
    public void TheConfiguredManualValueIsOnlyAcceptedAfterF3Passes()
    {
        WithConfig(() =>
        {
            AppConfig.Set(ModelOverrideStateMachine.ModelKey, "NOTAMODEL");
            Assert.Null(ModelOverrideStateMachine.ReadConfiguredManualModel());

            AppConfig.Set(ModelOverrideStateMachine.ModelKey, "PH4PUxx");
            Assert.Equal("PH4PUxx", ModelOverrideStateMachine.ReadConfiguredManualModel());
        });
    }

    [Fact]
    public void EvaluateUsesThePersistedModeAndManualValue()
    {
        WithConfig(() =>
        {
            AppConfig.Set(ModelOverrideStateMachine.ModeKey, "ManualPinned");
            AppConfig.Set(ModelOverrideStateMachine.ModelKey, "PH4PUxx");

            ModelOverrideDecision decision = ModelOverrideStateMachine.Evaluate(SupportDecision.Supported("PH4TRX1"));

            Assert.Equal(ModelOverrideMode.ManualPinned, decision.Mode);
            Assert.Equal("PH4PUxx", decision.EffectiveModel);
            Assert.True(decision.ManualApplied);
        });
    }

    [Fact]
    public void TheEnvironmentOverrideBehavesLikeAManualPin()
    {
        WithEnvironment("PH4PUxx", () =>
        {
            ModelOverrideDecision decision = ModelOverrideStateMachine.Evaluate(SupportDecision.NotInSet("PH6TQxx"));

            Assert.Equal(ModelOverrideMode.ManualPinned, decision.Mode);
            Assert.Equal("PH4PUxx", decision.EffectiveModel);
            Assert.True(decision.ManualApplied);
            Assert.Equal(ModelOverrideReason.ManualAccepted, decision.Reason);
        });
    }

    internal static void WithConfig(Action body)
    {
        string? mode = AppConfig.GetString(ModelOverrideStateMachine.ModeKey);
        string? model = AppConfig.GetString(ModelOverrideStateMachine.ModelKey);
        try
        {
            body();
        }
        finally
        {
            Restore(ModelOverrideStateMachine.ModeKey, mode);
            Restore(ModelOverrideStateMachine.ModelKey, model);
        }
    }

    internal static void WithEnvironment(string? value, Action body)
    {
        string? previous = Environment.GetEnvironmentVariable(Variable);
        try
        {
            Environment.SetEnvironmentVariable(Variable, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, previous);
        }
    }

    static void Restore(string key, string? value)
    {
        if (value is null) AppConfig.Remove(key);
        else AppConfig.Set(key, value);
    }
}
