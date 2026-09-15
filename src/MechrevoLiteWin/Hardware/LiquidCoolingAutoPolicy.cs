namespace MechrevoLite.Hardware;

/// <summary>
/// 液冷「自动」的刷新/下发决策（纯函数，3 秒状态定时器、档位处理器与单测共用）。
///
/// GCU 通道的自动是**厂商固件曲线**，不是客户端曲线：BT_LC/Control LC_FanCtrl=4 让设备进入
/// LC_CoolingAuto，固件自己按温度驱动泵与风扇（GCUService 的 PumpFanAuto/RunCoolingAuto）。
/// 厂商协议里泵没有独立自动档，官方界面也只在风扇档位表里提供「自动」——所以本应用里
/// 「泵自动」同样走这条厂商自动通道，客户端不提供泵的固定档自动曲线。
///
/// 曾经的 GCU 自动实现是客户端按温度挑一个具体泵速档（0..2）周期下发：3 秒一次的具体档位
/// 会把固件曲线钉死在那一刻的读数上，负载升高时泵不再跟随固件升速（用户报告的
/// 「自动模式高负载不升速」）。因此自动意图下的刷新只允许补发「进入自动」这一条命令，
/// 绝不重写具体档位。
///
/// BLE 直连通道没有厂商自动档（协议只有具体占空比），自动 = 客户端温度曲线；此时温度读数
/// 无效或过期必须保持现状，绝不能拿 0°C 去挑最低档。
/// </summary>
internal static class LiquidCoolingAutoPolicy
{
    /// <summary>定时刷新在 GCU 通道应当执行的动作。刻意没有「写具体档位」这一项。</summary>
    public enum RefreshAction
    {
        None,
        EnableVendorAuto,
    }

    /// <summary>温度读数在自动决策里的有效期；超过视为「没读到」。</summary>
    internal static readonly TimeSpan TemperatureFreshness = TimeSpan.FromSeconds(30);

    /// <summary>厂商风扇自动档（BT_LC/Control LC_FanCtrl=4 → LC_CoolingAuto）是否已在设备上生效。</summary>
    internal static bool IsVendorAutoActive(int reportedFanCtrl) =>
        reportedFanCtrl == LiquidCoolingDisplayPolicy.GcuFanAutoIndex;

    /// <summary>温度是否可用于挑自动档；0/负数表示读不到，绝不当成 0°C 处理。</summary>
    internal static bool TemperatureUsable(int temperatureC) => temperatureC > 0;

    /// <summary>合成自动决策使用的温度；读数过期时返回 0（不可用）。</summary>
    internal static int ResolveCoolingTemperature(int cpuTemp, int gpuTemp, bool fresh) =>
        fresh ? Math.Max(cpuTemp, gpuTemp) : 0;

    /// <summary>
    /// GCU 通道定时刷新的唯一决策点。只要用户把泵或风扇任一处选了「自动」，本应用就应当让
    /// 设备停留在厂商自动档：未在自动档就补发一次进入命令，已在自动档就什么都不做。
    /// 任何情况下都不会返回一个具体档位写入——那是覆盖固件曲线。
    /// </summary>
    internal static RefreshAction DecideGcuRefresh(bool pumpAuto, bool fanAuto, int reportedFanCtrl)
    {
        if (!pumpAuto && !fanAuto) return RefreshAction.None;
        return IsVendorAutoActive(reportedFanCtrl) ? RefreshAction.None : RefreshAction.EnableVendorAuto;
    }

    /// <summary>档位下拉选中项是否代表「自动」：是则下发厂商自动档而非具体档位。</summary>
    internal static bool ShouldTransmitVendorAuto(int selectedProfile) =>
        selectedProfile == WaterCoolerBle.ProfileAutomatic;
}
