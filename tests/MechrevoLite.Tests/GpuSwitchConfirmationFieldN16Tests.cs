using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// N16: the iGPU-only confirmation signal has TWO wire encodings across generations, and we only
/// understood one of them.
///
/// 5.56 (50-series) console: reads <c>CheckDGpuStatusforIGpuOnlyOnSuccess</c> and parses
/// <c>Contains("1") ? 1 : Contains("2") ? 2 : 0</c> (CCUWinUI.decompiled.cs:54637-54640).
///
/// 40-series console: emits <c>CheckDGpuStatusforIGpuOnlySwitch</c> with the EC byte value -
/// <b>85</b> = success, <b>170</b> = failure (MySettingManager.cs:842, 1292/1297/1306/1311/1316).
/// The 5.56 console's own status DTO declares BOTH fields (137930 and 137932), so a service may
/// emit either name.
///
/// Our reader only looked at <c>...OnSuccess</c> and parsed 1/2. On a machine whose service emits
/// <c>...Switch</c> = 85, that parse yields 0, so confirmation never succeeds - the systematic cause
/// behind "iGPU switching fails on five machines".
/// </summary>
public class GpuSwitchConfirmationFieldN16Tests
{
    static MechrevoHw Hardware() => new(null, new MechrevoDeviceCapabilities { IgpuOnly = true });

    /// <summary>Given the 5.56 encoding, When the status reports OnSuccess=2, Then the result is 2.</summary>
    [Fact]
    public void TheOnSuccessEncodingStillParses()
    {
        using MechrevoHw hw = Hardware();
        hw.HandleMessage("Setting/Status",
            """{"IGpuOnlyConnectionSwitch_Status":"IGPU_ONLY_CONNECT_RB_ON","CheckDGpuStatusforIGpuOnlyOnSuccess":"2"}""");

        Assert.Equal(2, hw.GpuSwitchResult);
    }

    /// <summary>
    /// Given the 40-series encoding, When the status reports Switch=85, Then the result is the
    /// success value 2 (85 is the EC "iGPU-only active" byte).
    /// </summary>
    [Fact]
    public void TheSwitchEncodingMaps85ToSuccess()
    {
        using MechrevoHw hw = Hardware();
        hw.HandleMessage("Setting/Status",
            """{"IGpuOnlyConnectionSwitch_Status":"IGPU_ONLY_CONNECT_RB_ON","CheckDGpuStatusforIGpuOnlySwitch":"85"}""");

        Assert.Equal(2, hw.GpuSwitchResult);
    }

    /// <summary>
    /// Given the 40-series encoding, When the status reports Switch=170, Then the result is the
    /// failure value 1 (170 is the EC "not ready / not supported" byte).
    /// </summary>
    [Fact]
    public void TheSwitchEncodingMaps170ToFailure()
    {
        using MechrevoHw hw = Hardware();
        hw.HandleMessage("Setting/Status",
            """{"IGpuOnlyConnectionSwitch_Status":"IGPU_ONLY_CONNECT_RB_OFF","CheckDGpuStatusforIGpuOnlySwitch":"170"}""");

        Assert.Equal(1, hw.GpuSwitchResult);
    }

    /// <summary>
    /// Given both fields present, When they disagree, Then the explicit OnSuccess field wins - it is
    /// the newer, more specific signal.
    /// </summary>
    [Fact]
    public void TheOnSuccessFieldWinsWhenBothArePresent()
    {
        using MechrevoHw hw = Hardware();
        hw.HandleMessage("Setting/Status",
            """{"IGpuOnlyConnectionSwitch_Status":"IGPU_ONLY_CONNECT_RB_ON","CheckDGpuStatusforIGpuOnlyOnSuccess":"2","CheckDGpuStatusforIGpuOnlySwitch":"170"}""");

        Assert.Equal(2, hw.GpuSwitchResult);
    }
}
