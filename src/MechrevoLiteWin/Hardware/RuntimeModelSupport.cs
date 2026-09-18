using Probe;

namespace MechrevoLite.Hardware;

/// <summary>
/// 运行时的机型支持判定（D1/D2）：把"身份从哪来"收敛到一处。
///
/// <list type="number">
/// <item><c>LMECHREVO_MODEL_OVERRIDE</c> 注入（T31，UI 审计子进程）——把该值**当作身份**直接做 F3；
/// 越界的注入值即 <see cref="SupportReason.NotInSet"/>（审计要能渲染"不支持"形态）。</item>
/// <item>审计模式且无注入——按最大 UI 渲染（<see cref="SupportReason.Ok"/>），不因读不到 EC 而误判。</item>
/// <item>生产——读 EC 身份 + 持久化的手动覆盖状态机（D2）。</item>
/// </list>
///
/// <para>本类只读（EC 只经只读传输），不含写路径。</para>
/// </summary>
public static class RuntimeModelSupport
{
    public static SupportDecision Current()
    {
        if (ModelOverrideStateMachine.EnvironmentOverride() is { } injected)
            return ModelOverrideStateMachine.ValidateManual(injected);

        if (Program.UiAuditMode) return SupportDecision.Supported("audit");

        try
        {
            if (!AcpiDriverReadTransport.TryOpen(out AcpiDriverReadTransport? transport, out string error) || transport is null)
            {
                Logger.WriteLine("Model identity unavailable: " + error);
                return SupportDecision.Unparsable();
            }
            using (transport)
            {
                SupportDecision auto = ModelSupport.Determine(transport);
                ModelOverrideDecision decision = ModelOverrideStateMachine.EvaluateConfigured(auto);
                return decision.ManualApplied
                    ? ModelOverrideStateMachine.ValidateManual(decision.EffectiveModel)
                    : auto;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Model support determination failed: " + ex.Message);
            return SupportDecision.Unparsable();
        }
    }

    /// <summary>是否被判为**明确不支持**（识别到但不在 24 集合内）。无法判定（Unparsable）不算。</summary>
    public static bool IsPositivelyUnsupported(SupportDecision decision) =>
        decision.Reason == SupportReason.NotInSet;
}
