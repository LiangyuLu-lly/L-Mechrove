using System.Drawing;
using System.Windows.Forms;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// 日间模式下打开的二级窗体必须用日间色板自绘——真机实测（2026-09-13）：
/// 主窗已切日间，弹窗仍整窗夜间底，白字读不出。
/// （原 SecondaryForms_FollowTheActiveTheme 针对 ColorCalibrationForm，
/// 2026-09-14 该表单删除——P3/AdobeRGB 不生效，弹窗改为屏幕行头内联下拉。）
/// </summary>
public class DayModeSecondaryFormTests
{
    /// <summary>
    /// 二级窗体是缓存实例（设置弹窗/灯效窗……夜间先开、日间再开是同一实例）。
    /// 主窗 ApplyThemeMode 只重刷主窗自己——缓存窗的底色/文字永远停在创建时的主题？
    /// 真机实测（2026-09-13）：夜间开弹窗 → 切日间 → 弹窗整窗仍是夜间底。
    /// </summary>
    [Fact]
    public void ApplyThemeMode_ReThemesOwnedSecondaryForms()
    {
        UiVisualStyle.SetAuditNightMode(true);
        SettingsForm? owner = null;
        SettingsDialog? dialog = null;
        try
        {
            owner = new SettingsForm();
            var themePanel = new Panel { Height = 50 };
            var officialPanel = new Panel { Height = 40 };
            themePanel.Visible = false;
            officialPanel.Visible = false;
            dialog = new SettingsDialog(themePanel, officialPanel, null, displayGroupAvailable: false);
            owner.AddOwnedForm(dialog);

            UiVisualStyle.SetAuditNightMode(false);
            owner.ApplyThemeMode(false);
            Application.DoEvents();

            Color dayWindow = Color.FromArgb(0xF2, 0xF5, 0xF9);
            Assert.Equal(dayWindow, dialog.BackColor);
            Assert.Equal(dayWindow, themePanel.BackColor);
        }
        finally
        {
            dialog?.Dispose();
            owner?.Dispose();
            UiVisualStyle.SetAuditNightMode(null);
        }
    }
}
