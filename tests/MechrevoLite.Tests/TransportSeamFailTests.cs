using System.Reflection;
using MechrevoLite.Hardware;
using Probe;

namespace MechrevoLite.Tests;

/// <summary>
/// T31（Wave A0）失败 / 边界路径：
/// <list type="bullet">
/// <item>EC 读不到或超时 → fail-closed，异常不得逃出上层；-1（读不到）与 0xFF（合法读值）必须可区分；</item>
/// <item>限充在 EC 不可用时 fail-closed，字节编码不变（回归钉）；</item>
/// <item>EC 写边界（T15 口径的反射断言）：显式枚举 MechrevoLiteWin + Probe 两程序集，
///   程序集数 != 2 或类型数 == 0 即失败（防空扫描假绿）；除既有基线外不得出现新的 EC 写点；
///   **仅 EC 口径**——NVRAM/SMU 直写不会让它失败；</item>
/// <item>读 IOCTL 的所有者只剩接缝与获豁免的限充同句柄读回（合并的机械证明）；</item>
/// <item>探针自带的重复 interop 与错误 IOCTL 注释已消失；</item>
/// <item>读 IOCTL 清点表（T31(e) 交付物）逐项有判定与理由。</item>
/// </list>
/// </summary>
public class TransportSeamFailTests
{
    const uint EcReadIoctl = 0x9C40A488;
    const uint EcWriteIoctl = 0x9C40A48C;

    /// <summary>-1 = 读不到；给定地址可读，但值恒为 0xFF（合法读值，不等于失败）。</summary>
    sealed class UnreadableEc : IEcReadTransport
    {
        readonly HashSet<int> _readable;
        public UnreadableEc(params int[] readable) => _readable = new HashSet<int>(readable);
        public int ReadByte(int address) => _readable.Contains(address) ? 0xFF : -1;
    }

    /// <summary>对某个地址抛 <see cref="TimeoutException"/>（模拟驱动卡死/超时），其余可读。</summary>
    sealed class TimingOutEc : IEcReadTransport
    {
        readonly int _timeoutAddress;
        public List<int> Attempts { get; } = new();
        public TimingOutEc(int timeoutAddress) => _timeoutAddress = timeoutAddress;
        public int ReadByte(int address)
        {
            Attempts.Add(address);
            if (address == _timeoutAddress) throw new TimeoutException($"EC read 0x{address:X3} timed out");
            return 0x42;
        }
    }

    [Fact]
    public void AnUnreadableEcReadIsFailClosedAndDistinctFromA0xFFReading()
    {
        EcSnapshotResult result = EcSnapshot.Capture(
            new UnreadableEc(0x4AB),
            new[] { new EcRange(0x4AB, 0x4AC) },
            Array.Empty<int>(),
            delayMs: 0);

        Assert.Equal(0xFF, result.Bytes[0x4AB]);          // 合法读值就是 0xFF
        Assert.False(result.Bytes.ContainsKey(0x4AC));    // 读不到 → 不编造 0xFF
        Assert.Equal(new[] { 0x4AC }, result.Errors);
    }

    [Fact]
    public void AnEcTransportThatTimesOutDoesNotEscapeAsAnException()
    {
        var ec = new TimingOutEc(0x4AB);

        EcSnapshotResult result = EcSnapshot.Capture(
            ec,
            new[] { new EcRange(0x4AA, 0x4AC) },
            Array.Empty<int>(),
            delayMs: 0);

        Assert.Equal(0x42, result.Bytes[0x4AA]);          // 单个地址超时不影响其余地址
        Assert.Equal(0x42, result.Bytes[0x4AC]);
        Assert.Equal(new[] { 0x4AB }, result.Errors);
        Assert.NotNull(result.ErrorDetails);
        Assert.Contains(result.ErrorDetails!, detail => detail.Contains("0x4AB") && detail.Contains(nameof(TimeoutException)));
        Assert.Equal(new[] { 0x4AA, 0x4AB, 0x4AC }, ec.Attempts);
    }

