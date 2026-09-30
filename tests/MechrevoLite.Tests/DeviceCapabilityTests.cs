using MechrevoLite.Gpu;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

[Collection(nameof(SerialGpuSwitchCollection))]
public class DeviceCapabilityTests
{
    [Fact]
    public void DashboardAndTrayFailClosedOnGpuSwitchWhenHardwareIsNull()
    {
        string source = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Settings.cs");
        Assert.DoesNotContain("CanOfferGpuModeSwitch ?? true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GpuRowLayout ?? GpuRowLayout.Mux", source, StringComparison.Ordinal);
        // 主界面与托盘都读同一个布局值，没有硬件时都退回「整行隐藏」。
        const string failClosed = "GpuRowLayout ?? GpuRowLayout.Hidden";
        int first = source.IndexOf(failClosed, StringComparison.Ordinal);
        int second = source.IndexOf(failClosed, first + 1, StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first, "dashboard and tray must both fail-closed when hw is null");
    }

    [Fact]
    public void OfficialProfileEnablesGpuOverclockUntilRuntimeStatusArrives()
    {
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true });
        hardware.HandleMessage("Fan/Status",
            "{\"GPU_CoreClockOffsetMinimumHWOC\":-200,\"GPU_CoreClockOffsetMaximumHWOC\":200}");

        Assert.True(hardware.SupportsGpuOverclock);

        hardware.HandleMessage("LCHWOC/Status", "{\"Support\":false,\"Enable\":false}");
        Assert.False(hardware.SupportsGpuOverclock);
    }

    [Fact]
    public void OfficialItemSupportProfile_MapsModelSpecificFeatures()
    {
        var profile = MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>
        {
            ["BIOS_PROJECT_ID"] = "MODEL140",
            ["KeyboardSupport"] = 1,
            ["LightbarSupport"] = 0,
            ["DGpuDirectConnectionSupport"] = 0,
            ["iGPUModeOnlySupport"] = 1,
            ["LiquidCoolingSupport"] = 0,
            ["ColorCalibrationSupport"] = 2,
            ["IsAMDPlatform"] = 1,
        });

        Assert.True(profile.ProfileAvailable);
        Assert.Equal("MODEL140", profile.ProjectId);
        Assert.True(profile.Keyboard);
        Assert.False(profile.Lightbar);
        Assert.False(profile.DgpuDirect);
        Assert.True(profile.IgpuOnly);
        Assert.True(profile.ColorCalibration);
        Assert.True(profile.AmdPlatform);
    }

    /// <summary>
    /// T9：热切换判据 = 服务写入的两个值，<c>APVersionCheck</c> 这个数值代理已删除——
    /// 23/24 都不得改变结论；任一值缺失或为 0 即不提供。
    /// </summary>
    [Fact]
    public void OfficialHotSwapGate_RequiresTheTwoVendorValuesAndIgnoresTheVersionProxy()
    {
        var version23 = MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>
        {
            ["APVersionCheck"] = 23,
            ["GpuHotSwapSwitchSupport"] = 1,
            ["lgpuHotSwapSwitchStatus"] = 1,
        });
        var version24 = MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>
        {
            ["APVersionCheck"] = 24,
            ["GpuHotSwapSwitchSupport"] = 1,
            ["lgpuHotSwapSwitchStatus"] = 1,
        });
        var blocked = MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>
        {
            ["GpuHotSwapSwitchSupport"] = 1,
            ["lgpuHotSwapSwitchStatus"] = 0,
        });

        Assert.True(version23.GpuHotSwap);
        Assert.True(version24.GpuHotSwap);
        Assert.False(blocked.GpuHotSwap);
    }

    [Theory]
    [InlineData("GpuHotSwapSwitchSupport")]
    [InlineData("lgpuHotSwapSwitchStatus")]
    public void OfficialHotSwapGate_MissingRequiredFlagIsUnsupported(string missingKey)
    {
        var values = new Dictionary<string, object?>
        {
            ["APVersionCheck"] = 24,
            ["GpuHotSwapSwitchSupport"] = 1,
            ["lgpuHotSwapSwitchStatus"] = 1,
        };
        values.Remove(missingKey);

        Assert.False(MechrevoDeviceCapabilities.FromValues(values).GpuHotSwap);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void GpuStatus_ParsesHotSwitchBlockedResponse(string value, bool expected)
    {
        using var hardware = new MechrevoHw(null,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true, DgpuDirect = true });
        hardware.HandleMessage("Setting/Status", $$"""
            {"DiscreteGpuDirectConnectionSwitch_Status":"DGPU_DIRECT_CONNECT_TOGGLE_OFF","IGpuCannotBeSwitchNowVisibility":"{{value}}"}
            """);

        Assert.Equal(expected, hardware.IgpuSwitchBlocked);
    }

    [Fact]
    public void LegacyProfileAliasesAndTextFlags_AreParsed()
    {
        var profile = MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["BIOSProjectID"] = "OLD40",
            ["KeyboardType"] = "3",
            ["DisplayRefreshSupport"] = "true",
            ["FanSettingsSupport"] = true,
            ["IsOldType"] = "enabled",
            ["DGpuDirectConnectionSupport"] = "false",
        });

        Assert.Equal("OLD40", profile.ProjectId);
        Assert.True(profile.Keyboard);
        Assert.Equal(3, profile.KeyboardType);
        Assert.True(profile.DisplayRefresh);
        Assert.Equal(1, profile.DisplayRefreshLevel);
        Assert.True(profile.FanSettings);
        Assert.True(profile.IsOldType);
        Assert.False(profile.DgpuDirect);
    }

    [Fact]
    public void ExtendedProfileAliasesAndMachineIdentity_AreParsed()
    {
        var profile = MechrevoDeviceCapabilities.FromValues(
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ProjectId"] = "LEGACY50",
                ["KeyboardBacklightSupport"] = "supported",
                ["DiscreteGpuDirectConnectionSupport"] = 1,
                ["IntegratedGpuOnlySupport"] = 1,
                ["WaterCoolingSupport"] = true,
                ["DisplayColorCalibrationSupport"] = 3,
                ["LogoLightbarSupport"] = 1,
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["SystemManufacturer"] = "MECHREVO",
                ["SystemProductName"] = "YAOSHI Series",
                ["SystemFamily"] = "ARL",
                ["SystemSKU"] = "0001",
                ["BaseBoardProduct"] = "YAOSHI Series-X6AR55xY",
                ["BIOSVersion"] = "N.1.32MRO60",
            });

        Assert.Equal("LEGACY50", profile.ProjectId);
        Assert.True(profile.IsMechrevo);
        Assert.Equal("YAOSHI Series", profile.Model);
        Assert.Equal("ARL", profile.SystemFamily);
        Assert.Equal("YAOSHI Series-X6AR55xY", profile.BaseboardProduct);
        Assert.True(profile.Keyboard);
        Assert.True(profile.DgpuDirect);
        Assert.True(profile.IgpuOnly);
        Assert.True(profile.LiquidCooling);
        Assert.True(profile.ColorCalibration);
        Assert.True(profile.LogoLight);
    }

    [Theory]
    [InlineData(new[] { "Lightbar_logo_PowerSwitch" }, new string[0])]
    [InlineData(new string[0], new[] { "MEZone_Lighbar4_logo" })]
    public void LogoRegistryLayouts_AreRecognized(string[] values, string[] subKeys) =>
        Assert.True(MechrevoDeviceCapabilities.HasLogoLightingRegistryLayout(values, subKeys));

    [Fact]
    public void LogoRegistryDetection_EnablesLogoWithoutItemSupportProfile()
    {
        var capabilities = MechrevoDeviceCapabilities.FromValues(
            new Dictionary<string, object?>(),
            logoRegistryDetected: true);

        Assert.True(capabilities.LogoLight);
        Assert.False(capabilities.ProfileAvailable);
    }

    [Fact]
    public void TurboSubMode_RequiresCapabilityFlag_NotCurrentStateValue()
    {
        var unsupported = MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>
        {
            ["TurboModeSupport"] = 1,
            ["SilentPerformanceModeSwitch"] = 1,
        });
        var supported = MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>
        {
            ["IsTurboSubModeSupport"] = 1,
            ["SilentPerformanceModeSwitch"] = 0,
        });

        Assert.False(unsupported.TurboSubMode);
        Assert.True(supported.TurboSubMode);
    }

    [Fact]
    public async Task UnsupportedTurboSubMode_IsRejectedWithoutPublishing()
    {
        int publishCount = 0;
        using var hardware = new MechrevoHw((_, _) =>
        {
            publishCount++;
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, TurboMode = true, TurboSubMode = false });
        var service = new MechrevoService(hardware);

        Assert.False(await service.SwitchTurboSubMode(silent: true));
        Assert.Equal(0, publishCount);
    }

    [Theory]
    [InlineData(140)]
    [InlineData(175)]
    public async Task GpuTgpLimit_ComesFromCurrentGcuStatus(int maximum)
    {
        var published = new List<IDictionary<string, object>>();
        using var hardware = new MechrevoHw((_, payload) =>
        {
            if (payload is IDictionary<string, object> values)
                published.Add(new Dictionary<string, object>(values));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true });
        hardware.HandleMessage("Fan/Status", $$"""
            {"GPU_ConfigurableTGPMinimum":80,"GPU_ConfigurableTGPMaximum":{{maximum}},"GPU_ConfigurableTGPTarget":100}
            """);
        var service = new MechrevoService(hardware);

        Assert.Equal(maximum, hardware.GpuTgpMaximum);
        Assert.True(hardware.GpuTgpAdjustable);
        Assert.False(await service.SetCustomDetail(new() { ["GpuConfigurableTGPTarget"] = (maximum + 1).ToString() }));
        Assert.Empty(published);
    }

    [Fact]
    public async Task FixedGpuTgp_IsNotExposedAsAdjustableOrPublished()
    {
        int publishCount = 0;
        using var hardware = new MechrevoHw((_, _) => { publishCount++; return Task.CompletedTask; },
            new MechrevoDeviceCapabilities { ProfileAvailable = true });
        hardware.HandleMessage("Fan/Status", "{\"GPU_ConfigurableTGPMinimum\":140,\"GPU_ConfigurableTGPMaximum\":140,\"GPU_ConfigurableTGPTarget\":140}");
        var service = new MechrevoService(hardware);

        Assert.False(hardware.GpuTgpAdjustable);
        Assert.False(await service.SetCustomDetail(new() { ["GpuConfigurableTGPTarget"] = "140" }));
        Assert.Equal(0, publishCount);
    }

    /// <summary>
    /// 没有独显直连（MUX）就没有显卡行——与官方 50 系控制台同口径（显卡页可见 = IsNvGpu ∧ DGpuDirectConnectionSupport）。
    /// 服务说支持核显-only 也一样：没有 MUX 的机器上 RB 寄存器没有可验证的硬件路径。
    /// </summary>
    [Fact]
    public async Task NoMux_OffersNoGpuSwitchAndPublishesNothing()
    {
        int publishCount = 0;
        using var hardware = new MechrevoHw((_, _) => { publishCount++; return Task.CompletedTask; },
            new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true, DgpuDirect = false });
        hardware.HandleMessage("Setting/Status", "{\"IGpuOnlyConnectionSwitch_Support\":true,\"DiscreteGpuDirectConnectionSwitch_Support\":false}");
        publishCount = 0;
        var service = new MechrevoService(hardware);

        Assert.False(hardware.CanOfferGpuModeSwitch);
        Assert.Equal(MechrevoLite.Gpu.GpuRowLayout.Hidden, hardware.GpuRowLayout);
        Assert.False(hardware.CanSwitchGpuMode(MechrevoService.GpuIGpu));
        Assert.False(hardware.CanSwitchGpuMode(MechrevoService.GpuStandard));
        Assert.False(hardware.CanSwitchGpuMode(MechrevoService.GpuAuto));
        Assert.False(hardware.CanSwitchGpuMode(MechrevoService.GpuDgpu));
        Assert.False(await service.SwitchGpuMode(MechrevoService.GpuIGpu));
        Assert.Equal(GpuRestartRequestOutcome.Unsupported,
            await service.RequestGpuModeRestartOutcomeAsync(MechrevoService.GpuDgpu));
        Assert.Equal(0, publishCount);
    }

    [Fact]
    public void RuntimeGpuSupport_OverridesStaleRegistryProfile()
    {
        using var hardware = new MechrevoHw(null,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true, DgpuDirect = true });

        hardware.HandleMessage("Setting/Status", "{\"DiscreteGpuDirectConnectionSwitch_Support\":false,\"IGpuOnlyConnectionSwitch_Support\":false}");

        Assert.False(hardware.SupportsDgpuDirect);
        Assert.False(hardware.SupportsIgpuOnly);
        Assert.False(hardware.CanSwitchGpuMode(MechrevoService.GpuAuto));
    }

    [Fact]
    public void RuntimeGpuStatus_InfersSupportWhenGcuOmitsTheCapabilityFlag()
    {
        using var hardware = new MechrevoHw(null,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = false, DgpuDirect = false });

        hardware.HandleMessage("Setting/Status",
            "{\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_OFF\"}");

        Assert.Null(hardware.DgpuDirectStatusSupport);
        Assert.True(hardware.IgpuOnlyStatusSupport);
        // 命令族存在 ≠ 有显卡行：这台机器没有 MUX，官方也不显示显卡页。
        Assert.False(hardware.CanSwitchGpuMode(MechrevoService.GpuIGpu));
        Assert.False(hardware.CanSwitchGpuMode(MechrevoService.GpuAuto));
    }

    /// <summary>
    /// 只有 MUX、没有核显模式位的 50 系：官方 IgpuButtonVisibility = IGPUModeSupport，
    /// 所以只有「标准 / 直连」两段（过去这里把 TOGGLE_IGPU 当成 MUX 机器的默认核显，是自创）。
    /// </summary>
    [Fact]
    public void MuxOnlyProfile_OffersStandardAndDirectButNoIgpu()
    {
        using var hardware = new MechrevoHw(null,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, DgpuDirect = true, IgpuOnly = false });

        Assert.Equal(MechrevoLite.Gpu.GpuRowLayout.Mux2, hardware.GpuRowLayout);
        Assert.False(hardware.CanSwitchGpuMode(MechrevoService.GpuIGpu));
        Assert.True(hardware.CanSwitchGpuMode(MechrevoService.GpuStandard));
        Assert.True(hardware.CanSwitchGpuMode(MechrevoService.GpuDgpu));
        Assert.False(hardware.CanSwitchGpuMode(MechrevoService.GpuAuto));
    }

    /// <summary>
    /// MUX 目标一律走重启路由：非热切换机型上 SwitchGpuMode 一条指令都不发，
    /// 集显 NVRAM 目标是 TOGGLE_IGPU + RESTART。
    /// </summary>
    [Fact]
    public async Task MuxTargets_NeverUseTheHotSwitchPath()
    {
        var actions = new List<string>();
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Setting/Control" && payload is IDictionary<string, object> values)
                actions.Add(values.TryGetValue("Action", out object? action) ? action?.ToString() ?? "" : "");
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, DgpuDirect = true, IgpuOnly = true });
        hardware.HandleMessage("Setting/Status",
            "{\"DiscreteGpuDirectConnectionSwitch_Status\":\"DGPU_DIRECT_CONNECT_TOGGLE_OFF\"}");
        var service = new MechrevoService(hardware);

        Assert.Equal(MechrevoLite.Gpu.GpuRowLayout.Mux3, hardware.GpuRowLayout);
        Assert.False(await service.SwitchGpuMode(MechrevoService.GpuIGpu));
        Assert.Empty(actions);

        Assert.Equal(GpuRestartRequestOutcome.Requested,
            await service.RequestGpuModeRestartOutcomeAsync(MechrevoService.GpuIGpu));
        Assert.Equal(new[] { "DGPU_DIRECT_CONNECT_TOGGLE_IGPU", "DGPU_DIRECT_CONNECT_RESTART" }, actions);
    }

    /// <summary>
    /// 服务自动档（仅 50 系热切换机型）：电池下期望运行态 2（核显），且 NVIDIA 设备真的断开才算成功。
    /// </summary>
    [Fact]
    public async Task AutomaticGpuMode_WaitsForTheBatteryRuntimeAndTheDeviceToLeave()
    {
        MechrevoHw? hardware = null;
        var actions = new List<string>();
        bool autoSent = false;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Setting/Control" && payload is IDictionary<string, object> values)
            {
                string action = values.TryGetValue("Action", out object? actionValue)
                    ? actionValue?.ToString() ?? ""
                    : "";
                actions.Add(action);
                if (action == "IGPU_ONLY_CONNECT_RB_AUTO") autoSent = true;
                if (action == "GETSTATUS" && autoSent)
                    hardware!.HandleMessage("Setting/Status",
                        "{\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_AUTO\",\"CheckDGpuStatusforIGpuOnlyOnSuccess\":\"2\"}");
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true, DgpuDirect = true, GpuHotSwap = true, NvidiaGpu = true });

        using var route = TestGpuRoute.Use(() => autoSent ? TestGpuRoute.IgpuOnly : TestGpuRoute.Hybrid);
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status",
                "{\"DiscreteGpuDirectConnectionSwitch_Status\":\"DGPU_DIRECT_CONNECT_TOGGLE_OFF\",\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_OFF\",\"CheckDGpuStatusforIGpuOnlyOnSuccess\":\"1\"}");
            var service = new MechrevoService(hardware);

            Assert.True(await service.SwitchAutomaticGpuMode(plugged: false));
            Assert.Equal(2, hardware.GpuSwitchResult);
            Assert.Contains("IGPU_ONLY_CONNECT_RB_AUTO", actions);
        }
    }

    [Theory]
    // 独显通路为显示权威：TOGGLE_OFF 一律显示混合，RB 寄存器只是混合模式内的热切换子开关
    // （本机实测 RB_ON 未被应用时硬件仍是混合，早先按 RB 显示「集显」是谎报）。
    [InlineData("DGPU_DIRECT_CONNECT_TOGGLE_OFF", "IGPU_ONLY_CONNECT_RB_ON", MechrevoService.GpuStandard)]
    [InlineData("DGPU_DIRECT_CONNECT_TOGGLE_OFF", "IGPU_ONLY_CONNECT_RB_OFF", MechrevoService.GpuStandard)]
    [InlineData("DGPU_DIRECT_CONNECT_TOGGLE_OFF", "IGPU_ONLY_CONNECT_RB_AUTO", MechrevoService.GpuStandard)]
    [InlineData("DGPU_DIRECT_CONNECT_TOGGLE_ON", "IGPU_ONLY_CONNECT_RB_AUTO", MechrevoService.GpuDgpu)]
    [InlineData("DGPU_DIRECT_CONNECT_TOGGLE_IGPU", "IGPU_ONLY_CONNECT_RB_OFF", MechrevoService.GpuIGpu)]
    public void GpuStatus_CombinesMuxAndIgpuOnlyLayers(string directStatus, string igpuStatus, int expected)
    {
        using var hardware = new MechrevoHw(null,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true, DgpuDirect = true });

        hardware.HandleMessage("Setting/Status", $$"""
            {"DiscreteGpuDirectConnectionSwitch_Status":"{{directStatus}}","IGpuOnlyConnectionSwitch_Status":"{{igpuStatus}}"}
            """);

        Assert.Equal(expected, hardware.GpuMode);
    }

    [Fact]
    public void GpuModeStatusVersion_AdvancesOnlyWhenGpuModeFieldsAreReported()
    {
        using var hardware = new MechrevoHw(null,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true, DgpuDirect = true });
        long version = hardware.GpuModeStatusVersion;

        hardware.HandleMessage("Setting/Status", "{\"WinKey\":\"WINKEY_STATUS_LOCK\"}");
        Assert.Equal(version, hardware.GpuModeStatusVersion);

        hardware.HandleMessage("Setting/Status",
            "{\"DiscreteGpuDirectConnectionSwitch_Status\":\"DGPU_DIRECT_CONNECT_TOGGLE_OFF\",\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_OFF\"}");
        Assert.Equal(version + 1, hardware.GpuModeStatusVersion);
    }

    [Fact]
    public void NumberPadStatus_EnablesTheNumberPadQuickSwitch()
    {
        using var hardware = new MechrevoHw(null,
            new MechrevoDeviceCapabilities { ProfileAvailable = true });

        hardware.HandleMessage("Setting/Status", "{\"NumPad\":\"NUMPAD_LOCK\"}");

        Assert.True(hardware.NumpadSeen);
        Assert.True(hardware.SupportsQuickSwitch("numpad"));
    }

    /// <summary>
    /// 热切换回标准（集显 → 标准）：官方 IgpuOnlyOffCommand 的载荷（RB_OFF + SetToWMIEC=OK），
    /// 服务回报运行态 1 **且** NVIDIA 设备重新在位才算成功。
    /// </summary>
    [Fact]
    public async Task HotSwitchBackToStandard_UsesOfficialCommandAndWaitsForTheDevice()
    {
        var published = new List<Dictionary<string, object>>();
        bool offSent = false;
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic != "Setting/Control" || payload is not IDictionary<string, object> values)
                return Task.CompletedTask;

            var copy = new Dictionary<string, object>(values);
            published.Add(copy);
            string action = copy.GetValueOrDefault("Action")?.ToString() ?? "";
            if (action == "IGPU_ONLY_CONNECT_RB_OFF") offSent = true;
            if (action == "GETSTATUS" && offSent)
                hardware!.HandleMessage("Setting/Status",
                    "{\"DiscreteGpuDirectConnectionSwitch_Status\":\"DGPU_DIRECT_CONNECT_TOGGLE_OFF\",\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_OFF\",\"CheckDGpuStatusforIGpuOnlyOnSuccess\":\"1\"}");
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true, DgpuDirect = true, GpuHotSwap = true, NvidiaGpu = true });

        using var route = TestGpuRoute.Use(() => offSent ? TestGpuRoute.Hybrid : TestGpuRoute.IgpuOnly);
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status",
                "{\"DiscreteGpuDirectConnectionSwitch_Status\":\"DGPU_DIRECT_CONNECT_TOGGLE_OFF\",\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_ON\",\"CheckDGpuStatusforIGpuOnlyOnSuccess\":\"2\"}");
            // 合成模式以独显通路为准（TOGGLE_OFF 下仍是混合）；热切换看的是寄存器本身。
            Assert.Equal(MechrevoService.GpuIGpu, hardware.IgpuOnlyRegister);
            var service = new MechrevoService(hardware);

            Assert.True(await service.SwitchGpuMode(MechrevoService.GpuStandard));
            Dictionary<string, object> command = Assert.Single(published,
                item => item.GetValueOrDefault("Action")?.ToString() == "IGPU_ONLY_CONNECT_RB_OFF");
            Assert.Equal("OK", command["SetToWMIEC"]);
            Assert.Equal(MechrevoService.GpuStandard, service.CurrentGpuMode);
            Assert.DoesNotContain(published, item => item.GetValueOrDefault("Action")?.ToString() == "IGPUONLYCONNECTIONSWITCH_STATUS");
        }
    }

    /// <summary>直连永远是重启路由：SwitchGpuMode 拒绝，一条指令都不发。</summary>
    [Fact]
    public async Task DirectIsNeverAHotSwitch()
    {
        int publishCount = 0;
        using var hardware = new MechrevoHw((_, _) => { publishCount++; return Task.CompletedTask; },
            new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true, DgpuDirect = true, GpuHotSwap = true, NvidiaGpu = true });
        hardware.HandleMessage("Setting/Status",
            "{\"DiscreteGpuDirectConnectionSwitch_Status\":\"DGPU_DIRECT_CONNECT_TOGGLE_OFF\",\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_OFF\"}");
        var service = new MechrevoService(hardware);

        Assert.True(hardware.CanSwitchGpuMode(MechrevoService.GpuDgpu));
        Assert.False(await service.SwitchGpuMode(MechrevoService.GpuDgpu));
        Assert.Equal(0, publishCount);
    }

    [Fact]
    public void HotSwitchPolling_MatchesOfficialRetryBounds()
    {
        Assert.Equal(61, MechrevoService.HotSwitchStatusPollLimit);
        Assert.True(MechrevoService.ShouldRetryHotSwitchPoll(0));
        Assert.False(MechrevoService.ShouldRetryHotSwitchPoll(1));
        Assert.True(MechrevoService.ShouldRetryHotSwitchPoll(4));
    }

    [Fact]
    public async Task GpuModeRestart_UsesOfficialMuxTargetThenVendorRestart()
    {
        var actions = new List<string>();
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic != "Setting/Control" || payload is not IDictionary<string, object> values)
                return Task.CompletedTask;

            string action = values.TryGetValue("Action", out object? actionValue)
                ? actionValue?.ToString() ?? ""
                : "";
            actions.Add(action);
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true, DgpuDirect = true });

        using (hardware)
        {
            hardware.HandleMessage("Setting/Status",
                "{\"DiscreteGpuDirectConnectionSwitch_Status\":\"DGPU_DIRECT_CONNECT_TOGGLE_OFF\",\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_OFF\"}");
            var service = new MechrevoService(hardware);

            Assert.True(await service.RequestGpuModeRestartAsync(MechrevoService.GpuIGpu));
            Assert.Equal(new[] { "DGPU_DIRECT_CONNECT_TOGGLE_IGPU", "DGPU_DIRECT_CONNECT_RESTART" }, actions);
        }
    }

    /// <summary>
    /// 50 系（我方 1.2 服务）重启路由与官方 GpuSettingPage.RestartDialog_PrimaryButtonClick 一致；
    /// 没有 MUX 时没有路由（过去的 RB_ON 兜底与「重启前发 RB_AUTO」都是自创，已删除）。
    /// </summary>
    [Theory]
    [InlineData(MechrevoService.GpuDgpu, true, "DGPU_DIRECT_CONNECT_TOGGLE_ON|IGPU_ONLY_CONNECT_RB_OFF|DGPU_DIRECT_CONNECT_TOGGLE_ON")]
    [InlineData(MechrevoService.GpuStandard, true, "DGPU_DIRECT_CONNECT_TOGGLE_OFF")]
    [InlineData(MechrevoService.GpuIGpu, true, "DGPU_DIRECT_CONNECT_TOGGLE_IGPU")]
    [InlineData(MechrevoService.GpuIGpu, false, "")]
    [InlineData(MechrevoService.GpuAuto, true, "")]
    public void GpuRestartRoute_UsesOfficialTargetActions(int targetMode, bool supportsDgpuDirect, string expected)
    {
        var context = new GpuRouteContext(DgpuGenerationKind.Gen50, GcuServiceTier.Modern12,
            supportsDgpuDirect, ThreeMode: true, HotSwap: false, IgpuMuxTarget: supportsDgpuDirect);
        GpuRestartRoute route = MechrevoService.CreateGpuRestartRoute(targetMode, context);
        string actions = string.Join('|', route.Payloads.Select(payload => payload["Action"]));

        Assert.Equal(expected, actions);
        Assert.Equal(expected.Length > 0, route.ServiceRestart);
    }

    [Fact]
    public async Task RefreshGpuModeStatus_UsesOnlyGetStatusAndReturnsTheReadBackMode()
    {
        var actions = new List<string>();
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Setting/Control" && payload is IDictionary<string, object> values)
            {
                string action = values.TryGetValue("Action", out object? actionValue)
                    ? actionValue?.ToString() ?? ""
                    : "";
                actions.Add(action);
                if (action == "GETSTATUS")
                {
                    hardware!.HandleMessage("Setting/Status",
                        "{\"DiscreteGpuDirectConnectionSwitch_Status\":\"DGPU_DIRECT_CONNECT_TOGGLE_ON\",\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_OFF\"}");
                }
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true, DgpuDirect = true });

        using (hardware)
        {
            hardware.HandleMessage("Setting/Status",
                "{\"DiscreteGpuDirectConnectionSwitch_Status\":\"DGPU_DIRECT_CONNECT_TOGGLE_OFF\",\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_OFF\"}");
            var service = new MechrevoService(hardware);

            Assert.Equal(GpuModeStatusReadback.Fresh, await service.RefreshGpuModeStatus());
            Assert.Equal(new[] { "GETSTATUS" }, actions);
            Assert.Equal(MechrevoService.GpuDgpu, service.CurrentGpuMode);
        }
    }

    [Fact]
    public async Task RefreshGpuModeStatus_UsesKnownStateWhenAnOlderGcuDoesNotEchoGetStatus()
    {
        var actions = new List<string>();
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Setting/Control" && payload is IDictionary<string, object> values)
                actions.Add(values.TryGetValue("Action", out object? action) ? action?.ToString() ?? "" : "");
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true, DgpuDirect = true });
        hardware.HandleMessage("Setting/Status",
            "{\"DiscreteGpuDirectConnectionSwitch_Status\":\"DGPU_DIRECT_CONNECT_TOGGLE_OFF\",\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_OFF\"}");
        var service = new MechrevoService(hardware);

        Assert.Equal(GpuModeStatusReadback.Cached, await service.RefreshGpuModeStatus());
        Assert.Equal(new[] { "GETSTATUS" }, actions);
        Assert.Equal(MechrevoService.GpuStandard, service.CurrentGpuMode);
    }

    [Fact]
    public async Task GpuModeSwitch_WithACachedTargetWaitsForPostCommandReadback()
    {
        var actions = new List<string>();
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Setting/Control" && payload is IDictionary<string, object> values)
            {
                string action = values.TryGetValue("Action", out object? actionValue)
                    ? actionValue?.ToString() ?? ""
                    : "";
                actions.Add(action);
                if (action == "GETSTATUS")
                {
                    hardware!.HandleMessage("Setting/Status",
                        "{\"DiscreteGpuDirectConnectionSwitch_Status\":\"DGPU_DIRECT_CONNECT_TOGGLE_OFF\",\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_OFF\"}");
                }
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, IgpuOnly = true, DgpuDirect = true, GpuHotSwap = true, NvidiaGpu = true });

        using (hardware)
        {
            hardware.HandleMessage("Setting/Status",
                "{\"DiscreteGpuDirectConnectionSwitch_Status\":\"DGPU_DIRECT_CONNECT_TOGGLE_OFF\",\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_OFF\"}");
            var service = new MechrevoService(hardware);

            // 缓存里已经是目标值：不能拿旧状态当确认，必须等发令之后的新回读（GETSTATUS 回包）。
            Assert.True(await service.SwitchGpuMode(MechrevoService.GpuStandard));
            Assert.Contains("IGPU_ONLY_CONNECT_RB_OFF", actions);
            Assert.Contains("GETSTATUS", actions);
        }
    }

    [Fact]
    public async Task UnsupportedGpuOverclock_IsRejectedEvenWhenRangesAreReported()
    {
        int publishCount = 0;
        using var hardware = new MechrevoHw((_, _) => { publishCount++; return Task.CompletedTask; },
            new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true });
        hardware.HandleMessage("Fan/Status", "{\"GPU_CoreClockOffsetMinimumHWOC\":-200,\"GPU_CoreClockOffsetMaximumHWOC\":200}");
        hardware.HandleMessage("LCHWOC/Status", "{\"Support\":false,\"Enable\":false}");
        var service = new MechrevoService(hardware);

        Assert.False(hardware.SupportsGpuOverclock);
        Assert.False(await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "100" }));
        Assert.Equal(0, publishCount);
    }

    [Theory]
    [InlineData(150)]
    [InlineData(250)]
    public async Task GpuCoreOffset_GcuEchoWithoutDriverReadbackIsNotAccepted(int requested)
    {
        var published = new List<int>();
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Fan/Control" && payload is IDictionary<string, object> values &&
                values.TryGetValue("GpuCoreClockOffsetOC", out object? offset) &&
                int.TryParse(offset?.ToString(), out int parsed))
            {
                published.Add(parsed);
                hardware!.HandleMessage("Fan/Status", $$"""
                    {"GPU_CoreClockOffsetMinimumHWOC":-500,"GPU_CoreClockOffsetMaximumHWOC":500,
                     "GPU_CoreClockOffsetOC":{{parsed}},"OverClockingSwitch":1}
                    """);
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true });

        using (hardware)
        {
            hardware.HandleMessage("Fan/Status",
                "{\"GPU_CoreClockOffsetMinimumHWOC\":-500,\"GPU_CoreClockOffsetMaximumHWOC\":500,\"GPU_CoreClockOffsetOC\":0,\"OverClockingSwitch\":1}");
            hardware.HandleMessage("LCHWOC/Status", "{\"Support\":true,\"Enable\":true}");
            var service = new MechrevoService(hardware);

            // 范围来自实测边界，与 GCU 上报的 ±500 无关。
            Assert.Equal(0, hardware.GpuCoreOffsetUserMinimum);
            Assert.Equal(250, hardware.GpuCoreOffsetUserMaximum);
            // 范围内 → 会发布给 GCU；但无驱动读回 → 无法确认物理生效，必须报失败。
            Assert.False(await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = requested.ToString() }));
            Assert.Contains(requested, published);
            int count = published.Count;
            // 越界 → 本地拒绝，不发任何命令。
            Assert.False(await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "251" }));
            Assert.Equal(count, published.Count);
        }
    }

    [Fact]
    public async Task GpuCoreOffset_UsesMeasuredCapNotReportedRange()
    {
        int publishCount = 0;
        using var hardware = new MechrevoHw((_, _) => { publishCount++; return Task.CompletedTask; },
            new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true });
        hardware.HandleMessage("Fan/Status",
            "{\"GpuCoreClockOffsetMin\":-200,\"GpuCoreClockOffsetMax\":200,\"GPU_CoreClockOffsetOC\":0}");
        hardware.HandleMessage("LCHWOC/Status", "{\"Support\":true,\"Enable\":true}");
        var service = new MechrevoService(hardware);

        // 上报 ±200 只是能力信号，不再压缩滑条——实测边界是 0..250。
        Assert.Equal(0, hardware.GpuCoreOffsetUserMinimum);
        Assert.Equal(250, hardware.GpuCoreOffsetUserMaximum);
        Assert.False(await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "400" }));
        Assert.Equal(0, publishCount);
    }

    [Fact]
    public async Task GpuMemoryOffset_GcuEchoWithoutDriverReadbackIsNotAccepted()
    {
        IDictionary<string, object>? detail = null;
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Fan/Control" && payload is IDictionary<string, object> values &&
                values.TryGetValue("GpuMemoryClockOffsetOC", out object? offset))
            {
                detail = new Dictionary<string, object>(values);
                hardware!.HandleMessage("Fan/Status", $$"""
                    {"GPU_MemoryClockOffsetMinimumHWOC":-500,"GPU_MemoryClockOffsetMaximumHWOC":1000,
                     "GPU_MemoryClockOffsetOC":{{offset}},"OverClockingSwitch":1}
                    """);
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true });

        using (hardware)
        {
            hardware.HandleMessage("Fan/Status",
                "{\"GPU_MemoryClockOffsetMinimumHWOC\":-500,\"GPU_MemoryClockOffsetMaximumHWOC\":1000,\"GPU_MemoryClockOffsetOC\":0,\"OverClockingSwitch\":1}");
            hardware.HandleMessage("LCHWOC/Status", "{\"Enable\":true}");
            var service = new MechrevoService(hardware);

            Assert.Null(hardware.LchwocSupportReported);
            Assert.True(hardware.SupportsGpuOverclock);
            Assert.False(await service.SetCustomDetail(new() { ["GpuMemoryClockOffsetOC"] = "650" }));
            Assert.Equal("650", detail!["GpuMemoryClockOffsetOC"]?.ToString());
        }
    }

    [Fact]
    public async Task IntelTccTarget_IsConvertedBackToRawOffset()
    {
        IDictionary<string, object>? detail = null;
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Fan/Control" && payload is IDictionary<string, object> values && values.ContainsKey("CpuTccOffset"))
            {
                detail = new Dictionary<string, object>(values);
                hardware!.HandleMessage("Fan/Status", "{\"TjMax\":105,\"CPU_TccOffsetMinimum\":10,\"CPU_TccOffsetMaximum\":30,\"CPU_TccOffset\":15}");
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, AmdPlatform = false });
        using (hardware)
        {
            hardware.HandleMessage("Fan/Status", "{\"TjMax\":105,\"CPU_TccOffsetMinimum\":10,\"CPU_TccOffsetMaximum\":30,\"CPU_TccOffset\":10}");
            var service = new MechrevoService(hardware);

            Assert.Equal(95, hardware.TccTarget);
            Assert.True(await service.SetCustomDetail(new() { ["CpuTccOffset"] = "90" }));
            Assert.IsType<int>(detail!["CpuTccOffset"]);
            Assert.Equal("15", detail!["CpuTccOffset"]?.ToString());
        }
    }

    [Fact]
    public async Task IntelTccWithoutReportedTjMax_UsesOfficialHundredDegreeBaseline()
    {
        IDictionary<string, object>? detail = null;
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Fan/Control" && payload is IDictionary<string, object> values && values.ContainsKey("CpuTccOffset"))
            {
                detail = new Dictionary<string, object>(values);
                hardware!.HandleMessage("Fan/Status", "{\"CPU_TccOffsetMinimum\":5,\"CPU_TccOffsetMaximum\":20,\"CPU_TccOffset\":15}");
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true });

        using (hardware)
        {
            hardware.HandleMessage("Fan/Status", "{\"CPU_TccOffsetMinimum\":5,\"CPU_TccOffsetMaximum\":20,\"CPU_TccOffset\":10}");
            var service = new MechrevoService(hardware);

            Assert.Equal(0, hardware.TjMax);
            Assert.Equal(90, hardware.TccTarget);
            Assert.Equal(80, hardware.TccMinimum);
            Assert.Equal(95, hardware.TccMaximum);
            Assert.True(await service.SetCustomDetail(new() { ["CpuTccOffset"] = "85" }));
            Assert.Equal(15, Assert.IsType<int>(detail!["CpuTccOffset"]));
        }
    }

    [Fact]
    public async Task IntelTcc_RemainsAdjustableWhenFirmwareOmitsMinimum()
    {
        IDictionary<string, object>? detail = null;
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Fan/Control" && payload is IDictionary<string, object> values &&
                values.TryGetValue("CpuTccOffset", out object? offset))
            {
                detail = new Dictionary<string, object>(values);
                hardware!.HandleMessage("Fan/Status", $$"""
                    {"CPU_TccOffsetMaximum":20,"CPU_TccOffset":{{offset}},"CPU_TccOffsetSwitch":1}
                    """);
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true });

        using (hardware)
        {
            hardware.HandleMessage("Fan/Status",
                "{\"CPU_TccOffsetMaximum\":20,\"CPU_TccOffset\":10,\"CPU_TccOffsetSwitch\":1}");
            var service = new MechrevoService(hardware);

            Assert.True(hardware.TccStatusSeen);
            Assert.True(hardware.TccAdjustable);
            Assert.Equal(80, hardware.TccMinimum);
            Assert.Equal(95, hardware.TccMaximum);
            Assert.True(await service.SetCustomDetail(new() { ["CpuTccOffset"] = "85" }));
            Assert.Equal(15, Assert.IsType<int>(detail!["CpuTccOffset"]));
        }
    }

    [Fact]
    public async Task AmdTccTarget_UsesDedicatedOfficialField()
    {
        IDictionary<string, object>? detail = null;
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Fan/Control" && payload is IDictionary<string, object> values && values.ContainsKey("CpuAmdTccTarget"))
            {
                detail = new Dictionary<string, object>(values);
                hardware!.HandleMessage("Fan/Status", "{\"CPU_TccOffsetMaximum\":95,\"Cpu_AmdTccTarget\":92}");
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, AmdPlatform = true });
        using (hardware)
        {
            hardware.HandleMessage("Fan/Status", "{\"CPU_TccOffsetMaximum\":95,\"Cpu_AmdTccTarget\":90}");
            var service = new MechrevoService(hardware);

            Assert.True(hardware.TccAdjustable);
            Assert.True(await service.SetCustomDetail(new() { ["CpuTccOffset"] = "92" }));
            Assert.Equal("92", detail!["CpuAmdTccTarget"]?.ToString());
        }
    }

    [Fact]
    public async Task AmdCustomPower_UsesOfficialSplAndSpptFields()
    {
        var commands = new List<IDictionary<string, object>>();
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Fan/Control" && payload is IDictionary<string, object> values &&
                values.TryGetValue("Action", out object? action) &&
                action?.ToString() == "SET_OPERATING_MODE_DETAIL")
            {
                commands.Add(new Dictionary<string, object>(values));
                if (values.TryGetValue("CpuAmdSPL", out object? spl))
                    hardware!.HandleMessage("Fan/Status", $"{{\"CPU_AmdSPL\":{spl},\"CPU_AmdSPPT\":100,\"CPU_AmdFPPT\":110}}");
                if (values.TryGetValue("CpuAmdSPPT", out object? sppt))
                    hardware!.HandleMessage("Fan/Status", $"{{\"CPU_AmdSPL\":90,\"CPU_AmdSPPT\":{sppt},\"CPU_AmdFPPT\":110}}");
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, AmdPlatform = true });

        using (hardware)
        {
            hardware.HandleMessage("Fan/Status", "{\"CPU_AmdSPL\":70,\"CPU_AmdSPPT\":90,\"CPU_AmdFPPT\":100,\"CPU_PL1Minimum\":35,\"CPU_PL1Maximum\":120,\"CPU_PL2Minimum\":35,\"CPU_PL2Maximum\":140}");
            var service = new MechrevoService(hardware);

            Assert.True(hardware.Pl1Adjustable);
            Assert.True(hardware.Pl2Adjustable);
            Assert.True(await service.SetCustomDetail(new() { ["PL1"] = "90", ["PL2"] = "100" }));
            Assert.Contains(commands, command => command.TryGetValue("CpuAmdSPL", out object? value) && value.ToString() == "90");
            Assert.Contains(commands, command => command.TryGetValue("CpuAmdSPPT", out object? value) && value.ToString() == "100");
            Assert.DoesNotContain(commands, command => command.ContainsKey("PL1") || command.ContainsKey("PL2"));
            Assert.Equal(90, hardware.Pl1);
            Assert.Equal(100, hardware.Pl2);
        }
    }

    [Fact]
    public async Task HighPerformancePowerOff_RequiresGcuAndWindowsPowerConfirmation()
    {
        var actions = new List<string>();
        bool? syncedEnabled = null;
        int syncedOperatingMode = -1;
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Setting/Control" && payload is IDictionary<string, object> values &&
                values.TryGetValue("Action", out object? actionValue))
            {
                string action = actionValue?.ToString() ?? string.Empty;
                actions.Add(action);
                if (action == "HIGHPERFORMANCEPOWERMODE_OFF")
                    hardware!.HandleMessage("Setting/Status",
                        "{\"HighPerformancePowerModeSwitch\":\"HIGHPERFORMANCEPOWERMODE_OFF\"}");
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true });

        using (hardware)
        {
            hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":2}");
            hardware.HandleMessage("Setting/Status",
                "{\"HighPerformancePowerModeSwitch\":\"HIGHPERFORMANCEPOWERMODE_ON\"}");
            var service = new MechrevoService(
                hardware,
                () => false,
                () => 0,
                () => false,
                (enabled, operatingMode) =>
                {
                    syncedEnabled = enabled;
                    syncedOperatingMode = operatingMode;
                    return true;
                });

            Assert.True(await service.SwitchQuick("highperf", false));
            Assert.Contains("HIGHPERFORMANCEPOWERMODE_OFF", actions);
            Assert.False(syncedEnabled);
            Assert.Equal(2, syncedOperatingMode);
        }
    }

    [Fact]
    public async Task HighPerformancePowerOff_DoesNotReportSuccessWhenWindowsModeStaysActive()
    {
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Setting/Control" && payload is IDictionary<string, object> values &&
                values.TryGetValue("Action", out object? actionValue) &&
                actionValue?.ToString() == "HIGHPERFORMANCEPOWERMODE_OFF")
            {
                hardware!.HandleMessage("Setting/Status",
                    "{\"HighPerformancePowerModeSwitch\":\"HIGHPERFORMANCEPOWERMODE_OFF\"}");
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true });

        using (hardware)
        {
            hardware.HandleMessage("Setting/Status",
                "{\"HighPerformancePowerModeSwitch\":\"HIGHPERFORMANCEPOWERMODE_ON\"}");
            var service = new MechrevoService(
                hardware,
                () => false,
                () => 0,
                () => false,
                (_, _) => false);

            Assert.False(await service.SwitchQuick("highperf", false));
        }
    }

    [Theory]
    [InlineData(12L, 12L, true, false)]
    [InlineData(12L, 13L, false, false)]
    [InlineData(12L, 13L, true, true)]
    public void SettingConfirmation_RequiresAStatusReceivedAfterTheCommand(
        long versionBeforeCommand, long observedVersion, bool valueMatches, bool expected)
    {
        Assert.Equal(expected, MechrevoService.IsFreshSettingState(
            versionBeforeCommand, observedVersion, valueMatches));
    }
}
