using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T9（Wave B）失败 / 边界：处置表缺项、越界、重复或空理由都必须被拒；
/// 热切换查表缺任一服务值即 fail-closed；BLOCKED-HW 证据缺 probe/raw/hash 即不合格。
/// happy 断言见 <see cref="HardcodeMatrixLookupTests"/>。
/// </summary>
public class HardcodeMatrixLookupFailTests
{
    static HardcodeEntry Entry(string item, string note = "n", int[]? lines = null) =>
        new(item, "assumption", HardcodeDisposition.MatrixGated, lines ?? new[] { 1 }, note);

    static List<HardcodeEntry> AllButLast()
    {
        var entries = new List<HardcodeEntry>();
        for (int index = 1; index <= 21; index++) entries.Add(Entry("C" + index));
        return entries;
    }

    [Fact]
    public void AMissingHardcodeIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => HardcodeTableValidator.Validate(AllButLast()));
    }

    [Fact]
    public void AnUnknownHardcodeIsRejected()
    {
        List<HardcodeEntry> entries = AllButLast();
        entries.Add(Entry("C23"));

        Assert.Throws<InvalidDataException>(() => HardcodeTableValidator.Validate(entries));
    }

    [Fact]
    public void ADuplicateHardcodeIsRejected()
    {
        List<HardcodeEntry> entries = AllButLast();
        entries.Add(Entry("C1"));

        Assert.Throws<InvalidDataException>(() => HardcodeTableValidator.Validate(entries));
    }

    [Fact]
    public void AnEmptyDispositionNoteIsRejected()
    {
        List<HardcodeEntry> entries = AllButLast();
        entries.Add(Entry("C22", note: "  "));

        Assert.Throws<InvalidDataException>(() => HardcodeTableValidator.Validate(entries));
    }

    [Fact]
    public void AnEntryWithoutLinesIsRejected()
    {
        List<HardcodeEntry> entries = AllButLast();
        entries.Add(Entry("C22", lines: Array.Empty<int>()));

        Assert.Throws<InvalidDataException>(() => HardcodeTableValidator.Validate(entries));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void TheHotSwapLookupFailsClosedWhenEitherServiceValueIsUnavailable(bool switchBit, bool statusBit)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (switchBit) values["GpuHotSwapSwitchSupport"] = 1;
        if (statusBit) values["lgpuHotSwapSwitchStatus"] = 1;

        Assert.False(FeatureMatrix.FromValues(values).GpuHotSwapAvailable);
    }

    [Fact]
    public void TheHotSwapLookupFailsClosedWhenTheStatusBitIsZero()
    {
        Assert.False(FeatureMatrix.FromValues(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GpuHotSwapSwitchSupport"] = 1,
            ["lgpuHotSwapSwitchStatus"] = 0,
        }).GpuHotSwapAvailable);
    }

    [Fact]
    public void TheBlockedEvidenceWithoutProbeRawOrHashIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => HotSwapEvidence.Parse(
            """{"status":"BLOCKED-HW","probe":"","raw":"x","hash":"4ba60e72403bf6bc2dadf1a4ff5d61facd92cf6811f60dfb684f31e3475c8952","replacement":"x"}"""));
        Assert.Throws<InvalidDataException>(() => HotSwapEvidence.Parse(
            """{"status":"BLOCKED-HW","probe":"p","raw":"","hash":"4ba60e72403bf6bc2dadf1a4ff5d61facd92cf6811f60dfb684f31e3475c8952","replacement":"x"}"""));
        Assert.Throws<InvalidDataException>(() => HotSwapEvidence.Parse(
            """{"status":"BLOCKED-HW","probe":"p","raw":"r","hash":"","replacement":"x"}"""));
    }
}
