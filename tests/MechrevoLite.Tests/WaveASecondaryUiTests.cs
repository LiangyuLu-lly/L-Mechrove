using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// Wave A（docs/run4-p2p3-design-spec.md Top 5）二级/三级界面护栏：
/// RColorPicker 中文化 + 主题 token、DonateControl 菜单无死项。
/// ColorCalibrationForm 的护栏已随表单删除（2026-09-14：P3/AdobeRGB 不生效，
/// 弹窗改为屏幕行头内联下拉）。RgbForm/LightForm/FanCurveForm 的
/// 硬编码色已由 UiStyleDisciplineTests.MigratedChromeFilesCarryNoHardcodedColors
/// 覆盖（三个文件都在迁移清单里），这里只补 FanCurveForm 的字体护栏。
/// </summary>
public class RColorPickerTests
{
    [Fact]
    public void RColorPicker_UsesChineseCaptions()
    {
        using var picker = new RColorPicker(Color.Red, allowRandom: false);

        Assert.Equal("颜色", picker.Text);

        var labels = picker.Controls.OfType<Label>().ToList();
        Assert.Contains(labels, l => l.Text == "十六进制");

        var buttons = picker.Controls.OfType<RButton>().ToList();
        Assert.Contains(buttons, b => b.Text == "确定");
        Assert.Contains(buttons, b => b.Text == "取消");
        Assert.DoesNotContain(buttons, b => b.Text is "OK" or "Cancel");
        Assert.DoesNotContain(labels, l => l.Text == "Hex");
    }

    /// <summary>
    /// 选中描边必须随主题：夜间 = UiVisualStyle.Accent，日间 = 同一 token 的日间值。
    /// 过去 Swatch.OnPaint 用遗留静态色 RForm.colorStandard/borderMain，日间不随主题。
    /// 直接渲染一个选中/未选中的色块，采样边框像素与 token 比对。
    /// </summary>
    [Theory]
    [InlineData(true, "夜间")]
    [InlineData(false, "日间")]
    public void RColorPicker_UsesThemeTokens(bool night, string label)
    {
        UiVisualStyle.SetAuditNightMode(night);
        try
        {
            using var picker = new RColorPicker(Color.Red, allowRandom: false);
            picker.CreateControl();

            // 色板是 Swatch : Panel（私有嵌套类）；SV 面板/色相条/预览也是 Panel，
            // 用类型名区分。取第一个色块，分别渲染选中与未选中态。
            Panel swatch = picker.Controls.OfType<Panel>()
                .First(p => p.GetType().Name == "Swatch");

            Color selected = RenderBorderPixel(swatch, selected: true);
            Color unselected = RenderBorderPixel(swatch, selected: false);

            Assert.Equal(UiVisualStyle.Accent.ToArgb(), selected.ToArgb());
            Assert.Equal(UiVisualStyle.Border.ToArgb(), unselected.ToArgb());
        }
        finally
        {
            UiVisualStyle.SetAuditNightMode(null);
        }
    }

    static Color RenderBorderPixel(Panel swatch, bool selected)
    {
        FieldInfo selectedField = swatch.GetType().GetField("Selected")!;
        selectedField.SetValue(swatch, selected);
        swatch.Invalidate();

        using var bitmap = new Bitmap(swatch.Width, swatch.Height);
        swatch.DrawToBitmap(bitmap, new Rectangle(Point.Empty, swatch.Size));
        // 描边厚度 ≥2px（选中）或 1px（未选中），(0,0) 一定落在描边上。
        return bitmap.GetPixel(0, 0);
    }
}

public class DonateControlMenuTests
{
    [Fact]
    public void DonateControlMenu_HasNoDeadItems()
    {
        bool previousAuditMode = Program.UiAuditMode;
        Program.UiAuditMode = true;
        try
        {
            using var form = new SettingsForm();
            var badge = new RBadgeButton();
            form.Controls.Add(badge);
            var donate = new MechrevoLite.Helpers.DonateControl(form, badge);

            // 菜单构建已从 Button_MouseUp 提取为私有 BuildContextMenu()，便于断言
            // （Show 需要真实可见窗口，测试环境无法走完整 MouseUp 路径）。
            MethodInfo build = typeof(MechrevoLite.Helpers.DonateControl)
                .GetMethod("BuildContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.NotNull(build);

            using var menu = (ContextMenuStrip)build.Invoke(donate, Array.Empty<object>())!;

            var items = menu.Items.OfType<ToolStripMenuItem>().ToList();
            Assert.Equal(2, items.Count);
            Assert.Equal("已赞助 ❤", items[0].Text);
            Assert.Equal("不再提醒", items[1].Text);

            // 无死控件规则：每个菜单项都必须有 Click 处理器。
            foreach (ToolStripMenuItem item in items)
                Assert.True(item.HasClickHandler(),
                    $"菜单项「{item.Text}」没有 Click 处理器（死菜单项）。");
        }
        finally
        {
            Program.UiAuditMode = previousAuditMode;
        }
    }
}

public class FanCurveFormStyleTests
{
    /// <summary>
    /// FanCurveForm 自绘护栏：OnPaint 不得出现系统刷（Brushes.*）或硬编码字体
    /// （"Segoe UI" / new Font( 字面量）——日间模式坐标轴/拖拽提示曾因此不可读。
    /// </summary>
    [Fact]
    public void FanCurveForm_UsesThemeBrushes()
    {
        string path = RepoRoot.Path("src", "MechrevoLiteWin", "FanCurveForm.cs");
        string[] lines = File.ReadAllLines(path);
        var violations = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.TrimStart().StartsWith("//")) continue;
            if (line.Contains("Brushes.")) violations.Add($"{i + 1}: {line.Trim()}");
            if (line.Contains("Segoe UI")) violations.Add($"{i + 1}: {line.Trim()}");
            if (line.Contains("new Font(")) violations.Add($"{i + 1}: {line.Trim()}");
        }

        Assert.True(violations.Count == 0,
            "FanCurveForm 自绘必须用 UiVisualStyle token 与 TypeScale 字体：\n  "
            + string.Join("\n  ", violations));
    }
}

/// <summary>ToolStripMenuItem 是否订阅了 Click（死菜单项检查）。</summary>
file static class MenuItemClickExtensions
{
    public static bool HasClickHandler(this ToolStripMenuItem item)
    {
        // .NET Core 的 ToolStripItem 用 s_clickEvent 作 Click 事件键（Framework 时代叫 EventClick）。
        object? key = null;
        for (Type? type = item.GetType(); type is not null && key is null; type = type.BaseType)
            key = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(f => f.Name is "EventClick" or "s_eventClick" or "s_clickEvent")?.GetValue(null);
        var events = item.GetType().GetProperty("Events", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? typeof(ToolStripItem).GetProperty("Events", BindingFlags.Instance | BindingFlags.NonPublic);
        var list = events?.GetValue(item) as EventHandlerList;
        return key is not null && list is not null && list[key] is not null;
    }
}

file static class RepoRoot
{
    public static string Path(params string[] tail)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return System.IO.Path.Combine(directory!.FullName, System.IO.Path.Combine(tail));
    }
}
