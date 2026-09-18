namespace MechrevoLite.Hardware;

/// <summary>
/// 键盘灯唯一接缝的纯策略：判定 × HID 连接态 × GCU 服务态 × HID 亮度写入是否生效 → 是否走官方（GCU）通道。
///
/// N9-2 修正：旧规则是「确定性不支持 + HID 未连接 + GCU 可用」。40 系真机上的现场 bug 是：
/// HID 控制器**存在且已连接**，但亮度写入不生效——滑条动了灯不变，而厂商自己的通道能调。
/// 旧规则把这种机器永远留在 HID 路径上，于是我们比厂商还差。
///
/// 新规则：GCU 可用 且（HID 不支持/未连接 或 HID 亮度写入未生效）→ 官方通道。
/// HID 写入生效时 HID 仍是主路径（防双发）；没有服务时绝不走官方通道——绝不假装成功。
///
/// 关键约束（5a6cf0a 被回退的原因）：<paramref name="hidBrightnessTookEffect"/> 只能来自
/// **运行时真实效果帧**的结果，绝不能来自构造期（设备尚不存在）的写入——那会把 Unknown 判定
/// 永久路由到 GCU 并跳过 HID 探测。调用方在未观测到失败前必须传 <c>true</c>。
/// </summary>
internal static class KeyboardLightPathPolicy
{
    /// <summary>
    /// 是否走官方（GCU）通道。<paramref name="hidBrightnessTookEffect"/> 为 false 表示 HID 亮度写入
    /// 未生效（设备在但该字段不被接受），此时回退到官方通道而不是静默无操作。
    /// </summary>
    internal static bool ShouldUseGcuKeyboardFallback(
        FeatureAvailability hid, bool hidConnected, bool serviceConnected, bool hidBrightnessTookEffect)
    {
        if (!serviceConnected) return false;
        // 旧契约原样保留：确定性「不支持」且 HID 未连接才走官方通道。
        if (hid == FeatureAvailability.Unsupported && !hidConnected) return true;
        // N9-2 新增：HID 已连接但亮度写入未生效（设备在、字段不被接受）→ 回退官方通道。
        // 仅限 Supported：Unknown 未探测，保持今天的 HID 阶梯，绝不因未观测到的失败改路由。
        return hid == FeatureAvailability.Supported && hidConnected && !hidBrightnessTookEffect;
    }
}