    [Fact]
    public void ChargeLimitStaysFailClosedWhenTheEcReadIsUnavailable()
    {
        Func<int, (bool Success, int AppliedPercent)>? previousSet = EcChargeLimit.TrySetOverride;
        Func<int>? previousRead = EcChargeLimit.ReadPercentOverride;
        try
        {
            EcChargeLimit.TrySetOverride = _ => (false, -1);
            EcChargeLimit.ReadPercentOverride = () => -1;

            Assert.False(EcChargeLimit.TrySet(80, out int applied));
            Assert.Equal(-1, applied);
            Assert.Equal(-1, EcChargeLimit.ReadPercent());   // 读不到 ≠ 100%
        }
        finally
        {
            EcChargeLimit.TrySetOverride = previousSet;
            EcChargeLimit.ReadPercentOverride = previousRead;
        }
    }

    [Fact]
    public void TheChargeLimitByteEncodingIsUnchanged()
    {
        Assert.Equal(0x7B9, EcChargeLimit.UpperRegister);
        Assert.Equal(0x7D0, EcChargeLimit.LowerRegister);
        Assert.Equal(0, EcChargeLimit.ValueFor(100));       // 满充 = 固件 no-limit 值
        Assert.Equal(80, EcChargeLimit.ValueFor(80));
        Assert.Equal(75, EcChargeLimit.LowerValueFor(80));  // 80 - 5 迟滞
        Assert.Equal(100, EcChargeLimit.PercentFor(0));
        Assert.Equal(80, EcChargeLimit.PercentFor(0x50));
        Assert.True(EcChargeLimit.IsSupportedLimit(40));
        Assert.False(EcChargeLimit.IsSupportedLimit(39));
    }

    // ---- EC 写边界（T15 口径）------------------------------------------------

    sealed record EcWriteSite(string AssemblyName, string TypeName, string Member);

    /// <summary>唯一获批的 EC 直写点 + 既有探针诊断写路径（冻结基线，写入理由）。</summary>
    static readonly Dictionary<string, string> EcWriteAllowlist = new(StringComparer.Ordinal)
    {
        ["MechrevoLite.Hardware.EcChargeLimit"] =
            "唯一获批 EC 直写：限充 0x7B9/0x7D0，写 IOCTL 0x9C40A48C，与厂商逐字节等价；T31 未改动",
        ["Probe.EcProbe"] =
            "PRE-EXISTING 非产品诊断写路径（W4 探针 CLI，commit be33135）；冻结基线：任何新增 EC 写点都会让本测试失败；T31 未获授权删除它",
    };

    static string? Validate(IReadOnlyList<Assembly> assemblies, IReadOnlyList<Type> types, IReadOnlyList<EcWriteSite> sites)
    {
        if (assemblies.Count != 2) return $"scanned {assemblies.Count} assemblies, expected exactly 2 (MechrevoLiteWin + Probe)";
        if (assemblies.Select(a => a.GetName().Name).Distinct(StringComparer.Ordinal).Count() != 2)
            return "the two scanned assemblies are not distinct";
        if (types.Count == 0) return "scanned 0 types - an empty scan is a false green";

        var unknown = sites.Select(s => s.TypeName).Distinct(StringComparer.Ordinal)
            .Where(name => !EcWriteAllowlist.ContainsKey(name)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0) return "EC write site(s) outside the allowlist: " + string.Join(", ", unknown);

        var matched = new HashSet<string>(sites.Select(s => s.TypeName), StringComparer.Ordinal);
        string[] stale = EcWriteAllowlist.Keys.Where(key => !matched.Contains(key)).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        if (stale.Length > 0) return "stale allowlist entry(ies): " + string.Join(", ", stale);

        return null;
    }

    static Assembly[] ScannedAssemblies() =>
        new[] { typeof(MechrevoHw).Assembly, typeof(IEcReadTransport).Assembly };

    static Type[] ScannedTypes(Assembly[] assemblies) =>
        AllTypes(assemblies.SelectMany(a => a.GetTypes())).ToArray();

