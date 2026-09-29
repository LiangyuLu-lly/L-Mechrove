namespace MechrevoLite.Hardware;

/// <summary>
/// 官方键盘固件效果目录（GCU Keyboard/Ctrl SetEffectALL 英文 ID）。
/// CHANGELOG 里的「16 种」是口误：CCU InitalizeKeyboardTypeEffect 的 else 分支是
/// Enum RGBKB_Effect 去掉 Impact/Flash/Mix/Thinking/Devour/UserMode/BatteryPercent/Manual/
/// ColorfulWave/Dawn/ColorMarquee/Twinkling/Sine/Interlace/Diagonal/Music 后剩下的 11 项。
/// 四区 / 四区单色 / EC 单区各有自己的目录（<see cref="LightingEffectCatalog.Keyboard"/>）。
/// HID 路径仍用 <c>RgbForm.HidEffects</c>（BetterRGB 10），不得把中文显示名当 MQTT effect。
/// </summary>
internal static class KeyboardFirmwareEffects
{
    internal static readonly (string Id, string Label)[] All =
        LightingEffectCatalog.PerKeyKeyboard().Select(e => (e.Id, e.Label)).ToArray();

    internal static bool Contains(string id) =>
        Array.Exists(All, e => string.Equals(e.Id, id, StringComparison.Ordinal))
        || Array.Exists(LightingEffectCatalog.FourZoneKeyboard(), e => string.Equals(e.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// ItemSupport\KeyboardType 是官方 RGBKB_Type 的**序号**：1 = SingleZone（EC 单区），
    /// 2 = FourZone，3 = FourZoneSingleColor，≥4 = 逐键各代；0 = Normal（未识别，保持全表）。
    /// 旧实现把 1/2 都当「单区」，于是四区键盘只剩单色/呼吸两项。
    /// </summary>
    internal static bool IsSingleZoneRgb(int keyboardType) =>
        KindFromRegistryOrdinal(keyboardType) == KeyboardLightKind.SingleZone;

    internal static KeyboardLightKind KindFromRegistryOrdinal(int keyboardType) =>
        LightingChannelDetector.KindFromTypeName(LightingChannelDetector.TypeNameFromOrdinal(keyboardType))
        ?? KeyboardLightKind.PerKey;

    internal static (string Id, string Label)[] Visible(KeyboardLightKind kind)
    {
        LightEffectSpec[] specs = LightingEffectCatalog.Keyboard(kind);
        return specs.Length == 0 ? All : specs.Select(e => (e.Id, e.Label)).ToArray();
    }

    internal static (string Id, string Label)[] Visible(int keyboardType) => Visible(KindFromRegistryOrdinal(keyboardType));

    internal static LightEffectSpec? Spec(KeyboardLightKind kind, string effect) =>
        LightingEffectCatalog.Find(LightingEffectCatalog.Keyboard(kind) is { Length: > 0 } specs
            ? specs : LightingEffectCatalog.PerKeyKeyboard(), effect);
}
