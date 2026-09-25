using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// 二级窗必须落在 420 主窗宽度内，风扇曲线只走贴边定位，模式名走 resx 且主窗与托盘一致。
/// </summary>
public class DialogFootprintTests
{
    static int Logical(Control control, int device) => device * 96 / Math.Max(1, control.DeviceDpi);

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

    [Fact]
    public void SettingsDialog_PlaceholderFitsParentWidth()
    {
        using var dialog = new SettingsDialog(new Panel { Height = 40 }, null, displayGroupAvailable: false);
        int logicalW = Logical(dialog, dialog.ClientSize.Width);
        int parentW = SettingsForm.CompactDashboardLogicalClientSize.Width;
        Assert.True(logicalW <= parentW, $"设置弹窗占位宽 {logicalW} 逻辑 px 超出主窗 {parentW}。");
    }

    [Fact]
    public void FirstRunGuideForm_FitsParentWidth()
    {
        using var _ = UseAuditMode();
        using var form = new FirstRunGuideForm();
        form.CreateControl();

        int parentW = SettingsForm.CompactDashboardLogicalClientSize.Width;
        int logicalW = Logical(form, form.ClientSize.Width);
        int logicalMinW = Logical(form, form.MinimumSize.Width);
        Assert.True(logicalW <= parentW, $"首次引导宽 {logicalW} 逻辑 px 超出主窗 {parentW}。");
        Assert.True(logicalMinW <= parentW, $"首次引导最小宽 {logicalMinW} 把窗口撑过主窗。");
        Assert.Contains(Descendants(form), c => c is ScrollableControl { AutoScroll: true });
    }

    [Fact]
    public void FanCurveForm_PlacesBesideOwner_AndDoesNotCenterOnScreen()
    {
        using var _ = UseAuditMode();
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        Program.hw = null;
        try
        {
            using var owner = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(640, 320),
                ClientSize = SettingsForm.CompactDashboardLogicalClientSize,
            };
            owner.Show();
            Application.DoEvents();

            using var form = new FanCurveForm();
            Assert.Equal(FormStartPosition.Manual, form.StartPosition);

            ResponsiveLayout.ShowAdjacentTo(form, owner);
            Application.DoEvents();

            Assert.False(form.Bounds.IntersectsWith(owner.Bounds),
                $"风扇曲线盖住了主窗（form={form.Bounds}, owner={owner.Bounds}）。");
            Assert.True(form.Left < owner.Left || form.Left >= owner.Right,
                $"风扇曲线必须贴在主窗旁边（form={form.Location}, owner={owner.Location}）。");
        }
        finally
        {
            Program.hw = previousHardware;
        }
    }

    [Theory]
    [InlineData("zh-CN", "静音模式", "平衡模式", "狂暴", "静音狂暴")]
    [InlineData("en", "Silent", "Balanced", "Turbo", "Silent Turbo")]
    public void ModeNames_MatchBetweenDashboardAndTray(string culture, string silent, string balanced, string turbo, string silentTurbo)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        bool previousAudit = Program.UiAuditMode;
        MechrevoLite.Hardware.MechrevoHw? previousHardware = Program.hw;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(silent, Properties.Strings.Silent);
            Assert.Equal(balanced, Properties.Strings.Balanced);
            Assert.Equal(turbo, Properties.Strings.Turbo);
            Assert.Equal(silentTurbo, Properties.Strings.SilentTurbo);

            Program.UiAuditMode = true;
            Program.hw = null;
            using var form = new SettingsForm();
            form.CreateControl();

            Assert.Equal(Properties.Strings.Silent, FindButton(form, Properties.Strings.Silent)?.Text);
            Assert.Equal(Properties.Strings.Balanced, FindButton(form, Properties.Strings.Balanced)?.Text);
            Assert.Equal(Properties.Strings.Turbo, FindButton(form, Properties.Strings.Turbo)?.Text);
            Assert.Equal(Properties.Strings.SilentTurbo, FindButton(form, Properties.Strings.SilentTurbo)?.Text);

            form.SetContextMenu();
            object? menuValue = typeof(SettingsForm)
                .GetField("contextMenuStrip", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(form);
            var menu = Assert.IsAssignableFrom<ContextMenuStrip>(menuValue);
            var texts = menu.Items.OfType<ToolStripMenuItem>()
                .Select(item => item.Text)
                .OfType<string>()
                .ToList();
            Assert.Contains(Properties.Strings.Silent, texts);
            Assert.Contains(Properties.Strings.Balanced, texts);
            Assert.Contains(Properties.Strings.Turbo, texts);
            Assert.Contains(Properties.Strings.SilentTurbo, texts);
            Assert.DoesNotContain(texts, text => text.Contains("(办公)", StringComparison.Ordinal)
                || text.Contains("(游戏)", StringComparison.Ordinal)
                || text.Contains("(增强)", StringComparison.Ordinal));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
            Program.hw = previousHardware;
            Program.UiAuditMode = previousAudit;
        }
    }

    static Button? FindButton(Control root, string text)
    {
        foreach (Control child in root.Controls)
        {
            if (child is Button button && button.Text == text) return button;
            Button? nested = FindButton(child, text);
            if (nested is not null) return nested;
        }
        return null;
    }

    static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls)
        {
            yield return control;
            foreach (Control descendant in Descendants(control))
                yield return descendant;
        }
    }
}
