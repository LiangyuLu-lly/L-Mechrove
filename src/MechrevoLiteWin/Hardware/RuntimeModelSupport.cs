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
    static readonly object IdentityGate = new();
    static ModelIdentity? _identity;            // 最近一次读到的 EC 身份（null = 驱动打不开）
    static string _identityError = "";
    static bool _identityAttempted;
    static DateTime _identityRetryAfterUtc = DateTime.MinValue;

    /// <summary>身份读不到/解析不了时的重试间隔；期间复用上一次的结果，不反复开驱动。</summary>
    internal static readonly TimeSpan IdentityRetryInterval = TimeSpan.FromSeconds(30);

    public static SupportDecision Current()
    {
        if (ModelOverrideStateMachine.EnvironmentOverride() is { } injected)
            return ModelOverrideStateMachine.ValidateManual(injected);

        if (Program.UiAuditMode) return SupportDecision.Supported("audit");

        try
        {
            ModelIdentity? identity = ReadIdentity(out string error);
            if (identity is null)
            {
                Logger.WriteLineIfChanged("model-identity", "Model identity unavailable: " + error);
                return IsServiceServed()
                    ? SupportDecision.Supported("GCU")
                    : SupportDecision.Unparsable();
            }
            // N8: the vendor's criterion, not the 24-code list. A machine the vendor service
            // serves is usable; only a machine it does NOT serve degrades to read-only.
            SupportDecision auto = ModelSupport.Determine(identity, IsServiceServed());
            ModelOverrideDecision decision = ModelOverrideStateMachine.EvaluateConfigured(auto);
            return decision.ManualApplied
                ? ModelOverrideStateMachine.ValidateManual(decision.EffectiveModel)
                : auto;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Model support determination failed: " + ex.Message);
            return SupportDecision.Unparsable();
        }
    }

    /// <summary>
    /// EC 身份（1856/1868）在进程生命周期内不会变：解析成功后永久缓存，之后的判定不再开驱动、
    /// 不再做 EC I/O——能力刷新每次都要问一遍，过去每次都在 UI 线程上开一次 ACPI 驱动。
    /// 驱动打不开或身份解析不了时按 <see cref="IdentityRetryInterval"/> 节流重试。
    /// 「是否被厂商服务服务」不缓存，每次判定都重新看（连接状态会变）。
    /// </summary>
    static ModelIdentity? ReadIdentity(out string error)
    {
        lock (IdentityGate)
        {
            if (_identity is { IsParsed: true } parsed)
            {
                error = "";
                return parsed;
            }
            DateTime now = DateTime.UtcNow;
            if (_identityAttempted && now < _identityRetryAfterUtc)
            {
                error = _identityError;
                return _identity;
            }
            _identityAttempted = true;
            _identityRetryAfterUtc = now + IdentityRetryInterval;
            if (!AcpiDriverReadTransport.TryOpen(out AcpiDriverReadTransport? transport, out string openError) || transport is null)
            {
                _identity = null;
                _identityError = openError;
                error = openError;
                return null;
            }
            using (transport)
            {
                _identity = ModelRegistry.Read(transport);
                _identityError = "";
                error = "";
                return _identity;
            }
        }
    }

    /// <summary>测试接缝：清空身份缓存。</summary>
    internal static void ResetIdentityCacheForTests()
    {
        lock (IdentityGate)
        {
            _identity = null;
            _identityError = "";
            _identityAttempted = false;
            _identityRetryAfterUtc = DateTime.MinValue;
        }
    }

    /// <summary>是否被判为**明确不支持**（识别到但厂商服务未服务该机）。无法判定（Unparsable）不算。</summary>
    public static bool IsPositivelyUnsupported(SupportDecision decision) =>
        decision.Reason == SupportReason.NotInSet;

    /// <summary>
    /// 仪表盘是否进入只读降级。GCU 首连仍在重试时不锁死——EC/ItemSupport 都还没到，
    /// 把「连接中」显示成「机型无法识别」是 beta18 现场误伤。
    /// </summary>
    public static bool ShouldDegradeToReadOnly(SupportDecision decision, bool gcuFirstConnectInProgress) =>
        !gcuFirstConnectInProgress && !decision.IsSupported;

    /// <summary>
    /// First MQTT handshake (ConnectionGeneration==0, never connected) is still in progress,
    /// whether or not <paramref name="isReconnecting"/> has flipped yet.
    /// </summary>
    public static bool IsGcuFirstConnectInProgress(bool isReconnecting, int connectionGeneration) =>
        connectionGeneration == 0;

    /// <summary>
    /// 厂商服务是否在服务本机（N8 的判据来源）。任一为真即算：
    /// 服务已连（MQTT 握手成功）或服务写入的 <c>ItemSupport</c> 有内容。
    /// 两者都拿不到即"未服务"，保留 D1 的只读降级。
    /// </summary>
    internal static bool IsServiceServed()
    {
        try
        {
            if (Program.hw is { IsConnected: true }) return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Service-served probe (MQTT) failed: " + ex.Message);
        }

        try
        {
            return MechrevoDeviceCapabilities.HasItemSupportContent();
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Service-served probe (ItemSupport) failed: " + ex.Message);
            return false;
        }
    }
}
