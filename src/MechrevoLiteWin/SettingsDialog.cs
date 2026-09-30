namespace MechrevoLite;

using System.Diagnostics;
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
    bool _syncingLanguage;

    /// <summary>测试接缝：非 null 时写入语言后不弹重启框、不重启进程。</summary>
    internal static Action<string>? LanguageChangedOverride { get; set; }

    public SettingsDialog(Control themePanel, Control? overdriveChk, bool displayGroupAvailable)
    {
        BackColor = UI.UiVisualStyle.Window;
        ForeColor = UI.UiVisualStyle.Text;
        Text = Properties.Strings.FooterSettings;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        // 托管控件（界面外观）已由主窗体按**设备像素**缩放（D()），若此处再让
        // WinForms 按 DPI 自动缩放一次，就是二次缩放。与 RColorPicker 同策略：
        // 关掉自动缩放，尺寸一律用设备像素自算（D() 与 ShrinkToContent）。
        AutoScaleMode = AutoScaleMode.None;
        // 占位宽对齐主窗；OnLoad（首帧之前）按实测内容收口，且不超过主窗。
        ClientSize = new Size(
            UI.ResponsiveLayout.LogicalToDevice(this, SettingsForm.CompactDashboardLogicalClientSize.Width),
            UI.ResponsiveLayout.LogicalToDevice(this, 340));
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

        AddHeader(Properties.Strings.SettingsZoneAppearance, 0);
        themePanel.Dock = DockStyle.Top;
        // 主窗以 Visible=false 延迟托管（Settings.cs 构建尾部）；弹窗接管后必须重新显示，
        // 否则界面外观（日间/夜间）在弹窗里永远空白。
        themePanel.Visible = true;
        AddRow(themePanel);
        AddRow(BuildLanguageRow(D));

        // 「显示」组只有响应加速一行（屏幕校色已迁出弹窗：改为「屏幕」行头内联下拉，
        // Settings.cs，2026-09-14）。控件缺失或机型不支持响应加速时整组（标题 + 行）
        // 都不建，避免截图里「显示」下面空无一物。
        if (overdriveChk is not null)
        {
            _displayHeader = AddHeader(Properties.Strings.SettingsZoneDisplay, 10);
            overdriveChk.Dock = DockStyle.Left;
            AddRow(overdriveChk);
            _displayRow = overdriveChk;
            SetDisplayGroupAvailable(displayGroupAvailable);
        }

        // 隐私与反馈（beta21）：匿名统计开关 + 问题反馈入口。都是应用自己的设置，与机型无关，恒可见。
        AddHeader(Properties.Strings.SettingsZonePrivacy, 10);
        AddRow(BuildPrivacyRow(D));
    }

    RCheckBox? _telemetryCheck;
    bool _syncingTelemetry;
    readonly ToolTip _toolTip = new() { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 200 };

    TableLayoutPanel BuildPrivacyRow(Func<int, int> D)
    {
        var row = new UI.BufferedTableLayoutPanel
        {
            Name = "panelPrivacy",
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _telemetryCheck = new RCheckBox
        {
            Name = "checkUsageTelemetry",
            Text = Properties.Strings.UsageTelemetryToggle,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = UI.UiVisualStyle.Text,
            Margin = new Padding(0, D(2), 0, D(4)),
            AccessibleDescription = Properties.Strings.UsageTelemetryTip,
        };
        SyncTelemetryCheck();
        _telemetryCheck.CheckedChanged += (_, _) =>
        {
            if (_syncingTelemetry || _telemetryCheck is null) return;
            Usage.UsageTelemetry.SetEnabled(_telemetryCheck.Checked);
        };
        _toolTip.SetToolTip(_telemetryCheck, Properties.Strings.UsageTelemetryTip);
        // 弹窗是缓存实例：再次打开时按当前配置回显（首次引导里也能改这个开关）。
        VisibleChanged += (_, _) => { if (Visible) SyncTelemetryCheck(); };

        var feedback = new RButton
        {
            Name = "buttonFeedback",
            Text = Properties.Strings.FeedbackOpen,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, D(2), 0, D(2)),
            Padding = new Padding(D(10), D(2), D(10), D(2)),
            Cursor = Cursors.Hand,
            BorderRadius = 4,
            Secondary = true,
        };
        // InitTheme 在这一行建好之前就跑过：按钮皮肤要自己上（底色 + 描边），否则只剩一行字，不像按钮。
        UI.UiVisualStyle.ApplySecondaryButton(feedback);
        feedback.BorderColor = UI.UiVisualStyle.Border;
        feedback.Click += (_, _) =>
        {
            using var form = new FeedbackForm();
            form.ShowDialog(this);
        };
        row.Controls.Add(_telemetryCheck, 0, 0);
        row.Controls.Add(feedback, 0, 1);
        return row;
    }

    void SyncTelemetryCheck()
    {
        if (_telemetryCheck is null) return;
        _syncingTelemetry = true;
        try { _telemetryCheck.Checked = Usage.UsageTelemetry.Enabled; }
        finally { _syncingTelemetry = false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    TableLayoutPanel BuildLanguageRow(Func<int, int> D)
    {
        var row = new UI.BufferedTableLayoutPanel
        {
            Name = "panelLanguage",
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = new Padding(0, D(8), 0, 0),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var label = new Label
        {
            Name = "labelLanguage",
            Text = Properties.Strings.Language,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = UI.UiVisualStyle.Text,
            Font = UI.UiVisualStyle.Font(UI.UiVisualStyle.TypeScale.Body),
            Margin = new Padding(0, D(6), D(8), 0),
        };

        var combo = new UI.RComboBox
        {
            Name = "comboLanguage",
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = D(140),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, D(2), 0, D(2)),
        };
        combo.Items.Add("中文");
        combo.Items.Add("English");
        string current = UiLanguage.Normalize(
            AppConfig.GetString(UiLanguage.ConfigKey),
            Thread.CurrentThread.CurrentUICulture.Name);
        _syncingLanguage = true;
        combo.SelectedIndex = UiLanguage.IndexOf(current);
        _syncingLanguage = false;
        combo.SelectedIndexChanged += (_, _) => OnLanguageSelected(combo);
        row.Controls.Add(label, 0, 0);
        row.Controls.Add(combo, 1, 0);
        return row;
    }

    void OnLanguageSelected(ComboBox combo)
    {
        if (_syncingLanguage) return;
        string code = UiLanguage.CodeFromIndex(combo.SelectedIndex);
        string? previous = AppConfig.GetString(UiLanguage.ConfigKey);
        if (string.Equals(previous, code, StringComparison.OrdinalIgnoreCase)) return;
        AppConfig.Set(UiLanguage.ConfigKey, code);
        if (LanguageChangedOverride is { } captured)
        {
            captured(code);
            return;
        }

        DialogResult answer = MessageBox.Show(
            this,
            Properties.Strings.LanguageRestartPrompt,
            Properties.Strings.LanguageRestartTitle,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        string path = Environment.ProcessPath ?? Application.ExecutablePath;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        Application.Exit();
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
        int parentCap = UI.ResponsiveLayout.LogicalToDevice(this, SettingsForm.CompactDashboardLogicalClientSize.Width);
        // 下限按逻辑像素缩放：旧的 320 是设备像素，175% 屏上只相当于 183 逻辑 px，
        // 弹窗被压成一条、日间/夜间只剩一个字（真机与审计截图一致）。
        int minW = UI.ResponsiveLayout.LogicalToDevice(this, 300);
        int targetW = Math.Min(Math.Max(needW, minW), Math.Min(maxW, parentCap));
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
