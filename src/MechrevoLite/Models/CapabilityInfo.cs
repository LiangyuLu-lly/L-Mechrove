namespace MechrevoLite.Models;

public class CapabilityInfo
{
    public string BiosProjectId { get; init; } = "";
    public bool KeyboardSupport { get; init; }
    public bool LightbarSupport { get; init; }
    public bool RgbLightbarSupport { get; init; }
    public bool FanSettingsSupport { get; init; }
    public bool FanBoostBtnSupport { get; init; }
    public bool TurboModeSupport { get; init; }
    public bool OcSettingsSupport { get; init; }
    public bool SystemMonitorSupport { get; init; }
    public bool DGpuDirectConnectionSupport { get; init; }
    public bool IGpuModeOnlySupport { get; init; }
    public bool LiquidCoolingSupport { get; init; }
    public bool LiquidCoolingAutoModeSupport { get; init; }
    public bool ColorCalibrationSupport { get; init; }
    public bool NumPadSupport { get; init; }
    public bool AcRecoverySwitchSupport { get; init; }
    public bool IsAmdPlatform { get; init; }
    public bool IsNvGpu { get; init; }
    public bool Otasupport { get; init; }
    public bool SmartBalanceSupport { get; init; }
    public bool IsTurboSubModeSupport { get; init; }
    public bool RamFan1p5Support { get; init; }
    public int KeyboardType { get; init; }
    public int PanelType { get; init; }
    public int APVersionCheck { get; init; }
    public int CustomizeTarget { get; init; }
}
