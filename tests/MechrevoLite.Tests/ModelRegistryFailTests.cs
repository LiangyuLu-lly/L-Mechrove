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
    public void AFamilyCodeWithNoAuxiliaryReadsExpandsLikeVendorToPh4ARxx()
    {
        // 厂商 GetProject2ExID：辅助字节读不到按 0。族 23 全 0 → 5889 PH4ARxx。
        ModelIdentity identity = ModelRegistry.Read(new ModelRegistryTests.FakeEc(new() { [1856] = 23 }));

        Assert.Equal("PH4ARxx", identity.ProjectId);
        Assert.Equal(23, identity.RawProjectByte);
        Assert.Equal(ModelSource.Ec, identity.Source);
        Assert.True(identity.IsParsed);
    }

    [Fact]
    public void UnreadableFamily24HelpersExpandLikeVendorToPh4PRxx()
    {
        ModelIdentity identity = ModelRegistry.Read(new ModelRegistryTests.FakeEc(new() { [1856] = 24 }));

        Assert.Equal("PH4PRxx", identity.ProjectId);
        Assert.Equal(24, identity.RawProjectByte);
        Assert.Equal(ModelSource.Ec, identity.Source);
        Assert.True(identity.IsParsed);
    }

    [Fact]
    public void ExpandProjectIdZeroFillsUnreadHelpersIntoPh4ARxx()
    {
        Assert.Equal(5889, ModelRegistry.ExpandProjectIdForTest(23, _ => -1));
    }

    [Fact]
    public void ExpandProjectIdZeroFillsUnreadHelpersIntoPh4PRxx()
    {
        Assert.Equal(6145, ModelRegistry.ExpandProjectIdForTest(24, _ => -1));
    }

    [Fact]
    public void ASingleUnreadHelperStillZeroFillsFamily23LikeVendor()
    {
        ModelIdentity identity = ModelRegistry.Read(new ModelRegistryTests.FakeEc(new()
        {
            [1856] = 23,
            [1110] = 0,
            [2003] = 0,
            [1905] = 0,
            [1906] = 0,
            [1183] = 0,
        }));

        Assert.Equal("PH4ARxx", identity.ProjectId);
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
