using System.Runtime.InteropServices;
using System.Text;

namespace Probe;

/// <summary>
/// EC 快照的只读传输抽象。整个文件只有读路径——没有任何写 IOCTL，构造上不可能改 EC。
/// </summary>
public interface IEcReadTransport
{
    /// <summary>读一个 EC 字节；失败返回 -1（0xFF 是合法读值，不表示失败）。</summary>
    int ReadByte(int address);
}

/// <summary>
/// <c>\\.\ACPIDriver</c> 只读通道。形状照抄 live-verified 的
/// <c>src\MechrevoLiteWin\Hardware\EcChargeLimit.cs</c>：读 IOCTL 0x9C40A488，
/// 入参 <c>[u32 地址]</c>（4 B），出参 16 B，首字节即值。
/// 有意不抄 <c>EcProbe.ReadReg</c> 的 2 字节入参——docs/ec-per-generation.md §1.2 明确说那是错的。
/// </summary>
public sealed class AcpiDriverReadTransport : IEcReadTransport, IDisposable
{
    const uint IoctlEcRead = 0x9C40A488; // EcChargeLimit 同值
    const string DevicePath = @"\\.\ACPIDriver";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr handle, uint code, IntPtr inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

    IntPtr _handle;
    AcpiDriverReadTransport(IntPtr handle) => _handle = handle;

    /// <summary>打开设备。失败时 <paramref name="error"/> 是逐字的 Win32 错误描述；调用方必须 STOP，不得安装/启动任何驱动或服务。</summary>
    public static bool TryOpen(out AcpiDriverReadTransport? transport, out string error)
    {
        IntPtr handle = CreateFile(DevicePath, 0xC0000000u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
        if (handle == new IntPtr(-1))
        {
            int code = Marshal.GetLastWin32Error();
            error = $"CreateFile(\"{DevicePath}\") failed: Win32 error {code} (0x{code:X8}) {new System.ComponentModel.Win32Exception(code).Message}";
            transport = null;
            return false;
        }
        transport = new AcpiDriverReadTransport(handle);
        error = "";
        return true;
    }

    public int ReadByte(int address)
    {
        IntPtr inBuffer = Marshal.AllocHGlobal(4);
        IntPtr outBuffer = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(inBuffer, 0, address);
            bool ok = DeviceIoControl(_handle, IoctlEcRead, inBuffer, 4, outBuffer, 16, out int returned, IntPtr.Zero);
            return ok && returned > 0 ? Marshal.ReadByte(outBuffer) : -1;
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
            Marshal.FreeHGlobal(outBuffer);
        }
    }

    public void Dispose()
    {
        if (_handle == new IntPtr(-1)) return;
        CloseHandle(_handle);
        _handle = new IntPtr(-1);
    }
}

/// <summary>一段 EC 地址范围，含端点。</summary>
public readonly record struct EcRange(int Start, int EndInclusive)
{
    public int Length => EndInclusive - Start + 1;
    public bool Contains(int address) => address >= Start && address <= EndInclusive;

    public static EcRange Parse(string text)
    {
        string[] parts = text.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) { int only = ParseAddress(parts[0]); return new EcRange(only, only); }
        if (parts.Length == 2)
        {
            int a = ParseAddress(parts[0]);
            int b = ParseAddress(parts[1]);
            return b < a ? new EcRange(b, a) : new EcRange(a, b);
        }
        throw new FormatException($"bad EC range: '{text}' (want 0x400-0x4FF or 0x740)");
    }

    public static int ParseAddress(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToInt32(text[2..], 16)
        : int.Parse(text);
}

/// <summary>一次快照结果：成功读到的地址→字节，以及读失败的地址。</summary>
public sealed record EcSnapshotResult(IReadOnlyDictionary<int, int> Bytes, IReadOnlyList<int> Errors, DateTimeOffset TakenUtc)
{
    public int Count => Bytes.Count;
    public int ErrorCount => Errors.Count;
}

