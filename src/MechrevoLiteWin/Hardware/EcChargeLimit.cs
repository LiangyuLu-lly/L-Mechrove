using System.Runtime.InteropServices;

namespace MechrevoLite.Hardware;

/// <summary>
/// 通过厂商驱动写 EC 的充电阈值寄存器。
///
/// 真机实测（2026-09-11）：
/// <list type="bullet">
/// <item>读：<c>IOCTL_GPD_ACPI_ECREAD</c>(0x9C40A488)，入参 <c>[u32 地址]</c>，出参首字节即值；</item>
/// <item>写：<c>IOCTL_GPD_ACPI_ECWRITE</c>(0x9C40A48C)，入参必须是 <c>[u32 地址][u8 值]</c> 共 5 字节
///   —— 驱动处理器 0x1400025B8 分两次 memcpy（4B + 1B）把参数送进 ACPI 方法 ECRW。
///   3/4 字节布局会被错位解析成 0x5007B9 这类地址：DeviceIoControl 返回成功、寄存器纹丝不动。
///   出参缓冲还必须非空，否则直接 ERROR_INVALID_PARAMETER(87)。</item>
/// </list>
///
/// **寄存器选型（这是一次返工）**：最初写的是固件字段表里的 <c>CGLM @ 0x78F</c>（名字就是
/// Charge LiMit，写完能回读），但真机验证发现**充电完全不受它控制**——设 80% 时电量一路涨到 86%，
/// 把上限抬到 100 或降回 86 都不改变充电状态。真正生效的是官方服务常量里的那一对：
/// <c>ADDR_BATTERY_CHARGE_LIMIT_UP = 0x7B9</c>（充到该值停止）与
/// <c>ADDR_BATTERY_CHARGE_LIMIT_DOWN = 0x7D0</c>（掉到该值以下才恢复充电）。
/// 旁证：社区工具 MechrevoBatteryManager（同方模具，翼龙/旷世实测可用）只写这两个地址，
/// 且强制 <c>0 ≤ 下限 &lt; 上限 ≤ 100</c>；有效时 Windows 电池图标会变成"智能充电"。
///
/// **本机复验（同上日期）**：写 <c>0x50/0x3C</c>（80/60）时电量已在阈值之上 → 充电立即停止，
/// 5 分钟只从 94% 漂到 95% 后持平（同一台机器限制前是 5 分钟涨约 3%）；界面上设 96% 后
/// 充电停在 95%。
/// </summary>
internal static class EcChargeLimit
{
    /// <summary>充电上限：充到这个百分比就停（厂商 EC 规范 ADDR_BATTERY_CHARGE_LIMIT_UP）。</summary>
    public const int UpperRegister = 0x7B9;

    /// <summary>复充下限：掉到这个百分比以下才恢复充电（ADDR_BATTERY_CHARGE_LIMIT_DOWN）。</summary>
    public const int LowerRegister = 0x7D0;

    /// <summary>
    /// 上限与复充下限之间的迟滞。留几个点是为了不让电量在阈值附近反复充放——
    /// 这是各家电源管理工具的通行做法，也保证 <c>下限 &lt; 上限</c> 这条约束成立。
    /// </summary>
    public const int RechargeHysteresis = 5;

    public const int MinimumPercent = 40;
    public const int MaximumPercent = 100;

    const uint IoctlEcRead = 2621482120u;   // 0x9C40A488
    const uint IoctlEcWrite = 2621482124u;  // 0x9C40A48C
    const string DevicePath = @"\\.\ACPIDriver";

    static readonly object Gate = new();

    /// <summary>
    /// 测试接缝：非 null 时由它代替真实 EC 写入（测试只记录，绝不碰硬件）。返回 (是否成功, 生效百分比)。
    /// 生产运行时保持 null，直接走厂商驱动。
    /// </summary>
    internal static Func<int, (bool Success, int AppliedPercent)>? TrySetOverride { get; set; }

