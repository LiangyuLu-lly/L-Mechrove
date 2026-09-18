using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// N9-2 (owner): on a 40-series machine the vendor console CAN adjust keyboard brightness and ours
/// cannot. The keyboard is multi-colour (so not a SingleColorKeyboardProjectIDs machine) and the
/// machine is not read-only.
///
/// The reverted attempt (5a6cf0a, backed out in 210b2ff) failed because it decided the fallback from
/// a brightness write issued during form construction - before any device exists - which returned
/// false and permanently routed an Unknown controller to the vendor channel, skipping the HID probe.
///
/// These tests pin the corrected contract: the fallback is driven by the outcome of the REAL effect
/// frame at runtime, and a construction-time (no-device) write must never poison the decision.
/// </summary>
public class KeyboardBrightnessFallbackN92Tests
{
    /// <summary>
    /// Given a machine whose HID controller is present and connected but whose brightness write does
    /// not take effect, When the vendor service is available, Then the vendor channel is used.
    /// </summary>
    [Fact]
    public void TheVendorChannelIsUsedWhenTheHidBrightnessWriteDoesNotTakeEffect()
    {
        bool useVendor = KeyboardLightPathPolicy.ShouldUseGcuKeyboardFallback(
            FeatureAvailability.Supported, hidConnected: true, serviceConnected: true,
            hidBrightnessTookEffect: false);

        Assert.True(useVendor);
    }

    /// <summary>
    /// Given the HID brightness write DOES take effect, When the vendor service is available,
    /// Then HID stays primary - no double-send.
    /// </summary>
    [Fact]
    public void HidStaysPrimaryWhenItsBrightnessWriteTakesEffect()
    {
        bool useVendor = KeyboardLightPathPolicy.ShouldUseGcuKeyboardFallback(
            FeatureAvailability.Supported, hidConnected: true, serviceConnected: true,
            hidBrightnessTookEffect: true);

        Assert.False(useVendor);
    }

    /// <summary>
    /// Given no vendor service, When the HID brightness write does not take effect,
    /// Then the vendor channel is NOT used - we never pretend success without a service.
    /// </summary>
    [Fact]
    public void TheVendorChannelIsNeverUsedWithoutTheService()
    {
        bool useVendor = KeyboardLightPathPolicy.ShouldUseGcuKeyboardFallback(
            FeatureAvailability.Supported, hidConnected: true, serviceConnected: false,
            hidBrightnessTookEffect: false);

        Assert.False(useVendor);
    }

    /// <summary>
    /// Given an Unknown verdict (never probed), When the HID brightness write has not been observed
    /// to fail, Then the vendor channel is NOT used - Unknown keeps today's HID ladder.
    /// This is the exact regression the reverted attempt introduced.
    /// </summary>
    [Fact]
    public void UnknownKeepsTheHidLadderWhenNoFailureWasObserved()
    {
        bool useVendor = KeyboardLightPathPolicy.ShouldUseGcuKeyboardFallback(
            FeatureAvailability.Unknown, hidConnected: false, serviceConnected: true,
            hidBrightnessTookEffect: true);

        Assert.False(useVendor);
    }

    /// <summary>
    /// Given a definitely-unsupported controller with no HID connection, When the service is
    /// available, Then the vendor channel is used (the pre-existing behaviour must survive).
    /// </summary>
    [Fact]
    public void UnsupportedWithoutHidStillUsesTheVendorChannel()
    {
        bool useVendor = KeyboardLightPathPolicy.ShouldUseGcuKeyboardFallback(
            FeatureAvailability.Unsupported, hidConnected: false, serviceConnected: true,
            hidBrightnessTookEffect: true);

        Assert.True(useVendor);
    }
}
