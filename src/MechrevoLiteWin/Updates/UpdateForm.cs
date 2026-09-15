using MechrevoLite.Helpers;
using MechrevoLite.UI;
using System.Diagnostics;

namespace MechrevoLite.Update;

/// <summary>
/// 更新窗口：显示服务端版本与更新说明，提供"下载并安装 / 打开下载页 / 稍后"。
///
/// 两条硬纪律：
/// <list type="bullet">
/// <item>没有合法 sha256（或下载地址不在允许的 host 列表内）时**禁用自动安装**，
/// 只保留"打开下载页"手动路径——静态后端下没有校验值就无法确认来源与完整性；</item>
/// <item>安装前再确认一次，因为安装会关闭程序并重启——绝不静默替换 exe。</item>
/// </list>
/// </summary>
internal sealed class UpdateForm : RForm
{
    readonly Label _headline = new();
    readonly Label _detail = new();
    readonly RTextBox _notes = new();
    readonly Label _notesEmpty = new();
    readonly Label _status = new();
    readonly ProgressBar _progress = new();
    readonly RButton _install = new();
    readonly RButton _downloadPage = new();
    readonly RButton _later = new();
    readonly RButton _feedback = new();
    TableLayoutPanel _root = null!;
    int _designClientWidth, _designClientHeight;

    UpdateInfo? _info;
    bool _busy;

    internal UpdateForm(bool autoCheck)
    {
        Initialize();
        Shown += async (_, _) => await RefreshAsync(autoCheck);
    }

    /// <summary>审计/测试专用：以离线合成信息呈现，不再触网刷新。</summary>
    internal UpdateForm(UpdateInfo info)
    {
        Initialize();
        Apply(info, autoCheck: false);
    }

