namespace MechrevoLite.Tests;

using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.Hardware;
using MechrevoLite.Mode;
using MechrevoLite.UI;

public class CustomModeFormTests
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

    static IDisposable UseAuditMode()
    {
        bool previous = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null;
        return new Scoped(previous, previousHardware);
    }

    sealed class Scoped(bool previousAudit, MechrevoHw? previousHardware) : IDisposable
    {
        public void Dispose()
        {
            Program.UiAuditMode = previousAudit;
            Program.hw = previousHardware;
        }
    }

    [Fact]
    public void RNumericUpDown_HideNativeSpinButtons_RemovesTheNativeArrowChild()
    {
        using var numeric = new RNumericUpDown();
        numeric.CreateControl();

        Control spinner = numeric.Controls[0];
        Assert.Equal("UpDownButtons", spinner.GetType().Name);
        Assert.True(spinner.Visible, "前置：原生微调按钮子窗口默认可见。");

        numeric.HideNativeSpinButtons();
        Assert.False(spinner.Visible, "原生上下箭头子窗口必须被隐藏。");
    }

    [Fact]
    public void CustomModeForm_NumericsHideNativeSpinners_AndRowsUseFixedRhythm()
    {
        using var _ = UseAuditMode();
        using var form = new CustomModeForm();
        form.CreateControl();

        // (a) 每个数字框都不得再露出系统绘制的上下箭头子窗口。
        var numerics = Descendants(form).OfType<RNumericUpDown>().ToList();
        Assert.True(numerics.Count > 0, "表单里必须存在 RNumericUpDown 参数行。");
        foreach (RNumericUpDown numeric in numerics)
        {
            Assert.True(numeric.Controls.Count > 0
                        && numeric.Controls[0].GetType().Name == "UpDownButtons",
                "前置：NumericUpDown 的第一个子控件应是系统 UpDownButtons。");
            Assert.True(numeric.NativeSpinButtonsHidden,
                "RNumericUpDown 必须调用 HideNativeSpinButtons 隐藏原生箭头。");
        }

        // (b) 参数行 = 固定 D(32) 行高 + Padding.Empty 边距（Settings.V2 MakeRow 同款节奏）。
        var tables = form.Controls.Find("paramTable", true);
        Assert.True(tables.Length == 1, $"必须恰好有一个 paramTable（实际 {tables.Length}）。");
        var rows = tables[0].Controls.OfType<TableLayoutPanel>().ToList();
        Assert.True(rows.Count > 0, "paramTable 的直接子控件必须是行面板。");
        // 2026-09-13, compact pass: 行高常量从 D(32) 收紧为 RowLogicalHeight(26)——仍容纳字体派生高的
        // 下拉（96dpi 23px）+ 呼吸；下拉行按内容取高（grow-only 跟随 combo.Height）。
        int expectedHeight = ResponsiveLayout.LogicalToDevice(form, CustomModeForm.RowLogicalHeight);
        foreach (Control row in rows)
        {
            Assert.Equal(Padding.Empty, row.Margin);
            // 2026-09-13, follow-up: 下拉行按内容取高——ComboBox 高度由字体派生（SetBoundsCore 强制，
            // 审计缩放视口里行缩到 32 而下拉仍 37），行高只增不减地跟随 combo.Height（基线 D(32)）；
            // 纯滑条/开关行仍固定 D(32)。
            var combos = row.Controls.OfType<RComboBox>().ToList();
            int expected = combos.Count > 0
                ? Math.Max(expectedHeight, combos.Max(c => c.Height))
                : expectedHeight;
            Assert.True(row.Height == expected,
                $"参数行高必须是 D(32)={expectedHeight}（下拉行 max(D(32), combo 高)={expected}），实际 {row.Height}。");
        }
    }
    [Fact]
    public void PowerPlanGuid_IsReadFromSelectedItem()
    {
        var selected = new KeyValuePair<string, string>(
            "高性能",
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            CustomModeForm.GetPowerPlanGuid(selected));
    }

    [Fact]
    public void PowerPlanGuid_RejectsInvalidSelection()
    {
        Assert.Null(CustomModeForm.GetPowerPlanGuid("高性能"));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void FanCurveOpen_EnablesIndependentControlWhenSupportedAndNotAlreadyActive(
        bool supported, bool alreadyEnabled, bool expectedRequest)
    {
        Assert.Equal(expectedRequest,
            FanCurveForm.ShouldRequestIndependentControlOnOpen(supported, alreadyEnabled));
    }

    [Fact]
    public void SetActivePlan_RejectsAnInvalidGuidWithoutReportingSuccess()
    {
        Assert.False(WinPowerPlan.SetActivePlan("not-a-power-plan"));
    }

    [Theory]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e", "381B4222-F694-41F0-9685-FF5BB260DF2E", true)]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", false)]
    [InlineData("not-a-power-plan", "381b4222-f694-41f0-9685-ff5bb260df2e", false)]
    public void PowerPlanConfirmation_RequiresAValidMatchingGuid(string requested, string actual, bool expected)
    {
        Assert.Equal(expected, WinPowerPlan.IsPlanConfirmed(requested, actual));
    }

    [Theory]
    [InlineData(2, 2, 2, true)]
    [InlineData(2, 2, 1, false)]
    [InlineData(2, 1, 2, false)]
    [InlineData(2, 7, 7, false)]
    public void BoostConfirmation_RequiresMatchingAcAndDcValues(int requested, int ac, int dc, bool expected)
    {
        Assert.Equal(expected, WinPowerPlan.IsBoostConfirmed(requested, ac, dc));
    }

    [Fact]
    public void FanCurveForm_IsContentDerivedAndCurveStillEditable()
    {
        // 2026-09-14, user request: 风扇曲线窗口过大（原按屏幕工作区猜 1100×920 逻辑 px）——
        // 改为内容实测：标题行 + 2×曲线最小高 + 底行；16 点曲线编辑能力不变。
        bool previousAuditMode = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null;
        try
        {
            using var form = new FanCurveForm();
            form.CreateControl();

            int logicalW = form.ClientSize.Width * 96 / Math.Max(1, form.DeviceDpi);
            int logicalH = form.ClientSize.Height * 96 / Math.Max(1, form.DeviceDpi);
            // 横排双图（2×340+48=728）比 420 主窗宽 1.73 倍。改回 G-Helper 的纵排后，
            // 宽不得超过主窗，高不得超过主窗，曲线本身仍要能放下 16 个点。
            int parentW = SettingsForm.CompactDashboardLogicalClientSize.Width;
            int parentH = SettingsForm.CompactDashboardLogicalClientSize.Height;
            Assert.True(logicalW <= parentW, $"风扇曲线窗口宽 {logicalW} 逻辑 px 超出主窗 {parentW}。");
            Assert.True(logicalH <= parentH, $"风扇曲线窗口高 {logicalH} 逻辑 px 超出主窗 {parentH}。");
            Assert.True(logicalH > 400, $"纵排双图高度 {logicalH} 仍是横排单行的收口，曲线会被挤扁。");

            var cpu = typeof(FanCurveForm).GetField("_cpuPanel", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(form)!;
            int points = ((int[])cpu.GetType().GetProperty("Duties")!.GetValue(cpu)!).Length;
            Assert.Equal(16, points);
        }
        finally
        {
            Program.hw = previousHardware;
            Program.UiAuditMode = previousAuditMode;
        }
    }

    [Fact]
    public void BoostCombo_NeverStartsWithAnEmptySelection()
    {
        bool previousAuditMode = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null;
        try
        {
            using var form = new CustomModeForm();
            form.CreateControl();

            var boostCombo = (ComboBox)typeof(CustomModeForm)
                .GetField("_boostCombo", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(form)!;
            Assert.True(boostCombo.SelectedIndex >= 0,
                $"睿频模式加载后不得留空（SelectedIndex={boostCombo.SelectedIndex}）。");
        }
        finally
        {
            Program.hw = previousHardware;
            Program.UiAuditMode = previousAuditMode;
        }
    }

    [Fact]
    public void SwitchPendingText_IsNeutralAndNeverUsesFailureWording()
    {
        // 2026-09-13, user request: 成功切换不得先闪现「切换未确认」——pending 文案必须是中性进行时。
        Assert.NotEqual(CustomModeForm.SwitchUnconfirmedText, CustomModeForm.SwitchPendingText);
        Assert.DoesNotContain("未确认", CustomModeForm.SwitchPendingText);
    }

    [Fact]
    public void BoostCombo_FirstEntryMeansUnchanged()
    {
        // 按模式保存：第 0 项「不改变」= 沿用官方行为，不再在打开时把当前系统值落成用户设置。
        bool previousAuditMode = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = null;
        try
        {
            using var form = new CustomModeForm();
            form.CreateControl();
            var boostCombo = (ComboBox)typeof(CustomModeForm)
                .GetField("_boostCombo", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(form)!;
            Assert.Equal(Properties.Strings.ModeTuneUnchanged, boostCombo.Items[0]);
            Assert.Equal(WinPowerPlan.BoostModes.Length + 1, boostCombo.Items.Count);
        }
        finally
        {
            Program.hw = previousHardware;
            Program.UiAuditMode = previousAuditMode;
        }
    }

    [Fact]
    public void PowerPlanGuid_UnchangedEntryIsNull()
    {
        Assert.Null(CustomModeForm.GetPowerPlanGuid(new KeyValuePair<string, string>(Properties.Strings.ModeTuneUnchanged, "")));
    }
}
