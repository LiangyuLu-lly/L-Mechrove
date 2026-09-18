using System.Windows.Forms;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T10（Wave B）happy 路径：D1 运行时表现由 <c>LMECHREVO_MODEL_OVERRIDE</c> 注入驱动——
/// 集合外代号 → 顶部横幅 + 只读降级；集合内代号 → 无横幅、写入入口可用。
/// 失败/边界断言见 <see cref="UnsupportedModelUiTests"/>。
/// </summary>
public class UnsupportedModelUiHappyTests
{
    internal static IDisposable Model(string? code)
    {
        string? previous = Environment.GetEnvironmentVariable(ModelOverrideStateMachine.OverrideVariable);
        Environment.SetEnvironmentVariable(ModelOverrideStateMachine.OverrideVariable, code);
        return new Restore(previous);
    }

    sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable(ModelOverrideStateMachine.OverrideVariable, previous);
    }

    /// <summary>Show 一次让 Visible 语义生效（祖先不可见时子控件 Visible 恒为 false），
    /// 并泵一次消息队列——RefreshDeviceCapabilities 在句柄线程之外会 BeginInvoke 延迟执行。</summary>
    internal static SettingsForm ShowForm()
    {
        var form = new SettingsForm { ClientSize = SettingsForm.CompactDashboardLogicalClientSize };
        form.CreateControl();
        form.Show();
        Application.DoEvents();
        form.RefreshDeviceCapabilities();
        Application.DoEvents();
        return form;
    }

    [Fact]
    public void AnOutOfSetInjectedModelShowsTheNoticeAndDisablesEveryWriteEntry()
    {
        using var model = Model("PH6AGxx");
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null!;
        try
        {
            Assert.Equal(SupportReason.NotInSet, RuntimeModelSupport.Current().Reason);
            using var form = ShowForm();

            Assert.True(form.IsReadOnlyDegraded, "集合外机型必须进入只读降级。");
            Control banner = form.Controls.Find("panelUnsupportedModelNotice", true).Single();
            Assert.True(banner.Visible, "不支持机型必须出现顶部横幅。");
            Label notice = form.Controls.Find("labelUnsupportedModelNotice", true).OfType<Label>().Single();
            Assert.Contains("PH6AGxx", notice.Text);

            foreach (string name in SettingsFormWriteEntries.Names)
            {
                Control? control = form.Controls.Find(name, true).FirstOrDefault();
                Assert.True(control is not null, $"{name} not found");
                Assert.False(control!.Enabled, $"{name} must be disabled while read-only");
            }
            // 手动机型覆盖入口必须在降级下仍可达（否则用户无路可走）。
            Control overrideBox = form.Controls.Find("textModelOverride", true).Single();
            Assert.True(overrideBox.Enabled, "只读降级下手动机型覆盖入口必须可用。");
            Assert.True(form.Controls.Find("buttonModelOverrideApply", true).Single().Enabled);
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
        }
    }

    [Fact]
    public void AnInSetInjectedModelHasNoNoticeAndKeepsWriteEntriesEnabled()
    {
        using var model = Model("PH4TRX1");
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null!;
        try
        {
            using var form = ShowForm();

            Assert.False(form.IsReadOnlyDegraded, "集合内机型不得降级。");
            Assert.False(form.Controls.Find("panelUnsupportedModelNotice", true).Single().Visible,
                "集合内机型不得出现横幅。");

            Control slider = form.Controls.Find("sliderBattery", true).Single();
            Assert.True(slider.Enabled, "集合内机型电池写入入口必须可用。");
            Assert.True(form.Controls.Find("buttonEco", true).Single().Enabled);
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
        }
    }

    [Fact]
    public void TheDecisionIsRecordedSoTheBannerCanNameTheModel()
    {
        using var model = Model("PH4TRX1");
        Assert.True(RuntimeModelSupport.Current().IsSupported);
        using var outOfSet = Model("PH6AGxx");
        Assert.Equal(SupportReason.NotInSet, RuntimeModelSupport.Current().Reason);
    }
}

internal static class SettingsFormWriteEntries
{
    // 只列真实存在于树上的写入入口（部分 designer 字段未注册 Name 或未挂树，生产禁用列表里保留但跳过）。
    internal static readonly string[] Names =
    {
        "sliderBattery", "buttonEco", "buttonStandard", "buttonUltimate",
    };
}
