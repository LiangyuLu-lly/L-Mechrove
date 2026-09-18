using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T6（Wave B）happy 路径：<see cref="FeatureMatrix"/> 只读服务写入的 <c>ItemSupport</c>/<c>GpuConfig</c>，
/// 存在即采用、缺失才按类别处置（EC/NVRAM 位 fail-closed、厂商常量位镜像默认），绝不重推厂商判据。
/// 失败/越界断言见 <see cref="FeatureMatrixFailTests"/>。
/// </summary>
public class FeatureMatrixTests
{
    static FeatureMatrix Matrix(params (string Key, object? Value)[] pairs)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, object? value) in pairs) values[key] = value;
        return FeatureMatrix.FromValues(values);
    }

    [Fact]
    public void EveryCapabilityBitHasExactlyOneComparisonEntry()
    {
        foreach (FeatureBit bit in Enum.GetValues<FeatureBit>())
        {
            FeatureBitDefinition definition = Assert.Single(FeatureMatrix.Definitions, item => item.Bit == bit);
            Assert.False(string.IsNullOrWhiteSpace(definition.Key), $"{bit} has no service key");
        }

        Assert.Equal(
            FeatureMatrix.Definitions.Count,
            FeatureMatrix.Definitions.Select(item => item.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void AValueWrittenByTheServiceIsUsedAsIs_NoRecompute()
    {
        // 常量位在厂商侧恒为 1，但服务显式写了 0 —— 必须照用，不得"重算"回 1。
        Assert.False(Matrix(("KeyboardSupport", 0)).IsSupported(FeatureBit.Keyboard));

        // EC 派生位写了 1 也必须照用（不得因"没看到别的位"而清零）。
        Assert.True(Matrix(("FanBoostBtnSupport", 1)).IsSupported(FeatureBit.FanBoost));
    }

    [Fact]
    public void ConstantBitsStillReportSupportedWhenTheServiceOmittedThem()
    {
        FeatureMatrix matrix = Matrix();

        Assert.True(matrix.IsSupported(FeatureBit.Keyboard));
        Assert.True(matrix.IsSupported(FeatureBit.SystemMonitor));
        Assert.True(matrix.IsSupported(FeatureBit.AcRecoverySwitch));
        Assert.False(matrix.ProfileAvailable);
    }

    [Fact]
    public void FanSettingsMirrorsTheVendorNonCommercialDefault()
    {
        Assert.True(Matrix().IsSupported(FeatureBit.FanSettings));
        Assert.True(Matrix(("IsProjectIdCommercial", 0)).IsSupported(FeatureBit.FanSettings));
        Assert.False(Matrix(("IsProjectIdCommercial", 1)).IsSupported(FeatureBit.OverclockSettings));
        Assert.False(Matrix(("IsProjectIdCommercial", 1)).IsSupported(FeatureBit.FanSettings));
    }

    [Fact]
    public void GpuHotSwapRequiresBothServiceValues()
    {
        Assert.True(Matrix(
            ("GpuHotSwapSwitchSupport", 1),
            ("lgpuHotSwapSwitchStatus", 1)).GpuHotSwapAvailable);

        Assert.False(Matrix(("GpuHotSwapSwitchSupport", 1)).GpuHotSwapAvailable);
        Assert.False(Matrix(("lgpuHotSwapSwitchStatus", 1)).GpuHotSwapAvailable);
        Assert.False(Matrix(
            ("GpuHotSwapSwitchSupport", 1),
            ("lgpuHotSwapSwitchStatus", 0)).GpuHotSwapAvailable);
    }

    [Fact]
    public void ProfileAvailableTracksWhetherTheServiceWroteAnything()
    {
        Assert.True(Matrix(("TurboModeSupport", 1)).ProfileAvailable);
    }

    [Fact]
    public void CommercialClassificationIsReadFromTheServiceValue()
    {
        Assert.False(Matrix().IsCommercial);
        Assert.True(Matrix(("IsProjectIdCommercial", 1)).IsCommercial);
    }
}
