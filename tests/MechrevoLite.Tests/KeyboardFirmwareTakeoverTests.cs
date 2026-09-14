using System.Reflection;

namespace MechrevoLite.Tests;

/// <summary>
/// Fn 热键（固件）接管键盘的回归。
///
/// 用户反馈：按 Fn 调键盘背光亮度时，灯会先闪一下厂商固件效果，再回到自定义效果。
/// 机制：Fn 由 EC/固件处理，固件改亮度的同时重新进入官方效果模式，把控制器踢出 ITE 自定义帧模式；
/// 我们的 HID 帧随即被静默忽略（WriteFile 仍返回成功，帧循环不会察觉）。
/// 应用已能从 GCU 的 Keyboard/Status（brightNess/effect 相对基线变化 + KeyboardStatusVersion 前进）
/// 检测到这次接管，但此前要等 180ms 去抖 + 固件通道往返才重申自定义帧模式——这段窗口就是可见的厂商效果。
///
/// 锁定契约：
/// 1) 一次真实变化只触发一次立即重申（同值回显与过期版本不触发，无重复风暴）；
/// 2) 重申按代际守卫，迟到的旧检测不得写 HID；
/// 3) 固件新亮度被保留（重申不把亮度写回旧值）。
/// </summary>
public class KeyboardFirmwareTakeoverTests
{
    static readonly MethodInfo ShouldReassert = typeof(Program).GetMethod(
        "ShouldReassertKeyboardCustomMode", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("seam missing: ShouldReassertKeyboardCustomMode");
    static readonly MethodInfo SyncBrightness = typeof(Program).GetMethod(
        "SyncKeyboardBrightnessForFirmwareChange", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("seam missing: SyncKeyboardBrightnessForFirmwareChange");
    static readonly MethodInfo ReassertGuarded = typeof(Program).GetMethod(
        "ReassertKeyboardCustomModeForCurrentGeneration", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("seam missing: ReassertKeyboardCustomModeForCurrentGeneration");

    static bool Should(long version, long baselineVersion, int bright, int baselineBright, string effect, string baselineEffect) =>
        (bool)ShouldReassert.Invoke(null, new object?[] { version, baselineVersion, bright, baselineBright, effect, baselineEffect })!;

    static bool Sync(int reported, Func<int> current, Action<int> set) =>
        (bool)SyncBrightness.Invoke(null, new object?[] { reported, current, set })!;

    static bool Guard(int generation, Func<int> current, Action reassert) =>
        (bool)ReassertGuarded.Invoke(null, new object?[] { generation, current, reassert })!;

    /// <summary>
    /// 一次真实的固件亮度变化（含随后 GCU 的同值周期回显）只允许一次重申。
    /// FeedFrame 复刻 OnHardwareStateChanged 的判定顺序：判定变化 → 代际守卫重申 → 推进基线。
    /// </summary>
    [Fact]
    public void FirmwareTakeover_OneChangeReassertsExactlyOnce()
    {
        long baselineVersion = 9;
        int baselineBrightness = 75;
        string baselineEffect = "5";
        int reasserts = 0;

        void FeedFrame(long version, int brightness, string effect)
        {
            if (!Should(version, baselineVersion, brightness, baselineBrightness, effect, baselineEffect)) return;
            if (!Guard(1, () => 1, () => reasserts++)) return;
            baselineVersion = version;
            baselineBrightness = brightness;
            baselineEffect = effect;
        }

        FeedFrame(10, 50, "5");   // 固件把亮度 75→50：真实变化 → 恰好一次重申
        FeedFrame(11, 50, "5");   // GCU 同值回显：不是变化
        FeedFrame(12, 50, "5");   // 同上

        Assert.Equal(1, reasserts);
    }

    /// <summary>效果变化（亮度不变）同样算固件接管，必须重申。</summary>
    [Fact]
    public void FirmwareTakeover_EffectOnlyChangeAlsoReasserts()
    {
        Assert.True(Should(10, 9, 50, 50, "Rainbow", "5"));
        Assert.False(Should(10, 9, 50, 50, "5", "5"));       // 亮度、效果都没变
        Assert.False(Should(9, 9, 50, 75, "Rainbow", "5"));  // 版本未前进（过期帧）
    }

    /// <summary>迟到的旧代际检测在代际推进后不得再写 HID；仍处于当前代际才执行。</summary>
    [Fact]
    public void FirmwareTakeover_StaleGenerationDoesNotReassert()
    {
        int reasserts = 0;
        int current = 2;

        Assert.False(Guard(1, () => current, () => reasserts++));   // 代际 1 已过期
        Assert.Equal(0, reasserts);

        Assert.True(Guard(2, () => current, () => reasserts++));    // 当前代际正常重申
        Assert.Equal(1, reasserts);
    }

    /// <summary>固件新亮度必须被保留：重申时同步的是固件值，不回写旧值。</summary>
    [Fact]
    public void FirmwareTakeover_KeepsTheFirmwareBrightness()
    {
        int applied = -1;

        // 固件 75 → 50：必须保留 50，不能写回旧值 75。
        Assert.True(Sync(50, () => 75, value => applied = value));
        Assert.Equal(50, applied);

        // 帧未带有效亮度（-1）：保持现有 HID 亮度，不下发。
        applied = -1;
        Assert.False(Sync(-1, () => 75, value => applied = value));
        Assert.Equal(-1, applied);

        // 已经是同一值：无变化。
        Assert.False(Sync(75, () => 75, value => applied = value));
    }
}
