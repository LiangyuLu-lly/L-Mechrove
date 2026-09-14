using System.Drawing;
using System.Windows.Forms;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// 主题切换幂等性护栏：日/夜来回切换后，整棵控件树里任何受管控件
/// （标签/按钮/下拉/数字框/容器/图标位图）的 BackColor/ForeColor 都必须是
/// **当前**色板的 token，不得残留上一主题的铬色。
///
/// 背景（2026-09-13 真机实测）：日→夜切换后「屏幕」行头、「亮度」标签、
/// 「键盘」「灯条」行名仍是日间白底（浅底浅字不可读），footer「设置」键残留
/// 旧主题描边——根因是 ApplyTree 的 Label 分支只刷 ForeColor 不刷 BackColor，
/// 构造期烤进去的日间 Window 底在切换后原样保留。
/// </summary>
public class ThemeSwitchIdempotencyTests
{
    const int SwitchCycles = 3;

    [Fact]
    public void ThemeSwitch_LeavesNoStaleControlBackgroundColors()
    {
        SettingsForm? form = null;
        try
        {
            // 夜间构建 → 切日间：全树不得残留任何夜间铬色
            UiVisualStyle.SetAuditNightMode(true);
            form = CreateAuditForm();
            UiVisualStyle.SetAuditNightMode(false);
            form.ApplyThemeMode(false);
            Application.DoEvents();

            List<string> violations = CollectStaleControls(form, night: false);
            Assert.True(violations.Count == 0,
                "日间模式下以下控件仍残留夜间铬色（ApplyThemeMode 没有刷到它们）：\n  "
                + string.Join("\n  ", violations));
        }
        finally
        {
            form?.Dispose();
            UiVisualStyle.SetAuditNightMode(null);
        }
    }

    [Fact]
    public void ThemeSwitch_IsIdempotentInBothDirections()
    {
        SettingsForm? form = null;
        try
        {
            UiVisualStyle.SetAuditNightMode(true);
            form = CreateAuditForm();

            var violations = new List<string>();
            bool night = true;
            for (int i = 0; i < SwitchCycles; i++)
            {
                night = !night;   // 夜构建 → 先切日 → 再切夜 → …
                UiVisualStyle.SetAuditNightMode(night);
                form.ApplyThemeMode(night);
                Application.DoEvents();

                List<string> round = CollectStaleControls(form, night);
                if (round.Count > 0)
                    violations.Add($"—— 第 {i + 1} 轮切换后（{(night ? "夜间" : "日间")}）——\n  "
                        + string.Join("\n  ", round));
            }

            Assert.True(violations.Count == 0,
                $"主题切换 {SwitchCycles} 个来回后仍有控件颜色不是当前色板 token：\n"
                + string.Join("\n", violations));
        }
        finally
        {
            form?.Dispose();
            UiVisualStyle.SetAuditNightMode(null);
        }
    }

    /// <summary>
    /// 折叠组组头图标专项：液冷/灯光/更多开关三个组头图标此前不挂 Tag——既不参与
    /// ApplyTree 的位体重渲染，也没人重算 BackColor，切到夜间后位图停留在日间 Muted、
    /// 底色停留在日间 Window，真机上就是三个浅色方块（2026-09-13 实测）。
    /// 按控件名直接锁定这三个图标：必须存在、已注册主题重刷、底色融入卡片、
    /// 位图主色等于当前色板 Muted。
    /// </summary>
    [Fact]
    public void CollapseGroupHeaderIcons_FollowThemeAcrossThreeCycles()
    {
        SettingsForm? form = null;
        try
        {
            UiVisualStyle.SetAuditNightMode(true);
            form = CreateAuditForm();

            var violations = new List<string>();
            bool night = true;
            for (int i = 0; i < SwitchCycles; i++)
            {
                night = !night;
                UiVisualStyle.SetAuditNightMode(night);
                form.ApplyThemeMode(night);
                Application.DoEvents();

                List<string> round = CollectCollapseIconViolations(form, night);
                if (round.Count > 0)
                    violations.Add($"—— 第 {i + 1} 轮切换后（{(night ? "夜间" : "日间")}）——\n  "
                        + string.Join("\n  ", round));
            }

            Assert.True(violations.Count == 0,
                $"折叠组组头图标未跟随主题（{SwitchCycles} 个来回）：\n" + string.Join("\n", violations));
        }
        finally
        {
            form?.Dispose();
            UiVisualStyle.SetAuditNightMode(null);
        }
    }

