using Probe;

namespace MechrevoLite.Tests;

/// <summary>
/// W4 只读 EC 快照探针（<c>src/Probe/EcSnapshot*.cs</c>）的解析测试。
///
/// 全部走假传输：没有一行测试会打开 <c>\\.\ACPIDriver</c> 或读硬件。
/// 真机运行由 <c>probe snapshot</c> 单独完成。
/// </summary>
public class EcSnapshotProbeTests
{
    sealed class FakeEc : IEcReadTransport
    {
        readonly Dictionary<int, int> _values;
        public List<int> Reads { get; } = new();
        public FakeEc(Dictionary<int, int> values) => _values = values;
        public int ReadByte(int address)
        {
            Reads.Add(address);
            return _values.TryGetValue(address, out int value) ? value : -1;
        }
    }

    static EcRange[] Range(params EcRange[] ranges) => ranges;

    static EcSnapshotResult Result(Dictionary<int, int> bytes) =>
        new(new SortedDictionary<int, int>(bytes), Array.Empty<int>(), DateTimeOffset.Now);

    [Theory]
    [InlineData("0x400-0x4FF", 0x400, 0x4FF)]
    [InlineData("0xF00-0xFFF", 0xF00, 0xFFF)]
    [InlineData("0x740", 0x740, 0x740)]
    [InlineData("1024-1279", 1024, 1279)]
    public void ARangeParsesToInclusiveEndpoints(string text, int start, int end)
    {
        EcRange range = EcRange.Parse(text);
        Assert.Equal(start, range.Start);
        Assert.Equal(end, range.EndInclusive);
        Assert.Equal(end - start + 1, range.Length);
    }

    [Fact]
    public void AReversedRangeIsNormalised() =>
        Assert.Equal(new EcRange(0x400, 0x4FF), EcRange.Parse("0x4FF-0x400"));

