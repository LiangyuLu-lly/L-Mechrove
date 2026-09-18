using Probe;

namespace MechrevoLite.Hardware;

/// <summary>F3 判定原因；**只有 <see cref="Ok"/> 表示支持**，其余一律降级为只读。</summary>
public enum SupportReason
{
    /// <summary>身份可解析且展开后的平台代号落在 24 机型集合内。</summary>
    Ok,

    /// <summary>身份读不到或叫不出厂商枚举名。</summary>
    Unparsable,

    /// <summary>身份可解析，但该平台代号不在 24 机型集合内（枚举成员却没有风扇表目录也算）。</summary>
    NotInSet,
}

/// <summary>F3 判定结果。<see cref="IsSupported"/> 恒等于 <c>Reason == Ok</c>，两者不可能互相矛盾。</summary>
public sealed record SupportDecision(bool IsSupported, SupportReason Reason, string ProjectId)
{
    public static SupportDecision Supported(string projectId) => new(true, SupportReason.Ok, projectId);

    public static SupportDecision Unparsable() =>
        new(false, SupportReason.Unparsable, ModelIdentity.UnknownName);

    public static SupportDecision NotInSet(string projectId) => new(false, SupportReason.NotInSet, projectId);
}

/// <summary>
/// F3（fail-closed）支持判定——**轴 1 集合口径**。
///
/// <para>支持当且仅当：身份可解析（<see cref="ModelIdentity.IsParsed"/>）**且**展开后的平台代号落在
/// <c>model-registry.json</c> 的 24 个平台代号内。没有"读不到就按默认值放行"的分支：
/// 每个失败路径都映射到 <see cref="SupportReason.Unparsable"/> 或 <see cref="SupportReason.NotInSet"/>。</para>
///
/// <para>身份只由 EC 1856/1868 决定。服务写入注册表的 <c>BIOS_PROJECT_ID</c> 是另一套 ID 空间
/// （映射关系未建立），至多作佐证，**绝不参与判定**；其不一致不得翻转结论。</para>
/// </summary>
public static class ModelSupport
{
    static readonly Lazy<ModelRegistryData> RegistryData = new(ModelRegistryData.Load);

    /// <summary>读 EC → 判定。EC 读不到即 <see cref="SupportReason.Unparsable"/>。</summary>
    public static SupportDecision Determine(IEcReadTransport transport) =>
        Determine(ModelRegistry.Read(transport), RegistryData.Value.PlatformCodeSet);

    /// <summary>
    /// 佐证重载。<paramref name="corroboratingBiosProjectId"/> 只应写日志/诊断，**被本判定刻意忽略**：
    /// 保留这个调用形状是为了让"佐证源与 EC 身份不一致时判定不变"这条规则可被测试锁定。
    /// </summary>
    public static SupportDecision Determine(
        ModelIdentity identity, IReadOnlySet<string> supportedCodes, string? corroboratingBiosProjectId) =>
        Determine(identity, supportedCodes);

    public static SupportDecision Determine(ModelIdentity identity, IReadOnlySet<string> supportedCodes)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(supportedCodes);

        if (!identity.IsParsed) return SupportDecision.Unparsable();
        return supportedCodes.Contains(identity.ProjectId)
            ? SupportDecision.Supported(identity.ProjectId)
            : SupportDecision.NotInSet(identity.ProjectId);
    }
}
