using System.Drawing.Drawing2D;

namespace MechrevoLite.UI
{
    /// <summary>
    /// 液冷「灯光」下拉菜单（设计稿 .combo / .mini 语言）：圆角、令牌化配色、分组头部。
    /// 继承 <see cref="CustomContextMenu"/> 复用其 DWM 圆角；渲染器独立成类，
    /// 不改动托盘菜单共享的 <see cref="CustomMenuRenderer"/>（其悬停色是另一套语义）。
    /// </summary>
    internal sealed class LiquidCoolingLightMenu : CustomContextMenu
    {
        /// <summary>分组头部行的 Tag 标记：渲染器据此不画悬停、文字取 Muted、字号降到 Caption。</summary>
        public const string HeaderTag = "lc-header";

        readonly List<ToolStripMenuItem> _fanLedItems = new();

        public LiquidCoolingLightMenu()
        {
            ShowImageMargin = false;
            Renderer = new LiquidCoolingMenuRenderer();
            ApplyTheme();
        }

        /// <summary>依赖 Mk2 风扇灯的能力项（打开菜单时按能力启用/禁用）。</summary>
        public IReadOnlyList<ToolStripMenuItem> FanLedItems => _fanLedItems;

        /// <summary>分组标题：不可点击、渲染器不画悬停、Muted 文字。</summary>
        public void AddHeader(string text)
        {
            Items.Add(new ToolStripMenuItem(text)
            {
                Tag = HeaderTag,
                Enabled = false,
                AutoSize = true,
                Padding = new Padding(0, 4, 0, 4),
            });
        }

        /// <summary>普通灯效项；<paramref name="requiresFanLed"/> 为真的项进入 <see cref="FanLedItems"/>。</summary>
        public ToolStripMenuItem AddItem(string text, Action onClick, bool requiresFanLed = false)
        {
            var item = new ToolStripMenuItem(text)
            {
                AutoSize = true,
                Padding = new Padding(0, 7, 0, 7),   // 高约 28-34 逻辑 px（设计稿 .combo 的 26 + 上下留白）
            };
            item.Click += (_, _) => onClick();
            Items.Add(item);
            if (requiresFanLed) _fanLedItems.Add(item);
            return item;
        }

        public void AddSeparator() => Items.Add(new ToolStripSeparator());

        /// <summary>日夜主题切换后重读调色板（渲染器按绘制时取令牌，这里刷新底色/前景并重绘）。</summary>
        public void ApplyTheme()
        {
            BackColor = UiVisualStyle.Input;
            ForeColor = UiVisualStyle.Text;
            Invalidate();
        }
    }

    /// <summary>
    /// 液冷灯光菜单渲染器：6 逻辑 px 圆角悬停块 + 分组头部 + 1px 令牌化描边/分隔线。
    /// 所有颜色在绘制时从 <see cref="UiVisualStyle"/> 读取，故日夜主题切换无需额外刷新。
    /// </summary>
    internal sealed class LiquidCoolingMenuRenderer : ToolStripProfessionalRenderer
    {
        public LiquidCoolingMenuRenderer() : base(new ProfessionalColorTable()) { }

        // UiDpi.Paint/192 的半值约定：半径/间距用 2×逻辑 px 表达（同 RComboDisplay 的 ratio*12 = 6 逻辑 px）。
        static int HalfLogicalPx(Graphics graphics, int logicalPx) =>
            (int)Math.Round(UiDpi.Paint(graphics) / 192f * (logicalPx * 2));

        static GraphicsPath RoundedPath(Rectangle rect, int radius)
        {
            int r = Math.Max(1, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2));
            var path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0) return path;
            path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
            path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
            path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            return path;
        }

        static bool IsHeader(ToolStripItem item) => Equals(item.Tag, LiquidCoolingLightMenu.HeaderTag);

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(UiVisualStyle.Input);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // 抑制左侧图标留白的默认浅色条（与 CustomMenuRenderer 同一处理）。
            using var brush = new SolidBrush(UiVisualStyle.Input);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var bounds = new Rectangle(Point.Empty, e.Item.Size);
            using (var fill = new SolidBrush(UiVisualStyle.Input))
                e.Graphics.FillRectangle(fill, bounds);

            if (IsHeader(e.Item) || !e.Item.Enabled) return;      // 头部/禁用项不画悬停
            if (!e.Item.Selected && !e.Item.Pressed) return;

            Color hoverColor = e.Item.Pressed ? UiVisualStyle.Border : UiVisualStyle.SurfaceRaised;
            var hover = Rectangle.Inflate(bounds, -2, -1);
            using var path = RoundedPath(hover, HalfLogicalPx(e.Graphics, 6));
            using var brush2 = new SolidBrush(hoverColor);
            var old = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush2, path);
            e.Graphics.SmoothingMode = old;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            bool header = IsHeader(e.Item);
            if (header)
            {
                e.TextColor = UiVisualStyle.Muted;
                e.TextFont = UiVisualStyle.Font(UiVisualStyle.TypeScale.Caption);
            }
            else
            {
                e.TextColor = e.Item.Enabled ? UiVisualStyle.Text : UiVisualStyle.Muted;
            }
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int pad = HalfLogicalPx(e.Graphics, 8);
            int y = e.Item.Height / 2;
            using var pen = new Pen(UiVisualStyle.Border);
            e.Graphics.DrawLine(pen, pad, y, Math.Max(pad, e.Item.Width - pad), y);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var bounds = new Rectangle(Point.Empty, e.ToolStrip.Size);
            if (bounds.Width <= 2 || bounds.Height <= 2) return;
            using var path = RoundedPath(Rectangle.Inflate(bounds, -1, -1), HalfLogicalPx(e.Graphics, 6));
            using var pen = new Pen(UiVisualStyle.Border);
            var old = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(pen, path);
            e.Graphics.SmoothingMode = old;
        }
    }
}