    /// <summary>三个折叠组组头图标（collapseGroup_*_icon）：存在性 + 主题注册 + 融底 + 位图色。</summary>
    static List<string> CollectCollapseIconViolations(Control root, bool night)
    {
        string mode = night ? "夜间" : "日间";
        var violations = new List<string>();
        Color[] backs = ChromeBacks();
        var icons = AllDescendants(root)
            .OfType<PictureBox>()
            .Where(picture => picture.Name.StartsWith("collapseGroup_", StringComparison.Ordinal)
                && picture.Name.EndsWith("_icon", StringComparison.Ordinal))
            .ToList();

        if (icons.Count != 3)
        {
            violations.Add($"找到 {icons.Count} 个 collapseGroup_*_icon（期望 3 个：液冷/灯光/更多开关）");
            return violations;
        }

        foreach (PictureBox icon in icons)
        {
            if (icon.Tag is not UiGlyph.Kind)
                violations.Add($"{Describe(icon)}: 未注册主题重刷（Tag={icon.Tag ?? "<null>"}，应为 UiGlyph.Kind）");
            if (icon.Parent is not null)
            {
                if (!backs.Any(allowed => SameRgb(icon.Parent.BackColor, allowed)))
                    violations.Add($"{Describe(icon)}: 父容器底色 {Rgb(icon.Parent.BackColor)} 不是{mode}色板铬色");
                if (!SameRgb(icon.BackColor, icon.Parent.BackColor))
                    violations.Add($"{Describe(icon)}: BackColor={Rgb(icon.BackColor)} 未融入父容器 "
                        + $"{Describe(icon.Parent)} 的 {Rgb(icon.Parent.BackColor)}");
            }

            if (icon.Image is not Bitmap bitmap)
            {
                violations.Add($"{Describe(icon)}: Image 为空（组头图标位图未渲染）");
                continue;
            }

            Color? rendered = MaxAlphaColor(bitmap);
            Color expected = UiVisualStyle.Muted;
            if (rendered is null)
                violations.Add($"{Describe(icon)}: 位图无像素（{bitmap.Width}x{bitmap.Height}）");
            // GDI+ 抗锯齿的边缘像素会把通道舍入 ±1（实测 Chevrons 出现 #8FA3BE），
            // 容差 3 仍远小于昼夜 Muted 的色距（|#8FA3BF-#5A6B80|≈53）。
            else if (!Near(rendered.Value, expected, 3))
                violations.Add($"{Describe(icon)}: 位图主色={Rgb(rendered.Value)} 不是{mode}色板 Muted={Rgb(expected)}（位图停留在旧主题）");
        }

        return violations;
    }

