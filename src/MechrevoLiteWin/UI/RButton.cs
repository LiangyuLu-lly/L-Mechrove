// TODO(nullable-migration): 这个文件继承自 g-helper 的 UI/原生互操作代码，尚未完成可空性标注。
// 项目已开启完整的可空性检查（csproj 里的 Nullable=enable），核心硬件层与 Helpers 均已清零；
// 这里显式关闭，是为了让剩余债务可见且局部化，而不是靠项目级 annotations 把它藏起来。
// 迁移某个文件时删掉下面这行（保留注解上下文，只关闭警告），然后把该文件的 CS86xx 告警修干净即可。
#nullable disable warnings
using System.Drawing.Drawing2D;

namespace MechrevoLite.UI
{
    /// <summary>分段按钮在一整条分段控件里的位置（决定圆角与描边画在哪几条边）。</summary>
    public enum RSegmentPosition
    {
        None,
        First,
        Middle,
        Last,
        Single,
    }

    /// <summary>
    /// 扁平圆角按钮（深潜座舱语言）：层次只用「面 + 1px 描边」，无渐变、无阴影。
    /// 颜色全部由外部（UiVisualStyle / 调用点）通过 BackColor/ForeColor/BorderColor 驱动：
    /// Activated 状态由调用点设置 Accent 底 + AccentText 字，控件只负责形状与边线。
    /// </summary>
    public class RButton : Button
    {
        // Design tokens
        private const float HoverShiftAmount = 0.06f;

        private int borderSize = 3;

        private int borderRadius = 8;
        public int BorderRadius
        {
            get { return borderRadius; }
            set { borderRadius = value; }
        }

        private Color borderColor = Color.Transparent;
        public Color BorderColor
        {
            get { return borderColor; }
            set { borderColor = value; }
        }

        private bool activated = false;
        public bool Activated
        {
            get { return activated; }
            set
            {
                if (activated != value)
                    Invalidate();
                activated = value;
                AccessibleDescription = activated ? "Active" : null;
            }
        }

        /// <summary>分段控件中的位置。非 None 时按「整条轨道」画边：外沿圆角 + 内部 1px 分隔线。</summary>
        private RSegmentPosition segmentPosition = RSegmentPosition.None;
        public RSegmentPosition SegmentPosition
        {
            get { return segmentPosition; }
            set
            {
                if (segmentPosition != value)
                    Invalidate();
                segmentPosition = value;
            }
        }

        private bool secondary = false;
        public bool Secondary
        {
            get { return secondary; }
            set { secondary = value; }
        }

        public bool Borderless { get; set; } = false;

        protected override bool ShowFocusCues => false;

        public RButton()
        {
            DoubleBuffered = true;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColorChanged += (s, e) => UpdateHoverColor();
            UpdateHoverColor();
        }

        private void UpdateHoverColor()
        {
            int lum = (BackColor.R * 30 + BackColor.G * 59 + BackColor.B * 11) / 100;
            Color target = lum > 128 ? Color.Black : Color.White;
            FlatAppearance.MouseOverBackColor = Shift(BackColor, target, HoverShiftAmount);
            FlatAppearance.MouseDownBackColor = Shift(BackColor, target, HoverShiftAmount * 2f);
        }

        private static Color Shift(Color from, Color target, float amount)
        {
            return Color.FromArgb(from.A,
                (int)(from.R + (target.R - from.R) * amount),
                (int)(from.G + (target.G - from.G) * amount),
                (int)(from.B + (target.B - from.B) * amount));
        }

