using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// N15 #13 / #16② (field, five machines incl. 50-series): iGPU switching fails. One common root
/// cause, not five: our payload for the iGPU-only action carries a `SetToWMIEC` field that does not
/// exist anywhere in the vendor binaries - it is our own invention. The vendor service and both
/// vendor consoles only ever send the action name (GCUService.decompiled.cs:2375-2383,
/// CCUWinUI metadata heap). An unknown field is at best ignored and at worst rejected, which is why
/// the switch silently fails across generations.
/// </summary>
public class IgpuSwitchPayloadN15Tests
{
    [Fact]
    public void TheIgpuOnlyPayloadCarriesOnlyTheAction()
    {
        Dictionary<string, object> payload = MechrevoService.GpuSwitchPayload(MechrevoService.GpuIGpu);

        Assert.Equal("IGPU_ONLY_CONNECT_RB_ON", payload["Action"]);
        Assert.DoesNotContain("SetToWMIEC", payload.Keys);
    }

    [Fact]
    public void TheIgpuOffPayloadCarriesOnlyTheAction()
    {
        Dictionary<string, object> payload = MechrevoService.GpuSwitchPayload(MechrevoService.GpuStandard);

        Assert.Equal("IGPU_ONLY_CONNECT_RB_OFF", payload["Action"]);
        Assert.DoesNotContain("SetToWMIEC", payload.Keys);
    }

    [Fact]
    public void NoGpuSwitchPayloadInventsAFieldTheVendorDoesNotSend()
    {
        foreach (int mode in new[]
                 {
                     MechrevoService.GpuIGpu, MechrevoService.GpuStandard,
                     MechrevoService.GpuAuto, MechrevoService.GpuDgpu,
                 })
        {
            Dictionary<string, object> payload = MechrevoService.GpuSwitchPayload(mode);
            Assert.DoesNotContain("SetToWMIEC", payload.Keys);
        }
    }

    [Fact]
    public void TheVendorBinariesNeverContainTheInventedField()
    {
        // Guard against reintroducing it: the field must not appear in our source either.
        string service = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Hardware", "MechrevoService.cs");
        Assert.DoesNotContain("SetToWMIEC", service, StringComparison.Ordinal);
    }
}
