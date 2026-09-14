using System.Runtime.InteropServices;

namespace Probe;

public static class EcProbe
{
    const uint IOCTL_GPD_ACPI_ECREAD = 2621482120u; // 0x9C402108

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr ovl);

    public static IntPtr OpenDriver()
    {
        return CreateFile(@"\\.\ACPIDriver", 0xC0000000u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
    }

    public static void CloseDriver(IntPtr h) => CloseHandle(h);

    public static byte ReadReg(IntPtr h, int addr, out bool ok)
    {
        IntPtr inBuf = Marshal.AllocHGlobal(4);
        IntPtr outBuf = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt16(inBuf, 0, (short)addr);
            bool succeeded = DeviceIoControl(h, IOCTL_GPD_ACPI_ECREAD, inBuf, 2, outBuf, 16, out int returned, IntPtr.Zero);
            ok = succeeded && returned > 0;
            return ok ? Marshal.ReadByte(outBuf) : (byte)0xFF;
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }
    }

    const uint IOCTL_GPD_ACPI_ECWRITE = 2621482124u; // 0x9C40A48C

    /// <summary>按 EC 规范地址（16 位，如 0x7B9）读一个字节；EEPROM/MMIO 命名空间由驱动映射。</summary>
    public static byte ReadReg(IntPtr h, int addr) => ReadReg(h, addr, out _);

    public static bool WriteReg(IntPtr h, int addr, byte value, int layout = 0)
    {
        IntPtr inBuf = Marshal.AllocHGlobal(8);
        IntPtr outBuf = Marshal.AllocHGlobal(16);
        try
        {
            // 驱动反汇编（UWACPIDriver.sys ECWRITE 处理器 → 0x1400025B8）：入参按
            // [u32 addr][u8 value] 两次拷贝（4B + 1B）送进 ACPI 方法 ECRW，共 5 字节。
            // 0 = [u16 addr][u8 value] (3B)，1 = [u16 addr][u16 value] (4B)，2 = [u32 addr][u8 value] (5B)
            if (layout == 2)
            {
                Marshal.WriteInt32(inBuf, 0, addr);
                Marshal.WriteByte(inBuf, 4, value);
            }
            else
            {
                Marshal.WriteInt16(inBuf, 0, (short)addr);
                if (layout == 0) Marshal.WriteByte(inBuf, 2, value);
                else Marshal.WriteInt16(inBuf, 2, (short)value);
            }
            int inSize = layout switch { 0 => 3, 1 => 4, _ => 5 };
            bool ok = DeviceIoControl(h, IOCTL_GPD_ACPI_ECWRITE, inBuf, inSize, outBuf, 16, out int returned, IntPtr.Zero);
            Console.WriteLine($"write(0x{addr:X3}={value:X2}, layout={layout}, inSize={inSize}) ok={ok} err=0x{Marshal.GetLastWin32Error():X8} returned={returned}");
            return ok;
        }
        finally { Marshal.FreeHGlobal(inBuf); Marshal.FreeHGlobal(outBuf); }
    }

    public static void DumpRegisters(int count = 0x40, int start = 0)
    {
        IntPtr h = OpenDriver();
        if (h == new IntPtr(-1)) { Console.WriteLine("open fail 0x" + Marshal.GetLastWin32Error().ToString("X8")); return; }
        for (int addr = start; addr < start + count; addr++)
        {
            byte v = ReadReg(h, addr);
            Console.Write(v == 0xFF ? $"0x{addr:X3}=ERR " : $"0x{addr:X3}={v:X2} ");
            if ((addr - start) % 8 == 7) Console.WriteLine();
        }
        Console.WriteLine();
        CloseHandle(h);
    }

    const uint IOCTL_GPD_ACPI_MMREADB = 2621482128u;  // 0x9C40A490
    const uint IOCTL_GPD_ACPI_MMWRITEB = 2621482136u; // 0x9C40A498

    /// <summary>物理地址读/写一个字节（DSDT：EC = MMIO 0xFED50000 + offset）。</summary>
    public static byte MmReadByte(IntPtr h, uint address, out bool ok)
    {
        IntPtr inBuf = Marshal.AllocHGlobal(8);
        IntPtr outBuf = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(inBuf, 0, (int)address);
            ok = DeviceIoControl(h, IOCTL_GPD_ACPI_MMREADB, inBuf, 4, outBuf, 16, out int returned, IntPtr.Zero) && returned > 0;
            return ok ? Marshal.ReadByte(outBuf) : (byte)0xFF;
        }
        finally { Marshal.FreeHGlobal(inBuf); Marshal.FreeHGlobal(outBuf); }
    }

    public static bool MmWriteByte(IntPtr h, uint address, byte value, int layout = 0)
    {
        IntPtr inBuf = Marshal.AllocHGlobal(8);
        IntPtr outBuf = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(inBuf, 0, (int)address);
            if (layout == 0) Marshal.WriteByte(inBuf, 4, value);
            else Marshal.WriteInt32(inBuf, 4, value);
            int inSize = layout == 0 ? 5 : 8;
            bool ok = DeviceIoControl(h, IOCTL_GPD_ACPI_MMWRITEB, inBuf, inSize, outBuf, 16, out int returned, IntPtr.Zero);
            Console.WriteLine($"mmwrite(0x{address:X8}={value:X2}, layout={layout}, inSize={inSize}) ok={ok} err=0x{Marshal.GetLastWin32Error():X8} returned={returned}");
            return ok;
        }
        finally { Marshal.FreeHGlobal(inBuf); Marshal.FreeHGlobal(outBuf); }
    }

    /// <summary>ec read &lt;addr&gt; [count] / ec write &lt;addr&gt; &lt;value&gt; / ec mm &lt;physaddr&gt; / ec mmwrite &lt;physaddr&gt; &lt;value&gt;。</summary>
    public static void RunMm(string[] args)
    {
        uint address = (uint)ParseNum(args[1]);
        IntPtr h = OpenDriver();
        if (h == new IntPtr(-1)) { Console.WriteLine("open fail 0x" + Marshal.GetLastWin32Error().ToString("X8")); return; }
        if (args[0].Equals("mmread", StringComparison.OrdinalIgnoreCase))
        {
            byte v = MmReadByte(h, address, out bool ok);
            Console.WriteLine($"mmread 0x{address:X8} = 0x{v:X2} ok={ok}");
        }
        else
        {
            byte value = (byte)ParseNum(args[2]);
            MmWriteByte(h, address, value);
            Console.WriteLine($"mmreadback 0x{address:X8} = 0x{MmReadByte(h, address, out _):X2}");
        }
        CloseHandle(h);
    }


    public static void Run(string[] args)
    {
        if (args.Length == 0) { DumpRegisters(); return; }
        if (args[0].Equals("mmread", StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("mmwrite", StringComparison.OrdinalIgnoreCase))
        {
            RunMm(args);
            return;
        }
        if (args[0].Equals("write", StringComparison.OrdinalIgnoreCase))
        {
            int addr = ParseNum(args[1]);
            byte value = (byte)ParseNum(args[2]);
            IntPtr h = OpenDriver();
            if (h == new IntPtr(-1)) { Console.WriteLine("open fail 0x" + Marshal.GetLastWin32Error().ToString("X8")); return; }
            if (args.Length > 3 && int.TryParse(args[3], out int only)) WriteReg(h, addr, value, layout: only);
            else for (int layout = 0; layout <= 2; layout++) WriteReg(h, addr, value, layout: layout);
            Console.WriteLine($"readback 0x{addr:X3}={ReadReg(h, addr):X2}");
            CloseHandle(h);
            return;
        }
        int start = ParseNum(args[0]);
        int count = args.Length > 1 ? ParseNum(args[1]) : 1;
        DumpRegisters(count, start);
    }

    static int ParseNum(string s) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToInt32(s[2..], 16) : int.Parse(s);
}
