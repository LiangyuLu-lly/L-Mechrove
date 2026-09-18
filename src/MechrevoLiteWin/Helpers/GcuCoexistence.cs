namespace MechrevoLite.Helpers;

/// <summary>外来 GCU（厂商服务 / 13688 占用者）的共存判定结果。</summary>
public enum GcuCoexistenceKind
{
    /// <summary>没有外来 GCU 迹象。</summary>
    None,

    /// <summary>存在外来 GCU 服务（<c>GCUBridge</c>）。</summary>
    ForeignService,

    /// <summary>13688 被外来进程占用。</summary>
    PortOwner,

    /// <summary>两者都有。</summary>
    Both,
}

/// <summary>
/// 外来 GCU 共存处置（C3 重定义，round 5）。
///
/// <para>事实修正：先前「卸官方台报 SEVERE → 共存冲突」是**误推**，那条只是示例、不是真缺陷。
/// 现在的契约是：检测到外来 GCU 环境时**提示用户自行移除官方控制台应用**（可见提示），
/// 绝不静默删除；服务的接管（先卸后装 + 回滚护栏）由安装器承担。</para>
/// </summary>
internal static class GcuCoexistence
{
    /// <summary>外来 GCU 服务名（厂商载荷注册名）。</summary>
    internal const string VendorServiceName = "GCUBridge";

    /// <summary>按「外来服务 × 13688 占用者」分类。</summary>
    internal static GcuCoexistenceKind Classify(bool foreignService, bool foreignPortOwner) =>
        (foreignService, foreignPortOwner) switch
        {
            (true, true) => GcuCoexistenceKind.Both,
            (true, false) => GcuCoexistenceKind.ForeignService,
            (false, true) => GcuCoexistenceKind.PortOwner,
            _ => GcuCoexistenceKind.None,
        };

    /// <summary>是否必须向用户显示「自行移除官方控制台」的提示。</summary>
    internal static bool RequiresConsoleRemovalPrompt(GcuCoexistenceKind kind) =>
        kind != GcuCoexistenceKind.None;

    /// <summary>可见提示文案：要求用户自行卸载官方控制台，绝不宣称由我们删除。</summary>
    internal static string BuildConsoleRemovalPrompt(GcuCoexistenceKind kind) =>
        "检测到机器上仍有厂商的 GCU 环境（" + kind + "）。\r\n" +
        "L-Mechrevo 不会替你静默删除厂商的软件。请手动卸载「官方控制台」应用" +
        "（设置 → 应用 → 已安装的应用），然后重新启动 L-Mechrevo。\r\n" +
        "GCU 服务的接管（先卸后装 + 回滚护栏）由安装器负责。";
}
