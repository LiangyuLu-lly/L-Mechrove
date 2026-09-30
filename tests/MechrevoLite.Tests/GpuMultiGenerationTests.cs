using System.Reflection;
using MechrevoLite.Gpu;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// beta21 显卡多代适配（docs/hardware/gpu-modes-implementation-plan.md §9）：服务档位、硬件回读推断、
/// NVIDIA 首选 GPU、档位放行矩阵、行布局、重启后核对、只读守卫。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class GpuMultiGenerationTests
{
    // ------------------------------------------------------------ 服务档位

    static readonly HashSet<string> NoFiles = new(StringComparer.OrdinalIgnoreCase);

    static GcuServiceTier Tier(string? imagePath, string? version, params string[] files)
    {
        var existing = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        return GcuServiceTierProbe.Detect(
            imagePath,
            path => version is not null && path.EndsWith("GCUService.exe", StringComparison.OrdinalIgnoreCase)
                ? Version.Parse(version)
                : null,
            path => existing.Contains(path));
    }

    const string Modern = @"C:\Program Files\L-Mechrevo\GCU\AiStoneService";
    const string Legacy = @"C:\Program Files\L-Mechrevo\GCU\UniwillService";
    const string Vendor = @"C:\Program Files\OEM\ControlCenter\AiStoneService";

    [Fact]
    public void OurNewestPayloadIsModern12()
    {
        Assert.Equal(GcuServiceTier.Modern12, Tier(
            "\"" + Modern + "\\GCUBridge.exe\"", "1.2.0.0",
            Modern + @"\MyControlCenter\GCUService.exe", Modern + @"\MyControlCenter\UEFI_Firmware.dll"));
    }

    [Fact]
    public void OurLegacyPayloadWithoutTheUefiLibraryIsLegacy1020()
    {
        Assert.Equal(GcuServiceTier.Legacy1020, Tier(
            Legacy + @"\GCUBridge.exe", "1.0.2.47", Legacy + @"\MyControlCenter\GCUService.exe"));
        // 同版本号但带 UEFI 库：不是 10/20 的那一版，按厂商交集用。
        Assert.Equal(GcuServiceTier.Foreign, Tier(
            Legacy + @"\GCUBridge.exe", "1.0.2.47",
            Legacy + @"\MyControlCenter\GCUService.exe", Legacy + @"\MyControlCenter\UEFI_Firmware.dll"));
    }

    [Fact]
    public void AVendorInstalledServiceIsForeign()
    {
        Assert.Equal(GcuServiceTier.Foreign, Tier(
            "\"" + Vendor + "\\GCUBridge.exe\" -service", "1.0.2.70", Vendor + @"\MyControlCenter\GCUService.exe"));
        // 我方目录下手动装的 1.0.2.70（40 系载荷）同样按厂商交集。
        Assert.Equal(GcuServiceTier.Foreign, Tier(
            Modern + @"\GCUBridge.exe", "1.0.2.70",
            Modern + @"\MyControlCenter\GCUService.exe", Modern + @"\MyControlCenter\UEFI_Firmware.dll"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingServiceIsUnknown(string? imagePath)
    {
        Assert.Equal(GcuServiceTier.Unknown, Tier(imagePath, "1.2.0.0"));
    }

    [Fact]
    public void AnUnreadableServiceVersionIsUnknown()
    {
        Assert.Equal(GcuServiceTier.Unknown, Tier(Modern + @"\GCUBridge.exe", null, Modern + @"\MyControlCenter\GCUService.exe"));
        Assert.Equal(GcuServiceTier.Unknown, Tier(Modern + @"\GCUBridge.exe", "1.2.0.0"));   // exe 不在
    }

    [Theory]
    [InlineData("\"C:\\A B\\GCUBridge.exe\" -k", "C:\\A B\\GCUBridge.exe")]
    [InlineData("C:\\A\\GCUBridge.exe -k", "C:\\A\\GCUBridge.exe")]
    [InlineData("C:\\A\\GCUBridge.exe", "C:\\A\\GCUBridge.exe")]
    public void TheImagePathIsStrippedOfQuotesAndArguments(string imagePath, string expected)
    {
        Assert.Equal(expected, GcuServiceTierProbe.ExecutableOf(imagePath));
    }

    // ------------------------------------------------------------ 回读推断（§4.5）

    [Theory]
    [InlineData(PanelAdapterKind.Discrete, DgpuPresence.Present, GpuRoute.Direct)]
    [InlineData(PanelAdapterKind.Discrete, DgpuPresence.Unknown, GpuRoute.Direct)]
    [InlineData(PanelAdapterKind.Integrated, DgpuPresence.Present, GpuRoute.Hybrid)]
    [InlineData(PanelAdapterKind.Integrated, DgpuPresence.Disabled, GpuRoute.IgpuOnly)]
    [InlineData(PanelAdapterKind.Integrated, DgpuPresence.Absent, GpuRoute.IgpuOnly)]
    [InlineData(PanelAdapterKind.Integrated, DgpuPresence.Unknown, GpuRoute.Unknown)]
    [InlineData(PanelAdapterKind.Unknown, DgpuPresence.Present, GpuRoute.Unknown)]
    [InlineData(PanelAdapterKind.Unknown, DgpuPresence.Absent, GpuRoute.Unknown)]
    public void TheRouteIsInferredFromTheReadbackTable(PanelAdapterKind panel, DgpuPresence dgpu, GpuRoute expected)
    {
        Assert.Equal(expected, GpuRouteInference.Infer(panel, dgpu));
    }

    [Theory]
    [InlineData(@"\\?\PCI#VEN_8086&DEV_7D67&SUBSYS_00000000#3&11583659&0&10#{5b45201d}", PanelAdapterKind.Integrated)]
    [InlineData(@"\\?\PCI#VEN_1002&DEV_15BF&SUBSYS_00000000#4&1&0#{5b45201d}", PanelAdapterKind.Integrated)]
    [InlineData(@"\\?\PCI#VEN_10DE&DEV_2C19&SUBSYS_00000000#4&2&0#{5b45201d}", PanelAdapterKind.Discrete)]
    [InlineData(@"\\?\ROOT#BasicDisplay#0000#{5b45201d}", PanelAdapterKind.Unknown)]
    [InlineData(null, PanelAdapterKind.Unknown)]
    public void TheAdapterPathIsClassifiedByVendor(string? path, PanelAdapterKind expected)
    {
        Assert.Equal(expected, GpuRouteInference.ClassifyAdapterPath(path));
    }

    [Theory]
    [InlineData(2, false, GpuRoute.Direct)]
    [InlineData(4, false, GpuRoute.Hybrid)]
    [InlineData(1, false, GpuRoute.IgpuOnly)]
    [InlineData(1, true, GpuRoute.Direct)]
    [InlineData(0, true, GpuRoute.Hybrid)]
    [InlineData(2, true, GpuRoute.IgpuOnly)]
    public void TheNvramDisplayModeByteDecodesPerPlatform(byte value, bool amd, GpuRoute expected)
    {
        Assert.Equal(expected, GpuRouteInference.DecodeOemDisplayMode(value, amd));
    }

    [Theory]
    [InlineData(255, false)]
    [InlineData(255, true)]
    [InlineData(3, false)]
    [InlineData(7, true)]
    public void UnsupportedOrUnknownNvramBytesDecodeToNothing(byte value, bool amd)
    {
        Assert.Null(GpuRouteInference.DecodeOemDisplayMode(value, amd));
    }

    [Fact]
    public void TheRouteMapsToTheUiModes()
    {
        Assert.Equal(MechrevoService.GpuIGpu, GpuRouteInference.ToGpuMode(GpuRoute.IgpuOnly));
        Assert.Equal(MechrevoService.GpuStandard, GpuRouteInference.ToGpuMode(GpuRoute.Hybrid));
        Assert.Equal(MechrevoService.GpuDgpu, GpuRouteInference.ToGpuMode(GpuRoute.Direct));
        Assert.Equal(-1, GpuRouteInference.ToGpuMode(GpuRoute.Unknown));
        Assert.Equal(GpuRoute.Unknown, GpuRouteInference.FromGpuMode(MechrevoService.GpuAuto));
    }

    // ------------------------------------------------------------ NVIDIA 首选 GPU

    [Theory]
    [InlineData(null, NvPreferredGpu.AutoSelect)]
    [InlineData(0x1u, NvPreferredGpu.HighPerformance)]
    [InlineData(0x80000001u, NvPreferredGpu.HighPerformance)]
    [InlineData(0x3u, NvPreferredGpu.HighPerformance)]
    [InlineData(0x10u, NvPreferredGpu.AutoSelect)]
    [InlineData(0x80000010u, NvPreferredGpu.AutoSelect)]
    [InlineData(0x0u, NvPreferredGpu.Integrated)]
    public void TheDrsRenderingModeDecodes(uint? value, NvPreferredGpu expected)
    {
        Assert.Equal(expected, NvPreferredGpuReader.Decode(value));
    }

    [Theory]
    [InlineData("NV_CTRL_PANEL_AUTOSELECT", NvPreferredGpu.AutoSelect)]
    [InlineData("nv_ctrl_panel_highperformance", NvPreferredGpu.HighPerformance)]
    [InlineData("GARBAGE", NvPreferredGpu.Unknown)]
    [InlineData("", NvPreferredGpu.Unknown)]
    [InlineData(null, NvPreferredGpu.Unknown)]
    public void TheServiceStatusDecodes(string? status, NvPreferredGpu expected)
    {
        Assert.Equal(expected, NvPreferredGpuReader.FromServiceStatus(status));
    }

    // ------------------------------------------------------------ 档位 × 代际放行矩阵

    [Fact]
    public void Gen30OnModern12AllowsTheToggleAndTheServiceRestartOnly()
    {
        bool Allowed(string action) => DisplayRoutePolicy.AllowsAction(
            DgpuGenerationKind.Gen30, action, threeMode: false, GcuServiceTier.Modern12, hotSwap: false);
        Assert.True(Allowed(DisplayRouteMatrix.ToggleOn));
        Assert.True(Allowed(DisplayRouteMatrix.ToggleOff));
        Assert.True(Allowed(DisplayRouteMatrix.Restart));
        Assert.False(Allowed(DisplayRouteMatrix.ToggleIgpu));
        Assert.False(Allowed(DisplayRouteMatrix.IgpuOnlyOn));
    }

    [Fact]
    public void AForeignServiceNeverGetsTheRestartOrTheIgpuTarget()
    {
        foreach (DgpuGenerationKind generation in new[] { DgpuGenerationKind.Gen30, DgpuGenerationKind.Gen40, DgpuGenerationKind.Gen50 })
        {
            Assert.True(DisplayRoutePolicy.AllowsAction(generation, DisplayRouteMatrix.ToggleOn, true, GcuServiceTier.Foreign, true));
            Assert.False(DisplayRoutePolicy.AllowsAction(generation, DisplayRouteMatrix.Restart, true, GcuServiceTier.Foreign, true));
            Assert.False(DisplayRoutePolicy.AllowsAction(generation, DisplayRouteMatrix.ToggleIgpu, true, GcuServiceTier.Foreign, true));
            Assert.False(DisplayRoutePolicy.AllowsAction(generation, DisplayRouteMatrix.IgpuOnlyOn, true, GcuServiceTier.Foreign, true));
        }
    }

    [Fact]
    public void Gen50RbActionsNeedAHotSwapMachine()
    {
        Assert.False(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.Gen50, DisplayRouteMatrix.IgpuOnlyOn, true, GcuServiceTier.Modern12, hotSwap: false));
        Assert.True(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.Gen50, DisplayRouteMatrix.IgpuOnlyOn, true, GcuServiceTier.Modern12, hotSwap: true));
        Assert.True(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.Gen50, DisplayRouteMatrix.IgpuOnlyOff, true, GcuServiceTier.Modern12, hotSwap: true));
        // 官方热切换处理器是 log-only：GPU_HOTSWAP_* 任何时候都不发。
        Assert.False(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.Gen50, DisplayRouteMatrix.HotSwapOn, true, GcuServiceTier.Modern12, hotSwap: true));
    }

    [Fact]
    public void Legacy1020RefusesEveryMuxAction()
    {
        foreach (DgpuGenerationKind generation in new[] { DgpuGenerationKind.Gen1020, DgpuGenerationKind.Gen30, DgpuGenerationKind.Gen40, DgpuGenerationKind.Gen50 })
        {
            foreach (string action in new[] { DisplayRouteMatrix.ToggleOn, DisplayRouteMatrix.ToggleOff, DisplayRouteMatrix.Restart, DisplayRouteMatrix.IgpuOnlyOn })
                Assert.False(DisplayRoutePolicy.AllowsAction(generation, action, true, GcuServiceTier.Legacy1020, true), $"{generation}/{action}");
        }
    }

    [Fact]
    public void AnUnknownTierAllowsNothing()
    {
        foreach (GenerationRouteFacts row in DisplayRouteMatrix.Rows)
            foreach (string action in row.ConsoleActions)
                Assert.False(DisplayRoutePolicy.AllowsAction(row.Generation, action, row.ThreeMode ?? true, GcuServiceTier.Unknown, true));
    }

    // ------------------------------------------------------------ 行布局（§7）

    [Theory]
    [InlineData(true, false, false, false, GpuRowLayout.NvPreference)]
    [InlineData(false, false, false, false, GpuRowLayout.Hidden)]
    [InlineData(false, false, true, true, GpuRowLayout.Hidden)]
    [InlineData(false, true, false, false, GpuRowLayout.Mux2)]
    [InlineData(false, true, false, true, GpuRowLayout.Mux3)]
    [InlineData(false, true, true, false, GpuRowLayout.HotSwap)]
    public void TheRowLayoutFollowsTheOfferPredicates(bool nvPreference, bool modeSwitch, bool hotSwap, bool igpuMuxTarget, GpuRowLayout expected)
    {
        Assert.Equal(expected, GpuRowLayouts.Resolve(nvPreference, modeSwitch, hotSwap, igpuMuxTarget));
    }

    static MechrevoHw Machine(DgpuIdentity generation, MechrevoDeviceCapabilities capabilities, GcuServiceTier tier,
        out IDisposable restore)
    {
        IDisposable gen = GpuCapabilityGatingHarness.Generation(generation);
        Func<GcuServiceTier>? previousTier = GcuServiceTierProbe.Override;
        GcuServiceTierProbe.Override = () => tier;
        restore = new Restore(() =>
        {
            GcuServiceTierProbe.Override = previousTier;
            gen.Dispose();
        });
        return GpuCapabilityGatingHarness.Hardware(capabilities);
    }

    sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    static readonly DgpuIdentity Gen1020 = new(DgpuGenerationKind.Gen1020, DgpuProbeSource.MarketingName, true, "GTX 1660 Ti", "2191");

    [Fact]
    public void ThisMachine_Gen50WithMuxAndHotSwap_GetsTheHotSwapLayout()
    {
        using MechrevoHw hw = Machine(GpuCapabilityGatingHarness.Gen50,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, DgpuDirect = true, IgpuOnly = true, GpuHotSwap = true, NvidiaGpu = true },
            GcuServiceTier.Modern12, out IDisposable restore);
        using (restore)
            Assert.Equal(GpuRowLayout.HotSwap, hw.GpuRowLayout);
    }

    [Fact]
    public void Gen40ThreeModeAndTwoModeGetDifferentLayouts()
    {
        using (MechrevoHw three = Machine(GpuCapabilityGatingHarness.Gen40,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, DgpuDirect = true, IgpuOnly = true },
            GcuServiceTier.Modern12, out IDisposable restoreThree))
        using (restoreThree)
            Assert.Equal(GpuRowLayout.Mux3, three.GpuRowLayout);

        using MechrevoHw two = Machine(GpuCapabilityGatingHarness.Gen40,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, DgpuDirect = true, IgpuOnly = false },
            GcuServiceTier.Modern12, out IDisposable restoreTwo);
        using (restoreTwo)
            Assert.Equal(GpuRowLayout.Mux2, two.GpuRowLayout);
    }

    [Fact]
    public void AForeignServiceOnAFortyThreeModeMachineLosesOnlyTheIgpuSegment()
    {
        using MechrevoHw hw = Machine(GpuCapabilityGatingHarness.Gen40,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, DgpuDirect = true, IgpuOnly = true },
            GcuServiceTier.Foreign, out IDisposable restore);
        using (restore)
        {
            Assert.Equal(GpuRowLayout.Mux2, hw.GpuRowLayout);
            Assert.False(hw.GpuServiceRestartAvailable);
        }
    }

    [Fact]
    public void TenTwentyNeedsTheLegacyServiceAndARecognisedServiceStatus()
    {
        using MechrevoHw hw = Machine(Gen1020, new MechrevoDeviceCapabilities { ProfileAvailable = true },
            GcuServiceTier.Legacy1020, out IDisposable restore);
        using (restore)
        {
            Assert.Equal(GpuRowLayout.Hidden, hw.GpuRowLayout);   // DGpu 还没报
            hw.HandleMessage("Setting/Status", """{"DGpu":"NV_CTRL_PANEL_AUTOSELECT"}""");
            Assert.Equal(GpuRowLayout.NvPreference, hw.GpuRowLayout);
            Assert.False(hw.CanOfferGpuModeSwitch);
        }

        using MechrevoHw modern = Machine(Gen1020, new MechrevoDeviceCapabilities { ProfileAvailable = true },
            GcuServiceTier.Modern12, out IDisposable restoreModern);
        using (restoreModern)
        {
            modern.HandleMessage("Setting/Status", """{"DGpu":"NV_CTRL_PANEL_AUTOSELECT"}""");
            Assert.Equal(GpuRowLayout.Hidden, modern.GpuRowLayout);
        }
    }

    [Fact]
    public void TheFiftySeriesMachineNeverGetsTheNvPreferenceLayout()
    {
        using MechrevoHw hw = Machine(GpuCapabilityGatingHarness.Gen50,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, DgpuDirect = true },
            GcuServiceTier.Legacy1020, out IDisposable restore);
        using (restore)
        {
            // 本机的基线帧也报 DGpu=NV_CTRL_PANEL_AUTOSELECT：不能因此把 50 系换成 10/20 布局。
            hw.HandleMessage("Setting/Status", """{"DGpu":"NV_CTRL_PANEL_AUTOSELECT"}""");
            Assert.False(hw.CanOfferNvPreferredGpu);
        }
    }

    // ------------------------------------------------------------ 首选 GPU 下发与回读

    static (MechrevoHw Hw, MechrevoService Service, List<string> Actions) NvMachine(out IDisposable restore)
    {
        var actions = new List<string>();
        IDisposable gen = GpuCapabilityGatingHarness.Generation(Gen1020);
        Func<GcuServiceTier>? previousTier = GcuServiceTierProbe.Override;
        GcuServiceTierProbe.Override = static () => GcuServiceTier.Legacy1020;
        int previousInterval = MechrevoService.NvPreferencePollIntervalMilliseconds;
        MechrevoService.NvPreferencePollIntervalMilliseconds = 10;
        Func<NvPreferredGpu>? previousDriver = NvPreferredGpuReader.DriverOverride;
        restore = new Restore(() =>
        {
            NvPreferredGpuReader.DriverOverride = previousDriver;
            MechrevoService.NvPreferencePollIntervalMilliseconds = previousInterval;
            GcuServiceTierProbe.Override = previousTier;
            gen.Dispose();
        });
        var hw = new MechrevoHw((_, payload) =>
        {
            if (payload is IDictionary<string, object> values && values.TryGetValue("Action", out object? action))
                actions.Add(action?.ToString() ?? "");
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true });
        hw.HandleMessage("Setting/Status", """{"DGpu":"NV_CTRL_PANEL_AUTOSELECT"}""");
        return (hw, new MechrevoService(hw), actions);
    }

    [Fact]
    public async Task ThePreferredGpuIsConfirmedOnlyByTheDriverReadback()
    {
        (MechrevoHw hw, MechrevoService service, List<string> actions) = NvMachine(out IDisposable restore);
        using (restore)
        using (hw)
        {
            NvPreferredGpuReader.DriverOverride = () =>
                actions.Contains(DisplayRouteMatrix.NvCtrlPanelHighPerformance) ? NvPreferredGpu.HighPerformance : NvPreferredGpu.AutoSelect;

            Assert.Equal(GpuApplyResult.Confirmed, await service.SetNvPreferredGpuAsync(NvPreferredGpu.HighPerformance));
            Assert.Contains(DisplayRouteMatrix.NvCtrlPanelHighPerformance, actions);
            Assert.Equal(NvPreferredGpu.HighPerformance, NvPreferredGpuMonitor.Last);
        }
    }

    [Fact]
    public async Task AnUnchangedDriverReadbackIsNotApplied()
    {
        (MechrevoHw hw, MechrevoService service, List<string> actions) = NvMachine(out IDisposable restore);
        using (restore)
        using (hw)
        {
            NvPreferredGpuReader.DriverOverride = static () => NvPreferredGpu.AutoSelect;

            Assert.Equal(GpuApplyResult.NotApplied, await service.SetNvPreferredGpuAsync(NvPreferredGpu.HighPerformance));
            Assert.Contains(DisplayRouteMatrix.NvCtrlPanelHighPerformance, actions);
            Assert.Equal(NvPreferredGpu.AutoSelect, NvPreferredGpuMonitor.Last);
        }
    }

    [Fact]
    public async Task ThePreferredGpuIsNeverSentOutsideTheLegacyTier()
    {
        var actions = new List<string>();
        using IDisposable gen = GpuCapabilityGatingHarness.Generation(Gen1020);
        using var hw = new MechrevoHw((_, payload) =>
        {
            if (payload is IDictionary<string, object> values && values.TryGetValue("Action", out object? action))
                actions.Add(action?.ToString() ?? "");
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true });
        hw.HandleMessage("Setting/Status", """{"DGpu":"NV_CTRL_PANEL_AUTOSELECT"}""");
        var service = new MechrevoService(hw);   // 默认档位 Modern12

        Assert.Equal(GpuApplyResult.Unsupported, await service.SetNvPreferredGpuAsync(NvPreferredGpu.HighPerformance));
        Assert.Empty(actions);
    }

    // ------------------------------------------------------------ 重启后核对（G12）

    [Theory]
    [InlineData(MechrevoService.GpuDgpu, GpuRoute.Direct, GpuRestartVerdict.Applied)]
    [InlineData(MechrevoService.GpuDgpu, GpuRoute.Hybrid, GpuRestartVerdict.NotApplied)]
    [InlineData(MechrevoService.GpuIGpu, GpuRoute.IgpuOnly, GpuRestartVerdict.Applied)]
    [InlineData(MechrevoService.GpuIGpu, GpuRoute.Hybrid, GpuRestartVerdict.NotApplied)]
    [InlineData(MechrevoService.GpuStandard, GpuRoute.Unknown, GpuRestartVerdict.Unconfirmed)]
    [InlineData(MechrevoService.GpuAuto, GpuRoute.Hybrid, GpuRestartVerdict.Unconfirmed)]
    public void TheRestartTargetIsComparedWithTheHardwareRoute(int target, GpuRoute actual, GpuRestartVerdict expected)
    {
        Assert.Equal(expected, GpuRestartVerifier.Evaluate(target, actual));
    }

    [Fact]
    public async Task AfterARebootTheVerifierReportsTheHardwareOutcomeOnce()
    {
        string? previousPending = AppConfig.GetString(GpuRestartVerifier.PendingKey);
        string? previousTick = AppConfig.GetString(GpuRestartVerifier.TickKey);
        string? previousTarget = AppConfig.GetString(GpuRestartVerifier.TargetKey);
        Action<GpuRestartVerdict, string>? previousNotify = GpuRestartVerifier.NotifyOverride;
        var notices = new List<(GpuRestartVerdict Verdict, string Message)>();
        try
        {
            GpuRestartVerifier.NotifyOverride = (verdict, message) => notices.Add((verdict, message));
            using var route = TestGpuRoute.Use(() => TestGpuRoute.Hybrid);

            GpuRestartVerifier.MarkPending(MechrevoService.GpuDgpu);
            AppConfig.Set(GpuRestartVerifier.TickKey, (Environment.TickCount64 + 60_000).ToString());   // 已重启

            Assert.Equal(GpuRestartVerdict.NotApplied, await GpuRestartVerifier.RunAfterBootAsync(TimeSpan.Zero));
            (GpuRestartVerdict verdict, string message) = Assert.Single(notices);
            Assert.Equal(GpuRestartVerdict.NotApplied, verdict);
            Assert.Contains(Properties.Strings.GpuRouteStandard, message, StringComparison.Ordinal);

            // 标记已取走：第二次什么都不说。
            Assert.Equal(GpuRestartVerdict.NotPending, await GpuRestartVerifier.RunAfterBootAsync(TimeSpan.Zero));
            Assert.Single(notices);
        }
        finally
        {
            GpuRestartVerifier.NotifyOverride = previousNotify;
            RestoreConfig(GpuRestartVerifier.PendingKey, previousPending);
            RestoreConfig(GpuRestartVerifier.TickKey, previousTick);
            RestoreConfig(GpuRestartVerifier.TargetKey, previousTarget);
        }
    }

    [Fact]
    public async Task AnUnknownReadbackAfterARebootStaysSilent()
    {
        string? previousPending = AppConfig.GetString(GpuRestartVerifier.PendingKey);
        string? previousTick = AppConfig.GetString(GpuRestartVerifier.TickKey);
        string? previousTarget = AppConfig.GetString(GpuRestartVerifier.TargetKey);
        Action<GpuRestartVerdict, string>? previousNotify = GpuRestartVerifier.NotifyOverride;
        var notices = new List<string>();
        TimeSpan previousDelay = GpuRestartVerifier.ReadbackRetryDelay;
        try
        {
            GpuRestartVerifier.NotifyOverride = (_, message) => notices.Add(message);
            GpuRestartVerifier.ReadbackRetryDelay = TimeSpan.FromMilliseconds(10);
            using var route = TestGpuRoute.Use(() => GpuRouteReadback.Unavailable);
            GpuRestartVerifier.MarkPending(MechrevoService.GpuDgpu);
            AppConfig.Set(GpuRestartVerifier.TickKey, (Environment.TickCount64 + 60_000).ToString());

            // 回读一直 Unknown：重试两次后不下结论，也不提示。
            Assert.Equal(GpuRestartVerdict.Unconfirmed, await GpuRestartVerifier.RunAfterBootAsync(TimeSpan.Zero));
            Assert.Empty(notices);
            Assert.False(AppConfig.Is(GpuRestartVerifier.PendingKey), "标记已取走，不会每次开机都核对。");
        }
        finally
        {
            GpuRestartVerifier.ReadbackRetryDelay = previousDelay;
            GpuRestartVerifier.NotifyOverride = previousNotify;
            RestoreConfig(GpuRestartVerifier.PendingKey, previousPending);
            RestoreConfig(GpuRestartVerifier.TickKey, previousTick);
            RestoreConfig(GpuRestartVerifier.TargetKey, previousTarget);
        }
    }

    static void RestoreConfig(string key, string? value)
    {
        if (value is null) AppConfig.Remove(key);
        else AppConfig.Set(key, value);
    }

    // ------------------------------------------------------------ 只读守卫

    /// <summary>回读类只准读：不得调用任何 Set / Save / Write / Delete / Restore 开头的方法（IL 级）。</summary>
    [Theory]
    [InlineData(typeof(GpuRouteProbe))]
    [InlineData(typeof(NvPreferredGpuReader))]
    [InlineData(typeof(GcuServiceTierProbe))]
    public void TheReadbackTypesNeverCallAWriteMethod(Type type)
    {
        string[] forbidden = { "Set", "Save", "Write", "Delete", "Restore", "Adjust" };
        var hits = new List<string>();
        foreach (Type scanned in new[] { type }.Concat(type.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)))
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                                       BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (MethodBase method in scanned.GetMethods(flags).Cast<MethodBase>().Concat(scanned.GetConstructors(flags)))
            {
                byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
                if (il is null) continue;
                foreach (MethodBase called in FirmwareWriteGuard.IlScanner.Run(method.Module, il).Calls)
                {
                    if (called.IsSpecialName) continue;   // 属性访问器不是写硬件
                    if (called.DeclaringType?.Name == "Logger") continue;   // 写日志不是写硬件
                    if (forbidden.Any(prefix => called.Name.StartsWith(prefix, StringComparison.Ordinal)))
                        hits.Add($"{scanned.Name}.{method.Name} -> {called.DeclaringType?.Name}.{called.Name}");
                }
            }
        }
        Assert.Empty(hits);
    }

    [Fact]
    public void TheRouteProbeNeverTouchesNvmlOrNvapi()
    {
        string source = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Gpu", "GpuRouteProbe.cs");
        Assert.DoesNotContain("NvmlHelper", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NvAPIWrapper", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LhmMonitor", source, StringComparison.Ordinal);
    }
}
