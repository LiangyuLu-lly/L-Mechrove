namespace MechrevoLite.UI;

internal static class UiVisualStyle
{
    internal const string ThemeModeConfigKey = "ui_theme_mode";

    // 基础字体：中文优先（DESIGN.md 第 3 节）。所有 new Font(...) 一律走 Font() 助手。
    internal const string BaseFontFamily = "Microsoft YaHei UI";

    internal static Font Font(float size, FontStyle style = FontStyle.Regular) =>
        new(BaseFontFamily, size, style);

    internal static Font Font(float size, FontStyle style, GraphicsUnit unit) =>
        new(BaseFontFamily, size, style, unit);

    /// <summary>
    /// 字号刻度（pt）。密集桌面工具的刻度，只允许这几档——新增界面时从这里挑，
    /// 不要再出现 7.125 / 11 这类随手值（归一时一次性收敛到本刻度）。
    /// 依据：UI/UX Pro Max 的 Typography → Font Size Scale（"一致模数刻度，不要随机字号"）。
    /// </summary>
    internal static class TypeScale
    {
        public const float Caption = 8.5F;    // 辅助文字、密集行
        public const float Body = 9F;         // 正文与控件默认
        public const float Subtitle = 9.5F;   // 分组标签、次级标题
        public const float Title = 12F;       // 对话框/窗口标题（卡片标题走 Subtitle + 加粗）
        public const float Display = 15F;     // 引导页等大标题
    }

    /// <summary>
    /// 间距刻度（逻辑像素，4 的倍数）。面板内边距按层级取档，见 DESIGN.md 第 5 节。
    /// 依据：UI/UX Pro Max 的 Layout → "8dp spacing rhythm" 与 "section spacing hierarchy"。
    /// </summary>
    internal static class Space
    {
        public const int Xs = 4;
        public const int Sm = 8;
        public const int Md = 12;
        public const int Lg = 16;
        public const int Xl = 24;
    }

    private readonly record struct Palette(
        Color Window,
        Color Surface,
        Color SurfaceRaised,
        Color Input,
        Color Border,
        Color Text,
        Color Muted,
        Color Accent,
        Color AccentText,
        Color AccentHover,
        Color AccentPressed,
        Color AccentBlue,
        Color Danger,
        Color Ok,
        Color Warn,
        Color Track);

    // 深潜座舱色板（DESIGN.md 第 2 节）。暗色是原生介质：窗口/面/抬升面三级亮度阶梯表达层次。
    private static readonly Palette NightPalette = new(
        Color.FromArgb(0x0B, 0x12, 0x20),
        Color.FromArgb(0x11, 0x1A, 0x2B),
        Color.FromArgb(0x16, 0x22, 0x3A),
        Color.FromArgb(0x0D, 0x15, 0x26),
        Color.FromArgb(0x24, 0x34, 0x4F),
        Color.FromArgb(0xE4, 0xEC, 0xF7),
        Color.FromArgb(0x8F, 0xA3, 0xBF),
        Color.FromArgb(0x4D, 0xA3, 0xFF),
        Color.FromArgb(0x08, 0x12, 0x1F),
        Color.FromArgb(0x6F, 0xB4, 0xFF),
        Color.FromArgb(0x3A, 0x8C, 0xE6),
        Color.FromArgb(0x74, 0xA8, 0xE8),
        // 危险色：抬亮到 #F0584E。原 #E5544B 在 SurfaceRaised 上只有 4.31:1，达不到
        // DESIGN.md §9 的 4.5:1 下限（状态文字要能当正文读，不只是装饰）。
        Color.FromArgb(0xF0, 0x58, 0x4E),
        Color.FromArgb(0x35, 0xC4, 0x6B),
        Color.FromArgb(0xE8, 0xB3, 0x39),
        Color.FromArgb(0x22, 0x30, 0x4A));

