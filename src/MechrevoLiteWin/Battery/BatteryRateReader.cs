using System.Runtime.InteropServices;

namespace MechrevoLite.Battery;

/// <summary>
/// 一次 <c>BATTERY_STATUS</c> 读数。<see cref="PowerState"/> 是 <c>POWER_ON_LINE(0x1)</c> / <c>DISCHARGING(0x2)</c> /
/// <c>CHARGING(0x4)</c> / <c>CRITICAL(0x8)</c> 的组合；<see cref="RateMilliwatts"/> 充电为正、放电为负，
/// 未知哨兵 <c>0x80000000</c> 已换成 null。引用类型：采样线程整体替换、界面线程整体读取，不会读到半个结构。
/// </summary>
internal sealed record BatteryStatusReading(
    uint PowerState,
    int? RateMilliwatts,
    uint CapacityMilliwattHours,
    uint VoltageMillivolts)
{
    internal const uint PowerOnLine = 0x1;
    internal const uint Discharging = 0x2;
    internal const uint Charging = 0x4;
    internal const uint Critical = 0x8;

    internal bool IsOnLine => (PowerState & PowerOnLine) != 0;
    internal bool IsCharging => (PowerState & Charging) != 0 || RateMilliwatts is > 0;
    internal bool IsDischarging => (PowerState & Discharging) != 0 || RateMilliwatts is < 0;
}

/// <summary><c>BATTERY_INFORMATION</c> 的诊断字段（mWh；<see cref="IsSystemBattery"/> = BATTERY_SYSTEM_BATTERY）。</summary>
internal sealed record BatteryInformationReading(
    uint DesignedCapacity,
    uint FullChargedCapacity,
    uint CycleCount,
    bool IsSystemBattery);

/// <summary>
/// 电池充放电功率直读：IOCTL_BATTERY_QUERY_STATUS → BATTERY_STATUS.Rate（mW，充电为正、放电为负）。
/// 与官方 GCU 服务（同控制码 0x29404C）及 g-helper 同一数据源；任何一步失败都返回 null（未知），不猜数值。
/// </summary>
internal static class BatteryRateReader
{
    const uint IoctlBatteryQueryTag = 0x294040;
    const uint IoctlBatteryQueryInformation = 0x294044;
    const uint IoctlBatteryQueryStatus = 0x29404C;
    const int BatteryInformationLevel = 0;               // BATTERY_QUERY_INFORMATION_LEVEL.BatteryInformation
    const uint BatterySystemBattery = 0x80000000;        // BATTERY_SYSTEM_BATTERY
    const int UnknownRateMilliwatts = unchecked((int)0x80000000);
    const uint GenericRead = 0x80000000;
    const uint GenericWrite = 0x40000000;
    const uint FileShareReadWrite = 0x00000003;
    const uint OpenExisting = 3;
    static readonly Guid BatteryInterfaceClass = new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");

    /// <summary>
    /// 读取当前充放功率（W）。查询放后台线程并限时 1 秒：个别机器的电池驱动可能阻塞，
    /// UI 线程不能陪等；超时或读不到按未知（null）处理。
    /// </summary>
    internal static decimal? ReadWatts() => FromMilliwatts(ReadStatus()?.RateMilliwatts);

