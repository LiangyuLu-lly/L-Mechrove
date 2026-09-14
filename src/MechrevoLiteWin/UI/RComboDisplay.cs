using System.Drawing.Drawing2D;

namespace MechrevoLite.UI;

/// <summary>
/// 只读「下拉框」外观（预览 .combo）：Input 底 + 1px Border + 6 逻辑 px 圆角 + 右端 ▾。
/// 灯光组的效果读数用它替裸 Label——预览里那一列是带框的下拉形态，裸文本在 420 宽下
/// 看起来像丢控件。继承 Label 以复用现有字段类型与文本同步（_kbEffectLabel 等均为 Label?）。
/// </summary>
internal sealed class RComboDisplay : Label
{
    public RComboDisplay()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float ratio = UiDpi.Paint(e.Graphics) / 192.0f;
        int radius = (int)Math.Round(ratio * 12);   // 6 逻辑 px
        int padX = (int)Math.Round(ratio * 16);     // 8 逻辑 px
        int caretW = (int)Math.Round(ratio * 14);   // ~7 逻辑 px
        int caretH = (int)Math.Round(ratio * 8);    // ~4 逻辑 px

        Rectangle rect = ClientRectangle;
        rect.Width -= 1;
        rect.Height -= 1;
        if (rect.Width <= 4 || rect.Height <= 4) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
        using (GraphicsPath path = RoundedPath(rect, r))
        {
            using (var fill = new SolidBrush(UiVisualStyle.Input))
                e.Graphics.FillPath(fill, path);
            using var border = new Pen(UiVisualStyle.Border, 1f);
            e.Graphics.DrawPath(border, path);
        }

        // 右端下拉箭头（预览 .combo .c：9px muted ▾，用形状画，避免字形依赖）
        int caretX = rect.Right - padX - caretW;
        int caretY = rect.Top + (rect.Height - caretH) / 2;
        using (var caret = new SolidBrush(UiVisualStyle.Muted))
        {
            e.Graphics.FillPolygon(caret, new[]
            {
                new Point(caretX, caretY),
                new Point(caretX + caretW, caretY),
                new Point(caretX + caretW / 2, caretY + caretH),
            });
        }

        Rectangle textRect = new(rect.Left + padX, rect.Top, Math.Max(0, rect.Width - padX * 2 - caretW), rect.Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, textRect, ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    static GraphicsPath RoundedPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
