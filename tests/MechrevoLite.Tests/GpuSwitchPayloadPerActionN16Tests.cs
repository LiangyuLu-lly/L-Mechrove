using MechrevoLite.Gpu;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// N16: the display-route payload differs PER ACTION in the vendor consoles. The 5.56 console
/// (50-series) sends SetToWMIEC="OK" ONLY for IGPU_ONLY_CONNECT_RB_ON and _OFF; AUTO and every
/// DGPU_DIRECT_* action carry the action name alone.
///
/// Evidence (CCUWinUI.decompiled.cs, 5.56):
///   53543-53544  IGPU_ONLY_CONNECT_RB_ON   + SetToWMIEC="OK"
///   53607-53608  IGPU_ONLY_CONNECT_RB_OFF  + SetToWMIEC="OK"
///   53671        IGPU_ONLY_CONNECT_RB_AUTO  (no field)
///   85136        DGPU_DIRECT_CONNECT_TOGGLE_ON   (no field)
///   85152        DGPU_DIRECT_CONNECT_TOGGLE_OFF  (no field)
///   85159        DGPU_DIRECT_CONNECT_TOGGLE_IGPU (no field)
///   85167        DGPU_DIRECT_CONNECT_RESTART     (no field)
///
/// The 40-series console reads val["SetToWMIEC"] for ON/OFF (MySettingManager.cs:625,647) and passes
/// "" for AUTO (:584) - the same per-action split. The parameter is unused in the handler body
/// (:1281-1321), so the field is read-but-ignored; sending it where the vendor does not is a
/// deviation from the reference spec, and one global shape cannot be right for both groups.
/// </summary>
public class GpuSwitchPayloadPerActionN16Tests
{
    [Fact]
    public void IgpuOnlyOnCarriesTheWmiecField()
    {
        Dictionary<string, object> payload = MechrevoService.CreateGpuModePayload(MechrevoService.GpuIGpu);

        Assert.Equal("IGPU_ONLY_CONNECT_RB_ON", payload["Action"]);
        Assert.Equal("OK", payload["SetToWMIEC"]);
    }

    [Fact]
    public void IgpuOnlyOffCarriesTheWmiecField()
    {
        Dictionary<string, object> payload = MechrevoService.CreateGpuModePayload(MechrevoService.GpuStandard);

        Assert.Equal("IGPU_ONLY_CONNECT_RB_OFF", payload["Action"]);
        Assert.Equal("OK", payload["SetToWMIEC"]);
    }

    [Fact]
    public void IgpuOnlyAutoDoesNotCarryTheWmiecField()
    {
        Dictionary<string, object> payload = MechrevoService.CreateGpuModePayload(MechrevoService.GpuAuto);

        Assert.Equal("IGPU_ONLY_CONNECT_RB_AUTO", payload["Action"]);
        Assert.False(payload.ContainsKey("SetToWMIEC"),
            "厂商 AUTO 载荷不带 SetToWMIEC（CCUWinUI:53671；40 系 MySettingManager.cs:584 传空串）。");
    }

    [Fact]
    public void DgpuDirectToggleOnDoesNotCarryTheWmiecField()
    {
        Dictionary<string, object> payload = MechrevoService.CreateGpuModePayload(MechrevoService.GpuDgpu);

        Assert.Equal("DGPU_DIRECT_CONNECT_TOGGLE_ON", payload["Action"]);
        Assert.False(payload.ContainsKey("SetToWMIEC"),
            "厂商 DGPU_DIRECT_* 载荷不带 SetToWMIEC（CCUWinUI:85136/85152/85159/85167）。");
    }

    [Fact]
    public void TheDirectConnectIgpuToggleDoesNotCarryTheWmiecField()
    {
        Dictionary<string, object> payload = MechrevoService.CreateGpuSwitchPayload(
            MechrevoService.GpuIGpu, supportsDgpuDirect: true, supportsIgpuOnly: false, useHotSwitch: false);

        Assert.Equal("DGPU_DIRECT_CONNECT_TOGGLE_IGPU", payload["Action"]);
        Assert.False(payload.ContainsKey("SetToWMIEC"));
    }

    /// <summary>
    /// The vendor's direct-connect sequence is ordered and repeats TOGGLE_ON around the iGPU-off:
    /// TOGGLE_ON, IGPU_ONLY_CONNECT_RB_OFF(+SetToWMIEC), TOGGLE_ON, then RESTART after 800 ms
    /// (CCUWinUI:85136-85168). Our restart route must reproduce that shape.
    /// </summary>
    [Fact]
    public void TheDirectConnectRestartRouteMatchesTheVendorSequence()
    {
        IReadOnlyList<Dictionary<string, object>> route = MechrevoService.CreateGpuRestartTargetPayloads(
            MechrevoService.GpuDgpu, supportsDgpuDirect: true, DgpuGenerationKind.Gen50);

        Assert.Equal(3, route.Count);
        Assert.Equal("DGPU_DIRECT_CONNECT_TOGGLE_ON", route[0]["Action"]);
        Assert.Equal("IGPU_ONLY_CONNECT_RB_OFF", route[1]["Action"]);
        Assert.Equal("OK", route[1]["SetToWMIEC"]);
        Assert.Equal("DGPU_DIRECT_CONNECT_TOGGLE_ON", route[2]["Action"]);
    }
}
