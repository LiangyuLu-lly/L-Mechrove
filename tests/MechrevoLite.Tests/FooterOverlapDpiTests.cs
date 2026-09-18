using MechrevoLite.Hardware;
using MechrevoLite.UI;
using System.Drawing;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// T35（Wave G，round 5 strict）底栏文字/图标重叠 + DPI 缩放错位。
///
/// 缺陷（#8 无界15Xpro ai9H365、#10 耀世16u）：底栏文字与图标矩形相交；DPI 缩放下重排错位。
/// 契约：每个审计视口下底栏子树 **sibling-overlap == 0**、**footer-occlusion == 0**，
/// 且布局倍率只跟真实宿主 DPI 走（审计绘制 DPI 不得参与布局）。
///
/// 失败路径见 <see cref="FooterOverlapDpiFailTests"/>。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class FooterOverlapDpiTests
{
    internal static SettingsForm NewForm(out MechrevoHw hardware)
    {
        bool previousAudit = Program.UiAuditMode;
        bool previousReported = Program.UiAuditUseReportedCapabilities;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.UiAuditUseReportedCapabilities = true;
        hardware = new MechrevoHw(null, new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true, TurboMode = true, CpuPerformanceTuning = true, FanSettings = true,
            LiquidCooling = true, DisplayRefresh = true, ColorCalibration = true, LcdOverdrive = true,
            DgpuDirect = true, IgpuOnly = true, Keyboard = true, Lightbar = true, LogoLight = true,
        });
        Program.hw = hardware;
        // 恢复由调用方的 RestoreProcessStatics 负责（硬件对象由测试持有）。
        _previousAudit = previousAudit;
        _previousReported = previousReported;
        _previousHardware = previousHardware;
        return new SettingsForm();
    }

    static bool _previousAudit;
    static bool _previousReported;
    static MechrevoHw? _previousHardware;

    internal static void RestoreProcessStatics()
    {
        Program.hw = _previousHardware!;
        Program.UiAuditUseReportedCapabilities = _previousReported;
        Program.UiAuditMode = _previousAudit;
    }

    internal static List<(string Kind, string Control, string Detail)> FooterFindings(SettingsForm form) =>
        UiAuditRunner.CheckSiblingOverlaps(form)
            .Where(finding => finding.Control.Contains("panelFooter", StringComparison.OrdinalIgnoreCase))
            .ToList();

    static void Prepare(SettingsForm form)
    {
        form.ClientSize = SettingsForm.CompactDashboardLogicalClientSize;
        form.CreateControl();
        form.Show();
        Application.DoEvents();
        form.PerformLayout();
        form.ApplyResponsiveBounds(new Rectangle(0, 0, 1600, 860));
        Application.DoEvents();
    }

    [Theory]
    [InlineData(1.0f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    [InlineData(2.0f)]
    public void TheFooterHasNoSiblingOverlapAtEveryAuditedScale(float scale)
    {
        SettingsForm form = NewForm(out MechrevoHw hardware);
        try
        {
            Prepare(form);
            if (Math.Abs(scale - 1.0f) > 0.001f)
                form.Scale(new SizeF(scale, scale));
            form.PerformLayout();
            Application.DoEvents();

            List<(string Kind, string Control, string Detail)> findings = FooterFindings(form);
            Assert.True(findings.Count == 0,
                $"倍率 {scale}: 底栏 sibling 重叠：" + string.Join("; ", findings.Select(f => $"{f.Control}: {f.Detail}")));
            Assert.Empty(UiAuditRunner.CheckFooterOcclusion(form));
        }
        finally
        {
            form.Hide();
            form.Dispose();
            hardware.Dispose();
            RestoreProcessStatics();
        }
    }

    [Fact]
    public void TheFooterTextAndIconRectsDoNotIntersect()
    {
        SettingsForm form = NewForm(out MechrevoHw hardware);
        try
        {
            Prepare(form);

            var footer = (Control?)form.Controls.Find("panelFooter", true).FirstOrDefault();
            Assert.NotNull(footer);
            Control[] siblings = footer!.Controls.Cast<Control>().Where(c => c.Visible).ToArray();
            Assert.True(siblings.Length >= 2, "前置：底栏应有至少两个可见子控件（文字/图标）。");

            for (int i = 0; i < siblings.Length; i++)
            for (int j = i + 1; j < siblings.Length; j++)
            {
                Rectangle overlap = Rectangle.Intersect(siblings[i].Bounds, siblings[j].Bounds);
                Assert.True(overlap.Width <= 3 || overlap.Height <= 3,
                    $"{siblings[i].Name} {siblings[i].Bounds} 与 {siblings[j].Name} {siblings[j].Bounds} 相交 {overlap}");
            }
        }
        finally
        {
            form.Hide();
            form.Dispose();
            hardware.Dispose();
            RestoreProcessStatics();
        }
    }

    [Theory]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    [InlineData(2.0f)]
    public void TheFooterReflowTracksTheScaleFactor(float scale)
    {
        SettingsForm form = NewForm(out MechrevoHw hardware);
        try
        {
            Prepare(form);
            var footer = (Control?)form.Controls.Find("panelFooter", true).FirstOrDefault();
            Assert.NotNull(footer);
            int baseWidth = footer!.Width;
            form.Scale(new SizeF(scale, scale));
            form.PerformLayout();
            Application.DoEvents();

            int expected = (int)MathF.Round(baseWidth * scale);
            Assert.InRange(footer.Width, expected - 4, expected + 4);
            foreach (Control child in footer.Controls.Cast<Control>().Where(c => c.Visible))
            {
                Assert.True(child.Right <= footer.ClientSize.Width + 2 && child.Bottom <= footer.ClientSize.Height + 2,
                    $"{child.Name} {child.Bounds} 超出底栏 {footer.ClientSize}");
            }
        }
        finally
        {
            form.Hide();
            form.Dispose();
            hardware.Dispose();
            RestoreProcessStatics();
        }
    }

    [Fact]
    public void TheAuditPaintDpiNeverChangesTheLayoutScale()
    {
        SettingsForm form = NewForm(out MechrevoHw hardware);
        int audit = UiDpi.AuditDpi;
        try
        {
            Prepare(form);
            float before = UiDpi.LayoutScale(form);
            UiDpi.AuditDpi = 192;   // 目标视口 DPI 只允许影响绘制
            float after = UiDpi.LayoutScale(form);

            Assert.Equal(before, after);
            Assert.Equal(96, UiDpi.Baseline);
        }
        finally
        {
            UiDpi.AuditDpi = audit;
            form.Hide();
            form.Dispose();
            hardware.Dispose();
            RestoreProcessStatics();
        }
    }
}
