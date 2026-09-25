using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// run5 二级界面收尾护栏（docs/run5-secondary-ui-redesign.md §0 剩余项）：
/// LightForm 填充行/高度/色块、FanCurveForm 纵排双图+RButton+状态提示拆分、
/// UpdateForm 空态+SetBusy 互斥+按钮两档宽、RColorPicker 随机语义/描边/等宽读数、
/// DonateForm 高度、FirstRunGuideForm 自适应描述+等宽底列、RgbForm 色块控件代差。
/// </summary>
public class Run5SecondaryCloseoutTests
{
    static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls)
        {
            yield return control;
            foreach (Control descendant in Descendants(control))
                yield return descendant;
        }
    }

    static T? GetField<T>(Control form, string name) where T : class
    {
        object? current = form;
        while (current is not null)
        {
            FieldInfo field = current.GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
            if (field is not null) return field.GetValue(form) as T;
            current = current.GetType().BaseType;
        }
        return null;
    }

    static IDisposable UseAuditMode()
    {
        bool previous = Program.UiAuditMode;
        Program.UiAuditMode = true;
        return new Scoped(previous);
    }

    sealed class Scoped(bool previous) : IDisposable
    {
        public void Dispose() => Program.UiAuditMode = previous;
    }

    static int Logical(Control control, int device) => device * 96 / Math.Max(1, control.DeviceDpi);

    // ---------- LightForm ----------

    [Fact]
    public void LightForm_NoFillRow_ContentDerivedHeight_Swatch36()
    {
        using var _ = UseAuditMode();
        using var form = new LightForm("HidLightbar/Ctrl", "灯条灯效", LightForm.LightbarEffects);
        form.CreateControl();

        var table = Assert.IsType<TableLayoutPanel>(form.Controls[0]);
        Assert.DoesNotContain(table.RowStyles.Cast<RowStyle>(), style => style.SizeType == SizeType.Percent && style.Height > 0);

        int logicalH = Logical(form, form.ClientSize.Height);
        Assert.True(logicalH <= 210, $"LightForm 高度 {logicalH} 逻辑 px 超过 210（填充行未删或未收口）。");

        var colorButton = Descendants(form).OfType<RColorButton>().First();
        Assert.Equal(36, Logical(colorButton, colorButton.Width));
    }

    [Fact]
    public void LightForm_OnShown_ClientHeightMatchesTable_NotDesign240()
    {
        using var _ = UseAuditMode();
        using var form = new LightForm("HidLightbar/Ctrl", "灯条灯效", LightForm.LightbarEffects);
        form.CreateControl();
        int designH = 240 * Math.Max(1, form.DeviceDpi) / 96;
        form.ClientSize = new Size(form.ClientSize.Width, designH);
        form.Show();
        Application.DoEvents();

        var table = Assert.IsType<TableLayoutPanel>(form.Controls[0]);
        Assert.Equal(table.Height, form.ClientSize.Height);
        int logicalH = Logical(form, form.ClientSize.Height);
        Assert.True(logicalH < 240, $"LightForm 首帧高度 {logicalH} 仍是 240 设计残留。");
    }

    // ---------- FanCurveForm ----------

    [Fact]
    public void FanCurveForm_HorizontalCharts_RButtonSave_SplitStatusHint()
    {
        using var _ = UseAuditMode();
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.hw = null;
        try
        {
            using var form = new FanCurveForm();
            form.CreateControl();

            var rawButtons = Descendants(form).OfType<Button>().Where(b => b is not RButton).ToList();
            Assert.True(rawButtons.Count == 0,
                "FanCurveForm 不得再有原生 Button（保存键应为 RButton）：\n  " + string.Join("\n  ", rawButtons));

            var cpu = GetField<Control>(form, "_cpuPanel")!;
            var gpu = GetField<Control>(form, "_gpuPanel")!;
            var charts = cpu.Parent!;
            Assert.Same(gpu.Parent, charts);
            var cpuPos = ((TableLayoutPanel)charts).GetPositionFromControl(cpu);
            var gpuPos = ((TableLayoutPanel)charts).GetPositionFromControl(gpu);
            // 纵排（G-Helper tableFanCharts：CPU 上、GPU 下）。横排把窗口撑到 728，盖过 420 主窗。
            Assert.Equal(0, cpuPos.Row);
            Assert.Equal(1, gpuPos.Row);
            Assert.Equal(cpuPos.Column, gpuPos.Column);
            int logicalW = Logical(form, form.ClientSize.Width);
            Assert.True(logicalW <= SettingsForm.CompactDashboardLogicalClientSize.Width,
                $"FanCurveForm 宽 {logicalW} 逻辑 px 超出主窗。");

            // 状态/常驻提示拆分：拖动提示是独立常驻 Label，_status 初始为空（不再被提示占用）。
            var status = GetField<Label>(form, "_status")!;
            var hint = Descendants(form).OfType<Label>()
                .First(l => l.Text == "拖动曲线点上下调整占空比");
            Assert.NotSame(status, hint);
            Assert.Equal(string.Empty, status.Text);

            // AutoSize 内容根不得 Dock=Fill：Fill 在句柄/ClientSize 之前会把空带撑进首帧。
            var autoSizeFill = Descendants(form).OfType<TableLayoutPanel>()
                .Where(table => table.AutoSize && table.Dock == DockStyle.Fill)
                .Select(table => table.Name)
                .ToList();
            Assert.True(autoSizeFill.Count == 0,
                "FanCurveForm AutoSize TableLayoutPanel 不得 Dock=Fill：\n  " + string.Join("\n  ", autoSizeFill));
        }
        finally
        {
            Program.hw = previousHardware;
        }
    }

    // ---------- UpdateForm ----------

    [Fact]
    public void UpdateForm_EmptyNotesState_BusyCoversFeedback_TwoTierWidths()
    {
        using var _ = UseAuditMode();
        using var form = new MechrevoLite.Update.UpdateForm(new MechrevoLite.Update.UpdateInfo(
            CurrentVersion: "0.0.0-test", LatestVersion: "9.9.9-test", Channel: "test", ChannelFallback: false,
            UpdateAvailable: false, ReleaseDate: null, Notes: null, FileName: null, Size: null,
            Sha256: null, DownloadUrl: null, DownloadPage: null));
        // Show 而非 CreateControl：Control.Visible 是「含父链」的有效可见性，隐藏窗体上
        // 恒为 false；此 ctor 不订阅 Shown，Show 不会触网刷新。
        form.Show();
        Application.DoEvents();

        // 空态：无更新说明时不留空只读框，显示 Muted 文案，说明行收为 AutoSize，窗口收口。
        var notes = GetField<RTextBox>(form, "_notes")!;
        Assert.NotNull(notes);   // _notes 必须是 RTextBox（不再是原生 TextBox）
        var notesEmpty = GetField<Label>(form, "_notesEmpty")!;
        Assert.False(notes.Visible);
        Assert.True(notesEmpty.Visible);
        Assert.Equal("暂无更新说明", notesEmpty.Text);
        var root = GetField<TableLayoutPanel>(form, "_root")!;
        Assert.Equal(SizeType.AutoSize, root.RowStyles[2].SizeType);
        int logicalW = Logical(form, form.ClientSize.Width);
        int logicalH = Logical(form, form.ClientSize.Height);
        int parentW = SettingsForm.CompactDashboardLogicalClientSize.Width;
        Assert.True(logicalW <= parentW, $"UpdateForm 宽 {logicalW} 逻辑 px 超出主窗 {parentW}。");
        Assert.True(logicalH < 380, $"空态高度 {logicalH} 逻辑 px 未收口（设计高 380）。");

        // 按钮宽度两档：96/96/88/88（逻辑 px）。
        var install = GetField<RButton>(form, "_install")!;
        var downloadPage = GetField<RButton>(form, "_downloadPage")!;
        var later = GetField<RButton>(form, "_later")!;
        var feedback = GetField<RButton>(form, "_feedback")!;
        Assert.Equal(96, Logical(install, install.Width));
        Assert.Equal(96, Logical(downloadPage, downloadPage.Width));
        Assert.Equal(88, Logical(later, later.Width));
        Assert.Equal(88, Logical(feedback, feedback.Width));

        // 下载互斥覆盖全部 4 键（此前漏了反馈）。
        typeof(MechrevoLite.Update.UpdateForm)
            .GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, new object[] { true });
        Assert.False(install.Enabled);
        Assert.False(downloadPage.Enabled);
        Assert.False(later.Enabled);
        Assert.False(feedback.Enabled, "SetBusy(true) 必须同时禁用反馈按钮。");
    }

    // ---------- RColorPicker ----------

    [Fact]
    public void RColorPicker_RandomFromPalette_TokenBorder_MonoReadout()
    {
        using var picker = new RColorPicker(Color.Red, allowRandom: true);
        // Show 后 PerformClick 才会真正触发（隐藏窗体上 CanSelect=false 会吞掉点击）。
        picker.Show();
        Application.DoEvents();

        var defaults = (Color[])typeof(RColorPicker)
            .GetField("Defaults", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var random = Descendants(picker).OfType<RButton>().First(b => b.Text == Properties.Strings.AuraRandomColor);
        // 多次点击：每次都应落在默认色板内（旧实现恒为黑色，必然失败）。
        for (int i = 0; i < 10; i++)
        {
            random.PerformClick();
            Assert.Contains(picker.Color, defaults);
        }

        var preview = GetField<Panel>(picker, "preview")!;
        Assert.Equal("Swatch", preview.GetType().Name);          // Swatch 同款 1px Border 自绘描边
        Assert.Equal(BorderStyle.None, preview.BorderStyle);     // 不再是原生 FixedSingle

        var rgbLabel = GetField<Label>(picker, "rgbLabel")!;
        Assert.Equal("Consolas", rgbLabel.Font.Name);            // 数据读数等宽
        Assert.Equal(UiVisualStyle.Muted.ToArgb(), rgbLabel.ForeColor.ToArgb());
    }

    // ---------- DonateForm ----------

    [Fact]
    public void DonateForm_HeightCappedAt500()
    {
        using var _ = UseAuditMode();
        using var form = new DonateForm();
        form.CreateControl();
        int logicalW = Logical(form, form.ClientSize.Width);
        int logicalH = Logical(form, form.ClientSize.Height);
        int parentW = SettingsForm.CompactDashboardLogicalClientSize.Width;
        Assert.True(logicalW <= parentW, $"DonateForm 宽 {logicalW} 逻辑 px 超出主窗 {parentW}。");
        Assert.True(logicalH <= 500, $"DonateForm 高度 {logicalH} 逻辑 px 超过 500。");
        int logicalMinW = Logical(form, form.MinimumSize.Width);
        Assert.True(logicalMinW <= parentW, $"DonateForm 最小宽 {logicalMinW} 把窗口撑过主窗。");

        var root = Assert.IsType<TableLayoutPanel>(form.Controls[0]);
        Assert.True(root.AutoSize, "内容根必须 AutoSize，否则 Dock=Fill 子项在首帧撑出空带。");
        Assert.DoesNotContain(root.RowStyles.Cast<RowStyle>(),
            style => style.SizeType == SizeType.Percent && style.Height > 0);
        AssertAutoSizeChildrenDockTop(root);
        Assert.Equal(root.Height, form.ClientSize.Height);
    }

    static void AssertAutoSizeChildrenDockTop(Control container)
    {
        foreach (Control child in container.Controls)
        {
            if (container.AutoSize)
                Assert.Equal(DockStyle.Top, child.Dock);
            AssertAutoSizeChildrenDockTop(child);
        }
    }

    // ---------- FirstRunGuideForm ----------

    [Fact]
    public void FirstRunGuideForm_NoFixedDescriptionWidth_EqualBottomColumns()
    {
        string source = File.ReadAllText(RepoRootPath("src", "MechrevoLiteWin", "FirstRunGuideForm.cs"));
        Assert.DoesNotContain("MaximumSize = new Size(485", source);   // 固定像素宽已删（自适应换行）
        Assert.Equal(2, CountOccurrences(source, "new ColumnStyle(SizeType.Percent, 33)"));
        Assert.Equal(1, CountOccurrences(source, "new ColumnStyle(SizeType.Percent, 34)"));
    }

    static int CountOccurrences(string text, string needle)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    // ---------- RgbForm ----------

    [Fact]
    public void RgbForm_SwatchUsesRColorButton()
    {
        string source = File.ReadAllText(RepoRootPath("src", "MechrevoLiteWin", "RgbForm.cs"));
        Assert.DoesNotContain("new Button { Size = new Size(D(46)", source);

        using var _ = UseAuditMode();
        using var form = new RgbForm(new MechrevoLite.Hardware.KeyboardRgb());
        form.CreateControl();
        var rawButtons = Descendants(form).OfType<Button>().Where(b => b is not RButton).ToList();
        Assert.True(rawButtons.Count == 0,
            "RgbForm 不得再有原生 Button（色块应为 RColorButton）：\n  " + string.Join("\n  ", rawButtons));
        // 默认模式下可能没有色块行（色块随 Static/Rain/Matrix 等模式出现）；
        // 只要出现 Tag=color-swatch 的控件，就必须是 RColorButton。
        var swatchTagged = Descendants(form).OfType<Control>().Where(b => Equals(b.Tag, "color-swatch")).ToList();
        Assert.All(swatchTagged, b => Assert.IsType<RColorButton>(b));
    }

    static string RepoRootPath(params string[] tail)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return System.IO.Path.Combine(directory!.FullName, System.IO.Path.Combine(tail));
    }
}
