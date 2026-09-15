using MechrevoLite.Hardware;
using MechrevoLite.UI;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// 最大化布局回归（run8）：最大化/还原后，内容宿主必须填满客户区、固定底栏恰好停靠一次且
/// 与内容零重叠；控件树中同名底栏/内容宿主各只能有一份。同时锁定「固定紧凑高度不得在
/// 非 Normal 窗口状态下回写」这一守卫。
/// </summary>
public class MaximizeLayoutTests
{
    static MechrevoDeviceCapabilities FullCaps() => new()
    {
        ProfileAvailable = true, TurboMode = true, CpuPerformanceTuning = true, FanSettings = true,
        LiquidCooling = true, DisplayRefresh = true, ColorCalibration = true, LcdOverdrive = true,
        DgpuDirect = true, IgpuOnly = true, Keyboard = true, Lightbar = true, LogoLight = true,
    };

    static T Field<T>(SettingsForm form, string name) where T : class =>
        (T)typeof(SettingsForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    static int CountByName(Control root, string name) =>
        root.Controls.Find(name, true).Length;

    static IEnumerable<Control> All(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in All(child)) yield return descendant;
        }
    }

    static void WithForm(Action<SettingsForm> body)
    {
        bool prevAudit = Program.UiAuditMode;
        bool prevReported = Program.UiAuditUseReportedCapabilities;
        MechrevoHw? prevHw = Program.hw;
        Program.UiAuditMode = true;
        Program.UiAuditUseReportedCapabilities = true;
        using var hardware = new MechrevoHw(null, FullCaps());
        Program.hw = hardware;
        try
        {
            using var form = new SettingsForm();
            form.ClientSize = SettingsForm.CompactDashboardLogicalClientSize;
            form.CreateControl();
            form.Show();
            Application.DoEvents();
            form.PerformLayout();
            form.ApplyResponsiveBounds(new Rectangle(0, 0, 2560, 1516));
            Application.DoEvents();
            body(form);
        }
        finally
        {
            Program.hw = prevHw!;
            Program.UiAuditUseReportedCapabilities = prevReported;
            Program.UiAuditMode = prevAudit;
        }
    }

    static void AssertSingleDockedFooter(SettingsForm form, string context)
    {
        Control dashboard = Field<Control>(form, "_dashboard");
        Control host = Field<Control>(form, "_dashboardScroll");
        Control footer = form.Controls.Find("panelFooter", true).Single();

        Assert.Equal(1, CountByName(form, "panelFooter"));
        Assert.Equal(1, CountByName(form, "dashboardPageHost"));
        Assert.Same(dashboard, footer.Parent);
        Assert.Same(dashboard, host.Parent);
        Assert.Equal(host.Bottom, footer.Top);            // 同父坐标系下的零重叠
        Assert.Equal(host.Width, footer.Width);
        Assert.True(footer.Bottom <= dashboard.ClientSize.Height,
            $"{context}: 底栏越出仪表盘宿主（footer.Bottom {footer.Bottom} > dashboard {dashboard.ClientSize.Height}）。");
    }

    [Fact]
    public void LayoutAfterMaximiseSizedResize_ContentHostFillsAndFooterDoesNotOverlap()
    {
        WithForm(form =>
        {
            // 模拟最大化后的客户区（物理 2584x1624 @175% ≈ 逻辑 1477x928）。
            form.ClientSize = new Size(1477, 928);
            form.PerformLayout();
            Application.DoEvents();

            AssertSingleDockedFooter(form, "maximise-sized resize");

            Control host = Field<Control>(form, "_dashboardScroll");
            Assert.True(host.Height > SettingsForm.CompactDashboardLogicalClientSize.Height,
                $"内容宿主未随最大化尺寸增高：{host.Height}。");
        });
    }

    [Fact]
    public void WindowStateMaximised_KeepsContentHostFilledSingleFooterAndControlsReachable()
    {
        WithForm(form =>
        {
            form.WindowState = FormWindowState.Maximized;
            Application.DoEvents();
            form.PerformLayout();
            Application.DoEvents();

            Assert.Equal(FormWindowState.Maximized, form.WindowState);
            AssertSingleDockedFooter(form, "maximised");

            // 最大化后底栏图标行与 GCU 指示仍可见、有面积（可点击的前提）。
            Control gcu = form.Controls.Find("panelGcuStatus", true).Single();
            Assert.True(gcu.Visible && gcu.Width > 0 && gcu.Height > 0, "最大化后 GCU 指示条不可见/无面积。");
            Button quit = Field<Button>(form, "buttonQuit");
            Assert.True(quit.Visible && quit.Width > 0 && quit.Height > 0, "最大化后底部退出键不可见/无面积。");
        });
    }

    [Fact]
    public void AttachDashboardChrome_IsIdempotent_AndDisposesStaleDuplicates()
    {
        WithForm(form =>
        {
            TableLayoutPanel dashboard = Field<TableLayoutPanel>(form, "_dashboard");

            // 预置陈旧重复实例（模拟构建路径被重入后的残留）。
            var staleFooter = new Panel { Name = "panelFooter" };
            dashboard.Controls.Add(staleFooter, 0, 1);
            var staleHost = new BufferedPanel { Name = "dashboardPageHost" };
            dashboard.Controls.Add(staleHost, 0, 0);
            Assert.Equal(2, CountByName(dashboard, "panelFooter"));
            Assert.Equal(2, CountByName(dashboard, "dashboardPageHost"));

            form.AttachDashboardChrome();
            form.AttachDashboardChrome();   // 幂等：重复调用不再新增

            Assert.Equal(1, CountByName(form, "panelFooter"));
            Assert.Equal(1, CountByName(form, "dashboardPageHost"));
            Assert.True(staleFooter.IsDisposed, "陈旧底栏实例未被释放。");
            Assert.True(staleHost.IsDisposed, "陈旧内容宿主实例未被释放。");
            Assert.DoesNotContain(staleFooter, All(form));
            Assert.DoesNotContain(staleHost, All(form));
        });
    }
}
