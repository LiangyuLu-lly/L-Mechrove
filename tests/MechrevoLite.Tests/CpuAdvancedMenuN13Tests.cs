using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// N13 (owner): the CPU advanced menu only takes effect on 50-series machines; on 30/40-series it
/// does nothing. That is the vendor's own design and is allowed - so we must NOT make it work there,
/// we must HIDE it there. The availability must come from the vendor's own capability signal, not a
/// hard-coded generation check.
/// </summary>
public class CpuAdvancedMenuN13Tests
{
    [Fact]
    public void TheMenuIsHiddenWhenTheVendorDoesNotReportTheCapability()
    {
        // The vendor's own capability bit is absent -> hidden, whatever the generation.
        MechrevoDeviceCapabilities caps = MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>());

        Assert.False(caps.CpuPerformanceTuning);
    }

    [Fact]
    public void TheMenuIsShownWhenTheVendorReportsTheCapability()
    {
        var values = new Dictionary<string, object?> { ["CPUPerformanceAndOverClockMenuSupport"] = 1 };
        MechrevoDeviceCapabilities caps = MechrevoDeviceCapabilities.FromValues(values);

        Assert.True(caps.CpuPerformanceTuning);
    }

    [Fact]
    public void TheGateUsesTheVendorCapabilityNotAHardCodedGeneration()
    {
        string source = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Hardware", "MechrevoHw.cs");
        // The gate must consult the vendor capability; a generation literal would be the wrong fix.
        Assert.Contains("SupportsOverclockMenu", source, StringComparison.Ordinal);
        Assert.Contains("CPUPerformanceAndOverClockMenuSupport", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMenuIsNotOfferedWhenTheStateFrameNeverReportedIt()
    {
        // CpuAdvancedPerformanceSeen is only set when the service actually reports the field; a
        // machine whose service never reports it must not show the entry.
        string source = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Hardware", "MechrevoHw.cs");
        Assert.Contains("CpuAdvancedPerformanceSeen && SupportsOverclockMenu", source, StringComparison.Ordinal);
    }
}
