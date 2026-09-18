using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T6（Wave B）失败 / 边界：EC/NVRAM 派生位缺值必须 fail-closed；常量位不得把服务显式写的 0 抬回 1；
/// 热切换缺任一值即不提供。happy 断言见 <see cref="FeatureMatrixTests"/>。
/// </summary>
public class FeatureMatrixFailTests
{
    static FeatureMatrix Matrix(params (string Key, object? Value)[] pairs)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, object? value) in pairs) values[key] = value;
        return FeatureMatrix.FromValues(values);
    }

    [Theory]
    [InlineData(FeatureBit.FanBoost)]
    [InlineData(FeatureBit.LiquidCooling)]
    [InlineData(FeatureBit.DgpuDirect)]
    [InlineData(FeatureBit.RamFan15)]
    [InlineData(FeatureBit.TurboMode)]
    [InlineData(FeatureBit.ColorCalibration)]
    [InlineData(FeatureBit.NvidiaGpu)]
    [InlineData(FeatureBit.Lightbar)]
    [InlineData(FeatureBit.Numpad)]
    [InlineData(FeatureBit.GpuHotSwapSwitch)]
    public void AnEcOrNvramDerivedBitIsFailClosedWhenTheServiceOmittedIt(FeatureBit bit)
    {
        FeatureMatrix matrix = Matrix(("UnrelatedKey", 1));

        Assert.False(matrix.IsPresent(bit));
        Assert.False(matrix.IsSupported(bit));
    }

    [Fact]
    public void APresentZeroIsNeverRaisedToTheConstantDefault()
    {
        Assert.True(Matrix(("KeyboardSupport", 1)).IsSupported(FeatureBit.Keyboard));
        Assert.False(Matrix(("KeyboardSupport", 0)).IsSupported(FeatureBit.Keyboard));
        Assert.False(Matrix(("SystemMonitorSupport", 0)).IsSupported(FeatureBit.SystemMonitor));
        Assert.False(Matrix(("AcRecoverySwitchSupport", 0)).IsSupported(FeatureBit.AcRecoverySwitch));
    }

    [Fact]
    public void AnEmptyProfileDoesNotInventEcCapabilities()
    {
        FeatureMatrix matrix = Matrix();

        Assert.False(matrix.IsSupported(FeatureBit.DgpuDirect));
        Assert.False(matrix.IsSupported(FeatureBit.FanBoost));
        Assert.False(matrix.GpuHotSwapAvailable);
    }

    [Fact]
    public void HotSwapIsNotOfferedWhenEitherVendorValueIsMissing()
    {
        Assert.False(Matrix(("GpuHotSwapSwitchSupport", 1)).GpuHotSwapAvailable);
        Assert.False(Matrix(("lgpuHotSwapSwitchStatus", 1)).GpuHotSwapAvailable);
        Assert.False(Matrix().GpuHotSwapAvailable);
    }

    [Fact]
    public void ACommercialModelNeverGetsTheNonCommercialFanDefault()
    {
        Assert.False(Matrix(("IsProjectIdCommercial", 1)).IsSupported(FeatureBit.FanSettings));
        Assert.False(Matrix(("IsProjectIdCommercial", 1)).IsSupported(FeatureBit.OverclockSettings));
    }

    [Fact]
    public void TheDefinitionTableRejectsAMissingBit()
    {
        Assert.All(FeatureMatrix.Definitions, definition =>
            Assert.True(Enum.IsDefined(definition.Bit), $"unknown bit {definition.Bit} in the table"));
    }
}
