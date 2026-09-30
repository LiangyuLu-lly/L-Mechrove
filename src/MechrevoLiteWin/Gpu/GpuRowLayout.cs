namespace MechrevoLite.Gpu;

/// <summary>
/// 显卡行的分段布局（§7）。一行分段按钮，标题固定「显卡模式」；布局只由能力判定决定，
/// 主界面与托盘用同一个值。
/// </summary>
public enum GpuRowLayout
{
    /// <summary>无 MUX / 服务档位未知 / 代际未知 / 无独显：整行隐藏。</summary>
    Hidden,

    /// <summary>GTX 10/16、RTX 20：自动选择 · 独显优先（NVIDIA 全局首选 GPU，无需重启）。</summary>
    NvPreference,

    /// <summary>30 系、40 两模档、50 无核显目标：标准 · 直连（都要重启）。</summary>
    Mux2,

    /// <summary>40 三模档、50 非热切换：集显 · 标准 · 直连（都要重启）。</summary>
    Mux3,

    /// <summary>50 热切换：集显 · 标准 · 直连；集显↔标准热切换，进出直连要重启。</summary>
    HotSwap,
}

/// <summary>布局判定（纯函数）。输入是 <see cref="Hardware.MechrevoHw"/> 的四个供货谓词。</summary>
public static class GpuRowLayouts
{
    public static GpuRowLayout Resolve(bool nvPreference, bool modeSwitch, bool hotSwap, bool igpuMuxTarget)
    {
        if (nvPreference) return GpuRowLayout.NvPreference;
        if (!modeSwitch) return GpuRowLayout.Hidden;
        if (hotSwap) return GpuRowLayout.HotSwap;
        return igpuMuxTarget ? GpuRowLayout.Mux3 : GpuRowLayout.Mux2;
    }

    /// <summary>该布局是否有「集显」分段。</summary>
    public static bool HasIgpuSegment(GpuRowLayout layout) =>
        layout is GpuRowLayout.Mux3 or GpuRowLayout.HotSwap;

    /// <summary>该布局是否有第二、三两段（标准/直连 或 自动选择/独显优先）。</summary>
    public static bool HasPrimarySegments(GpuRowLayout layout) => layout != GpuRowLayout.Hidden;

    /// <summary>该布局的切换是否依赖 MUX 路由回读（10/20 看的是驱动首选 GPU，不看路由）。</summary>
    public static bool UsesRouteReadback(GpuRowLayout layout) =>
        layout is GpuRowLayout.Mux2 or GpuRowLayout.Mux3 or GpuRowLayout.HotSwap;
}
