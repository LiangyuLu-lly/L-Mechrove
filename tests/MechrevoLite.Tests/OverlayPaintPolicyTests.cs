using MechrevoLite.Overlay;

namespace MechrevoLite.Tests;

public class OverlayPaintPolicyTests
{
    [Fact]
    public void ShouldInvalidate_when_fingerprint_unchanged_is_false()
    {
        Assert.False(OverlayPaintPolicy.ShouldInvalidate("CPU: 45° 4.2G", "CPU: 45° 4.2G"));
    }

    [Fact]
    public void ShouldInvalidate_when_fingerprint_changed_is_true()
    {
        Assert.True(OverlayPaintPolicy.ShouldInvalidate("CPU: 45° 4.2G", "CPU: 46° 4.2G"));
    }
}
