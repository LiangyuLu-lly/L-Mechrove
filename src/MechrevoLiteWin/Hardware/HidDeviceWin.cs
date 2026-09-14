using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MechrevoLite.Hardware;

/// <summary>
/// 精简 HID 访问层（P/Invoke Windows HID API，语义对齐 BetterRGB 的 hidapi Windows 后端）。
/// 仅实现灯效所需：SetupDi 枚举（含 usage page/usage/接口号）+ 打开 + feature report + output report。
/// </summary>
public class HidDeviceWin : IDisposable
{
    static Guid HidGuid = new(0x4D1E55B2, 0xF16F, 0x11CF, 0x88, 0xCB, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30);
    const uint DigcfPresent = 0x2, DigcfDeviceInterface = 0x10;
    const uint FileShareRead = 0x1, FileShareWrite = 0x2;
    const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct SpDeviceInterfaceData { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct SpDeviceInterfaceDetailData
    {
        public uint cbSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    /// <summary>ERROR_NO_MORE_ITEMS：设备接口枚举走到末尾的正常返回，不是故障。</summary>
    const int ErrorNoMoreItems = 259;

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfoSet, IntPtr devInfo, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfoSet);

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    public string Path { get; private set; } = "";
    public ushort UsagePage { get; private set; }
    public ushort Usage { get; private set; }
    public ushort VendorID { get; private set; }
    public ushort ProductID { get; private set; }
    public int InterfaceNumber { get; private set; }
    public ushort OutputReportLength { get; private set; }
    public ushort FeatureReportLength { get; private set; }
    public string DeviceInfo => $"VID={VendorID:X4} PID={ProductID:X4} usage_page=0x{UsagePage:X4} usage=0x{Usage:X4} iface={InterfaceNumber}";

    SafeFileHandle? _handle;

    /// <summary>枚举所有 HID 设备（返回含 VID 过滤 + usage/接口信息）。</summary>
    public static List<HidDeviceWin> Enumerate(int vendorIdFilter = 0)
    {
        var list = new List<HidDeviceWin>();
        IntPtr devInfoSet = SetupDiGetClassDevs(ref HidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (devInfoSet == (IntPtr)(-1) || devInfoSet == IntPtr.Zero)
        {
            Logger.WriteLine($"HidEnum: SetupDiGetClassDevs fail err={Marshal.GetLastWin32Error()}");
            return list;
        }
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new SpDeviceInterfaceData { cbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero, ref HidGuid, i, ref data))
                {
                    // ERROR_NO_MORE_ITEMS 是这个循环的正常终止条件，不是故障。
                    // 此前每次枚举都会因此写一行 "fail err=259"，把正常流程记成错误，
                    // 排查 HID 问题时容易被它带偏。
                    int error = Marshal.GetLastWin32Error();
                    if (error != ErrorNoMoreItems)
                        Logger.WriteLine($"HidEnum: SetupDiEnumDeviceInterfaces({i}) fail err={error}");
                    break;
                }
                // 第一段调用以 ERROR_INSUFFICIENT_BUFFER(122) 返回所需大小属正常行为
                if (!SetupDiGetDeviceInterfaceDetail(devInfoSet, ref data, IntPtr.Zero, 0, out uint need, IntPtr.Zero)
                    && Marshal.GetLastWin32Error() != 122) continue;
                IntPtr buf = Marshal.AllocHGlobal((int)need);
                try
                {
                    Marshal.WriteInt32(buf, 0, IntPtr.Size == 8 ? 8 : 5);   // 第二段调用必须先填充 cbSize
                    if (!SetupDiGetDeviceInterfaceDetail(devInfoSet, ref data, buf, need, out _, IntPtr.Zero))
                    {
                        Logger.WriteLine($"HidEnum: detail fail err={Marshal.GetLastWin32Error()}");
                        continue;
                    }
                    // 路径紧跟 4 字节 cbSize 字段（实测 hex：cbSize=8 但路径从偏移 4 起）
                    string path = Marshal.PtrToStringUni(buf + 4) ?? "";
                    var dev = new HidDeviceWin { Path = path };
                    if (dev.Inspect(vendorIdFilter)) list.Add(dev);
                    else dev.Dispose();
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devInfoSet); }
        return list;
    }

