namespace MechrevoLite.Hardware;

/// <summary>
/// 官方键盘固件效果目录（GCU Keyboard/Ctrl SetEffectALL 英文 ID）。
/// CHANGELOG 里的「16 种」是口误：CCU InitalizeKeyboardTypeEffect 的 else 分支是
/// Enum RGBKB_Effect 去掉 Impact/Flash/Mix/Thinking/Devour/UserMode/BatteryPercent/Manual/
/// ColorfulWave/Dawn/ColorMarquee/Twinkling/Sine/Interlace/Diagonal/Music 后剩下的 11 项。
/// HID 路径仍用 <c>RgbForm.HidEffects</c>（BetterRGB 10），不得把中文显示名当 MQTT effect。
/// </summary>
internal static class KeyboardFirmwareEffects
{
    internal static readonly (string Id, string Label)[] All =
    {
        ("Single", "单色"),
        ("Breathing", "呼吸"),
        ("Wave", "波浪"),
        ("Reactive", "按键反应"),
        ("Rainbow", "彩虹"),
        ("Ripple", "涟漪"),
        ("Raindrop", "雨滴"),
        ("Marquee", "跑马灯"),
        ("Spark", "火花"),
        ("Aurora", "极光"),
        ("Gaming", "游戏"),
    };

    internal static bool Contains(string id) =>
        Array.Exists(All, e => string.Equals(e.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// 官方 KeyboardType：1/2 = 单区 RGB（只有整键盘单色/呼吸）；0 = 未知（保持全表）；≥3 = 分区/逐键。
    /// </summary>
    internal static bool IsSingleZoneRgb(int keyboardType) => keyboardType is 1 or 2;

    internal static readonly (string Id, string Label)[] SingleZone =
    {
        ("Single", "单色"),
        ("Breathing", "呼吸"),
    };

    internal static (string Id, string Label)[] Visible(int keyboardType) =>
        IsSingleZoneRgb(keyboardType) ? SingleZone : All;
}
