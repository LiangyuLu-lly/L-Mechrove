namespace MechrevoLite.Hardware;

/// <summary>
/// 系统唤醒（resume）后键盘灯恢复的纯策略。
///
/// <para>真机缺陷（#4 耀世16u）：休眠唤醒后灯效丢失。唤醒路径必须做到两件事——
/// 用户开着灯时**恰好重放一次**效果（旧线程"看起来在跑"不等于还在亮），
/// 用户关着灯时**一次都不重放**（不得把灯点亮）。</para>
/// </summary>
internal static class KeyboardResumePolicy
{
    /// <summary>是否应重放键盘灯效果：只取决于用户意图（<c>KbPowerOn</c>）。</summary>
    internal static bool ShouldRestoreEffect(bool kbPowerOn) => kbPowerOn;

    /// <summary>本次唤醒要发出的效果重放请求数：开=1，关=0。</summary>
    internal static int EffectRestoreRequests(bool kbPowerOn) => kbPowerOn ? 1 : 0;
}
