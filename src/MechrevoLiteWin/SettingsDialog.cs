namespace MechrevoLite;

using MechrevoLite.UI;

/// <summary>
/// 设置弹窗（DESIGN.md v2 §0.5）：收纳低频系统项，footer ⚙ 打开。
/// 面板/控件由主窗体传入（跨窗托管控件沿用 ApplyTree 主题刷新，切主题不重画）。
/// 滚动布局、跟随主题、贴边显示（ShowAdjacentTo）。
/// </summary>
public sealed class SettingsDialog : UI.RForm
{
    Control? _displayHeader;
    Control? _displayRow;
    Form? _owner;

    public SettingsDialog(Control themePanel, Control officialPanel,
        Control? overdriveChk, bool displayGroupAvailable)
    {
        BackColor = UI.UiVisualStyle.Window;
        ForeColor = UI.UiVisualStyle.Text;
        Text = "设置";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        // 托管控件（界面外观/官方控制台面板）已由主窗体按**设备像素**缩放（D()），若此处再让
        // WinForms 按 DPI 自动缩放一次，就是二次缩放（实测 ≈3.06×），内容撑破窗口 →
        // 「控制台」按钮被右边缘裁掉（真机 2026-09-13 截图）。与 RColorPicker 同策略：
        // 关掉自动缩放，尺寸一律用设备像素自算（D() 与 ShrinkToContent）。
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(470, 340);   // 占位；OnLoad（首帧之前）按实测内容收口
        AutoScroll = true;
        InitTheme(true);
        // I5：非模态 Show() 的窗体被 Close() 会连同被过继进来的主界面面板一起释放，
        // 而 OpenSettingsDialog 缓存了实例且不清空 → 再次打开时用已释放控件构造，异常被吞、
        // 设置再也打不开。照 RgbForm 的做法：用户关闭只隐藏，保持实例存活可反复打开。
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };

        int D(int v) => UI.ResponsiveLayout.LogicalToDevice(this, v);

        var root = new UI.BufferedTableLayoutPanel
        {
            // 高度按内容收紧（规格 §3.1 DoD）：root 改 Dock=Top + AutoSize，
            // ShowAdjacentTo 按实测内容高收缩窗口；溢出由窗体 AutoScroll 兜底。
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 12,
            Padding = new Padding(D(16), D(12), D(16), D(12)),
            BackColor = UI.UiVisualStyle.Window,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);
        int r = 0;

        // 组标题不单独占位：只有该组至少有一行**可见**时才渲染标题。
        // 空的组标题（例如机型不支持响应加速时的「显示」）既不留空块也不留间距——
        // 标题与行都是 AutoSize 行，一起隐藏时行高为 0。
        Label AddHeader(string text, int topMargin)
        {
            var header = new Label
            {
                Text = text,
                Font = UI.UiVisualStyle.Font(UI.UiVisualStyle.TypeScale.Caption),
                ForeColor = UI.UiVisualStyle.Muted,
                AutoSize = true,
                Margin = new Padding(0, D(topMargin), 0, D(4)),
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(header, 0, r++);
            return header;
        }

        void AddRow(Control control)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(control, 0, r++);
        }

        AddHeader("界面", 0);
        themePanel.Dock = DockStyle.Top;
        // 主窗以 Visible=false 延迟托管（Settings.cs 构建尾部）；弹窗接管后必须重新显示，
        // 否则 界面外观（日间/夜间）与 官方控制台 两节在弹窗里永远空白（真机实测）。
        themePanel.Visible = true;
        officialPanel.Visible = true;
        AddRow(themePanel);

        // 「显示」组只有响应加速一行（屏幕校色已迁出弹窗：改为「屏幕」行头内联下拉，
        // Settings.cs，2026-09-14）。控件缺失或机型不支持响应加速时整组（标题 + 行）
        // 都不建，避免截图里「显示」下面空无一物。
        if (overdriveChk is not null)
        {
            _displayHeader = AddHeader("显示", 10);
            overdriveChk.Dock = DockStyle.Left;
            AddRow(overdriveChk);
            _displayRow = overdriveChk;
            SetDisplayGroupAvailable(displayGroupAvailable);
        }