    static IEnumerable<Type> AllTypes(IEnumerable<Type> types)
    {
        foreach (Type type in types)
        {
            yield return type;
            foreach (Type nested in AllTypes(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)))
                yield return nested;
        }
    }

    static IEnumerable<MethodBase> Methods(Type type)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        foreach (MethodBase method in type.GetMethods(Flags).Cast<MethodBase>()) yield return method;
        foreach (MethodBase ctor in type.GetConstructors(Flags).Cast<MethodBase>()) yield return ctor;
        if (type.TypeInitializer is { } initializer) yield return initializer;
    }

    static bool IsIoctl(object? raw, uint ioctl) => raw switch
    {
        uint value => value == ioctl,
        int value => unchecked((uint)value) == ioctl,
        ushort value => value == ioctl,
        byte value => value == ioctl,
        long value => unchecked((ulong)value) == ioctl,
        ulong value => value == ioctl,
        _ => false,
    };

    static bool OwnsIoctlConstant(Type type, uint ioctl) =>
        type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Any(field => field.IsLiteral && IsIoctl(field.GetRawConstantValue(), ioctl));

    /// <summary>方法体里内联的 ldc.i4 &lt;ioctl&gt;：EC 写 IOCTL 一定以这个立即数出现在调用点。</summary>
    static bool UsesIoctlImmediate(MethodBase method, uint ioctl)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null) return false;
        byte b0 = (byte)(ioctl & 0xFF), b1 = (byte)((ioctl >> 8) & 0xFF), b2 = (byte)((ioctl >> 16) & 0xFF), b3 = (byte)((ioctl >> 24) & 0xFF);
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] == 0x20 && il[i + 1] == b0 && il[i + 2] == b1 && il[i + 3] == b2 && il[i + 4] == b3) return true;
        }
        return false;
    }

    static List<EcWriteSite> FindEcWriteSites(IReadOnlyList<Assembly> assemblies, IReadOnlyList<Type> types)
    {
        var sites = new List<EcWriteSite>();
        foreach (Assembly assembly in assemblies)
        {
            string assemblyName = assembly.GetName().Name!;
            foreach (Type type in types.Where(t => t.Assembly == assembly))
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                {
                    if (field.IsLiteral && IsIoctl(field.GetRawConstantValue(), EcWriteIoctl))
                        sites.Add(new EcWriteSite(assemblyName, type.FullName!, field.Name));
                }
                foreach (MethodBase method in Methods(type))
                {
                    if (UsesIoctlImmediate(method, EcWriteIoctl))
                        sites.Add(new EcWriteSite(assemblyName, type.FullName!, method.Name));
                }
            }
        }
        return sites.Distinct().ToList();
    }

    [Fact]
    public void TheEcWriteBoundaryGuardScansExactlyTwoAssemblies()
    {
        Assembly[] assemblies = ScannedAssemblies();

        Assert.Equal(2, assemblies.Length);
        Assert.Equal(2, assemblies.Select(a => a.GetName().Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new[] { "L-Mechrevo", "Probe" }, assemblies.Select(a => a.GetName().Name).OrderBy(n => n, StringComparer.Ordinal));

        Type[] types = ScannedTypes(assemblies);
        Assert.True(types.Length > 0, "scanned 0 types - an empty scan is a false green");
        Assert.Null(Validate(assemblies, types, FindEcWriteSites(assemblies, types)));
    }

    [Fact]
    public void TheEcWriteBoundaryGuardRejectsAnEmptyScanOrAWrongAssemblyCount()
    {
        Type[] types = { typeof(MechrevoHw) };
        var sites = new[] { new EcWriteSite("L-Mechrevo", "MechrevoLite.Hardware.EcChargeLimit", "Write") };
        Assembly app = typeof(MechrevoHw).Assembly;
        Assembly probe = typeof(IEcReadTransport).Assembly;

        Assert.NotNull(Validate(Array.Empty<Assembly>(), types, sites));
        Assert.NotNull(Validate(new[] { app }, types, sites));
        Assert.NotNull(Validate(new[] { app, app }, types, sites));
        Assert.NotNull(Validate(new[] { app, probe }, Array.Empty<Type>(), sites));
    }

    [Fact]
    public void TheEcWriteBoundaryGuardFlagsAnUnknownEcWriteSite()
    {
        Assembly[] assemblies = ScannedAssemblies();
        Type[] types = ScannedTypes(assemblies);
        var unknown = new EcWriteSite("L-Mechrevo", "MechrevoLite.Hardware.SomeNewEcWriter", "WriteByte");

        string? error = Validate(assemblies, types, new[] { unknown });

        Assert.NotNull(error);
        Assert.Contains("SomeNewEcWriter", error!);
    }

    [Fact]
    public void TheEcWriteBoundaryGuardAllowsOnlyTheTwoKnownSites()
    {
        Assembly[] assemblies = ScannedAssemblies();
        Type[] types = ScannedTypes(assemblies);

        List<EcWriteSite> sites = FindEcWriteSites(assemblies, types);

        Assert.Null(Validate(assemblies, types, sites));
        string[] owners = sites.Select(s => s.TypeName).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "MechrevoLite.Hardware.EcChargeLimit", "Probe.EcProbe" }, owners);
    }

    [Fact]
    public void TheSingleEcReadIoctlIsOwnedByTheSeamAndTheExemptedChargeLimit()
    {
        Assembly[] assemblies = ScannedAssemblies();
        Type[] types = ScannedTypes(assemblies);

        string[] owners = types.Where(t => OwnsIoctlConstant(t, EcReadIoctl))
            .Select(t => t.FullName!).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "MechrevoLite.Hardware.EcChargeLimit", "Probe.AcpiDriverIo" }, owners);
    }

    [Fact]
    public void EcProbeHasNoOwnEcInteropAndItsWrongIoctlCommentIsGone()
    {
        string source = File.ReadAllText(RepoFile(@"src\Probe\EcProbe.cs"));

        Assert.DoesNotContain("0x9C402108", source);   // 注释里那个假 IOCTL 值（真值 0x9C40A488）
        Assert.Contains("0x9C40A488", source);         // 改正后的真值：读路径已并入接缝
        Assert.DoesNotContain("DllImport", source);    // 自带的重复 interop 已删
        Assert.DoesNotContain("CreateFile", source);
        Assert.DoesNotContain("DeviceIoControl", source);
    }

    // ---- Pawn\ 端口 I/O 漏网（T15 口径补强）-----------------------------------

    /// <summary>
    /// Pawn\ 源码里出现任一 EC 访问指纹即视为 EC 写漏网。T15 的反射断言按「EC 写 IOCTL」口径，
    /// 端口 I/O 型 EC 写（不经过 <c>\\.\ACPIDriver</c>）会漏网；Pawn\ 是唯一获批的非 EC 直写载体
    /// （SMU，经 PawnIO 设备 0xA1B22104），这里显式钉死它不碰任何 EC 寄存器。
    /// </summary>
    static readonly string[] EcAccessFingerprints =
    {
        "0x9C40A488", "0x9C40A48C",   // EC 读 / 写 IOCTL
        "0x9C402108", "0x9C40210C",   // 旧 / 错误的 IOCTL 形状
        "0x7B9", "0x7D0",             // 限充上限 / 复充下限寄存器
        "ACPIDriver", "ECREAD", "ECWRITE", "ECRW",
    };

    static string? ValidatePawnSources(IReadOnlyList<string> sources)
    {
        if (sources.Count == 0) return "scanned 0 Pawn sources - an empty scan is a false green";
        foreach (string source in sources)
            foreach (string token in EcAccessFingerprints)
                if (source.Contains(token, StringComparison.OrdinalIgnoreCase))
                    return $"Pawn source carries the EC access fingerprint '{token}'";
        return null;
    }

    static string[] PawnSources() =>
        Directory.GetFiles(RepoFile(@"src\MechrevoLiteWin\Pawn"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText).ToArray();

    [Fact]
    public void ThePawnCarrierPerformsNoEcRegisterWrites()
    {
        string[] sources = PawnSources();
        Assert.True(sources.Length > 0, "scanned 0 Pawn sources - an empty scan is a false green");
        Assert.Null(ValidatePawnSources(sources));

        Type[] pawnTypes = ScannedTypes(ScannedAssemblies())
            .Where(t => (t.Namespace ?? "").StartsWith("PawnIO", StringComparison.Ordinal)).ToArray();
        Assert.True(pawnTypes.Length > 0, "scanned 0 PawnIO types - an empty scan is a false green");

        Assert.DoesNotContain(pawnTypes, t => OwnsIoctlConstant(t, EcReadIoctl) || OwnsIoctlConstant(t, EcWriteIoctl));
        Assert.DoesNotContain(pawnTypes, t => Methods(t).Any(m => UsesIoctlImmediate(m, EcReadIoctl) || UsesIoctlImmediate(m, EcWriteIoctl)));
    }

    [Fact]
    public void ThePawnEcGuardRejectsAnEcAccessFingerprint()
    {
        Assert.NotNull(ValidatePawnSources(Array.Empty<string>()));
        Assert.NotNull(ValidatePawnSources(new[] { "const uint ioctl = 0x9C40A48C;" }));
        Assert.NotNull(ValidatePawnSources(new[] { "// charge limit 0x7B9" }));
        Assert.NotNull(ValidatePawnSources(new[] { @"open \\.\ACPIDriver" }));
        Assert.Null(ValidatePawnSources(new[] { "const uint execute = 0xA1B22104;" }));
    }

    [Fact]
    public void TheIoctlInventoryCoversEveryReadSiteWithADisposition()
    {
        var readSites = new[]
        {
            new { Site = @"src\Probe\EcSnapshot.cs:23", Ioctl = "0x9C40A488", Disposition = "SEAM", Reason = "接缝真源：唯一 interop 与 IOCTL 常量（合并目标）" },
            new { Site = @"src\Probe\EcProbe.cs:7,9-13", Ioctl = "0x9C40A488", Disposition = "MERGE", Reason = "2 B 入参是已知错误形状；读路径并入接缝，自带 interop 与错误注释一并删除" },
            new { Site = @"src\MechrevoLiteWin\Hardware\EcChargeLimit.cs:46,166-181", Ioctl = "0x9C40A488", Disposition = "EXEMPT_WITH_REASON", Reason = "限充写事务的同句柄读回（:126-143 写后校验与回滚）；改走接缝会拆散事务边界" },
            new { Site = @"src\Probe\EcSnapshotReport.cs:143", Ioctl = "0x9C40A488", Disposition = "DOC_ONLY", Reason = "只打印 IOCTL 形状，无实现" },
        };
        var writeSites = new[]
        {
            new { Site = @"src\MechrevoLiteWin\Hardware\EcChargeLimit.cs:47,191", Ioctl = "0x9C40A48C", Disposition = "PERMITTED", Reason = "唯一获批 EC 直写（限充），T31 未改动" },
            new { Site = @"src\Probe\EcProbe.cs:40,45-71", Ioctl = "0x9C40A48C", Disposition = "PRE_EXISTING_BASELINE", Reason = "非产品诊断写路径（W4 探针 CLI），T31 未新增未改动；写边界测试冻结基线" },
        };

        Assert.Equal(4, readSites.Length);
        Assert.Equal(2, writeSites.Length);
        Assert.All(readSites, site =>
        {
            Assert.True(File.Exists(RepoFile(site.Site.Split(':')[0])), site.Site);
            Assert.False(string.IsNullOrWhiteSpace(site.Disposition));
            Assert.False(string.IsNullOrWhiteSpace(site.Reason));
        });
        Assert.All(writeSites, site => Assert.False(string.IsNullOrWhiteSpace(site.Reason)));

        string evidenceDir = RepoFile(Path.Combine(".omo", "evidence"));
        if (!Directory.Exists(evidenceDir)) return;   // 非 QA 目录（fresh clone）只断言表，不落盘

        string json = System.Text.Json.JsonSerializer.Serialize(
            new { task = "T31", read_ioctl = "0x9C40A488", write_ioctl = "0x9C40A48C", read_sites = readSites, write_sites = writeSites },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        string path = Path.Combine(evidenceDir, "t31-ioctl-inventory.json");
        File.WriteAllText(path, json + Environment.NewLine, new System.Text.UTF8Encoding(false));

        string written = File.ReadAllText(path);
        Assert.Contains("0x9C40A488", written);
        Assert.Contains("0x9C40A48C", written);
        foreach (var site in readSites) Assert.Contains(site.Disposition, written);
        foreach (var site in writeSites) Assert.Contains(site.Disposition, written);
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
