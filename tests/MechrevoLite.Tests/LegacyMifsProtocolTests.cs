using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

public class LegacyMifsProtocolTests
{
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    public void ModeWritesUseBitlandOperationAndModeNumbers(int operatingMode, int vendorMode)
    {
        var request = LegacyMifsWmi.BuildModeRequest(true, operatingMode);
        Assert.Equal(32, request.Length);
        Assert.Equal(251, request[1]);
        Assert.Equal(8, request[3]);
        Assert.Equal(vendorMode, request[4]);
        Assert.Equal(0, request[0]);
        Assert.Equal(0, request[2]);
        Assert.All(request.Skip(5), value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(30, 0, 1)]
    [InlineData(32, 0, 1)]
    [InlineData(30, 1, 2)]
    [InlineData(32, 1, 2)]
    [InlineData(30, 2, 0)]
    [InlineData(32, 2, 0)]
    [InlineData(30, 3, 2)]
    public void WindowsAndRawAcpiRepliesUseTheirOwnPayloadOffsets(int length, int vendorMode, int expected)
    {
        var response = new byte[length];
        response[length == 30 ? 2 : 4] = (byte)vendorMode;
        Assert.Equal(expected, LegacyMifsWmi.ReadOperatingMode(response));
    }

    [Fact]
    public void CustomAndInvalidRepliesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LegacyMifsWmi.BuildModeRequest(true, 3));
        Assert.Throws<IOException>(() => LegacyMifsWmi.ReadOperatingMode(new byte[29]));
        var response = new byte[30];
        response[2] = 4;
        Assert.Throws<IOException>(() => LegacyMifsWmi.ReadOperatingMode(response));
    }
}
