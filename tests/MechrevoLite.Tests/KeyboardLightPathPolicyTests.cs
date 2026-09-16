using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 键盘灯唯一接缝的纯策略（防双发）：判定 × hidConnected × serviceConnected 全矩阵。
/// 契约：恰好一条路径——只有「Unsupported 且 HID 未连接且 GCU 可用」才走官方通道；
/// Unknown 绝不路由到 GCU（保持今天的 HID 行为）。
/// </summary>
public class KeyboardLightPathPolicyTests
{
    [Theory]
    // Unknown：无论 HID/服务态如何，一律不走 GCU。
    [InlineData(FeatureAvailability.Unknown, false, true, false)]
    [InlineData(FeatureAvailability.Unknown, true, true, false)]
    [InlineData(FeatureAvailability.Unknown, false, false, false)]
    [InlineData(FeatureAvailability.Unknown, true, false, false)]
    // Unsupported：仅 HID 未连接且服务可用时走 GCU；HID 已连接或服务不可用仍走 HID。
    [InlineData(FeatureAvailability.Unsupported, false, true, true)]
    [InlineData(FeatureAvailability.Unsupported, true, true, false)]
    [InlineData(FeatureAvailability.Unsupported, false, false, false)]
    [InlineData(FeatureAvailability.Unsupported, true, false, false)]
    // Supported：一律走 HID。
    [InlineData(FeatureAvailability.Supported, false, true, false)]
    [InlineData(FeatureAvailability.Supported, true, true, false)]
    [InlineData(FeatureAvailability.Supported, false, false, false)]
    [InlineData(FeatureAvailability.Supported, true, false, false)]
    // FeatureAvailability 是 internal：public 测试方法不能用它作参数类型，故 InlineData 走 object 再转换。
    public void ShouldUseGcuKeyboardFallback_PinsTheFullMatrix(
        object hid, bool hidConnected, bool serviceConnected, bool expected)
    {
        Assert.Equal(expected, KeyboardLightPathPolicy.ShouldUseGcuKeyboardFallback(
            (FeatureAvailability)hid, hidConnected, serviceConnected));
    }
}
