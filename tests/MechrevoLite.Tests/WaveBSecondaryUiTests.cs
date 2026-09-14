using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// Wave B（docs/run4-p2p3-design-spec.md §3.1-3.7 P2 批次）二级/三级界面护栏：
/// CustomModeForm 控件代差、RgbForm 死控件/设备丢失态、LightForm 关灯禁用、
/// RColorPicker Hex 校验态、SettingsDialog 陈旧行/高度收紧。
/// </summary>
public class WaveBSecondaryUiTests
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

    // ---------- 1. CustomModeForm ----------

    [Fact]
    public void CustomModeForm_UsesThemedControls()
    {
        using var _ = UseAuditMode();
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.hw = null;
        try
        {
            using var form = new CustomModeForm();
            form.CreateControl();

            var rawCombos = Descendants(form).OfType<ComboBox>().Where(c => c is not RComboBox).ToList();
            var rawNumerics = Descendants(form).OfType<NumericUpDown>().Where(n => n is not RNumericUpDown).ToList();
            var rawButtons = Descendants(form).OfType<Button>().Where(b => b is not RButton).ToList();

            Assert.True(rawCombos.Count == 0,
                "CustomModeForm 不得再有原生 ComboBox（应使用 RComboBox）：\n  " + string.Join("\n  ", rawCombos));
            Assert.True(rawNumerics.Count == 0,
                "CustomModeForm 不得再有原生 NumericUpDown（应使用 RNumericUpDown）：\n  " + string.Join("\n  ", rawNumerics));
            Assert.True(rawButtons.Count == 0,
                "CustomModeForm 不得再有原生 Button（应使用 RButton）：\n  " + string.Join("\n  ", rawButtons));

            // 档位按钮 = 分段组（ApplySegmentGroup）
            var segments = Descendants(form).OfType<RButton>()
                .Where(b => b.SegmentPosition != RSegmentPosition.None).ToList();
            Assert.True(segments.Count >= 4,
                $"自定义档位按钮必须是分段组（ApplySegmentGroup），实际 {segments.Count} 枚分段按钮。");

            // 紧凑性：目标高度 ≤700 逻辑 px（规格 §3.2）
            int logicalHeight = form.ClientSize.Height * 96 / Math.Max(1, form.DeviceDpi);
            Assert.True(logicalHeight <= 700,
                $"CustomModeForm 初始高度 {logicalHeight} 逻辑 px 超过规格目标 700。");
        }
        finally
        {
            Program.hw = previousHardware;
        }
    }

    // ---------- 2. RgbForm ----------

    [Fact]
    public void RgbForm_HasNoDeadControls()
    {
        string source = File.ReadAllText(RepoRootPath("src", "MechrevoLiteWin", "RgbForm.cs"));
        Assert.DoesNotContain("MakeStaticZonesCombo", source);

        using var _ = UseAuditMode();
        using var form = new RgbForm(new MechrevoLite.Hardware.KeyboardRgb());
        form.CreateControl();

        var rawCombos = Descendants(form).OfType<ComboBox>().Where(c => c is not RComboBox).ToList();
        Assert.True(rawCombos.Count == 0,
            "RgbForm 不得再有原生 ComboBox（应使用 RComboBox）：\n  " + string.Join("\n  ", rawCombos));
    }

    [Fact]
    public void RgbForm_ShowsDeviceLostState()
    {
        using var _ = UseAuditMode();
        using var form = new RgbForm(new MechrevoLite.Hardware.KeyboardRgb());
        form.CreateControl();

        MethodInfo onLost = form.GetType().GetMethod("OnRgbDeviceLost",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(onLost);
        onLost.Invoke(form, Array.Empty<object>());

        var status = GetField<Label>(form, "_lblStatus");
        Assert.NotNull(status);
        Assert.Equal(UiVisualStyle.Danger.ToArgb(), status.ForeColor.ToArgb());

        var hidPanel = GetField<Panel>(form, "_hidPanel");
        // 2026-09-13, user request: 「开启灯效」/「停止灯效」按钮已作为重复控件移除（模式选择即应用；停止由仪表盘键盘行电源开关承担）。
        // 2026-09-13, user request: _comboMode/_comboCloseTimer 已移除——模式改由 _rgb.KbHidMode 单一真相源驱动，
        // 睡眠时间/离电开关移入仪表盘「灯光」组头（Settings.V2.AddHeaderControl）。
        Assert.NotNull(hidPanel);

        Assert.False(hidPanel.Enabled, "设备丢失后 HID 参数面板必须禁用。");
    }

    [Fact]
    public void RgbForm_RemovesIdleCaption_AndHidesEmptyStatusRow()
    {
        // 2026-09-14, user request: 常驻说明「自定义效果（HID 直连）」删除——正常态不占状态行，
        // 状态行只在有事可说（连接中/失败/断连）时出现。
        string source = File.ReadAllText(RepoRootPath("src", "MechrevoLiteWin", "RgbForm.cs"));
        Assert.DoesNotContain("自定义效果（HID 直连）", source);

        using var _ = UseAuditMode();
        using var form = new RgbForm(new MechrevoLite.Hardware.KeyboardRgb());
        form.CreateControl();

        var status = GetField<Label>(form, "_lblStatus");
        Assert.NotNull(status);
        Assert.False(status.Visible, "空状态文本时状态行必须隐藏（不占行高）。");
    }

    [Fact]
    public void RgbForm_IsContentDerivedAndCompact()
    {
        // 2026-09-14, user request: 键盘灯效窗口过大——宽度按内容实测（标签列+控件列），
        // 高度跟随内容表；不再整树 ScaleFrom96。
        using var _ = UseAuditMode();
        using var form = new RgbForm(new MechrevoLite.Hardware.KeyboardRgb());
        form.CreateControl();

        int logicalW = form.ClientSize.Width * 96 / Math.Max(1, form.DeviceDpi);
        int logicalH = form.ClientSize.Height * 96 / Math.Max(1, form.DeviceDpi);
        Assert.True(logicalW <= 400, $"键盘灯效窗口宽 {logicalW} 逻辑 px 超出紧凑上限 400。");
        Assert.True(logicalH <= 320, $"键盘灯效窗口高 {logicalH} 逻辑 px 超出紧凑上限 320。");
    }

    [Fact]
    public void LightGroupPrefsRow_LivesInContent_AndHidesOnCollapse()
    {
        // 用户 2026-09-13 改址：两个全局灯光设置放「灯光」内容区首行（随组折叠收起），不放组头。
        string v2Source = File.ReadAllText(RepoRootPath("src", "MechrevoLiteWin", "Settings.V2.cs"));
        Assert.Contains("rowLightPrefs", v2Source);
        Assert.Contains("lighting_off_on_battery", v2Source);
        Assert.Contains("lighting_idle_seconds", v2Source);
        // 组头 API 已回退：不得再有 AddHeaderControl。
        string groupSource = File.ReadAllText(RepoRootPath("src", "MechrevoLiteWin", "UI", "RCollapseGroup.cs"));
        Assert.DoesNotContain("AddHeaderControl", groupSource);

        // 行为验证：SetContent 进内容区的控件随折叠一起隐藏/恢复。
        using var group = new MechrevoLite.UI.RCollapseGroup("灯光", MechrevoLite.UI.UiGlyph.Kind.Gear, "test_group_prefs", defaultExpanded: true);
        var batteryChk = new RCheckBox { Text = "离电自动关闭全部灯效", AutoSize = true };
        var sleepCombo = new RComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        var row = new TableLayoutPanel { Name = "rowLightPrefs", Dock = DockStyle.Top, AutoSize = true };
        row.Controls.Add(batteryChk);
        row.Controls.Add(sleepCombo);
        group.SetContent(row);
        group.CreateControl();

        Assert.True(batteryChk.Visible, "展开时内容首行必须可见。");
        group.Toggle();
        Assert.False(group.Expanded);
        Assert.False(row.Visible, "折叠后内容区（含新行）必须隐藏。");
        Assert.False(batteryChk.Visible, "折叠后离电开关必须随内容区隐藏。");
        Assert.False(sleepCombo.Visible, "折叠后睡眠时间下拉必须随内容区隐藏。");
        group.Toggle();
        Assert.True(group.Expanded);
        Assert.True(batteryChk.Visible, "重新展开后离电开关必须恢复可见。");
    }

    // ---------- 3. LightForm ----------

    [Fact]
    public void LightForm_DisablesControlsWhenChannelOff()
    {
        using var _ = UseAuditMode();
        // 2026-09-13, user request: 「灯光开关」已作为重复控件移除（电源由仪表盘灯条/Logo 行承担），
        // 参数禁用改由持久化电源状态（LightingSettingsStore）驱动；「模式」下拉同为重复控件移除。
        string directory = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string? previousOverride = Environment.GetEnvironmentVariable(MechrevoLite.Hardware.LightingSettingsStore.ConfigDirectoryOverrideVariable);
        Environment.SetEnvironmentVariable(MechrevoLite.Hardware.LightingSettingsStore.ConfigDirectoryOverrideVariable, directory);
        try
        {
            // 先确保关 → 亮度/速度/颜色 必须禁用
            MechrevoLite.Hardware.LightingSettingsStore.SavePower("HidLightbar/Ctrl", powerOn: false);
            using (var form = new LightForm("HidLightbar/Ctrl", "灯条灯效", LightForm.LightbarEffects))
            {
                form.CreateControl();
                var slider = Descendants(form).OfType<RSlider>().First();
                var comboSpeed = Descendants(form).OfType<RComboBox>().First();
                var colorButton = Descendants(form).OfType<RColorButton>().First();
                Assert.False(slider.Enabled, "关灯后亮度滑条必须禁用。");
                Assert.False(comboSpeed.Enabled, "关灯后速度下拉必须禁用。");
                Assert.False(colorButton.Enabled, "关灯后颜色按钮必须禁用。");
            }

            // 重新开灯：恢复可用
            MechrevoLite.Hardware.LightingSettingsStore.SavePower("HidLightbar/Ctrl", powerOn: true);
            using (var form = new LightForm("HidLightbar/Ctrl", "灯条灯效", LightForm.LightbarEffects))
            {
                form.CreateControl();
                var slider = Descendants(form).OfType<RSlider>().First();
                var comboSpeed = Descendants(form).OfType<RComboBox>().First();
                var colorButton = Descendants(form).OfType<RColorButton>().First();
                Assert.True(slider.Enabled, "开灯时亮度滑条必须可用。");
                Assert.True(comboSpeed.Enabled, "开灯时速度下拉必须可用。");
                Assert.True(colorButton.Enabled, "开灯时颜色按钮必须可用。");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(MechrevoLite.Hardware.LightingSettingsStore.ConfigDirectoryOverrideVariable, previousOverride);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    // ---------- 4. RColorPicker ----------

    [Fact]
    public void RColorPicker_RejectsInvalidHex()
    {
        using var picker = new RColorPicker(Color.FromArgb(255, 10, 20, 30), allowRandom: false);
        picker.CreateControl();

        var hexBox = GetField<RTextBox>(picker, "hexBox");
        Assert.NotNull(hexBox);

        Color normal = UiVisualStyle.Text;

        hexBox.Text = "ZZZZZZ";
        Assert.Equal(UiVisualStyle.Danger.ToArgb(), hexBox.ForeColor.ToArgb());
        Assert.Equal(10, picker.Color.R);   // 非法输入不得改色

        hexBox.Text = "#1A2B3C";
        Assert.Equal(normal.ToArgb(), hexBox.ForeColor.ToArgb());
        Assert.Equal(0x1A, picker.Color.R); // 合法输入生效

        hexBox.Text = "12";   // 位数不足同样是非法
        Assert.Equal(UiVisualStyle.Danger.ToArgb(), hexBox.ForeColor.ToArgb());
    }

    // ---------- 5. SettingsDialog ----------

    [Fact]
    public void SettingsDialog_HasNoStaleRows()
    {
        string source = File.ReadAllText(RepoRootPath("src", "MechrevoLiteWin", "SettingsDialog.cs"));
        foreach (string stale in new[] { "悬浮窗", "局部调光", "局部背光", "自动刷新率" })
            Assert.True(!source.Contains(stale),
                $"SettingsDialog 不得再包含已移除功能的行「{stale}」（该功能已从弹窗移除/移入屏幕行头）。");

        using var _ = UseAuditMode();
        var themePanel = new Panel { Height = 50 };
        var officialPanel = new Panel { Height = 40 };
        var overdrive = new RCheckBox { Text = "响应加速", Height = 30 };
        // 2026-09-14：校色按钮删除（改为屏幕行头内联下拉），弹窗只收 overdrive。
        var dialog = new SettingsDialog(themePanel, officialPanel, overdrive);
        try
        {
            dialog.Show();
            Application.DoEvents();

            // 高度按内容收紧：不得保留 420 的旧固定高度（内容少时底部大片留白）
            int logicalHeight = dialog.ClientSize.Height * 96 / Math.Max(1, dialog.DeviceDpi);
            Assert.True(logicalHeight <= 340,
                $"SettingsDialog 高度 {logicalHeight} 逻辑 px 未按内容收紧（旧固定 420）。");
        }
        finally
        {
            dialog.Dispose();
        }
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
