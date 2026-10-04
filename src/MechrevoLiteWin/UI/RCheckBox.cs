using System.Drawing.Drawing2D;

namespace MechrevoLite.UI
{
    /// <summary>
    /// 开关外观的复选框（DESIGN.md 第 4 节「开关」）。保持 CheckBox 基类是为了让既有
    /// AutoSize / CheckedChanged 接线零改动，绘制完全自管。
    /// </summary>
    public class RCheckBox : CheckBox
    {
        private const int TrackWidth = 38;
        private const int TrackHeight = 20;
        private const int InlineTrackWidth = 30;
        private const int InlineTrackHeight = 16;
        private const int KnobDiameter = 12;
        private const int TextGap = 8;
        private const int StateGap = 6;
        private const int FocusRadius = 6;
        private const int SlideMilliseconds = 120;

        private static readonly Color KnobOnColor = Color.White;

        private readonly System.Windows.Forms.Timer slideTimer = new() { Interval = 16 };

        private int borderRadius = 5;
        private bool switchOnRight;
        private bool showStateText;
        private float knobPosition;
        private float slideFrom;
        private long slideStarted;
        private Font? stateFont;

        public RCheckBox()
        {
            DoubleBuffered = true;
            SetStyle(
                ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
                true);
            knobPosition = Checked ? 1F : 0F;
            slideTimer.Tick += OnSlideTick;
        }

        public int BorderRadius
        {
            get => borderRadius;
            set => borderRadius = value;
        }

        public bool SwitchOnRight
        {
            get => switchOnRight;
            set
            {
                if (switchOnRight == value) return;
                switchOnRight = value;
                ResizeForAutoSize();
                Invalidate();
            }
        }

        public bool ShowStateText
        {
            get => showStateText;
            set
            {
                if (showStateText == value) return;
                showStateText = value;
                ResizeForAutoSize();
                Invalidate();
            }
        }

        private Font StateFont => stateFont ??= new Font("Consolas", Math.Max(6F, Font.Size - 1F));

        private TextFormatFlags LabelFlags
        {
            get
            {
                TextFormatFlags flags = TextFormatFlags.SingleLine;
                if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
                return flags;
            }
        }

        public override Size GetPreferredSize(Size proposedSize) => MeasurePreferredSize(ResponsiveLayout.LogicalToDevice(this, 96));

        protected override void OnPaint(PaintEventArgs pevent)
        {
            if (Appearance != System.Windows.Forms.Appearance.Normal)
            {
                base.OnPaint(pevent);
                return;
            }

            Graphics g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var background = new SolidBrush(BackColor))
                g.FillRectangle(background, ClientRectangle);

            float scale = UiDpi.Paint(g) / 96F;
            Size trackSize = TrackSize(scale);
            Size stateSize = ShowStateText ? MeasureText("OFF", StateFont) : Size.Empty;
            int gap = (int)Math.Round(TextGap * scale);
            int stateGap = (int)Math.Round(StateGap * scale);
            bool hasState = stateSize.Width > 0;

            // SwitchOnRight=false 时开关占原勾选框的位置，所以 ON/OFF 只能画在它前面；
            // SwitchOnRight=true 时文字在左，ON/OFF 紧贴开关左侧。
            int trackLeft = SwitchOnRight ? Width - trackSize.Width : hasState ? stateSize.Width + stateGap : 0;
            Rectangle track = new(trackLeft, (Height - trackSize.Height) / 2, trackSize.Width, trackSize.Height);
            Rectangle state = hasState
                ? new Rectangle(SwitchOnRight ? trackLeft - stateGap - stateSize.Width : 0, 0, stateSize.Width, Height)
                : Rectangle.Empty;
            int labelLeft = SwitchOnRight ? 0 : trackLeft + trackSize.Width + gap;
            int labelRight = SwitchOnRight ? (hasState ? state.Left : trackLeft) - gap : Width;
            Rectangle label = new(labelLeft, 0, Math.Max(0, labelRight - labelLeft), Height);

            DrawText(g, label, Text, Font, Enabled ? UiVisualStyle.Text : UiVisualStyle.Muted);
            if (hasState)
                DrawText(g, state, Checked ? "ON" : "OFF", StateFont,
                    Enabled && Checked ? UiVisualStyle.Accent : UiVisualStyle.Muted);

            DrawSwitch(g, track);
            if (Focused) DrawFocusRing(g, scale);
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            StartSlide();
            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            stateFont?.Dispose();
            stateFont = null;
            ResizeForAutoSize();
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                slideTimer.Stop();
                slideTimer.Dispose();
                stateFont?.Dispose();
            }

