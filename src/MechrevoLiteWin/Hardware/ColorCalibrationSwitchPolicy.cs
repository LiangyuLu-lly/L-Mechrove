namespace MechrevoLite.Hardware;

/// <summary>校色切换的前置判定。</summary>
internal enum ColorCalibrationDecision
{
    /// <summary>条件齐备，可以下发命令。</summary>
    Proceed,

    /// <summary>目标档位已生效，无需再发。</summary>
    AlreadyApplied,

    /// <summary>HDR 已开启：厂商路径会拒绝，必须报失败而不是空发。</summary>
    HdrBlocked,

    /// <summary>未连接或本机不支持校色。</summary>
    Unavailable,
}

/// <summary>
/// 校色切换的纯判据（缺陷 #7：调色无法切换 sRGB 等；手动调整恒失败）。
///
/// <para>成功判据只有一个：**读回 == 请求**。开方向只认档位（sRGB=2），关方向只认开关；
/// HDR 开启时绝不盲发（厂商会丢命令，界面却以为成功）。</para>
/// </summary>
internal static class ColorCalibrationSwitchPolicy
{
    /// <summary>读回是否与请求一致。<paramref name="expectedOn"/> 为真时比对档位，否则比对开关。</summary>
    internal static bool StateMatches(bool expectedOn, bool actualOn, int actualMode, int targetMode) =>
        expectedOn ? actualMode == targetMode : actualOn == expectedOn;

    /// <summary>下发前的判定（顺序：可用 → HDR → 已生效 → 继续）。</summary>
    internal static ColorCalibrationDecision Decide(
        bool connectedAndSupported, bool expectedOn, bool hdrEnabled, bool currentOn, int currentMode, int targetMode)
    {
        if (!connectedAndSupported) return ColorCalibrationDecision.Unavailable;
        if (expectedOn && hdrEnabled) return ColorCalibrationDecision.HdrBlocked;
        return StateMatches(expectedOn, currentOn, currentMode, targetMode)
            ? ColorCalibrationDecision.AlreadyApplied
            : ColorCalibrationDecision.Proceed;
    }
}
