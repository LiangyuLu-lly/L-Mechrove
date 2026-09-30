using NvAPIWrapper.DRS;
using NvAPIWrapper.Native.Exceptions;
using NvAPIWrapper.Native.General;

namespace MechrevoLite.Gpu;

/// <summary>
/// NVIDIA 控制面板「首选图形处理器」的全局取值（GTX 10/16、RTX 20 机器上官方唯一的显卡选项）。
/// </summary>
public enum NvPreferredGpu
{
    /// <summary>读不到 / 不可识别。</summary>
    Unknown,

    /// <summary>自动选择（驱动默认）。</summary>
    AutoSelect,

    /// <summary>高性能 NVIDIA 处理器。</summary>
    HighPerformance,

    /// <summary>集成显卡（官方开关没有这一档，界面显示为未确认）。</summary>
    Integrated,
}

/// <summary>
/// 首选 GPU 的解码与**只读**回读。厂商 10/20 服务（1.0.2.47）收到
/// <c>NV_CTRL_PANEL_AUTOSELECT</c> / <c>NV_CTRL_PANEL_HIGHPERFORMANCE</c> 后经 <c>NVControlSetting.dll</c>
/// 写 NVIDIA 驱动的 DRS 全局配置；该 DLL 内含 <c>SHIM_RENDERING_MODE</c> 的设置 id（0x10F9DC81）。
/// 是否生效只认驱动里读回来的值。
///
/// <para>本类只允许 <c>CreateAndLoad</c> / <c>GetSetting</c>：不写 DRS、不保存配置（IL 守卫测试锁住）。</para>
/// </summary>
public static class NvPreferredGpuReader
{
    /// <summary>NVAPI <c>SHIM_RENDERING_MODE_ID</c>（Optimus 首选 GPU）。</summary>
    public const uint ShimRenderingModeId = 0x10F9DC81;

    const uint EnableBit = 0x00000001;
    const uint AutoSelectBit = 0x00000010;

    /// <summary>测试接缝：非 null 时由它代替驱动读取（生产代码从不设置）。</summary>
    internal static Func<NvPreferredGpu>? DriverOverride { get; set; }

    /// <summary>
    /// 解码 <c>SHIM_RENDERING_MODE</c>：未设置 = 驱动默认自动选择；置 <c>ENABLE</c> 位 = 高性能 NVIDIA；
    /// 只有 <c>AUTO_SELECT</c> 位 = 自动选择；两位都不置 = 集成显卡。<c>OVERRIDE_BIT</c> 等高位不影响判定。
    /// </summary>
    public static NvPreferredGpu Decode(uint? value)
    {
        if (value is not uint raw) return NvPreferredGpu.AutoSelect;
        if ((raw & EnableBit) != 0) return NvPreferredGpu.HighPerformance;
        if ((raw & AutoSelectBit) != 0) return NvPreferredGpu.AutoSelect;
        return NvPreferredGpu.Integrated;
    }

    /// <summary>服务 <c>Setting/Status.DGpu</c> 串 → 取值（只作辅助，不是生效证据）。</summary>
    public static NvPreferredGpu FromServiceStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return NvPreferredGpu.Unknown;
        string text = status.Trim().ToUpperInvariant();
        if (text.Contains("NV_CTRL_PANEL_HIGHPERFORMANCE", StringComparison.Ordinal)) return NvPreferredGpu.HighPerformance;
        if (text.Contains("NV_CTRL_PANEL_AUTOSELECT", StringComparison.Ordinal)) return NvPreferredGpu.AutoSelect;
        return NvPreferredGpu.Unknown;
    }

    /// <summary>
    /// 从 NVIDIA 驱动读当前全局首选 GPU。只在 10/20 布局下调用（这一代没有热切换，
    /// 初始化 NVAPI 唤醒一下独显没有副作用）；失败返回 <see cref="NvPreferredGpu.Unknown"/>。
    /// </summary>
    public static NvPreferredGpu ReadFromDriver()
    {
        if (DriverOverride is { } factory) return factory();
        try
        {
            using DriverSettingsSession session = DriverSettingsSession.CreateAndLoad();
            uint? value;
            try
            {
                // NvAPIWrapper 在 SettingNotFound 时返回 null 而不是抛异常（真机探针实测）；
                // 全局配置没有这一项 = 驱动默认（自动选择）。
                DriverSettingsProfile? profile = session.CurrentGlobalProfile;
                if (profile is null)
                {
                    Logger.WriteLineThrottled("nv-preferred-gpu-read", "NVIDIA preferred GPU read: no global DRS profile", 60_000);
                    return NvPreferredGpu.Unknown;
                }
                ProfileSetting? setting = profile.GetSetting(ShimRenderingModeId);
                value = setting?.CurrentValue is { } current
                    ? Convert.ToUInt32(current, System.Globalization.CultureInfo.InvariantCulture)
                    : null;
            }
            catch (NVIDIAApiException ex) when (ex.Status == Status.SettingNotFound)
            {
                value = null;   // 旧版包装库会抛异常，语义同上
            }
            NvPreferredGpu decoded = Decode(value);
            Logger.WriteLineIfChanged("nv-preferred-gpu",
                $"NVIDIA preferred GPU (DRS 0x{ShimRenderingModeId:X8}) = {(value is uint v ? "0x" + v.ToString("X8") : "unset")} -> {decoded}");
            return decoded;
        }
        catch (Exception ex)
        {
            Logger.WriteLineThrottled("nv-preferred-gpu-read", "NVIDIA preferred GPU read failed: " + ex.Message, 60_000);
            return NvPreferredGpu.Unknown;
        }
    }
}

/// <summary>
/// 首选 GPU 回读缓存（10/20 布局的界面高亮用）。读驱动要初始化 NVAPI，不在每次重画时读：
/// 只在显卡行出现、切换完成、主窗打开时显式刷新。
/// </summary>
public static class NvPreferredGpuMonitor
{
    static int _last = (int)NvPreferredGpu.Unknown;
    static int _refreshing;

    /// <summary>回读结果变化（可能在后台线程触发）。</summary>
    public static event Action? Changed;

    public static NvPreferredGpu Last => (NvPreferredGpu)Volatile.Read(ref _last);

    /// <summary>记下一次读数；变化时通知。</summary>
    public static void Store(NvPreferredGpu value)
    {
        int previous = Interlocked.Exchange(ref _last, (int)value);
        if (previous == (int)value) return;
        try { Changed?.Invoke(); }
        catch (Exception ex) { Logger.WriteLine("NVIDIA preferred GPU change handler failed: " + ex.Message); }
    }

    /// <summary>后台读一次驱动（同一时间只跑一个）。</summary>
    public static void RefreshInBackground()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try { Store(NvPreferredGpuReader.ReadFromDriver()); }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
        });
    }

    /// <summary>测试接缝：清空缓存。</summary>
    internal static void ResetForTests() => Interlocked.Exchange(ref _last, (int)NvPreferredGpu.Unknown);
}
