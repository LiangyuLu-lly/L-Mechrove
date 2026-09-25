using MechrevoLite.Overlay;

namespace MechrevoLite.Tests;

public class OverlayZOrderTests
{
    [Fact]
    public void ShouldReassertTopmost_when_already_topmost_is_false()
    {
        Assert.False(OverlayZOrderPolicy.ShouldReassertTopmost(true));
    }

    [Fact]
    public void ShouldReassertTopmost_when_style_bit_clear_is_true()
    {
        Assert.True(OverlayZOrderPolicy.ShouldReassertTopmost(false));
    }
}