    private static readonly Palette DayPalette = new(
        Color.FromArgb(0xF2, 0xF5, 0xF9),
        Color.FromArgb(0xFF, 0xFF, 0xFF),
        Color.FromArgb(0xE9, 0xEF, 0xF6),
        Color.FromArgb(0xFF, 0xFF, 0xFF),
        Color.FromArgb(0xC9, 0xD5, 0xE3),
        Color.FromArgb(0x16, 0x20, 0x2E),
        Color.FromArgb(0x5A, 0x6B, 0x80),
        Color.FromArgb(0x1F, 0x6F, 0xE0),
        Color.White,
        Color.FromArgb(0x1A, 0x5F, 0xC4),
        Color.FromArgb(0x14, 0x50, 0x9F),
        Color.FromArgb(0x00, 0x66, 0xCC),
        Color.FromArgb(0xBE, 0x1E, 0x2D),
        // 日间 Ok/Warn 压深到 4.5:1 以上：#1E9E54 / #B77E14 在浅底上只有 2.99/3.02:1，
        // 状态文字在日间几乎读不出来（A 批把对话框从"永远深色"改成跟随主题后才暴露）。
        Color.FromArgb(0x16, 0x78, 0x3F),
        Color.FromArgb(0x8B, 0x5F, 0x0F),
        Color.FromArgb(0xD7, 0xE0, 0xEB));

    private static bool? _auditNightMode;

    private static Palette Current => IsNightMode ? NightPalette : DayPalette;

    public static Color Window => Current.Window;
    public static Color Surface => Current.Surface;
    public static Color SurfaceRaised => Current.SurfaceRaised;
    public static Color Input => Current.Input;
    public static Color Border => Current.Border;
    public static Color Text => Current.Text;
    public static Color Muted => Current.Muted;
    public static Color Accent => Current.Accent;
    internal static Color AccentText => Current.AccentText;
    private static Color AccentHover => Current.AccentHover;
    private static Color AccentPressed => Current.AccentPressed;
    public static Color AccentBlue => Current.AccentBlue;
    public static Color Danger => Current.Danger;
    public static Color Ok => Current.Ok;
    public static Color Warn => Current.Warn;
    public static Color Track => Current.Track;

    internal static bool IsNightMode => _auditNightMode ?? ResolveNightMode(AppConfig.GetString(ThemeModeConfigKey));

    internal static bool ResolveNightMode(string? savedMode) =>
        !string.Equals(savedMode, "day", StringComparison.OrdinalIgnoreCase);

    internal static void SetNightMode(bool night)
    {
        string mode = night ? "night" : "day";
        if (string.Equals(AppConfig.GetString(ThemeModeConfigKey), mode, StringComparison.OrdinalIgnoreCase)) return;
        AppConfig.Set(ThemeModeConfigKey, mode);
        AppConfig.Flush();
    }

    internal static void SetAuditNightMode(bool? night) => _auditNightMode = night;

    internal static (Color Background, Color Foreground) GetAccentButtonColors(bool night)
    {
        Palette palette = night ? NightPalette : DayPalette;
        return (palette.Accent, palette.AccentText);
    }

    internal static (Color Background, Color Foreground) GetAccentButtonHoverColors(bool night)
    {
        Palette palette = night ? NightPalette : DayPalette;
        return (palette.AccentHover, palette.AccentText);
    }

    /// <summary>
    /// 主题切换后把整棵控件树里「还带着另一套色板铬色」的底色映射到当前色板
    /// （缓存窗体重刷用：窗体创建于旧主题，ApplyTree 只管前景/按钮，容器底色
    /// 要靠这里按 token 对 token 换）。非铬色（设备色、语义色）原样保留。
    /// </summary>
    internal static void RetintChrome(Control root)
    {
        root.BackColor = MapChrome(root.BackColor);
        foreach (Control child in root.Controls) RetintChrome(child);
    }

    private static Color MapChrome(Color color)
    {
        Palette from = IsNightMode ? DayPalette : NightPalette;
        Palette to = Current;
        if (SameRgb(color, from.Window)) return to.Window;
        if (SameRgb(color, from.Surface)) return to.Surface;
        if (SameRgb(color, from.SurfaceRaised)) return to.SurfaceRaised;
        if (SameRgb(color, from.Input)) return to.Input;
        if (SameRgb(color, from.Border)) return to.Border;
        if (SameRgb(color, from.Track)) return to.Track;
        return color;
    }

    internal static double ContrastRatio(Color first, Color second)
    {
        double Luminance(Color color)
        {
            static double Linear(byte value)
            {
                double channel = value / 255D;
                return channel <= 0.04045D
                    ? channel / 12.92D
                    : Math.Pow((channel + 0.055D) / 1.055D, 2.4D);
            }

            return 0.2126D * Linear(color.R) + 0.7152D * Linear(color.G) + 0.0722D * Linear(color.B);
        }

        double a = Luminance(first);
        double b = Luminance(second);
        return (Math.Max(a, b) + 0.05D) / (Math.Min(a, b) + 0.05D);
    }

