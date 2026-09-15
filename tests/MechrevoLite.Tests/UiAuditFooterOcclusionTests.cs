using MechrevoLite.Hardware;
using MechrevoLite.UI;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// 底栏遮挡回归（run6 F2）：固定底栏 <c>panelFooter</c> 覆盖内容控件会让它拿不到点击点
/// （UIA GetClickablePoint 抛错、WindowFromPoint 命中底栏），用户表现为「拨不动」。
/// 检查本体是 <see cref="UiAuditRunner.CheckFooterOcclusion"/>，UI 审计每个视口每张窗体都会跑，
/// 并把 <c>footer-occlusion</c> 的逐视口结果写进报告。
///
/// 集成断言（<see cref="SettingsForm_AtCompactWindow_NoControlIsOccludedByFooter"/>）是 F2 的
/// RED→GREEN 锚：修复前「开机自启动」开关滚到底仍落在底栏矩形里。
/// </summary>
public class UiAuditFooterOcclusionTests
{
    [Fact]
    public void ControlCoveredByFooter_IsFlagged()
    {
        using var form = new Form { Name = "FooterProbe", ClientSize = new Size(300, 200) };
        try
        {
            var footer = new Panel { Name = "panelFooter", Dock = DockStyle.Bottom, Height = 60 };
            form.Controls.Add(footer);
            var box = new CheckBox { Name = "buriedSwitch", Text = "被埋开关", Bounds = new Rectangle(20, 150, 120, 24) };
            form.Controls.Add(box);
            form.Show();
            Application.DoEvents();

            var findings = UiAuditRunner.CheckFooterOcclusion(form);

            Assert.Contains(findings, f =>
                f.Kind == "footer-occlusion" && f.Control.Contains("buriedSwitch", StringComparison.Ordinal));
            Assert.True(UiAuditRunner.HasFooter(form));
        }
        finally { form.Hide(); }
    }

    [Fact]
    public void ControlAboveFooter_IsNotFlagged()
    {
        using var form = new Form { Name = "FooterProbeOk", ClientSize = new Size(300, 200) };
        try
        {
            var footer = new Panel { Name = "panelFooter", Dock = DockStyle.Bottom, Height = 60 };
            form.Controls.Add(footer);
            form.Controls.Add(new CheckBox { Name = "visibleSwitch", Text = "正常开关", Bounds = new Rectangle(20, 40, 120, 24) });
            form.Show();
            Application.DoEvents();

            Assert.Empty(UiAuditRunner.CheckFooterOcclusion(form));
        }
        finally { form.Hide(); }
    }

    [Fact]
    public void FormWithoutFooter_IsNotApplicable()
    {
        using var form = new Form { Name = "NoFooterProbe", ClientSize = new Size(300, 200) };
        try
        {
            form.Controls.Add(new CheckBox { Name = "someSwitch", Text = "开关", Bounds = new Rectangle(20, 40, 120, 24) });
            form.Show();
            Application.DoEvents();

            Assert.False(UiAuditRunner.HasFooter(form));
            Assert.Empty(UiAuditRunner.CheckFooterOcclusion(form));
        }
        finally { form.Hide(); }
    }

    /// <summary>
    /// F2 RED→GREEN 锚：默认/最小窗口（529 逻辑客户区）下，展开「更多开关」后滚到最底，
    /// 任何一个可交互控件都不得落在底栏矩形里。修复前「开机自启动」开关仍被底栏盖住。
    /// </summary>
    [Fact]
    public void SettingsForm_AtCompactWindow_NoControlIsOccludedByFooter()
    {
        bool previousAudit = Program.UiAuditMode;
        bool previousReported = Program.UiAuditUseReportedCapabilities;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.UiAuditUseReportedCapabilities = true;
        using var hardware = new MechrevoHw(null, new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            TurboMode = true,
            CpuPerformanceTuning = true,
            FanSettings = true,
            LiquidCooling = true,
            DisplayRefresh = true,
            ColorCalibration = true,
            LcdOverdrive = true,
            DgpuDirect = true,
            IgpuOnly = true,
            Keyboard = true,
            Lightbar = true,
            LogoLight = true,
        });
        Program.hw = hardware;
        try
        {
            using var form = new SettingsForm();
            form.ClientSize = SettingsForm.CompactDashboardLogicalClientSize;
            form.CreateControl();
            form.Show();
            Application.DoEvents();
            form.PerformLayout();
            form.ApplyResponsiveBounds(new Rectangle(0, 0, 1600, 860));
            Application.DoEvents();

            // 更多开关组在审计模式强制展开，含「开机自启动」。
            Control? startup = form.Controls.Find("quick_startup", true).FirstOrDefault();
            Assert.NotNull(startup);
            Assert.True(startup!.Visible, "前置：开机自启动开关可见。");

            var findings = UiAuditRunner.CheckFooterOcclusion(form);

            Assert.True(findings.Count == 0,
                "底栏遮挡了内容控件：" + string.Join("; ", findings.Select(f => $"{f.Control}: {f.Detail}")));
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditUseReportedCapabilities = previousReported;
            Program.UiAuditMode = previousAudit;
        }
    }

    /// <summary>
    /// F2 核心回归锚：滚动量必须覆盖控件树的真实内容底边。折叠组的 AutoSize 只按直接子控件
    /// 的首选高度求和，内部有行溢出的控件（下拉/按钮比行高）不会计入；旧实现直接用
    /// stack.Height 设范围，末尾内容因此滚不上来，被固定底栏盖住。
    /// </summary>
    [Fact]
    public void UpdateDashboardScroll_RangeCoversDeepestContent()
    {
        bool previousAudit = Program.UiAuditMode;
        bool previousReported = Program.UiAuditUseReportedCapabilities;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.UiAuditUseReportedCapabilities = true;
        using var hardware = new MechrevoHw(null, new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true, TurboMode = true, CpuPerformanceTuning = true, FanSettings = true,
            LiquidCooling = true, DisplayRefresh = true, ColorCalibration = true, LcdOverdrive = true,
            DgpuDirect = true, IgpuOnly = true, Keyboard = true, Lightbar = true, LogoLight = true,
        });
        Program.hw = hardware;
        try
        {
            using var form = new SettingsForm();
            form.ClientSize = SettingsForm.CompactDashboardLogicalClientSize;
            form.CreateControl();
            form.Show();
            Application.DoEvents();
            form.ApplyResponsiveBounds(new Rectangle(0, 0, 1600, 860));
            Application.DoEvents();

            Control stack = (Control)typeof(SettingsForm)
                .GetField("_dashboardStack", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            Control host = (Control)typeof(SettingsForm)
                .GetField("_dashboardScroll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            RScrollBar bar = (RScrollBar)typeof(SettingsForm)
                .GetField("_dashboardScrollBar", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

            typeof(SettingsForm)
                .GetMethod("UpdateDashboardScroll", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(form, new object[] { true });
            int deep = (int)typeof(SettingsForm)
                .GetMethod("DeepestContentBottom", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { stack })!;
            int required = deep + stack.Padding.Bottom - host.ClientSize.Height;

            Assert.True(required > 0, "前置：内容确实溢出视口。");
            Assert.True(bar.MaxValue >= required - 1,
                $"滚动量 {bar.MaxValue} 未覆盖真实内容底边 {deep}（视口 {host.ClientSize.Height}，需要 {required}）。");
            Assert.Empty(UiAuditRunner.CheckFooterOcclusion(form));
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditUseReportedCapabilities = previousReported;
            Program.UiAuditMode = previousAudit;
        }
    }
}