    /// <summary>位图里 alpha 最高像素的 RGB：线性图标的核心笔画像素即渲染时用的 token 原色。</summary>
    static Color? MaxAlphaColor(Bitmap bitmap)
    {
        Color? best = null;
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++)
        {
            Color pixel = bitmap.GetPixel(x, y);
            if (best is null || pixel.A > best.Value.A) best = pixel;
        }
        return best is { A: > 0 } ? best : null;
    }

    /// <summary>整个测试周期（含 ApplyThemeMode）都保持审计门：主题切换走
    /// SetAuditNightMode 内存覆盖，绝不写 AppConfig（真实配置隔离）。</summary>
    static SettingsForm CreateAuditForm()
    {
        bool previousAuditMode = Program.UiAuditMode;
        Program.UiAuditMode = true;
        try
        {
            return new SettingsForm();
        }
        finally
        {
            Program.UiAuditMode = previousAuditMode;
        }
    }

    /// <summary>当前色板的容器铬色 token（BackColor 合法集）。</summary>
    static Color[] ChromeBacks() =>
    [
        UiVisualStyle.Window, UiVisualStyle.Surface, UiVisualStyle.SurfaceRaised, UiVisualStyle.Input, UiVisualStyle.Track,
    ];

    /// <summary>当前色板的前景 token（ForeColor 合法集；语义状态色允许原样保留）。</summary>
    static Color[] Foregrounds() =>
    [
        UiVisualStyle.Text, UiVisualStyle.Muted, UiVisualStyle.Accent, UiVisualStyle.AccentText,
        UiVisualStyle.AccentBlue, UiVisualStyle.Danger, UiVisualStyle.Ok, UiVisualStyle.Warn,
    ];

    static List<string> CollectStaleControls(Control root, bool night)
    {
        string mode = night ? "夜间" : "日间";
        var violations = new List<string>();
        Color[] backs = ChromeBacks();
        Color[] foreTokens = Foregrounds();

        void Check(Control control, Color back, bool checkFore, Color fore)
        {
            if (!backs.Any(allowed => SameRgb(back, allowed)))
                violations.Add($"{Describe(control)}: BackColor={Rgb(back)} 不属于{mode}色板铬色 "
                    + $"[期望 {string.Join('/', backs.Select(Rgb))}]");
            if (checkFore && !foreTokens.Any(allowed => SameRgb(fore, allowed)))
                violations.Add($"{Describe(control)}: ForeColor={Rgb(fore)} 不属于{mode}色板前景 token");
        }

        foreach (Control control in AllDescendants(root))
        {
            switch (control)
            {
                case RColorButton:
                    break;   // 设备色色板，不受主题管控
                case Label label:
                    Check(label, label.BackColor, checkFore: true, fore: label.ForeColor);
                    break;
                case PictureBox picture when picture.Tag is UiGlyph.Kind:
                    Check(picture, picture.BackColor, checkFore: false, fore: default);
                    // 图标底必须与所在卡片/行同色（贴在父容器底上才不显块）——之前的残留
                    // 就是构造期烤的 Window 底从不重算，切换后成另一主题的浅色方块。
                    if (picture.Parent is not null && !SameRgb(picture.BackColor, picture.Parent.BackColor))
                        violations.Add($"{Describe(picture)}: BackColor={Rgb(picture.BackColor)} 与父容器 "
                            + $"{Describe(picture.Parent)} 的 {Rgb(picture.Parent.BackColor)} 不一致（图标块不融底）");
                    break;
                case CheckBox or RadioButton:
                    break;   // 自绘开关（RCheckBox）等：底色/描边自管，FlatAppearance 不参与绘制
                case ButtonBase button:
                    // 选中态主题键/主按钮允许 Accent 底；其余一律铬色
                    Check(button, button.BackColor, checkFore: true, fore: button.ForeColor);
                    // 只有系统绘制的普通 Button 消费 FlatAppearance 描边；RButton/RCheckBox 自画
                    if (button is Button plain && plain.FlatAppearance.BorderSize > 0
                        && !backs.Any(allowed => SameRgb(plain.FlatAppearance.BorderColor, allowed))
                        && !SameRgb(plain.FlatAppearance.BorderColor, UiVisualStyle.Border)
                        && !SameRgb(plain.FlatAppearance.BorderColor, UiVisualStyle.Accent))
                        violations.Add($"{Describe(plain)}: FlatAppearance.BorderColor={Rgb(plain.FlatAppearance.BorderColor)} "
                            + $"不是{mode}色板 token（残留旧主题描边）");
                    break;
                case ComboBox combo:
                    Check(combo, combo.BackColor, checkFore: true, fore: combo.ForeColor);
                    break;
                case TextBoxBase textBox:
                    Check(textBox, textBox.BackColor, checkFore: true, fore: textBox.ForeColor);
                    break;
                case NumericUpDown numeric:
                    Check(numeric, numeric.BackColor, checkFore: true, fore: numeric.ForeColor);
                    break;
                case Panel or TableLayoutPanel or FlowLayoutPanel:
                    Check(control, control.BackColor, checkFore: false, fore: default);
                    break;
                case TrackBar track:
                    Check(track, track.BackColor, checkFore: false, fore: default);
                    break;
            }
        }

        return violations;
    }

    static IEnumerable<Control> AllDescendants(Control root)
    {
        foreach (Control control in root.Controls)
        {
            yield return control;
            foreach (Control descendant in AllDescendants(control))
                yield return descendant;
        }
    }

    static string Describe(Control control)
    {
        if (!string.IsNullOrEmpty(control.Name)) return $"{control.GetType().Name} \"{control.Name}\"";
        string text = control.Text is { Length: > 0 } t ? $"\"{t}\" " : "";
        string parent = control.Parent is null ? "" : $@" in \{control.Parent.Name}";
        return $"{control.GetType().Name} {text}{parent}";
    }

    static string Rgb(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    static bool SameRgb(Color first, Color second) =>
        first.R == second.R && first.G == second.G && first.B == second.B;

    static bool Near(Color first, Color second, int tolerance) =>
        Math.Abs(first.R - second.R) <= tolerance
        && Math.Abs(first.G - second.G) <= tolerance
        && Math.Abs(first.B - second.B) <= tolerance;
}
