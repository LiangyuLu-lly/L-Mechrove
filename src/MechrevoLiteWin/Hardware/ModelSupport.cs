using Probe;

namespace MechrevoLite.Hardware;

/// <summary>F3 判定原因；**只有 <see cref="Ok"/> 表示支持**，其余一律降级为只读。</summary>
public enum SupportReason
{
    /// <summary>身份可解析，且厂商服务在该机可用（服务在跑/已连/服务写入的 ItemSupport 有内容）。</summary>
    Ok,

    /// <summary>身份读不到或叫不出厂商枚举名。</summary>
    Unparsable,

    /// <summary>身份可解析，但厂商服务未服务该机（服务缺失/未连/ItemSupport 空）。</summary>
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
/// F3（fail-closed）支持判定——**厂商服务口径**（N8 订正）。
///
/// <para><b>订正记录（amended by owner）</b>：原判据是"身份可解析 **且** 展开后的平台代号落在
/// <c>model-registry.json</c> 的 24 个平台代号内"。现场证据（真机 30 系 <c>Taitan Series GM7TG0M</c>，
/// 识别到 <c>GK7NXXR</c>）显示厂商官方控制台在该机上显卡模式/风扇/温度全部可用，而我们的 24 码判据
/// 把它锁成只读 —— **我们比厂商更严**，违背"厂商能做的我们也要能做"的目标。该判据过窄，已订正。</para>
///
/// <para>现在的判据贴近厂商：**身份可解析 且 厂商服务在该机可用**（服务在跑/已连/服务写入的
/// <c>ItemSupport</c> 有内容）即接受；没有机型专属风扇表目录时回退到 23 张通用 flat 表
/// （<c>DefaultFanTable_{Gaming,Office,Turbo}</c> + <c>M1T1..M4T5</c>），而不是强制只读。</para>
///
/// <para><b>保留 D1 的真实场景</b>：服务未服务该机（服务缺失/未连/<c>ItemSupport</c> 空）仍须只读降级 ——
/// 不得把"真不支持"变成"假装支持"。身份读不到仍是 <see cref="SupportReason.Unparsable"/>。</para>
///
/// <para>身份只由 EC 1856/1868 决定。服务写入注册表的 <c>BIOS_PROJECT_ID</c> 是另一套 ID 空间
/// （映射关系未建立），至多作佐证，**绝不参与判定**；其不一致不得翻转结论。</para>
/// </summary>
public static class ModelSupport
{
    static readonly Lazy<ModelRegistryData> RegistryData = new(ModelRegistryData.Load);

    /// <summary>读 EC → 判定。EC 读不到即 <see cref="SupportReason.Unparsable"/>。</summary>
    public static SupportDecision Determine(IEcReadTransport transport, bool serviceServed) =>
        Determine(ModelRegistry.Read(transport), RegistryData.Value.PlatformCodeSet, serviceServed);

    /// <summary>
    /// 佐证重载。<paramref name="corroboratingBiosProjectId"/> 只应写日志/诊断，**被本判定刻意忽略**：
    /// 保留这个调用形状是为了让"佐证源与 EC 身份不一致时判定不变"这条规则可被测试锁定。
    /// </summary>
    public static SupportDecision Determine(
        ModelIdentity identity, IReadOnlySet<string> supportedCodes, string? corroboratingBiosProjectId, bool serviceServed) =>
        Determine(identity, supportedCodes, serviceServed);

    /// <summary>
    /// 厂商服务口径的判定。<paramref name="serviceServed"/> 由调用方从厂商服务状态得出
    /// （服务在跑/已连/服务写入的 <c>ItemSupport</c> 有内容）。<paramref name="supportedCodes"/> 只用于
    /// 诊断与"有专属目录"的区分，**不再是否决条件**。
    /// </summary>
    public static SupportDecision Determine(ModelIdentity identity, IReadOnlySet<string> supportedCodes, bool serviceServed)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(supportedCodes);

        if (!identity.IsParsed)
        {
            // 现场 yilong15 Pro GM5HG0A：GCU 已连接但 EC 1856 读不到 → 旧逻辑 Unparsable 只读。
            // 北极星：官方在服务这台机，EC 身份失败不得把整机锁死。
            if (!serviceServed) return SupportDecision.Unparsable();
            string fallback = identity.BiosProjectId is { Length: > 0 } bios
                && bios != ModelIdentity.UnknownName
                ? bios
                : "GCU";
            return SupportDecision.Supported(fallback);
        }
        return serviceServed
            ? SupportDecision.Supported(identity.ProjectId)
            : SupportDecision.NotInSet(identity.ProjectId);
    }
}
