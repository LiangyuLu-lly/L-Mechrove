namespace MechrevoLite.UI;

/// <summary>
/// DPI 读取入口。刻意分成「布局」和「绘制」两套，因为 UI 审计对二者的要求正好相反。
///
/// ===== 审计是怎么模拟目标 DPI 的 =====
///
/// <see cref="UiAuditRunner"/> 无法真的换一块显示器，它的做法是：
///   1. 在宿主 DPI 下正常构造并显示窗体（布局按宿主 DPI 完成）；
///   2. 用 <c>form.Scale(viewportDpi / hostDpi)</c> 把整棵控件树**相对**缩放到目标尺寸；
///   3. 用 <c>RForm.AuditLayoutScale</c> 告知响应式布局目标逻辑倍率。
///
/// ===== 因此两套语义必须分开 =====
///
/// 布局（<see cref="Layout"/>）：参与第 1 步的尺寸计算，必须读**真实宿主 DPI**。
///   如果这里返回目标视口 DPI，控件就已经按目标尺寸排好了，第 2 步的相对缩放会再乘一次
///   同样的比例，结果是几何被缩放两遍——表现为大量 text-clipping / parent-overflow 误报。
///
/// 绘制（<see cref="Paint"/>）：发生在第 2 步之后，且 WinForms 不会因为 Control.Scale
///   而改变 Graphics 的 DPI，所以画布上报的仍是宿主 DPI。自绘控件用它算圆角半径和描边
///   粗细，若继续读宿主 DPI，就会出现「控件尺寸已按目标 DPI 缩小、圆角却仍按宿主 DPI」
///   的不一致：同一份代码在 96 DPI 与 168 DPI 主机上跑出不同的截图。
///   这是 UI 审计此前唯一已知的验证盲区。
///
/// 判断标准很简单：结果会影响控件的位置或尺寸 -> <see cref="Layout"/>；
/// 只影响像素怎么画 -> <see cref="Paint"/>。
/// </summary>
internal static class UiDpi
{
    /// <summary>逻辑基线 DPI。所有窗体的 AutoScaleDimensions 都以 96 为基准。</summary>
    public const int Baseline = 96;

    /// <summary>
    /// 审计模式下固定的视口 DPI；0 表示未启用覆盖（正常运行）。
    /// 由 <see cref="UiAuditRunner"/> 在创建窗体前设置、在 finally 中清零。
    /// 只影响 <see cref="Paint"/>，不影响 <see cref="Layout"/>。
    /// </summary>
    internal static int AuditDpi { get; set; }

    /// <summary>布局用 DPI：始终是真实宿主 DPI，不受审计覆盖影响。</summary>
    internal static int Layout(Control control) => Math.Max(Baseline, control.DeviceDpi);

    /// <summary>布局用缩放倍率，下限 1。</summary>
    internal static float LayoutScale(Control control) => Math.Max(1F, Layout(control) / (float)Baseline);

    /// <summary>绘制用 DPI：审计期间固定为视口 DPI，正常运行时是画布上报的真实 DPI。</summary>
    internal static float Paint(Graphics graphics) =>
        AuditDpi > 0 ? AuditDpi : Math.Max(Baseline, graphics.DpiX);

    /// <summary>
    /// 绘制用 DPI，取不到 <see cref="Graphics"/> 时使用（例如在 WM_PAINT 里直接操作 HDC）。
    /// 只能用于纯绘制计算，绝不能用于任何影响控件尺寸或位置的地方。
    /// </summary>
    internal static float Paint(Control control) =>
        AuditDpi > 0 ? AuditDpi : Math.Max(Baseline, control.DeviceDpi);
}
