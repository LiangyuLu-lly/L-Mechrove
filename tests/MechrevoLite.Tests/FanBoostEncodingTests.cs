using MechrevoLite.Fan;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T21（Wave D）happy 路径：<c>0x751</c> 风扇模式/增压位与 <c>1859-1862</c> cTGP/DB 控制位的
/// 编解码逐条对齐厂商，且门控走 <see cref="FeatureMatrix"/>。
///
/// 失败路径见 <see cref="FanBoostEncodingFailTests"/>。
/// </summary>
public class FanBoostEncodingTests
{
    [Fact]
    public void TheEncodingAddressesAndBitsMatchTheVendor()
    {
        Assert.Equal(1873, FanBoostEncoding.Address);   // 0x751
        Assert.Equal(0x40, FanBoostEncoding.BoostMask); // bit6
        Assert.Equal(4, FanBoostEncoding.ModeBitLow);
        Assert.Equal(7, FanBoostEncoding.ModeBitHigh);

        Assert.Equal(1859, GpuPowerControlEncoding.ControlAddress);
        Assert.Equal(1860, GpuPowerControlEncoding.ConfigurableTgpAddress);
        Assert.Equal(1861, GpuPowerControlEncoding.DynamicBoostAddress);
        Assert.Equal(1862, GpuPowerControlEncoding.MaximumTgpAddress);
    }

    [Theory]
    [InlineData(0, false, 0x00)]
    [InlineData(1, false, 0xA0)]
    [InlineData(2, false, 0x10)]
    [InlineData(0, true, 0x40)]
    [InlineData(1, true, 0xE0)]
    [InlineData(2, true, 0x50)]
    public void EncodeUsesTheVendorBaseBytesPlusBoostBit6(int mode, bool boost, int expected)
    {
        Assert.Equal((byte)expected, FanBoostEncoding.Encode(mode, boost));
    }

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0xA0, 1)]
    [InlineData(0x10, 2)]
    [InlineData(0xE0, 1)]   // 0xA0 + boost
    [InlineData(0x50, 2)]   // 0x10 + boost
    [InlineData(0xC0, 1)]   // bit7+bit6
    [InlineData(0x90, 0)]   // bit7+bit4 -> vendor returns 0
    public void DecodeReadsBit4AndBit7(int value, int expected)
    {
        Assert.Equal(expected, FanBoostEncoding.Decode((byte)value));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void EncodeAndDecodeRoundTripForEveryModeAndBoost(int mode, bool boost)
    {
        byte encoded = FanBoostEncoding.Encode(mode, boost);

        Assert.Equal(mode, FanBoostEncoding.Decode(encoded));
        Assert.Equal(boost, FanBoostEncoding.BoostEnabled(encoded));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(2, 2)]
    public void TheSetterParameterMapsToTheReadBackLogicalMode(int parameter, int logicalMode)
    {
        Assert.Equal(logicalMode, FanBoostEncoding.LogicalModeForSetterParameter(parameter));
    }

    [Fact]
    public void TheBoostBitDoesNotChangeTheDecodedMode()
    {
        foreach (int mode in new[] { 0, 1, 2 })
        {
            byte encoded = FanBoostEncoding.Encode(mode, boostEnabled: false);
            Assert.Equal(mode, FanBoostEncoding.Decode((byte)(encoded | FanBoostEncoding.BoostMask)));
        }
    }

    [Fact]
    public void FanBoostSupportIsGatedByTheFeatureMatrix()
    {
        FeatureMatrix supported = FeatureMatrix.FromValues(new Dictionary<string, object?>
        {
            ["FanBoostBtnSupport"] = 1,
        });
        FeatureMatrix unsupported = FeatureMatrix.FromValues(new Dictionary<string, object?>
        {
            ["FanBoostBtnSupport"] = 0,
        });
        FeatureMatrix missing = FeatureMatrix.FromValues(new Dictionary<string, object?>());

        Assert.True(FanBoostEncoding.IsAvailable(supported));
        Assert.False(FanBoostEncoding.IsAvailable(unsupported));
        Assert.False(FanBoostEncoding.IsAvailable(missing));   // fail-closed
    }

    [Theory]
    [InlineData(true, false, true, 0b101)]
    [InlineData(false, true, false, 0b010)]
    [InlineData(false, false, false, 0b000)]
    [InlineData(true, true, true, 0b111)]
    public void CtgpAndDynamicBoostControlBitsRoundTrip(bool ctgp, bool db, bool funCtrl, int expected)
    {
        byte encoded = GpuPowerControlEncoding.EncodeControl(ctgp, db, funCtrl);

        Assert.Equal((byte)expected, encoded);
        Assert.Equal((ctgp, db, funCtrl), GpuPowerControlEncoding.DecodeControl(encoded));
    }
}
