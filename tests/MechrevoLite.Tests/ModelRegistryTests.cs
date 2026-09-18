using MechrevoLite.Hardware;
using Probe;

namespace MechrevoLite.Tests;

/// <summary>
/// T1（Wave A）happy 路径：<see cref="ModelRegistry"/> 从 EC 读出机型身份，
/// 并按厂商 <c>GetProject2ExID</c> 展开规则命名。
/// 全部走 <see cref="IEcReadTransport"/> 假件——不碰 <c>\\.\ACPIDriver</c>、不发一条 IOCTL。
/// 失败/边界断言见 <see cref="ModelRegistryFailTests"/>。
/// </summary>
public class ModelRegistryTests
{
    /// <summary>固定 EC 字节映像；未给出的地址一律 -1（读不到），并记录读取顺序。</summary>
    internal sealed class FakeEc : IEcReadTransport
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

    [Fact]
    public void ADirectProjectByteDecodesToTheVendorEnumName()
    {
        ModelIdentity identity = ModelRegistry.Read(new FakeEc(new() { [1856] = 18 }));

        Assert.Equal("PH4TRX1", identity.ProjectId);
        Assert.Equal(18, identity.RawProjectByte);
        Assert.Equal(ModelSource.Ec, identity.Source);
    }

    [Fact]
    public void AProjectByteOutsideTheExpandedFamiliesIsNotExpanded()
    {
        ModelIdentity identity = ModelRegistry.Read(new FakeEc(new() { [1856] = 9 }));

        Assert.Equal("PF", identity.ProjectId);
    }

    [Fact]
    public void ThePhxAxxxFamilyExpandsThroughTheVendorSwitch()
    {
        // 23 = PHxAxxx; 展开只看 1110(bit7) / 2003(bit0|high nibble) / 1905(bit0) / 1906。
        Assert.Equal("PH4ARxx", ReadIdentity(1856, 23, 1110, 0, 2003, 0, 1905, 0).ProjectId);
        Assert.Equal("PH4AUxx", ReadIdentity(1856, 23, 1110, 0, 2003, 1, 1905, 0).ProjectId);
        Assert.Equal("PH4AUxf", ReadIdentity(1856, 23, 1110, 0, 2003, 1, 1905, 0, 1906, 3).ProjectId);
        Assert.Equal("PH4AXxx", ReadIdentity(1856, 23, 1110, 0x80, 1905, 0).ProjectId);
        Assert.Equal("PH6ARxx", ReadIdentity(1856, 23, 1110, 0, 2003, 0, 1905, 1).ProjectId);
        Assert.Equal("PH6AQxx", ReadIdentity(1856, 23, 1110, 0x80, 2003, 0, 1905, 1).ProjectId);
        Assert.Equal("PH6AGxx", ReadIdentity(1856, 23, 1110, 0x80, 2003, 0x30, 1905, 1).ProjectId);
    }

    [Fact]
    public void ThePhxPxxxFamilyExpandsThroughTheVendorSwitch()
    {
        Assert.Equal("PH4PRxx", ReadIdentity(1856, 24, 1110, 0, 2003, 0, 1905, 0).ProjectId);
        Assert.Equal("PH4PUxx", ReadIdentity(1856, 24, 1110, 0, 2003, 1, 1905, 0).ProjectId);
        Assert.Equal("PH4PGx1", ReadIdentity(1856, 24, 1110, 0x80, 1905, 0, 1906, 1).ProjectId);
        Assert.Equal("PH4PGx2", ReadIdentity(1856, 24, 1110, 0x80, 1905, 0, 1906, 9).ProjectId);
        Assert.Equal("PH4AQE3", ReadIdentity(1856, 24, 1110, 0x80, 1905, 0, 1906, 4).ProjectId);
        Assert.Equal("PH6PRxx", ReadIdentity(1856, 24, 1110, 0, 1905, 1).ProjectId);
        Assert.Equal("PH6PGEx", ReadIdentity(1856, 24, 1110, 0x80, 2002, 8, 1905, 1).ProjectId);
    }

    [Fact]
    public void TheAdapterWattDecodeSelectsThe150WVariant()
    {
        // 1183 & 0x78 == 24 -> 150 W -> the *150W enum member; anything else -> the plain one.
        Assert.Equal("PH6PG0x150W", ReadIdentity(1856, 24, 1110, 0x80, 2002, 16, 1905, 1, 1183, 0x18).ProjectId);
        Assert.Equal("PH6PG0x", ReadIdentity(1856, 24, 1110, 0x80, 2002, 16, 1905, 1, 1183, 0x00).ProjectId);
        Assert.Equal("PH6PG3x150W", ReadIdentity(1856, 24, 1110, 0x80, 2002, 12, 1905, 1, 1183, 0x18).ProjectId);
        Assert.Equal("PH6PG3x", ReadIdentity(1856, 24, 1110, 0x80, 2002, 12, 1905, 1, 1183, 0x00).ProjectId);
        Assert.Equal("PH6PG7x150W", ReadIdentity(1856, 24, 1110, 0x80, 2002, 18, 1905, 1, 1183, 0x18).ProjectId);
        Assert.Equal("PH6PG7x", ReadIdentity(1856, 24, 1110, 0x80, 2002, 18, 1905, 1, 1183, 0x00).ProjectId);
    }

    [Fact]
    public void TheBiosProjectByteHonoursTheSixBitIdMask()
    {
        // 1994 bit0 == 0 -> mask 15; == 1 -> mask 63（厂商 IsSupport6BitID）。
        // 映射照抄厂商 switch：masked 0 -> IDR、1 -> IDX、…、16 -> ID2，其余 -> NA。
        ModelIdentity fourBit = ModelRegistry.Read(new FakeEc(new() { [1856] = 18, [1994] = 0, [1868] = 0x00 }));
        Assert.Equal("IDR", fourBit.BiosProjectId);

        ModelIdentity sixBit = ModelRegistry.Read(new FakeEc(new() { [1856] = 18, [1994] = 1, [1868] = 0x10 }));
        Assert.Equal("ID2", sixBit.BiosProjectId);   // 0x10 & 63 = 16

        ModelIdentity sixBitValue18 = ModelRegistry.Read(new FakeEc(new() { [1856] = 18, [1994] = 1, [1868] = 0x12 }));
        ModelIdentity fourBitValue18 = ModelRegistry.Read(new FakeEc(new() { [1856] = 18, [1994] = 0, [1868] = 0x12 }));
        Assert.Equal("NA", sixBitValue18.BiosProjectId);   // 0x12 & 63 = 18 -> 不在厂商 switch 内
        Assert.Equal("IDV", fourBitValue18.BiosProjectId); // 0x12 & 15 = 2
    }

    [Fact]
    public void AllAuxiliaryReadsGoThroughTheSeam()
    {
        var ec = new FakeEc(new()
        {
            [1856] = 23,
            [1110] = 0x80,
            [2002] = 0,
            [2003] = 0x30,
            [1905] = 1,
            [1906] = 1,
            [1183] = 0,
            [1994] = 0,
            [1868] = 0,
        });

        ModelRegistry.Read(ec);

        foreach (int address in new[] { 1856, 1994, 1868, 1110, 2002, 2003, 1905, 1906, 1183 })
            Assert.Contains(address, ec.Reads);
    }

    static ModelIdentity ReadIdentity(params int[] addressValuePairs)
    {
        var values = new Dictionary<int, int>();
        for (int i = 0; i < addressValuePairs.Length; i += 2)
            values[addressValuePairs[i]] = addressValuePairs[i + 1];
        return ModelRegistry.Read(new FakeEc(values));
    }
}
