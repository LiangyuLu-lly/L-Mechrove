using MechrevoLite.Hardware;

namespace MechrevoLite.Fan;

/// <summary>机型（轴 1）到风扇表目录的解析结果类别。</summary>
public enum FanTableResolutionKind
{
    /// <summary>该机型在 <c>UserFanTables</c> 里有自己的目录——用目录里的 JSON。</summary>
    Directory,

    /// <summary>没有该机型的目录：回退到 23 张通用 flat 表（N8）。</summary>
    GenericFlatTables,

    /// <summary>既没有该机型的目录、也没有通用 flat 表：按厂商行为回退"从 EC 读默认表"。</summary>
    EcdDefaults,

    /// <summary>身份读不到/解析不出——没有任何目录可用，也不得猜测。</summary>
    Unparsable,
}

/// <summary>一次机型 → 风扇表目录解析的结果。</summary>
public sealed record FanTableResolution(
    FanTableResolutionKind Kind,
    string Model,
    string? Directory,
    bool InSupportedSet,
    string Reason)
{
    /// <summary>是否解析到了本机型自己的目录。</summary>
    public bool IsDirectory => Kind == FanTableResolutionKind.Directory;
}

/// <summary>
/// 机型（轴 1）→ 风扇表目录的**唯一规则**，逐条等价于厂商
/// <c>FanTable_Manager1p5_CML</c> 构造函数（<c>FanTable_Manager1p5_CML.cs:45-51</c>）：
/// <code>
/// int id = EcCtrl.GetProject2ExID(EcCtrl.GetProjectIdFromEC());
/// string name = Enum.GetName(typeof(ProjectID), id);
/// m_path = m_path + "\\" + name;
/// </c>
/// <see cref="ModelRegistry"/> 已把这条规则的结果放进 <see cref="ModelIdentity.ProjectId"/>，
/// 本类只负责"目录名就是这个字符串"这一层，**不做任何跨机型回退**。
///
/// <para>目录缺失（例如 <c>PH6TQxx</c>/<c>PH6AGxx</c> 这两个只有枚举成员、没有目录的代号）时按厂商
/// 行为回退"从 EC 读默认表"（<c>FanTable_Manager1p5_CML.FanTable_Refresh</c>，见 <c>:84-87,:600-658</c>）。
/// 只读：本类不写 EC，也不开设备。</para>
/// </summary>
public static class FanTableResolver
{
    /// <summary>风扇表根目录名（厂商 <c>m_path</c> 的尾巴）。</summary>
    public const string RootFolderName = "UserFanTables";

    /// <summary>
    /// 解析机型自己的风扇表目录。目录不存在时回退 <see cref="FanTableResolutionKind.EcdDefaults"/>，
    /// 返回的 <see cref="FanTableResolution.Directory"/> 恒为 <c>null</c>——调用方拿不到别的机型路径。
    /// </summary>
    public static FanTableResolution Resolve(string fanTablesRoot, ModelIdentity identity, IReadOnlySet<string> platformCodes)
    {
        ArgumentNullException.ThrowIfNull(fanTablesRoot);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(platformCodes);

        if (!identity.IsParsed)
            return new FanTableResolution(
                FanTableResolutionKind.Unparsable, identity.ProjectId, null, false,
                "model identity is not parseable from the EC");

        string name = identity.ProjectId;
        bool inSupportedSet = platformCodes.Contains(name);

        string candidate = Path.Combine(fanTablesRoot, name);
        if (Directory.Exists(candidate))
            return new FanTableResolution(
                FanTableResolutionKind.Directory, name, candidate, inSupportedSet,
                "resolved to the model's own fan-table directory");

        // N8: no per-model directory -> fall back to the 23 generic flat tables, which is what the
        // vendor uses for platforms outside the 24 per-model codes (e.g. GK7NXXR). Only when those
        // are absent too do we fall back to reading the default table from the EC.
        if (HasGenericFlatTables(fanTablesRoot))
            return new FanTableResolution(
                FanTableResolutionKind.GenericFlatTables, name, fanTablesRoot, inSupportedSet,
                "model has no fan-table directory; using the generic flat fan tables");

        return new FanTableResolution(
            FanTableResolutionKind.EcdDefaults, name, null, inSupportedSet,
            inSupportedSet
                ? "model directory is missing; read the default fan table from the EC"
                : "model has no fan-table directory; read the default fan table from the EC");
    }

    /// <summary>
    /// 通用 flat 表是否存在（N8）。厂商用这 23 张表服务没有机型专属目录的平台
    /// （<c>DefaultFanTable_{Gaming,Office,Turbo}</c> + <c>M1T1..M4T5</c>）。
    /// </summary>
    internal static bool HasGenericFlatTables(string fanTablesRoot)
    {
        if (string.IsNullOrWhiteSpace(fanTablesRoot) || !Directory.Exists(fanTablesRoot)) return false;
        return File.Exists(Path.Combine(fanTablesRoot, "DefaultFanTable_Gaming.json"))
            && File.Exists(Path.Combine(fanTablesRoot, "M1T1.json"));
    }
}
