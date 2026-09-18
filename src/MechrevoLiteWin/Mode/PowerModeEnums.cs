using MechrevoLite.Hardware;

namespace MechrevoLite.Mode;

/// <summary>
/// 厂商 <c>SysPowerModeIndex</c>（40B 实测 1483-1492）。**与 <see cref="ConsoleOperatingMode"/>
/// 是两套枚举，禁止互相强转**：数值恰好重叠但语义不同。
/// </summary>
public enum VendorSysPowerMode
{
    Performance = 1,
    Balanced = 2,
    BatterySaver = 3,
    Benchmark = 4,
}

/// <summary>
/// 控制台/服务的 <c>OperatingMode</c>（Fan/Status 上报与 OPERATING_*_MODE 指令使用）：
/// 0=Office 1=Gaming 2=Turbo 3=Customize。**不是** <see cref="VendorSysPowerMode"/>。
/// </summary>
public enum ConsoleOperatingMode
{
    Office = 0,
    Gaming = 1,
    Turbo = 2,
    Customize = 3,
}

/// <summary>
/// 两套模式枚举之间的**显式命名转换**（禁止隐式同值转换）。
/// 只用于遥测/对照；下发的永远是 <see cref="ConsoleOperatingMode"/> 的 OPERATING_*_MODE 指令与绝对量。
/// </summary>
public static class PowerModeMapping
{
    /// <summary>UI/服务视觉枚举（<c>MechrevoService.ModeGaming=0/ModeTurbo=1/ModeOffice=2/ModeCustom=3</c>）→ 控制台模式。</summary>
    public static ConsoleOperatingMode FromVisualMode(int visualMode) => visualMode switch
    {
        MechrevoService.ModeGaming => ConsoleOperatingMode.Gaming,
        MechrevoService.ModeTurbo => ConsoleOperatingMode.Turbo,
        MechrevoService.ModeOffice => ConsoleOperatingMode.Office,
        MechrevoService.ModeCustom => ConsoleOperatingMode.Customize,
        _ => throw new ArgumentOutOfRangeException(nameof(visualMode), visualMode, "unknown visual mode"),
    };

    /// <summary>控制台模式 → 视觉枚举值。</summary>
    public static int ToVisualMode(ConsoleOperatingMode mode) => mode switch
    {
        ConsoleOperatingMode.Office => MechrevoService.ModeOffice,
        ConsoleOperatingMode.Gaming => MechrevoService.ModeGaming,
        ConsoleOperatingMode.Turbo => MechrevoService.ModeTurbo,
        ConsoleOperatingMode.Customize => MechrevoService.ModeCustom,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown console mode"),
    };

    /// <summary>控制台模式 → 厂商 <c>SysPowerModeIndex</c>；Customize 无厂商对应项（返回 false）。</summary>
    public static bool TryToVendor(ConsoleOperatingMode mode, out VendorSysPowerMode vendor)
    {
        switch (mode)
        {
            case ConsoleOperatingMode.Office:
                vendor = VendorSysPowerMode.Balanced;
                return true;
            case ConsoleOperatingMode.Gaming:
                vendor = VendorSysPowerMode.Performance;
                return true;
            case ConsoleOperatingMode.Turbo:
                vendor = VendorSysPowerMode.Benchmark;
                return true;
            default:
                vendor = default;
                return false;
        }
    }
}