    public static void ApplyWindow(Form form)
    {
        form.BackColor = Window;
        form.ForeColor = Text;
        form.Font = Font(TypeScale.Body);
        ApplyTree(form);
    }

    public static void ApplySection(Control section)
    {
        SetContainerColor(section, Surface);
        ApplyTree(section);
    }

    /// <summary>
    /// v2 行规范：分区 = 无框行。容器递归涂 Window 底（内层布局的 BackColor 在
    /// 构造期复制了当时面板的 Surface，重刷必须覆盖回来，否则窄窗口下内层会
    /// 留下一块块 Surface 色斑）。
    /// </summary>
    public static void ApplyRow(Control row)
    {
        SetContainerColor(row, Window);
        ApplyTree(row);
    }

    public static void ApplyPrimaryButton(Button button)
    {
        button.BackColor = Accent;
        button.ForeColor = AccentText;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = AccentHover;
        button.FlatAppearance.MouseDownBackColor = AccentPressed;
        button.Font = Font(TypeScale.Body, FontStyle.Bold);
    }

    public static void ApplySecondaryButton(ButtonBase button)
    {
        if (button is RColorButton || Equals(button.Tag, "color-swatch")) return;
        button.BackColor = SurfaceRaised;
        button.ForeColor = Text;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = 1;
    }