    void Initialize()
    {
        BackColor = UiVisualStyle.Window;
        ForeColor = UiVisualStyle.Text;
        Text = "检查更新";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 380);
        InitTheme(true);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(20, 16, 20, 16),
            BackColor = UiVisualStyle.Surface,
        };
        _root = root;
        Controls.Add(root);

        _headline.Text = "正在检查更新…";
        _headline.Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Title, FontStyle.Bold);
        _headline.ForeColor = UiVisualStyle.Text;
        _headline.AutoSize = true;
        _headline.Margin = new Padding(0, 0, 0, 6);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_headline, 0, 0);

        _detail.Text = "";
        _detail.AutoSize = true;
        _detail.Margin = new Padding(0, 0, 0, 8);
        UiVisualStyle.ApplyMuted(_detail);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_detail, 0, 1);

        _notes.Multiline = true;
        _notes.ReadOnly = true;
        _notes.ScrollBars = ScrollBars.Vertical;
        _notes.BackColor = UiVisualStyle.Input;
        _notes.ForeColor = UiVisualStyle.Text;
        _notes.Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Body);
        _notes.Dock = DockStyle.Fill;
        _notes.Margin = new Padding(0, 0, 0, 8);
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(_notes, 0, 2);

        // 空态（检查失败/已是最新）：不留空只读框，整行换成 Muted 文案并收口高度
        //（Empty States；行样式与窗口高度由 ApplyNotesState 切换）。
        _notesEmpty.Text = "暂无更新说明";
        _notesEmpty.ForeColor = UiVisualStyle.Muted;
        _notesEmpty.AutoSize = true;
        _notesEmpty.Margin = new Padding(0, 4, 0, 4);
        _notesEmpty.Visible = false;
        root.Controls.Add(_notesEmpty, 0, 2);

        _status.Text = "";
        _status.AutoSize = true;
        _status.Margin = new Padding(0, 0, 0, 8);
        UiVisualStyle.ApplyMuted(_status);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_status, 0, 3);

        _progress.Dock = DockStyle.Fill;
        _progress.Height = 6;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Visible = false;
        _progress.Margin = new Padding(0, 0, 0, 8);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_progress, 0, 4);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = Padding.Empty,
            BackColor = UiVisualStyle.Surface,
        };
        // 按钮宽度两档（run5 二级界面收尾）：主操作 96、次操作 88，间距统一 Space.Sm。
        _install.Text = "下载并安装";
        _install.AutoSize = false;
        _install.Size = new Size(96, 32);
        _install.Margin = new Padding(0, 0, UiVisualStyle.Space.Sm, 0);
        _install.Cursor = Cursors.Hand;
        _install.Click += async (_, _) => await StartInstallAsync();

        _downloadPage.Text = "打开下载页";
        _downloadPage.AutoSize = false;
        _downloadPage.Size = new Size(96, 32);
        _downloadPage.Margin = new Padding(0, 0, UiVisualStyle.Space.Sm, 0);
        _downloadPage.Cursor = Cursors.Hand;
        _downloadPage.Click += (_, _) => OpenDownloadPage();

        _later.Text = "稍后";
        _later.AutoSize = false;
        _later.Size = new Size(88, 32);
        _later.Margin = new Padding(0, 0, UiVisualStyle.Space.Sm, 0);
        _later.Cursor = Cursors.Hand;
        _later.Click += (_, _) => Close();

        _feedback.Text = "反馈";
        _feedback.AutoSize = false;
        _feedback.Size = new Size(88, 32);
        _feedback.Margin = Padding.Empty;
        _feedback.Cursor = Cursors.Hand;
        _feedback.Click += (_, _) => ShowFeedback();

        buttons.Controls.Add(_install);
        buttons.Controls.Add(_downloadPage);
        buttons.Controls.Add(_later);
        buttons.Controls.Add(_feedback);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(buttons, 0, 5);

        UiVisualStyle.ApplyWindow(this);
        UiVisualStyle.ApplySection(root);
        UiVisualStyle.ApplyTitle(_headline, UiVisualStyle.TypeScale.Title);
        ResponsiveLayout.ScaleFrom96(this, this);
        _designClientWidth = ClientSize.Width;
        _designClientHeight = ClientSize.Height;
        // 首帧之前完成空态收口：CreateControl 让句柄/布局就绪，否则 ApplyNotesState 会被
        // IsHandleCreated 守卫跳过，等到 OnShown（已可见）才真正收口——首帧先按设计高画再跳。
        CreateControl();
        ApplyNotesState();
    }

    protected override void OnShown(EventArgs e)
    {
        // 先按空态收口，再交给基类钳制工作区（顺序反了会把收口后的窗口再放大钳回）。
        ApplyNotesState();
        base.OnShown(e);
    }

    /// <summary>
    /// 空态切换：无更新说明时隐藏只读框、显示 Muted 文案、说明行改 AutoSize 并把窗口
    /// 高度收到内容实测值；有说明时恢复设计高度。设计尺寸在 Initialize 末尾捕获
    /// （已经过 ScaleFrom96，均为设备像素）。
    /// </summary>
    void ApplyNotesState()
    {
        bool hasNotes = !string.IsNullOrWhiteSpace(_notes.Text);
        _notes.Visible = hasNotes;
        _notesEmpty.Visible = !hasNotes;
        if (_root.RowStyles.Count > 2)
            _root.RowStyles[2] = hasNotes
                ? new RowStyle(SizeType.Percent, 100)
                : new RowStyle(SizeType.AutoSize);
        if (IsHandleCreated && !IsDisposed)
        {
            // GetRowHeights 在 Dock=Fill 的表里会把 AutoSize 行拉伸到撑满客户区，不能当
            // 内容高度用；GetPreferredSize 返回的是内容需求（隐藏控件不计入）。
            ResponsiveLayout.PerformLayoutTree(this);
            var preferred = _root.GetPreferredSize(new Size(_root.Width, 0));
            int contentHeight = preferred.Height + _root.Padding.Vertical;
            int targetHeight = hasNotes ? _designClientHeight : Math.Min(_designClientHeight, contentHeight);
            ClientSize = new Size(_designClientWidth, targetHeight);
        }
    }

    async Task RefreshAsync(bool autoCheck)
    {
        try
        {
            UpdateInfo? info = UpdateChecker.Cached ?? await UpdateChecker.CheckAsync(force: !autoCheck);
            Apply(info, autoCheck);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("更新窗口刷新失败：" + ex.Message);
            Apply(null, autoCheck);
        }
    }

    void Apply(UpdateInfo? info, bool autoCheck)
    {
        _info = info;
        if (info is null)
        {
            _headline.Text = "检查更新失败";
            _detail.Text = "无法连接到更新服务器（网络不可用或服务端异常）。";
            _notes.Text = "";
            ApplyNotesState();
            _status.Text = "";
            _install.Enabled = false;
            _downloadPage.Enabled = false;
            return;
        }

        _install.Enabled = false;
        _downloadPage.Enabled = !string.IsNullOrWhiteSpace(info.DownloadPage);
        _notes.Text = info.Notes ?? "";
        ApplyNotesState();

        if (!info.UpdateAvailable)
        {
            _headline.Text = "已是最新版本";
            _detail.Text = $"当前版本 {info.CurrentVersion}"
                + (string.IsNullOrWhiteSpace(info.LatestVersion) ? "" : $"，服务端最新 {info.LatestVersion}");
            _status.Text = "";
            return;
        }

        _headline.Text = $"发现新版本 {info.LatestVersion}";
        _detail.Text = $"当前版本 {info.CurrentVersion}"
            + (string.IsNullOrWhiteSpace(info.ReleaseDate) ? "" : $" · 发布于 {info.ReleaseDate}");

        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
        {
            _status.Text = "该版本在网盘发布，请点『打开下载页』手动下载。";
            _status.ForeColor = UiVisualStyle.Warn;
            _install.Enabled = false;
            return;
        }

        if (!UpdateInstaller.CanSelfInstall(out string selfInstallReason))
        {
            // 例如框架依赖构建：能下载但不能自替换，直接引导到下载页，别让用户点了报错。
            _install.Enabled = false;
            _status.Text = selfInstallReason;
            _status.ForeColor = UiVisualStyle.Warn;
            return;
        }

        // fail-closed：下载地址协议/host 白名单 + 必备 sha256 全部通过才允许自动安装。
        if (!UpdatePolicy.TryAcceptOffer(info, out string policyReason))
        {
            _install.Enabled = false;
            _status.Text = policyReason;
            _status.ForeColor = UiVisualStyle.Warn;
            Logger.WriteLine("更新窗口：自动安装被拒 —— " + policyReason);
            return;
        }

        _install.Enabled = true;
        _status.Text = "服务端提供了 SHA-256，下载后会自动校验。";
        _status.ForeColor = UiVisualStyle.Ok;
    }

    async Task StartInstallAsync()
    {
        if (_busy || _info is not { UpdateAvailable: true } info) return;

        // 与 UpdateForm.Apply 同一套判定；按钮正常不可达这里，作为兜底再拒一次。
        if (!UpdatePolicy.TryAcceptOffer(info, out string reason))
        {
            _status.Text = reason;
            _status.ForeColor = UiVisualStyle.Warn;
            Logger.WriteLine("更新窗口：安装被拒 —— " + reason);
            return;
        }

        if (!UpdateInstaller.CanSelfInstall(out string why))
        {
            MessageBox.Show(why + "\n\n将为你打开下载页。", "无法自动安装",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            OpenDownloadPage();
            return;
        }

        DialogResult confirm = MessageBox.Show(
            "安装会关闭本程序，替换当前 exe 后自动重启。\n\n继续吗？",
            "安装更新", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;

        SetBusy(true);
        try
        {
            var progress = new Progress<int>(p => _progress.Value = Math.Clamp(p, 0, 100));
            string? package = await UpdateInstaller.DownloadAsync(info, progress);
            if (package is null)
            {
                _status.Text = "下载失败。可以点『打开下载页』手动下载。";
                _status.ForeColor = UiVisualStyle.Danger;
                return;
            }

            PackageVerification verification = UpdateInstaller.Verify(package, info);
            if (!verification.Ok)
            {
                UpdateInstaller.DiscardPackage(package);
                _status.Text = "更新包未通过校验：" + verification.Reason;
                _status.ForeColor = UiVisualStyle.Danger;
                Logger.WriteLine("更新包校验失败：" + verification.Reason);
                return;
            }

            string? newExe = UpdateInstaller.ExtractPackage(package, info);
            if (newExe is null)
            {
                _status.Text = "更新包解压失败（没找到可执行文件）。";
                _status.ForeColor = UiVisualStyle.Danger;
                return;
            }

            string target = UpdateInstaller.CurrentExePath;
            if (!UpdateInstaller.StartUpdater(newExe, target))
            {
                _status.Text = "无法启动更新器（可能是当前目录不可写），请用『打开下载页』手动更新。";
                _status.ForeColor = UiVisualStyle.Danger;
                return;
            }

            _status.Text = "更新器已启动，程序即将退出…";
            _status.ForeColor = UiVisualStyle.Ok;
            Logger.WriteLine($"更新安装流程已启动：{newExe} -> {target}（{verification.Reason}）");
            await Task.Delay(600);
            Program.RequestShutdownForUpdate();
        }
        catch (Exception ex)
        {
            Logger.WriteLine("更新安装流程失败：" + ex.Message);
            _status.Text = "安装失败：" + ex.Message;
            _status.ForeColor = UiVisualStyle.Danger;
        }
        finally
        {
            SetBusy(false);
        }
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        _progress.Visible = busy;
        _progress.Value = 0;
        _install.Enabled = !busy;
        _downloadPage.Enabled = !busy && !string.IsNullOrWhiteSpace(_info?.DownloadPage);
        _later.Enabled = !busy;
        _feedback.Enabled = !busy;   // run5 收尾：下载互斥此前漏了反馈按钮
    }

    void OpenDownloadPage()
    {
        string? page = _info?.DownloadPage;
        if (string.IsNullOrWhiteSpace(page)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = page, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.WriteLine("打开下载页失败：" + ex.Message);
        }
    }

    static void ShowFeedback() => SettingsForm.ShowTestFeedback();
}
