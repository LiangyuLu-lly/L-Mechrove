using MechrevoLite.Models;
using Microsoft.Win32;

namespace MechrevoLite.Services;

public class CapabilityService
{
    const string DefaultPath = @"HKLM\SOFTWARE\OEM\GamingCenter2\ItemSupport";
    readonly string _path;
    public CapabilityService(string path = DefaultPath) { _path = path; }

    public CapabilityInfo Load()
    {
        var key = Registry.LocalMachine.OpenSubKey(_path.Replace(@"HKLM\", ""), false);
        return new CapabilityInfo
        {
            BiosProjectId = GetString(key, "BIOS_PROJECT_ID"),
            KeyboardSupport = GetBool(key, "KeyboardSupport"),
            LightbarSupport = GetBool(key, "LightbarSupport"),
            RgbLightbarSupport = GetBool(key, "RGBLightbarSupport"),
            FanSettingsSupport = GetBool(key, "FanSettingsSupport"),
            FanBoostBtnSupport = GetBool(key, "FanBoostBtnSupport"),
            TurboModeSupport = GetBool(key, "TurboModeSupport"),
            OcSettingsSupport = GetBool(key, "OcSettingsSupport"),
            SystemMonitorSupport = GetBool(key, "SystemMonitorSupport"),
            DGpuDirectConnectionSupport = GetBool(key, "DGpuDirectConnectionSupport"),
            IGpuModeOnlySupport = GetBool(key, "iGPUModeOnlySupport"),
            LiquidCoolingSupport = GetBool(key, "LiquidCoolingSupport"),
            LiquidCoolingAutoModeSupport = GetBool(key, "LiquidCoolingAutoModeSupport"),
            ColorCalibrationSupport = GetBool(key, "ColorCalibrationSupport"),
            NumPadSupport = GetBool(key, "NumPadSupport"),
            AcRecoverySwitchSupport = GetBool(key, "AcRecoverySwitchSupport"),
            IsAmdPlatform = GetBool(key, "IsAMDPlatform"),
            IsNvGpu = GetBool(key, "IsNvGpu"),
            Otasupport = GetBool(key, "OTASupport"),
            SmartBalanceSupport = GetBool(key, "IsSmartBalanceSupport"),
            IsTurboSubModeSupport = GetBool(key, "IsTurboSubModeSupport"),
            RamFan1p5Support = GetBool(key, "RamFan1p5Support"),
            KeyboardType = GetInt(key, "KeyboardType"),
            PanelType = GetInt(key, "PanelType"),
            APVersionCheck = GetInt(key, "APVersionCheck"),
            CustomizeTarget = GetInt(key, "CustomizeTarget"),
        };
    }

    static string GetString(RegistryKey? k, string name) => k?.GetValue(name)?.ToString() ?? "";
    static bool GetBool(RegistryKey? k, string name) => int.TryParse(k?.GetValue(name)?.ToString(), out var v) && v == 1;
    static int GetInt(RegistryKey? k, string name) => int.TryParse(k?.GetValue(name)?.ToString(), out var v) ? v : 0;
}
