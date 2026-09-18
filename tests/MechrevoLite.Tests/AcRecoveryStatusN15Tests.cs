using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// N15 #17 (field, 25耀世16U 70ti+275hx): the "来电自启" (AC recovery) icon shows OFF while the feature
/// is actually ON - a state read-back/parse mismatch.
///
/// The vendor service exposes TWO fields: `AcRecoverySwitch_Status` (the string we already parse) and
/// `ACRecoveryStatus` (a hex-encoded byte, GCUService.decompiled.cs:21092). We never read the latter.
/// When the string field is absent or carries an unexpected spelling, the icon falls back to OFF even
/// though the byte says ON.
/// </summary>
public class AcRecoveryStatusN15Tests
{
    [Fact]
    public void TheHexByteStatusIsParsedWhenTheStringFieldIsAbsent()
    {
        // Only the byte field is present, and it says ON (0x01).
        var values = new Dictionary<string, object?> { ["ACRecoveryStatus"] = "0x01" };
        MechrevoDeviceCapabilities caps = MechrevoDeviceCapabilities.FromValues(values);

        Assert.True(caps.AcRecoveryOn);
    }

    [Fact]
    public void TheHexByteStatusSaysOffWhenZero()
    {
        var values = new Dictionary<string, object?> { ["ACRecoveryStatus"] = "0x00" };
        MechrevoDeviceCapabilities caps = MechrevoDeviceCapabilities.FromValues(values);

        Assert.False(caps.AcRecoveryOn);
    }

    [Fact]
    public void TheStringFieldStillWinsWhenPresent()
    {
        // The string field is the primary source; the byte is the fallback.
        var values = new Dictionary<string, object?>
        {
            ["AcRecoverySwitch_Status"] = "ACRECOVERY_TOGGLE_ON",
            ["ACRecoveryStatus"] = "0x00",
        };
        MechrevoDeviceCapabilities caps = MechrevoDeviceCapabilities.FromValues(values);

        Assert.True(caps.AcRecoveryOn);
    }

    [Fact]
    public void AnUnreadableStatusIsUnknownNotOff()
    {
        // Neither field present: the state is unknown, which must not be reported as OFF.
        MechrevoDeviceCapabilities caps = MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>());

        Assert.Null(caps.AcRecoveryOn);
    }
}
