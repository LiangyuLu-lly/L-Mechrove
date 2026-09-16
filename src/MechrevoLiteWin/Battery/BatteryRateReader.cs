using System.Runtime.InteropServices;

namespace MechrevoLite.Battery;

/// <summary>
/// 电池充放电功率直读：IOCTL_BATTERY_QUERY_STATUS → BATTERY_STATUS.Rate（mW，充电为正、放电为负）。
/// 与官方 GCU 服务（同控制码 0x29404C）及 g-helper 同一数据源；任何一步失败都返回 null（未知），不猜数值。
/// </summary>
internal static class BatteryRateReader
{
    const uint IoctlBatteryQueryTag = 0x294040;
    const uint IoctlBatteryQueryStatus = 0x29404C;
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
    internal static decimal? ReadWatts()
    {
        try
        {
            Task<int?> query = Task.Run(QueryRateMilliwatts);
            return query.Wait(1000) ? FromMilliwatts(query.Result) : null;
        }
        catch (Exception ex)
        {
            Logger.WriteLineIfChanged("battery-rate", "电池功率读取失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>mW → W；未知哨兵 0x80000000 视为无读数。</summary>
    internal static decimal? FromMilliwatts(int? milliwatts) =>
        milliwatts is null || milliwatts == UnknownRateMilliwatts
            ? null
            : milliwatts.Value / 1000m;

    static int? QueryRateMilliwatts()
    {
        foreach (string path in BatteryInterfacePaths())
        {
            int? rate = QueryRateMilliwatts(path);
            if (rate is not null && rate != UnknownRateMilliwatts) return rate;
        }
        return null;
    }

    static int? QueryRateMilliwatts(string devicePath)
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
            return status.Rate;
        }
        finally
        {
            CloseHandle(handle);
        }
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

    // CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0（只枚举在场设备）。
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    static extern int CmGetDeviceInterfaceListSize(out uint length, ref Guid interfaceClassGuid, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_ListW")]
    static extern int CmGetDeviceInterfaceList(ref Guid interfaceClassGuid, string? deviceId,
        [Out] char[] buffer, uint bufferLength, uint flags);

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
