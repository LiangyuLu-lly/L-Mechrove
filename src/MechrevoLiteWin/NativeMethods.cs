using MechrevoLite;
﻿using System.Runtime.InteropServices;


public static class NativeMethods
{

    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("User32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    // ---- Gamma LUT 检测（校色实际落地验证用）----
    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll", EntryPoint = "GetDeviceGammaRamp")]
    public static extern bool GetDeviceGammaRamp(IntPtr hDC, IntPtr lpRamp);   // 3×256×2 字节缓冲

    public static string GetGammaRampHash()
    {
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return "fail";
        IntPtr buf = Marshal.AllocHGlobal(3 * 256 * 2);
        try
        {
            if (!GetDeviceGammaRamp(hdc, buf)) return "fail";
            long h = 0;
            for (int i = 0; i < 3 * 256; i += 8)
            {
                ushort v = (ushort)Marshal.ReadInt16(buf, i * 2);
                h = (h * 31 + v) % 1000000007;
            }
            return h.ToString();
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    public static TimeSpan GetIdleTime()
    {
        LASTINPUTINFO lastInPut = new LASTINPUTINFO();
        lastInPut.cbSize = (uint)Marshal.SizeOf(lastInPut);
        GetLastInputInfo(ref lastInPut);
        return TimeSpan.FromMilliseconds((uint)Environment.TickCount - lastInPut.dwTime);

    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int RegisterWindowMessage(string lpString);

    /// <summary>
    /// 输入新鲜度的测试接缝：非 null 时替代 GetLastInputInfo 读取「最近一次真实输入距今多久」。
    /// 测试只喂时间、绝不产生真实输入；生产运行时保持 null，直接读 user32。重置前不得跨测试泄漏。
    /// </summary>
    internal static Func<TimeSpan>? IdleTimeProvider { get; set; }

    /// <summary>「刚刚发生的真实输入」窗口（毫秒）。息屏等一次性动作的放行条件。</summary>
    internal const int FreshInputWindowMs = 500;

    /// <summary>
    /// 最近一次真实用户输入是否落在 <paramref name="windowMs"/> 窗口内。程序化写入 Checked、
    /// UIA TogglePattern 与启动回读都不会刷新 GetLastInputInfo，因此这是真实动作的唯一放行条件。
    /// </summary>
    public static bool HasFreshUserInput(int windowMs = FreshInputWindowMs)
    {
        TimeSpan idle = IdleTimeProvider is { } provider ? provider() : GetIdleTime();
        return idle >= TimeSpan.Zero && idle <= TimeSpan.FromMilliseconds(windowMs);
    }

    // Monitor Power detection

    internal const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x0;
    internal const int WM_POWERBROADCAST = 0x0218;
    internal const int PBT_POWERSETTINGCHANGE = 0x8013;
    internal const int PBT_APMSUSPEND = 0x0004;
    internal const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    [DllImport("User32.dll", SetLastError = true)]
    internal static extern IntPtr RegisterPowerSettingNotification(IntPtr hWnd, [In] Guid PowerSettingGuid, uint Flags);

    [DllImport("User32.dll", SetLastError = true)]
    internal static extern bool UnregisterPowerSettingNotification(IntPtr hWnd);

    [DllImport("User32.dll", SetLastError = true)]
    internal static extern IntPtr RegisterSuspendResumeNotification(IntPtr hRecipient, uint Flags);

    [DllImport("User32.dll", SetLastError = true)]
    internal static extern bool UnregisterSuspendResumeNotification(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    public class PowerSettingGuid
    {
        // Windows 8+: 0=Monitor Off, 1=Monitor On, 2=Monitor Dimmed
        public static Guid ConsoleDisplayState { get; } = new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");
        // Win11 24H2 Energy Saver: 0=Off, 1=Standard, 2=High Savings
        public static Guid EnergySaverStatus { get; } = new Guid("550E8400-E29B-41D4-A716-446655440000");

        public static Guid LIDSWITCH_STATE_CHANGE = new Guid("ba3e0f4d-b817-4094-a2d1-d56379e6a0f3");
    }



}
