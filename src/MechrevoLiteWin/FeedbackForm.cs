using MechrevoLite.Helpers;
using MechrevoLite.UI;
using MechrevoLite.Usage;

namespace MechrevoLite;

/// <summary>
/// 问题反馈：描述 + 可选联系方式 + 可选附带系统信息与脱敏日志，点「发送」才上传（与匿名统计开关无关）。
/// 发送成功显示服务端编号；失败原样说明原因，并提示改用「诊断」导出诊断包。
/// </summary>
public sealed class FeedbackForm : RForm
{
    readonly RTextBox _message;
    readonly RTextBox _contact;
    readonly RCheckBox _attach;
    readonly Label _status;
    readonly RButton _send;
    readonly RButton _cancel;
    CancellationTokenSource? _sending;
    bool _sent;

    /// <summary>测试接缝：替代真实发送。</summary>
    internal static Func<string, string?, bool, CancellationToken, Task<FeedbackResult>>? SendOverride { get; set; }

    public FeedbackForm()
    {
        Text = Properties.Strings.FeedbackTitle;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(SettingsForm.CompactDashboardLogicalClientSize.Width, 430);
        // 下限与首次引导一致：小屏高缩放（1280x720@200%）下窗口要能缩进工作区，内容区滚动而不是裁切。
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
            RowCount = 1,
            Padding = new Padding(UiVisualStyle.Space.Xl, UiVisualStyle.Space.Lg, UiVisualStyle.Space.Xl, UiVisualStyle.Space.Md),
            Margin = Padding.Empty,
            BackColor = UiVisualStyle.Surface,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.Controls.Add(WrappingLabel(Properties.Strings.FeedbackLead, UiVisualStyle.TypeScale.Body, UiVisualStyle.Muted, header), 0, 0);
        root.Controls.Add(header, 0, 0);

        // 内容区可滚动（与首次引导同一结构）：窗口被工作区压矮时出现滚动条，不裁切输入框与按钮。
        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiVisualStyle.Window,
        };
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(UiVisualStyle.Space.Lg, UiVisualStyle.Space.Md, UiVisualStyle.Space.Lg, UiVisualStyle.Space.Sm),
            Margin = Padding.Empty,
            BackColor = UiVisualStyle.Window,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++) body.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        body.Controls.Add(FieldLabel(Properties.Strings.FeedbackMessageLabel), 0, 0);
        _message = new RTextBox
        {
            Name = "textFeedbackMessage",
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            MaxLength = FeedbackUpload.MaxMessageChars,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Height = 120,
            Margin = new Padding(0, UiVisualStyle.Space.Xs, 0, UiVisualStyle.Space.Sm),
            PlaceholderText = Properties.Strings.FeedbackMessagePlaceholder,
            AccessibleName = Properties.Strings.FeedbackMessageLabel,
        };
        body.Controls.Add(_message, 0, 1);

        body.Controls.Add(FieldLabel(Properties.Strings.FeedbackContactLabel), 0, 2);
        _contact = new RTextBox
        {
            Name = "textFeedbackContact",
            MaxLength = FeedbackUpload.MaxContactChars,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Margin = new Padding(0, UiVisualStyle.Space.Xs, 0, UiVisualStyle.Space.Sm),
            PlaceholderText = Properties.Strings.FeedbackContactPlaceholder,
            AccessibleName = Properties.Strings.FeedbackContactLabel,
        };
        body.Controls.Add(_contact, 0, 3);

        _attach = new RCheckBox
        {
            Name = "checkFeedbackAttach",
            Text = Properties.Strings.FeedbackAttach,
            Checked = true,
            AutoSize = true,
            ForeColor = UiVisualStyle.Text,
            Margin = new Padding(0, 0, 0, UiVisualStyle.Space.Xs),
            AccessibleDescription = Properties.Strings.FeedbackAttachTip,
        };
        body.Controls.Add(_attach, 0, 4);

        _status = WrappingLabel(Properties.Strings.FeedbackAttachTip, UiVisualStyle.TypeScale.Caption, UiVisualStyle.Muted, body);
        _status.Name = "labelFeedbackStatus";
        body.Controls.Add(_status, 0, 5);
        scroll.Controls.Add(body);
        root.Controls.Add(scroll, 0, 1);

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(UiVisualStyle.Space.Lg, UiVisualStyle.Space.Sm, UiVisualStyle.Space.Lg, UiVisualStyle.Space.Sm),
            Margin = Padding.Empty,
            BackColor = UiVisualStyle.Surface,
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _cancel = CreateButton(Properties.Strings.FeedbackCancel);
        _cancel.Name = "buttonFeedbackCancel";
        _cancel.Click += (_, _) => Close();
        _send = CreateButton(Properties.Strings.FeedbackSend);
        _send.Name = "buttonFeedbackSend";
        _send.Click += async (_, _) => await SendAsync();
        UiVisualStyle.ApplyPrimaryButton(_send);
        actions.Controls.Add(_cancel, 0, 0);
        actions.Controls.Add(_send, 1, 0);
        root.Controls.Add(actions, 0, 2);

        CancelButton = _cancel;
        ActiveControl = _message;
        UiVisualStyle.ApplyWindow(this);
        // 输入框的滚动条跟随主题（InitTheme 在子控件建好之前跑过，这里补一次窗口主题），颜色仍用 Input 令牌。
        foreach (RTextBox box in new[] { _message, _contact })
        {
            box.ApplyTheme(darkTheme);
            box.BackColor = UiVisualStyle.Input;
            box.ForeColor = UiVisualStyle.Text;
        }
        header.BackColor = UiVisualStyle.Surface;
        actions.BackColor = UiVisualStyle.Surface;
        UiVisualStyle.ApplyPrimaryButton(_send);
        ResponsiveLayout.ScaleFrom96(this, this);

        FormClosing += (_, _) =>
        {
            try { _sending?.Cancel(); } catch (ObjectDisposedException) { }
        };
    }

    internal async Task SendAsync()
    {
        if (_sent)
        {
            Close();
            return;
        }
        string message = _message.Text;
        if (!FeedbackUpload.IsSendable(message))
        {
            ShowStatus(Properties.Strings.FeedbackTooShort, UiVisualStyle.Danger);
            _message.Focus();
            return;
        }

        SetInputsEnabled(false);
        ShowStatus(Properties.Strings.FeedbackSending, UiVisualStyle.Muted);
        using var sending = new CancellationTokenSource();
        _sending = sending;
        FeedbackResult result;
        try
        {
            result = SendOverride is { } send
                ? await send(message, _contact.Text, _attach.Checked, sending.Token)
                : await FeedbackUpload.SendAsync(message, _contact.Text, _attach.Checked, sending.Token);
        }
        catch (OperationCanceledException)
        {
            return;   // 窗口在发送途中被关掉
        }
        finally
        {
            _sending = null;
        }
        if (IsDisposed) return;

        if (result.Ok)
        {
            _sent = true;
            ShowStatus(string.Format(Properties.Strings.FeedbackSent, result.Ticket), UiVisualStyle.Ok);
            _send.Text = Properties.Strings.FeedbackClose;
            _send.Enabled = true;
            _cancel.Enabled = false;
            ToastForm.ShowNotice(string.Format(Properties.Strings.FeedbackSent, result.Ticket));
            return;
        }
        ShowStatus(string.Format(Properties.Strings.FeedbackFailed, result.Error), UiVisualStyle.Danger);
        SetInputsEnabled(true);
    }

    /// <summary>当前状态行（测试读取）。</summary>
    internal string StatusText => _status.Text;

    internal void SetInputsForTests(string message, string? contact, bool attach)
    {
        _message.Text = message;
        _contact.Text = contact ?? "";
        _attach.Checked = attach;
    }

    void SetInputsEnabled(bool enabled)
    {
        _message.ReadOnly = !enabled;
        _contact.ReadOnly = !enabled;
        _attach.Enabled = enabled;
        _send.Enabled = enabled;
    }

    void ShowStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }

    static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Body, FontStyle.Bold),
        ForeColor = UiVisualStyle.Text,
        Margin = Padding.Empty,
    };

    /// <summary>随父容器宽度折行的标签（与首次引导的描述同一做法：AutoSize + 动态 MaximumSize）。</summary>
    static Label WrappingLabel(string text, float size, Color color, Control host)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Font = UiVisualStyle.Font(size),
            ForeColor = color,
            Margin = Padding.Empty,
        };
        bool reflowing = false;
        void Reflow()
        {
            if (reflowing) return;
            int width = Math.Max(40, host.ClientSize.Width - host.Padding.Horizontal - label.Margin.Horizontal - 2);
            if (label.MaximumSize.Width == width) return;
            reflowing = true;
            try { label.MaximumSize = new Size(width, 0); }
            finally { reflowing = false; }
        }
        host.Layout += (_, _) => Reflow();
        host.Resize += (_, _) => Reflow();
        label.FontChanged += (_, _) => Reflow();
        return label;
    }

    static RButton CreateButton(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        Margin = new Padding(UiVisualStyle.Space.Xs, 0, UiVisualStyle.Space.Xs, 0),
        Cursor = Cursors.Hand,
        BorderRadius = 4,
        Secondary = true,
    };
}