    /// <summary>
    /// 读一次完整的电池状态（与 <see cref="ReadWatts"/> 同一次 IOCTL，不增加 I/O）。
    /// 后台线程限时 1 秒；读不到返回 null。
    /// </summary>
    internal static BatteryStatusReading? ReadStatus()
    {
        try
        {
            Task<BatteryStatusReading?> query = Task.Run(QueryStatus);
            return query.Wait(1000) ? query.Result : null;
        }
        catch (Exception ex)
        {
            Logger.WriteLineIfChanged("battery-rate", "电池状态读取失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 读 <c>BATTERY_INFORMATION</c>（设计容量 / 满充容量 / 循环次数）。只给诊断包用：本机实测固件把
    /// 设计值填了两遍、循环次数为 0（hidden-readonly-info-plan §2.6），不上主界面。读不到返回 null。
    /// </summary>
    internal static BatteryInformationReading? ReadInformation()
    {
        try
        {
            Task<BatteryInformationReading?> query = Task.Run(QueryInformation);
            return query.Wait(1000) ? query.Result : null;
        }
        catch (Exception ex)
        {
            Logger.WriteLineIfChanged("battery-info-ioctl", "电池信息读取失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>mW → W；未知哨兵 0x80000000 视为无读数。</summary>
    internal static decimal? FromMilliwatts(int? milliwatts) =>
        milliwatts is null || milliwatts == UnknownRateMilliwatts
            ? null
            : milliwatts.Value / 1000m;

    /// <summary>原始 <c>BATTERY_STATUS</c> → 读数（纯函数，测试直接调用）。</summary>
    internal static BatteryStatusReading FromRaw(uint powerState, uint capacity, uint voltage, int rate) =>
        new(powerState, rate == UnknownRateMilliwatts ? null : rate, capacity, voltage);

    static BatteryStatusReading? QueryStatus()
    {
        BatteryStatusReading? first = null;
        foreach (string path in BatteryInterfacePaths())
        {
            BatteryStatusReading? reading = QueryStatus(path);
            if (reading is null) continue;
            if (reading.RateMilliwatts is not null) return reading;
            first ??= reading;
        }
        return first;
    }

    static BatteryStatusReading? QueryStatus(string devicePath)
    {
        IntPtr handle = CreateFileW(devicePath, GenericRead | GenericWrite, FileShareReadWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return null;
        try
        {
            uint timeout = 0;
            if (!DeviceIoControl(handle, IoctlBatteryQueryTag, ref timeout, sizeof(uint),
                    out uint tag, sizeof(uint), out _, IntPtr.Zero) || tag == 0)
                return null;

            var wait = new BatteryWaitStatus { BatteryTag = tag };
            if (!DeviceIoControlStatus(handle, IoctlBatteryQueryStatus, ref wait,
                    (uint)Marshal.SizeOf<BatteryWaitStatus>(), out BatteryStatus status,
                    (uint)Marshal.SizeOf<BatteryStatus>(), out _, IntPtr.Zero))
                return null;
            return FromRaw(status.PowerState, status.Capacity, status.Voltage, status.Rate);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    static BatteryInformationReading? QueryInformation()
    {
        foreach (string path in BatteryInterfacePaths())
        {
            IntPtr handle = CreateFileW(path, GenericRead | GenericWrite, FileShareReadWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle == new IntPtr(-1)) continue;
            try
            {
                uint timeout = 0;
                if (!DeviceIoControl(handle, IoctlBatteryQueryTag, ref timeout, sizeof(uint),
                        out uint tag, sizeof(uint), out _, IntPtr.Zero) || tag == 0)
                    continue;

                var query = new BatteryQueryInformation { BatteryTag = tag, InformationLevel = BatteryInformationLevel };
                if (!DeviceIoControlInformation(handle, IoctlBatteryQueryInformation, ref query,
                        (uint)Marshal.SizeOf<BatteryQueryInformation>(), out BatteryInformation info,
                        (uint)Marshal.SizeOf<BatteryInformation>(), out _, IntPtr.Zero))
                    continue;
                return new BatteryInformationReading(info.DesignedCapacity, info.FullChargedCapacity, info.CycleCount,
                    (info.Capabilities & BatterySystemBattery) != 0);
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        return null;
    }

    static string[] BatteryInterfacePaths()
    {
        Guid guid = BatteryInterfaceClass;
        if (CmGetDeviceInterfaceListSize(out uint length, ref guid, null, 0) != 0 || length == 0)
            return [];
        var buffer = new char[length];
        if (CmGetDeviceInterfaceList(ref guid, null, buffer, length, 0) != 0) return [];
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BatteryWaitStatus
    {
        public uint BatteryTag;
        public uint Timeout;
        public uint PowerState;
        public uint LowCapacity;
        public uint HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BatteryStatus
    {
        public uint PowerState;
        public uint Capacity;
        public uint Voltage;
        public int Rate;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BatteryQueryInformation
    {
        public uint BatteryTag;
        public int InformationLevel;
        public uint AtRate;
    }

    // BATTERY_INFORMATION：Technology(UCHAR) + Reserved[3] + Chemistry[4] 用 8 个字节整体占位。
    [StructLayout(LayoutKind.Sequential)]
    struct BatteryInformation
    {
        public uint Capabilities;
        public uint TechnologyAndReserved;
        public uint Chemistry;
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
        public uint CriticalBias;
        public uint CycleCount;
    }

    // CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0（只枚举在场设备）。
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    static extern int CmGetDeviceInterfaceListSize(out uint length, ref Guid interfaceClassGuid, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_ListW")]
    static extern int CmGetDeviceInterfaceList(ref Guid interfaceClassGuid, string? deviceId,
        [Out] char[] buffer, uint bufferLength, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    static extern bool DeviceIoControlInformation(IntPtr device, uint controlCode, ref BatteryQueryInformation inBuffer,
        uint inBufferSize, out BatteryInformation outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr device, uint controlCode, ref uint inBuffer, uint inBufferSize,
        out uint outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    static extern bool DeviceIoControlStatus(IntPtr device, uint controlCode, ref BatteryWaitStatus inBuffer,
        uint inBufferSize, out BatteryStatus outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
