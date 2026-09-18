namespace MechrevoLite.Hardware;

/// <summary>
/// Keyboard-light path policy (single seam): decides whether the vendor (GCU) channel is used.
///
/// N9-2 correction: the old rule was "definitely unsupported + HID not connected + GCU available".
/// Field bug on a 40-series machine: its HID controller IS present and connected, but the brightness
/// write does not take effect - the slider moves and the light does not change, while the vendor's
/// own channel could adjust it. The old rule kept such a machine on the HID path forever, so we were
/// worse than the vendor.
///
/// New rule: GCU available AND (HID unsupported/not connected OR the HID brightness write did not
/// take effect) -> vendor channel. When the HID write works, HID stays primary (no double-send).
/// Without the service the vendor channel is never used - never silently pretend success.
/// </summary>
internal static class KeyboardLightPathPolicy
{
    /// <summary>
    /// Whether to use the vendor (GCU) channel. <paramref name="hidWriteTookEffect"/> false means the
    /// HID brightness write did not take effect (device present but the field is not honoured), in
    /// which case we fall back to the vendor channel instead of silently doing nothing.
    /// </summary>
    internal static bool ShouldUseGcuKeyboardFallback(
        FeatureAvailability hid, bool hidConnected, bool serviceConnected, bool hidWriteTookEffect)
    {
        if (!serviceConnected) return false;
        if (hid == FeatureAvailability.Unsupported || !hidConnected) return true;
        return !hidWriteTookEffect;
    }
}