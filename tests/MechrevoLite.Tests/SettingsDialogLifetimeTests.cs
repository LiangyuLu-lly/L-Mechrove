using System.Windows.Forms;
using Xunit;

namespace MechrevoLite.Tests;

/// <summary>
/// I5：footer「设置」只能打开一次。根因是非模态 Close() 会释放 SettingsDialog
/// 以及被过继进去的主界面面板，而缓存字段未清空 → 再次打开时用已释放控件构造 → 异常被吞。
/// 契约：用户关闭只隐藏（不释放），因此可以反复打开。
/// </summary>
public class SettingsDialogLifetimeTests
{
    [Fact]
    public void UserCloseHidesSoTheDialogCanReopen()
    {
        // 用户 2026-09-13：弹窗移除局部调光/悬浮窗/自动刷新率开关；2026-09-14 校色按钮也移除（3 参数）。
        var dialog = new SettingsDialog(new Panel(), new Panel(), null);
        try
        {
            dialog.Show();
            Assert.True(dialog.Visible);

            dialog.Close();   // 用户关闭（标题栏 X 或再次点「设置」）
            Assert.False(dialog.IsDisposed,
                "用户关闭必须只隐藏：非模态 Close() 会连同被过继的主界面面板一起释放，导致设置再也打不开。");
            Assert.False(dialog.Visible);

            dialog.Show();    // 第二次打开
            Assert.True(dialog.Visible, "隐藏后必须能再次显示（footer 设置可反复打开）。");

            dialog.Close();
            dialog.Show();    // 第三次打开
            Assert.True(dialog.Visible, "第三次打开同样成立。");
        }
        finally
        {
            dialog.Dispose();
        }
    }

    /// <summary>
    /// 主窗把 界面外观/官方控制台 面板以 Visible=false「延迟托管」进设置弹窗
    /// （Settings.cs 构建尾部：deferred.Visible = false）。弹窗接管后必须重新显示，
    /// 否则弹窗里 界面外观（日间/夜间切换）与 官方控制台 两节永远空白——
    /// 用户既切不了日间模式，也看不到官方控制台状态（2026-09-13 真机实测）。
    /// </summary>
    [Fact]
    public void HostedDeferredPanels_BecomeVisibleInsideTheDialog()
    {
        var themePanel = new Panel { Height = 50 };
        var dayButton = new Button { Name = "buttonDayMode", Text = "日间", Dock = DockStyle.Fill, Height = 30 };
        themePanel.Controls.Add(dayButton);
        var officialPanel = new Panel { Height = 40 };
        // 复刻主窗的延迟托管：面板先 Visible=false，再交给弹窗（Settings.cs 构建尾部）。
        themePanel.Visible = false;
        officialPanel.Visible = false;
        var dialog = new SettingsDialog(themePanel, officialPanel, null);
        try
        {
            dialog.Show();
            Application.DoEvents();

            Assert.True(themePanel.Visible, "界面外观面板（日间/夜间切换）在弹窗内必须可见。");
            Assert.True(dayButton.Visible, "日间按钮在弹窗内必须可见可点。");
            Assert.True(officialPanel.Visible, "官方控制台面板在弹窗内必须可见。");
        }
        finally
        {
            dialog.Dispose();
        }
    }
}
