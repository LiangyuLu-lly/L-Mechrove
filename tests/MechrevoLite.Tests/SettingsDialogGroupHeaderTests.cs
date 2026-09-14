using MechrevoLite.UI;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// 设置弹窗的分组标题契约：标题只在「该组至少有一行可见」时渲染。
/// 机型不支持响应加速（LCDOverdriveSwitch 未上报）时「显示」组没有任何行，
/// 标题必须一并隐藏——否则截图里就是「显示」下面空无一物。
///
/// 规则：AddHeader 只在组内有可见行时保留；隐藏时标题与行同属 AutoSize 行，
/// 高度一起收为 0，不留下间距。全仓库分组审计：
/// - 本弹窗 3 组：界面(themePanel，恒有)、显示(overdriveChk，可选)、系统(officialPanel，恒有)。
/// - 仪表盘「更多开关」3 组（输入设备/键盘与热键/电源与系统）：已由
///   SettingsForm.SyncQuickSwitchVisibility 按 QuickSwitchGroups 抑制空标题（本次未改）。
/// </summary>
public class SettingsDialogGroupHeaderTests
{
    static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child))
                yield return descendant;
        }
    }

    static Label Header(SettingsDialog dialog, string text) =>
        Descendants(dialog).OfType<Label>().Single(label => label.Text == text);

    [Fact]
    public void DisplayHeader_IsHiddenWhenTheOnlyRowIsUnavailable()
    {
        var overdrive = new RCheckBox { Text = "响应加速" };
        using var dialog = new SettingsDialog(new Panel(), new Panel(), overdrive, displayGroupAvailable: false);
        dialog.Show();
        Application.DoEvents();

        Assert.False(Header(dialog, "显示").Visible, "没有可见行的「显示」组不得保留标题。");
        Assert.False(overdrive.Visible, "不可用的响应加速行必须隐藏。");
        Assert.True(Header(dialog, "界面").Visible);
        Assert.True(Header(dialog, "系统").Visible);
    }

    [Fact]
    public void DisplayHeader_RendersWhenTheRowIsAvailable()
    {
        var overdrive = new RCheckBox { Text = "响应加速" };
        using var dialog = new SettingsDialog(new Panel(), new Panel(), overdrive, displayGroupAvailable: true);
        dialog.Show();
        Application.DoEvents();

        Assert.True(Header(dialog, "显示").Visible);
        Assert.True(overdrive.Visible);
    }

    [Fact]
    public void EmptyDisplayGroup_LeavesNoVerticalGap()
    {
        var overdrive = new RCheckBox { Text = "响应加速" };
        using var dialog = new SettingsDialog(new Panel(), new Panel(), overdrive, displayGroupAvailable: true);
        dialog.Show();
        Application.DoEvents();
        int systemTopWithGroup = Header(dialog, "系统").Top;

        dialog.SetDisplayGroupAvailable(false);
        dialog.PerformLayout();
        Application.DoEvents();

        Assert.False(Header(dialog, "显示").Visible);
        Assert.True(Header(dialog, "系统").Top < systemTopWithGroup,
            "收掉空组后「系统」标题必须上移，说明空组没有留下占位间距。");
    }

    [Fact]
    public void MissingOverdriveControl_StillRendersTheOtherGroups()
    {
        // 接线兼容：即使响应加速控件整体缺失（null），界面/系统两组仍照常渲染，且不建「显示」标题。
        using var dialog = new SettingsDialog(new Panel(), new Panel(), null, displayGroupAvailable: false);
        dialog.Show();
        Application.DoEvents();

        Assert.True(Header(dialog, "界面").Visible);
        Assert.True(Header(dialog, "系统").Visible);
        Assert.DoesNotContain(Descendants(dialog).OfType<Label>(), label => label.Text == "显示");
    }
}
