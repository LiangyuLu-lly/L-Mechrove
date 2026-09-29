using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.Overlay;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

public class SettingsLayoutTests
{
    [Fact]
    public void DashboardSections_DockTopAfterConstruct()
    {
        using var form = new SettingsForm();
        var sections = (Control[])typeof(SettingsForm)
            .GetField("_dashboardSections", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(form)!;

        Assert.NotEmpty(sections);
        Assert.All(sections, section => Assert.Equal(DockStyle.Top, section.Dock));
    }

    [Fact]
    public void ModeButtons_FormSegmentedControlsAfterConstruction()
    {
        bool previousAuditMode = Program.UiAuditMode;
        Program.UiAuditMode = true;
        try
        {
            using var form = new SettingsForm();
            RButton silent = form.Controls.Find("buttonSilent", true).OfType<RButton>().Single();
            RButton balanced = form.Controls.Find("buttonBalanced", true).OfType<RButton>().Single();
            RButton turbo = form.Controls.Find("buttonTurbo", true).OfType<RButton>().Single();
            RButton eco = form.Controls.Find("buttonEco", true).OfType<RButton>().Single();
            RButton standard = form.Controls.Find("buttonStandard", true).OfType<RButton>().Single();
            RButton ultimate = form.Controls.Find("buttonUltimate", true).OfType<RButton>().Single();

            // 深潜座舱：性能组（5 段）与显卡组（3 段）是两段连续的分段控件（首/中/尾 + 零间距）。
            Assert.Equal(RSegmentPosition.First, silent.SegmentPosition);
            Assert.Equal(RSegmentPosition.Middle, balanced.SegmentPosition);
            Assert.Equal(RSegmentPosition.Middle, turbo.SegmentPosition);
            Assert.Equal(RSegmentPosition.First, eco.SegmentPosition);
            Assert.Equal(RSegmentPosition.Middle, standard.SegmentPosition);
            Assert.Equal(RSegmentPosition.Last, ultimate.SegmentPosition);
            Assert.Equal(Padding.Empty, balanced.Margin);
            Assert.Equal(Padding.Empty, standard.Margin);
        }
        finally
        {
            Program.UiAuditMode = previousAuditMode;
        }
    }

    [Fact]
    public void DpiResponsiveBoundsDispatcher_IsParameterless()
    {
        System.Windows.Forms.MethodInvoker callback = RForm.CreateResponsiveBoundsCallback(null!);

        Assert.Empty(callback.Method.GetParameters());
    }

    [Fact]
    public void DefaultDashboardUsesTheV2SinglePageWidth()
    {
        // DESIGN.md v2 §5：单页纵排固定 420 逻辑宽。
        Assert.Equal(420, SettingsForm.CompactDashboardLogicalClientSize.Width);
    }

    [Fact]
    public void BrightnessPanelIsAutoSize()
    {
        // 亮度卡行数随能力位增减（局部调光/响应加速/校色各自成行），固定高度在任何
        // 缩放下都会溢出（v2 第一轮审计 100%-200% 连续报溢出）——契约改为 AutoSize。
        Assert.False(typeof(SettingsForm).GetField("BrightnessLogicalHeight",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) is not null,
            "BrightnessLogicalHeight 常量已随固定高亮度卡移除，不应再存在。");
    }

    [Fact]
    public void FixedWidthDashboardRowsUseFractionsThatCannotCollapseTrailingControls()
    {
        Assert.Equal((26F, 60F, 14F), SettingsForm.BrightnessColumnPercentages);
        // 界面外观卡改为「标题吃剩余 + 两个按钮按文字实测宽」：百分比列在窄弹窗里把
        // 日间/夜间压成一个字（UI 审计 buttonDayMode 需 35px、只剩 18px）。
        Assert.Null(typeof(SettingsForm).GetField("ThemeColumnPercentages",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
    }

    [Fact]
    public void ThemeModeButtonsSizeToTheirTextAtAnyScale()
    {
        var panel = SettingsForm.CreateThemeModePanel(v => v * 2, _ => { }, out Button day, out Button night);
        using (panel)
        {
            Assert.True(day.AutoSize);
            Assert.True(night.AutoSize);
            Assert.True(day.MinimumSize.Width >= 128);
            var layout = Assert.IsAssignableFrom<TableLayoutPanel>(panel.Controls[0]);
            Assert.Equal(SizeType.Percent, layout.ColumnStyles[0].SizeType);
            Assert.Equal(SizeType.AutoSize, layout.ColumnStyles[1].SizeType);
            Assert.Equal(SizeType.AutoSize, layout.ColumnStyles[2].SizeType);
        }
    }

    [Fact]
    public void DashboardHasNoWideBreakpointAnymore()
    {
        // v2 单页纵排废弃了两列断点：无论窗口多宽都是单列纵排（宽由窗口固定）。
        Assert.False(typeof(SettingsForm).GetMethod("GetDashboardColumnCount",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) is not null,
            "GetDashboardColumnCount 已随两列卡片布局移除，不应再存在。");
    }

    [Fact]
    public void LiquidCoolingRow_ReservesTenPercentMoreHeightForTheLightingButton()
    {
        // v3 紧凑化：灯光键改为固定窄列小键（26 逻辑高），三行 32 + 余量。
        // 旧值 156 是"按钮高度跟随下拉首选高"时代的字体余量，已随真机验收的瘦身作废。
        Assert.Equal(112, SettingsForm.LiquidCoolingLogicalHeight);
        Assert.True(SettingsForm.LiquidCoolingLogicalHeight >= 96 * 1.10F);
    }

    [Fact]
    public void LiquidCoolingControls_UseCompactConnectionStatusAndDefaultInputHeight()
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

            Panel panel = form.Controls.Find("panelLc", true).OfType<Panel>().Single();
            Button status = FindControl<Button>(panel, "未连接");
            ComboBox pump = form.Controls.Find("comboLcPump", true).OfType<ComboBox>().Single();
            Button light = form.Controls.Find("buttonLcLight", true).OfType<Button>().Single();

            Assert.True(status.Width <= pump.Width + 1,
                $"Connection status width {status.Width}px should not exceed one liquid-cooling control column ({pump.Width}px).");
            // 状态按钮必须装得下自己的文字。这里原来断言的是「高度 ≤24（比 34px 表头行矮 30%）」——
            // 那个"矮一圈"靠的是宿主面板的上下内边距，而内边距会随 DPI 放大：175% 实测表头行 60px、
            // 宿主 49px、按钮只剩 32px，而 14.9pt 的文字加按钮自带边框内边距要约 33px，
            // 于是文字被截断（用户 2026-09-11 报告）。现在按钮撑满行高，改用"装得下"这条不变量。
            int neededTextHeight = TextRenderer.MeasureText(status.Text, status.Font).Height;
            Assert.True(status.Height >= neededTextHeight + 4,
                $"液冷状态按钮高 {status.Height}px，装不下 {neededTextHeight}px 的文字。");
            // v3 紧凑化：灯光键改成与下拉同行的固定窄键（真机验收：原实现吃满整行太大）。
            Assert.True(light.Height <= status.Height + 4,
                $"灯光键高 {light.Height} 应 ≈ 同行的状态键高 {status.Height}（同一行同一标尺；"
                + "原生下拉高度由字体驱动，跨标尺比较不稳定）。");
            Assert.True(light.Width <= pump.Width + 1,
                $"灯光键宽 {light.Width}px 不应超过泵速下拉 {pump.Width}px（固定窄列）。");
            // v3 灯光键进一步收窄到 96 逻辑宽，避免在低 DPL 下拉列里显胖。
            Assert.True(light.Width <= ResponsiveLayout.LogicalToDevice(form, 96),
                $"灯光键宽 {light.Width}px 应不超过 96 逻辑像素（{ResponsiveLayout.LogicalToDevice(form, 96)}px）。");
            // 泵速下拉跨第 1、2 列；列宽调整后仍必须占满这两列的完整宽度。
            var lcLayout = Assert.IsType<TableLayoutPanel>(pump.Parent);
            int expectedPumpWidth = lcLayout.GetColumnWidths().Skip(1).Take(2).Sum();
            Assert.Equal(expectedPumpWidth, pump.Width);

            // 灯光行迷你控件（用户 2026-09-12：还是稍大、没有圆角、显得素）：
            // 两个控件都必须是圆角 RButton，Surface 底 + Border 描边 + 悬停提亮，
            // 上下 4px 边距（32px 行内 24 设备px 高）。
            AssertMiniRoundedControl(light);
            AssertMiniRoundedControl(status);
            Assert.True(status.AutoEllipsis, "状态键必须保留单行省略号。");
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditUseReportedCapabilities = previousReportedCapabilities;
            Program.UiAuditMode = previousAuditMode;
        }
    }

    // 用户 2026-09-13：设置弹窗整体移除「局部调光」；「悬浮窗」开关与 footer 重复也移除；
    // 「自动刷新率」从弹窗移到仪表盘「屏幕」行头右侧（与标题同一行）。
    [Fact]
    public void LocalDimmingControl_IsRemovedFromTheControlTree()
    {
        using var form = new SettingsForm();
        form.CreateControl();
        form.PerformLayout();

        Assert.Empty(form.Controls.Find("checkLocalDimming", true));
        Assert.Null(typeof(SettingsForm).GetField("_localDimChk",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form));
    }

    [Fact]
    public void SettingsDialog_HasNoOverlayOrLocalDimmingOrAutoHzToggle_WhileFooterOverlayButtonRemains()
    {
        using var form = new SettingsForm();
        form.CreateControl();
        form.PerformLayout();

        Control themePanel = (Control)typeof(SettingsForm).GetField("_themeModePanel",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        Assert.Null(typeof(SettingsForm).GetField("_officialConsolePanel",
            BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Empty(form.Controls.Find("panelOfficialConsole", true));
        Control overdrive = (Control)typeof(SettingsForm).GetField("_overdriveChk",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        using var dialog = new SettingsDialog(themePanel, overdrive, displayGroupAvailable: true);

        Assert.Empty(dialog.Controls.Find("checkOverlayToggle", true));
        Assert.Empty(dialog.Controls.Find("checkLocalDimming", true));
        Assert.Empty(dialog.Controls.Find("checkAutoRefreshRate", true));
        // footer 悬浮窗键保持原位（悬浮窗功能本身不删，只删弹窗里的重复入口）。
        Assert.Single(form.Controls.Find("buttonOverlay", true));
    }

    [Fact]
    public void AutoRefreshRateToggle_SitsRightOfTheScreenRowTitleInTheSameHeaderRow()
    {
        using var form = new SettingsForm();
        form.CreateControl();
        form.PerformLayout();

        CheckBox autoHz = form.Controls.Find("checkAutoRefreshRate", true).OfType<CheckBox>().Single();
        Label title = form.Controls.Find("labelScreenRow", true).OfType<Label>().Single();
        ComboBox combo = form.Controls.Find("comboColorCalibration", true).OfType<ComboBox>().Single();

        // 2026-09-14：行头右侧改为两个并排的 TLP AutoSize 列（校色下拉 + 自动刷新率开关），
        // 开关与下拉同在屏幕行头 TableLayoutPanel 里（断言随布局更新）。
        Assert.Same(autoHz.Parent, combo.Parent);
        var head = Assert.IsAssignableFrom<TableLayoutPanel>(autoHz.Parent);
        Assert.True(head.GetColumn(combo) < head.GetColumn(autoHz) && head.GetColumn(autoHz) > head.GetColumn(title),
            $"校色下拉（列 {head.GetColumn(combo)}）与 自动刷新率 开关（列 {head.GetColumn(autoHz)}）必须在「屏幕」标题（列 {head.GetColumn(title)}）右侧同一行头里。");
    }

    private static void AssertMiniRoundedControl(Button control)
    {
        RButton rounded = Assert.IsType<RButton>(control);
        Assert.Equal(12, rounded.BorderRadius);
        Assert.Equal(UiVisualStyle.Border, rounded.BorderColor);
        Assert.Equal(UiVisualStyle.Surface, control.BackColor);
        Assert.Equal(UiVisualStyle.SurfaceRaised, control.FlatAppearance.MouseOverBackColor);
        Assert.Equal(new Padding(0, 4, 0, 4), control.Margin);
    }

    /// <summary>
    /// 电池上限的百分比读数必须装得下最宽的串（"100%"）。UI 审计在多个视口报过
    /// 「needs 36, available 15」——真机截图里那行只剩一个 "1"。
    /// </summary>
    [Fact]
    public void BatteryLimitReadoutFitsTheWidestText()
    {
        using var form = new SettingsForm();
        form.ClientSize = SettingsForm.CompactDashboardLogicalClientSize;
        form.CreateControl();
        form.PerformLayout();

        Label value = form.Controls.Find("labelBatteryLimitValue", true).OfType<Label>().Single();
        int needed = TextRenderer.MeasureText("100%", value.Font).Width;

        Assert.True(value.Width >= needed,
            $"电池读数标签宽 {value.Width}px，装不下最宽文本 {needed}px" +
            $"（父容器 {value.Parent?.GetType().Name} {value.Parent?.Width}px，Dock={value.Dock}）。");
    }

    [Fact]
    public void LiquidCoolingLightMenu_ExposesOfficialHeadFanAndCustomProfiles()
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
            FieldInfo field = typeof(SettingsForm).GetField("_liquidCoolingLightMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ContextMenuStrip menu = Assert.IsAssignableFrom<ContextMenuStrip>(field.GetValue(form));   // T5：菜单改为定制子类
            string[] labels = menu.Items.OfType<ToolStripMenuItem>().Select(item => item.Text ?? "").ToArray();

            Assert.Contains("青色常亮", labels);
            Assert.Contains("青色呼吸", labels);
            Assert.Contains("自定义颜色常亮…", labels);
            Assert.Contains("自定义颜色呼吸…", labels);
            Assert.Contains("多彩", labels);
            Assert.Contains("彩色呼吸", labels);
            Assert.Contains("风扇旋转色（Mk2）", labels);
            Assert.Contains("风扇彩虹（Mk2）", labels);
            Assert.Contains("关闭全部灯光", labels);
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditUseReportedCapabilities = previousReportedCapabilities;
            Program.UiAuditMode = previousAuditMode;
        }
    }

    [Fact]
    public void CollapseGroup_HeaderRendersAboveItsContent()
    {
        // Dock=Top 按 Z 序倒序布局（最后加的最靠顶）。添加顺序 content→header→divider
        // 之外的组织方式会让内容压到标题上方（审计展开态实证）。
        using var group = new RCollapseGroup("液冷", UiGlyph.Kind.Droplet, "test_group_dock_order", defaultExpanded: true);
        group.PerformLayout();
        Control header = group.Controls.Find("collapseGroup_test_group_dock_order_header", true).Single();
        Control content = group.Controls.Find("collapseGroup_test_group_dock_order_content", true).Single();
        Control divider = group.Controls.Find("collapseGroup_test_group_dock_order_divider", true).Single();

        Assert.True(divider.Top < header.Top || group.Padding.Top >= header.Top,
            "The divider must render above the header.");
        Assert.True(header.Top < content.Top, "The header must render above the content.");
    }

    [Fact]
    public void LightingCollapseGroup_IsArrangedIntoTheDashboardStack()
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

            // 灯光组必须进 dashboardStack：RefreshDeviceCapabilities 里先 Clear 再逐块
            // EnableSection，漏发灯光组会让整组（标题+键盘/灯条/Logo 行）从页面栈消失。
            Control lighting = form.Controls.Find("collapseGroup_group_light_open", true).Single();
            Assert.Equal("dashboardStack", lighting.Parent?.Name);
            // 三盏灯行都在组内容里（EnabledSection 只负责装组，行可见性走能力位）。
            Assert.Single(form.Controls.Find("rowKeyboard", true));
            Assert.Single(form.Controls.Find("rowLightbar", true));
            Assert.Single(form.Controls.Find("rowLogo", true));
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAuditMode;
        }
    }

    // ColorCalibrationButton_ReservesRightLayoutMargin 已删（2026-09-14）：
    // 校色按钮改为屏幕行头内联下拉（comboColorCalibration），布局护栏移到 Run5LcCalibTests。

    [Fact]
    public void MuxOnlyGpuLayout_ShowsPureIgpuButHidesAutomaticMode()
    {
        bool previousAuditMode = Program.UiAuditMode;
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = false;
        using var hardware = new MechrevoLite.Hardware.MechrevoHw(null, new MechrevoLite.Hardware.MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            DgpuDirect = true,
            IgpuOnly = false,
        });
        Program.hw = hardware;
        try
        {
            using var form = new SettingsForm();
            form.CreateControl();
            form.RefreshDeviceCapabilities();
            Button eco = form.Controls.Find("buttonEco", true).OfType<Button>().Single();
            Button standard = form.Controls.Find("buttonStandard", true).OfType<Button>().Single();
            Button ultimate = form.Controls.Find("buttonUltimate", true).OfType<Button>().Single();
            TableLayoutPanel table = form.Controls.Find("tableGPU", true).OfType<TableLayoutPanel>().Single();

            form.VisualiseGPUButtons(eco: true, ultimate: true, auto: false);

            Assert.Contains(eco, table.Controls.Cast<Control>());
            Assert.Contains(standard, table.Controls.Cast<Control>());
            Assert.Contains(ultimate, table.Controls.Cast<Control>());
            // 自动模式按钮已整体移除：不再出现在窗体控件树里（不只是移出布局）。
            Assert.Empty(form.Controls.Find("buttonOptimized", true));
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAuditMode;
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("night", true)]
    [InlineData("day", false)]
    [InlineData("DAY", false)]
    public void ThemeMode_UsesNightByDefaultAndAcceptsAnExplicitDayPreference(string? savedMode, bool expectedNight)
    {
        Assert.Equal(expectedNight, UiVisualStyle.ResolveNightMode(savedMode));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThemeAccentText_HasReadableContrastInBothModes(bool night)
    {
        (Color background, Color foreground) = UiVisualStyle.GetAccentButtonColors(night);
        Assert.True(UiVisualStyle.ContrastRatio(background, foreground) >= 4.5,
            $"Accent text contrast was too low for {(night ? "night" : "day")} mode.");

        (background, foreground) = UiVisualStyle.GetAccentButtonHoverColors(night);
        Assert.True(UiVisualStyle.ContrastRatio(background, foreground) >= 4.5,
            $"Accent hover text contrast was too low for {(night ? "night" : "day")} mode.");
    }

    [Fact]
    public void NightTheme_ReplacesDayForegroundsAndPreservesSemanticStatusColors()
    {
        Color semanticStatus = Color.FromArgb(0, 200, 80);
        UiVisualStyle.SetAuditNightMode(true);
        try
        {
            using var form = new Form();
            // 初值写死为「日间色板」的正文/次要色（DESIGN.md 第 2 节）：切到夜间后应被换成夜间对应色。
            var title = new Label { ForeColor = Color.FromArgb(0x16, 0x20, 0x2E) };
            var description = new Label { ForeColor = Color.FromArgb(0x5A, 0x6B, 0x80) };
            var status = new Label { ForeColor = semanticStatus };
            form.Controls.Add(title);
            form.Controls.Add(description);
            form.Controls.Add(status);

            UiVisualStyle.ApplyWindow(form);

            Assert.Equal(UiVisualStyle.Text, title.ForeColor);
            Assert.Equal(UiVisualStyle.Muted, description.ForeColor);
            Assert.Equal(semanticStatus, status.ForeColor);
        }
        finally
        {
            UiVisualStyle.SetAuditNightMode(null);
        }
    }

    [Fact]
    public void OverlayPowerText_OnlyRendersAvailablePositiveReadings()
    {
        Assert.Equal("", HardwareOverlay.FormatPowerText(null));
        Assert.Equal("", HardwareOverlay.FormatPowerText(-1f));
        Assert.Equal("", HardwareOverlay.FormatPowerText(0f));
        Assert.Equal("42.3W", HardwareOverlay.FormatPowerText(42.34f));
    }

    [Theory]
    [InlineData(false, 35)]
    [InlineData(false, 100)]
    [InlineData(false, 300)]
    [InlineData(true, 35)]
    [InlineData(true, 100)]
    [InlineData(true, 300)]
    public void OverlayTelemetryColumn_ReservesSpaceForFrequencyBeforePower(bool showFans, int scalePercent)
    {
        using var overlay = new HardwareOverlay();
        SetPrivateField(overlay, "_showTemp", true);
        SetPrivateField(overlay, "_showFans", showFans);
        SetPrivateField(overlay, "_showPower", true);
        SetPrivateField(overlay, "_scalePercent", scalePercent);
        InvokePrivateMethod(overlay, "UpdateOverlaySize");

        float scale = 2f * scalePercent / 100f;
        float telemetryWidth = MeasureTelemetryWidth(showFans, scale);
        int leftPadding = Scale(scale, 8);
        int powerColumnWidth = Scale(scale, 46);
        float availableWidth = overlay.Size.Width - leftPadding - powerColumnWidth - leftPadding;

        Assert.True(telemetryWidth <= availableWidth,
            $"Telemetry requires {telemetryWidth:F1}px but only {availableWidth:F1}px is available before the power column at {scalePercent}% scale.");
    }

    private static void SetPrivateField(HardwareOverlay overlay, string name, object value)
    {
        FieldInfo field = typeof(HardwareOverlay).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(overlay, value);
    }

    private static T FindControl<T>(Control root, string text) where T : Control =>
        Flatten(root).OfType<T>().Single(control => control.Text == text);

    private static IEnumerable<Control> Flatten(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
            foreach (Control descendant in Flatten(child))
                yield return descendant;
    }

    private static void InvokePrivateMethod(HardwareOverlay overlay, string name)
    {
        MethodInfo method = typeof(HardwareOverlay).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(overlay, null);
    }

    private static float MeasureTelemetryWidth(bool showFans, float scale)
    {
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = new Font("Consolas", 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var rpmFont = new Font("Consolas", 8.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);

        const string temperatureAndFrequency = "GPU: 100° 9.9G ";
        const string fan = "9999";
        float charWidth = graphics.MeasureString("XX", font).Width - graphics.MeasureString("X", font).Width;
        float end = graphics.MeasureString(temperatureAndFrequency, font).Width;
        if (!showFans) return end;

        float fanX = charWidth * temperatureAndFrequency.Length;
        end = Math.Max(end, fanX + graphics.MeasureString(fan, font).Width);
        float rpmX = fanX + charWidth * fan.Length + 2f * scale;
        return Math.Max(end, rpmX + graphics.MeasureString("RPM", rpmFont).Width);
    }

    private static int Scale(float scale, int value) => (int)(scale * value);

    /// <summary>
    /// I3：「更多开关」**首次**展开时三组间距异常（折叠期卡片被放进从未可见的容器，
    /// 网格高度按窄宽算成"每项一行"的超高值并冻结）。展开后必须确定性重排：
    /// 每个网格的高度 == 其当前宽度下的首选高度，卡片高度 == 根布局底边 + 底内边距。
    /// 注意：审计模式会在构造期强制展开，覆盖不到这条路径，所以这里必须 UiAuditMode=false。
    /// </summary>
    [Fact]
    public void QuickGroup_FirstExpansion_FitsGridHeightsToContent()
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = false;
        Program.hw = null!;
        try
        {
            using var form = new SettingsForm();
            form.CreateControl();
            form.PerformLayout();

            var group = form.Controls.Find("collapseGroup_group_quick_open", true)
                .OfType<RCollapseGroup>().Single();
            group.Visible = true;
            Assert.False(group.Expanded, "前置条件：更多开关组默认为收起。");

            // 机制级复现真机故障：真机上卡片在「从未可见」的容器里被按 ~144px 窄宽算过一次
            // （不可见容器不参与布局 → 网格被算成"每项一行"的超高值并冻结）。这里显式复现该状态：
            // 先展开让控件落地，再收起（内容不可见，布局被跳过），然后写入窄宽。
            group.Toggle();
            form.PerformLayout();
            var card = form.Controls.Find("panelQuickSwitch", true).OfType<Panel>().Single();
            group.Toggle();
            form.PerformLayout();
            card.Width = 144;                     // 折叠期窄宽：不可见 → 不触发任何重排，几何就此冻结
            form.PerformLayout();

            group.Toggle();                       // 再展开：修复后展开时会有确定性 fit 纠正几何
            form.PerformLayout();

            var root = form.Controls.Find("quickSwitchRoot", true).OfType<TableLayoutPanel>().Single();
            var grids = root.Controls.OfType<FlowLayoutPanel>().ToArray();
            Assert.NotEmpty(grids);
            foreach (FlowLayoutPanel grid in grids)
            {
                Assert.True(grid.Width > 0, "网格必须有实际宽度（折叠期几何不应为 0）。");
                int expected = grid.GetPreferredSize(new Size(grid.Width, 0)).Height;
                Assert.InRange(grid.Height, expected - 1, expected + 1);
            }
            int cardExpected = root.Bottom + card.Padding.Bottom;
            Assert.InRange(card.Height, cardExpected - 1, cardExpected + 1);
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
        }
    }

    /// <summary>I3 的不变量：收起再展开（用户说的"关掉后重新打开就正常"）必须保持同样的几何。</summary>
    [Fact]
    public void QuickGroup_ReExpansion_KeepsStableHeights()
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = false;
        Program.hw = null!;
        try
        {
            using var form = new SettingsForm();
            form.CreateControl();
            form.PerformLayout();
            var group = form.Controls.Find("collapseGroup_group_quick_open", true)
                .OfType<RCollapseGroup>().Single();
            group.Visible = true;

            group.Toggle();
            form.PerformLayout();
            var root = form.Controls.Find("quickSwitchRoot", true).OfType<TableLayoutPanel>().Single();
            int[] first = root.Controls.OfType<FlowLayoutPanel>().Select(g => g.Height).ToArray();

            group.Toggle();                       // 收起
            form.PerformLayout();
            group.Toggle();                       // 再展开
            form.PerformLayout();
            int[] second = root.Controls.OfType<FlowLayoutPanel>().Select(g => g.Height).ToArray();

            Assert.Equal(first.Length, second.Length);
            for (int i = 0; i < first.Length; i++)
                Assert.InRange(second[i], first[i] - 1, first[i] + 1);
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
        }
    }

    /// <summary>
    /// I4a 的结构性前提：待机（等待命令确认）期间把焦点停在卡片面板上——Panel 可被编程聚焦、
    /// 不画焦点环、且不进 Tab 序，因此蓝色高光不会跳到右侧/下一个开关。
    /// 真机证据（pending 期间截图）在 artifacts 中。
    /// </summary>
    [Fact]
    public void QuickSwitch_CardPanelCanHostThePendingFocusPark()
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null!;
        try
        {
            using var form = new SettingsForm();
            form.CreateControl();
            form.PerformLayout();
            // I4a 的停放点 = 快捷开关的父容器（分组网格）：可被编程聚焦、不画焦点环、不进 Tab 序。
            // 必须先展开组：停放点要在可见链里才是可聚焦的（真机待机时组本来就是展开的）。
            var quickGroup = form.Controls.Find("collapseGroup_group_quick_open", true)
                .OfType<RCollapseGroup>().Single();
            quickGroup.Visible = true;
            quickGroup.Toggle();
            form.PerformLayout();

            RCheckBox? quickSwitch = null;
            void Walk(Control parent)
            {
                foreach (Control child in parent.Controls)
                {
                    if (quickSwitch is null && child is RCheckBox box &&
                        box.Name.StartsWith("quick_", StringComparison.Ordinal))
                        quickSwitch = box;
                    Walk(child);
                }
            }
            Walk(form);
            Assert.NotNull(quickSwitch);
            var parkSink = quickSwitch!.Parent;
            Assert.NotNull(parkSink);
            // CanSelect 还要求窗体处于显示状态（测试宿主不显示窗体，属宿主假象）；这里断言产品侧不变量：
            // 停放点必须是可编程聚焦的容器（Panel 系）、展开后自身可见可用、且不进 Tab 序。
            // 端到端行为（待机期间高光不跳走）由真机截图证据覆盖。
            // 结构性前提（无头宿主无法显示窗体，故不断言运行期焦点行为；端到端行为由真机截图证据覆盖）。
            Assert.IsAssignableFrom<Panel>(parkSink);
            Assert.False(parkSink.TabStop, "停放点不得进入 Tab 序（不改变键盘导航）。");
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
        }
    }
}
