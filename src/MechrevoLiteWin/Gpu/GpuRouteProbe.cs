using System.ComponentModel;
using System.Runtime.InteropServices;
using MechrevoLite.Display;

namespace MechrevoLite.Gpu;

/// <summary>内屏此刻接在哪块显卡上（MUX 的硬件事实）。</summary>
public enum PanelAdapterKind
{
    /// <summary>找不到内屏目标 / CCD 读取失败 / 适配器不可识别。</summary>
    Unknown,

    /// <summary>Intel / AMD 核显驱动内屏。</summary>
    Integrated,

    /// <summary>NVIDIA 独显驱动内屏（独显直连）。</summary>
    Discrete,
}

/// <summary>NVIDIA 显示设备是否在位（核显-only / 热切换的硬件事实）。</summary>
public enum DgpuPresence
{
    /// <summary>CfgMgr 失败，或设备在但处于其它问题态（过渡中）。</summary>
    Unknown,

    /// <summary>设备在位且驱动已启动：程序可以用独显。</summary>
    Present,

    /// <summary>设备在位但被禁用（问题码 22）。</summary>
    Disabled,

    /// <summary>总线上没有在场的 NVIDIA 显示设备。</summary>
    Absent,
}

/// <summary>由硬件回读推出的显卡路由。与服务上报的「目标值」无关。</summary>
public enum GpuRoute
{
    Unknown,
    IgpuOnly,
    Hybrid,
    Direct,
}

/// <summary>一次硬件回读：内屏适配器 + 独显在位。</summary>
public readonly record struct GpuRouteReadback(PanelAdapterKind Panel, DgpuPresence Dgpu)
{
    public static readonly GpuRouteReadback Unavailable = new(PanelAdapterKind.Unknown, DgpuPresence.Unknown);

    public GpuRoute Route => GpuRouteInference.Infer(Panel, Dgpu);

    public override string ToString() => $"panel={Panel} dgpu={Dgpu} route={Route}";
}

/// <summary>回读 → 路由的纯函数（§4.5）。不碰硬件，测试直接调用。</summary>
public static class GpuRouteInference
{
    public static GpuRoute Infer(PanelAdapterKind panel, DgpuPresence dgpu) => panel switch
    {
        PanelAdapterKind.Discrete => GpuRoute.Direct,
        PanelAdapterKind.Integrated => dgpu switch
        {
            DgpuPresence.Present => GpuRoute.Hybrid,
            DgpuPresence.Disabled or DgpuPresence.Absent => GpuRoute.IgpuOnly,
            _ => GpuRoute.Unknown,
        },
        _ => GpuRoute.Unknown,
    };

    /// <summary>路由 → 界面模式值（0 集显 / 1 标准 / 2 直连）；Unknown 为 -1。</summary>
    public static int ToGpuMode(GpuRoute route) => route switch
    {
        GpuRoute.IgpuOnly => Hardware.MechrevoService.GpuIGpu,
        GpuRoute.Hybrid => Hardware.MechrevoService.GpuStandard,
        GpuRoute.Direct => Hardware.MechrevoService.GpuDgpu,
        _ => -1,
    };

    /// <summary>界面模式值 → 路由；自动与未知值为 Unknown。</summary>
    public static GpuRoute FromGpuMode(int mode) => mode switch
    {
        Hardware.MechrevoService.GpuIGpu => GpuRoute.IgpuOnly,
        Hardware.MechrevoService.GpuStandard => GpuRoute.Hybrid,
        Hardware.MechrevoService.GpuDgpu => GpuRoute.Direct,
        _ => GpuRoute.Unknown,
    };

    /// <summary>适配器设备路径 → 核显/独显：含 <c>VEN_10DE</c> 为独显，<c>VEN_8086</c>/<c>VEN_1002</c> 为核显。</summary>
    public static PanelAdapterKind ClassifyAdapterPath(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return PanelAdapterKind.Unknown;
        if (devicePath.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) return PanelAdapterKind.Discrete;
        if (devicePath.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) ||
            devicePath.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase))
            return PanelAdapterKind.Integrated;
        return PanelAdapterKind.Unknown;
    }

    /// <summary>
    /// NVRAM 显示模式字节（厂商服务写的 MUX 目标）→ 路由。Intel：2 直连 / 4 混合 / 1 核显；
    /// AMD：1 直连 / 0 混合 / 2 核显；255 = 不支持；其它值不认。编码出处：S40 MySettingManager.cs:1593-1628。
    /// </summary>
    public static GpuRoute? DecodeOemDisplayMode(byte value, bool isAmd) => (isAmd, value) switch
    {
        (false, 2) => GpuRoute.Direct,
        (false, 4) => GpuRoute.Hybrid,
        (false, 1) => GpuRoute.IgpuOnly,
        (true, 1) => GpuRoute.Direct,
        (true, 0) => GpuRoute.Hybrid,
        (true, 2) => GpuRoute.IgpuOnly,
        _ => null,
    };

    internal static bool IsInternalOutput(DisplayNative.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY technology) =>
        technology is DisplayNative.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
            or DisplayNative.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED
            or DisplayNative.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS
            or DisplayNative.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED;
}

