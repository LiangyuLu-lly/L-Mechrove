using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 键盘灯唯一接缝的纯策略（防双发）：判定 × hidConnected × serviceConnected × hidBrightnessTookEffect 全矩阵。
/// 契约：恰好一条路径——「Unsupported 或 HID 未连接」且 GCU 可用时走官方通道；
/// 另外 N9-2 新增：HID 已连接但亮度写入未生效时也走官方通道（否则滑条动了灯不变）。
/// Unknown 在未观测到写入失败时绝不路由到 GCU（保持今天的 HID 行为）。
/// </summary>
public class KeyboardLightPathPolicyTests
{
    [Theory]
    // Unknown：未观测到写入失败（true）时，无论 HID/服务态如何，一律不走 GCU。
    [InlineData(FeatureAvailability.Unknown, false, true, true, false)]
    [InlineData(FeatureAvailability.Unknown, true, true, true, false)]
    [InlineData(FeatureAvailability.Unknown, false, false, true, false)]
    [InlineData(FeatureAvailability.Unknown, true, false, true, false)]
    // Unsupported：仅 HID 未连接且服务可用时走 GCU；HID 已连接或服务不可用仍走 HID。
    [InlineData(FeatureAvailability.Unsupported, false, true, true, true)]
    [InlineData(FeatureAvailability.Unsupported, true, true, true, false)]
    [InlineData(FeatureAvailability.Unsupported, false, false, true, false)]
    [InlineData(FeatureAvailability.Unsupported, true, false, true, false)]
    // Supported：写入生效时一律走 HID。
    [InlineData(FeatureAvailability.Supported, false, true, true, false)]
    [InlineData(FeatureAvailability.Supported, true, true, true, false)]
    [InlineData(FeatureAvailability.Supported, false, false, true, false)]
    [InlineData(FeatureAvailability.Supported, true, false, true, false)]
    // N9-2：Supported 且 HID 已连接，但亮度写入未生效 → 回退官方通道（现场 bug 的修复面）。
    [InlineData(FeatureAvailability.Supported, true, true, false, true)]
    // 写入未生效但没有服务 → 绝不走官方通道（不假装成功）。
    [InlineData(FeatureAvailability.Supported, true, false, false, false)]
    // FeatureAvailability 是 internal：public 测试方法不能用它作参数类型，故 InlineData 走 object 再转换。
    public void ShouldUseGcuKeyboardFallback_PinsTheFullMatrix(
        object hid, bool hidConnected, bool serviceConnected, bool hidBrightnessTookEffect, bool expected)
    {
        Assert.Equal(expected, KeyboardLightPathPolicy.ShouldUseGcuKeyboardFallback(
            (FeatureAvailability)hid, hidConnected, serviceConnected, hidBrightnessTookEffect));
    }
}
