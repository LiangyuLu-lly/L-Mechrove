using System.Drawing;
using MechrevoLite.UI;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// 样式纪律的机器化护栏（docs/ui-consistency-pass.md §2 规范 / §3 护栏）。
///
/// 规范的失效方式从来不是"写错一次"，而是"半年后没人记得"：新加一个标签随手写
/// <c>Font(11F)</c>、新加一张卡片随手写 <c>Color.FromArgb(30, 30, 30)</c>，
/// 于是刻度与色板又回到各自为政。这组测试把规范变成可执行约束——违反了就红。
///
/// 白名单是**显式**的：每一条例外都在这里写明理由，新增例外必须改这份文件，
/// 于是例外本身也是一次被看见的决策。
/// </summary>
public class UiStyleDisciplineTests
{
    /// <summary>已迁移到语义 token 的饰面文件：这些文件里不允许再出现硬编码颜色。</summary>
    static readonly string[] MigratedChromeFiles =
    {
        Path.Combine("src", "MechrevoLiteWin", "Settings.cs"),
        Path.Combine("src", "MechrevoLiteWin", "CustomModeForm.cs"),
        // ColorCalibrationForm.cs 已删（2026-09-14：弹窗改为屏幕行头内联下拉）。
        Path.Combine("src", "MechrevoLiteWin", "FanCurveForm.cs"),
        Path.Combine("src", "MechrevoLiteWin", "DonateForm.cs"),
        Path.Combine("src", "MechrevoLiteWin", "Updates", "UpdateForm.cs"),
        Path.Combine("src", "MechrevoLiteWin", "RgbForm.cs"),
        Path.Combine("src", "MechrevoLiteWin", "LightForm.cs"),
    };

    /// <summary>允许保留的字面量颜色，逐条给出理由。</summary>
    static readonly (string File, string Needle, string Why)[] AllowedColorLiterals =
    {
        (Path.Combine("src", "MechrevoLiteWin", "Settings.cs"), "lc_light_color", "液冷灯光的出厂默认色：设备色彩数据，不是饰面"),
        (Path.Combine("src", "MechrevoLiteWin", "Settings.cs"), "but.BackColor", "禁用态调色：对既有颜色做 alpha 混色，无法用固定 token 表达"),
        (Path.Combine("src", "MechrevoLiteWin", "Settings.cs"), "Math.Clamp(hw.LcLedRed", "液冷灯的 RGB 读回值：设备色彩数据，不是饰面"),
        (Path.Combine("src", "MechrevoLiteWin", "Settings.Designer.cs"), "BorderColor = Color.Transparent", "RButton 的「不画描边」语义，不是 BackColor 透明"),
        (Path.Combine("src", "MechrevoLiteWin", "LightForm.cs"), "settings.ColorArgb", "灯色读回值：设备色彩数据，不是饰面"),
        (Path.Combine("src", "MechrevoLiteWin", "LightForm.cs"), "_singleColor = Color.White", "单色灯效的出厂默认色：设备色彩数据，不是饰面"),
    };

    /// <summary>允许保留的字号字面量，逐条给出理由。</summary>
    static readonly (string File, string Needle, string Why)[] AllowedFontLiterals =
    {
        (Path.Combine("src", "MechrevoLiteWin", "Helpers", "ToastForm.cs"), "36f", "Toast 倒计时数字：Pixel 单位的独立视觉层级，不进 TypeScale"),
    };

