using MechrevoLite.Battery;
using MechrevoLite.Gpu;
using MechrevoLite.Hardware;

namespace MechrevoLite;

/// <summary>
/// HardwareControl 兼容层（Mechrevo 化）：UI 传感器显示的数据源。
/// 静态字段由 MechrevoHw 的 MQTT 快照填充（DataChanged 事件同步）。
/// </summary>
public static class HardwareControl
{
    // ---- 传感器字段（UI 绑定）----
    public static int cpuTemp;
    public static int? cpuUsage;
    public static int? cpuFan;
    public static int cpuFanRPM;
    public static int gpuTemp;
    public static int? gpuUsage;
    public static int? gpuFan;
    public static int gpuFanRPM;
    public static int? midFan;
    public static int cpuFrequency;   // MHz（Overlay 频率显示）
    public static int gpuCoreFreq;    // MHz
    public static float? cpuPower;
    public static float? gpuPower;
    public static int? ramUsage;
    public static int? ramUsedMb;
    public static int? vramUsage;
    public static int? vramUsedMb;
    public static int batteryCapacity;
    public static int batteryCharge;
    public static decimal batteryHealth = -1;
    public static decimal? batteryRate;
    public static bool chargeWatt;

    // 电池充放瓦数来自本地 OS 电池 IOCTL（不走 MQTT 推流）。读不到保持 null——
    // 悬浮窗与托盘提示只在有数值时显示，不编数。默认读取器顺带记下同一次 IOCTL 的 PowerState
    // （充电中 / 放电 / 已接通），不增加 I/O；测试替换读取器时只替换功率。
    internal static Func<decimal?> batteryRateReader = ReadBatteryStatusWatts;
    internal const int BatteryRateRefreshIntervalMs = 5000;
    static long _lastBatteryRateRead;
    static BatteryStatusReading? _batteryStatus;

    /// <summary>最近一次电池状态读数（与 <see cref="batteryRate"/> 同一次 IOCTL）；读不到为 null。</summary>
    internal static BatteryStatusReading? BatteryStatus => Volatile.Read(ref _batteryStatus);

    static decimal? ReadBatteryStatusWatts()
    {
        BatteryStatusReading? reading = BatteryRateReader.ReadStatus();
        Volatile.Write(ref _batteryStatus, reading);
        return BatteryRateReader.FromMilliwatts(reading?.RateMilliwatts);
    }

    /// <summary>测试接缝：直接设定电池状态。</summary>
    internal static void SetBatteryStatusForTests(BatteryStatusReading? reading) => Volatile.Write(ref _batteryStatus, reading);

    // ---- 供电方式（只读展示，docs/hardware/hidden-readonly-info-plan.md §2.1/§3）----
    // 只读 EC 0x7CC/0x49F，走 MechrevoService.EcReadTransportFactory（只读接口）；不驱动任何行为
    // （Program.ReadPowerSource 仍只分电池/插电）。10/20 没有这个寄存器，不解码。

    /// <summary>EC 读取的等待上限：界面线程也会走到这里，超时就用上一次的结果。</summary>
    internal const int PowerInputReadTimeoutMs = 250;
    static PowerInputSampler _powerInputSampler = CreatePowerInputSampler();
    static int _powerInputRefreshing;

    static PowerInputSampler CreatePowerInputSampler() => new(
        () => MechrevoService.EcReadTransportFactory(),
        static () => SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online,
        PowerInputDecodeEnabled);

    /// <summary>10/20（旧版服务或 10/20 独显）的 EC 规格里没有 0x7CC：不解码，只按 Windows 分电池/外接电源。</summary>
    internal static bool PowerInputDecodeEnabled() =>
        GcuServiceTierProbe.Current() != GcuServiceTier.Legacy1020 &&
        Program.hw?.DgpuGeneration != DgpuGenerationKind.Gen1020;

    /// <summary>最近一次供电采样（从未采样为 null）。</summary>
    internal static PowerInputSample? PowerInput => Volatile.Read(ref _powerInputSampler).Current;

    /// <summary>
    /// 刷新供电方式（5 s 限频，<paramref name="force"/> 用于插拔事件）。EC 读放后台、最多等
    /// <see cref="PowerInputReadTimeoutMs"/>；同一时间只有一个读取在跑，驱动卡住也不会堆积线程。
    /// </summary>
    internal static void RefreshPowerInput(bool force = false)
    {
        PowerInputSampler sampler = Volatile.Read(ref _powerInputSampler);
        if (!sampler.NeedsRefresh(force)) return;
        if (Interlocked.CompareExchange(ref _powerInputRefreshing, 1, 0) != 0) return;
        Task refresh;
        try
        {
            refresh = Task.Run(() =>
            {
                try { sampler.Refresh(force); }
                catch (Exception ex) { Logger.WriteLineIfChanged("power-input", "Power input refresh failed: " + ex.Message); }
                finally { Interlocked.Exchange(ref _powerInputRefreshing, 0); }
            });
        }
        catch
        {
            Interlocked.Exchange(ref _powerInputRefreshing, 0);
            return;
        }
        try { refresh.Wait(PowerInputReadTimeoutMs); }
        catch { /* 采样自己吞异常；这里只是不陪等 */ }
    }

