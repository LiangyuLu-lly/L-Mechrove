using MechrevoLite.Gpu;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T16（Wave D）happy 路径：**轴 2** dGPU 代际运行时探测 + 逐代显示路由事实表 + 代际解析持久化。
///
/// <para>两条正交轴：平台/机箱代号（轴 1）绝不决定代际（轴 2）。探测只吃 GPU 营销名与 NVIDIA
/// PCI device-id；<c>BIOS_PROJECT_ID</c> 不是判据。事实表里"路由怎么落地"只有 40 系 PROVEN。</para>
///
/// 失败路径见 <see cref="GpuGenerationMatrixFailTests"/>。
/// </summary>
public class GpuGenerationMatrixTests
{
    sealed class MemoryStorage : IDgpuGenerationStorage
    {
        readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? Get(string key) => _values.TryGetValue(key, out string? value) ? value : null;
        public void Set(string key, string value) => _values[key] = value;
    }

    static GpuAdapter Nvidia(string name, string device) => new(name, "10DE", device);

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3050 6GB Laptop GPU", DgpuGenerationKind.Gen30)]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", DgpuGenerationKind.Gen40)]
    [InlineData("NVIDIA GeForce RTX 5090 Laptop GPU", DgpuGenerationKind.Gen50)]
    public void MarketingNameResolvesToTheGeneration(string name, DgpuGenerationKind expected)
    {
        DgpuIdentity identity = DgpuGenerationProbe.Resolve(new[] { Nvidia(name, "0000") }, true);

        Assert.Equal(expected, identity.Generation);
        Assert.Equal(DgpuProbeSource.MarketingName, identity.Source);
        Assert.True(identity.HasDgpu);
        Assert.True(identity.IsResolved);
    }

    [Theory]
    [InlineData("2206", DgpuGenerationKind.Gen30)]
    [InlineData("2882", DgpuGenerationKind.Gen40)]
    [InlineData("2C02", DgpuGenerationKind.Gen50)]
    public void DeviceIdHighByteResolvesToTheGeneration(string deviceId, DgpuGenerationKind expected)
    {
        DgpuIdentity identity = DgpuGenerationProbe.Resolve(new[] { Nvidia("", deviceId) }, true);

        Assert.Equal(expected, identity.Generation);
        Assert.Equal(DgpuProbeSource.PciDeviceId, identity.Source);
    }

    [Fact]
    public void MarketingNameWinsOverTheDeviceIdHeuristic()
    {
        // 名称说 40 系、device-id 落到 50 系区间：名称是确定性更高的信号。
        DgpuIdentity identity = DgpuGenerationProbe.Resolve(
            new[] { Nvidia("NVIDIA GeForce RTX 4060 Laptop GPU", "2C02") }, true);

        Assert.Equal(DgpuGenerationKind.Gen40, identity.Generation);
        Assert.Equal(DgpuProbeSource.MarketingName, identity.Source);
    }

    [Fact]
    public void NoNvidiaAdapterIsNoDgpuWhichIsASeparateState()
    {
        var adapters = new[]
        {
            new GpuAdapter("Intel(R) UHD Graphics", "8086", "46A6"),
            new GpuAdapter("AMD Radeon 780M", "1002", "15BF"),
        };

        DgpuIdentity identity = DgpuGenerationProbe.Resolve(adapters, true);

        Assert.Equal(DgpuGenerationKind.NoDgpu, identity.Generation);
        Assert.False(identity.HasDgpu);
        Assert.NotEqual(DgpuGenerationKind.Unknown, identity.Generation);
        Assert.Null(DgpuGenerationProbe.Resolve(adapters, true).MarketingName);
    }

    [Fact]
    public void EnumerationUnavailableIsUnknownNeverNoDgpu()
    {
        DgpuIdentity identity = DgpuGenerationProbe.Resolve(Array.Empty<GpuAdapter>(), false);

        Assert.Equal(DgpuGenerationKind.Unknown, identity.Generation);
        Assert.NotEqual(DgpuGenerationKind.NoDgpu, identity.Generation);
        Assert.False(identity.HasDgpu);
        Assert.Equal(DgpuIdentity.Unknown, identity);
    }

    [Fact]
    public void AnNvidiaAdapterWithNoResolvableGenerationIsUnknownButStillHasDgpu()
    {
        DgpuIdentity identity = DgpuGenerationProbe.Resolve(
            new[] { new GpuAdapter("NVIDIA RTX A5000 Laptop GPU", "10DE", "1FB8") }, true);

        Assert.Equal(DgpuGenerationKind.Unknown, identity.Generation);
        Assert.True(identity.HasDgpu);
        Assert.False(identity.IsResolved);
    }

    [Fact]
    public void TwentySeriesIsOutsideTheAxisTwoValueDomain()
    {
        Assert.Null(DgpuGenerationProbe.FromMarketingName("NVIDIA GeForce RTX 2080 Laptop GPU"));
        Assert.Equal(
            DgpuGenerationKind.Unknown,
            DgpuGenerationProbe.Resolve(new[] { Nvidia("NVIDIA GeForce RTX 2080", "1E90") }, true).Generation);
    }

    [Fact]
    public void TheMatrixHasOneRowPerGenerationAndNeverUnknownOrNoDgpu()
    {
        // N11: the 40-series has two capability tiers (with / without 双显三模), so there are four
        // rows: 30, 40-with-3-mode, 40-without-3-mode, 50.
        Assert.Equal(4, DisplayRouteMatrix.Rows.Count);
        Assert.Equal(
            new[] { DgpuGenerationKind.Gen30, DgpuGenerationKind.Gen40, DgpuGenerationKind.Gen40, DgpuGenerationKind.Gen50 },
            DisplayRouteMatrix.Rows.Select(row => row.Generation).OrderBy(value => value).ToArray());
        Assert.Null(DisplayRouteMatrix.Find(DgpuGenerationKind.Unknown));
        Assert.Null(DisplayRouteMatrix.Find(DgpuGenerationKind.NoDgpu));
    }

    [Fact]
    public void OnlyFortyIsProvenForTheDisplayRouteWritePath()
    {
        Assert.Equal(EvidenceMark.Proven, DisplayRouteMatrix.ServiceWritePathMark(DgpuGenerationKind.Gen40));
        Assert.True(DisplayRouteMatrix.IsDisplayRouteWritePathProven(DgpuGenerationKind.Gen40));

        // 绑定规则：30 系与 50 系的落地方式保持 UNKNOWN（30 系未反编译、50 系 IL 混淆）。
        Assert.Equal(EvidenceMark.Unknown, DisplayRouteMatrix.ServiceWritePathMark(DgpuGenerationKind.Gen30));
        Assert.Equal(EvidenceMark.Unknown, DisplayRouteMatrix.ServiceWritePathMark(DgpuGenerationKind.Gen50));
        Assert.False(DisplayRouteMatrix.IsDisplayRouteWritePathProven(DgpuGenerationKind.Gen30));
        Assert.False(DisplayRouteMatrix.IsDisplayRouteWritePathProven(DgpuGenerationKind.Gen50));
    }

    [Fact]
    public void Gen30HasNoIgpuOnlyAndNoRestartWhichAreProvenAbsent()
    {
        GenerationRouteFacts row = DisplayRouteMatrix.Find(DgpuGenerationKind.Gen30)!;

        Assert.Equal(EvidenceMark.ProvenAbsent, row.IgpuOnly.Mark);
        Assert.Equal(EvidenceMark.ProvenAbsent, row.Restart.Mark);
        Assert.False(DisplayRouteMatrix.AllowsIgpuOnly(DgpuGenerationKind.Gen30));
        Assert.False(DisplayRouteMatrix.AllowsRestart(DgpuGenerationKind.Gen30));
        Assert.DoesNotContain(row.ConsoleActions, action => action.Contains("IGPU_ONLY", StringComparison.Ordinal));
        Assert.DoesNotContain(row.ConsoleActions, action => action.Contains("RESTART", StringComparison.Ordinal));
        Assert.Contains(DisplayRouteMatrix.ToggleOn, row.ConsoleActions);
        Assert.Contains(DisplayRouteMatrix.ToggleOff, row.ConsoleActions);
    }

    [Fact]
    public void Gen50HasTheFullActionVocabularyIncludingHotSwap()
    {
        GenerationRouteFacts row = DisplayRouteMatrix.Find(DgpuGenerationKind.Gen50)!;

        Assert.True(DisplayRouteMatrix.AllowsIgpuOnly(DgpuGenerationKind.Gen50));
        Assert.True(DisplayRouteMatrix.AllowsRestart(DgpuGenerationKind.Gen50));
        Assert.True(DisplayRouteMatrix.AllowsHotSwap(DgpuGenerationKind.Gen50));
        Assert.Contains(DisplayRouteMatrix.IgpuOnlyOn, row.ConsoleActions);
        Assert.Contains(DisplayRouteMatrix.IgpuOnlyOff, row.ConsoleActions);
        Assert.Contains(DisplayRouteMatrix.IgpuOnlyAuto, row.ConsoleActions);
        Assert.Contains(DisplayRouteMatrix.Restart, row.ConsoleActions);
        Assert.Contains(DisplayRouteMatrix.HotSwapOn, row.ConsoleActions);
    }

    [Fact]
    public void EveryCellCarriesItsEvidenceSource()
    {
        foreach (GenerationRouteFacts row in DisplayRouteMatrix.Rows)
        {
            RouteCell[] cells = { row.ConsoleProtocol, row.ServiceWritePath, row.IgpuOnly, row.Restart, row.HotSwap };
            foreach (RouteCell cell in cells)
            {
                Assert.False(string.IsNullOrWhiteSpace(cell.Source), $"{row.Generation} cell without source");
                Assert.False(string.IsNullOrWhiteSpace(cell.Detail), $"{row.Generation} cell without detail");
            }
            Assert.NotEmpty(row.ConsoleActions);
            Assert.All(row.ConsoleActions, action => Assert.False(string.IsNullOrWhiteSpace(action)));
        }
    }

    [Fact]
    public void TheConsoleSideProtocolIsMqttWithPerGenerationMarks()
    {
        // 控制台侧证据分级：40/50 系有逐方法反编译的代码 → PROVEN；
        // 30 系是 .NET Native（无 IL），只能到元数据+PDB 符号级 → INFERRED。
        Assert.Equal(EvidenceMark.Inferred, DisplayRouteMatrix.Find(DgpuGenerationKind.Gen30)!.ConsoleProtocol.Mark);
        Assert.Equal(EvidenceMark.Proven, DisplayRouteMatrix.Find(DgpuGenerationKind.Gen40)!.ConsoleProtocol.Mark);
        Assert.Equal(EvidenceMark.Proven, DisplayRouteMatrix.Find(DgpuGenerationKind.Gen50)!.ConsoleProtocol.Mark);

        foreach (GenerationRouteFacts row in DisplayRouteMatrix.Rows)
            Assert.Contains("MQTT", row.ConsoleProtocol.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gen30ConsoleSideProtocolIsInferredFromSymbolEvidence()
    {
        GenerationRouteFacts row = DisplayRouteMatrix.Find(DgpuGenerationKind.Gen30)!;

        // 控制台侧：程序集为 .NET Native（无 IL），依据元数据标识符堆 + 完整 PDB 符号表的符号级证据
        // → INFERRED（不得升为 PROVEN，见 g30-console-decompile.md）。
        Assert.Equal(EvidenceMark.Inferred, row.ConsoleProtocol.Mark);
        Assert.Contains("Setting/Control", row.ConsoleProtocol.Detail, StringComparison.Ordinal);
        Assert.Contains("g30-console-decompile", row.ConsoleProtocol.Source, StringComparison.OrdinalIgnoreCase);

        // 30 系只有 TOGGLE_ON/OFF 两个动作——不多不少。
        Assert.Equal(
            new[] { DisplayRouteMatrix.ToggleOn, DisplayRouteMatrix.ToggleOff }.OrderBy(value => value),
            row.ConsoleActions.OrderBy(value => value));

        // 服务侧写路径仍 UNKNOWN（只有控制台侧有符号级证据）。
        Assert.Equal(EvidenceMark.Unknown, row.ServiceWritePath.Mark);
    }

    [Fact]
    public void EveryGenerationSeparatesTheConsoleSideFromTheServiceSide()
    {
        foreach (GenerationRouteFacts row in DisplayRouteMatrix.Rows)
        {
            // 两列必须是各自独立的格：控制台侧至少 INFERRED（有证据链）/ 服务侧另有其标记。
            Assert.NotEqual(EvidenceMark.Unknown, row.ConsoleProtocol.Mark);
            Assert.False(string.IsNullOrWhiteSpace(row.ConsoleProtocol.Detail));
            Assert.False(string.IsNullOrWhiteSpace(row.ServiceWritePath.Detail));
            Assert.NotEqual(row.ConsoleProtocol.Detail, row.ServiceWritePath.Detail);
        }

        // 控制台侧证据分级：30 系仅符号级（INFERRED），40/50 系为代码级（PROVEN）。
        Assert.Equal(EvidenceMark.Inferred, DisplayRouteMatrix.Find(DgpuGenerationKind.Gen30)!.ConsoleProtocol.Mark);
        Assert.Equal(EvidenceMark.Proven, DisplayRouteMatrix.Find(DgpuGenerationKind.Gen40)!.ConsoleProtocol.Mark);
        Assert.Equal(EvidenceMark.Proven, DisplayRouteMatrix.Find(DgpuGenerationKind.Gen50)!.ConsoleProtocol.Mark);

        Assert.Equal(EvidenceMark.Proven, DisplayRouteMatrix.Find(DgpuGenerationKind.Gen40)!.ServiceWritePath.Mark);
        Assert.Equal(EvidenceMark.Unknown, DisplayRouteMatrix.Find(DgpuGenerationKind.Gen30)!.ServiceWritePath.Mark);
        Assert.Equal(EvidenceMark.Unknown, DisplayRouteMatrix.Find(DgpuGenerationKind.Gen50)!.ServiceWritePath.Mark);
    }

    [Fact]
    public void TheTwoVendorOemDisplayModeEncodingsAreRecordedAndInverted()
    {
        GenerationRouteFacts row = DisplayRouteMatrix.Find(DgpuGenerationKind.Gen40)!;

        OemDisplayModeEncoding amd = row.OemDisplayMode.Single(encoding => encoding.Platform == "AMD");
        OemDisplayModeEncoding intel = row.OemDisplayMode.Single(encoding => encoding.Platform == "Intel");

        Assert.Equal((1, 0, 2), (amd.Direct, amd.Hybrid, amd.Igpu));
        Assert.Equal((2, 4, 1), (intel.Direct, intel.Hybrid, intel.Igpu));
        // 两套编码恰好相反：直连/核显互换，混合也不同值。
        Assert.NotEqual(amd.Direct, intel.Direct);
        Assert.NotEqual(amd.Igpu, intel.Igpu);
        Assert.False(string.IsNullOrWhiteSpace(amd.Source));
        Assert.False(string.IsNullOrWhiteSpace(intel.Source));
    }

    [Fact]
    public void TheProbeNeverAcceptsABiosOrProjectCheatSignal()
    {
        string[] forbidden = { "bios", "project", "projectid", "platformcode", "chassis" };

        foreach (System.Reflection.MethodInfo method in typeof(DgpuGenerationProbe).GetMethods())
        {
            if (method.IsSpecialName) continue;
            foreach (System.Reflection.ParameterInfo parameter in method.GetParameters())
            {
                string name = parameter.Name?.ToLowerInvariant() ?? "";
                Assert.DoesNotContain(forbidden, token => name.Contains(token, StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void StoreReResolvesOnlyWhenTheHardwareFingerprintChanges()
    {
        var adapters = new[] { Nvidia("RTX 4060", "2882") };
        string fingerprint = DgpuGenerationStore.Fingerprint(adapters);

        Assert.True(DgpuGenerationStore.ShouldReResolve(null, fingerprint));
        Assert.True(DgpuGenerationStore.ShouldReResolve("something-else", fingerprint));
        Assert.False(DgpuGenerationStore.ShouldReResolve(fingerprint, fingerprint));
    }

    [Fact]
    public void StoreRoundTripsThroughTheStorageSeam()
    {
        var storage = new MemoryStorage();
        var identity = new DgpuIdentity(DgpuGenerationKind.Gen40, DgpuProbeSource.MarketingName, true, "RTX 4060", "2882");

        DgpuGenerationStore.Save(storage, identity, "fingerprint-1");

        DgpuIdentity? loaded = DgpuGenerationStore.Load(storage);
        Assert.NotNull(loaded);
        Assert.Equal(DgpuGenerationKind.Gen40, loaded!.Generation);
        Assert.Equal(DgpuProbeSource.MarketingName, loaded.Source);
        Assert.True(loaded.HasDgpu);
        Assert.Equal("fingerprint-1", storage.Get(DgpuGenerationStore.FingerprintKey));
    }

    /// <summary>
    /// C5 端到端接线：代际数据必须到达真实消费方（<see cref="MechrevoHw"/> 的门控谓词），
    /// 且换代际确实改变被门控行为——不是只断言单测返回值。
    /// </summary>
    [Fact]
    public void E2EWiring_ProviderGenerationReachesTheHardwareFacadeGate()
    {
        try
        {
            GpuGenerationProvider.Override = () => new DgpuIdentity(
                DgpuGenerationKind.Gen30, DgpuProbeSource.MarketingName, true, "RTX 3050", "25A2");
            using (var hardware = new MechrevoHw((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities()))
            {
                Assert.Equal(DgpuGenerationKind.Gen30, hardware.DgpuGeneration);
                Assert.False(hardware.IsGpuActionAllowedByGeneration(DisplayRouteMatrix.Restart));
                Assert.False(hardware.IsGpuActionAllowedByGeneration(DisplayRouteMatrix.IgpuOnlyOn));
                Assert.True(hardware.IsGpuActionAllowedByGeneration(DisplayRouteMatrix.ToggleOn));
            }

            GpuGenerationProvider.Override = () => new DgpuIdentity(
                DgpuGenerationKind.Gen40, DgpuProbeSource.MarketingName, true, "RTX 4060", "2882");
            using (var hardware = new MechrevoHw((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities()))
            {
                Assert.Equal(DgpuGenerationKind.Gen40, hardware.DgpuGeneration);
                Assert.True(hardware.IsGpuActionAllowedByGeneration(DisplayRouteMatrix.Restart));
            }
        }
        finally
        {
            GpuGenerationProvider.Override = null;
        }
    }
}