    [Fact]
    public void NoRawFontSizesOutsideTheTypeScale()
    {
        var violations = new List<string>();
        foreach (string path in SourceFiles())
        {
            string relative = Relative(path);
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(lines[i], @"UiVisualStyle\.Font\(\s*[0-9]")) continue;
                if (IsAllowed(AllowedFontLiterals, relative, lines[i])) continue;
                violations.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(violations.Count == 0,
            "字号必须取自 UiVisualStyle.TypeScale（Caption/Body/Subtitle/Title/Display），不得写字面量：\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void MigratedChromeFilesCarryNoHardcodedColors()
    {
        var violations = new List<string>();
        foreach (string relative in MigratedChromeFiles)
        {
            string path = Path.Combine(RepoRoot, relative);
            Assert.True(File.Exists(path), $"迁移清单里的文件不存在：{relative}");
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.TrimStart().StartsWith("//")) continue;
                if (!HardcodedColor().IsMatch(line)) continue;
                // 白名单按「语句」而非「单行」匹配：多行调用的 needle（如 lc_light_color）
                // 会落在下一行，只比对的当前行会把合法例外报成违规。
                if (IsAllowed(AllowedColorLiterals, relative, Statement(lines, i))) continue;
                violations.Add($"{relative}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(violations.Count == 0,
            "饰面颜色必须取自 UiVisualStyle 色板 token（DESIGN.md 第 2 节）：\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>
    /// `BackColor = Color.Transparent` 在自绘容器上会画成黑条——2026-09-11 用户报的
    /// 「日间模式有些问题」就是这个（未选中标签的指示条）。要用"透出底色"就写父容器同色。
    /// </summary>
    [Fact]
    public void NoTransparentBackColorsOnDrawnContainers()
    {
        var violations = new List<string>();
        foreach (string path in SourceFiles())
        {
            string relative = Relative(path);
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("BackColor = Color.Transparent")) continue;
                if (lines[i].TrimStart().StartsWith("//")) continue;
                violations.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(violations.Count == 0,
            "禁止 BackColor = Color.Transparent：把它换成父容器的实际颜色（UiVisualStyle.<Token>）：\n  "
            + string.Join("\n  ", violations));
    }

    static System.Text.RegularExpressions.Regex HardcodedColor() => new(
        @"Color\.FromArgb\(|\bColor\.(?:White|Black|Gray|Transparent|Red|Lime|Blue|Yellow)\b|\bBrushes\.\w+",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    static bool IsAllowed((string File, string Needle, string Why)[] allowed, string relative, string line) =>
        allowed.Any(entry => entry.File == relative && line.Contains(entry.Needle));

    /// <summary>取一行及其后两行，作为「一条语句」的近似（多行构造/多行实参调用）。</summary>
    static string Statement(string[] lines, int index) =>
        string.Join(' ', lines.Skip(index).Take(3));

    static IEnumerable<string> SourceFiles()
    {
        string root = Path.Combine(RepoRoot, "src", "MechrevoLiteWin");
        Assert.True(Directory.Exists(root), "找不到 src/MechrevoLiteWin");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    static string Relative(string path) => Path.GetRelativePath(RepoRoot, path);

    static string RepoRoot
    {
        get
        {
            _repoRoot ??= FindRepoRoot();
            return _repoRoot!;
        }
    }

    static string? _repoRoot;

    static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}

/// <summary>
/// 色板可读性：DESIGN.md §9 验收条件 3「所有正文/次文本对比度 ≥ 4.5:1」。
///
/// 状态色（Ok/Warn/Danger）也算正文——它们是给人读的状态文字，不是装饰点。
/// 这一条不是理论要求：A 批把对话框从「永远深色」改成跟随主题后，日间 Ok/Warn
/// 只有 2.99/3.02:1，实测才暴露出来（见 docs/ui-consistency-pass.md §3.1）。
/// </summary>
public class PaletteContrastTests
{
    [Theory]
    [InlineData(true, "夜间")]
    [InlineData(false, "日间")]
    public void EveryReadableForegroundMeetsAaOnEverySurface(bool night, string label)
    {
        UiVisualStyle.SetAuditNightMode(night);
        try
        {
            var surfaces = new (string Name, Color Color)[]
            {
                ("Window", UiVisualStyle.Window),
                ("Surface", UiVisualStyle.Surface),
                ("SurfaceRaised", UiVisualStyle.SurfaceRaised),
                ("Input", UiVisualStyle.Input),
            };
            var foregrounds = new (string Name, Color Color)[]
            {
                ("Text", UiVisualStyle.Text),
                ("Muted", UiVisualStyle.Muted),
                ("Danger", UiVisualStyle.Danger),
                ("Ok", UiVisualStyle.Ok),
                ("Warn", UiVisualStyle.Warn),
            };

            var violations = new List<string>();
            foreach ((string fgName, Color fg) in foregrounds)
                foreach ((string bgName, Color bg) in surfaces)
                {
                    double ratio = UiVisualStyle.ContrastRatio(fg, bg);
                    if (ratio < 4.5) violations.Add($"{fgName} on {bgName} = {ratio:F2}:1");
                }

            Assert.True(violations.Count == 0,
                $"{label}色板低于 AA 4.5:1：\n  " + string.Join("\n  ", violations));
        }
        finally
        {
            UiVisualStyle.SetAuditNightMode(null);
        }
    }
}

/// <summary>
/// 日间模式专项护栏（docs/run4-p2p3-design-spec.md §4.4）：
/// ① 下拉框主题必须日夜都套（此前 ApplyDarkComboTheme 在日间直接 return，
///    日间下拉保留深色/遗留箭头铬）；② 标题图标不得再走 [临时诊断] 的 Danger 色。
/// </summary>
public class DayModeThemeTests
{
    [Theory]
    [InlineData(true, "DarkMode_CFD")]
    [InlineData(false, "CFD")]
    public void ComboBoxes_GetThemedChromeInBothModes(bool night, string expectedTheme)
    {
        UiVisualStyle.SetAuditNightMode(night);
        try
        {
            using var form = new Form();
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Parent = form };
            combo.Items.Add("测试项");
            UiVisualStyle.ApplyWindow(form);

            Assert.Equal(expectedTheme, UiVisualStyle.AppliedComboTheme(combo));
        }
        finally
        {
            UiVisualStyle.SetAuditNightMode(null);
        }
    }

    [Fact]
    public void TitleGlyphs_DoNotUseTheTemporaryDiagnosticDangerColor()
    {
        UiVisualStyle.SetAuditNightMode(false);
        try
        {
            Color danger = UiVisualStyle.Danger;
            using var box = new PictureBox { Size = new Size(16, 16) };
            UiVisualStyle.ApplyGlyph(box, UiGlyph.Kind.Gauge);

            using Bitmap bitmap = new((Image)Assert.IsAssignableFrom<Image>(box.Image).Clone());
            var dangerPixels = new List<Point>();
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                    if (bitmap.GetPixel(x, y).A > 8 && bitmap.GetPixel(x, y).ToArgb() == danger.ToArgb())
                        dangerPixels.Add(new Point(x, y));

            Assert.True(dangerPixels.Count == 0,
                $"日间模式标题图标仍使用 [临时诊断] Danger 色（{dangerPixels.Count} 个像素）");
        }
        finally
        {
            UiVisualStyle.SetAuditNightMode(null);
        }
    }
}
