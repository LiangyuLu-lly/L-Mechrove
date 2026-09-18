using MechrevoLite.Gpu;

namespace MechrevoLite.Tests;

/// <summary>
/// T16 失败/边界路径：代际判不出时**绝不并入某个已知代际**；事实表的"未知"不得被当成放行；
/// 指纹与持久化对损坏输入 fail-closed。happy 路径见 <see cref="GpuGenerationMatrixTests"/>。
/// </summary>
public class GpuGenerationMatrixFailTests
{
    sealed class MemoryStorage : IDgpuGenerationStorage
    {
        readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? Get(string key) => _values.TryGetValue(key, out string? value) ? value : null;
        public void Set(string key, string value) => _values[key] = value;
    }

    [Fact]
    public void UnknownGenerationIsNeverTreatedAsAFortySeries()
    {
        Assert.False(DisplayRouteMatrix.IsDisplayRouteWritePathProven(DgpuGenerationKind.Unknown));
        Assert.NotEqual(EvidenceMark.Proven, DisplayRouteMatrix.ServiceWritePathMark(DgpuGenerationKind.Unknown));
        Assert.Null(DisplayRouteMatrix.Find(DgpuGenerationKind.Unknown));
        Assert.Empty(DisplayRouteMatrix.ConsoleActions(DgpuGenerationKind.Unknown));
        Assert.NotEqual(DgpuIdentity.Unknown, new DgpuIdentity(
            DgpuGenerationKind.Gen40, DgpuProbeSource.MarketingName, true, "RTX 4060", "2882"));
    }

    [Fact]
    public void GpuRoutePolicyDoesNotBorrowAnotherGenerationsRestrictionsForUnknown()
    {
        // Unknown/NoDgpu 不套用 30 系的"无 iGPU-only / 无重启"限制——
        // 它们是"没判出来"，不是"30 系"。收紧只对已证实的代际生效。
        Assert.True(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.Unknown, DisplayRouteMatrix.Restart));
        Assert.True(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.NoDgpu, DisplayRouteMatrix.ToggleOn));
        // 而已判到的 30 系必须收紧。
        Assert.False(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.Gen30, DisplayRouteMatrix.Restart));
        Assert.False(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.Gen30, DisplayRouteMatrix.IgpuOnlyOn));
    }

    [Fact]
    public void AnActionOutsideTheConsoleVocabularyIsRejected()
    {
        Assert.False(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.Gen40, "SOMETHING_INVENTED"));
        Assert.False(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.Gen50, ""));
    }

    [Fact]
    public void MalformedDeviceIdsResolveToNothing()
    {
        Assert.Null(DgpuGenerationProbe.FromDeviceId(null));
        Assert.Null(DgpuGenerationProbe.FromDeviceId(""));
        Assert.Null(DgpuGenerationProbe.FromDeviceId("ZZZZ"));
        Assert.Null(DgpuGenerationProbe.FromDeviceId("123"));
        Assert.Null(DgpuGenerationProbe.FromDeviceId("FFFF"));
    }

    [Fact]
    public void AnEmptyAdapterListWithUnavailableEnumerationStaysUnknown()
    {
        DgpuIdentity identity = DgpuGenerationProbe.Resolve(Array.Empty<GpuAdapter>(), false);

        Assert.Equal(DgpuIdentity.Unknown, identity);
        Assert.False(identity.HasDgpu);
        // 只有"枚举可用且为空"才是无独显。
        Assert.Equal(DgpuGenerationKind.NoDgpu, DgpuGenerationProbe.Resolve(Array.Empty<GpuAdapter>(), true).Generation);
    }

    [Fact]
    public void FingerprintIsOrderInsensitiveButContentSensitive()
    {
        GpuAdapter a = new("RTX 4060", "10DE", "2882");
        GpuAdapter b = new("RTX 4070", "10DE", "2705");

        Assert.Equal(DgpuGenerationStore.Fingerprint(new[] { a, b }), DgpuGenerationStore.Fingerprint(new[] { b, a }));
        Assert.NotEqual(DgpuGenerationStore.Fingerprint(new[] { a }), DgpuGenerationStore.Fingerprint(new[] { b }));
        Assert.NotEqual(DgpuGenerationStore.Fingerprint(new[] { a }), DgpuGenerationStore.Fingerprint(Array.Empty<GpuAdapter>()));
    }

    [Fact]
    public void StoreRejectsACorruptPersistedGeneration()
    {
        var storage = new MemoryStorage();
        storage.Set(DgpuGenerationStore.GenerationKey, "not-a-generation");
        storage.Set(DgpuGenerationStore.FingerprintKey, "fp");

        Assert.Null(DgpuGenerationStore.Load(storage));
    }

    [Fact]
    public void StoreRejectsAnUnknownOrNoDgpuPersistedGeneration()
    {
        var storage = new MemoryStorage();
        storage.Set(DgpuGenerationStore.GenerationKey, DgpuGenerationKind.Unknown.ToString());

        Assert.Null(DgpuGenerationStore.Load(storage));
    }

    [Fact]
    public void EveryMatrixRowIsAConcreteResolvedGeneration()
    {
        Assert.All(DisplayRouteMatrix.Rows, row =>
        {
            Assert.True(row.Generation is DgpuGenerationKind.Gen30 or DgpuGenerationKind.Gen40 or DgpuGenerationKind.Gen50,
                $"{row.Generation} must not be a matrix row");
            Assert.NotEqual(EvidenceMark.Unknown, row.ConsoleCarrier.Mark);
        });
    }

    /// <summary>
    /// 绑定规则（勿重开）：控制台不写 <c>OemDisplayMode</c>/任何固件变量，也不存在写固件变量的接缝。
    /// IL 级断言（非源码 grep），并显式要求至少扫到 2 个程序集、若干个类型（防空扫描假绿）。
    /// </summary>
    [Fact]
    public void NoFirmwareVariableWriteSeamExistsInTheProduct()
    {
        IReadOnlyList<string> hits = FirmwareWriteGuard.Scan();

        Assert.Equal(2, FirmwareWriteGuard.AssembliesScanned);
        Assert.True(FirmwareWriteGuard.TypesScanned > 100,
            $"expected a real scan, only {FirmwareWriteGuard.TypesScanned} types enumerated");
        Assert.Empty(hits);
    }
}
