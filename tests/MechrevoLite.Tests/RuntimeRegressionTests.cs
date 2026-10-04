using LibreHardwareMonitor.Hardware;
using MechrevoLite.Hardware;
using MechrevoLite.Mode;

namespace MechrevoLite.Tests;

public class RuntimeRegressionTests
{
    [Theory]
    [InlineData(MechrevoService.ModeOffice, AsusACPI.PerformanceSilent)]
    [InlineData(MechrevoService.ModeGaming, AsusACPI.PerformanceBalanced)]
    [InlineData(MechrevoService.ModeTurbo, AsusACPI.PerformanceTurbo)]
    [InlineData(MechrevoService.ModeCustom, AsusACPI.PerformanceManual)]
    public void MechrevoMode_MapsToTheVisualMode(int serviceMode, int visualMode)
    {
        Assert.Equal(visualMode, MechrevoService.ToVisualMode(serviceMode));
    }

    [Theory]
    [InlineData(AsusACPI.PerformanceManual, true)]
    [InlineData(AsusACPI.PerformanceTurbo, false)]
    [InlineData(MechrevoService.ModeCustom, false)]
    public void CustomVisualState_UsesTheVisualManualEnum(int mode, bool expected) =>
        Assert.Equal(expected, SettingsForm.IsCustomVisualMode(mode));

    [Fact]
    public void TrayCustomProfile_UsesTheSameManualVisualMode()
    {
        int visualMode = MechrevoService.ToVisualMode(MechrevoService.ModeCustom);

        Assert.True(SettingsForm.IsCustomVisualMode(visualMode));
    }

    [Fact]
    public void RepeatedCustomModeNotification_DoesNotRepaintTheMainModeButtons()
    {
        int custom = MechrevoService.ToVisualMode(MechrevoService.ModeCustom);

        Assert.False(SettingsForm.ShouldRefreshVisualMode(custom, false, custom, false));
        Assert.True(SettingsForm.ShouldRefreshVisualMode(AsusACPI.PerformanceBalanced, false, custom, false));
    }

    [Fact]
    public void TurboSubModeChange_StillRefreshesTheMainModeButtons()
    {
        Assert.True(SettingsForm.ShouldRefreshVisualMode(
            AsusACPI.PerformanceTurbo, false,
            AsusACPI.PerformanceTurbo, true));
    }

    [Theory]
    [InlineData("GPU Package", true)]
    [InlineData("GPU Package Power", true)]
    [InlineData("Total Board Power", true)]
    [InlineData("GPU Power Limit", false)]
    [InlineData("GPU Memory Capacity", false)]
    public void LhmMonitor_ClassifiesGpuPowerSensorsSafely(string name, bool expected)
    {
        Assert.Equal(expected, LhmMonitor.IsGpuPowerSensorName(name));
    }

    [Theory]
    [InlineData("CPU Package", true)]
    [InlineData("Package Power", true)]
    [InlineData("CPU Package Power", true)]
    [InlineData("CPU Cores", false)]
    [InlineData("CPU Platform", false)]
    public void LhmMonitor_ClassifiesCpuPackagePowerSensorsSafely(string name, bool expected)
    {
        Assert.Equal(expected, LhmMonitor.IsCpuPowerSensorName(name));
    }

    [Theory]
    [InlineData(true, 42f, true, 3f, 2f, 42f)]
    [InlineData(true, 0f, true, 3f, 2f, 0f)]
    [InlineData(true, null, true, 3f, 2f, null)]
    [InlineData(false, null, true, 3f, 2f, 3f)]
    [InlineData(false, null, false, null, 2f, 2f)]
    public void LhmMonitor_PrefersDiscreteGpuTelemetryOverIntegratedGpuTelemetry(
        bool nvidiaPresent, float? nvidiaPower, bool amdPresent, float? amdPower,
        float? integratedPower, float? expected)
    {
        Assert.Equal(expected, LhmMonitor.SelectGpuPower(
            nvidiaPresent, nvidiaPower, amdPresent, amdPower, integratedPower));
    }

    [Theory]
    [InlineData(HardwareType.GpuNvidia, true)]
    [InlineData(HardwareType.GpuAmd, true)]
    [InlineData(HardwareType.Cpu, false)]
    public void LhmMonitor_RecognizesGpuHardwareFamilies(HardwareType type, bool expected)
    {
        Assert.Equal(expected, LhmMonitor.IsGpuHardwareType(type));
    }

    [Theory]
    [InlineData(null, 42f, 42f)]
    [InlineData(65f, 42f, 65f)]
    [InlineData(0f, 42f, 42f)]
    [InlineData(-1f, 42f, 42f)]
    [InlineData(0f, null, null)]
    [InlineData(null, null, null)]
    public void HardwareControl_UsesGpuPowerFallbackWhenLocalSensorIsMissingOrInvalid(
        float? localPower, float? fallbackPower, float? expected)
    {
        Assert.Equal(expected, HardwareControl.ResolveGpuPower(localPower, fallbackPower));
    }