/// <summary>W4 只读 EC 快照：捕获 + 十六进制转储。解析部分是纯函数，测试用假传输注入。</summary>
public static class EcSnapshot
{
    /// <summary>docs/ec-per-generation.md §4/§5 命名的三段。</summary>
    public static readonly IReadOnlyList<EcRange> DocumentedRanges = new[]
    {
        new EcRange(0x400, 0x4FF),
        new EcRange(0x720, 0x7FF),
        new EcRange(0xF00, 0xFFF),
    };

    /// <summary>docs 关心的只读面包屑；落在三段内的会被 Capture 去重。</summary>
    public static readonly IReadOnlyList<int> DocumentedBreadcrumbs = new[]
    {
        0x740, 0x74C, 0x751, 0x7AB, 0x7C6, 0xF5D, 0xF5E, 0xF5F,
        0x743, 0x744, 0x745, 0x746, 0x786, 0x787, 0x7E8, 0xB0, 0x72A, 0x7D2, 0xCB,
    };

    /// <summary>逐地址读取；<paramref name="delayMs"/> 给 EC 总线留间隔。地址按范围顺序去重，读失败记入 Errors。</summary>
    public static EcSnapshotResult Capture(
        IEcReadTransport transport,
        IReadOnlyList<EcRange> ranges,
        IReadOnlyList<int> breadcrumbs,
        int delayMs = 4,
        Action<int>? progress = null)
    {
        var bytes = new SortedDictionary<int, int>();
        var errors = new List<int>();
        var seen = new HashSet<int>();

        foreach (EcRange range in ranges)
        {
            for (int address = range.Start; address <= range.EndInclusive; address++)
                ReadOne(transport, address, bytes, errors, seen, delayMs, progress);
        }
        foreach (int address in breadcrumbs)
            ReadOne(transport, address, bytes, errors, seen, delayMs, progress);

        return new EcSnapshotResult(bytes, errors, DateTimeOffset.Now);
    }

    static void ReadOne(
        IEcReadTransport transport, int address, SortedDictionary<int, int> bytes,
        List<int> errors, HashSet<int> seen, int delayMs, Action<int>? progress)
    {
        if (!seen.Add(address)) return;
        int value = transport.ReadByte(address);
        if (value < 0) errors.Add(address);
        else bytes[address] = value;
        progress?.Invoke(address);
        if (delayMs > 0) Thread.Sleep(delayMs);
    }

    /// <summary>把读到的字节渲染成 16 列十六进制 + ASCII；读失败显示 <c>??</c>。</summary>
    public static string FormatHexDump(EcSnapshotResult result, IReadOnlyList<EcRange> ranges)
    {
        var sb = new StringBuilder();
        foreach (EcRange range in ranges)
        {
            sb.AppendLine($"-- 0x{range.Start:X3}-0x{range.EndInclusive:X3} --");
            for (int line = range.Start; line <= range.EndInclusive; line += 16)
            {
                sb.Append($"0x{line:X3}  ");
                var ascii = new StringBuilder(16);
                for (int i = 0; i < 16; i++)
                {
                    int address = line + i;
                    if (address > range.EndInclusive) { sb.Append("   "); continue; }
                    if (result.Bytes.TryGetValue(address, out int value))
                    {
                        sb.Append($"{value:X2} ");
                        ascii.Append(value >= 32 && value < 127 ? (char)value : '.');
                    }
                    else { sb.Append("?? "); ascii.Append('!'); }
                }
                sb.AppendLine(" " + ascii);
            }
        }
        return sb.ToString();
    }

    /// <summary>渲染范围外的面包屑（范围内的已在十六进制段里）。</summary>
    public static string FormatLooseBreadcrumbs(EcSnapshotResult result, IReadOnlyList<EcRange> ranges)
    {
        var sb = new StringBuilder();
        foreach (int address in DocumentedBreadcrumbs)
        {
            if (ranges.Any(r => r.Contains(address))) continue;
            sb.AppendLine(result.Bytes.TryGetValue(address, out int value)
                ? $"0x{address:X} = 0x{value:X2} ({value})"
                : $"0x{address:X} = read error");
        }
        return sb.ToString();
    }
}
