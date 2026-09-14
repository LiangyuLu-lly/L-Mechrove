using System.Drawing.Drawing2D;

namespace MechrevoLite.UI;

/// <summary>
/// 主面板的深色细滚动条（预览 .scroll::-webkit-scrollbar：8px、透明轨道、Border 色圆角滑块）。
/// 原生 WinForms 滚动条由系统绘制，在深色面板上是一根白条（真机验收暴露），这里自绘替代。
/// 值语义与滚轮/拖动由宿主接线：宿主把 <see cref="Value"/> 应用到内容偏移。
/// </summary>
internal sealed class RScrollBar : Control
{
    int _value;
    int _max = 1;
    int _contentHeight = 1;
    int _viewHeight = 1;
    bool _dragging;
    int _dragStartY;
    int _dragStartValue;

    public event EventHandler? ValueChanged;

    public RScrollBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Visible = false;
        Cursor = Cursors.Hand;
        TabStop = false;
        BackColor = UiVisualStyle.Window;
    }

    /// <summary>内容/视口高度变化时重算范围；无溢出时自动隐藏（预览的滚动条只在需要时出现）。</summary>
    public void SetRange(int contentHeight, int viewHeight)
    {
        _contentHeight = Math.Max(1, contentHeight);
        _viewHeight = Math.Max(1, viewHeight);
        int max = Math.Max(0, _contentHeight - _viewHeight);
        _max = Math.Max(1, max);
        bool visible = max > 0;
        if (Visible != visible) Visible = visible;
        int clamped = Math.Clamp(_value, 0, max);
        bool changed = clamped != _value;
        _value = clamped;
        Invalidate();
        if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, 0, Math.Max(0, _contentHeight - _viewHeight));
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>一格滚轮/翻页步长：约 40 逻辑 px。</summary>
    public int Step => Math.Max(24, (int)Math.Round(UiDpi.Paint(this) / 96.0 * 40));

    int ThumbHeight(int track) =>
        Math.Max(track / 10, (int)((long)track * _viewHeight / Math.Max(1, _contentHeight)));

    protected override void OnPaint(PaintEventArgs e)
    {
        float ratio = UiDpi.Paint(e.Graphics) / 192.0f;
        int pad = Math.Max(1, (int)Math.Round(ratio * 2));      // ~1 逻辑 px 边距
        int radius = Math.Max(2, (int)Math.Round(ratio * 8));   // ~4 逻辑 px 圆角
        int thumbH = ThumbHeight(Height);
        int travel = Math.Max(1, Height - thumbH);
        int thumbY = Height <= thumbH ? 0 : (int)Math.Round((double)travel * _value / _max);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath path = RoundedRect(
            new Rectangle(pad, thumbY + pad, Math.Max(2, Width - pad * 2), Math.Max(4, thumbH - pad * 2)),
            radius);
        using var brush = new SolidBrush(UiVisualStyle.Border);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int thumbH = ThumbHeight(Height);
        int travel = Math.Max(1, Height - thumbH);
        int thumbY = Height <= thumbH ? 0 : (int)Math.Round((double)travel * _value / _max);
        if (e.Y >= thumbY && e.Y <= thumbY + thumbH)
        {
            _dragging = true;
            _dragStartY = e.Y;
            _dragStartValue = _value;
        }
        else
        {
            Value = _value + (e.Y < thumbY ? -_viewHeight : _viewHeight);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        int thumbH = ThumbHeight(Height);
        int travel = Math.Max(1, Height - thumbH);
        Value = _dragStartValue + (int)Math.Round((double)(e.Y - _dragStartY) * _max / travel);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        base.OnMouseUp(e);
    }

    static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }
        int d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
