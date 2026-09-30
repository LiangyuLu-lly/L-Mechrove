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

    /// <summary>
    /// 审计误报回归（beta21 b21e）：性能模式编辑器绑定到一行说明的自定义模式时，滚动宿主不限宽的
    /// PreferredSize 把模式行（可折行 FlowLayoutPanel）压到最窄量出多一行，报「根内容溢出」，
    /// 而实际宽度下内容恰好放得下。高度要按根控件的当前宽度量。
    /// </summary>
    [Fact]
    public void WrappableContentIsMeasuredAtTheActualWidth()
    {
        bool audit = Program.UiAuditMode;
        Program.UiAuditMode = true;
        try
        {
            using var form = new CustomModeForm();
            form.CreateControl();
            form.Show();
            Application.DoEvents();
            foreach (string id in new[] { "balanced", "custom1" })
            {
                form.BindMode(id);
                Application.DoEvents();
                Assert.DoesNotContain(UiAuditRunner.CheckClipping(form), f => f.Kind == "docked-root-overflow");
            }
        }
        finally
        {
            Program.UiAuditMode = audit;
        }
    }

    /// <summary>真的放不下（按实际宽度量也超出客户区、又没有滚动条）照样要报。</summary>
    [Fact]
    public void AGenuinelyTooTallPanelRootIsStillFlagged()
    {
        using var form = new Form { Name = "TallProbe", ClientSize = new Size(300, 100) };
        try
        {
            var host = new Panel { Name = "tallRoot", Dock = DockStyle.Fill, AutoScroll = false };
            host.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 400 });
            var column = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
            column.Controls.Add(new Label { Text = "内容", AutoSize = true, Height = 300 });
            host.Controls.Add(column);
            form.Controls.Add(host);
            form.Show();
            Application.DoEvents();

            Assert.Contains(UiAuditRunner.CheckClipping(form), f =>
                f.Kind == "docked-root-overflow" && f.Control.Contains("tallRoot", StringComparison.Ordinal));
        }
        finally
        {
            form.Hide();
        }
    }

    [Fact]
    public void CjkLabelInSlightlyTooNarrowColumn_IsFlagged()
    {
        using var form = new Form { Name = "CjkClipProbe", ClientSize = new Size(400, 80) };
        try
        {
            const string text = "功耗墙";
            Font font = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
            Size noPad = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
            Size overhang = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPrefix | TextFormatFlags.GlyphOverhangPadding | TextFormatFlags.WordBreak);
            Assert.True(overhang.Width > noPad.Width,
                $"CJK overhang fixture: NoPadding={noPad.Width}, GlyphOverhangPadding={overhang.Width}.");

            // availableWidth = column − 8px reserve. Size so NoPadding still fits the +2 slack
            // and GlyphOverhangPadding does not — the YAOSHI miss.
            int columnWidth = noPad.Width + 8;
            var column = new Panel
            {
                Name = "cjkColumn",
                Size = new Size(columnWidth, 30),
                Location = Point.Empty,
                Padding = Padding.Empty,
                Margin = Padding.Empty,
            };
            var label = new Label
            {
                Name = "powerWallLabel",
                Text = text,
                AutoSize = false,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Font = font,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            column.Controls.Add(label);
            form.Controls.Add(column);
            form.Show();
            Application.DoEvents();

            string? clipping = UiAuditRunner.GetTextClipping(label);

            Assert.NotNull(clipping);
            Assert.Contains(text, clipping, StringComparison.Ordinal);
        }
        finally
        {
            form.Hide();
        }
    }

    [Fact]
    public void ComboBoxItemTextWiderThanBounds_IsFlagged()
    {
        using var form = new Form { Name = "ComboClipProbe", ClientSize = new Size(200, 80) };
        try
        {
            var combo = new ComboBox
            {
                Name = "yaoshiCombo",
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 36,
                Height = 28,
                Location = new Point(8, 8),
            };
            combo.Items.Add("功耗墙");
            combo.SelectedIndex = 0;
            form.Controls.Add(combo);
            form.Show();
            Application.DoEvents();

            string? clipping = UiAuditRunner.GetTextClipping(combo);

            Assert.NotNull(clipping);
            Assert.Contains("功耗墙", clipping, StringComparison.Ordinal);
        }
        finally
        {
            form.Hide();
        }
    }
}
