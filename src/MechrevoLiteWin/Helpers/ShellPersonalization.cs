using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace MechrevoLite.Helpers;

/// <summary>
/// 任务栏自动隐藏、窗口透明效果、系统深色主题。
///
/// 这三项官方控制台也有，而且**完全不走 MQTT**：自动隐藏是 Shell 的 AppBar 状态，
/// 另两项是 HKCU 下的个性化注册表值。所以这里不需要 GCU 服务，也没有机型差异——
/// 它们是 Windows 的设置，不是这台笔记本的硬件能力。
///
/// 对照官方实现（CCUWinUI 的 DisplaySettingPage 视图模型）：
/// 自动隐藏用 SHAppBarMessage 的 ABM_GETSTATE / ABM_SETSTATE；
/// 透明写 EnableTransparency；深色写 AppsUseLightTheme 与 SystemUsesLightTheme
/// 两个值（只写一个会出现任务栏与应用主题不一致），改完广播 WM_SETTINGCHANGE
/// 带 "ImmersiveColorSet"，否则已经在跑的程序不会重绘。
/// </summary>
internal static class ShellPersonalization
{
    const string PersonalizeKey = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // ---- 任务栏自动隐藏（Shell AppBar 状态）----

    const uint ABM_GETSTATE = 0x00000004;
    const uint ABM_SETSTATE = 0x0000000A;
    const int ABS_AUTOHIDE = 0x1;
    const int ABS_ALWAYSONTOP = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public nint lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int left, top, right, bottom;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    static extern nuint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, string lParam,
        uint flags, uint timeout, out nint result);

    static APPBARDATA NewAppBarData() => new() { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };

    /// <summary>读取 Shell 的 AppBar 状态位（ABS_AUTOHIDE / ABS_ALWAYSONTOP）。</summary>
    static int GetTaskbarState()
    {
        APPBARDATA data = NewAppBarData();
        return (int)SHAppBarMessage(ABM_GETSTATE, ref data);
    }

    public static bool? IsTaskbarAutoHide()
    {
        try { return (GetTaskbarState() & ABS_AUTOHIDE) != 0; }
        catch (Exception ex)
        {
            Logger.WriteLine("Taskbar auto-hide read failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 设置任务栏自动隐藏。
    ///
    /// ABM_SETSTATE 的 lParam 是**整个状态字**而不是单个开关，所以必须先读出现状、
    /// 只改自动隐藏那一位再写回。官方那边直接写 1 或 0，副作用是顺手把
    /// 「任务栏置顶」也关掉了——这里不复刻那个副作用。
    /// </summary>
    public static bool SetTaskbarAutoHide(bool autoHide)
    {
        try
        {
            int state = GetTaskbarState();
            int target = autoHide ? state | ABS_AUTOHIDE : state & ~ABS_AUTOHIDE;
            if (target == state) return true;

            APPBARDATA data = NewAppBarData();
            data.lParam = target;
            SHAppBarMessage(ABM_SETSTATE, ref data);

            // ABM_SETSTATE 没有返回值可判，只能回读确认。
            bool applied = (GetTaskbarState() & ABS_AUTOHIDE) != 0 == autoHide;
            if (!applied) Logger.WriteLine($"Taskbar auto-hide not confirmed: requested {autoHide}.");
            return applied;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Taskbar auto-hide write failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>当前任务栏是否置顶。改自动隐藏时用来验证这一位没被顺带改掉。</summary>
    internal static bool IsTaskbarAlwaysOnTop() => (GetTaskbarState() & ABS_ALWAYSONTOP) != 0;

    // ---- 个性化注册表项 ----

    static int? RegistryGet(string name)
    {
        try { return Registry.GetValue(PersonalizeKey, name, null) as int?; }
        catch (Exception ex)
        {
            Logger.WriteLine($"Personalize read failed ({name}): " + ex.Message);
            return null;
        }
    }

    static bool RegistrySet(string name, int value)
    {
        try
        {
            Registry.SetValue(PersonalizeKey, name, value, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Personalize write failed ({name}): " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 通知已运行的程序重新读主题色。不广播的话注册表已经变了但界面要等下次登录才变。
    /// </summary>
    static void BroadcastImmersiveColorSet()
    {
        const nint HWND_BROADCAST = 0xFFFF;
        const uint WM_SETTINGCHANGE = 0x001A;
        const uint SMTO_ABORTIFHUNG = 0x0002;
        try
        {
            SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, 0, "ImmersiveColorSet",
                SMTO_ABORTIFHUNG, 100, out _);
        }
        catch (Exception ex) { Logger.WriteLine("ImmersiveColorSet broadcast failed: " + ex.Message); }
    }

    /// <summary>透明效果。注册表缺这个值时 Windows 的默认行为是开启。</summary>
    public static bool? IsTransparencyEnabled() => RegistryGet("EnableTransparency") switch
    {
        null => null,
        0 => false,
        _ => true,
    };

    public static bool SetTransparencyEnabled(bool enabled)
    {
        if (!RegistrySet("EnableTransparency", enabled ? 1 : 0)) return false;
        BroadcastImmersiveColorSet();
        return IsTransparencyEnabled() == enabled;
    }

    /// <summary>系统深色主题。AppsUseLightTheme=0 即深色。</summary>
    public static bool? IsDarkTheme() => RegistryGet("AppsUseLightTheme") switch
    {
        null => null,
        0 => true,
        _ => false,
    };

    /// <summary>
    /// 切换深色主题。必须同时写应用主题与系统主题两个值：
    /// 只写 AppsUseLightTheme 会出现应用变深色而任务栏还是浅色的割裂状态。
    /// </summary>
    public static bool SetDarkTheme(bool dark)
    {
        int light = dark ? 0 : 1;
        bool ok = RegistrySet("AppsUseLightTheme", light);
        ok &= RegistrySet("SystemUsesLightTheme", light);
        if (!ok) return false;
        BroadcastImmersiveColorSet();
        return IsDarkTheme() == dark;
    }
}
