using MechrevoLite.UI;
using System.Diagnostics;

namespace MechrevoLite;

/// <summary>
/// One-time setup guide shown after the main window has painted. It also carries the
/// anonymous-statistics notice with its switch; users who already saw the guide before the
/// notice existed get the notice alone (<paramref name="telemetryOnly"/>) once.
/// </summary>
public sealed class FirstRunGuideForm : RForm
{
    readonly RCheckBox _telemetry;

    /// <summary>用户在告知里留下的选择（关窗、点「稍后」也按它生效：用户已经看到了告知）。</summary>
    internal bool TelemetryChecked => _telemetry.Checked;

    public FirstRunGuideForm() : this(telemetryOnly: false) { }

    public FirstRunGuideForm(bool telemetryOnly)
    {
        _telemetry = new RCheckBox
        {
            Name = "checkFirstRunTelemetry",
            Text = Properties.Strings.UsageTelemetryToggle,
            Checked = Usage.UsageTelemetry.Enabled,
            AutoSize = true,
            ForeColor = UiVisualStyle.Text,
            Margin = new Padding(0, UiVisualStyle.Space.Xs, 0, 0),
        };
        Text = telemetryOnly ? "L-Mechrevo" : Properties.Strings.FirstRunTitle;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(SettingsForm.CompactDashboardLogicalClientSize.Width, telemetryOnly ? 300 : 470);
        MinimumSize = new Size(320, 280);
        BackColor = UiVisualStyle.Window;
        ForeColor = UiVisualStyle.Text;
        InitTheme(true);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiVisualStyle.Window,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        Controls.Add(root);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(UiVisualStyle.Space.Xl, UiVisualStyle.Space.Lg, UiVisualStyle.Space.Xl, UiVisualStyle.Space.Sm),
            Margin = Padding.Empty,
            BackColor = UiVisualStyle.Surface,
        };
        header.Controls.Add(new Label
        {
            Text = telemetryOnly ? Properties.Strings.FirstRunTelemetryTitle : Properties.Strings.FirstRunHeadline,
            AutoSize = true,
            Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Display, FontStyle.Bold),
            ForeColor = UiVisualStyle.Text,
            Margin = Padding.Empty,
        }, 0, 0);
        if (!telemetryOnly)
        {
            header.Controls.Add(new Label
            {
                Text = Properties.Strings.FirstRunLead,
                AutoSize = true,
                Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Body),
                ForeColor = UiVisualStyle.Muted,
                Margin = new Padding(0, UiVisualStyle.Space.Xs, 0, 0),
            }, 0, 1);
        }
        root.Controls.Add(header, 0, 0);

        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiVisualStyle.Window,
        };
        var steps = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(UiVisualStyle.Space.Lg, UiVisualStyle.Space.Md, UiVisualStyle.Space.Lg, UiVisualStyle.Space.Md),
            Margin = Padding.Empty,
            BackColor = UiVisualStyle.Window,
        };
        steps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int stepRow = 0;
        if (!telemetryOnly)
        {
            steps.RowCount = 4;
            // N10: the installer already set up the GCU service and removed the vendor console, so the
            // guide must NOT send the user to download it - that would reinstall what we just removed.
            steps.Controls.Add(CreateStep("1", "无需安装任何其他控制台",
                "安装器已经装好 GCU 服务与驱动，本程序自带全部必要组件。厂商「官方控制台」已由安装器清理并替换为 L-Mechrevo，不需要再下载或安装它。"), 0, stepRow++);
            steps.Controls.Add(CreateStep("2", "直接开始使用",
                "开机启动、性能模式、显卡模式与灯效都在这个窗口里；GCU 服务在后台运行，无需额外操作。"), 0, stepRow++);
            steps.Controls.Add(CreateStep("!", "退出其他灯效控制软件",
                "不要同时运行 BetterRGB、OpenRGB 或其他厂商灯效程序，否则多个程序抢占 HID 设备可能导致灯效失效或设备访问冲突。", warning: true), 0, stepRow++);
        }
        else
        {
            steps.RowCount = 1;
        }
        // 匿名统计告知（beta21）：说清楚发什么、不发什么、在哪里关，开关就在告知里。
        // 单独告知时标题已经在页头，步骤标题换成「发送什么、不发送什么」，不重复三遍。
        steps.Controls.Add(CreateStep("i",
            telemetryOnly ? Properties.Strings.FirstRunTelemetryStepTitle : Properties.Strings.FirstRunTelemetryTitle,
            Properties.Strings.FirstRunTelemetryBody, extra: _telemetry), 0, stepRow);
        scroll.Controls.Add(steps);
        root.Controls.Add(scroll, 0, 1);

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(UiVisualStyle.Space.Lg, UiVisualStyle.Space.Sm, UiVisualStyle.Space.Lg, UiVisualStyle.Space.Sm),
            Margin = Padding.Empty,
            BackColor = UiVisualStyle.Surface,
        };
        // 底部三列等宽（run5 二级界面收尾）：主按钮的视觉权重由 ApplyPrimaryButton 承担。
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // N10: no "official download" button - the vendor console is removed by the installer.
        var later = CreateButton("稍后");
        later.DialogResult = DialogResult.Cancel;
        var system = CreateButton(telemetryOnly ? Properties.Strings.FirstRunTelemetryOk : "开始使用");
        system.DialogResult = DialogResult.OK;
        UiVisualStyle.ApplyPrimaryButton(system);
        if (telemetryOnly)
        {
            // 只有告知时一个按钮就够：开关的选择随关窗生效，「稍后」没有意义（Esc 同样关窗）。
            later.Dispose();
            actions.Controls.Add(system, 1, 0);
            CancelButton = system;
        }
        else
        {
            actions.Controls.Add(later, 0, 0);
            actions.Controls.Add(system, 1, 0);
            CancelButton = later;
        }
        root.Controls.Add(actions, 0, 2);

        AcceptButton = system;
        // 初始焦点给主按钮：否则落在统计开关上，打开就带一圈焦点框，回车也不会关窗。
        ActiveControl = system;
        UiVisualStyle.ApplyWindow(this);
        header.BackColor = UiVisualStyle.Surface;
        actions.BackColor = UiVisualStyle.Surface;
        UiVisualStyle.ApplyPrimaryButton(system);
        ResponsiveLayout.ScaleFrom96(this, this);
    }

    private static Control CreateStep(string marker, string title, string description, bool warning = false, Control? extra = null)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = extra is null ? 2 : 3,
            Margin = new Padding(0, 0, 0, UiVisualStyle.Space.Sm),
            Padding = new Padding(UiVisualStyle.Space.Xs, UiVisualStyle.Space.Sm, UiVisualStyle.Space.Xs, UiVisualStyle.Space.Sm),
            BackColor = UiVisualStyle.Window,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var badge = new Label
        {
            Text = marker,
            AutoSize = false,
            Size = new Size(28, 28),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Subtitle, FontStyle.Bold),
            ForeColor = warning ? UiVisualStyle.Danger : UiVisualStyle.AccentText,
            BackColor = warning ? UiVisualStyle.SurfaceRaised : UiVisualStyle.Accent,
            Margin = new Padding(0, 0, UiVisualStyle.Space.Sm, 0),
        };
        row.SetRowSpan(badge, 2);
        row.Controls.Add(badge, 0, 0);
        row.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Subtitle, FontStyle.Bold),
            ForeColor = warning ? UiVisualStyle.Danger : UiVisualStyle.Text,
            Margin = Padding.Empty,
        }, 1, 0);
        // 描述宽度随列自适应（Text Reflow Critical：禁固定宽裁字）：AutoSize 标签 + 动态
        // MaximumSize。最大宽每次布局都取第二列的真实宽度（不是固定像素，175% 下不会提前折行），
        // 高度由 AutoSize 按换行结果自己长——手算高度在审计缩放下会少一行（旧实现 304x47 装不下三行）。
        var desc = new Label
        {
            Text = description,
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Caption),
            ForeColor = UiVisualStyle.Muted,
            Margin = new Padding(0, UiVisualStyle.Space.Xs, 0, 0),
        };
        bool reflowing = false;
        void ReflowDescription()
        {
            if (reflowing) return;
            int[] widths = row.GetColumnWidths();
            if (widths.Length < 2) return;
            int width = Math.Max(40, widths[1] - desc.Margin.Horizontal - 2);
            if (desc.MaximumSize.Width == width) return;
            reflowing = true;
            try { desc.MaximumSize = new Size(width, 0); }
            finally { reflowing = false; }
        }
        row.Layout += (_, _) => ReflowDescription();
        row.Resize += (_, _) => ReflowDescription();
        desc.FontChanged += (_, _) => ReflowDescription();
        row.Controls.Add(desc, 1, 1);
        if (extra is not null)
        {
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            row.Controls.Add(extra, 1, 2);
        }
        return row;
    }

    private static Button CreateButton(string text) => new RButton
    {
        Text = text,
        Dock = DockStyle.Fill,
        Margin = new Padding(UiVisualStyle.Space.Xs, 0, UiVisualStyle.Space.Xs, 0),
        Cursor = Cursors.Hand,
        BorderRadius = 4,
        Secondary = true,
    };

}
