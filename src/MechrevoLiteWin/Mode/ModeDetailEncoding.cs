using System.Globalization;

namespace MechrevoLite.Mode;

/// <summary>
/// <c>SET_OPERATING_MODE_DETAIL</c> 下发的**绝对量**编码（厂商字段名/单位），
/// 与厂商控制台逐条对齐（CCUWinUI <c>49956-50489</c>）。
///
/// <para>纯函数：只做数值/字符串编码，不发布、不碰硬件。服务层是唯一消费方。</para>
/// </summary>
public static class ModeDetailEncoding
{
    /// <summary>TjMax 缺失时厂商按 100 °C 处理。</summary>
    public const int DefaultTjMax = 100;

    /// <summary><c>CpuTccOffset</c> 的绝对量 = <c>TjMax − target</c>（TjMax 为 0 时按 100）。</summary>
    public static int TccOffset(int tjMax, int target) =>
        Math.Clamp((tjMax > 0 ? tjMax : DefaultTjMax) - target, 0, 100);

    /// <summary>PL4 下发值：厂商 double-flag 时**减半**（AMD 机型原样）。</summary>
    public static int Pl4WireWatts(int watts, bool amdPlatform, int scale) =>
        amdPlatform || scale <= 0 ? watts : watts / scale;

    /// <summary>PL 一律以**字符串**下发（厂商载荷是字符串，不是 JSON 数字）。</summary>
    public static string Pl(int watts) => watts.ToString(CultureInfo.InvariantCulture);
}
