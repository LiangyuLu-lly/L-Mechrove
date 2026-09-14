using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

public class FanCurveTests
{
    [Fact]
    public void NormalizeFanCurve_AllowsZeroDutyAt48DegreePoint()
    {
        var temperatures = new byte[] { 0, 48, 52, 56, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255 };
        var requested = new[] { 0, 0, 35, 40 };

        var normalized = MechrevoHw.NormalizeFanCurve(requested, temperatures);

        Assert.Equal(0, normalized[1]);
        Assert.Equal(35, normalized[2]);
        Assert.Equal(40, normalized[3]);
    }

    [Fact]
    public void NormalizeFanCurve_AllowsFullDutyRangeAt48DegreePoint()
    {
        var temperatures = new byte[] { 0, 48, 52, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255 };

        Assert.Equal(0, MechrevoHw.NormalizeFanCurve(new[] { 0, 0, 20 }, temperatures)[1]);
        Assert.Equal(100, MechrevoHw.NormalizeFanCurve(new[] { 0, 100, 100 }, temperatures)[1]);
    }
}
