using System.Drawing.Drawing2D;

namespace MechrevoLite.UI;

/// <summary>
/// 卡片标题的线性图标（DESIGN.md §8 硬禁令：无 emoji 图标，统一线性线条）。
///
/// 原实现是 icons8 的彩色位图资源，只有 4 张卡有、另外 6 张没有，既违反"统一线性"，
/// 也让标题行一半带图一半不带。这里改为 GDI+ 现画的单色线稿：颜色随主题走，
/// 尺寸按调用方给的像素边长绘制，所以夜/日两套主题和高 DPI 都自动适配。
/// </summary>
internal static class UiGlyph
{
    internal enum Kind
    {
        Gauge,      // 性能模式
        VideoCard,  // 显卡模式
        Battery,    // 电池
        Display,    // 屏幕
        Toggles,    // 快捷开关
        Droplet,    // 液冷
        Refresh,    // 刷新率
        Contrast,   // 主题
        Console,    // 官方控制台
        Info,       // 版本
        Overlay,    // 悬浮窗（footer，预览 ◎ 同心圆）
        Heart,      // 赞助（footer）
        Close,      // 退出（footer，预览 ✕）
        Gear,       // 设置 / 灯光组（预览圆 + 8 向辐条）
        Chevrons,   // 更多开关组（预览 < >）
        Package,    // 导出诊断包（盒 + 拉链线）
    }

