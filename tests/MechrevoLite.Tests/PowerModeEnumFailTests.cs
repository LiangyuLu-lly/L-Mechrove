using MechrevoLite.Mode;

namespace MechrevoLite.Tests;

/// <summary>
/// T19 失败/边界路径：两套枚举不可互转、越界视觉模式被拒、绝对量编码对越界输入 fail-closed。
/// happy 路径见 <see cref="PowerModeEnumTests"/>。
/// </summary>
public class PowerModeEnumFailTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void AnOutOfRangeVisualModeIsRejected(int visualMode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PowerModeMapping.FromVisualMode(visualMode));
    }

    [Fact]
    public void TheVendorEnumHasNoCustomizeMember()
    {
        // Customize 只存在于控制台枚举；厂商索引里没有对应项。
        Assert.DoesNotContain("Customize", Enum.GetNames<VendorSysPowerMode>());
        Assert.Contains("Customize", Enum.GetNames<ConsoleOperatingMode>());
    }

    [Fact]
    public void TryingToMapCustomizeToVendorFails()
    {
        Assert.False(PowerModeMapping.TryToVendor(ConsoleOperatingMode.Customize, out VendorSysPowerMode vendor));
        Assert.Equal(default, vendor);
    }

    [Theory]
    [InlineData(0, 10, 90)]      // TjMax 0 -> 100
    [InlineData(100, 100, 0)]
    [InlineData(100, 0, 100)]
    public void TccOffsetStaysInRange(int tjMax, int target, int expected)
    {
        int value = ModeDetailEncoding.TccOffset(tjMax, target);
        Assert.Equal(expected, value);
        Assert.InRange(value, 0, 100);
    }

    [Theory]
    [InlineData(200, 0)]
    [InlineData(200, -1)]
    public void ANonPositivePl4ScaleNeverDividesByZero(int watts, int scale)
    {
        Assert.Equal(watts, ModeDetailEncoding.Pl4WireWatts(watts, amdPlatform: false, scale));
    }

    [Fact]
    public void PlFormattingIsCultureInvariant()
    {
        Assert.Equal("1234", ModeDetailEncoding.Pl(1234));
        Assert.DoesNotContain(",", ModeDetailEncoding.Pl(1234));
    }
}
