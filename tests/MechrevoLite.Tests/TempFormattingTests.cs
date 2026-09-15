using MechrevoLite.Helpers;

namespace MechrevoLite.Tests;

/// <summary>
/// 温度格式化的表征测试：锁定删除 fahrenheit 键之后仍须成立的既有可观察行为。
///
/// 背景：beta17 删除了 fahrenheit 配置键（全库无任何写入路径，见 Beta17DeadCodeGuardTests），
/// 温度因此固定为摄氏。FormatTemp 是活代码（Settings.cs 的 CPU/GPU 温度标签消费它），
/// 所以它的输出契约必须被钉住——将来若有人重新引入华氏支持，必须先改这里的测试。
/// </summary>
public class TempFormattingTests
{
    [Theory]
    [InlineData(25.4, "25°C")]
    [InlineData(25.6, "26°C")]
    [InlineData(0.0, "0°C")]
    [InlineData(-3.6, "-4°C")]
    [InlineData(99.4, "99°C")]
    [InlineData(100.0, "100°C")]
    public void FormatTemp_AlwaysRendersRoundedCelsius(double celsius, string expected)
    {
        Assert.Equal(expected, TempHelper.FormatTemp(celsius));
    }

    [Fact]
    public void FormatTemp_NeverEmitsFahrenheitSuffix()
    {
        // 曾经的华氏分支已随 fahrenheit 键删除：任何输入都不得出现 °F。
        Assert.DoesNotContain("°F", TempHelper.FormatTemp(100.0));
        Assert.DoesNotContain("°F", TempHelper.FormatTemp(-40.0));
    }

    [Fact]
    public void FormatTemp_MidpointFollowsExistingRounding()
    {
        // 表征现状：Math.Round 的默认中点规则是 ToEven（2.5 -> 2、3.5 -> 4）。
        // 这条断言让"改变四舍五入语义"成为显式决定，而不是悄悄改变用户看到的读数。
        Assert.Equal("2°C", TempHelper.FormatTemp(2.5));
        Assert.Equal("4°C", TempHelper.FormatTemp(3.5));
    }
}
