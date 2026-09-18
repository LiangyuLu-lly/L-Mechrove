using MechrevoLite.Fan;
using Probe;

namespace MechrevoLite.Tests;

/// <summary>
/// T14（Wave C）happy 路径：规格类严格由 **EC 1934 bit6**（<c>MyEcCtrl.IsSuportRamFan1p5()</c>，
/// <c>MyEcCtrl.cs:233-238</c>）在 <c>RamFan1</c>/<c>RamFan1p5</c> 之间选择；读失败按厂商语义
/// （<c>ref byte</c> 保持 0）落到 RamFan1。只读——不写 EC。
/// 失败/边界断言见 <see cref="FanSpecSelectionFailTests"/>。
/// </summary>
public class FanSpecSelectionTests
{
    [Fact]
    public void BitSixSetSelectsRamFan1p5()
    {
        Assert.Equal(FanSpecClass.RamFan1p5, FanSpecSelector.SelectFor(new FakeEc(new() { [1934] = 0x40 })));
    }

    [Fact]
    public void BitSixClearSelectsRamFan1()
    {
        Assert.Equal(FanSpecClass.RamFan1, FanSpecSelector.SelectFor(new FakeEc(new() { [1934] = 0x00 })));
    }

    [Fact]
    public void OnlyBitSixMatters()
    {
        Assert.Equal(FanSpecClass.RamFan1, FanSpecSelector.SelectFor(new FakeEc(new() { [1934] = 0x20 })));
        Assert.Equal(FanSpecClass.RamFan1, FanSpecSelector.SelectFor(new FakeEc(new() { [1934] = 0xBF })));
    }

    [Fact]
    public void SelectionReadsTheCapabilityByteThroughTheSeam()
    {
        var ec = new FakeEc(new() { [1934] = 0x40 });

        FanSpecSelector.SelectFor(ec);

        Assert.Equal(new[] { FanSpecSelector.CapabilityAddress }, ec.Reads);
    }

    [Fact]
    public void AnUnreadableCapabilityByteFallsBackToRamFan1()
    {
        // 厂商 Read 失败时 ref byte 保持 0 -> bit6=0 -> legacy RamFan1；不猜 1p5。
        Assert.Equal(FanSpecClass.RamFan1, FanSpecSelector.SelectFor(new FakeEc(new())));
    }

    [Fact]
    public void RamFan2IsNeverReachable()
    {
        Assert.False(FanSpecSelector.IsReachable(FanSpecClass.RamFan2));
        Assert.True(FanSpecSelector.IsReachable(FanSpecClass.RamFan1));
        Assert.True(FanSpecSelector.IsReachable(FanSpecClass.RamFan1p5));

        foreach (bool capability in new[] { true, false })
            Assert.NotEqual(FanSpecClass.RamFan2, FanSpecSelector.Select(capability));
    }

    sealed class FakeEc : IEcReadTransport
    {
        readonly Dictionary<int, int> _values;
        public FakeEc(Dictionary<int, int> values) => _values = values;
        public List<int> Reads { get; } = new();
        public int ReadByte(int address)
        {
            Reads.Add(address);
            return _values.TryGetValue(address, out int value) ? value : -1;
        }
    }
}
