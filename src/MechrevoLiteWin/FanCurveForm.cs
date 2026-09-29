// TODO(nullable-migration): 这个文件继承自 g-helper 的 UI/原生互操作代码，尚未完成可空性标注。
// 项目已开启完整的可空性检查（csproj 里的 Nullable=enable），核心硬件层与 Helpers 均已清零；
// 这里显式关闭，是为了让剩余债务可见且局部化，而不是靠项目级 annotations 把它藏起来。
// 迁移某个文件时删掉下面这行（保留注解上下文，只关闭警告），然后把该文件的 CS86xx 告警修干净即可。
#nullable disable warnings
using MechrevoLite.Hardware;
using MechrevoLite.Properties;
using MechrevoLite.UI;

namespace MechrevoLite;

/// <summary>
/// 风扇曲线编辑器（原版 CustomModeSettingPage 协议移植）：
/// 16 个固定温度档的占空比曲线点，鼠标上下拖动实时保存（SET_FAN_SPEED_CURVE_SETTING T0-T15）。
/// 打开时请求 GET_FAN_SPEED_CURVE_SETTING 回读当前档曲线。
/// </summary>
public class FanCurveForm : RForm
{
    CurvePanel _cpuPanel = null!, _gpuPanel = null!;
    Label _status = null!;
    RCheckBox? _respectiveChk;
    Label? _respectiveHint;
    bool _syncingRespective;
    bool _shown;
    bool _initialIndependentControlRequested;
    readonly System.Windows.Forms.Timer _saveTimer;   // 拖动防抖：150ms 合并一次下发
    bool _cpuPending, _gpuPending;
    bool _saveInProgress;

    internal static bool ShouldRequestIndependentControlOnOpen(bool supported, bool alreadyEnabled) =>
        supported && !alreadyEnabled;

    int D(int value) => ResponsiveLayout.LogicalToDevice(this, value);