    /// <summary>
    /// 测试接缝：非 null 时由它代替真实 EC 回读（测试只记录，绝不碰硬件）。返回 -1 表示读不到。
    /// </summary>
    internal static Func<int>? ReadPercentOverride { get; set; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr handle, uint code, IntPtr inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

    /// <summary>
    /// 上限寄存器值语义：100% 写固件的「无上限」值 0（出厂状态就是 0/0，实测能充满到 100%），
    /// 其余按百分比直写（实测 0x64/0x50/0x3C/0x28/0x14 全部原值读回，不钳位）。
    /// </summary>
    internal static int ValueFor(int percent) => percent >= MaximumPercent ? 0 : percent;

    /// <summary>复充下限：满充档同样归零（无上限就不需要复充下限）。</summary>
    internal static int LowerValueFor(int percent) =>
        percent >= MaximumPercent ? 0 : Math.Max(0, percent - RechargeHysteresis);

    /// <summary>回读值 → 用户可见的百分比。</summary>
    internal static int PercentFor(int registerValue) =>
        registerValue <= 0 ? MaximumPercent : Math.Clamp(registerValue, MinimumPercent, MaximumPercent);

    public static bool IsSupportedLimit(int percent) => percent is >= MinimumPercent and <= MaximumPercent;

    /// <summary>
    /// 只有实测过的机型才允许写。EC 字段布局随机型而变（本机就是例子：固件字段表里的
    /// CGLM@0x78F 能写能回读却管不住充电），在未验证的机器上按同一地址写等于往未知寄存器里塞值。
    /// 配置项 <c>ec_charge_limit</c> 可强制开关（"1"/"0"），供新机型验证时用。
    /// </summary>
    public static bool IsSupportedMachine(string? model)
    {
        string? forced = AppConfig.GetString("ec_charge_limit");
        if (forced == "1") return true;
        if (forced == "0") return false;
        return model?.Contains("YAOSHI", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>本机是否允许走 EC 直写通道。</summary>
    public static bool IsAvailableOnThisMachine() => IsSupportedMachine(AppConfig.GetModel());

    /// <summary>写入充电阈值（上限 + 复充下限一对）并回读确认。</summary>
    public static bool TrySet(int percent, out int appliedPercent)
    {
        appliedPercent = -1;
        if (!IsSupportedLimit(percent)) return false;

        if (TrySetOverride is { } seam)
        {
            (bool ok, int applied) = seam(percent);
            appliedPercent = ok ? applied : -1;
            return ok;
        }

        lock (Gate)
        {
            IntPtr handle = CreateFile(DevicePath, 0xC0000000u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
            if (handle == new IntPtr(-1)) return false;
            try
            {
                int wantedUpper = ValueFor(percent);
                int wantedLower = LowerValueFor(percent);
                int previousUpper = Read(handle, UpperRegister);
                int previousLower = Read(handle, LowerRegister);

                if (!Write(handle, UpperRegister, (byte)wantedUpper)) return false;
                if (Read(handle, UpperRegister) != wantedUpper) return false;

                if (!Write(handle, LowerRegister, (byte)wantedLower))
                {
                    // 下限写失败就把上限还原：只留半套阈值可能让 EC 行为不可预期。
                    if (previousUpper >= 0) Write(handle, UpperRegister, (byte)previousUpper);
                    return false;
                }
                if (Read(handle, LowerRegister) != wantedLower)
                {
                    if (previousLower >= 0) Write(handle, LowerRegister, (byte)previousLower);
                    if (previousUpper >= 0) Write(handle, UpperRegister, (byte)previousUpper);
                    return false;
                }

                appliedPercent = PercentFor(Read(handle, UpperRegister));
                return appliedPercent == PercentFor(wantedUpper);
            }
            finally { CloseHandle(handle); }
        }
    }

    /// <summary>只读当前上限（诊断用）。失败返回 -1。</summary>
    public static int ReadPercent()
    {
        if (ReadPercentOverride is { } seam) return seam();
        lock (Gate)
        {
            IntPtr handle = CreateFile(DevicePath, 0xC0000000u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
            if (handle == new IntPtr(-1)) return -1;
            try
            {
                int upper = Read(handle, UpperRegister);
                return upper < 0 ? -1 : PercentFor(upper);
            }
            finally { CloseHandle(handle); }
        }
    }

    static int Read(IntPtr handle, int address)
    {
        IntPtr inBuffer = Marshal.AllocHGlobal(4);
        IntPtr outBuffer = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(inBuffer, 0, address);
            bool ok = DeviceIoControl(handle, IoctlEcRead, inBuffer, 4, outBuffer, 16, out int returned, IntPtr.Zero);
            return ok && returned > 0 ? Marshal.ReadByte(outBuffer) : -1;
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
            Marshal.FreeHGlobal(outBuffer);
        }
    }

    static bool Write(IntPtr handle, int address, byte value)
    {
        IntPtr inBuffer = Marshal.AllocHGlobal(8);
        IntPtr outBuffer = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(inBuffer, 0, address);
            Marshal.WriteByte(inBuffer, 4, value);
            return DeviceIoControl(handle, IoctlEcWrite, inBuffer, 5, outBuffer, 16, out _, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
            Marshal.FreeHGlobal(outBuffer);
        }
    }
}
