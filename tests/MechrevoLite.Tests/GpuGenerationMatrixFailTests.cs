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
    public void AResolvedGen30StaysClosedForActionsTheFactTableMarksAbsent()
    {
        // 已判到的 30 系必须收紧。Unknown/NoDgpu 的关闭断言在 UnresolvedGenerationAllowsNoConsoleAction。
        Assert.False(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.Gen30, DisplayRouteMatrix.Restart));
        Assert.False(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.Gen30, DisplayRouteMatrix.IgpuOnlyOn));
    }

    /// <summary>
    /// 事实表没有 Unknown/NoDgpu 行。没判出代际不等于"把 30/40/50 的动作并集都放开"。
    /// 未证实的能力不得放行（EvidenceMark 文件头）。
    /// </summary>
    [Theory]
    [InlineData(DgpuGenerationKind.Unknown, DisplayRouteMatrix.ToggleOn)]
    [InlineData(DgpuGenerationKind.Unknown, DisplayRouteMatrix.ToggleOff)]
    [InlineData(DgpuGenerationKind.Unknown, DisplayRouteMatrix.ToggleIgpu)]
    [InlineData(DgpuGenerationKind.Unknown, DisplayRouteMatrix.Restart)]
    [InlineData(DgpuGenerationKind.Unknown, DisplayRouteMatrix.IgpuOnlyOn)]
    [InlineData(DgpuGenerationKind.Unknown, DisplayRouteMatrix.IgpuOnlyOff)]
    [InlineData(DgpuGenerationKind.Unknown, DisplayRouteMatrix.IgpuOnlyAuto)]
    [InlineData(DgpuGenerationKind.Unknown, DisplayRouteMatrix.HotSwapOn)]
    [InlineData(DgpuGenerationKind.Unknown, DisplayRouteMatrix.HotSwapOff)]
    [InlineData(DgpuGenerationKind.NoDgpu, DisplayRouteMatrix.ToggleOn)]
    [InlineData(DgpuGenerationKind.NoDgpu, DisplayRouteMatrix.Restart)]
    [InlineData(DgpuGenerationKind.NoDgpu, DisplayRouteMatrix.IgpuOnlyOn)]
    [InlineData(DgpuGenerationKind.NoDgpu, DisplayRouteMatrix.HotSwapOn)]
    public void UnresolvedGenerationAllowsNoConsoleAction(DgpuGenerationKind generation, string action)
    {
        Assert.False(DisplayRoutePolicy.AllowsAction(generation, action));
        Assert.False(DisplayRoutePolicy.AllowsAction(generation, action, threeMode: true));
        Assert.False(DisplayRoutePolicy.AllowsAction(generation, action, threeMode: false));
        Assert.False(DisplayRoutePolicy.AllowsAction(generation, "SOMETHING_INVENTED"));
    }

    /// <summary>
    /// 仓库没有 10/20 系的 PCI 区间或控制台事实行。不得发明编码：营销名与 device-id 都留在 Unknown，
    /// 并且 Unknown 不解锁任何代际动作。
    /// </summary>
    [Fact]
    public void TenAndTwentySeriesStayUnknownAndUnlockNoGpuAction()
    {
        Assert.Null(DgpuGenerationProbe.FromMarketingName("NVIDIA GeForce GTX 1080"));
        Assert.Null(DgpuGenerationProbe.FromMarketingName("NVIDIA GeForce RTX 2060 Laptop GPU"));
        Assert.Null(DgpuGenerationProbe.FromDeviceId("1E90"));
        Assert.Equal(
            DgpuGenerationKind.Unknown,
            DgpuGenerationProbe.Resolve(
                new[] { new GpuAdapter("NVIDIA GeForce RTX 2060 Laptop GPU", "10DE", "1E90") },
                true).Generation);

        Assert.False(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.Unknown, DisplayRouteMatrix.ToggleOn));
        Assert.False(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.Unknown, DisplayRouteMatrix.Restart, true));
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
            Assert.NotEqual(EvidenceMark.Unknown, row.ConsoleProtocol.Mark);
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