    [Theory]
    [InlineData(18f, 14f, 18f)]
    [InlineData(0f, 19.1f, 19.1f)]
    [InlineData(-1f, 19.1f, 19.1f)]
    [InlineData(0f, null, null)]
    [InlineData(null, null, null)]
    public void HardwareControl_UsesCpuPowerFallbackWhenLocalSensorIsMissingOrInvalid(
        float? localPower, float? fallbackPower, float? expected)
    {
        Assert.Equal(expected, HardwareControl.ResolveCpuPower(localPower, fallbackPower));
    }

    [Fact]
    public void GcuSupportWithoutRanges_UsesBoundedUiRange()
    {
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask,
            new MechrevoDeviceCapabilities { ProfileAvailable = true });
        hardware.HandleMessage("LCHWOC/Status", "{\"Support\":true,\"Enable\":false}");

        Assert.True(hardware.SupportsGpuOverclock);
        // 上报只有 Support 没有范围时，滑条用实测边界（核心实测 0..250）。
        Assert.Equal(0, hardware.GpuCoreOffsetUserMinimum);
        Assert.Equal(250, hardware.GpuCoreOffsetUserMaximum);
    }

    [Fact]
    public void ColorCalibration_StatusAliasesAreParsedWithRegistryFallback()
    {
        using var hardware = new MechrevoHw(null,
            new MechrevoDeviceCapabilities { ProfileAvailable = true });
        hardware.HandleMessage("Setting/Status", "{\"ColorCalibrationSwitch_Status\":\"COLOR_CALIBRATION_ON\",\"CurrentColorCalibration\":3}");

        Assert.True(hardware.ColorCalibrationSwitchSeen);
        Assert.True(hardware.ColorCalibrationModeSeen);
        Assert.Equal(3, hardware.ColorCalibrationMode);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    public void ColorCalibrationSwitch_PrefersFreshRuntimeStateOverRegistryFallback(
        bool? registryState, bool? runtimeState, bool? expected) =>
        Assert.Equal(expected,
            MechrevoService.ResolveColorCalibrationSwitch(registryState, runtimeState));

    [Fact]
    public async Task CustomProfileSwitch_UsesFreshTargetStatusBeforeNotifyingUi()
    {
        MechrevoHw? hardware = null;
        int statusRequests = 0;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Fan/Control" && payload is IDictionary<string, object> values &&
                values.TryGetValue("Action", out object? action))
            {
                string command = action?.ToString() ?? "";
                if (command is "OPERATING_CUSTOM_MODE" or "GETSTATUS")
                {
                    if (command == "GETSTATUS") statusRequests++;
                    hardware!.HandleMessage("Fan/Status",
                        "{\"OperatingMode\":3,\"CustomProfileIndex\":2,\"CPU_PL1\":55,\"CPU_PL2\":80}");
                }
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true });

        using (hardware)
        {
            var service = new MechrevoService(hardware);
            int notifications = 0;
            hardware.CustomModeChanged += () => notifications++;

            Assert.True(await service.SwitchCustomProfile(2));
            Assert.Equal(0, statusRequests);
            Assert.True(hardware.CustomProfileStatusVersion > 0);
            Assert.True(notifications >= 1);
            Assert.Equal(2, hardware.CustomProfileIndex);
            Assert.Equal(55, hardware.Pl1);
        }
    }

    [Fact]
    public async Task LateCustomModeStatus_StillNotifiesUiAfterServiceTimeout()
    {
        MechrevoHw? hardware = null;
        int delayedStatusScheduled = 0;
        var modeChanged = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Fan/Control" && payload is IDictionary<string, object> values &&
                values.TryGetValue("Action", out object? action) &&
                action?.ToString() == "OPERATING_CUSTOM_MODE" &&
                Interlocked.Exchange(ref delayedStatusScheduled, 1) == 0)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2_100);
                    hardware!.HandleMessage("Fan/Status",
                        "{\"OperatingMode\":3,\"CustomProfileIndex\":0,\"CPU_PL1\":55,\"CPU_PL2\":80}");
                });
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true });

        using (hardware)
        {
            hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":1,\"CustomProfileIndex\":0}");
            var service = new MechrevoService(hardware);
            int modeChangedCount = 0;
            service.ModeChanged += mode =>
            {
                Interlocked.Increment(ref modeChangedCount);
                modeChanged.TrySetResult(mode);
            };

            bool confirmed = await service.SwitchCustomProfile(0);

            // The delayed GCU packet arrives after SwitchCustomProfile's bounded
            // confirmation window, but the eventual hardware state must still
            // converge the main window to Custom.
            Assert.False(confirmed);
            Assert.Equal(MechrevoService.ModeCustom,
                await modeChanged.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(3, hardware.OperatingMode);

            // Keep filtering queued pre-switch packets after the target was
            // accepted; they must not roll the UI back to the old mode.
            hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":1,\"CustomProfileIndex\":0}");
            Assert.Equal(3, hardware.OperatingMode);
            Assert.Equal(1, Volatile.Read(ref modeChangedCount));
        }
    }
}