    /// <summary>测试接缝：换一个采样器（返回原来的，便于还原）。</summary>
    internal static PowerInputSampler ReplacePowerInputSamplerForTests(PowerInputSampler sampler) =>
        Interlocked.Exchange(ref _powerInputSampler, sampler);

    /// <summary>
    /// 限频刷新 <see cref="batteryRate"/>（默认 5 秒；force 用于测试与显式刷新）。
    /// 读取失败时字段为 null，调用方按「未知」处理。
    /// </summary>
    internal static void RefreshBatteryRate(bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now - _lastBatteryRateRead < BatteryRateRefreshIntervalMs) return;
        _lastBatteryRateRead = now;
        batteryRate = batteryRateReader();
    }

    public static LhmMonitor? lhm;   // 实时功耗（LibreHardwareMonitorLib 本地直读）
    static readonly object lhmLock = new();
    static CpuPowerSampler? cpuPowerSampler;

    // 热切换期间我方不得占住独显（§10 第 9 条）：悬浮窗开着时 LHM 打开 GPU 传感器、NVML 持有句柄，
    // RB_ON 断开独显时它们会让切换失败。挂起期间的启用请求只记下，结束后补上。
    static int _dgpuHandleSuspensions;
    static bool _monitoringRequestedWhileSuspended;

    public static void EnableLocalMonitoring()
    {
        lock (lhmLock)
        {
            if (_dgpuHandleSuspensions > 0)
            {
                _monitoringRequestedWhileSuspended = true;
                return;
            }
            lhm ??= new LhmMonitor();
            cpuPowerSampler ??= new CpuPowerSampler();
        }
    }

    public static void DisableLocalMonitoring()
    {
        lock (lhmLock)
        {
            _monitoringRequestedWhileSuspended = false;
            ReleaseLocalMonitoringLocked();
        }
    }

    static void ReleaseLocalMonitoringLocked()
    {
        lhm?.Dispose();
        lhm = null;
        cpuPowerSampler?.Dispose();
        cpuPowerSampler = null;
        cpuPower = gpuPower = null;
        vramUsedMb = vramUsage = null;
    }

    /// <summary>
    /// 释放我方对独显的句柄（LHM 传感器、NVML），返回的租约 Dispose 时按原状态恢复。
    /// 只在热切换断开独显前调用。
    /// </summary>
    internal static IDisposable SuspendDgpuHandles(string reason)
    {
        bool wasMonitoring;
        lock (lhmLock)
        {
            wasMonitoring = lhm is not null;
            _dgpuHandleSuspensions++;
            if (wasMonitoring)
            {
                _monitoringRequestedWhileSuspended = true;
                ReleaseLocalMonitoringLocked();
            }
        }
        NvmlHelper.Shutdown();
        Logger.WriteLine($"dGPU handles released for {reason} (local monitoring was {(wasMonitoring ? "on" : "off")}).");
        return new DgpuHandleLease();
    }

    static void ResumeDgpuHandles()
    {
        bool restore;
        lock (lhmLock)
        {
            _dgpuHandleSuspensions = Math.Max(0, _dgpuHandleSuspensions - 1);
            restore = _dgpuHandleSuspensions == 0 && _monitoringRequestedWhileSuspended;
            if (_dgpuHandleSuspensions == 0) _monitoringRequestedWhileSuspended = false;
        }
        if (restore) EnableLocalMonitoring();
    }

    sealed class DgpuHandleLease : IDisposable
    {
        int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) ResumeDgpuHandles();
        }
    }

    public static void AttachMechrevoHw(MechrevoHw hw)
    {
        hw.DataChanged += () =>
        {
            cpuTemp = hw.CpuTemp;
            cpuUsage = hw.CpuUsage;
            cpuFan = hw.CpuFanDuty;
            cpuFanRPM = hw.CpuFanRpm;
            // 恒为 null：这套协议里没有第三颗风扇的读数（见 MechrevoHw.MidFanSeen 的说明）。
            // 界面按 null 隐藏那块读数，AsusACPI.IsMidFanSupported() 也据此回答不支持。
            midFan = null;
            cpuFrequency = hw.CpuFrequency;
            gpuTemp = hw.GpuTemp;
            gpuUsage = hw.GpuUsage;
            gpuFan = hw.GpuFanDuty;
            gpuFanRPM = hw.GpuFanRpm;
            gpuCoreFreq = hw.GpuCoreFreq;
            ramUsage = hw.RamUsage;
            ramUsedMb = (int)(hw.RamUsedGb * 1024);
            batteryCapacity = hw.BatteryPercent;
            batteryCharge = hw.BatteryPercent;
        };
    }

    private static void RefreshLocalMonitoring()
    {
        lock (lhmLock)
        {
            if (lhm is null) return;
            lhm.Update();
            float? localCpuPower = lhm.CpuPower;
            float? localGpuPower = lhm.GpuPower;
            cpuPower = ResolveCpuPower(localCpuPower,
                IsUsablePower(localCpuPower) ? null : cpuPowerSampler?.Sample());
            gpuPower = ResolveGpuPower(localGpuPower,
                IsUsablePower(localGpuPower) ? null : NvmlHelper.GetGpuPower());
            // MQTT GpuMem is total VRAM. Used VRAM and percentage come from the local monitor.
            var nvmlMemory = lhm.GpuMemoryUsedMb > 0 && lhm.GpuMemoryTotalMb > 0
                ? null
                : NvmlHelper.GetMemoryInfo();
            long memoryUsedMb = lhm.GpuMemoryUsedMb > 0
                ? lhm.GpuMemoryUsedMb
                : nvmlMemory?.usedMb ?? 0;
            long memoryTotalMb = lhm.GpuMemoryTotalMb > 0
                ? lhm.GpuMemoryTotalMb
                : nvmlMemory?.totalMb ?? 0;
            vramUsedMb = memoryUsedMb > 0 ? (int)Math.Min(memoryUsedMb, int.MaxValue) : null;
            vramUsage = memoryUsedMb > 0 && memoryTotalMb > 0
                ? (int)Math.Clamp(memoryUsedMb * 100 / memoryTotalMb, 0, 100)
                : null;
        }
    }

    /// <summary>
    /// 功耗墙生效校验。喂的是实测 CPU 封装功耗与 GCU 报的 PL1，
    /// 用来回答「写下去的功耗墙 CPU 到底有没有照着做」——服务端回读只证明它记下了。
    /// </summary>
    internal static readonly PowerWallVerifier powerWall = new();

    /// <summary>
    /// 把这一轮的功耗样本喂给校验器。放在传感器刷新里而不是某个窗口里：
    /// 判定需要连续 60 秒的样本，只在窗口打开时采样永远凑不满。
    /// </summary>
    static void ObservePowerWall()
    {
        // 没有实测功耗或 GCU 没报 PL1 时不喂——喂进去只会得出「判不出来」，
        // 但会把有效样本挤出窗口。
        float? watts = cpuPower;
        int limit = Program.hw?.Pl1 ?? -1;
        if (watts is null || limit <= 0) return;
        powerWall.Observe(watts.Value, limit, DateTime.Now);
    }

    internal static float? ResolveCpuPower(float? localPower, float? fallbackPower) =>
        SelectUsablePower(localPower, fallbackPower);

    internal static float? ResolveGpuPower(float? localPower, float? fallbackPower) =>
        SelectUsablePower(localPower, fallbackPower);

    static float? SelectUsablePower(float? localPower, float? fallbackPower) =>
        IsUsablePower(localPower) ? localPower : IsUsablePower(fallbackPower) ? fallbackPower : null;

    static bool IsUsablePower(float? power) =>
        power is > 0 && float.IsFinite(power.Value);

    public static bool readFans;
    public static bool readUsage;
    public static bool readMemory;
    public static bool readPower;
    public static bool readBattery;
    public static void ReadSensors()
    {
        RefreshBatteryRate();
        RefreshPowerInput();
    }
    public static void ReadSensorsOverlay() => SampleLocalPower();

    /// <summary>
    /// 刷新本地功耗读数并喂一个样本给功耗墙校验。
    /// 悬浮窗与自定义性能模式窗口都走这里——功耗墙判定需要连续 60 秒的样本，
    /// 只在其中一处采样会经常凑不满一个窗口。
    /// </summary>
    internal static void SampleLocalPower()
    {
        RefreshLocalMonitoring();
        ObservePowerWall();
        RefreshBatteryRate();
    }
    public static int GetBatteryChargePercentage() => batteryCharge;
    public static int GetCPUTemp() => cpuTemp;
    public static void KillGPUApps() { }
    public static void ResetCPUPowerCounter() => cpuPowerSampler?.Reset();
    public static void DisposeGpuControl() { }
    public static void RecreateGpuControl() { }
    public static IGpuControl? GpuControl => null;
}
