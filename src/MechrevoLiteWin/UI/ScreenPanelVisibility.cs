namespace MechrevoLite.UI;

/// <summary>
/// 屏幕面板的可见性判定（T29）。
///
/// <para>此前该判定挂在 <c>AppConfig.NoGpu()</c> 上（"没有独显才显示屏幕面板"），语义恰好反转：
/// 有独显的机器反而看不到屏幕节。屏幕面板是否可用只取决于机器上报的刷新率能力，与"有没有 dGPU"无关。</para>
/// </summary>
public static class ScreenPanelVisibility
{
    /// <summary>机器上报了刷新率（&gt; 0）即显示屏幕面板。</summary>
    public static bool PanelVisible(int maxFrequency) => maxFrequency > 0;

    /// <summary>只有能提供高于最低刷新率时，才显示刷新率选择表。</summary>
    public static bool RefreshTableVisible(int maxFrequency, int minRate) => maxFrequency > minRate;
}