/// <summary>
/// 显卡路由的**只读**硬件回读（§4）。两件事：
/// <list type="number">
/// <item>内屏接在哪块显卡上：<c>QueryDisplayConfig(QDC_ALL_PATHS)</c> + <c>GET_ADAPTER_NAME</c>。
///   必须用 ALL_PATHS：内屏没点亮时活动路径里没有它。</item>
/// <item>NVIDIA 显示设备是否在位：CfgMgr（设备树），**不用 NVML / NVAPI / LHM**——
///   它们会唤醒独显，还会占住句柄让热切换失败。</item>
/// </list>
/// 全部只读：不写 EC、不写固件变量、不改设备状态。
/// </summary>
public static class GpuRouteProbe
{
    /// <summary>测试接缝：非 null 时整份回读由它给出（生产代码从不设置）。</summary>
    internal static Func<GpuRouteReadback>? Override { get; set; }

    /// <summary>读一次完整回读。失败的一半返回 Unknown，不抛异常。</summary>
    public static GpuRouteReadback Read()
    {
        if (Override is { } factory) return factory();
        return new GpuRouteReadback(ReadInternalPanelAdapter(), ReadDgpuPresence());
    }

    /// <summary>只读独显在位（热切换确认用，比完整回读少一次 CCD 查询）。</summary>
    public static DgpuPresence ReadPresence() =>
        Override is { } factory ? factory().Dgpu : ReadDgpuPresence();

    const int ErrorInsufficientBuffer = 122;

    static PanelAdapterKind ReadInternalPanelAdapter()
    {
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int error = DisplayNative.GetDisplayConfigBufferSizes(
                    DisplayNative.QUERY_DEVICE_CONFIG_FLAGS.QDC_ALL_PATHS, out uint pathCount, out uint modeCount);
                if (error != 0) return PanelAdapterKind.Unknown;

                var paths = new DisplayNative.DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DisplayNative.DISPLAYCONFIG_MODE_INFO[modeCount];
                error = DisplayNative.QueryDisplayConfig(
                    DisplayNative.QUERY_DEVICE_CONFIG_FLAGS.QDC_ALL_PATHS,
                    ref pathCount, paths, ref modeCount, modes, nint.Zero);
                if (error == ErrorInsufficientBuffer) continue;   // 拓扑在两次调用之间变了
                if (error != 0) return PanelAdapterKind.Unknown;

                PanelAdapterKind found = PanelAdapterKind.Unknown;
                var seen = new HashSet<(uint, int, uint)>();
                for (int i = 0; i < pathCount; i++)
                {
                    DisplayNative.DISPLAYCONFIG_PATH_TARGET_INFO target = paths[i].targetInfo;
                    if (!target.targetAvailable) continue;
                    if (!GpuRouteInference.IsInternalOutput(target.outputTechnology)) continue;
                    // ALL_PATHS 会把同一个目标按每个源各列一遍。
                    if (!seen.Add((target.adapterId.LowPart, target.adapterId.HighPart, target.id))) continue;

                    var adapter = new DisplayNative.DISPLAYCONFIG_ADAPTER_NAME();
                    adapter.header.type = DisplayNative.DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME;
                    adapter.header.size = (uint)Marshal.SizeOf<DisplayNative.DISPLAYCONFIG_ADAPTER_NAME>();
                    adapter.header.adapterId = target.adapterId;
                    adapter.header.id = 0;
                    if (DisplayNative.DisplayConfigGetDeviceInfo(ref adapter) != 0) continue;

                    PanelAdapterKind kind = GpuRouteInference.ClassifyAdapterPath(adapter.adapterDevicePath);
                    if (kind == PanelAdapterKind.Unknown) continue;
                    if (found == PanelAdapterKind.Unknown) found = kind;
                    else if (found != kind) return PanelAdapterKind.Unknown;   // 两块卡都声称接着内屏：不猜
                }
                return found;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLineThrottled("gpu-route-ccd", "GPU route: internal panel query failed: " + ex.Message, 60_000);
        }
        return PanelAdapterKind.Unknown;
    }

    const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    const uint CmGetIdListFilterPresent = 0x00000100;
    const uint CmGetIdListFilterClass = 0x00000200;
    const int CrSuccess = 0;
    const int CrBufferSmall = 0x1A;
    const uint DnStarted = 0x00000008;
    const uint CmProbDisabled = 22;

    static DgpuPresence ReadDgpuPresence()
    {
        try
        {
            string[] ids = PresentDisplayDeviceIds();
            string[] nvidia = ids.Where(id => id.StartsWith(@"PCI\VEN_10DE", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (nvidia.Length == 0) return DgpuPresence.Absent;

            bool started = false, disabled = false, other = false;
            foreach (string id in nvidia)
            {
                if (CM_Locate_DevNodeW(out uint devInst, id, 0) != CrSuccess) { other = true; continue; }
                if (CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) != CrSuccess) { other = true; continue; }
                if ((status & DnStarted) != 0) started = true;
                else if (problem == CmProbDisabled) disabled = true;
                else other = true;
            }
            if (started) return DgpuPresence.Present;
            if (disabled && !other) return DgpuPresence.Disabled;
            return DgpuPresence.Unknown;
        }
        catch (Exception ex)
        {
            Logger.WriteLineThrottled("gpu-route-cfgmgr", "GPU route: device presence query failed: " + ex.Message, 60_000);
            return DgpuPresence.Unknown;
        }
    }

    static string[] PresentDisplayDeviceIds()
    {
        const uint flags = CmGetIdListFilterClass | CmGetIdListFilterPresent;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint length, DisplayClassGuid, flags) != CrSuccess)
                throw new Win32Exception("CM_Get_Device_ID_List_Size failed");
            if (length <= 1) return Array.Empty<string>();
            var buffer = new char[length];
            int result = CM_Get_Device_ID_ListW(DisplayClassGuid, buffer, length, flags);
            if (result == CrBufferSmall) continue;   // 设备在两次调用之间增加了
            if (result != CrSuccess) throw new Win32Exception($"CM_Get_Device_ID_List failed: 0x{result:X}");
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        throw new Win32Exception("CM_Get_Device_ID_List kept growing");
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint bufferLength, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);
}

