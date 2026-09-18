using MechrevoLite.Hardware;
using MechrevoLite.Mode;
using Probe;

namespace MechrevoLite.Tests;

/// <summary>
/// T20（Wave D）happy 路径：PL/Tcc 默认值**逐 SKU 从 EC 读**，厂商命名（Gaming/Office/**Turbo**），
/// 地址 Gaming 1840-1843 / Office 1844-1847 / Turbo 1959-1962 / Tcc 2008-2010。
///
/// 失败路径（读不到即禁用）见 <see cref="PlDefaultFailTests"/>。真机数值属 BLOCKED-HW。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class PlDefaultTests
{
    internal sealed class FakeEc : IEcReadTransport
    {
        readonly Dictionary<int, int> _values;
        public FakeEc(Dictionary<int, int> values) => _values = values;
        public int ReadByte(int address) => _values.TryGetValue(address, out int value) ? value : -1;
    }

    static PlDefaultSet ReadViaService(int visualMode, Dictionary<int, int> ec)
    {
        Func<IEcReadTransport?> original = MechrevoService.EcReadTransportFactory;
        try
        {
            MechrevoService.EcReadTransportFactory = () => new FakeEc(ec);
            return MechrevoService.ReadPlDefaults(visualMode).Values;
        }
        finally
        {
            MechrevoService.EcReadTransportFactory = original;
        }
    }

    [Fact]
    public void TheEcAddressesAreTheVendorOnes()
    {
        Assert.Equal(new[] { 1840, 1841, 1842, 1843 }, PlDefaults.AddressesFor(PlDefaultMode.Gaming));
        Assert.Equal(new[] { 1844, 1845, 1846, 1847 }, PlDefaults.AddressesFor(PlDefaultMode.Office));
        // Turbo 默认值住在 1959-1962（GetTurboPLDefaultValue）——不是 BatterySaver。
        Assert.Equal(new[] { 1959, 1960, 1961, 1962 }, PlDefaults.AddressesFor(PlDefaultMode.Turbo));
        Assert.Equal(2008, PlDefaults.TccAddressFor(PlDefaultMode.Gaming));
        Assert.Equal(2009, PlDefaults.TccAddressFor(PlDefaultMode.Office));
        Assert.Equal(2010, PlDefaults.TccAddressFor(PlDefaultMode.Turbo));
    }

    [Theory]
    [InlineData(MechrevoService.ModeGaming, PlDefaultMode.Gaming)]
    [InlineData(MechrevoService.ModeOffice, PlDefaultMode.Office)]
    [InlineData(MechrevoService.ModeTurbo, PlDefaultMode.Turbo)]
    public void TheVisualModeMapsToItsDefaultSet(int visualMode, PlDefaultMode expected)
    {
        Assert.Equal(expected, PlDefaults.ModeForVisualMode(visualMode));
    }

    [Fact]
    public void GamingDefaultsComeFromTheGamingAddresses()
    {
        var ec = new Dictionary<int, int> { [1840] = 45, [1841] = 60, [1842] = 80, [1843] = 90, [2008] = 15 };

        PlDefaultsResult result = PlDefaults.Read(PlDefaultMode.Gaming, address => ec[address]);

        Assert.True(result.Editable);
        Assert.Equal(45, result.Values.Pl1);
        Assert.Equal(60, result.Values.Pl2);
        Assert.Equal(80, result.Values.Pl4);
        Assert.Equal(15, result.Values.TccOffset);
    }

    [Fact]
    public void TurboDefaultsComeFromTheTurboAddressesNotBatterySaver()
    {
        var ec = new Dictionary<int, int> { [1959] = 50, [1960] = 70, [1961] = 95, [1962] = 100, [2010] = 20 };

        PlDefaultsResult result = PlDefaults.Read(PlDefaultMode.Turbo, address => ec[address]);

        Assert.True(result.Editable);
        Assert.Equal(50, result.Values.Pl1);
        Assert.Equal(70, result.Values.Pl2);
        Assert.Equal(95, result.Values.Pl4);
        Assert.Equal(20, result.Values.TccOffset);
        Assert.NotEqual(1944, PlDefaults.AddressesFor(PlDefaultMode.Turbo)[0]);
    }

    [Fact]
    public void TheTransportOverloadReadsThroughTheSeam()
    {
        var transport = new FakeEc(new Dictionary<int, int>
        {
            [1844] = 35, [1845] = 45, [1846] = 60, [1847] = 70, [2009] = 12,
        });

        PlDefaultsResult result = PlDefaults.Read(PlDefaultMode.Office, transport);

        Assert.True(result.Editable);
        Assert.Equal(35, result.Values.Pl1);
        Assert.Equal(12, result.Values.TccOffset);
    }

    /// <summary>C5 端到端接线：EC 数据必须经服务边界到达消费方，且换值确实改变结果。</summary>
    [Fact]
    public void E2EWiring_TheServiceReturnsTheEcValuesAndTheyChangeWithTheEc()
    {
        PlDefaultSet first = ReadViaService(MechrevoService.ModeTurbo,
            new Dictionary<int, int> { [1959] = 50, [1960] = 70, [1961] = 95, [1962] = 100, [2010] = 20 });
        PlDefaultSet second = ReadViaService(MechrevoService.ModeTurbo,
            new Dictionary<int, int> { [1959] = 55, [1960] = 75, [1961] = 99, [1962] = 105, [2010] = 25 });

        Assert.Equal(50, first.Pl1);
        Assert.Equal(55, second.Pl1);
        Assert.NotEqual(first.Pl1, second.Pl1);
        Assert.True(first.IsAvailable);
    }
}
