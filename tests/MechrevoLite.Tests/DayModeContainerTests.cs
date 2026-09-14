using System.Drawing;
using System.Windows.Forms;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// 日间模式容器重刷护栏：夜间构建的主窗切到日间后，仪表盘上任何自绘容器
/// （Panel/TableLayoutPanel/FlowLayoutPanel 及其子类）都不得残留夜间底色——
/// 残留就是真机上「日间模式下一块块深色卡片/黑条」的根因（2026-09-13 真机实测）。
/// </summary>
public class DayModeContainerTests
{
    [Fact]
    public void DayMode_RepaintsEveryDashboardContainerWithTheDaySurface()
    {
        Color nightWindow = Color.FromArgb(0x0B, 0x12, 0x20);
        Color nightSurface = Color.FromArgb(0x11, 0x1A, 0x2B);
        Color nightSurfaceRaised = Color.FromArgb(0x16, 0x22, 0x3A);

        UiVisualStyle.SetAuditNightMode(true);
        SettingsForm? form = null;
        try
        {
            form = new SettingsForm();
            UiVisualStyle.SetAuditNightMode(false);
            form.ApplyThemeMode(false);
            Application.DoEvents();

            var violations = new List<string>();
            Walk(form, violations, nightWindow, nightSurface, nightSurfaceRaised, "");

            Assert.True(violations.Count == 0,
                "日间模式下以下容器仍残留夜间底色（ApplyThemeMode 没有刷到它们）：\n  "
                + string.Join("\n  ", violations));
        }
        finally
        {
            form?.Dispose();
            UiVisualStyle.SetAuditNightMode(null);
        }
    }

    static void Walk(Control root, List<string> violations, Color nightWindow, Color nightSurface, Color nightSurfaceRaised, string path)
    {
        foreach (Control control in root.Controls)
        {
            bool isContainer = control is Panel or TableLayoutPanel or FlowLayoutPanel;
            if (isContainer)
            {
                Color back = control.BackColor;
                string name = string.IsNullOrEmpty(path) ? control.Name : path + "/" + control.Name;
                if (SameRgb(back, nightWindow)) violations.Add($"{name}: BackColor=NightWindow {back}");
                else if (SameRgb(back, nightSurface)) violations.Add($"{name}: BackColor=NightSurface {back}");
                else if (SameRgb(back, nightSurfaceRaised)) violations.Add($"{name}: BackColor=NightSurfaceRaised {back}");
            }
            Walk(control, violations, nightWindow, nightSurface, nightSurfaceRaised,
                (string.IsNullOrEmpty(path) ? "" : path + "/") + (control.Name is { Length: > 0 } ? control.Name : control.GetType().Name));
        }
    }

    static bool SameRgb(Color first, Color second) =>
        first.A == second.A && first.R == second.R && first.G == second.G && first.B == second.B;
}
