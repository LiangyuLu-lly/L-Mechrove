using MechrevoLite.Fan;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T21 失败/边界路径：越界模式被拒；厂商"双位同置返回 0"的读回被如实保留；
/// 编码不越界；缺值 fail-closed。happy 路径见 <see cref="FanBoostEncodingTests"/>。
/// </summary>
public class FanBoostEncodingFailTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(99)]
    public void AnOutOfRangeLogicalModeIsRejected(int mode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FanBoostEncoding.Encode(mode, boostEnabled: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => FanBoostEncoding.Encode(mode, boostEnabled: true));
    }

    [Fact]
    public void BothModeBitsSetDecodesToZeroLikeTheVendor()
    {
        // bit4=1 且 bit7=1：厂商 GetFanMode 保留 result=0，不得猜成别的模式。
        Assert.Equal(0, FanBoostEncoding.Decode(0x90));
        Assert.Equal(0, FanBoostEncoding.Decode(0xD0));
    }

    [Fact]
    public void AnOutOfRangeSetterParameterIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FanBoostEncoding.LogicalModeForSetterParameter(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => FanBoostEncoding.LogicalModeForSetterParameter(-1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TheEncoderNeverSetsBitsOutsideTheVendorMask(int mode)
    {
        foreach (bool boost in new[] { false, true })
        {
            byte encoded = FanBoostEncoding.Encode(mode, boost);
            Assert.Equal(0, encoded & ~(byte)0xF0);   // 只有 0xA0/0x10/0x40 这些位被使用
        }
    }

    [Fact]
    public void TheControlEncoderNeverSetsBitsOutside0To2()
    {
        foreach (bool ctgp in new[] { false, true })
        foreach (bool db in new[] { false, true })
        foreach (bool funCtrl in new[] { false, true })
        {
            byte encoded = GpuPowerControlEncoding.EncodeControl(ctgp, db, funCtrl);
            Assert.Equal(0, encoded & ~(byte)0x07);
        }
    }

    [Fact]
    public void AMissingFanBoostBitIsFailClosed()
    {
        Assert.False(FanBoostEncoding.IsAvailable(FeatureMatrix.FromValues(new Dictionary<string, object?>())));
        Assert.False(FanBoostEncoding.IsAvailable(FeatureMatrix.FromValues(
            new Dictionary<string, object?> { ["FanBoostBtnSupport"] = "false" })));
    }

    [Fact]
    public void ANullMatrixIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => FanBoostEncoding.IsAvailable(null!));
    }
}
