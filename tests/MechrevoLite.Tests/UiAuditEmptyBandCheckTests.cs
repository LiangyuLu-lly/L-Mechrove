using MechrevoLite.Hardware;
using MechrevoLite.UI;
using System.Drawing;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// 能力门禁回归检查（run5 门禁审计钉死）：机型不支持某功能时入口被隐藏，
/// 隐藏必须**收拢**——不得留下「有高度无内容」的空带，剩余行不得互相压盖。
/// 检查本体是 <see cref="UiAuditRunner.CheckEmptyBands"/> 与
/// <see cref="UiAuditRunner.CheckSiblingOverlaps"/>（UI 审计每视口每窗体都会跑）。
/// </summary>
public class UiAuditEmptyBandCheckTests
{
    [Fact]
    public void FixedRowWithOnlyHiddenChild_IsFlaggedAsEmptyBand()
    {
        using var form = new Form { Name = "BandProbe", ClientSize = new Size(300, 120) };
        var rows = new TableLayoutPanel { Name = "bandRows", Dock = DockStyle.Top, ColumnCount = 1, RowCount = 2, Height = 70 };
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // 固定行高：隐藏子控件后仍是 34px
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        var gated = new Button { Name = "gatedChild", Text = "门禁行", Visible = false };
        rows.Controls.Add(gated, 0, 0);
        rows.Controls.Add(new Button { Name = "keptChild", Text = "保留行" }, 0, 1);
        form.Controls.Add(rows);
        form.Show();
        Application.DoEvents();

        var findings = UiAuditRunner.CheckEmptyBands(form);

        Assert.Contains(findings, f => f.Kind == "empty-band" && f.Control.Contains("bandRows", StringComparison.Ordinal));
    }

    [Fact]
    public void RowWithVisibleChild_IsNotFlagged()
    {
        using var form = new Form { Name = "BandProbeOk", ClientSize = new Size(300, 120) };
        var rows = new TableLayoutPanel { Name = "okRows", Dock = DockStyle.Top, ColumnCount = 1, RowCount = 2, Height = 70 };
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        rows.Controls.Add(new Button { Name = "visibleChild", Text = "可见行" }, 0, 0);
        rows.Controls.Add(new Button { Name = "keptChild", Text = "保留行" }, 0, 1);
        form.Controls.Add(rows);
        form.Show();
        Application.DoEvents();

        Assert.Empty(UiAuditRunner.CheckEmptyBands(form));
    }

    [Fact]
    public void ContainerWithAllChildrenHidden_IsFlaggedAsEmptyBand()
    {
        using var form = new Form { Name = "PanelProbe", ClientSize = new Size(300, 120) };
        var panel = new Panel { Name = "gatedPanel", Dock = DockStyle.Top, Height = 40 };
        panel.Controls.Add(new Button { Name = "hiddenChild", Visible = false });
        form.Controls.Add(panel);
        form.Show();
        Application.DoEvents();

        var findings = UiAuditRunner.CheckEmptyBands(form);

        Assert.Contains(findings, f => f.Kind == "empty-band" && f.Control.Contains("gatedPanel", StringComparison.Ordinal));
    }

    [Fact]
    public void OverlappingSiblings_AreFlagged()
    {
        using var form = new Form { Name = "OverlapProbe", ClientSize = new Size(300, 120) };
        var host = new Panel { Name = "overlapHost", Dock = DockStyle.Fill };
        host.Controls.Add(new Button { Name = "a", Bounds = new Rectangle(0, 0, 100, 30) });
        host.Controls.Add(new Button { Name = "b", Bounds = new Rectangle(10, 5, 100, 30) });
        form.Controls.Add(host);
        form.Show();
        Application.DoEvents();

        var findings = UiAuditRunner.CheckSiblingOverlaps(form);

        Assert.Contains(findings, f => f.Kind == "sibling-overlap" && f.Control.Contains("overlapHost", StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> CapabilityProfiles()
    {
        yield return new object[] { "NoLighting", new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true, TurboMode = true, TurboSubMode = true, CpuPerformanceTuning = true,
            FanSettings = true, LiquidCooling = true, DisplayRefresh = true, ColorCalibration = true,
            LcdOverdrive = true, DgpuDirect = true, IgpuOnly = true,
            Keyboard = false, Lightbar = false, LogoLight = false,
        } };
        yield return new object[] { "NoSilentTurbo", new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true, TurboMode = true, TurboSubMode = false, CpuPerformanceTuning = true,
            FanSettings = true, LiquidCooling = true, DisplayRefresh = true, ColorCalibration = true,
            LcdOverdrive = true, DgpuDirect = true, IgpuOnly = true,
            Keyboard = true, Lightbar = true, LogoLight = true,
        } };
        yield return new object[] { "NoLiquidCooling", new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true, TurboMode = true, TurboSubMode = true, CpuPerformanceTuning = true,
            FanSettings = true, LiquidCooling = false, DisplayRefresh = true, ColorCalibration = true,
            LcdOverdrive = true, DgpuDirect = true, IgpuOnly = true,
            Keyboard = true, Lightbar = true, LogoLight = true,
        } };
        yield return new object[] { "MinimalDisplay", new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true, TurboMode = false, TurboSubMode = false, CpuPerformanceTuning = false,
            FanSettings = false, LiquidCooling = false, DisplayRefresh = false, ColorCalibration = false,
            LcdOverdrive = false, DgpuDirect = false, IgpuOnly = false,
            Keyboard = false, Lightbar = false, LogoLight = false,
        } };
    }

    /// <summary>
    /// 门禁画像集成断言：按画像注入能力后渲染 SettingsForm，
    /// 「零空带 + 零重叠」必须同时成立（隐藏收拢，不留洞、不挤邻行）。
    /// </summary>
    [Theory]
    [MemberData(nameof(CapabilityProfiles))]
    public void SettingsForm_UnderCapabilityProfile_HasNoEmptyBandAndNoOverlap(string profileName, MechrevoDeviceCapabilities capabilities)
    {
        bool previousAuditMode = Program.UiAuditMode;
        bool previousReported = Program.UiAuditUseReportedCapabilities;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.UiAuditUseReportedCapabilities = true;
        using var auditHardware = new MechrevoHw(null, capabilities);
        Program.hw = auditHardware;
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

            var bands = UiAuditRunner.CheckEmptyBands(form);
            var overlaps = UiAuditRunner.CheckSiblingOverlaps(form);

            Assert.True(bands.Count == 0,
                $"{profileName}: 空带未收拢 —— {string.Join("; ", bands.Select(b => $"{b.Control}: {b.Detail}"))}");
            Assert.True(overlaps.Count == 0,
                $"{profileName}: 可见行重叠 —— {string.Join("; ", overlaps.Select(o => $"{o.Control}: {o.Detail}"))}");
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditUseReportedCapabilities = previousReported;
            Program.UiAuditMode = previousAuditMode;
        }
    }
}