        private static GraphicsPath GetFigurePath(Rectangle rect, int radius, RSegmentPosition position)
        {
            GraphicsPath path = new GraphicsPath();
            float curve = Math.Max(1, radius * 2F);
            bool roundLeft = position is RSegmentPosition.None or RSegmentPosition.First or RSegmentPosition.Single;
            bool roundRight = position is RSegmentPosition.None or RSegmentPosition.Last or RSegmentPosition.Single;

            path.StartFigure();
            if (roundLeft) path.AddArc(rect.X, rect.Y, curve, curve, 180, 90);
            else path.AddLine(rect.X, rect.Y, rect.X, rect.Y);
            path.AddLine(roundLeft ? rect.X + (int)(curve / 2) : rect.X, rect.Y, rect.Right - (roundRight ? (int)(curve / 2) : 0), rect.Y);
            if (roundRight) path.AddArc(rect.Right - curve, rect.Y, curve, curve, 270, 90);
            path.AddLine(rect.Right, roundRight ? rect.Y + (int)(curve / 2) : rect.Y, rect.Right, rect.Bottom - (roundRight ? (int)(curve / 2) : 0));
            if (roundRight) path.AddArc(rect.Right - curve, rect.Bottom - curve, curve, curve, 0, 90);
            path.AddLine(rect.Right - (roundRight ? (int)(curve / 2) : 0), rect.Bottom, rect.X + (roundLeft ? (int)(curve / 2) : 0), rect.Bottom);
            if (roundLeft) path.AddArc(rect.X, rect.Bottom - curve, curve, curve, 90, 90);
            path.AddLine(rect.X, roundLeft ? rect.Bottom - (int)(curve / 2) : rect.Bottom, rect.X, roundLeft ? rect.Y + (int)(curve / 2) : rect.Y);
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// 分段边线：外沿段画外框三边 + 外角弧线；中段画顶/底描边（跨段连续）；
        /// 内分隔线上下内缩（预览 .seg 的 5px），且与选中段相邻时隐藏
        ///（预览 .on::before / :has(+.on)::before）。相邻段各画一条线拼成整条轨道边框。
        /// </summary>
        private static void DrawSegmentBorder(Graphics g, Rectangle rect, int radius, Color color, RSegmentPosition position,
            int separatorInset, bool suppressSeparator)
        {
            using Pen pen = new(color, 1f);
            pen.Alignment = PenAlignment.Inset;
            int w = rect.Width - 1;
            int h = rect.Height - 1;
            int r = Math.Min(radius, Math.Min(w, h) / 2);
            switch (position)
            {
                case RSegmentPosition.First:
                    g.DrawLine(pen, rect.X, rect.Y + r, rect.X, rect.Bottom - r);           // 左边
                    g.DrawLine(pen, rect.X + r, rect.Y, rect.Right - 1, rect.Y);            // 顶边
                    g.DrawLine(pen, rect.X + r, rect.Bottom - 1, rect.Right - 1, rect.Bottom - 1); // 底边
                    g.DrawArc(pen, rect.X, rect.Y, r * 2, r * 2, 180, 90);
                    g.DrawArc(pen, rect.X, rect.Bottom - 1 - r * 2, r * 2, r * 2, 90, 90);
                    break;
                case RSegmentPosition.Middle:
                    g.DrawLine(pen, rect.X, rect.Y, rect.Right - 1, rect.Y);                 // 顶边
                    g.DrawLine(pen, rect.X, rect.Bottom - 1, rect.Right - 1, rect.Bottom - 1); // 底边
                    if (!suppressSeparator)
                        g.DrawLine(pen, rect.X, rect.Y + separatorInset, rect.X, rect.Bottom - separatorInset); // 内缩分隔线
                    break;
                case RSegmentPosition.Last:
                    g.DrawLine(pen, rect.Right - 1, rect.Y + r, rect.Right - 1, rect.Bottom - r); // 右边
                    g.DrawLine(pen, rect.X, rect.Y, rect.Right - r, rect.Y);                 // 顶边
                    g.DrawLine(pen, rect.X, rect.Bottom - 1, rect.Right - r, rect.Bottom - 1);// 底边
                    g.DrawArc(pen, rect.Right - 1 - r * 2, rect.Y, r * 2, r * 2, 270, 90);
                    g.DrawArc(pen, rect.Right - 1 - r * 2, rect.Bottom - 1 - r * 2, r * 2, r * 2, 0, 90);
                    break;
                case RSegmentPosition.Single:
                default:
                    using (GraphicsPath path = GetFigurePath(new Rectangle(rect.X, rect.Y, w, h), r, RSegmentPosition.None))
                        g.DrawPath(pen, path);
                    break;
            }
        }

        bool _hover;

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            if (segmentPosition != RSegmentPosition.None) Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            if (segmentPosition != RSegmentPosition.None) Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            if (segmentPosition != RSegmentPosition.None) Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            float ratio = UiDpi.Paint(pevent.Graphics) / 192.0f;
            int border = (int)Math.Round((ratio * borderSize - 1) / 2) * 2 + 1;
            int radius = (int)Math.Round(ratio * borderRadius, MidpointRounding.AwayFromZero);
            Rectangle rectSurface = ClientRectangle;
            bool segment = segmentPosition != RSegmentPosition.None;

            if (!segment)
            {
                base.OnPaint(pevent);

                using GraphicsPath pathSurface = GetFigurePath(rectSurface, radius + border, segmentPosition);
                using (Pen penSurface = new(Parent?.BackColor ?? UiVisualStyle.Window, border))
                {
                    pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    Region = new Region(pathSurface);
                    pevent.Graphics.DrawPath(penSurface, pathSurface);

                    if (Enabled && !Borderless && borderColor.A > 0)
                    {
                        int inset = border / 2 + 1;
                        Rectangle borderRect = new(inset, inset, rectSurface.Width - 2 * inset, rectSurface.Height - 2 * inset);
                        using GraphicsPath pathBorder = GetFigurePath(borderRect, radius, RSegmentPosition.None);
                        using Pen penBorder = new(borderColor, 1f);
                        penBorder.Alignment = PenAlignment.Center;
                        pevent.Graphics.DrawPath(penBorder, pathBorder);
                    }
                }

                if (!Enabled && ForeColor != SystemColors.ControlText)
                {
                    var rect = pevent.ClipRectangle;
                    if (Image is not null)
                    {
                        rect.Y += Image.Height;
                        rect.Height -= Image.Height;
                    }
                    else
                    {
                        using (var brush = new SolidBrush(Parent?.BackColor ?? UiVisualStyle.Window))
                            pevent.Graphics.FillRectangle(brush, rect);
                        using (var brush = new SolidBrush(BackColor))
                            pevent.Graphics.FillRectangle(brush, rect);
                    }
                    TextFormatFlags disabledFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak;
                    TextRenderer.DrawText(pevent.Graphics, Text, Font, rect, UiVisualStyle.Muted, disabledFlags);
                }
                return;
            }

            // 分段模式（预览 1:1）：整条轨道 = Input 底 + 1px Border 描边（跨段连续）；
            // 选中段 = 内缩的 Accent 圆角块（不再整格实底）；分隔线内缩且不与选中段相邻。
            // 颜色不经过 BackColor/BorderColor，状态由调用点既有的 Activated 驱动。
            int padInset = (int)Math.Round(ratio * 8);        // 4 逻辑 px = 1 描边 + 3 轨道内边距
            int separatorInset = (int)Math.Round(ratio * 10); // 5 逻辑 px
            int insetRadius = (int)Math.Round(ratio * 12);    // 6 逻辑 px
            bool prevActivated = false;
            if (Parent is TableLayoutPanel table)
            {
                TableLayoutPanelCellPosition pos = table.GetPositionFromControl(this);
                if (pos.Column > 0)
                    prevActivated = (table.GetControlFromPosition(pos.Column - 1, pos.Row) as RButton)?.Activated == true;
            }

            Color fill = !activated && _hover && Enabled
                ? Shift(UiVisualStyle.Input, Color.White, HoverShiftAmount)
                : UiVisualStyle.Input;
            Color textColor = !Enabled ? UiVisualStyle.Muted
                : activated ? UiVisualStyle.AccentText
                : UiVisualStyle.Text;

            using (GraphicsPath pathSurface = GetFigurePath(rectSurface, radius + border, segmentPosition))
            using (Pen penSurface = new(Parent?.BackColor ?? UiVisualStyle.Window, border))
            {
                pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Region = new Region(pathSurface);
                pevent.Graphics.DrawPath(penSurface, pathSurface);
                using (Brush brush = new SolidBrush(fill))
                    pevent.Graphics.FillPath(brush, pathSurface);
                DrawSegmentBorder(pevent.Graphics, rectSurface, radius, UiVisualStyle.Border, segmentPosition,
                    separatorInset, activated || prevActivated);
            }

            if (activated)
            {
                Rectangle inner = rectSurface;
                inner.Inflate(-padInset, -padInset);
                if (inner.Width > 4 && inner.Height > 4)
                {
                    using GraphicsPath pathInner = GetFigurePath(inner, insetRadius, RSegmentPosition.Single);
                    using Brush brushInner = new SolidBrush(UiVisualStyle.Accent);
                    pevent.Graphics.FillPath(brushInner, pathInner);
                }
            }

            Font textFont = activated ? UiVisualStyle.Font(Font.Size, FontStyle.Bold) : Font;
            TextRenderer.DrawText(pevent.Graphics, Text, textFont, rectSurface, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (activated && !ReferenceEquals(textFont, Font)) textFont.Dispose();
        }
    }
}
