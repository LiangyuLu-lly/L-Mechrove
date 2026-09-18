using MechrevoLite.Hardware;
using MechrevoLite.Mode;
using Probe;

namespace MechrevoLite.Tests;

/// <summary>
/// T20 失败/边界路径：**读不到就禁用**，绝不猜瓦数；Customize 没有 EC 默认组；
/// 地址族不重叠。happy 路径见 <see cref="PlDefaultTests"/>。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class PlDefaultFailTests
{
    static PlDefaultsResult ReadFromService(int visualMode, Func<IEcReadTransport?> factory)
    {
        Func<IEcReadTransport?> original = MechrevoService.EcReadTransportFactory;
        try
        {
            MechrevoService.EcReadTransportFactory = factory;
            return MechrevoService.ReadPlDefaults(visualMode);
        }
        finally
        {
            MechrevoService.EcReadTransportFactory = original;
        }
    }

    [Fact]
    public void AMissingPlByteMakesTheWholeSetUnavailable()
    {
        // 1842 缺失：不得返回部分值，也不得填猜测瓦数。
        var ec = new Dictionary<int, int> { [1840] = 45, [1841] = 60, [1843] = 90, [2008] = 15 };

        PlDefaultsResult result = PlDefaults.Read(PlDefaultMode.Gaming, address => ec.TryGetValue(address, out int value) ? value : -1);

        Assert.False(result.Editable);
        Assert.Null(result.Values.Pl1);
        Assert.Null(result.Values.Pl2);
        Assert.Null(result.Values.Pl4);
        Assert.Null(result.Values.TccOffset);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public void AMissingTccByteAlsoMakesTheSetUnavailable()
    {
        var ec = new Dictionary<int, int> { [1840] = 45, [1841] = 60, [1842] = 80, [1843] = 90 };

        PlDefaultsResult result = PlDefaults.Read(PlDefaultMode.Gaming, address => ec.TryGetValue(address, out int value) ? value : -1);

        Assert.False(result.Editable);
        Assert.False(result.Values.IsAvailable);
    }

    [Fact]
    public void NoEcTransportMeansNoEditing()
    {
        PlDefaultsResult result = ReadFromService(MechrevoService.ModeGaming, () => null);

        Assert.False(result.Editable);
        Assert.Equal(PlDefaultMode.Gaming, result.Mode);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public void CustomizeHasNoEcDefaultSet()
    {
        PlDefaultsResult result = ReadFromService(MechrevoService.ModeCustom,
            () => new PlDefaultTests.FakeEc(new Dictionary<int, int>()));

        Assert.False(result.Editable);
        Assert.Null(result.Mode);
    }

    [Fact]
    public void AnAllUnavailableEcNeverProducesHardcodedWatts()
    {
        PlDefaultsResult result = PlDefaults.Read(PlDefaultMode.Turbo, _ => -1);

        Assert.False(result.Editable);
        Assert.Equal(PlDefaultSet.Unavailable, result.Values);
        Assert.Null(result.Values.Pl1);
        Assert.Null(result.Values.Pl2);
        Assert.Null(result.Values.Pl4);
        Assert.Null(result.Values.TccOffset);
    }

    [Fact]
    public void TheThreeAddressFamiliesDoNotOverlap()
    {
        var all = new HashSet<int>();
        foreach (PlDefaultMode mode in Enum.GetValues<PlDefaultMode>())
        {
            foreach (int address in PlDefaults.AddressesFor(mode))
                Assert.True(all.Add(address), $"address {address} appears in more than one family");
            Assert.True(all.Add(PlDefaults.TccAddressFor(mode)), "tcc address overlaps a PL family");
        }
    }
}