    /// <param name="gap">图标右侧留白（给 Label.Image：Label 没有图文间距属性，只能画进位图里）。</param>
    internal static Bitmap Render(Kind kind, int size, Color color, int gap = 0)
    {
        int side = Math.Max(8, size);
        var bitmap = new Bitmap(side + Math.Max(0, gap), side);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float stroke = Math.Max(1F, side / 12F);
        using var pen = new Pen(color, stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        using var brush = new SolidBrush(color);

        float m = stroke * 1.6F;
        float w = side - m * 2F;          // 绘制区边长
        float x = m;
        float y = m;
        float cx = x + w / 2F;
        float cy = y + w / 2F;

        switch (kind)
        {
            case Kind.Gauge:
                // 表盘下半弧 + 指针
                g.DrawArc(pen, x, y, w, w, 200F, 140F);
                g.DrawLine(pen, cx, cy + w * 0.18F, x + w * 0.74F, y + w * 0.30F);
                g.FillEllipse(brush, cx - stroke, cy + w * 0.18F - stroke, stroke * 2F, stroke * 2F);
                break;

            case Kind.VideoCard:
            {
                // 显卡：板 + 风扇 + 金手指
                float h = w * 0.72F;
                float top = cy - h / 2F;
                using GraphicsPath card = RoundedRect(new RectangleF(x, top, w, h), stroke * 1.4F);
                g.DrawPath(pen, card);
                float r = h * 0.26F;
                g.DrawEllipse(pen, cx - r, top + h * 0.5F - r, r * 2F, r * 2F);
                g.DrawLine(pen, x + w * 0.22F, top + h + stroke * 0.6F, x + w * 0.78F, top + h + stroke * 0.6F);
                break;
            }

            case Kind.Battery:
            {
                float h = w * 0.56F;
                float top = cy - h / 2F;
                using GraphicsPath body = RoundedRect(new RectangleF(x, top, w * 0.82F, h), stroke * 1.2F);
                g.DrawPath(pen, body);
                // 正极小凸起
                g.DrawLine(pen, x + w * 0.82F + stroke * 0.5F, cy - h * 0.16F, x + w * 0.82F + stroke * 0.5F, cy + h * 0.16F);
                // 电量条
                g.DrawLine(pen, x + w * 0.14F, cy, x + w * 0.5F, cy);
                break;
            }

            case Kind.Display:
            {
                float h = w * 0.66F;
                float top = cy - h * 0.72F;
                using GraphicsPath screen = RoundedRect(new RectangleF(x, top, w, h), stroke * 1.2F);
                g.DrawPath(pen, screen);
                g.DrawLine(pen, cx, top + h, cx, top + h + w * 0.16F);
                g.DrawLine(pen, cx - w * 0.22F, top + h + w * 0.16F, cx + w * 0.22F, top + h + w * 0.16F);
                break;
            }

            case Kind.Toggles:
            {
                // 两组开关：一个开、一个关
                float pillW = w * 0.66F;
                float pillH = w * 0.30F;
                float right = x + w;
                using GraphicsPath top1 = RoundedRect(new RectangleF(x, y + w * 0.04F, pillW, pillH), pillH / 2F);
                g.DrawPath(pen, top1);
                float knob = pillH * 0.66F;
                g.FillEllipse(brush, x + pillW - knob - pillH * 0.17F, y + w * 0.04F + (pillH - knob) / 2F, knob, knob);

                using GraphicsPath bottom = RoundedRect(new RectangleF(right - pillW, y + w * 0.56F, pillW, pillH), pillH / 2F);
                g.DrawPath(pen, bottom);
                g.FillEllipse(brush, right - pillW + pillH * 0.17F, y + w * 0.56F + (pillH - knob) / 2F, knob, knob);
                break;
            }

            case Kind.Droplet:
            {
                using GraphicsPath drop = new();
                drop.AddBezier(cx, y + w * 0.08F, cx + w * 0.46F, y + w * 0.46F, x + w * 0.88F, y + w * 0.62F, x + w * 0.62F, y + w * 0.86F);
                drop.AddBezier(x + w * 0.62F, y + w * 0.86F, x + w * 0.34F, y + w * 1.06F, x + w * 0.12F, y + w * 0.66F, x + w * 0.38F, y + w * 0.44F);
                drop.AddBezier(x + w * 0.38F, y + w * 0.44F, cx - w * 0.08F, y + w * 0.28F, cx - w * 0.06F, y + w * 0.16F, cx, y + w * 0.08F);
                drop.CloseFigure();
                g.DrawPath(pen, drop);
                break;
            }

            case Kind.Refresh:
                g.DrawArc(pen, x, y, w, w, 60F, 280F);
                // 箭头
                g.DrawLine(pen, x + w * 0.86F, y + w * 0.12F, x + w * 0.96F, y + w * 0.36F);
                g.DrawLine(pen, x + w * 0.62F, y + w * 0.16F, x + w * 0.9F, y + w * 0.14F);
                break;

            case Kind.Overlay:
                // 同心圆 ◎（预览 footer 悬浮窗图标）
                g.DrawEllipse(pen, x, y, w, w);
                g.DrawEllipse(pen, cx - w * 0.17F, cy - w * 0.17F, w * 0.34F, w * 0.34F);
                break;

            case Kind.Heart:
            {
                // 心形（预览 footer 赞助图标）：两瓣贝塞尔 + 底部尖点
                using GraphicsPath heart = new();
                float t = y + w * 0.06F;
                float b = y + w * 0.96F;
                heart.AddBezier(cx, b, x + w * 0.06F, t + w * 0.62F, x + w * 0.02F, t, x + w * 0.27F, t);
                heart.AddBezier(x + w * 0.27F, t, x + w * 0.44F, t, cx, t + w * 0.16F, cx, t + w * 0.34F);
                heart.AddBezier(cx, t + w * 0.34F, cx, t + w * 0.16F, x + w * 0.56F, t, x + w * 0.73F, t);
                heart.AddBezier(x + w * 0.73F, t, x + w * 0.98F, t, x + w * 0.94F, t + w * 0.62F, cx, b);
                heart.CloseFigure();
                g.DrawPath(pen, heart);
                break;
            }

            case Kind.Close:
                // ✕（预览 footer 退出图标）；0.18..0.82 与相邻图标（◎/♥，约 0.85w）的光学大小对齐
                //（此前 0.24..0.76 只有邻居一半大，真机看着像没对齐）。
                g.DrawLine(pen, x + w * 0.18F, y + w * 0.18F, x + w * 0.82F, y + w * 0.82F);
                g.DrawLine(pen, x + w * 0.82F, y + w * 0.18F, x + w * 0.18F, y + w * 0.82F);
                break;

            case Kind.Gear:
            {
                // 圆 + 8 向辐条（预览 footer 设置图标）
                g.DrawEllipse(pen, cx - w * 0.20F, cy - w * 0.20F, w * 0.40F, w * 0.40F);
                float rIn = w * 0.33F;
                float rOut = w * 0.50F;
                for (int i = 0; i < 8; i++)
                {
                    double angle = Math.PI / 4 * i;
                    g.DrawLine(pen,
                        cx + (float)(Math.Cos(angle) * rIn), cy + (float)(Math.Sin(angle) * rIn),
                        cx + (float)(Math.Cos(angle) * rOut), cy + (float)(Math.Sin(angle) * rOut));
                }
                break;
            }

            case Kind.Chevrons:
            {
                // < >（预览「更多开关」组图标）
                using GraphicsPath left = new();
                left.AddLines(new[]
                {
                    new PointF(x + w * 0.36F, y + w * 0.10F),
                    new PointF(x + w * 0.08F, cy),
                    new PointF(x + w * 0.36F, y + w * 0.90F),
                });
                g.DrawPath(pen, left);
                using GraphicsPath right = new();
                right.AddLines(new[]
                {
                    new PointF(x + w * 0.64F, y + w * 0.10F),
                    new PointF(x + w * 0.92F, cy),
                    new PointF(x + w * 0.64F, y + w * 0.90F),
                });
                g.DrawPath(pen, right);
                break;
            }

            case Kind.Contrast:
            {
                g.DrawEllipse(pen, x, y, w, w);
                using GraphicsPath half = new();
                half.AddArc(x, y, w, w, -90F, 180F);
                half.CloseFigure();
                using var fill = new SolidBrush(Color.FromArgb(90, color));
                g.FillPath(fill, half);
                break;
            }

            case Kind.Console:
            {
                float h = w * 0.74F;
                float top = cy - h / 2F;
                using GraphicsPath box = RoundedRect(new RectangleF(x, top, w, h), stroke * 1.4F);
                g.DrawPath(pen, box);
                float line = top + h * 0.32F;
                g.DrawLine(pen, x + w * 0.12F, line, x + w * 0.84F, line);
                g.DrawLine(pen, x + w * 0.2F, top + h * 0.62F, x + w * 0.36F, top + h * 0.62F);
                g.DrawLine(pen, x + w * 0.46F, top + h * 0.62F, x + w * 0.7F, top + h * 0.62F);
                break;
            }

            case Kind.Package:
            {
                // 盒 + 拉链线（导出诊断包）
                float h = w * 0.72F;
                float top = cy - h / 2F;
                using GraphicsPath box = RoundedRect(new RectangleF(x, top, w, h), stroke * 1.4F);
                g.DrawPath(pen, box);
                g.DrawLine(pen, x, top + h * 0.30F, x + w, top + h * 0.30F);
                g.DrawLine(pen, cx, top + h * 0.30F, cx, top + h);
                g.DrawLine(pen, cx - w * 0.16F, top + h * 0.44F, cx + w * 0.16F, top + h * 0.44F);
                break;
            }

            default:   // Info
            {
                g.DrawEllipse(pen, x, y, w, w);
                g.FillEllipse(brush, cx - stroke, y + w * 0.24F - stroke, stroke * 2F, stroke * 2F);
                g.DrawLine(pen, cx, y + w * 0.42F, cx, y + w * 0.76F);
                break;
            }
        }

        return bitmap;
    }

    static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        float d = Math.Min(radius * 2F, Math.Min(rect.Width, rect.Height));
        GraphicsPath path = new();
        path.AddArc(rect.X, rect.Y, d, d, 180F, 90F);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270F, 90F);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0F, 90F);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90F, 90F);
        path.CloseFigure();
        return path;
    }
}