            base.Dispose(disposing);
        }

        internal Size MeasurePreferredSize(int dpi)
        {
            float scale = dpi / 96F;
            Size track = TrackSize(scale);
            Size label = MeasureText(Text, Font);
            Size state = ShowStateText ? MeasureText("OFF", StateFont) : Size.Empty;
            int width = track.Width + label.Width;
            if (label.Width > 0) width += (int)Math.Round(TextGap * scale);
            int height = Math.Max(track.Height, Math.Max(label.Height, state.Height));
            if (state.Width > 0)
            {
                width += state.Width + (int)Math.Round(StateGap * scale);
                height = Math.Max(height, state.Height);
            }

            return new Size(width, height);
        }

        private Size TrackSize(float scale) => new(
            (int)Math.Round((SwitchOnRight ? TrackWidth : InlineTrackWidth) * scale),
            (int)Math.Round((SwitchOnRight ? TrackHeight : InlineTrackHeight) * scale));

        private Size MeasureText(string? text, Font font) =>
            string.IsNullOrEmpty(text)
                ? Size.Empty
                : TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), LabelFlags);

        private void DrawText(Graphics g, Rectangle bounds, string? text, Font font, Color color)
        {
            if (string.IsNullOrEmpty(text) || bounds.Width <= 0) return;
            TextRenderer.DrawText(g, text, font, bounds, color,
                LabelFlags | TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        private void DrawSwitch(Graphics g, Rectangle track)
        {
            int radius = track.Height / 2;
            using (GraphicsPath trackPath = RComboBox.RoundedRect(track, radius, radius))
            using (var trackBrush = new SolidBrush(UiVisualStyle.Input))
                g.FillPath(trackBrush, trackPath);

            // 填充宽度跟随滑块位置：动画中途是渐进的强调色，两端分别等于「全关」「全开」。
            int fillWidth = (int)Math.Round(knobPosition * track.Width);
            if (fillWidth >= track.Height)
            {
                using GraphicsPath fillPath = RComboBox.RoundedRect(new Rectangle(track.X, track.Y, fillWidth, track.Height), radius, radius);
                using var fillBrush = new SolidBrush(Enabled ? UiVisualStyle.Accent : UiVisualStyle.Muted);
                g.FillPath(fillBrush, fillPath);
            }

            Rectangle borderRect = Rectangle.Inflate(track, -1, -1);
            int borderRadius = Math.Max(1, radius - 1);
            using (GraphicsPath borderPath = RComboBox.RoundedRect(borderRect, borderRadius, borderRadius))
            using (var borderPen = new Pen(UiVisualStyle.Border))
                g.DrawPath(borderPen, borderPath);

            int knob = Math.Max(4, (int)Math.Round(track.Height * (KnobDiameter / (float)TrackHeight)));
            int inset = (track.Height - knob) / 2;
            float travel = track.Width - 2 * inset - knob;
            float knobCenter = track.X + inset + knob / 2F + knobPosition * travel;
            using var knobBrush = new SolidBrush(Checked ? (Enabled ? KnobOnColor : UiVisualStyle.Surface) : UiVisualStyle.Muted);
            g.FillEllipse(knobBrush, knobCenter - knob / 2F, track.Y + inset, knob, knob);
        }

        private void DrawFocusRing(Graphics g, float scale)
        {
            int radius = Math.Max(1, Math.Min((int)Math.Round(FocusRadius * scale), (Height - 1) / 2));
            using GraphicsPath path = RComboBox.RoundedRect(new Rectangle(1, 1, Width - 3, Height - 3), radius, radius);
            using var pen = new Pen(UiVisualStyle.Accent);
            g.DrawPath(pen, path);
        }

        private void ResizeForAutoSize()
        {
            if (AutoSize) Size = GetPreferredSize(Size.Empty);
        }

        private void StartSlide()
        {
            float target = Checked ? 1F : 0F;
            // 句柄未创建或不可见时没有消息循环推进动画，直接落到终点，避免停在半路。
            if (!IsHandleCreated || !Visible || knobPosition == target)
            {
                slideTimer.Stop();
                knobPosition = target;
                return;
            }

            slideFrom = knobPosition;
            slideStarted = Environment.TickCount64;
            slideTimer.Stop();
            slideTimer.Start();
        }

        private void OnSlideTick(object? sender, EventArgs e)
        {
            float progress = Math.Min(1F, (Environment.TickCount64 - slideStarted) / (float)SlideMilliseconds);
            float eased = 1F - (1F - progress) * (1F - progress);
            float target = Checked ? 1F : 0F;
            knobPosition = slideFrom + (target - slideFrom) * eased;
            if (progress >= 1F)
            {
                knobPosition = target;
                slideTimer.Stop();
            }

            Invalidate();
        }
    }
}
