namespace MechrevoLite.Hardware;

/// <summary>
/// 液冷档位回显：把「用户意图」（AppConfig 的 lc_pump_profile / lc_fan_profile）与
/// 「厂商回读」合成界面应当显示的档位。
///
/// 自动档在回读里没有自己的值，只按回读同步会把「自动」挤成具体手动档——用户看到的就是
/// 选完自动几秒后又跳回去：
/// - 泵速：厂商协议没有自动档，客户端按温度挑一档（0..2）下发，回读只会有那个具体档位；
/// - 风扇：厂商自动档回读 LC_FanCtrl=4，而 BLE 直连通道的「100%」也是 4，同值不同义。
/// </summary>
internal static class LiquidCoolingDisplayPolicy
{
    /// <summary>GCU 通道的厂商风扇自动档索引（BT_LC/Control LC_FanCtrl=4）。</summary>
    internal const int GcuFanAutoIndex = 4;

    /// <summary>
    /// 手动档的界面文本：最高档读作「最大」，其余档位保持传入的百分比文本。
    /// 2026-09-13 用户要求（泵/风扇最高档都是 90%，界面里显示「最大」）。
    /// </summary>
    internal static string GearLabel(string percentLabel, int profile, int topProfile) =>
        profile == topProfile ? "最大" : percentLabel;

    /// <summary>
    /// 档位→界面百分比文本（与档位下拉共用同一张表：泵 45/60/90，风扇 40/50/60/90）。
    /// 下拉与组摘要都从这里取字符串，避免两处各写一份后漂移。
    /// </summary>
    internal static readonly string[] PumpGearLabels = { "45%", "60%", "90%" };
    internal static readonly string[] FanGearLabels = { "40%", "50%", "60%", "90%" };

    /// <summary>
    /// 组摘要里的单通道档位文本（2026-09-14 用户要求：手动档也要在摘要里出现）：
    /// 自动→「泵自动」，最高档→「泵最大」（复用 GearLabel 的措辞），其余→「泵60%」；
    /// 未设置（ProfileUnset/越界）→ null（摘要里不出现该通道）。
    /// </summary>
    internal static string? GearSummaryLabel(string channelPrefix, string[] gearLabels, int profile, int topProfile)
    {
        if (profile == WaterCoolerBle.ProfileAutomatic) return channelPrefix + "自动";
        if (profile < 0 || profile >= gearLabels.Length) return null;
        return channelPrefix + GearLabel(gearLabels[profile], profile, topProfile);
    }

    internal static int FanProfile(int intentProfile, int reportedFanCtrl) =>
        intentProfile == WaterCoolerBle.ProfileAutomatic || reportedFanCtrl == GcuFanAutoIndex
            ? WaterCoolerBle.ProfileAutomatic
            : reportedFanCtrl;

    internal static int PumpProfile(int intentProfile, int reportedPumpCtrl) =>
        intentProfile == WaterCoolerBle.ProfileAutomatic
            ? WaterCoolerBle.ProfileAutomatic
            : reportedPumpCtrl;
}
