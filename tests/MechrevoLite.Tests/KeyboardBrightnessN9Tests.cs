using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// N9-2 (field bug, 40-series machine): the vendor console CAN adjust keyboard brightness there,
/// ours cannot. The keyboard is multi-colour (so NOT a SingleColorKeyboardProjectIDs machine - that
/// lead is excluded) and the machine is NOT in read-only mode.
///
/// Root cause: the GCU brightness path is gated on `hid == Unsupported &amp;&amp; !hidConnected`
/// (KeyboardLightPathPolicy). On a machine whose HID controller IS present and connected, the
/// brightness slider only ever writes the HID report; if that controller does not honour the
/// brightness field, the slider moves and nothing happens - while the vendor's own path
/// (Keyboard/Ctrl SetEffectALL with `light`) would have worked.
///
/// The fix: when the HID write does not take effect, fall back to the vendor's MQTT path instead of
/// silently doing nothing. HID is a normal device channel and is NOT covered by the EC-write /
/// firmware-write bans.
/// </summary>
public class KeyboardBrightnessN9Tests
{
    // ---- the vendor path is reachable whenever the service is up -------------

    [Fact]
    public void TheVendorPathIsUsedWhenHidIsConnectedButTheWriteDidNotTakeEffect()
    {
        // HID present and connected, but the brightness write did not take effect -> vendor path.
        Assert.True(KeyboardLightPathPolicy.ShouldUseGcuKeyboardFallback(
            FeatureAvailability.Supported, hidConnected: true, serviceConnected: true, hidWriteTookEffect: false));
    }

    [Fact]
    public void TheVendorPathIsUsedWhenHidIsUnsupported()
    {
        Assert.True(KeyboardLightPathPolicy.ShouldUseGcuKeyboardFallback(
            FeatureAvailability.Unsupported, hidConnected: false, serviceConnected: true, hidWriteTookEffect: false));
    }

    [Fact]
    public void TheVendorPathIsUsedWhenHidIsUnknownButTheWriteDidNotTakeEffect()
    {
        Assert.True(KeyboardLightPathPolicy.ShouldUseGcuKeyboardFallback(
            FeatureAvailability.Unknown, hidConnected: true, serviceConnected: true, hidWriteTookEffect: false));
    }

    // ---- HID stays the primary path when it works ---------------------------

    [Fact]
    public void HidStaysPrimaryWhenItsWriteTookEffect()
    {
        Assert.False(KeyboardLightPathPolicy.ShouldUseGcuKeyboardFallback(
            FeatureAvailability.Supported, hidConnected: true, serviceConnected: true, hidWriteTookEffect: true));
    }

    // ---- no service => no vendor path (never silently pretend) --------------

    [Fact]
    public void WithoutTheServiceTheVendorPathIsNotUsed()
    {
        Assert.False(KeyboardLightPathPolicy.ShouldUseGcuKeyboardFallback(
            FeatureAvailability.Supported, hidConnected: true, serviceConnected: false, hidWriteTookEffect: false));
        Assert.False(KeyboardLightPathPolicy.ShouldUseGcuKeyboardFallback(
            FeatureAvailability.Unsupported, hidConnected: false, serviceConnected: false, hidWriteTookEffect: false));
    }

    // ---- the vendor payload carries the brightness --------------------------

    [Fact]
    public void TheVendorBrightnessPayloadUsesTheSetEffectAllLightField()
    {
        // The vendor protocol has no standalone "set brightness" command: `light` rides on
        // SetEffectALL. Pin the field name and the 0..4 domain the service expects.
        Assert.Equal("SetEffectALL", MechrevoService.KeyboardEffectFunction);
        Assert.Equal("light", MechrevoService.KeyboardEffectLightField);
        Assert.Equal(0, KeyboardRgb.MapSoftwareBrightnessToHardwareLevel(0));
        Assert.Equal(4, KeyboardRgb.MapSoftwareBrightnessToHardwareLevel(100));
    }

    [Fact]
    public void TheBrightnessLevelIsClampedToTheVendorDomain()
    {
        Assert.Equal(0, KeyboardRgb.MapSoftwareBrightnessToHardwareLevel(-50));
        Assert.Equal(4, KeyboardRgb.MapSoftwareBrightnessToHardwareLevel(999));
    }
}
