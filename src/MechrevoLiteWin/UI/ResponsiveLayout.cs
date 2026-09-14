namespace MechrevoLite.UI;

/// <summary>Shared DPI and screen-boundary helpers for top-level WinForms UI.</summary>
public static class ResponsiveLayout
{
    public const int LogicalScreenMargin = 12;

    // 布局：必须读真实宿主 DPI。审计的 form.Scale 是相对缩放，若这里返回模拟视口 DPI
    // 会导致同一比例被应用两次。详见 UiDpi 的说明。
    /// <summary>
    /// 逻辑单位 → 设备像素。缩放来源必须与窗体当前的缩放来源一致：
    /// 实机取宿主 DPI；UI 审计下取 <see cref="RForm.AuditLayoutScale"/>（视口倍率），
    /// 否则审计截图里会出现「窗口按视口放大、面板按宿主 DPI 定高」的错配，
    /// 表现为内容溢出面板边界（200% 下 GPU 分区 6px 溢出即此形态）。
    /// </summary>
    public static int LogicalToDevice(Control control, int value)
    {
        float scale = control.FindForm() is RForm { AuditLayoutScale: > 0f } auditForm
            ? auditForm.AuditLayoutScale
            : UiDpi.Layout(control) / 96f;
        return (int)Math.Round(value * scale);
    }

    /// <summary>
    /// Scales controls authored at the 96-DPI logical baseline after they are added
    /// to a form whose designer layout has already completed its own autoscale pass.
    /// </summary>
    public static void ScaleFrom96(Control control, Control dpiOwner)
    {
        float factor = UiDpi.Layout(dpiOwner) / 96f;
        if (Math.Abs(factor - 1f) > 0.01f)
            control.Scale(new SizeF(factor, factor));
        PerformLayoutTree(control);
    }

    public static Rectangle WorkingAreaFor(Control control)
        => Screen.FromControl(control).WorkingArea;

    public static void ConstrainToWorkingArea(Form form, Rectangle? requestedArea = null)
    {
        if (form.IsDisposed || form.WindowState != FormWindowState.Normal) return;

        Rectangle area = requestedArea ?? WorkingAreaFor(form);
        int margin = LogicalToDevice(form, LogicalScreenMargin);
        int frameWidth = Math.Max(0, form.Width - form.ClientSize.Width);
        int frameHeight = Math.Max(0, form.Height - form.ClientSize.Height);
        int maxClientWidth = Math.Max(240, area.Width - margin * 2 - frameWidth);
        int maxClientHeight = Math.Max(200, area.Height - margin * 2 - frameHeight);

        Size wanted = form.ClientSize;
        Size constrained = new(
            Math.Min(wanted.Width, maxClientWidth),
            Math.Min(wanted.Height, maxClientHeight));
        if (constrained != wanted)
            form.ClientSize = constrained;

        PerformLayoutTree(form);

        int maxLeft = Math.Max(area.Left + margin, area.Right - margin - form.Width);
        int maxTop = Math.Max(area.Top + margin, area.Bottom - margin - form.Height);
        form.Left = Math.Clamp(form.Left, area.Left + margin, maxLeft);
        form.Top = Math.Clamp(form.Top, area.Top + margin, maxTop);
    }

    public static void PlaceBottomRight(Form form, Rectangle? requestedArea = null)
    {
        Rectangle area = requestedArea ?? WorkingAreaFor(form);
        ConstrainToWorkingArea(form, area);
        int margin = LogicalToDevice(form, LogicalScreenMargin);
        form.Location = new Point(
            Math.Max(area.Left + margin, area.Right - margin - form.Width),
            Math.Max(area.Top + margin, area.Bottom - margin - form.Height));
    }

    public static void PlaceAdjacent(Form child, Form owner)
    {
        Rectangle area = Screen.FromControl(owner).WorkingArea;
        ConstrainToWorkingArea(child, area);
        int gap = LogicalToDevice(owner, 10);
        int left = owner.Left - child.Width - gap;
        if (left < area.Left)
            left = owner.Right + gap;
        child.Location = new Point(
            Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - child.Width)),
            Math.Clamp(owner.Top, area.Top, Math.Max(area.Top, area.Bottom - child.Height)));
    }

    public static int VisibleContentBottom(Control container)
        => container.Controls.Cast<Control>()
            .Where(control => control.Visible)
            .Select(control => control.Bottom + control.Margin.Bottom)
            .DefaultIfEmpty(0)
            .Max();

    public static void PerformLayoutTree(Control root)
    {
        root.PerformLayout();
        foreach (Control child in root.Controls)
            PerformLayoutTree(child);
        root.PerformLayout();
    }
}
