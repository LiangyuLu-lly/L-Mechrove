using MechrevoLite.Hardware;
using Probe;

namespace MechrevoLite.Tests;

/// <summary>
/// T1（Wave A）失败 / 边界路径：身份的"读不到"与"读得到但叫不出名字"必须都 fail-closed 成
/// <c>Unknown</c>，且绝不把传输层异常放出去。对应的 happy 断言见 <see cref="ModelRegistryTests"/>。
/// </summary>
public class ModelRegistryFailTests
{
    sealed class UnreadableEc : IEcReadTransport
    {
        public List<int> Attempts { get; } = new();
        public int ReadByte(int address)
        {
            Attempts.Add(address);
            return -1;
        }
    }

    sealed class ThrowingEc : IEcReadTransport
    {
        public int ReadByte(int address) => throw new TimeoutException($"EC read 0x{address:X3} timed out");
    }

    [Fact]
    public void AnUnreadableProjectByteIsUnknownAndDoesNotThrow()
    {
        ModelIdentity identity = ModelRegistry.Read(new UnreadableEc());

        Assert.Equal("Unknown", identity.ProjectId);
        Assert.Equal(-1, identity.RawProjectByte);
        Assert.Equal("Unknown", identity.BiosProjectId);
        Assert.Equal(ModelSource.Unknown, identity.Source);
        Assert.False(identity.IsParsed);
    }

    [Fact]
    public void ATransportExceptionDoesNotEscapeAndStillYieldsUnknown()
    {
        ModelIdentity identity = ModelRegistry.Read(new ThrowingEc());

        Assert.Equal("Unknown", identity.ProjectId);
        Assert.Equal(ModelSource.Unknown, identity.Source);
    }

    [Fact]
    public void AProjectByteWithNoVendorEnumNameIsUnknown()
    {
        // 250 落在 ProjectID 枚举之外：读得到但叫不出名字 -> fail-closed。
        ModelIdentity identity = ModelRegistry.Read(new ModelRegistryTests.FakeEc(new() { [1856] = 250 }));

        Assert.Equal("Unknown", identity.ProjectId);
        Assert.Equal(250, identity.RawProjectByte);
        Assert.Equal(ModelSource.Unknown, identity.Source);
    }

    [Fact]
    public void TheProjectByteIsMaskedToALowByteBeforeNaming()
    {
        // 0x112 -> 0x12 = 18 = PH4TRX1（与厂商 (byte)&0xFF 等价）。
        ModelIdentity identity = ModelRegistry.Read(new ModelRegistryTests.FakeEc(new() { [1856] = 0x112 }));

        Assert.Equal("PH4TRX1", identity.ProjectId);
        Assert.Equal(18, identity.RawProjectByte);
    }

    [Fact]
    public void AFamilyCodeWithNoAuxiliaryReadsStaysTheFamilyMemberAndDoesNotThrow()
    {
        // 23（PHxAxxx）在辅助字节全读不到时，厂商路径全部走 sys=0/rom=0/module=0 -> PH4ARxx。
        ModelIdentity identity = ModelRegistry.Read(new ModelRegistryTests.FakeEc(new() { [1856] = 23 }));

        Assert.Equal("PH4ARxx", identity.ProjectId);
        Assert.Equal(ModelSource.Ec, identity.Source);
    }

    [Fact]
    public void AMissingSixBitFlagFallsBackToTheFourBitMask()
    {
        // 1994 读不到时厂商 Read 保持 Data=0 -> bit0=0 -> mask 15（0x12 & 15 = 2 -> IDV）。
        ModelIdentity identity = ModelRegistry.Read(new ModelRegistryTests.FakeEc(new() { [1856] = 18, [1868] = 0x12 }));

        Assert.Equal("IDV", identity.BiosProjectId);
    }

    [Fact]
    public void AnUnreadableBiosProjectByteIsUnknownRatherThanIDR()
    {
        ModelIdentity identity = ModelRegistry.Read(new ModelRegistryTests.FakeEc(new() { [1856] = 18 }));

        Assert.Equal("Unknown", identity.BiosProjectId);
    }

    [Fact]
    public void ACodeWithoutAFanTableDirectoryIsStillAParsedIdentity()
    {
        // project byte 22 = PH6TQxx（枚举成员但没有风扇表目录）仍是可解析身份，由 T3 判 NotInSet。
        ModelIdentity identity = ModelRegistry.Read(new ModelRegistryTests.FakeEc(new() { [1856] = 22 }));

        Assert.Equal("PH6TQxx", identity.ProjectId);
        Assert.Equal(ModelSource.Ec, identity.Source);
    }
}