/// <summary>
/// 回读缓存。界面每次重画都要知道实际路由，但不能每次都查设备树：结果缓存
/// <see cref="StaleAfterMilliseconds"/>，过期时后台刷新，变化时发 <see cref="Changed"/>。
/// </summary>
public static class GpuRouteMonitor
{
    public const int StaleAfterMilliseconds = 3000;

    static readonly object Sync = new();
    static GpuRouteReadback _last = GpuRouteReadback.Unavailable;
    static long _lastTick;
    static bool _hasReading;
    static int _refreshing;

    /// <summary>回读结果变化（后台线程触发，订阅方自行切回 UI 线程）。</summary>
    public static event Action? Changed;

    /// <summary>最近一次回读（从未读过时为 <see cref="GpuRouteReadback.Unavailable"/>）。</summary>
    public static GpuRouteReadback Last
    {
        get { lock (Sync) return _last; }
    }

    /// <summary>同步读一次并更新缓存。CCD + CfgMgr 都是毫秒级。</summary>
    public static GpuRouteReadback Refresh()
    {
        GpuRouteReadback now;
        try { now = GpuRouteProbe.Read(); }
        catch (Exception ex)
        {
            Logger.WriteLine("GPU route readback failed: " + ex.Message);
            now = GpuRouteReadback.Unavailable;
        }

        bool changed;
        lock (Sync)
        {
            changed = !_hasReading || now != _last;
            _last = now;
            _lastTick = Environment.TickCount64;
            _hasReading = true;
        }
        if (changed)
        {
            Logger.WriteLine("GPU route readback: " + now);
            try { Changed?.Invoke(); }
            catch (Exception ex) { Logger.WriteLine("GPU route change handler failed: " + ex.Message); }
        }
        return now;
    }

    /// <summary>后台读一次，最多等 <paramref name="timeoutMilliseconds"/>；超时返回旧值（读取仍在后台完成）。</summary>
    public static async Task<GpuRouteReadback> RefreshAsync(int timeoutMilliseconds = 500)
    {
        Task<GpuRouteReadback> read = Task.Run(Refresh);
        try
        {
            return await read.WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Logger.WriteLine($"GPU route readback took longer than {timeoutMilliseconds} ms; using the previous value.");
            return Last;
        }
    }

    /// <summary>
    /// 界面取值：从未读过时同步读一次（首帧就要有正确高亮）；过期时起一次后台刷新，先返回旧值。
    /// </summary>
    public static GpuRouteReadback Snapshot()
    {
        bool hasReading;
        long age;
        lock (Sync)
        {
            hasReading = _hasReading;
            age = Environment.TickCount64 - _lastTick;
        }
        if (!hasReading) return Refresh();
        if (age >= StaleAfterMilliseconds && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
        {
            _ = Task.Run(() =>
            {
                try { Refresh(); }
                finally { Interlocked.Exchange(ref _refreshing, 0); }
            });
        }
        return Last;
    }

    /// <summary>测试接缝：清空缓存。</summary>
    internal static void ResetForTests()
    {
        lock (Sync)
        {
            _last = GpuRouteReadback.Unavailable;
            _lastTick = 0;
            _hasReading = false;
        }
    }
}
