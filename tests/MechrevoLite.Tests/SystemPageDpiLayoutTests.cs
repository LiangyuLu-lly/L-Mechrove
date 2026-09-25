using System.Drawing;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// 系统页（常用/设备/系统 的第三页）在 125% DPI 下的行内容纳契约。
///
/// 存在理由：UI 审计截图在 1600x900-125% 下显示「beta13 / 开机自启」与
/// 「官方控制台 / 正在检测」两行文字相互压叠（100% 正常）。截图差异无法区分
/// 「布局真的溢出」和「截图工具抖动」，所以这里转成确定性断言：
/// 每个分区面板必须装得下它自己的子控件（面板高度 ≥ 子控件高度）。
/// </summary>
public class SystemPageDpiLayoutTests
{
    /// <summary>按审计的方式构造并放大窗体：UiAuditMode 下建窗体，再 form.Scale(1.25)。</summary>
    static void WithScaledForm(float ratio, Action<SettingsForm> assert)
    {
        bool previousAuditMode = Program.UiAuditMode;
        bool previousReportedCapabilities = Program.UiAuditUseReportedCapabilities;
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.UiAuditUseReportedCapabilities = false;
        Program.hw = null!;
        try
        {
            using var form = new SettingsForm();
            form.ClientSize = SettingsForm.CompactDashboardLogicalClientSize;
            form.CreateControl();
            form.PerformLayout();
            if (Math.Abs(ratio - 1F) > 0.001F)
            {
                form.Scale(new SizeF(ratio, ratio));
                form.PerformLayout();
            }
            assert(form);
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditUseReportedCapabilities = previousReportedCapabilities;
            Program.UiAuditMode = previousAuditMode;
        }
    }

    /// <summary>
    /// 每个分区面板必须装得下它的直接子控件：高度不够时子控件会溢出面板边界，
    /// 在相邻行上画出重叠文字（审计截图里 125% 的「beta13 / 开机自启」即此形态）。
    /// </summary>
    [Theory]
    [InlineData("panelVersion")]
    [InlineData("panelBattery")]
    [InlineData("panelHz")]
    [InlineData("panelBrightness")]
    public void SectionPanels_ContainTheirChildren(string panelName)
    {
        foreach (float ratio in new[] { 1F, 1.25F, 1.5F })
        {
            WithScaledForm(ratio, form =>
            {
                Panel? panel = form.Controls.Find(panelName, true).OfType<Panel>().FirstOrDefault();
                if (panel is null) return;   // 机型不支持时该分区不存在
                foreach (Control child in panel.Controls)
                {
                    if (!child.Visible || child is TableLayoutPanel) continue;   // TLP 自行分配空间
                    Assert.True(
                        child.Bottom <= panel.ClientSize.Height + 1,
                        $"{panelName} 高 {panel.ClientSize.Height} 装不下 {child.Name}（底 {child.Bottom}，ratio={ratio}）。");
                    Assert.True(
                        child.Right <= panel.ClientSize.Width + 1,
                        $"{panelName} 宽 {panel.ClientSize.Width} 装不下 {child.Name}（右 {child.Right}，ratio={ratio}）。");
                }
            });
        }
    }
}
