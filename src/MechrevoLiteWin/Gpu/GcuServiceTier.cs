using System.Diagnostics;
using MechrevoLite.Helpers;

namespace MechrevoLite.Gpu;

/// <summary>
/// 本机正在运行的 GCU 服务是哪一档。**能发哪些显卡动作由服务档位决定，不由独显代际决定**：
/// 我方安装器给 30/40/50 装 1.2.0.0（<c>AiStoneService</c>），给 GTX 10/16、RTX 20 装 1.0.2.47
/// （<c>UniwillService</c>）；厂商自己装的服务我们分不清是哪一版，只用各版都有的交集。
/// </summary>
public enum GcuServiceTier
{
    /// <summary>服务未注册 / 读不到版本：一个显卡动作都不发。</summary>
    Unknown,

    /// <summary>我方 1.0.2.47（无 <c>UEFI_Firmware.dll</c>）：只有 <c>NV_CTRL_PANEL_*</c>。</summary>
    Legacy1020,

    /// <summary>我方 1.2.0.0 及以上：MUX 目标 + 服务自带的 <c>DGPU_DIRECT_CONNECT_RESTART</c> + 热切换。</summary>
    Modern12,

    /// <summary>
    /// 厂商自己装的服务（或我方目录下的 1.0.2.70 手动载荷）：只用 30/40 各版都有的
    /// <c>TOGGLE_ON/OFF</c>，重启由我方发起。
    /// </summary>
    Foreign,
}

/// <summary>
/// 服务档位探测：读 GCUBridge 服务镜像路径与同目录 <c>MyControlCenter\GCUService.exe</c> 的文件版本。
/// 只读注册表与文件版本，不碰服务本身。结果进程内缓存，MQTT 重连时 <see cref="Invalidate"/>。
/// </summary>
public static class GcuServiceTierProbe
{
    static readonly object Sync = new();
    static GcuServiceTier? _cached;

    /// <summary>1.2 起是我方 Modern 载荷（30/40/50）。</summary>
    internal static readonly Version ModernMinimum = new(1, 2);

    /// <summary>厂商 30/40 服务都是 1.0.2.70；低于它且没有 UEFI 库的是 10/20 的 1.0.2.47。</summary>
    internal static readonly Version VendorThirtyForty = new(1, 0, 2, 70);

    /// <summary>测试接缝：非 null 时直接返回它的结果（生产代码从不设置）。</summary>
    internal static Func<GcuServiceTier>? Override { get; set; }

    /// <summary>当前档位（缓存）。</summary>
    public static GcuServiceTier Current()
    {
        if (Override is { } factory) return factory();
        lock (Sync)
        {
            if (_cached is { } cached) return cached;
            GcuServiceTier detected = DetectFromSystem();
            _cached = detected;
            return detected;
        }
    }

    /// <summary>服务可能被重装/替换（MQTT 重连）时丢弃缓存。</summary>
    public static void Invalidate()
    {
        lock (Sync) _cached = null;
    }

    static GcuServiceTier DetectFromSystem()
    {
        try
        {
            string? imagePath = GcuCoexistence.ReadServiceImagePath(GcuCoexistence.VendorServiceName);
            GcuServiceTier tier = Detect(imagePath, ReadFileVersion, File.Exists);
            Logger.WriteLine($"GCU service tier: {tier} (image={imagePath ?? "-"})");
            return tier;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("GCU service tier detection failed: " + ex.Message);
            return GcuServiceTier.Unknown;
        }
    }

    /// <summary>
    /// 纯函数判档。<paramref name="bridgeImagePath"/> 是服务 <c>ImagePath</c>（可能带引号与参数）；
    /// <paramref name="fileVersion"/> 读文件版本，读不到返回 <c>null</c>。
    /// </summary>
    public static GcuServiceTier Detect(
        string? bridgeImagePath,
        Func<string, Version?> fileVersion,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileVersion);
        ArgumentNullException.ThrowIfNull(fileExists);

        string? bridgeExe = ExecutableOf(bridgeImagePath);
        if (bridgeExe is null) return GcuServiceTier.Unknown;
        string? serviceDir = Path.GetDirectoryName(bridgeExe);
        if (string.IsNullOrEmpty(serviceDir)) return GcuServiceTier.Unknown;

        bool ours = GcuCoexistence.IsOurImagePath(bridgeExe);
        if (!ours) return GcuServiceTier.Foreign;

        string controlCenter = Path.Combine(serviceDir, "MyControlCenter");
        string serviceExe = Path.Combine(controlCenter, "GCUService.exe");
        if (!fileExists(serviceExe))
        {
            serviceExe = Path.Combine(serviceDir, "GCUService.exe");
            if (!fileExists(serviceExe)) return GcuServiceTier.Unknown;
        }

        Version? version = fileVersion(serviceExe);
        if (version is null) return GcuServiceTier.Unknown;
        if (version >= ModernMinimum) return GcuServiceTier.Modern12;

        bool hasUefiLibrary = fileExists(Path.Combine(controlCenter, "UEFI_Firmware.dll")) ||
                              fileExists(Path.Combine(serviceDir, "UEFI_Firmware.dll"));
        if (version < VendorThirtyForty && !hasUefiLibrary) return GcuServiceTier.Legacy1020;

        // 我方目录下的 1.0.2.70（手动装的 40 系载荷）：按厂商服务的交集用。
        return GcuServiceTier.Foreign;
    }

    /// <summary>从服务 <c>ImagePath</c> 取出可执行文件路径（去引号、去参数）。</summary>
    internal static string? ExecutableOf(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        string text = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            return close > 1 ? text[1..close] : null;
        }
        int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? text[..(exe + 4)] : text;
    }

    static Version? ReadFileVersion(string path)
    {
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            if (info.FileMajorPart == 0 && info.FileMinorPart == 0 && info.FileBuildPart == 0 && info.FilePrivatePart == 0)
                return Version.TryParse(info.FileVersion, out Version? parsed) ? parsed : null;
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        }
        catch
        {
            return null;
        }
    }
}
