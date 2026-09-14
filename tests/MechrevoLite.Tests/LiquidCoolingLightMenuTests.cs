using System.Windows.Forms;
using MechrevoLite.UI;
using Xunit;

namespace MechrevoLite.Tests;

/// <summary>
/// 液冷灯光下拉菜单（LiquidCoolingLightMenu / LiquidCoolingMenuRenderer）的契约测试。
/// 高度契约：普通项 AutoSize 高度落在 [28, 34] 逻辑 px（工厂用 Space.Md=12 上下 Padding 实现）。
/// 头部行契约：Tag == "lc-header" 且 Enabled == false（不可点击、渲染器不画悬停）。
/// </summary>
public class LiquidCoolingLightMenuTests
{
    [Fact]
    public void Menu_UsesThemedRoundedRenderer()
    {
        using var menu = new LiquidCoolingLightMenu();

        Assert.IsAssignableFrom<CustomContextMenu>(menu);
        Assert.IsType<LiquidCoolingMenuRenderer>(menu.Renderer);
        Assert.False(menu.ShowImageMargin);
    }

    [Fact]
    public void Menu_GroupHeadersAreMutedAndNonInteractive()
    {
        using var menu = new LiquidCoolingLightMenu();

        menu.AddHeader("头部灯效");

        var header = Assert.IsType<ToolStripMenuItem>(Assert.Single(menu.Items));
        Assert.Equal("lc-header", header.Tag);
        Assert.False(header.Enabled);
    }

    [Fact]
    public void Menu_ItemsAndSeparators_AreOrderedForTheDesign()
    {
        using var menu = new LiquidCoolingLightMenu();

        menu.AddHeader("头部灯效");
        var item = menu.AddItem("流光", () => { });
        menu.AddSeparator();
        var fanItem = menu.AddItem("风扇同步", () => { }, requiresFanLed: true);

        // 本用例共加入 4 项：header0 / item / separator / fanItem —— 与下方索引 0..3 一一对应。
        Assert.Equal(4, menu.Items.Count);
        Assert.Same(header0(menu), menu.Items[0]);
        Assert.Same(item, menu.Items[1]);
        Assert.IsType<ToolStripSeparator>(menu.Items[2]);
        Assert.Same(fanItem, menu.Items[3]);
        Assert.Contains(fanItem, menu.FanLedItems);
        Assert.DoesNotContain(item, menu.FanLedItems);
    }

    [Fact]
    public void Menu_DefaultItemHeightIsInRange()
    {
        using var menu = new LiquidCoolingLightMenu();

        var item = menu.AddItem("流光", () => { });

        Assert.InRange(item.Height, 28, 34);
    }

    [Fact]
    public void Menu_ApplyTheme_RereadsPaletteTokens()
    {
        using var menu = new LiquidCoolingLightMenu();

        menu.ApplyTheme();

        Assert.Equal(UiVisualStyle.Input, menu.BackColor);
        Assert.Equal(UiVisualStyle.Text, menu.ForeColor);
    }

    static ToolStripMenuItem header0(LiquidCoolingLightMenu menu) => (ToolStripMenuItem)menu.Items[0]!;
}
