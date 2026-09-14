namespace MechrevoLite.UI;

/// <summary>
/// 折叠组（DESIGN.md v2 §0.3）：分隔线 + 标题行（箭头 + 图标 + 名称 + 右侧摘要）+ 可收起的内容区。
///
/// - 展开切换用 SuspendLayout/ResumeLayout 包裹，不做动画（WinForms 高抖动风险）。
/// - 初始态：入参 defaultExpanded；之后展开状态持久化到 AppConfig（键由调用方给定）。
/// - 整组可见性由 RefreshDeviceCapabilities 控制 Visible；组内子面板的 Visible 一律
///   由能力布尔驱动，绝不读 control.Visible 回来——父容器隐藏会让那个 getter 变 false。
/// - Dock 占位顺序：分隔线（索引最高→先布局→最顶）→ header → content。
/// </summary>
internal sealed class RCollapseGroup : BufferedPanel
{
    readonly Panel _content;
    readonly Label _arrow;
    readonly Label _summary;
    readonly Label _titleLabel;
    readonly string _configKey;
    bool _suppressTogglePersistence;

    public RCollapseGroup(string title, UiGlyph.Kind icon, string configKey, bool defaultExpanded = true, bool showDivider = false)
    {
        _configKey = configKey;
        Name = "collapseGroup_" + configKey;
        DoubleBuffered = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        BackColor = UiVisualStyle.Window;

        int D(int value) => ResponsiveLayout.LogicalToDevice(this, value);

        _content = new Panel
        {
            Name = Name + "_content",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiVisualStyle.Window,
        };

        _arrow = new Label
        {
            Name = Name + "_arrow",
            Text = defaultExpanded ? "▾" : "▸",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = Padding.Empty,
            ForeColor = UiVisualStyle.Muted,
            Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Body),
        };

        _titleLabel = new Label
        {
            Name = Name + "_title",
            Text = title,
            AutoSize = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
            ForeColor = UiVisualStyle.Text,
            Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Body, FontStyle.Bold),
        };

        var header = new BufferedTableLayoutPanel
        {
            Name = Name + "_header",
            Dock = DockStyle.Top,
            ColumnCount = 4,
            RowCount = 1,
            Height = D(30),
            Margin = Padding.Empty,
            Padding = new Padding(D(2), D(1), D(2), D(1)),
            BackColor = UiVisualStyle.Window,
            Cursor = Cursors.Hand,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(18)));   // 箭头（预览 12px 位）
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(24)));   // 图标（预览 16px + 8 间距）
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // 组头图标（预览每个组头都有线性图标）：走 ApplyGlyph 把图标种类记到 Tag 上，
        // 此后每次主题重刷 ApplyTree 都按当前 Muted 重渲染位图并重算底色（Tag 路径
        // 渲染的就是 Muted，见 UiVisualStyle.RenderGlyph）。早前不挂 Tag 的版本在
        // 日→夜切换后位图与底色都停在构建期主题，真机上就是三个浅色方块（2026-09-13 实测）。
        var iconBox = new PictureBox
        {
            Name = Name + "_icon",
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = UiVisualStyle.Window,
            Margin = Padding.Empty,
        };
        _summary = new Label
        {
            Name = Name + "_summary",
            Text = "",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = Padding.Empty,
            ForeColor = UiVisualStyle.Muted,
            Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Caption),
        };
        header.Controls.Add(_arrow, 0, 0);
        header.Controls.Add(iconBox, 1, 0);
        header.Controls.Add(_titleLabel, 2, 0);
        header.Controls.Add(_summary, 3, 0);
        // 入父树后再注册：ApplyGlyph 的首次渲染会把图标底取为父容器现色（组头=Window）。
        UiVisualStyle.ApplyGlyph(iconBox, icon);
        header.AccessibleName = title;
        header.AccessibleRole = AccessibleRole.PushButton;
        header.Click += (_, _) => Toggle();
        foreach (Control cell in header.Controls) cell.Click += (_, _) => Toggle();

        var divider = new Panel
        {
            Name = Name + "_divider",
            Dock = DockStyle.Top,
            Height = 1,
            Margin = Padding.Empty,
            BackColor = UiVisualStyle.Border,
            // 预览只在电池行下方有一条分隔线（液冷组）；灯光/更多开关组之间只有留白。
            Visible = showDivider,
        };

        Controls.Add(_content);
        Controls.Add(header);
        Controls.Add(divider);
        // Dock=Top 按 Z 序倒序布局（索引最高→最先布局→最顶）：divider 最后加 → 最顶，
        // header 次之，content 最内（分隔线下方）。反过来加（真机首验前的旧序）会让
        // 内容区压到标题上方。不要 BringToFront：会把 header 移回布局末位 = 显示在最下面。

        Expanded = defaultExpanded;
        _content.Visible = Expanded;
        if (Program.UiAuditMode)
        {
            // 审计强制展开（不写配置），保证折叠组内容可被截图。
            Expanded = true;
            _content.Visible = true;
            _arrow.Text = "▾";
        }
    }

    public bool Expanded { get; private set; }

    /// <summary>右侧摘要（如「已连接 · 泵自动」）。</summary>
    public string Summary
    {
        get => _summary.Text;
        set => _summary.Text = value;
    }

    public Label TitleLabel => _titleLabel;

    /// <summary>审计模式下强制展开（不管持久化状态），保证折叠组内容可被截图审计。</summary>
    public void ForceExpandedForAudit()
    {
        _suppressTogglePersistence = true;
        Expanded = true;
        _content.Visible = true;
        _arrow.Text = "▾";
        _suppressTogglePersistence = false;
    }

    /// <summary>放入内容区（承载一个既有分区面板；高度沿用该面板自己的几何）。</summary>
    public void SetContent(Control content)
    {
        content.Dock = DockStyle.Top;
        _content.Controls.Add(content);
    }

    public void Toggle()
    {
        SuspendLayout();
        _content.SuspendLayout();
        try
        {
            Expanded = !Expanded;
            _content.Visible = Expanded;
            _arrow.Text = Expanded ? "▾" : "▸";
            if (!_suppressTogglePersistence && !Program.UiAuditMode)
                AppConfig.Set(_configKey, Expanded ? 1 : 0);
        }
        finally
        {
            _content.ResumeLayout(true);
            ResumeLayout(true);
        }
        Toggled?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Toggled;
}
