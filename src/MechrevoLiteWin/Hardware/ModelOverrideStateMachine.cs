namespace MechrevoLite.Hardware;

/// <summary>手动覆盖（D2）的三态。</summary>
public enum ModelOverrideMode
{
    /// <summary>自动识别成功即生效；手动值不起作用。</summary>
    Auto,

    /// <summary>显式保持：手动值生效并跨重启保持（前提是 F3 通过）。</summary>
    ManualPinned,

    /// <summary>自动失败时的兜底：只在自动不可用时生效，自动一恢复即让位。</summary>
    ManualFallback,
}

/// <summary>本次判定的原因，便于 UI 提示"当前是自动 / 被手动钉住 / 手动被拒"。</summary>
public enum ModelOverrideReason
{
    AutoOk,
    AutoUnsupported,
    ManualAccepted,
    ManualRejected,
}

/// <summary>一次覆盖判定的结果。<see cref="ManualApplied"/> 为真才表示正在按手动值工作。</summary>
public sealed record ModelOverrideDecision(
    ModelOverrideMode Mode,
    string? EffectiveModel,
    bool ManualApplied,
    ModelOverrideReason Reason);

/// <summary>
/// 手动覆盖的显式状态机 + 持久化（D2）。
///
/// <para>规则：自动优先；<see cref="ModelOverrideMode.ManualPinned"/> 下手动值生效；
/// <see cref="ModelOverrideMode.ManualFallback"/> 只在自动失败时生效、自动恢复即让位。
/// **非法手动值一律被拒**（F3 不通过就不接受），且绝不写盘。</para>
///
/// <para>持久化只走 <see cref="AppConfig"/>（其 <c>WriteAtomic</c> 负责原子替换与 .bak）。
/// <c>LMECHREVO_MODEL_OVERRIDE</c> 与状态机一致：非空即等价于一次 ManualPinned，非法值同样被拒。</para>
/// </summary>
public static class ModelOverrideStateMachine
{
    public const string ModeKey = "model_override_mode";
    public const string ModelKey = "model_override";
    public const string OverrideVariable = "LMECHREVO_MODEL_OVERRIDE";

    static readonly Lazy<IReadOnlySet<string>> SupportedCodes =
        new(() => ModelRegistryData.Load().PlatformCodeSet);

    /// <summary>手动值是否可解析为厂商 ProjectID 名或落在 24 机型集合内。空白即 <see cref="SupportReason.Unparsable"/>。</summary>
    public static SupportDecision ValidateManual(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return SupportDecision.Unparsable();
        string code = model.Trim();
        // Auto ModelSupport already accepts parseable service-served names; PH6AGxx is enum member 5894.
        return SupportedCodes.Value.Contains(code) || ModelRegistry.IsKnownProjectName(code)
            ? SupportDecision.Supported(code)
            : SupportDecision.NotInSet(code);
    }

    public static ModelOverrideMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "manualpinned" or "pinned" => ModelOverrideMode.ManualPinned,
        "manualfallback" or "fallback" => ModelOverrideMode.ManualFallback,
        _ => ModelOverrideMode.Auto,
    };

    public static ModelOverrideDecision Decide(ModelOverrideMode mode, SupportDecision auto, SupportDecision manual)
    {
        ArgumentNullException.ThrowIfNull(auto);
        ArgumentNullException.ThrowIfNull(manual);

        if (mode == ModelOverrideMode.Auto) return FollowAuto(mode, auto);

        if (!manual.IsSupported)
        {
            // 非法手动值被拒：仍然按自动结果走，但原因如实标 ManualRejected（UI 要能提示）。
            ModelOverrideDecision followed = FollowAuto(mode, auto);
            return followed with { Reason = ModelOverrideReason.ManualRejected };
        }

        if (mode == ModelOverrideMode.ManualPinned)
            return new ModelOverrideDecision(mode, manual.ProjectId, true, ModelOverrideReason.ManualAccepted);

        // ManualFallback：自动成功时让位，自动失败时兜底。
        return auto.IsSupported
            ? FollowAuto(mode, auto)
            : new ModelOverrideDecision(mode, manual.ProjectId, true, ModelOverrideReason.ManualAccepted);
    }

    /// <summary>按持久化的模式 + 持久化的手动值判定（不含环境变量）。</summary>
    public static ModelOverrideDecision EvaluateConfigured(SupportDecision auto) =>
        Decide(ParseMode(AppConfig.GetString(ModeKey)), auto, ValidateManual(ReadConfiguredManualModel()));

    /// <summary>
    /// 生产入口：环境变量（UI 审计子进程注入）优先于持久化配置，二者都过状态机。
    /// </summary>
    public static ModelOverrideDecision Evaluate(SupportDecision auto)
    {
        string? injected = EnvironmentOverride();
        if (injected is not null)
            return Decide(ModelOverrideMode.ManualPinned, auto, ValidateManual(injected));
        return EvaluateConfigured(auto);
    }

    public static string? EnvironmentOverride()
    {
        string? value = Environment.GetEnvironmentVariable(OverrideVariable);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>持久化的手动值；F3 不通过时返回 null（**不生效**）。</summary>
    public static string? ReadConfiguredManualModel()
    {
        SupportDecision decision = ValidateManual(AppConfig.GetString(ModelKey));
        return decision.IsSupported ? decision.ProjectId : null;
    }

    public static ModelOverrideMode ReadConfiguredMode() => ParseMode(AppConfig.GetString(ModeKey));

    public static void SetMode(ModelOverrideMode mode) => AppConfig.Set(ModeKey, mode.ToString());

    /// <summary>
    /// 保存手动值：只有 F3 通过才写盘。非法值返回 false 并**不产生任何写入**（返回值里的
    /// <see cref="SupportDecision"/> 说明原因：Unparsable / NotInSet）。
    /// </summary>
    public static bool TrySetManual(string? model, out SupportDecision decision)
    {
        decision = ValidateManual(model);
        if (!decision.IsSupported) return false;
        AppConfig.Set(ModelKey, decision.ProjectId);
        return true;
    }

    static ModelOverrideDecision FollowAuto(ModelOverrideMode mode, SupportDecision auto) =>
        auto.IsSupported
            ? new ModelOverrideDecision(mode, auto.ProjectId, false, ModelOverrideReason.AutoOk)
            : new ModelOverrideDecision(mode, null, false, ModelOverrideReason.AutoUnsupported);
}