        AddHeader("系统", 10);
        officialPanel.Dock = DockStyle.Top;
        AddRow(officialPanel);
    }

    /// <summary>
    /// 「显示」组的可见性由宿主（SettingsForm）按机型能力决定：行与标题同进同退，
    /// 保证标题只在至少有一行可见时渲染。构造后能力变化时由 RefreshDeviceCapabilities 重发。
    /// </summary>
    internal void SetDisplayGroupAvailable(bool available)
    {
        if (_displayRow is not null) _displayRow.Visible = available;
        if (_displayHeader is not null) _displayHeader.Visible = available;
    }

    /// <summary>按内容收紧窗口：宽与高都改。高取 root（Dock=Top + AutoSize）实测高；
    /// 宽取"行的所需宽度"上界（绝对列之和 + 同行控件首选宽 + padding），宁可略宽绝不裁切；
    /// 两者都钳制到工作区。幂等：OnLoad 与再次打开的 ShowAdjacentTo 都会调一次。</summary>
    internal void ShrinkToContent()
    {
        Control? content = Controls.OfType<Control>().FirstOrDefault(c => c is TableLayoutPanel);
        if (content is null || !content.Visible) return;
        Rectangle wa = Screen.FromControl(this).WorkingArea;
        int maxW = Math.Max(240, wa.Width - 64);
        int maxH = Math.Max(160, wa.Height - 64);

        int needW = content.Padding.Horizontal + RequiredRowWidth(content);
        int targetW = Math.Min(Math.Max(needW, 320), maxW);
        int targetH = Math.Min(content.Height, maxH);
        if (ClientSize.Width == targetW && ClientSize.Height == targetH) return;
        ClientSize = new Size(targetW, targetH);
        ResponsiveLayout.PerformLayoutTree(this);
    }

    /// <summary>行的所需宽度上界：对每个 TableLayoutPanel 累加其**绝对列**宽，再补上同行控件
    /// 的最大首选宽（百分比列至少要有内容宽度），取全树最大值。</summary>
    static int RequiredRowWidth(Control node)
    {
        int widest = 0;
        if (node is TableLayoutPanel tlp)
        {
            int absolute = 0;
            foreach (ColumnStyle style in tlp.ColumnStyles)
                if (style.SizeType == SizeType.Absolute) absolute += (int)style.Width;

            int widestChild = 0;
            foreach (Control child in tlp.Controls)
            {
                int pref = child is TableLayoutPanel inner
                    ? RequiredRowWidth(inner)
                    : child.PreferredSize.Width;
                if (pref > widestChild) widestChild = pref;
            }
            int need = absolute + widestChild + tlp.Padding.Horizontal + tlp.Margin.Horizontal;
            if (need > widest) widest = need;
        }
        foreach (Control child in node.Controls)
        {
            int w = RequiredRowWidth(child);
            if (w > widest) widest = w;
        }
        return widest;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Load 在首次绘制之前触发：这里按内容收口尺寸并按 owner 贴边定位。
        // ApplyResponsiveBounds（RForm.OnShown）随后只做工作区钳制，正常不会改几何。
        ResponsiveLayout.PerformLayoutTree(this);
        ShrinkToContent();
        if (_owner is not null) ResponsiveLayout.PlaceAdjacent(this, _owner);
    }

    /// <summary>
    /// 定位并显示。首次打开时尺寸/位置在 OnLoad（首帧之前）确定；再次打开（句柄已在）时
    /// 在 Show 之前重算并贴边，保证首帧即最终帧；Show 之后不再改 Location/Size。
    /// </summary>
    internal void ShowAdjacentTo(Form owner)
    {
        if (Visible)
        {
            Activate();
            return;
        }

        _owner = owner;
        if (IsHandleCreated)
        {
            ResponsiveLayout.PerformLayoutTree(this);
            ShrinkToContent();
            ResponsiveLayout.PlaceAdjacent(this, owner);
        }
        Show();
    }
}
