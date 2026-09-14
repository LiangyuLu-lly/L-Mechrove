using System.Drawing.Drawing2D;

namespace MechrevoLite.UI
{
    /// <summary>
    /// 全自绘水平滑条（DESIGN.md 第 4 节「滑条」）。只取 TrackBar 的常用子集，
    /// 避免原生控件在暗色主题下露出系统配色。
    /// </summary>
    public class RSlider : Control
    {
        private const int TrackThickness = 4;
        private const int ThumbDiameter = 14;
        private const int ThumbRing = 3;
        private const int FocusRadius = 6;

        /// <summary>最短可用轨道（逻辑 px）：拇指直径 ×2 + 最小拖动行程；布局据此留宽。</summary>
        internal const int MinTrackLogicalWidth = 104;

        private static readonly Color ThumbFill = Color.FromArgb(0xE9, 0xF1, 0xFC);

        private int minimum;
        private int maximum = 100;
        private int currentValue;
        private int smallChange = 1;
        private int largeChange = 10;
        private bool dragging;

        public RSlider()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable,
                true);
            TabStop = true;
            AccessibleRole = AccessibleRole.Slider;
            Height = 28;
            MinimumSize = new Size(MinTrackLogicalWidth, 20);
        }

        public event EventHandler? ValueChanged;

        public int Minimum
        {
            get => minimum;
            set
            {
                if (minimum == value) return;
                minimum = value;
                if (maximum < minimum) maximum = minimum;   // 反向区间会让比例除零，顶高另一端而不是抛异常
                ApplyValue(currentValue);
                Invalidate();
            }
        }

        public int Maximum
        {
            get => maximum;
            set
            {
                if (maximum == value) return;
                maximum = value;
                if (minimum > maximum) minimum = maximum;
                ApplyValue(currentValue);
                Invalidate();
            }
        }

        public int Value
        {
            get => currentValue;
            set => ApplyValue(value);
        }

        public int SmallChange
        {
            get => smallChange;
            set => smallChange = Math.Max(0, value);
        }

        public int LargeChange
        {
            get => largeChange;
            set => largeChange = Math.Max(0, value);
        }

        // 方向键默认会被窗体当成焦点导航键吃掉，必须显式声明为输入键。
        protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) switch
        {
            Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown => true,
            _ => base.IsInputKey(keyData),
        };

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
            Announce($"{AccessibleName}, slider, {Value}");
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Left or Keys.Down: ApplyValue(currentValue - smallChange); break;
                case Keys.Right or Keys.Up: ApplyValue(currentValue + smallChange); break;
                case Keys.PageUp: ApplyValue(currentValue + largeChange); break;
                case Keys.PageDown: ApplyValue(currentValue - largeChange); break;
                case Keys.Home: ApplyValue(minimum); break;
                case Keys.End: ApplyValue(maximum); break;
                default: return;
            }

            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            dragging = true;
            Capture = true;
            ApplyValueAt(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) ApplyValueAt(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !dragging) return;
            dragging = false;
            Capture = false;
            ApplyValueAt(e.X);
        }

        // 捕获被别处抢走（弹窗、Alt+Tab）时结束拖动，否则无按键移动鼠标也会继续改值。
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) dragging = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (e.Delta == 0) return;
            ApplyValue(currentValue + (e.Delta > 0 ? smallChange : -smallChange));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var background = new SolidBrush(BackColor))
                g.FillRectangle(background, ClientRectangle);

            float scale = UiDpi.Paint(g) / 96F;
            int trackSize = Math.Max(2, (int)Math.Round(TrackThickness * scale));
            int thumbSize = Math.Max(6, (int)Math.Round(ThumbDiameter * scale));
            int ring = Math.Max(1, (int)Math.Round(ThumbRing * scale));

            float left = thumbSize / 2F;
            float right = Width - thumbSize / 2F;
            if (right <= left) right = left + 1F;
            float ratio = maximum > minimum ? (currentValue - minimum) / (float)(maximum - minimum) : 0F;
            float centerX = left + ratio * (right - left);

            Color filled = Enabled ? UiVisualStyle.Accent : UiVisualStyle.Muted;
            int trackRadius = trackSize / 2;
            int trackTop = (Height - trackSize) / 2;
            int trackX = (int)Math.Round(left - trackRadius);
            int trackWidth = (int)Math.Round(right - left) + trackSize;

            using (GraphicsPath trackPath = RComboBox.RoundedRect(new Rectangle(trackX, trackTop, trackWidth, trackSize), trackRadius, trackRadius))
            using (var trackBrush = new SolidBrush(UiVisualStyle.Track))
                g.FillPath(trackBrush, trackPath);

            int fillWidth = (int)Math.Round(centerX - left) + trackRadius;
            if (fillWidth >= trackSize)
            {
                using GraphicsPath fillPath = RComboBox.RoundedRect(new Rectangle(trackX, trackTop, fillWidth, trackSize), trackRadius, trackRadius);
                using var fillBrush = new SolidBrush(filled);
                g.FillPath(fillBrush, fillPath);
            }

            float thumbTop = (Height - thumbSize) / 2F;
            using (var thumbRingBrush = new SolidBrush(filled))
                g.FillEllipse(thumbRingBrush, centerX - thumbSize / 2F, thumbTop, thumbSize, thumbSize);
            int innerSize = thumbSize - 2 * ring;
            if (innerSize > 0)
            {
                using var thumbBrush = new SolidBrush(Enabled ? ThumbFill : UiVisualStyle.Surface);
                g.FillEllipse(thumbBrush, centerX - innerSize / 2F, thumbTop + ring, innerSize, innerSize);
            }

            if (Focused)
            {
                int radius = Math.Max(1, Math.Min((int)Math.Round(FocusRadius * scale), (Height - 1) / 2));
                using GraphicsPath focusPath = RComboBox.RoundedRect(new Rectangle(1, 1, Width - 3, Height - 3), radius, radius);
                using var focusPen = new Pen(UiVisualStyle.Accent);
                g.DrawPath(focusPen, focusPath);
            }
        }

        private void ApplyValue(int raw)
        {
            int clamped = Math.Clamp(raw, minimum, maximum);
            if (currentValue == clamped) return;
            currentValue = clamped;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
            if (Focused) Announce(clamped.ToString());
        }

        private void ApplyValueAt(int x)
        {
            float scale = UiDpi.Paint(this) / 96F;
            float left = Math.Max(1F, ThumbDiameter * scale / 2F);
            float right = Width - left;
            float ratio = right > left ? Math.Clamp((x - left) / (right - left), 0F, 1F) : 0F;
            ApplyValue(minimum + (int)Math.Round(ratio * (maximum - minimum)));
        }

        private void Announce(string text)
        {
            try
            {
                AccessibilityObject.RaiseAutomationNotification(
                    System.Windows.Forms.Automation.AutomationNotificationKind.Other,
                    System.Windows.Forms.Automation.AutomationNotificationProcessing.MostRecent,
                    text);
            }
            catch { }
        }
    }
}
