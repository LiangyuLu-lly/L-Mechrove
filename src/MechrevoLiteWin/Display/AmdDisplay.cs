// TODO(nullable-migration): 这个文件继承自 g-helper 的 UI/原生互操作代码，尚未完成可空性标注。
// 项目已开启完整的可空性检查（csproj 里的 Nullable=enable），核心硬件层与 Helpers 均已清零；
// 这里显式关闭，是为了让剩余债务可见且局部化，而不是靠项目级 annotations 把它藏起来。
// 迁移某个文件时删掉下面这行（保留注解上下文，只关闭警告），然后把该文件的 CS86xx 告警修干净即可。
#nullable disable warnings
using MechrevoLite;
using Microsoft.Win32;
using PawnIO;
using System.Diagnostics;

public static class AmdDisplay
{
    private const string DisplayPath0 =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0001\DAL2_DATA__2_0\DisplayPath_0";

    private static string _cachedAdjustmentPath = null;
    private static bool _isPathSearched = false;

    private static string GetAdjustmentPath()
    {
        if (_isPathSearched) return _cachedAdjustmentPath;

        try
        {
            using RegistryKey dp0 = Registry.LocalMachine.OpenSubKey(DisplayPath0, writable: false);
            if (dp0 != null)
            {
                string edidKey = Array.Find(dp0.GetSubKeyNames(),
                    name => name.StartsWith("EDID_", StringComparison.OrdinalIgnoreCase));

                if (edidKey != null)
                {
                    _cachedAdjustmentPath = $@"HKEY_LOCAL_MACHINE\{DisplayPath0}\{edidKey}\Adjustment";
                }
            }
        }
        catch
        {
            // Handle permissions or missing keys silently
        }
        finally
        {
            _isPathSearched = true;
        }

        return _cachedAdjustmentPath;
    }

    public static bool IsOledPowerOptimization()
    {
        if (!AppConfig.IsOLED() || !CpuInfo.IsAMD) return false;

        try
        {
            string path = GetAdjustmentPath();
            if (path == null) return false;

            object value = Registry.GetValue(path, "DAL_SCE_Settings", null);

            if (value is byte[] data && data.Length >= 5)
            {
                return (data[4] & 0x02) != 0;
            }
        } catch
        {
            Logger.WriteLine("Can't check AMD OLED Optimization flag");
        }

        return false;
    }

    public static void RunAdrenaline()
    {
        string desktopPath = @"C:\Program Files\AMD\CNext\CNext\RadeonSoftware.exe";
        string uwpPackageFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Packages\AdvancedMicroDevicesInc-2.AMDRadeonSoftware_0a9344xs7nr4m"
        );

        if (File.Exists(desktopPath))
        {
            Process.Start(new ProcessStartInfo(desktopPath)
            {
                UseShellExecute = true,
                Verb = "runas" 
            });
            return; 
        }

        if (Directory.Exists(uwpPackageFolder))
        {
            string aumid = @"shell:AppsFolder\AdvancedMicroDevicesInc-2.AMDRadeonSoftware_0a9344xs7nr4m!App";
            Process.Start(new ProcessStartInfo("explorer.exe", aumid)
            {
                UseShellExecute = true
            });
            return;
        }

        Logger.WriteLine("AMD Radeon Software is not installed.");
    }

}
