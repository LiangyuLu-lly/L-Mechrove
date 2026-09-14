using System.Drawing.Drawing2D;

namespace MechrevoLite.UI;

internal class BufferedPanel : Panel
{
    /// <summary>
    /// 卡片外观（DESIGN.md 第 4 节）：Surface 底 + 1px Border 描边 + 8px 圆角，无阴影。
    /// 圆角靠 Region 裁形，描边在 OnPaint 里画；父容器底在四角透出即是圆角效果。
    /// </summary>
    public bool CardStyle { get; set; }

    private int _cardRadius = 8;
    public int CardRadius
    {
        get => _cardRadius;
        set { _cardRadius = value; UpdateCardRegion(); Invalidate(); }
    }

    public BufferedPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateCardRegion();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateCardRegion();
    }

    void UpdateCardRegion()
    {
        if (!CardStyle || Width <= 0 || Height <= 0) { Region = null; return; }
        int radius = Math.Max(1, (int)Math.Round(CardRadius * (DeviceDpi / 96.0)));
        int d = radius * 2;
        using GraphicsPath path = new();
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(Width - d - 1, 0, d, d, 270, 90);
        path.AddArc(Width - d - 1, Height - d - 1, d, d, 0, 90);
        path.AddArc(0, Height - d - 1, d, d, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!CardStyle || Width <= 0 || Height <= 0) return;
        int radius = Math.Max(1, (int)Math.Round(CardRadius * (DeviceDpi / 96.0)));
        int d = radius * 2;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath path = new();
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(Width - d - 1, 0, d, d, 270, 90);
        path.AddArc(Width - d - 1, Height - d - 1, d, d, 0, 90);
        path.AddArc(0, Height - d - 1, d, d, 90, 90);
        path.CloseFigure();
        using Pen pen = new(UiVisualStyle.Border, 1f);
        e.Graphics.DrawPath(pen, path);
    }
}

internal sealed class BufferedTableLayoutPanel : TableLayoutPanel
{
    public BufferedTableLayoutPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }
}
