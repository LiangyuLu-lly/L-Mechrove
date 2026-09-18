using MechrevoLite.UI;
using System.Drawing;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// T35 失败/边界路径：相交必须被抓到，贴边（≤3px）与父子关系不得误报；
/// 真实底栏在放大后仍不得相交。happy 路径见 <see cref="FooterOverlapDpiTests"/>。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class FooterOverlapDpiFailTests
{
    [Fact]
    public void TwoOverlappingSiblingsAreFlagged()
    {
        using var form = new Form { Name = "OverlapProbe", ClientSize = new Size(300, 200) };
        try
        {
            form.Controls.Add(new Label { Name = "footerText", Text = "文字", Bounds = new Rectangle(10, 10, 80, 20) });
            form.Controls.Add(new Label { Name = "footerIcon", Text = "图标", Bounds = new Rectangle(60, 12, 60, 20) });
            form.Show();
            Application.DoEvents();

            var findings = UiAuditRunner.CheckSiblingOverlaps(form);

            Assert.Contains(findings, finding => finding.Kind == "sibling-overlap");
        }
        finally { form.Hide(); }
    }

    [Fact]
    public void TouchingButNotOverlappingSiblingsAreNotFlagged()
    {
        using var form = new Form { Name = "AdjacentProbe", ClientSize = new Size(300, 200) };
        try
        {
            form.Controls.Add(new Label { Name = "left", Text = "左", Bounds = new Rectangle(10, 10, 50, 20) });
            form.Controls.Add(new Label { Name = "right", Text = "右", Bounds = new Rectangle(60, 10, 50, 20) });
            form.Show();
            Application.DoEvents();

            Assert.Empty(UiAuditRunner.CheckSiblingOverlaps(form));
        }
        finally { form.Hide(); }
    }

    [Fact]
    public void AParentAndItsChildAreNotReportsAsSiblings()
    {
        using var form = new Form { Name = "NestedProbe", ClientSize = new Size(300, 200) };
        try
        {
            var parent = new Panel { Name = "parent", Bounds = new Rectangle(10, 10, 120, 40) };
            parent.Controls.Add(new Label { Name = "child", Text = "子", Bounds = new Rectangle(0, 0, 120, 40) });
            form.Controls.Add(parent);
            form.Show();
            Application.DoEvents();

            Assert.Empty(UiAuditRunner.CheckSiblingOverlaps(form));
        }
        finally { form.Hide(); }
    }

    [Fact]
    public void TheRealFooterStaysCleanAfterAnExtraScaleUp()
    {
        SettingsForm form = FooterOverlapDpiTests.NewForm(out var hardware);
        try
        {
            form.ClientSize = SettingsForm.CompactDashboardLogicalClientSize;
            form.CreateControl();
            form.Show();
            Application.DoEvents();
            form.PerformLayout();
            form.ApplyResponsiveBounds(new Rectangle(0, 0, 1600, 860));
            form.Scale(new SizeF(1.5f, 1.5f));
            form.PerformLayout();
            Application.DoEvents();

            Assert.Empty(FooterOverlapDpiTests.FooterFindings(form));
        }
        finally
        {
            form.Hide();
            form.Dispose();
            hardware.Dispose();
            FooterOverlapDpiTests.RestoreProcessStatics();
        }
    }

    [Fact]
    public void TheLayoutScaleNeverDropsBelowOne()
    {
        using var form = new Form { Name = "ScaleProbe", ClientSize = new Size(200, 100) };
        form.CreateControl();
        Assert.True(UiDpi.LayoutScale(form) >= 1F);
        Assert.Equal(96, UiDpi.Baseline);
    }
}
