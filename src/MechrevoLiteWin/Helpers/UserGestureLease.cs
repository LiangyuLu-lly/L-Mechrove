namespace MechrevoLite.Helpers;

/// <summary>
/// 一次性「真实用户手势」凭证。只能在 <see cref="NativeMethods.HasFreshUserInput"/>（≤500ms 内
/// 确有真实输入）时捕获，供随后可能被硬件写入 / await / 去抖计时器拖延的动作消费一次；
/// 程序化 / 自动化路径拿不到新鲜输入 → 拿不到凭证 → 动作被拒。
///
/// 与 <see cref="SystemRestart"/> 的确认凭证同源（同一份判定窗口与有效期）。两个消费者
/// 各持一个实例，互不放行：重启凭证不会授权充电上限写入，反之亦然。
/// </summary>
internal sealed class UserGestureLease
{
    /// <summary>凭证有效期：覆盖捕获之后到动作发起之间的硬件写入 / await / 去抖计时器（远大于 500ms 输入窗口）。</summary>
    internal const int ValidityMs = 15000;

    long _expiresAt;

    /// <summary>只有新鲜真实输入才授予一次短时凭证；成功返回 true。</summary>
    internal bool Capture()
    {
        if (!NativeMethods.HasFreshUserInput()) return false;
        Volatile.Write(ref _expiresAt, Environment.TickCount64 + ValidityMs);
        return true;
    }

    /// <summary>当前是否有未过期、未消费的凭证。</summary>
    internal bool IsActive
    {
        get
        {
            long expiresAt = Volatile.Read(ref _expiresAt);
            return expiresAt != 0 && Environment.TickCount64 <= expiresAt;
        }
    }

    /// <summary>消费凭证（一次性）：放行动作后立即调用，消费后不能再放行第二次。</summary>
    internal void Consume() => Volatile.Write(ref _expiresAt, 0);

    /// <summary>测试接缝：清空未被消费的凭证，避免凭证跨测试泄漏。</summary>
    internal void Reset() => Volatile.Write(ref _expiresAt, 0);
}
