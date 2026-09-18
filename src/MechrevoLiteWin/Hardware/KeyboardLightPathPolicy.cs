namespace MechrevoLite.Hardware;

/// <summary>
/// 键盘灯唯一接缝的纯策略：判定 × HID 连接态 × GCU 服务态 → 是否走官方（GCU）通道。
/// 两个分支按构造互斥（防双发）；Unknown 绝不路由到 GCU（保持今天的 HID 行为）。
/// </summary>
internal static class KeyboardLightPathPolicy
{
    /// <summary>只有「确定性不支持 + HID 未连接 + GCU 可用」才走官方通道，其余一律 HID。</summary>
    internal static bool ShouldUseGcuKeyboardFallback(
        FeatureAvailability hid, bool hidConnected, bool serviceConnected)
        => serviceConnected && hid == FeatureAvailability.Unsupported && !hidConnected;
}
