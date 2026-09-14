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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    public static void LockScreen()
    {
        LockWorkStation();
    }

    // Monitor Power detection

    internal const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x0;
    internal const uint DEVICE_NOTIFY_SERVICE_HANDLE = 0x1;
    internal const int WM_POWERBROADCAST = 0x0218;
    internal const int PBT_POWERSETTINGCHANGE = 0x8013;
    internal const int PBT_APMSUSPEND = 0x0004;
    internal const int PBT_APMRESUMESUSPEND = 0x0007;
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
        // 0=Powered by AC, 1=Powered by Battery, 2=Powered by short-term source (UPC)
        public static Guid AcdcPowerSource { get; } = new Guid("5d3e9a59-e9D5-4b00-a6bd-ff34ff516548");
        // POWERBROADCAST_SETTING.Data = 1-100
        public static Guid BatteryPercentageRemaining { get; } = new Guid("a7ad8041-b45a-4cae-87a3-eecbb468a9e1");
        // Windows 8+: 0=Monitor Off, 1=Monitor On, 2=Monitor Dimmed
        public static Guid ConsoleDisplayState { get; } = new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");
        // Windows 8+, Session 0 enabled: 0=User providing Input, 2=User Idle
        public static Guid GlobalUserPresence { get; } = new Guid("786E8A1D-B427-4344-9207-09E70BDCBEA9");
        // 0=Monitor Off, 1=Monitor On.
        public static Guid MonitorPowerGuid { get; } = new Guid("02731015-4510-4526-99e6-e5a17ebd1aea");
        // 0=Battery Saver Off, 1=Battery Saver On.
        public static Guid PowerSavingStatus { get; } = new Guid("E00958C0-C213-4ACE-AC77-FECCED2EEEA5");
        // Win11 24H2 Energy Saver: 0=Off, 1=Standard, 2=High Savings
        public static Guid EnergySaverStatus { get; } = new Guid("550E8400-E29B-41D4-A716-446655440000");

        // Windows 8+: 0=Off, 1=On, 2=Dimmed
        public static Guid SessionDisplayStatus { get; } = new Guid("2B84C20E-AD23-4ddf-93DB-05FFBD7EFCA5");

        // Windows 8+, no Session 0: 0=User providing Input, 2=User Idle
        public static Guid SessionUserPresence { get; } = new Guid("3C0F4548-C03F-4c4d-B9F2-237EDE686376");
        // 0=Exiting away mode 1=Entering away mode
        public static Guid SystemAwaymode { get; } = new Guid("98a7f580-01f7-48aa-9c0f-44352c29e5C0");

        /* Windows 8+ */
        // POWERBROADCAST_SETTING.Data not used
        public static Guid IdleBackgroundTask { get; } = new Guid(0x515C31D8, 0xF734, 0x163D, 0xA0, 0xFD, 0x11, 0xA0, 0x8C, 0x91, 0xE8, 0xF1);

        public static Guid PowerSchemePersonality { get; } = new Guid(0x245D8541, 0x3943, 0x4422, 0xB0, 0x25, 0x13, 0xA7, 0x84, 0xF6, 0x79, 0xB7);

        // The Following 3 Guids are the POWERBROADCAST_SETTING.Data result of PowerSchemePersonality
        public static Guid MinPowerSavings { get; } = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        public static Guid MaxPowerSavings { get; } = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");
        public static Guid TypicalPowerSavings { get; } = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");

        public static Guid LIDSWITCH_STATE_CHANGE = new Guid("ba3e0f4d-b817-4094-a2d1-d56379e6a0f3");
    }



}