    public FanCurveForm()
    {
        BackColor = UiVisualStyle.Window;
        ForeColor = UiVisualStyle.Text;
        Text = Strings.FanCurve;
        // 纵排后内容在 420 主窗宽内放得下；可缩放 + AutoScroll 是小屏钳制时的退路，不是把 FixedSingle 改小后裁切。
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        AutoScroll = true;
        StartPosition = FormStartPosition.Manual;
        InitTheme(true);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = UiVisualStyle.Window,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        // 标题两行：标题+状态，拖动提示换行。三列横排在 420 主窗里放不下。
        // 行数与放入的行一致，避免 ColumnCount 对不上时 WinForms 多出幻影空行。
        int parentWidth = D(SettingsForm.CompactDashboardLogicalClientSize.Width);
        var titleFlow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = UiVisualStyle.Window,
            Padding = new Padding(D(12), D(8), D(12), 0),
        };
        titleFlow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titleFlow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleFlow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        titleFlow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var titleLabel = new Label { Text = Strings.FanCurveTitle, Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Title, FontStyle.Bold), ForeColor = UiVisualStyle.Text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, D(12), 0) };
        titleFlow.Controls.Add(titleLabel, 0, 0);
        // 状态与常驻提示拆两个 Label（run4 §3.3 语义拆分）：_status 只装动态状态
        //（表名/保存回显），拖动提示是常驻文案，不再被状态覆盖。
        _status = new Label { Text = "", ForeColor = UiVisualStyle.Muted, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, D(2), 0, 0) };
        titleFlow.Controls.Add(_status, 1, 0);
        var hint = new Label
        {
            Text = Strings.FanCurveDrag,
            ForeColor = UiVisualStyle.Muted,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, D(2), 0, 0),
            MaximumSize = new Size(Math.Max(1, parentWidth - D(24)), 0),
        };
        titleFlow.SetColumnSpan(hint, 2);
        titleFlow.Controls.Add(hint, 0, 1);
        root.Controls.Add(titleFlow, 0, 0);

        _saveTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await FlushPendingAsync();
        };

        // 高度自适应：固定 48 在 200% 缩放下装不下换行后的说明文字（提示语会折到第二行），
        // 超出部分被父容器裁掉——`--ui-audit` 的 parent-overflow 报的就是它。MinimumSize
        // 保住原来的视觉高度。
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            BackColor = UiVisualStyle.Window,
            Padding = new Padding(D(12), 0, D(12), D(8)),
            MinimumSize = new Size(0, D(40)),
        };
        var btnSave = new RButton
        {
            Text = Strings.Save,
            Width = D(88), Height = D(30), Margin = new Padding(0, 0, D(12), 0),
            Cursor = Cursors.Hand,
        };
        btnSave.Click += async (_, _) => await SendBoth();
        bottom.Controls.Add(btnSave);

        // 风扇独立控制（原版 SET_FAN_CONTROL_RESPECTIVE）：关=双风扇共用 GPU 曲线，开=CPU/GPU 各自独立
        _respectiveChk = new RCheckBox
        {
            Text = Strings.FanIndependent,
            Checked = Program.hw?.FanRespective ?? false,
            ForeColor = UiVisualStyle.Text,
            AutoSize = true,
            Margin = new Padding(0, D(4), D(12), 0),
            Cursor = Cursors.Hand,
        };
        _respectiveChk.CheckedChanged += async (_, _) =>
        {
            if (_syncingRespective) return;
            if (Program.service is null) return;
            bool requested = _respectiveChk.Checked;
            _respectiveChk.Enabled = false;
            bool confirmed = await Program.service.SwitchFanRespective(requested);
            if (!confirmed)
            {
                _syncingRespective = true;
                _respectiveChk.Checked = Program.hw?.FanRespective ?? !requested;
                _syncingRespective = false;
                _status.Text = Strings.FanIndependentUnconfirmed;
            }
            _respectiveChk.Enabled = true;
        };
        bottom.Controls.Add(_respectiveChk);
        _respectiveHint = new Label
        {
            Text = Strings.FanIndependentHint,
            ForeColor = UiVisualStyle.Muted,
            AutoSize = true, Margin = new Padding(0, D(4), 0, 0),
            Font = UiVisualStyle.Font(UiVisualStyle.TypeScale.Caption),
        };
        bottom.Controls.Add(_respectiveHint);
        // 曲线最小尺寸由窗口尺寸保证（FixedSingle 不可调 + 下方按 MinLogical* 计算）；
        // 不在面板上设 MinimumSize——设备像素下限不随审计的 form.Scale 缩放，会把面板顶出单元格。
        _cpuPanel = new CurvePanel("CPU", UiVisualStyle.Accent, () => { _cpuPending = true; _saveTimer.Stop(); _saveTimer.Start(); })
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(D(12), 0, D(12), 0),
        };

        _gpuPanel = new CurvePanel("GPU", UiVisualStyle.Danger, () => { _gpuPending = true; _saveTimer.Stop(); _saveTimer.Start(); })
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(D(12), 0, D(12), 0),
        };

        // 纵排（G-Helper tableFanCharts：CPU 上、GPU 下）。横排两张 340 图要 728 宽，
        // 缩到并排则 16 个拖点重叠。纵排后每张图拿到整列，仍宽于 MinLogicalWidth。
        var charts = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiVisualStyle.Window,
        };
        charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        charts.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        charts.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        charts.Controls.Add(_cpuPanel, 0, 0);
        charts.Controls.Add(_gpuPanel, 0, 1);
        root.Controls.Add(charts, 0, 1);
        root.Controls.Add(bottom, 0, 2);

        if (Program.hw is not null)
        {
            Program.hw.CurveUpdated += OnCurveUpdated;
            Program.hw.CustomModeChanged += OnCustomChanged;
            FormClosed += (_, _) =>
            {
                Program.hw.CurveUpdated -= OnCurveUpdated;
                Program.hw.CustomModeChanged -= OnCustomChanged;
                _saveTimer.Stop();
                _saveTimer.Dispose();
            };
        }
        else
        {
            FormClosed += (_, _) =>
            {
                _saveTimer.Stop();
                _saveTimer.Dispose();
            };
        }
        OnCurveUpdated();
        OnCustomChanged();

        Shown += async (_, _) =>
        {
            _shown = true;
            // 请求当前档曲线（服务端切档后已推，重发确保最新）
            if (Program.hw is { IsConnected: true })
            {
                try
                {
                    await Program.hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GET_FAN_SPEED_CURVE_SETTING" });
                    await EnableIndependentControlOnOpenAsync();
                }
                catch (Exception ex) { Logger.WriteLine("Fan curve initial refresh failed: " + ex.Message); }
            }
        };
        UiVisualStyle.ApplyWindow(this);
        UiVisualStyle.ApplyTitle((Label)titleFlow.Controls[0], UiVisualStyle.TypeScale.Title);
        UiVisualStyle.ApplyMuted(_status);
        UiVisualStyle.ApplyPrimaryButton(btnSave);

        // 宽对齐主窗（420 逻辑）。先定宽让底行换行，再按标题 + 两张曲线最小高 + 底行定高。
        // 高不超过主窗；超出部分由 AutoScroll 承接，不裁切曲线。
        // D() 已是设备像素，不再 ScaleFrom96（会叠乘宿主 DPI）。
        int width = parentWidth;
        MinimumSize = new Size(D(320), D(240));
        if (!IsHandleCreated) CreateControl();
        ClientSize = new Size(width, ClientSize.Height);
        ResponsiveLayout.PerformLayoutTree(this);
        int height = titleFlow.Height + 2 * D(CurvePanel.MinLogicalHeight) + bottom.Height + D(2);
        int parentHeight = D(SettingsForm.CompactDashboardLogicalClientSize.Height);
        ClientSize = new Size(width, Math.Min(height, parentHeight));
    }

    void OnCurveUpdated()
    {
        try
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(OnCurveUpdated); return; }
            var hw = Program.hw;
            if (hw is null) return;
            _cpuPanel.Load(hw.CpuCurveUpT, hw.CpuCurveDuty);
            _gpuPanel.Load(hw.GpuCurveUpT, hw.GpuCurveDuty);
            _status.Text = Strings.FanTablePrefix + (string.IsNullOrEmpty(hw.TableName) ? Strings.UnknownValue : hw.TableName);
        }
        catch (Exception ex) { Logger.WriteLine("Fan curve refresh failed: " + ex.GetType().Name + " " + ex.Message); }
    }

    int _customChangedQueued;   // 1 = 已有一次回显在 UI 队列里

    void OnCustomChanged()
    {
        try
        {
            if (IsDisposed || _respectiveChk is null) return;
            if (InvokeRequired)
            {
                // 每帧状态都会发这个事件：已有一次在排队就合并（执行时读的就是最新值）。
                if (Interlocked.Exchange(ref _customChangedQueued, 1) != 0) return;
                BeginInvoke(() =>
                {
                    Interlocked.Exchange(ref _customChangedQueued, 0);
                    OnCustomChanged();
                });
                return;
            }
            _syncingRespective = true;
            _respectiveChk.Checked = Program.hw?.FanRespective ?? false;
            _syncingRespective = false;
            bool supported = Program.UiAuditMode || Program.hw?.SupportsFanRespective == true;
            _respectiveChk.Visible = supported;
            if (_respectiveHint is not null) _respectiveHint.Visible = supported;
            // 打开时没能判定（当时未连接）才在这里补一次；设备本来就是「独立」时销掉这次机会，
            // 否则用户手动关掉之后，下一帧状态会被当成「首次打开」再打开一次。
            if (_shown && !_initialIndependentControlRequested &&
                Program.hw is { IsConnected: true, SupportsFanRespective: true } respective)
            {
                if (ShouldRequestIndependentControlOnOpen(true, respective.FanRespective))
                    _ = EnableIndependentControlOnOpenAsync();
                else
                    _initialIndependentControlRequested = true;
            }
        }
        catch (Exception ex) { Logger.WriteLine("Fan respective UI sync failed: " + ex.GetType().Name + " " + ex.Message); }
    }

    async Task EnableIndependentControlOnOpenAsync()
    {
        if (_initialIndependentControlRequested || _respectiveChk is null ||
            Program.hw is not { IsConnected: true } hw || Program.service is null)
            return;

        // 一次性机会在判定之前就销掉：设备本来就是「独立」时不下发，但若留着这次机会，
        // 用户之后手动关掉会被下一帧状态当成「首次打开」再打开一次（实测：关掉 235ms 后弹回）。
        _initialIndependentControlRequested = true;
        if (!ShouldRequestIndependentControlOnOpen(hw.SupportsFanRespective, hw.FanRespective)) return;

        _respectiveChk.Enabled = false;
            _status.Text = Strings.FanIndependentEnabling;
        bool confirmed = await Program.service.SwitchFanRespective(true);
        if (IsDisposed || _respectiveChk is null) return;

        _syncingRespective = true;
        _respectiveChk.Checked = Program.hw?.FanRespective ?? false;
        _syncingRespective = false;
        _respectiveChk.Enabled = true;
            _status.Text = confirmed ? Strings.FanIndependentOn : Strings.FanIndependentUnconfirmed;
    }

    async Task SendBoth()
    {
        _cpuPending = _gpuPending = true;
        await FlushPendingAsync();
    }

    async Task FlushPendingAsync()
    {
        if (_saveInProgress) return;
        if (Program.hw is not { IsConnected: true })
        {
            _status.Text = Strings.NotConnectedNotSaved;
            return;
        }

        _saveInProgress = true;
        try
        {
            while (_cpuPending || _gpuPending)
            {
                bool sendCpu = _cpuPending, sendGpu = _gpuPending;
                _cpuPending = _gpuPending = false;
                int[] cpu = _cpuPanel.Duties.ToArray();
                int[] gpu = _gpuPanel.Duties.ToArray();
                if (sendCpu) await Program.hw.SetFanCurve(0, cpu);
                if (sendGpu) await Program.hw.SetFanCurve(1, gpu);
                await Program.hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GET_FAN_SPEED_CURVE_SETTING" });
                await Task.Delay(700);
                bool confirmed = (!sendCpu || CurveMatches(cpu, Program.hw.CpuCurveDuty, Program.hw.CpuCurveUpT))
                    && (!sendGpu || CurveMatches(gpu, Program.hw.GpuCurveDuty, Program.hw.GpuCurveUpT));
                _status.Text = confirmed ? string.Format(Strings.ConfirmedAt, DateTime.Now.ToString("HH:mm:ss")) : Strings.SaveUnconfirmedRetry;
                // 曲线记进当前模式：档位被轮转复用后重新装入，也还是这条曲线。
                if (confirmed)
                    MechrevoLite.Mode.PerfModeService.Instance?.RecordFanCurves(sendCpu ? cpu : null, sendGpu ? gpu : null);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Fan curve save failed: " + ex.Message);
            _status.Text = Strings.SaveFailed;
        }
        finally
        {
            _saveInProgress = false;
            if (_cpuPending || _gpuPending) _saveTimer.Start();
        }
    }

    static bool CurveMatches(int[] requested, byte[] actual, byte[] temperatures)
    {
        int[] expected = MechrevoHw.NormalizeFanCurve(requested, temperatures);
        int valid = Array.FindIndex(temperatures, value => value == 255);
        if (valid < 2) valid = Math.Min(11, actual.Length);
        return Enumerable.Range(0, valid).All(i => actual[i] == expected[i]);
    }

    /// <summary>曲线面板：16 点折线 + 可上下拖动的点（Y=占空比 0-100%）。</summary>
    sealed class CurvePanel : Panel
    {
        /// <summary>曲线区最小尺寸（逻辑 px）：16 点可分辨间距 + 轴标签/标题带。</summary>
        internal const int MinLogicalWidth = 340;
        internal const int MinLogicalHeight = 170;

        static readonly Font SmallFont = UiVisualStyle.Font(UiVisualStyle.TypeScale.Subtitle);
        static readonly Font TitleFont = UiVisualStyle.Font(UiVisualStyle.TypeScale.Title, FontStyle.Bold);
        readonly string _label;
        readonly Color _color;
        readonly int[] _duties = new int[16];
        readonly byte[] _temps = new byte[16];
        int _valid = 16;         // 有效点个数（温度 255 哨兵前的个数，无有效数据时 16）
        int _dragIdx = -1;
        readonly Action _changed;

        public int[] Duties => _duties;

        public CurvePanel(string label, Color color, Action changed)
        {
            _label = label;
            _color = color;
            _changed = changed;
            BackColor = UiVisualStyle.Input;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            for (int i = 0; i < 16; i++) _duties[i] = 0;
        }

        public void Load(byte[] temps, byte[] duties)
        {
            int valid = 16;
            for (int i = 0; i < 16; i++)
            {
                _temps[i] = i < temps.Length ? temps[i] : (byte)0;
                int d = i < duties.Length ? duties[i] : 0;
                _duties[i] = d == 255 ? 0 : Math.Clamp(d, 0, 100);
                if (i > 0 && valid == 16 && _temps[i] == 255) valid = i;
            }
            _valid = valid == 16 ? 16 : Math.Max(valid, 1);
            Invalidate();
        }

        // 绘制/命中共用几何：轴带随 DPI 缩放（此前是裸设备像素，175% 下字体放大而边带不变，
        // 图区比例失衡）。左 44（Y 标签）右 12 上 26（标题带）下 22（温度标签带），逻辑 px。
        (int left, int w, int h, int top, int bottom) Geo()
        {
            float scale = UiDpi.Paint(this) / 96F;
            int left = (int)Math.Round(44 * scale), right = (int)Math.Round(12 * scale);
            int top = (int)Math.Round(26 * scale), bottomPad = (int)Math.Round(22 * scale);
            return (left, Width - left - right, Height - top - bottomPad, top, top + Height - top - bottomPad);
        }

        int X(int i, int left, int w) => left + (i == 0 ? 0 : w * i / Math.Max(1, _valid - 1));
        int Y(int duty, int h, int bottom) => bottom - h * duty / 100;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var mutedBrush = new SolidBrush(UiVisualStyle.Muted);
            var (left, w, h, top, bottom) = Geo();

            // 网格：Y 每 25%（0/25/50/75/100），带横贯虚线
            using var gridPen = new Pen(UiVisualStyle.Track, 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            using var axisPen = new Pen(UiVisualStyle.Border, 1f);
            for (int pct = 0; pct <= 100; pct += 25)
            {
                int y = Y(pct, h, bottom);
                g.DrawLine(pct == 0 || pct == 100 ? axisPen : gridPen, left, y, left + w, y);
                var size = g.MeasureString(pct + "%", SmallFont);
                g.DrawString(pct + "%", SmallFont, mutedBrush, left - size.Width - 8, y - size.Height / 2);
            }
            // 垂直网格仅绘制服务端声明的有效温度档。
            for (int i = 0; i < _valid; i++)
            {
                int x = X(i, left, w);
                g.DrawLine(gridPen, x, top, x, bottom);
                if (i < _temps.Length && _temps[i] != 255 && _temps[i] != 0)
                {
                    var size = g.MeasureString(_temps[i].ToString(), SmallFont);
                    g.DrawString(_temps[i].ToString(), SmallFont, mutedBrush, x - size.Width / 2, bottom + 8);
                }
            }
            g.DrawLine(axisPen, left, bottom, left + w, bottom);   // X 轴

            using var titleBrush = new SolidBrush(_color);
            g.DrawString(string.Format(Strings.FanCurvePaint, _label), TitleFont, titleBrush, left, 3 * UiDpi.Paint(this) / 96F);

            // 折线 + 点
            var pts = new Point[_valid];
            for (int i = 0; i < _valid; i++)
                pts[i] = new Point(X(i, left, w), Y(_duties[i], h, bottom));
            using var pen = new Pen(_color, 2.5f);
            for (int i = 0; i < pts.Length - 1; i++)
                g.DrawLine(pen, pts[i], pts[i + 1]);
            using var brush = new SolidBrush(_color);
            foreach (var p in pts)
                g.FillEllipse(brush, p.X - 6, p.Y - 6, 12, 12);
            if (_dragIdx >= 0)
            {
                var p = pts[_dragIdx];
                using var sel = new Pen(UiVisualStyle.Text, 2.5f);
                g.DrawEllipse(sel, p.X - 10, p.Y - 10, 20, 20);
                // 拖拽提示泡：抬升面底 + 正文色字 + 边框描边（日夜都按 token 走）
                var size = g.MeasureString(_duties[_dragIdx] + "%", SmallFont);
                using var tipBack = new SolidBrush(UiVisualStyle.SurfaceRaised);
                using var tipBorder = new Pen(UiVisualStyle.Border, 1f);
                g.FillRectangle(tipBack, p.X - size.Width / 2 - 4, p.Y - 26, size.Width + 8, size.Height + 4);
                g.DrawRectangle(tipBorder, p.X - size.Width / 2 - 4, p.Y - 26, size.Width + 8, size.Height + 4);
                using var tipText = new SolidBrush(UiVisualStyle.Text);
                g.DrawString(_duties[_dragIdx] + "%", SmallFont, tipText, p.X - size.Width / 2, p.Y - 25);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var (left, w, h, top, bottom) = Geo();
            double best = double.MaxValue;
            for (int i = 0; i < _valid; i++)
            {
                int px = X(i, left, w), py = Y(_duties[i], h, bottom);
                double d = Math.Sqrt((e.X - px) * (e.X - px) + (e.Y - py) * (e.Y - py));
                if (d < best) { best = d; _dragIdx = i; }
            }
            if (best > 12 * UiDpi.Paint(this) / 96F) _dragIdx = -1;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragIdx < 0) return;
            var (_, _, h, _, bottom) = Geo();
            int v = Math.Clamp((bottom - e.Y) * 100 / h, 0, 100);
            if (v == _duties[_dragIdx]) return;
            _duties[_dragIdx] = v;
            int[] safe = MechrevoHw.NormalizeFanCurve(_duties, _temps);
            Array.Copy(safe, _duties, _duties.Length);
            Invalidate();
            _changed();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragIdx >= 0) { _dragIdx = -1; Invalidate(); _changed(); }
        }
    }
}