    /// <summary>打开设备并读取 HID 能力（usage/报告长度；接口号从路径 MI_xx 解析）。</summary>
    bool Inspect(int vendorIdFilter)
    {
        // 路径形如 \\?\HID#VID_048D&PID_600B&MI_01#...  → VID/PID/MI 从实例 ID 段解析（无需打开设备）
        var instance = Path.Split('#').ElementAtOrDefault(1) ?? "";
        foreach (var seg in instance.Split('&'))
        {
            if (seg.StartsWith("VID_", StringComparison.OrdinalIgnoreCase) && ushort.TryParse(seg[4..], System.Globalization.NumberStyles.HexNumber, null, out var v)) VendorID = v;
            else if (seg.StartsWith("PID_", StringComparison.OrdinalIgnoreCase) && ushort.TryParse(seg[4..], System.Globalization.NumberStyles.HexNumber, null, out var p)) ProductID = p;
            else if (seg.StartsWith("MI_", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(seg[3..], System.Globalization.NumberStyles.HexNumber, null, out var mi)) InterfaceNumber = mi;
        }
        if (vendorIdFilter != 0 && VendorID != vendorIdFilter) return false;
        // Some ITE8291 firmware exposes its RGB interface as write-only. hidapi can
        // still enumerate it, but requiring GENERIC_READ here made the interface
        // disappear completely on those machines. Access=0 remains sufficient for
        // descriptor inspection when both writable attempts are denied.
        int lastError = 0;
        foreach (uint access in new[] { GenericRead | GenericWrite, GenericWrite, 0U })
        {
            _handle = CreateFile(Path, access, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (!_handle.IsInvalid) break;
            lastError = Marshal.GetLastWin32Error();
            _handle.Dispose();
            _handle = null;
        }
        if (_handle is null)
        {
            if (vendorIdFilter == 0x048D || VendorID == 0x048D)
                Logger.WriteLine($"HidEnum: ITE inspect open fail {Path} err={lastError}");
            return false;
        }
        bool ok = HidD_GetPreparsedData(_handle, out IntPtr pd);
        if (ok)
        {
            if (HidP_GetCaps(pd, out var caps))
            {
                UsagePage = caps.UsagePage;
                Usage = caps.Usage;
                OutputReportLength = caps.OutputReportByteLength;
                FeatureReportLength = caps.FeatureReportByteLength;
            }
            HidD_FreePreparsedData(pd);
        }
        _handle.Dispose();
        _handle = null;
        return ok;
    }

    /// <summary>打开设备（共享模式：本机 GCUBridge 持有接口句柄，只能共享打开）。</summary>
    public bool Open()
    {
        _handle?.Dispose();
        _handle = CreateFile(Path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (_handle.IsInvalid)
        {
            _handle.Dispose();
            _handle = CreateFile(Path, GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        }
        if (_handle.IsInvalid)
        {
            _handle.Dispose();
            _handle = null;
            return false;
        }
        return true;
    }

    /// <summary>发送 feature report（buffer[0] = report ID）。virtual：测试可注入失败/断线设备。</summary>
    public virtual bool SetFeature(byte[] report)
        => _handle is not null && HidD_SetFeature(_handle, report, report.Length);

    /// <summary>发送 output report（buffer[0] = report ID；65B = ID0 + 64 数据）。virtual：测试可注入失败设备。</summary>
    public virtual bool Write(byte[] report)
    {
        if (_handle is null) return false;
        return WriteFile(_handle, report, (uint)report.Length, out uint written, IntPtr.Zero) && written == report.Length;
    }

    public virtual void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}
