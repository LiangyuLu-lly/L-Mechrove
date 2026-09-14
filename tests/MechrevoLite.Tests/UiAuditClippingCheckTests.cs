using MechrevoLite.UI;
using System.Drawing;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// 钉死 UI 审计的裁切检查：设置弹窗曾把「控制台」按钮裁在右缘（根 TableLayoutPanel 的
/// 绝对列之和超过内容区），旧审计因豁免与漏采窗体而报 0。契约：
/// <list type="bullet">
/// <item>Dock=Top 的根 TableLayoutPanel 绝对列宽之和超过客户区 → 必须报违规并带上该控件名；</item>
/// <item>尺寸正确的同形窗体 → 不得报违规。</item>
/// </list>
/// </summary>
public class UiAuditClippingCheckTests
{
    [Fact]
    public void DockedRootWithOversizedAbsoluteColumns_IsFlagged()
    {
        using var form = new Form { Name = "ClipProbe", ClientSize = new Size(200, 120) };
        try
        {
            var root = new TableLayoutPanel
            {
                Name = "clipRoot",
                Dock = DockStyle.Top,
                ColumnCount = 2,
                RowCount = 1,
                Padding = Padding.Empty,
                Margin = Padding.Empty,
                Height = 30,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            root.Controls.Add(
                new Button { Name = "consoleButton", Text = "控制台", Size = new Size(150, 28), Margin = Padding.Empty },
                1, 0);
            form.Controls.Add(root);
            form.Show();
            Application.DoEvents();

            var findings = UiAuditRunner.CheckClipping(form);

            // 两条新检查都必须命中：根内容超宽（a）+ 绝对列溢出（b）。
            Assert.Contains(findings, f =>
                f.Kind == "docked-root-overflow" && f.Control.Contains("clipRoot", StringComparison.Ordinal));
            Assert.Contains(findings, f =>
                f.Kind == "absolute-column-overflow" && f.Control.Contains("clipRoot", StringComparison.Ordinal));
        }
        finally
        {
            form.Hide();
        }
    }

    [Fact]
    public void CorrectlySizedDockedRoot_IsNotFlagged()
    {
        using var form = new Form { Name = "ClipProbeOk", ClientSize = new Size(400, 160) };
        try
        {
            var root = new TableLayoutPanel
            {
                Name = "okRoot",
                Dock = DockStyle.Top,
                ColumnCount = 2,
                RowCount = 1,
                Padding = Padding.Empty,
                Margin = Padding.Empty,
                Height = 30,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            root.Controls.Add(
                new Button { Name = "okButton", Text = "控制台", Size = new Size(110, 28), Margin = Padding.Empty },
                0, 0);
            form.Controls.Add(root);
            form.Show();
            Application.DoEvents();

            Assert.Empty(UiAuditRunner.CheckClipping(form));
        }
        finally
        {
            form.Hide();
        }
    }
}
