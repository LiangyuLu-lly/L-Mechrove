using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// Cold start: MQTT <c>IgpuOnlyStatusSupport</c> may still be null.
/// Registry <c>iGPUModeOnlySupport</c> overlays until the bit arrives.
/// MQTT 0 wins; MQTT 1 still offers.
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class IgpuOnlyColdStartOverlayTests
{
    [Fact]
    public void NullMqtt_WithRegistryThreeModeBit_OffersIgpuOnly()
    {
        using var generation = GpuCapabilityGatingHarness.Generation(GpuCapabilityGatingHarness.Gen40);
        using MechrevoHw hardware = GpuCapabilityGatingHarness.Hardware(new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            IgpuOnly = true,
            DgpuDirect = true,
        });
        hardware.SetIgpuOnlyStatusSupportForTests(null);

        Assert.True(hardware.CanOfferIgpuOnly);
    }

    [Fact]
    public void MqttBitZero_WinsOverRegistryThreeModeBit()
    {
        using var generation = GpuCapabilityGatingHarness.Generation(GpuCapabilityGatingHarness.Gen40);
        using MechrevoHw hardware = GpuCapabilityGatingHarness.Hardware(new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            IgpuOnly = true,
            DgpuDirect = true,
        });
        hardware.SetIgpuOnlyStatusSupportForTests(false);

        Assert.False(hardware.CanOfferIgpuOnly);
    }

    [Fact]
    public void MqttBitOne_StillOffersIgpuOnly()
    {
        using var generation = GpuCapabilityGatingHarness.Generation(GpuCapabilityGatingHarness.Gen40);
        using MechrevoHw hardware = GpuCapabilityGatingHarness.Hardware(new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            DgpuDirect = true,
        });
        hardware.SetIgpuOnlyStatusSupportForTests(true);

        Assert.True(hardware.CanOfferIgpuOnly);
    }
}
