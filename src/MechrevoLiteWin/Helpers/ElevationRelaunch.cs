using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MechrevoLite.Helpers;

/// <summary>
/// Always-elevated launch (owner decision: "全都是管理员权限"). A launch from a filtered admin token -
/// Start menu, desktop shortcut, the installer's "launch" checkbox, the relaunch after a silent
/// update - hands over to an elevated instance and exits before it creates any window or connection:
/// <list type="number">
/// <item>the installer-created Highest autostart task (<c>LMechrevo_&lt;SID&gt;</c>) - no UAC prompt;</item>
/// <item>otherwise one UAC prompt (runas). Declining it keeps the app running unelevated.</item>
/// </list>
/// Standard users have no admin token to elevate to and keep running unelevated. The handed-over
/// action travels through a short-lived config entry, because the task always starts the exe with
/// its fixed <c>startup</c> argument.
/// </summary>
internal static class ElevationRelaunch
{
    internal const string RelaunchArgument = "--elevated-relaunch";
    internal const string AfterUpdateArgument = "--after-update";
    internal const string RequestKey = "elevation_relaunch_request";
    internal static readonly TimeSpan RequestLifetime = TimeSpan.FromSeconds(90);

    internal enum TokenKind { Unknown, Default, Full, Limited }

    const int TokenElevationTypeClass = 18;

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        out int tokenInformation, int tokenInformationLength, out int returnLength);

    internal static TokenKind CurrentTokenKind()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (!GetTokenInformation(identity.Token, TokenElevationTypeClass, out int type, sizeof(int), out _))
                return TokenKind.Unknown;
            return type switch { 1 => TokenKind.Default, 2 => TokenKind.Full, 3 => TokenKind.Limited, _ => TokenKind.Unknown };
        }
        catch { return TokenKind.Unknown; }
    }

    /// <summary>Launches that end in the tray/main window. Helper and one-shot actions never hand over.</summary>
    internal static bool IsUiLaunchAction(string? action) =>
        string.IsNullOrEmpty(action) || action is "startup" or AfterUpdateArgument;

    /// <summary>
    /// Pure decision. Only a filtered admin token (UAC admin approval mode) can be elevated without
    /// credentials; a launch that already went through a handover never hands over again (no loops).
    /// </summary>
    internal static bool ShouldHandOver(TokenKind token, string? action, bool alreadyHandedOver, bool uiAudit) =>
        token == TokenKind.Limited && !alreadyHandedOver && !uiAudit && IsUiLaunchAction(action);

    internal static string FormatRequest(DateTimeOffset now, string action) =>
        now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + "|" + (action ?? "");

    /// <summary>Pure: the handed-over action, or null when the entry is missing, malformed or stale.</summary>
    internal static string? ParseRequest(string? raw, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        int bar = raw.IndexOf('|');
        if (bar <= 0) return null;
        if (!long.TryParse(raw.AsSpan(0, bar), NumberStyles.Integer, CultureInfo.InvariantCulture, out long stamp)) return null;
        TimeSpan age = now - DateTimeOffset.FromUnixTimeSeconds(stamp);
        if (age < TimeSpan.FromSeconds(-5) || age > RequestLifetime) return null;
        string action = raw[(bar + 1)..];
        return IsUiLaunchAction(action) ? action : null;
    }

    /// <summary>
    /// Elevated side: the action a filtered launch handed over (and consumes it), or null when this
    /// start is a genuine logon autostart.
    /// </summary>
    internal static string? ConsumeRequest()
    {
        string? raw = AppConfig.GetString(RequestKey);
        if (raw is null) return null;
        AppConfig.Remove(RequestKey);
        AppConfig.Flush();
        return ParseRequest(raw, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Filtered side: start the elevated instance. True = it is starting and this process must exit.
    /// </summary>
    internal static bool TryHandOver(string action)
    {
        // 1) The installer's Highest task starts the same exe elevated without a prompt. Only used when
        //    the image cannot be replaced by the user; otherwise it would elevate whatever sits there.
        if (ExecutableTrust.IsCurrentImageInProtectedLocation(out string trust))
        {
            AppConfig.Set(RequestKey, FormatRequest(DateTimeOffset.UtcNow, action));
            AppConfig.Flush();
            if (Startup.TryRunUserTaskForElevation(out string reason))
            {
                Logger.WriteLine("Elevation: handed over to the autostart task (" + reason + ")");
                return true;
            }
            AppConfig.Remove(RequestKey);
            AppConfig.Flush();
            Logger.WriteLine("Elevation: autostart task not usable: " + reason);
        }
        else
        {
            Logger.WriteLine("Elevation: task handover skipped: " + trust);
        }

        // 2) One UAC prompt.
        try
        {
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            using Process? elevated = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = string.IsNullOrEmpty(action) ? RelaunchArgument : RelaunchArgument + " " + action,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
            });
            if (elevated is null) return false;
            Logger.WriteLine("Elevation: relaunched through UAC");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Logger.WriteLine("Elevation: UAC declined; continuing without administrator rights");
            return false;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Elevation: UAC relaunch failed: " + ex.Message);
            return false;
        }
    }
}

/// <summary>
/// "Show the running instance": a second launch (shortcut, tray-less start) brings the existing main
/// window up instead of silently exiting. The elevated window lets this one registered message through
/// UIPI (ChangeWindowMessageFilterEx), so a filtered launcher can reach it.
/// </summary>
internal static class SingleInstanceSignal
{
    internal const string MessageName = "LMechrevo.ShowMainWindow.v1";
    static readonly IntPtr HwndBroadcast = new(0xFFFF);
    const int AsfwAny = -1;
    const uint MsgfltAllow = 1;

    static readonly Lazy<int> _message = new(() => NativeMethods.RegisterWindowMessage(MessageName));

    internal static int Message => _message.Value;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, int message, uint action, IntPtr changeFilterStruct);

    internal static void RequestShow()
    {
        try
        {
            int message = Message;
            if (message == 0) return;
            AllowSetForegroundWindow(AsfwAny);   // let the running instance take the foreground
            PostMessage(HwndBroadcast, message, IntPtr.Zero, IntPtr.Zero);
            Logger.WriteLine("Asked the running instance to show its window");
        }
        catch (Exception ex) { Logger.WriteLine("Show-window signal failed: " + ex.Message); }
    }

    internal static void AllowFromLowerIntegrity(IntPtr hwnd)
    {
        try
        {
            if (Message != 0) ChangeWindowMessageFilterEx(hwnd, Message, MsgfltAllow, IntPtr.Zero);
        }
        catch (Exception ex) { Logger.WriteLine("Message filter setup failed: " + ex.Message); }
    }
}
