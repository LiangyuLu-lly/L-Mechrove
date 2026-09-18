using System.Runtime.InteropServices;

namespace Probe;

public static class EcProbe
{
    // EC 读不再自带 interop（T31 合并）：走 AcpiDriverIo 的唯一实现——IOCTL 0x9C40A488，
    // 4 B 入参 / 16 B 出参，首字节即值。旧常量注释里的 IOCTL 值是错的、旧的 2 B 入参也是
    // 已知的错误形状（docs/ec-per-generation.md §1.2），随合并一并删除。

    public static IntPtr OpenDriver() => AcpiDriverIo.Open();

    public static void CloseDriver(IntPtr h) => AcpiDriverIo.Close(h);

    /// <summary>读一个 EC 寄存器字节；失败返回 -1——0xFF 是合法读值，不等于失败。</summary>
    public static int ReadReg(IntPtr h, int addr) => AcpiDriverIo.ReadByte(h, addr);

    const uint IOCTL_GPD_ACPI_ECWRITE = 2621482124u; // 0x9C40A48C

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
            bool ok = AcpiDriverIo.Ioctl(h, IOCTL_GPD_ACPI_ECWRITE, inBuf, inSize, outBuf, 16, out int returned);
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
            int v = ReadReg(h, addr);
            // 读不到写 ??，不再和 0xFF 这个合法读值混为一谈。
            Console.Write(v < 0 ? $"0x{addr:X3}=?? " : $"0x{addr:X3}={v:X2} ");
            if ((addr - start) % 8 == 7) Console.WriteLine();
        }
        Console.WriteLine();
        CloseDriver(h);
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
            ok = AcpiDriverIo.Ioctl(h, IOCTL_GPD_ACPI_MMREADB, inBuf, 4, outBuf, 16, out int returned) && returned > 0;
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
            bool ok = AcpiDriverIo.Ioctl(h, IOCTL_GPD_ACPI_MMWRITEB, inBuf, inSize, outBuf, 16, out int returned);
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
        CloseDriver(h);
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
            int readback = ReadReg(h, addr);
            Console.WriteLine($"readback 0x{addr:X3}={(readback < 0 ? "??" : readback.ToString("X2"))}");
            CloseDriver(h);
            return;
        }
        int start = ParseNum(args[0]);
        int count = args.Length > 1 ? ParseNum(args[1]) : 1;
        DumpRegisters(count, start);
    }

    static int ParseNum(string s) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToInt32(s[2..], 16) : int.Parse(s);
}
