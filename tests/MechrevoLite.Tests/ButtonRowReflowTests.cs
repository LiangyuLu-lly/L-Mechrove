using System.Reflection;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// 单行按钮条的重排守卫。
///
/// 现场症状是「正常使用时性能模式和显卡模式的按钮经常闪烁」。根因是
/// <c>ReflowPerformanceButtons</c> / <c>VisualiseGPUButtons</c> / <c>ReflowLightingActions</c>
/// 都用 <c>Controls.Clear()</c> 加重新添加来铺按钮，而它们的调用方
/// <c>RefreshDeviceCapabilities</c> 挂在 <c>CapabilitiesChanged</c> 上——那个事件在十二个
/// 状态主题上都会触发，其中液冷/设置/风扇状态每三秒被 GETSTATUS 轮询回来一次。
/// 于是按钮每三秒被拆掉重建一次，子控件短暂脱离父容器就是屏幕上看到的闪烁。
///
/// 原先已有一个 <c>_lastCapabilityLayout</c> 指纹守卫，但它放在方法末尾，
/// 只挡住了 ArrangeDashboard 与 PerformLayout，三处重排都在守卫之前跑完了。
///
/// 这里直接测那个抽出来的守卫本体：布局没变必须一次控件树都不动。
/// </summary>
public class ButtonRowReflowTests
{
    static readonly MethodInfo Reflow = typeof(MechrevoLite.SettingsForm)
        .GetMethod("ReflowSingleRow", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ReflowSingleRow 不存在——闪烁守卫被删掉了？");

    static bool Invoke(TableLayoutPanel table, List<Control> buttons, ref string lastLayout)
    {
        object?[] args = [table, buttons, lastLayout];
        bool changed = (bool)Reflow.Invoke(null, args)!;
        lastLayout = (string)args[2]!;
        return changed;
    }

    static List<Control> Row(params string[] names) =>
        names.Select(name => (Control)new Button { Name = name }).ToList();

    [Fact]
    public void FirstCallLaysOutTheButtons()
    {
        using var table = new TableLayoutPanel();
        string last = "";

        Assert.True(Invoke(table, Row("a", "b", "c"), ref last));

        Assert.Equal(3, table.Controls.Count);
        Assert.Equal(3, table.ColumnCount);
    }

    /// <summary>
    /// 这一条就是闪烁的修复本体：同样的布局再来一次，控件树必须一动不动。
    /// </summary>
    [Fact]
    public void RepeatingTheSameLayoutDoesNotTouchTheControlTree()
    {
        using var table = new TableLayoutPanel();
        string last = "";
        Invoke(table, Row("a", "b", "c"), ref last);
        Control[] before = table.Controls.Cast<Control>().ToArray();

        bool changed = Invoke(table, Row("a", "b", "c"), ref last);

        Assert.False(changed);
        // 同一批控件实例、同样的顺序：没有被移除再加回去。
        Assert.Equal(before, table.Controls.Cast<Control>().ToArray());
    }

    /// <summary>按钮数量变了必须重排，否则新入口不会出现。</summary>
    [Fact]
    public void AddingAButtonTriggersAReflow()
    {
        using var table = new TableLayoutPanel();
        string last = "";
        Invoke(table, Row("a", "b"), ref last);

        Assert.True(Invoke(table, Row("a", "b", "c"), ref last));
        Assert.Equal(3, table.Controls.Count);
    }

    [Fact]
    public void RemovingAButtonTriggersAReflow()
    {
        using var table = new TableLayoutPanel();
        string last = "";
        Invoke(table, Row("a", "b", "c"), ref last);

        Assert.True(Invoke(table, Row("a", "c"), ref last));
        Assert.Equal(2, table.Controls.Count);
    }

    /// <summary>
    /// 顺序变了也必须重排：数量相同但排列不同，签名只比数量的话会漏掉。
    /// </summary>
    [Fact]
    public void ReorderingButtonsTriggersAReflow()
    {
        using var table = new TableLayoutPanel();
        string last = "";
        Invoke(table, Row("a", "b", "c"), ref last);

        Assert.True(Invoke(table, Row("c", "b", "a"), ref last));
        Assert.Equal("c", table.GetControlFromPosition(0, 0)!.Name);
    }

    /// <summary>
    /// 空行也必须真的布局一次，而且至少留一列。
    ///
    /// 这条抓到过一个真实边界：签名原本只拼控件名，空行拼出来是空串，
    /// 与「从未布局过」的初值 "" 相同，于是首次就摊到空行时会被当成「没变化」跳过，
    /// ColumnCount 留在 0。签名加了数量前缀之后两者不再混淆。
    /// </summary>
    [Fact]
    public void AnEmptyRowStillGetsLaidOutAndKeepsOneColumn()
    {
        using var table = new TableLayoutPanel();
        string last = "";

        Assert.True(Invoke(table, [], ref last));

        Assert.Equal(1, table.ColumnCount);
        Assert.Empty(table.Controls);

        // 第二次同样是空行，这时才该跳过。
        Assert.False(Invoke(table, [], ref last));
    }

    /// <summary>列宽必须平均分满一行，否则按钮会挤在左边。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    public void ColumnsSplitTheRowEvenly(int count)
    {
        using var table = new TableLayoutPanel();
        string last = "";
        string[] names = Enumerable.Range(0, count).Select(i => "b" + i).ToArray();

        Invoke(table, Row(names), ref last);

        Assert.Equal(count, table.ColumnStyles.Count);
        foreach (ColumnStyle style in table.ColumnStyles)
        {
            Assert.Equal(SizeType.Percent, style.SizeType);
            Assert.Equal(100F / count, style.Width, 3);
        }
    }
}