    [Fact]
    public void CaptureReadsRangesInOrderAndDeduplicatesBreadcrumbs()
    {
        var transport = new FakeEc(new() { [0x400] = 0x11, [0x401] = 0x22, [0xB0] = 0x33 });

        EcSnapshotResult result = EcSnapshot.Capture(
            transport, Range(new EcRange(0x400, 0x401)), new[] { 0x400, 0xB0 }, delayMs: 0);

        Assert.Equal(0x11, result.Bytes[0x400]);
        Assert.Equal(0x22, result.Bytes[0x401]);
        Assert.Equal(0x33, result.Bytes[0xB0]);
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { 0x400, 0x401, 0xB0 }, transport.Reads); // 0x400 在范围内，不重复读
    }

    [Fact]
    public void CaptureRecordsUnreadableAddressesWithoutInventingAValue()
    {
        var transport = new FakeEc(new() { [0x400] = 0x11 });

        EcSnapshotResult result = EcSnapshot.Capture(
            transport, Range(new EcRange(0x400, 0x401)), Array.Empty<int>(), delayMs: 0);

        Assert.True(result.Bytes.ContainsKey(0x400));
        Assert.False(result.Bytes.ContainsKey(0x401));
        Assert.Equal(new[] { 0x401 }, result.Errors);
    }

    [Fact]
    public void TheHexDumpShowsBytesAndMarksReadErrors()
    {
        var transport = new FakeEc(new() { [0x400] = 0x00, [0x401] = 0x01 });
        EcSnapshotResult result = EcSnapshot.Capture(
            transport, Range(new EcRange(0x400, 0x401), new EcRange(0x410, 0x410)), Array.Empty<int>(), delayMs: 0);

        string dump = EcSnapshot.FormatHexDump(result, Range(new EcRange(0x400, 0x401), new EcRange(0x410, 0x410)));

        Assert.Contains("0x400  00 01", dump);
        Assert.Contains("0x410  ??", dump); // 0x410 读失败 → 不显示 0xFF
    }

    [Fact]
    public void LooseBreadcrumbsSkipAddressesAlreadyInsideARange()
    {
        var transport = new FakeEc(new() { [0x740] = 0x1A, [0xB0] = 0x00 });
        EcSnapshotResult result = EcSnapshot.Capture(
            transport, Range(new EcRange(0x740, 0x740)), new[] { 0xB0 }, delayMs: 0);

        string loose = EcSnapshot.FormatLooseBreadcrumbs(result, Range(new EcRange(0x740, 0x740)));

        Assert.DoesNotContain("0x740", loose);
        Assert.Contains("0xB0 = 0x00", loose);
    }

    [Fact]
    public void AnchorsAreParsedFromTheLiveMqttFrames()
    {
        var frames = new List<KeyValuePair<string, string>>
        {
            new("Fan/Status", """{"CPU_PL1":210,"CPU_PL2":210,"PL4":210,"GPU_ConfigurableTGPTarget":150,"CPU_TccOffset":0}"""),
            new("System/FanInfo", """{"CpuFanRpm":2372,"GpuFanRpm":2100,"CpuFanDuty":30,"GpuFanDuty":30}"""),
            new("System/CpuInfo", """{"CpuTemperature":52}"""),
            new("System/GpuInfo", """{"GpuTemperature":48}"""),
            new("System/BatteryInfo", """{"BatteryLifePercent":100}"""),
            new("junk", "not json at all"),
        };

        EcAnchors anchors = EcAnchors.FromFrames(frames);

        Assert.Equal(210, anchors.Pl1);
        Assert.Equal(210, anchors.Pl4);
        Assert.Equal(150, anchors.Tgp);
        Assert.Equal(0, anchors.TccOffset);   // 0 是合法 TCC，不是「未知」
        Assert.Equal(2372, anchors.CpuFanRpm);
        Assert.Equal(100, anchors.BatteryPercent);
        Assert.Equal(52, anchors.CpuTemp);
        Assert.Equal(48, anchors.GpuTemp);
    }

    [Fact]
    public void AmdPowerFieldNamesFallBackWhenTheGenericKeysAreAbsent() =>
        Assert.Equal(65, EcAnchors.FromFrames(new[]
        {
            new KeyValuePair<string, string>("Fan/Status", """{"CPU_AmdSPL":65,"CPU_AmdSPPT":115,"CPU_AmdFPPT":140}"""),
        }).Pl1);

    [Fact]
    public void AnchorChecksReportConsistencyAgainstTheLiveValues()
    {
        EcSnapshotResult result = Result(new()
        {
            [0x783] = 210, [0x784] = 210, [0x785] = 210, [0x4AB] = 100,
            [0x464] = 0x09, [0x465] = 0x44, [0x744] = 70,
            [0x7B9] = 0x50, [0x7D0] = 0x3C, [0x740] = 0x1A, [0x74C] = 0x1A,
        });
        var anchors = new EcAnchors(210, 210, 210, 150, 0, 2372, 2100, 52, 48, 100);

        IReadOnlyList<AnchorCheck> checks = EcSnapshotReport.AnalyzeAnchors(result, anchors);

        Assert.Equal("consistent", checks.Single(c => c.Register == "0x783").Verdict);
        Assert.Equal("consistent", checks.Single(c => c.Register == "0x4AB").Verdict);
        Assert.Contains("high byte", checks.Single(c => c.Register == "0x464/0x465").Verdict);
        Assert.Contains("NOT TGP", checks.Single(c => c.Register == "0x744").Verdict);
        Assert.Contains("0x50", checks.Single(c => c.Register == "0x7B9/0x7D0").Observed);
    }

    [Fact]
    public void AnchorChecksFlagAPowerMismatch()
    {
        EcSnapshotResult result = Result(new() { [0x783] = 99, [0x784] = 99, [0x785] = 99, [0x4AB] = 100 });
        var anchors = new EcAnchors(210, 210, 210, -1, -1, -1, -1, -1, -1, 100);

        AnchorCheck pl1 = EcSnapshotReport.AnalyzeAnchors(result, anchors).Single(c => c.Register == "0x783");

        Assert.Contains("MISMATCH", pl1.Verdict);
        Assert.Contains("210", pl1.Verdict);
    }

    /// <summary>只读构造保证：探针的任何源文件都不得出现写 IOCTL 或写方法。</summary>
    [Fact]
    public void TheProbeIsReadOnlyByConstruction()
    {
        foreach (string name in new[] { "EcSnapshot.cs", "EcSnapshotReport.cs" })
        {
            string source = File.ReadAllText(RepoFile(Path.Combine("src", "Probe", name)));
            Assert.Contains("0x9C40A488", source);
            Assert.DoesNotContain("0x9C40A48C", source); // ECWRITE
            Assert.DoesNotContain("ECWRITE", source);
            Assert.DoesNotContain("WriteReg", source);
            Assert.DoesNotContain("MmWriteByte", source);
        }
    }

    static string RepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, relativePath);
    }
}