    /// <summary>把一组按钮按视觉顺序连成一条分段控件（首/中/尾自动分配圆角与边线）。
    /// 轨道圆角 8 逻辑 px（预览 .seg 的 border-radius:8）——RButton 默认 8 token 只有一半。</summary>
    public static void ApplySegmentGroup(params RButton[] buttons)
    {
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].BorderRadius = 16;
            buttons[i].SegmentPosition = buttons.Length == 1
                ? RSegmentPosition.Single
                : i == 0 ? RSegmentPosition.First
                : i == buttons.Length - 1 ? RSegmentPosition.Last
                : RSegmentPosition.Middle;
        }
    }

    public static void ApplyTitle(Label label, float size = TypeScale.Subtitle)
    {
        label.ForeColor = Text;
        label.Font = Font(size, FontStyle.Bold);
    }

    public static void ApplyMuted(Label label)
    {
        label.ForeColor = Muted;
    }

    /// <summary>
    /// 绑定标题图标：把图标种类记在 <c>Tag</c> 上并按当前主题渲染一次。
    /// 之后每次主题重刷都会由 <c>ApplyTree</c> 按 Tag 重新渲染，夜/日自动跟随。
    /// </summary>
    internal static void ApplyGlyph(PictureBox box, UiGlyph.Kind kind)
    {
        box.Tag = kind;
        RenderGlyph(box, kind);
        Logger.WriteLine($"[diag] glyph {kind} -> {box.Name}#{box.GetHashCode()} form={box.FindForm()?.GetHashCode()} tagAfter={box.Tag}");
    }

    /// <summary>标题行没有独立图标位的卡片：把图标画进 Label 的 Image（带右侧留白）。</summary>
    internal static void ApplyGlyph(Label label, UiGlyph.Kind kind)
    {
        label.Tag = kind;
        RenderGlyph(label, kind);
    }

    // footer 图标键的图标种类登记：主题重刷时重渲染位图（ApplyTree 的 footer-ghost 分支消费）。
    // 取色器一并登记：普通键取主题 Muted；悬浮窗键传激活态取色器（Accent/Muted 随
    // AppConfig.IsOverlay() 现算）——主题重刷时按当前主题+激活态重渲染，不再残留
    // 构建期主题色（日→夜后 ◎ 图标变色的根因）。
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> _footerGlyphs = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> _footerGlyphColors = new();

    internal static void ApplyFooterGlyph(ButtonBase button, UiGlyph.Kind kind, int side)
        => ApplyFooterGlyph(button, kind, side, null);

    /// <param name="color">图标取色器；null = 主题 Muted（重刷时现算，日夜自动跟随）。</param>
    internal static void ApplyFooterGlyph(ButtonBase button, UiGlyph.Kind kind, int side, Func<Color>? color)
    {
        _footerGlyphs.AddOrUpdate(button, kind);
        _footerGlyphColors.AddOrUpdate(button, color ?? (Func<Color>)(() => Muted));
        Image? previous = button.Image;
        button.Image = UiGlyph.Render(kind, side, color?.Invoke() ?? Muted, 0);
        previous?.Dispose();
    }

    private static void RenderFooterGlyph(ButtonBase button, UiGlyph.Kind kind)
    {
        int side = button.Image is { Width: > 8 } existing ? existing.Width : 16;
        _footerGlyphColors.TryGetValue(button, out object? color);
        ApplyFooterGlyph(button, kind, side, color as Func<Color>);
    }

    /// <summary>
    /// footer 图标键（预览 .fbtn）的唯一造型来源：BuildFooterV2 的六个键（四个设计器旧键 +
    /// 设置/诊断两个新键）共用这一套铬，构造期与主题重刷（footer-ghost 分支）不再各写一份
    /// 字面量。调用前先设 BackColor（描边底取它）。图标+文字用 ImageAboveText 关系布局：
    /// WinForms 为图与文各保留独立条带，结构上不可能叠进同一矩形。
    /// </summary>
    internal static void StyleFooterGhostButton(ButtonBase button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.Tag = "footer-ghost";
        button.Margin = Padding.Empty;
        button.Padding = Padding.Empty;
        button.ImageAlign = ContentAlignment.TopCenter;
        button.TextAlign = ContentAlignment.BottomCenter;
        button.TextImageRelation = TextImageRelation.ImageAboveText;
        button.ForeColor = Text;
        button.Font = Font(TypeScale.Caption);
        if (button is RButton rbutton)
        {
            rbutton.Borderless = true;
            rbutton.BorderRadius = 2;   // 统一圆角：设计器给 buttonOverlay 的是 5，其余 RButton 是 2
        }
        button.FlatAppearance.BorderColor = button.BackColor;
        button.FlatAppearance.BorderSize = 0;
    }

    private static void RenderGlyph(PictureBox box, UiGlyph.Kind kind)
    {
        int side = box.Height > 8 ? box.Height : box.Width > 8 ? box.Width : 16;
        Image? previous = box.Image;
        box.Image = UiGlyph.Render(kind, side, Muted);
        previous?.Dispose();
        // 图标底随父容器现算：注册期（ApplyGlyph）与主题重刷（ApplyTree）两条渲染
        // 路径都要重算，否则构造期烤进的 Window 底在切换后就是另一主题的残色方块。
        // 注册时控件可能还没入父树（BuildHeadIcon）——那时保留构造期显式设的底色。
        if (box.Parent is not null) box.BackColor = box.Parent.BackColor;
    }

    private static void RenderGlyph(Label label, UiGlyph.Kind kind)
    {
        // v2 行头图标由独立 PictureBox 承载（BuildHeadRow/RCollapseGroup 图标列），
        // Label 路径不再挂位图：Label 的 Image 与 Text 在 WinForms 中独立绘制于同一
        // 内容区（Padding 同时作用于两者），无法错开——挂图必叠（r12-r14 实证）。
        if (label.Image is not null)
        {
            Image? previous = label.Image;
            label.Image = null;
            previous?.Dispose();
        }
        if (label.Padding.Left != 0)
        {
            label.Padding = Padding.Empty;
        }
    }

    private static void ApplyTree(Control root)
    {
        foreach (Control control in root.Controls)
        {
            switch (control)
            {
                case RColorButton:
                    break;
                case PictureBox glyph when glyph.Tag is UiGlyph.Kind kind:
                    RenderGlyph(glyph, kind);
                    // 图标底随父容器走（构造期烤进的 Window 底在切换后就是另一主题的残色块）
                    glyph.BackColor = glyph.Parent?.BackColor ?? Surface;
                    break;
                case Label glyphLabel when glyphLabel.Tag is UiGlyph.Kind kind:
                    RenderGlyph(glyphLabel, kind);
                    // Tag 分支此前直接 break，前景/底色从不重算——labelVersion 的 Muted
                    // 就是这么在日夜切换后残留成上一主题色的（2026-09-13 真机实测）。
                    glyphLabel.ForeColor = ResolveLabelForeground(glyphLabel.ForeColor);
                    glyphLabel.BackColor = glyphLabel.Parent?.BackColor ?? Surface;
                    break;
                case CheckBox check:
                    check.ForeColor = Text;
                    check.BackColor = check.Parent?.BackColor ?? Surface;
                    break;
                case RadioButton radio:
                    radio.ForeColor = Text;
                    radio.BackColor = radio.Parent?.BackColor ?? Surface;
                    break;
                case ButtonBase button when Equals(button.Tag, "mini-chip"):
                    // 迷你圆角键（预览 .mini，液冷灯光行）：Surface 底 + RButton 自画描边 +
                    // 悬停抬亮。不能走 Secondary 皮肤——那会把底色重刷成 SurfaceRaised，
                    // 与「迷你键是行内小芯片」的层次对不上；语义前景色（状态色）保留。
                    button.BackColor = Surface;
                    button.ForeColor = ResolveLabelForeground(button.ForeColor);
                    button.FlatAppearance.BorderSize = 0;
                    button.FlatAppearance.MouseOverBackColor = SurfaceRaised;
                    button.FlatAppearance.MouseDownBackColor = SurfaceRaised;
                    // 描边色也要跟随当前调色板重设，否则日夜切换后仍是构建期的夜/昼边框色
                    //（主题重刷只走 ApplyTree，不会再回构造函数）。
                    if (button is RButton chip) chip.BorderColor = Border;
                    break;
                case ButtonBase button when Equals(button.Tag, "footer-ghost"):
                    // footer 图标键（预览 .fbtn）：无框无底，仅悬停换面——不参与 Secondary 皮肤。
                    // 底/字/描边全部随父容器现算：此前 ForeColor 从不重刷，日→夜切换后
                    // 「设置」键文字残留上一主题 Text 色（浅底浅字不可读，2026-09-13 真机实测）。
                    button.BackColor = button.Parent?.BackColor ?? Surface;
                    button.ForeColor = ResolveLabelForeground(button.ForeColor);
                    button.FlatAppearance.BorderColor = button.BackColor;
                    button.FlatAppearance.BorderSize = 0;
                    button.FlatAppearance.MouseOverBackColor = SurfaceRaised;
                    button.FlatAppearance.MouseDownBackColor = SurfaceRaised;
                    // 图标位图也要按当前 Muted 重渲染，否则停留在构建期主题的灰阶
                    //（日→夜后齿轮图标只剩 3.4:1，真机实测的「残留方块」之一）。
                    if (_footerGlyphs.TryGetValue(button, out object? glyphKind) && glyphKind is UiGlyph.Kind footerKind)
                        RenderFooterGlyph(button, footerKind);
                    break;
                case ButtonBase button:
                    ApplySecondaryButton(button);
                    break;
                case ComboBox combo:
                    // DropDownList 的 ComboBox 由系统绘制，FlatStyle/BackColor 都压不住白底泄漏，
                    // 只能改 owner-draw 自己画。RComboBox 已有自己的 OnDrawItem，跳过免得双重绘制。
                    combo.FlatStyle = FlatStyle.Flat;
                    combo.BackColor = Input;
                    combo.ForeColor = Text;
                    if (combo is not RComboBox && combo.DrawMode != DrawMode.OwnerDrawFixed)
                    {
                        combo.DrawMode = DrawMode.OwnerDrawFixed;
                        combo.DrawItem += ComboBoxDrawItem;
                    }
                    // 下拉箭头按钮是系统绘制的独立子窗口：owner-draw 管不到，得切 DarkMode_CFD 主题。
                    ApplyDarkComboTheme(combo);
                    // RComboBox 自画外框/箭头区（BorderColor/ButtonColor/ArrowColor 不走
                    // BackColor/ForeColor）：构造期烤进的旧主题铬色在切换后残留，随重刷更新。
                    if (combo is RComboBox themed)
                    {
                        themed.BorderColor = Border;
                        themed.ButtonColor = Input;
                        themed.ArrowColor = Muted;
                    }
                    break;
                case TextBoxBase textBox:
                    textBox.BackColor = Input;
                    textBox.ForeColor = Text;
                    textBox.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case NumericUpDown numeric:
                    numeric.BorderStyle = BorderStyle.None;
                    numeric.BackColor = Input;
                    numeric.ForeColor = Text;
                    // 数字框内部是独立的编辑子窗口（以及 UpDown 微调子窗口），
                    // 父级颜色不会传下去——必须逐个子控件染色，否则白底照旧。
                    foreach (Control child in numeric.Controls)
                    {
                        child.BackColor = Input;
                        child.ForeColor = Text;
                    }
                    ApplyExplorerTheme(numeric);
                    break;
                case Label label:
                    label.ForeColor = ResolveLabelForeground(label.ForeColor);
                    // 底色随父容器现算：屏幕/亮度/电池/遥测/液冷/灯效行的标签在构造期
                    // 把当时主题的 Window/Surface 烤进了 BackColor，而容器重刷只涂容器——
                    // 切主题后标签仍是上一主题的底色块（浅底浅字不可读，2026-09-13 真机实测）。
                    label.BackColor = label.Parent?.BackColor ?? Surface;
                    break;
                case TrackBar track:
                    track.BackColor = track.Parent?.BackColor ?? Surface;
                    break;
            }

            ApplyTree(control);
        }
    }

    private static Color ResolveLabelForeground(Color color)
    {
        if (IsMuted(color) || IsPaletteMuted(color)) return Muted;
        return IsSemanticForeground(color) ? color : Text;
    }

    private static bool IsPaletteMuted(Color color) =>
        SameRgb(color, NightPalette.Muted) || SameRgb(color, DayPalette.Muted);

    private static bool IsSemanticForeground(Color color)
    {
        if (color.A == 0) return false;
        int spread = Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
        return spread >= 48;
    }

    private static bool SameRgb(Color first, Color second) =>
        first.R == second.R && first.G == second.G && first.B == second.B;

    // 已挂过句柄级主题的控件（HandleCreated 只挂一次，避免 ApplyTree 多次调用叠加处理器）。
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> _themedControls = new();

    /// <summary>把下拉框切到与当前主题匹配的系统铬（夜=DarkMode_CFD，日=CFD 标准铬）。
    /// 句柄未建时等建好再切；主题切换后按记录的上次主题重套。</summary>
    private static void ApplyDarkComboTheme(ComboBox combo)
    {
        string target = ComboThemeFor(IsNightMode);
        if (_themedControls.TryGetValue(combo, out object? existing) && existing is string applied && applied == target) return;
        _themedControls.AddOrUpdate(combo, target);
        void Apply()
        {
            try
            {
                SetWindowTheme(combo.Handle, ComboThemeFor(IsNightMode), null);
            }
            catch (Exception ex) { Logger.WriteLine("ComboBox theme failed: " + ex.Message); }
        }
        if (combo.IsHandleCreated) Apply();
        else combo.HandleCreated += (_, _) => Apply();
    }

    internal static string ComboThemeFor(bool night) => night ? "DarkMode_CFD" : "CFD";

    /// <summary>测试缝：返回该下拉框已套用的 uxtheme 主题名（未套用为 null）。</summary>
    internal static string? AppliedComboTheme(ComboBox combo) =>
        _themedControls.TryGetValue(combo, out object? value) && value is string theme ? theme : null;

    /// <summary>深色主题下拉项绘制：Input 底 + Text 字，选中项用 Border 提亮。</summary>
    private static void ComboBoxDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo || e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        Color background = selected ? Border : Input;
        Color foreground = selected ? Text : (combo.Enabled ? Text : Muted);
        using (SolidBrush brush = new(background))
            e.Graphics.FillRectangle(brush, e.Bounds);
        Rectangle textRect = new(e.Bounds.X + 2, e.Bounds.Y, Math.Max(1, e.Bounds.Width - 4), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, combo.GetItemText(combo.Items[e.Index]), combo.Font, textRect, foreground,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", ExactSpelling = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string? pszSubIdList);

    // 已挂过主题的控件（HandleCreated 只挂一次，避免 ApplyTree 多次调用叠加处理器）。
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> _themedNumeric = new();

    /// <summary>把数字框本体与其微调按钮子窗口都切到深色资源管理器主题（白底泄漏的根源）。</summary>
    private static void ApplyExplorerTheme(Control control)
    {
        if (_themedNumeric.TryGetValue(control, out _)) return;
        _themedNumeric.Add(control, new object());
        void Apply()
        {
            string theme = IsNightMode ? "DarkMode_Explorer" : "Explorer";
            try
            {
                SetWindowTheme(control.Handle, theme, null);
                foreach (Control child in control.Controls)
                    SetWindowTheme(child.Handle, theme, null);
            }
            catch (Exception ex) { Logger.WriteLine("SetWindowTheme failed: " + ex.Message); }
        }
        if (control.IsHandleCreated) Apply();
        else control.HandleCreated += (_, _) => Apply();
    }

    private static void SetContainerColor(Control root, Color color)
    {
        root.BackColor = color;
        foreach (Control control in root.Controls)
        {
            if (control is Panel or TableLayoutPanel or FlowLayoutPanel)
                SetContainerColor(control, color);
        }
    }

    private static bool IsMuted(Color color)
    {
        int spread = Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
        int average = (color.R + color.G + color.B) / 3;
        return spread < 24 && average is >= 90 and <= 205;
    }
}
