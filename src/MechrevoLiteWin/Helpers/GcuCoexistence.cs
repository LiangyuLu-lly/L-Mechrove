using Microsoft.Win32;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace MechrevoLite.Helpers;

/// <summary>外来 GCU（厂商服务 / 13688 占用者）的共存判定结果。</summary>
public enum GcuCoexistenceKind
{
    /// <summary>没有外来 GCU 迹象。</summary>
    None,

    /// <summary>存在外来 GCU 服务（<c>GCUBridge</c>）。</summary>
    ForeignService,

    /// <summary>13688 被外来进程占用。</summary>
    PortOwner,

    /// <summary>两者都有。</summary>
    Both,
}

/// <summary>
/// 外来 GCU 共存处置（C3 重定义，round 5）。
///
/// <para>事实修正：先前「卸官方台报 SEVERE → 共存冲突」是**误推**，那条只是示例、不是真缺陷。
/// 现在的契约是：检测到外来 GCU 环境时**提示用户自行移除官方控制台应用**（可见提示），
/// 绝不静默删除；服务的接管（先卸后装 + 回滚护栏）由安装器承担。</para>
/// </summary>
internal static class GcuCoexistence
{
    /// <summary>外来 GCU 服务名（厂商载荷注册名）。</summary>
    internal const string VendorServiceName = "GCUBridge";

    /// <summary>厂商控制台界面进程名。只用于探测，不终结。</summary>
    static readonly string[] VendorConsoleProcessNames =
    {
        "CCUWinUI", "SystrayComponent", "ControlCenterU", "GamingCenterU", "GCUUI",
    };

    /// <summary>按「外来服务 × 13688 占用者」分类。</summary>
    internal static GcuCoexistenceKind Classify(bool foreignService, bool foreignPortOwner) =>
        (foreignService, foreignPortOwner) switch
        {
            (true, true) => GcuCoexistenceKind.Both,
            (true, false) => GcuCoexistenceKind.ForeignService,
            (false, true) => GcuCoexistenceKind.PortOwner,
            _ => GcuCoexistenceKind.None,
        };

    /// <summary>是否必须向用户显示「自行移除官方控制台」的提示。</summary>
    internal static bool RequiresConsoleRemovalPrompt(GcuCoexistenceKind kind) =>
        kind != GcuCoexistenceKind.None;

    /// <summary>可见提示文案：要求用户自行卸载官方控制台，绝不宣称由我们删除。</summary>
    internal static string BuildConsoleRemovalPrompt(GcuCoexistenceKind kind) =>
        "检测到机器上仍有厂商的 GCU 环境（" + kind + "）。\r\n" +
        "L-Mechrevo 不会替你静默删除厂商的软件。请手动卸载「官方控制台」应用" +
        "（设置 → 应用 → 已安装的应用），然后重新启动 L-Mechrevo。\r\n" +
        "GCU 服务的接管（先卸后装 + 回滚护栏）由安装器负责。";

    internal static bool IsOurImagePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Contains("L-Mechrevo", StringComparison.OrdinalIgnoreCase);

    /// <summary>厂商 GCUBridge：服务镜像存在且不属于 L-Mechrevo。</summary>
    internal static bool IsLeftoverVendorService(string? imagePath) =>
        !string.IsNullOrWhiteSpace(imagePath) && !IsOurImagePath(imagePath);

    /// <summary>13688 被占用且占用者不是我们的 GCU。</summary>
    internal static bool IsLeftoverPortOwner(bool portInUse, string? ownerImagePath) =>
        portInUse && !IsOurImagePath(ownerImagePath);

    /// <summary>
    /// 启动时：有外来控制台/服务或 13688 占用者则走可见提示，绝不静默删除。
    /// 探测函数可注入；省略时读本机服务镜像与监听端口。
    /// </summary>
    internal static bool WarnAtStartup(
        Action<string> showPrompt,
        Func<bool>? leftoverVendorConsoleOrService = null,
        Func<bool>? leftoverPortOwner = null)
    {
        ArgumentNullException.ThrowIfNull(showPrompt);
        try
        {
            bool foreignService = leftoverVendorConsoleOrService?.Invoke() ?? DetectLeftoverVendorConsoleOrService();
            bool foreignPortOwner = leftoverPortOwner?.Invoke() ?? DetectLeftoverPortOwner();
            GcuCoexistenceKind kind = Classify(foreignService, foreignPortOwner);
            if (!RequiresConsoleRemovalPrompt(kind)) return false;
            string prompt = BuildConsoleRemovalPrompt(kind);
            Logger.WriteLine(prompt);
            showPrompt(prompt);
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("GCU coexistence probe failed: " + ex.Message);
            return false;
        }
    }

    static bool DetectLeftoverVendorConsoleOrService()
    {
        try
        {
            if (IsVendorConsoleInstalled() || IsVendorConsoleRunning()) return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("GCU coexistence console probe failed: " + ex.Message);
        }

        try
        {
            return IsLeftoverVendorService(ReadServiceImagePath(VendorServiceName));
        }
        catch (Exception ex)
        {
            Logger.WriteLine("GCU coexistence service probe failed: " + ex.Message);
            return false;
        }
    }

    static bool DetectLeftoverPortOwner()
    {
        try
        {
            if (!IsMqttPortListening(MqttSecurity.DefaultPort)) return false;
            return IsLeftoverPortOwner(true, ReadServiceImagePath(VendorServiceName));
        }
        catch (Exception ex)
        {
            Logger.WriteLine("GCU coexistence port probe failed: " + ex.Message);
            return false;
        }
    }

    internal static string? ReadServiceImagePath(string serviceName)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName);
        return key?.GetValue("ImagePath")?.ToString();
    }

    internal static bool IsMqttPortListening(int port)
    {
        IPEndPoint[] listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        foreach (IPEndPoint endpoint in listeners)
            if (endpoint.Port == port) return true;
        return false;
    }

    /// <summary>当前会话里是否还有厂商控制台界面。其他会话的同名进程不算本机残留。</summary>
    static bool IsVendorConsoleRunning()
    {
        int sessionId = CurrentSessionId();
        foreach (string name in VendorConsoleProcessNames)
        {
            Process[] processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Any(process => !process.HasExited && process.SessionId == sessionId))
                    return true;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Can't inspect vendor console process {name}: {ex.Message}");
            }
            finally
            {
                foreach (Process process in processes) process.Dispose();
            }
        }

        return false;
    }

    static int CurrentSessionId()
    {
        try
        {
            using Process current = Process.GetCurrentProcess();
            return current.SessionId;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Can't read the current session id: " + ex.Message);
            return -1;
        }
    }

    /// <summary>
    /// 厂商包是否仍装在本机。WindowsApps 对标准用户常拒绝枚举，失败后继续查 OEM 目录。
    /// </summary>
    static bool IsVendorConsoleInstalled()
    {
        try
        {
            string windowsApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (Directory.Exists(windowsApps) &&
                Directory.EnumerateDirectories(windowsApps, "CCU.WinUI_*").Any())
                return true;
        }
        catch (UnauthorizedAccessException)
        {
            // WindowsApps is ACL-locked for a standard user; fall through to the OEM probe.
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Vendor console package probe failed: " + ex.Message);
        }

        try
        {
            string oem = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OEM");
            return Directory.Exists(oem)
                && Directory.EnumerateFiles(oem, "ControlCenterU.exe", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Vendor console OEM probe failed: " + ex.Message);
            return false;
        }
    }
}
