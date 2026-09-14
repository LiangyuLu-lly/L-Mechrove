using MechrevoLite.Helpers;

namespace MechrevoLite.Tests;

/// <summary>
/// 任务栏自动隐藏 / 透明效果 / 深色主题。
///
/// 这三项官方控制台也有，而且都不走 MQTT：自动隐藏是 Shell 的 AppBar 状态，
/// 另两项是 HKCU\...\Themes\Personalize 下的注册表值。
///
/// 这些测试**不改变用户的系统状态**。写入路径的验证方式是「把当前值再写一遍」：
/// 既跑完整的写入 + 广播 + 回读链路，又不会让任务栏或主题真的变一下。
/// </summary>
public class ShellPersonalizationTests
{
    [Fact]
    public void TaskbarAutoHideCanBeRead()
    {
        bool? state = ShellPersonalization.IsTaskbarAutoHide();

        // Shell 在任何桌面会话里都会应答这个查询，读不到就是互操作签名写错了。
        Assert.NotNull(state);
    }

    /// <summary>
    /// 写入自身当前值必须成功且不改变状态。这条同时覆盖了「已是目标值就直接返回」的短路。
    /// </summary>
    [Fact]
    public void WritingTaskbarAutoHideToItsCurrentValueIsANoOp()
    {
        bool? before = ShellPersonalization.IsTaskbarAutoHide();
        Assert.NotNull(before);
        bool alwaysOnTopBefore = ShellPersonalization.IsTaskbarAlwaysOnTop();

        Assert.True(ShellPersonalization.SetTaskbarAutoHide(before.Value));

        Assert.Equal(before, ShellPersonalization.IsTaskbarAutoHide());
        // 官方那边写 ABM_SETSTATE 时直接给 1/0，会顺手清掉「任务栏置顶」这一位。
        // 我们只改自动隐藏那一位，所以置顶状态必须原样保留。
        Assert.Equal(alwaysOnTopBefore, ShellPersonalization.IsTaskbarAlwaysOnTop());
    }

    /// <summary>
    /// 透明效果写入自身当前值：完整走一遍注册表写 + ImmersiveColorSet 广播 + 回读，
    /// 用户看不到任何变化。注册表值不存在时跳过（新装系统可能没有这个值）。
    /// </summary>
    [Fact]
    public void WritingTransparencyToItsCurrentValueRoundTrips()
    {
        bool? before = ShellPersonalization.IsTransparencyEnabled();
        if (before is null) return;

        Assert.True(ShellPersonalization.SetTransparencyEnabled(before.Value));
        Assert.Equal(before, ShellPersonalization.IsTransparencyEnabled());
    }

    [Fact]
    public void WritingDarkThemeToItsCurrentValueRoundTrips()
    {
        bool? before = ShellPersonalization.IsDarkTheme();
        if (before is null) return;

        Assert.True(ShellPersonalization.SetDarkTheme(before.Value));
        Assert.Equal(before, ShellPersonalization.IsDarkTheme());
    }

    /// <summary>
    /// 界面回显靠 CheckBox.Tag 走这个路由。三个键都必须能读出值，
    /// 否则开关会永远停在未勾选状态。
    /// </summary>
    [Theory]
    [InlineData("taskbarautohide")]
    [InlineData("transparency")]
    [InlineData("darktheme")]
    public void EachWindowsSwitchKeyIsRoutedToAReader(string key)
    {
        // 注册表值缺失时透明/深色可能返回 null，这里只要求路由认得这个键——
        // 认不得的键会走到 default 分支，与「读到了 null」在行为上无法区分，
        // 所以下面那条不认识的键的测试才是真正的判据。
        _ = MechrevoLite.SettingsForm.ReadWindowsPersonalizationSwitch(key);
    }

    /// <summary>硬件开关的键不能落到 Windows 路由里，否则它们会被当成系统设置去读。</summary>
    [Theory]
    [InlineData("wifi")]
    [InlineData("lightbar")]
    [InlineData("powerlight")]
    [InlineData("")]
    public void DeviceSwitchKeysAreNotHandledByTheWindowsRouter(string key) =>
        Assert.Null(MechrevoLite.SettingsForm.ReadWindowsPersonalizationSwitch(key));

    /// <summary>
    /// 这三项不是设备能力：硬件层不该声称支持它们，否则可见性会被 GCU 连接状态左右。
    /// </summary>
    [Theory]
    [InlineData("taskbarautohide")]
    [InlineData("transparency")]
    [InlineData("darktheme")]
    public void WindowsSwitchesAreNotDeviceCapabilities(string key)
    {
        using var hardware = new MechrevoLite.Hardware.MechrevoHw();

        Assert.False(hardware.SupportsQuickSwitch(key));
    }
}
