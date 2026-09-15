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

    public static LhmMonitor? lhm;   // 实时功耗（LibreHardwareMonitorLib 本地直读）
    static readonly object lhmLock = new();
    static CpuPowerSampler? cpuPowerSampler;

    public static void EnableLocalMonitoring()
    {
        lock (lhmLock)
        {
            lhm ??= new LhmMonitor();
            cpuPowerSampler ??= new CpuPowerSampler();
        }
    }

    public static void DisableLocalMonitoring()
    {
        lock (lhmLock)
        {
            lhm?.Dispose();
            lhm = null;
            cpuPowerSampler?.Dispose();
            cpuPowerSampler = null;
            cpuPower = gpuPower = null;
            vramUsedMb = vramUsage = null;
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
    public static void ReadSensors() { }
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
    }
    public static int GetBatteryChargePercentage() => batteryCharge;
    public static int GetCPUTemp() => cpuTemp;
    public static void KillGPUApps() { }
    public static void ResetCPUPowerCounter() => cpuPowerSampler?.Reset();
    public static void DisposeGpuControl() { }
    public static void RecreateGpuControl() { }
    public static IGpuControl? GpuControl => null;
}
