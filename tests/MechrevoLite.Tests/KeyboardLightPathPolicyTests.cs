using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// Keyboard-light path policy (single seam, no double-send): the full matrix over
/// availability x hidConnected x serviceConnected x hidWriteTookEffect.
///
/// N9-2 contract: the vendor channel is used when the service is available AND (HID is
/// unsupported/not connected OR the HID brightness write did not take effect). When the HID write
/// works, HID stays primary. Without the service the vendor channel is never used.
/// </summary>
public class KeyboardLightPathPolicyTests
{
    [Theory]
    // No service: never the vendor channel, whatever the HID state.
    [InlineData(FeatureAvailability.Unknown, false, false, false, false)]
    [InlineData(FeatureAvailability.Unknown, true, false, false, false)]
    [InlineData(FeatureAvailability.Unsupported, false, false, false, false)]
    [InlineData(FeatureAvailability.Supported, true, false, false, false)]
    // Unsupported / not connected: vendor channel whenever the service is up.
    [InlineData(FeatureAvailability.Unsupported, false, true, false, true)]
    [InlineData(FeatureAvailability.Unsupported, false, true, true, true)]
    // Supported + connected + the HID write worked: HID stays primary.
    [InlineData(FeatureAvailability.Supported, true, true, true, false)]
    [InlineData(FeatureAvailability.Unknown, true, true, true, false)]
    // N9-2: Supported/Unknown + connected but the HID write did NOT take effect -> vendor channel.
    [InlineData(FeatureAvailability.Supported, true, true, false, true)]
    [InlineData(FeatureAvailability.Unknown, true, true, false, true)]
    // FeatureAvailability is internal: public test methods cannot use it as a parameter type, so
    // InlineData goes through object and is cast.
    public void ShouldUseGcuKeyboardFallback_PinsTheFullMatrix(
        object hid, bool hidConnected, bool serviceConnected, bool hidWriteTookEffect, bool expected)
    {
        Assert.Equal(expected, KeyboardLightPathPolicy.ShouldUseGcuKeyboardFallback(
            (FeatureAvailability)hid, hidConnected, serviceConnected, hidWriteTookEffect));
    }
}
