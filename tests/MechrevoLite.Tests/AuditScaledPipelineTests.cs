using System.Drawing;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// UI 审计的缩放管线契约：审计用 <c>form.Scale(ratio)</c> + <c>ScaleFonts(ratio)</c> +
/// <c>AuditLayoutScale</c> 模拟高 DPI 视口，代码里任何「按 DPI 重算尺寸」的地方都必须认
/// <c>RForm.EffectiveLayoutScale</c>，否则窗口会缩回 100% 而字体停在放大后的尺寸，
/// 截图里就会出现「文字溢出压到相邻行」的假缺陷（125% 系统页曾如此）。
/// </summary>
public class AuditScaledPipelineTests
{
    /// <summary>完整复刻 UiAuditRunner 的缩放序列（含 ApplyResponsiveBounds 重排）。</summary>
    static void WithAuditScaledForm(float ratio, Action<SettingsForm> assert)
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

            if (Math.Abs(ratio - 1F) > 0.01F)
            {
                form.Scale(new SizeF(ratio, ratio));
                ScaleFonts(form, ratio);
            }
            form.AuditLayoutScale = ratio;
            form.ApplyResponsiveBounds(new Rectangle(0, 0, 1600, 860));
            form.PerformLayout();
            assert(form);
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditUseReportedCapabilities = previousReportedCapabilities;
            Program.UiAuditMode = previousAuditMode;
        }
    }

    /// <summary>与 UiAuditRunner.ScaleFonts 逐字一致（先快照后赋值，避免枚举中途被布局打断）。</summary>
    static void ScaleFonts(Control root, float ratio)
    {
        var fonts = Flatten(root).Select(control => (Control: control, Font: control.Font)).ToArray();
        foreach (var item in fonts)
        {
            Font old = item.Font;
            float size = Math.Max(1f, old.SizeInPoints * ratio);
            item.Control.Font = new Font(old.FontFamily, size, old.Style, GraphicsUnit.Point, old.GdiCharSet, old.GdiVerticalFont);
        }
    }

    static IEnumerable<Control> Flatten(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
            foreach (Control descendant in Flatten(child))
                yield return descendant;
    }

    [Theory]
    [InlineData(1.25F)]
    [InlineData(1.5F)]
    public void ScaledViewport_KeepsWindowAtAuditScale(float ratio) =>
        WithAuditScaledForm(ratio, form =>
        {
            int expectedWidth = (int)Math.Round(SettingsForm.CompactDashboardLogicalClientSize.Width * ratio);
            int minHeight = (int)Math.Round(300 * ratio);

            // 宽度必须跟着审计倍率走，而不是被按宿主 DPI 缩回 100%（那是曾经的假缺陷根因）。
            Assert.True(
                Math.Abs(form.ClientSize.Width - expectedWidth) <= 2,
                $"窗口宽 {form.ClientSize.Width} 与审计倍率 {ratio} 下的期望 {expectedWidth} 不符——" +
                "ApplyResponsiveBounds 把窗口缩回了宿主 DPI，截图会呈现文字压行的假缺陷。");
            // 高度随内容自适应（DESIGN.md v2 §5）并封顶工作区；这里验证它始终在
            // [最小高, 工作区上限] 区间内且跟随倍率，不锁具体公式。
            Assert.True(form.ClientSize.Height >= minHeight,
                $"窗口高 {form.ClientSize.Height} 低于最小高 {minHeight}（ratio={ratio}）。");
            Assert.True(form.ClientSize.Height <= 860,
                $"窗口高 {form.ClientSize.Height} 超过审计视口工作区（ratio={ratio}）。");
            // 高度必须同步使用 CompactDashboardLogicalClientSize 常量，而不是被 660 的硬编码上限锁死。
            int expectedHeight = (int)Math.Round(SettingsForm.CompactDashboardLogicalClientSize.Height * ratio);
            Assert.Equal(expectedHeight, form.ClientSize.Height);
        });

    [Theory]
    [InlineData(1F)]
    [InlineData(1.25F)]
    [InlineData(1.5F)]
    public void ScaledViewport_SystemRowChildrenFitTheirPanels(float ratio) =>
        WithAuditScaledForm(ratio, form =>
        {
            foreach (string panelName in new[] { "panelVersion" })
            {
                Panel? panel = form.Controls.Find(panelName, true).OfType<Panel>().FirstOrDefault();
                if (panel is null) continue;
                foreach (Control child in panel.Controls)
                {
                    if (!child.Visible || child is TableLayoutPanel) continue;
                    Assert.True(
                        child.Bottom <= panel.ClientSize.Height + 1,
                        $"{panelName} 高 {panel.ClientSize.Height} 装不下 {child.Name}（底 {child.Bottom}，ratio={ratio}）。");
                    Assert.True(
                        child.Right <= panel.ClientSize.Width + 1,
                        $"{panelName} 宽 {panel.ClientSize.Width} 装不下 {child.Name}（右 {child.Right}，ratio={ratio}）。");
                }
            }
        });

    /// <summary>
    /// 审计缩放下字体必须只被放大一次：重复放大（枚举中途树被布局改动导致同一控件被访问多次）
    /// 会让单个控件的字变成天文数字，截图里表现为某一行文字异常巨大。
    /// </summary>
    [Fact]
    public void ScaledViewport_ScalesEachFontExactlyOnce()
    {
        bool previousAuditMode = Program.UiAuditMode;
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null!;
        try
        {
            using var form = new SettingsForm();
            form.CreateControl();
            form.PerformLayout();
            const float ratio = 1.5F;
            var before = Flatten(form).Where(c => c.Visible).ToDictionary(c => c, c => c.Font.SizeInPoints);
            ScaleFonts(form, ratio);
            foreach (var (control, size) in before)
            {
                float expected = Math.Max(1F, size * ratio);
                Assert.True(
                    Math.Abs(control.Font.SizeInPoints - expected) < 0.05F,
                    $"{control.Name} 字体 {control.Font.SizeInPoints:F1}pt ≠ 期望 {expected:F1}pt——被重复放大。");
            }
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAuditMode;
        }
    }
}
