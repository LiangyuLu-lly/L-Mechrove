using MechrevoLite.Fan;
using Probe;

namespace MechrevoLite.Tests;

/// <summary>
/// T14（Wave C）失败/边界面：错规格类必须**显式拒绝**——<c>RamFan2</c> 在 5.17.51 反编译里
/// 从未被实例化（<c>FanTable_Manager2</c> 无构造点），把 1p5 表按 RamFan2 解释必须报错，
/// 而不是静默选中；选择器在任何输入下都不得返回 RamFan2。
/// </summary>
public class FanSpecSelectionFailTests
{
    [Fact]
    public void InterpretingAFanTableAsRamFan2IsRejected()
    {
        Assert.Throws<NotSupportedException>(() => FanSpecSelector.RequireReachable(FanSpecClass.RamFan2));
    }

    [Fact]
    public void ReachableSpecClassesAreAccepted()
    {
        Assert.Equal(FanSpecClass.RamFan1, FanSpecSelector.RequireReachable(FanSpecClass.RamFan1));
        Assert.Equal(FanSpecClass.RamFan1p5, FanSpecSelector.RequireReachable(FanSpecClass.RamFan1p5));
    }

    [Fact]
    public void BitFourDoesNotSelectRamFan1p5()
    {
        // EC 1934 的 bit4（0x10）不是 1p5 判据；只有 bit6（0x40）。
        Assert.Equal(FanSpecClass.RamFan1, FanSpecSelector.SelectFor(new FakeEc(new() { [1934] = 0x10 })));
    }

    [Fact]
    public void TheSelectorNeverReturnsRamFan2ForAnyInput()
    {
        foreach (bool capability in new[] { true, false })
            Assert.NotEqual(FanSpecClass.RamFan2, FanSpecSelector.Select(capability));

        Assert.False(FanSpecSelector.IsReachable(FanSpecClass.RamFan2));
    }

    [Fact]
    public void ATransportThatThrowsIsTreatedAsNoCapability()
    {
        Assert.Equal(FanSpecClass.RamFan1, FanSpecSelector.SelectFor(new ThrowingEc()));
    }

    sealed class FakeEc : IEcReadTransport
    {
        readonly Dictionary<int, int> _values;
        public FakeEc(Dictionary<int, int> values) => _values = values;
        public int ReadByte(int address) => _values.TryGetValue(address, out int value) ? value : -1;
    }

    sealed class ThrowingEc : IEcReadTransport
    {
        public int ReadByte(int address) => throw new IOException("transport failed");
    }
}
