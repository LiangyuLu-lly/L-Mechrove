using MechrevoLite.Hardware;
using MechrevoLite.Helpers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Text.Json;

namespace MechrevoLite.Tests;

public class MechrevoHardwareTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(15)]
    [InlineData(17)]
    public void IsInvalidCurve_RejectsUnexpectedLengthsWithoutIndexingPastInput(int length)
    {
        Assert.True(AsusACPI.IsInvalidCurve(new byte[length]));
    }

    [Fact]
    public void IsInvalidCurve_AcceptsACompleteSafeCurve()
    {
        byte[] curve = [0, 48, 52, 56, 60, 70, 80, 90, 20, 30, 40, 50, 60, 70, 80, 100];

        Assert.False(AsusACPI.IsInvalidCurve(curve));
    }

    [Fact]
    public async Task WaitForState_CompletesAsSoonAsMatchingStatusArrives()
    {
        using var hardware = new MechrevoHw();
        var elapsed = Stopwatch.StartNew();
        Task<bool> wait = hardware.WaitForStateAsync(
            () => hardware.OperatingMode == 2,
            TimeSpan.FromSeconds(2));

        await Task.Delay(25);
        hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":2}");

        Assert.True(await wait);
        Assert.True(elapsed.ElapsedMilliseconds < 500, $"State wait took {elapsed.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void HandleMessage_RaisesTelemetryRefreshOnlyForTelemetryTopics()
    {
        using var hardware = new MechrevoHw();
        int refreshes = 0;
        hardware.DataChanged += () => refreshes++;

        hardware.HandleMessage("Setting/Status", "{\"UsbCharger\":\"USB_CHARGER_STATUS_ON\"}");
        hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":1}");
        Assert.Equal(0, refreshes);

        hardware.HandleMessage("System/CpuInfo", "{\"CpuTemperature\":55,\"CpuUsage\":12}");
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public void ConnectionReady_RaisesForInitialConnectionAndEveryReconnect()
    {
        using var hardware = new MechrevoHw();
        var generations = new List<int>();
        hardware.ConnectionReady += generations.Add;

        Assert.Equal(1, hardware.NotifyConnectionReady());
        Assert.Equal(2, hardware.NotifyConnectionReady());

        Assert.Equal([1, 2], generations);
    }

    [Fact]
    public void HasTelemetrySince_ChangesOnlyAfterSystemTelemetryArrives()
    {
        using var hardware = new MechrevoHw();
        long started = Environment.TickCount64;

        hardware.HandleMessage("Setting/Status", "{\"CloseTimer\":15}");
        Assert.False(hardware.HasTelemetrySince(started));

        hardware.HandleMessage("System/CpuInfo", "{\"CpuTemperature\":55}");
        Assert.True(hardware.HasTelemetrySince(started));
    }

    [Fact]
    public void IsTelemetryStale_RequiresRecentSystemTelemetry()
    {
        using var hardware = new MechrevoHw();
        const long now = 1_000_000;

        hardware.HandleMessage("Setting/Status", "{\"CloseTimer\":15}");
        Assert.True(hardware.IsTelemetryStale(TimeSpan.FromSeconds(45), now));

        hardware.HandleMessage("System/CpuInfo", "{\"CpuTemperature\":55}");
        long receivedAt = Environment.TickCount64;
        Assert.False(hardware.IsTelemetryStale(TimeSpan.FromSeconds(45), receivedAt + 44_000));
        Assert.True(hardware.IsTelemetryStale(TimeSpan.FromSeconds(45), receivedAt + 45_001));
    }

    [Fact]
    public async Task RefreshAll_RearmsTelemetryAndRequestsPersistentState()
    {
        var published = new ConcurrentQueue<(string Topic, string? Command)>();
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            var values = Assert.IsAssignableFrom<IDictionary<string, object>>(payload);
            string? command = values.TryGetValue("Action", out object? action)
                ? action?.ToString()
                : values.TryGetValue("Report", out object? report) ? report?.ToString() : null;
            published.Enqueue((topic, command));
            return Task.CompletedTask;
        });
        var service = new MechrevoService(hardware);

        await service.RefreshAll();

        Assert.Contains(("System/Control", "System_ON"), published);
        Assert.Contains(("BatteryProtection/Control", "GET"), published);
        Assert.Contains(("Fan/Control", "GET_FAN_SPEED_CURVE_SETTING"), published);
        Assert.Contains(("Keyboard/Ctrl", "GETSTATUS"), published);
    }

    [Fact]
    public async Task SwitchMode_UsesStatusEventInsteadOfFixedDelay()
    {
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Fan/Control" && TryGetAction(payload, out string? action) && TryGetOperatingMode(action, out int opMode))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(25);
                    hardware!.HandleMessage("Fan/Status", $"{{\"OperatingMode\":{opMode}}}");
                });
            }
            return Task.CompletedTask;
        });
        using (hardware)
        {
            var service = new MechrevoService(hardware);
            var elapsed = Stopwatch.StartNew();

            Assert.True(await service.SwitchMode(MechrevoService.ModeTurbo));
            Assert.Equal(MechrevoService.ModeTurbo, service.CurrentMode);
            Assert.True(elapsed.ElapsedMilliseconds < 500, $"Mode switch took {elapsed.ElapsedMilliseconds}ms");
        }
    }

    [Fact]
    public async Task SwitchMode_RapidRequestsSkipSupersededQueuedMode()
    {
        MechrevoHw? hardware = null;
        var commands = new ConcurrentQueue<string>();
        var firstPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Fan/Control" && TryGetAction(payload, out string? action) && TryGetOperatingMode(action, out int opMode))
            {
                commands.Enqueue(action!);
                firstPublished.TrySetResult();
                _ = Task.Run(async () =>
                {
                    await Task.Delay(40);
                    hardware!.HandleMessage("Fan/Status", $"{{\"OperatingMode\":{opMode}}}");
                });
            }
            return Task.CompletedTask;
        });
        using (hardware)
        {
            var service = new MechrevoService(hardware);
            Task<bool> first = service.SwitchMode(MechrevoService.ModeOffice);
            await firstPublished.Task;
            Task<bool> superseded = service.SwitchMode(MechrevoService.ModeGaming);
            Task<bool> latest = service.SwitchMode(MechrevoService.ModeTurbo);

            await Task.WhenAll(first, superseded, latest);

            Assert.False(await first);
            Assert.False(await superseded);
            Assert.True(await latest);
            Assert.Equal(MechrevoService.ModeTurbo, service.CurrentMode);
            Assert.Equal(2, commands.Count);
            Assert.DoesNotContain("OPERATING_GAMING_MODE", commands);
        }
    }

    [Fact]
    public async Task ColorCalibration_ModeSwitchUsesOnlyTheSelectedOfficialCommand()
    {
        bool enabled = false;
        int actualMode = 1;
        var published = new List<IDictionary<string, object>>();
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Setting/Control" && payload is IDictionary<string, object> values)
            {
                published.Add(new Dictionary<string, object>(values));
                if (TryGetAction(payload, out string? action) && action == "COLOR_CALIBRATION_ON_P3")
                {
                    enabled = true;
                    actualMode = 3;
                    hardware!.HandleMessage("Setting/Status", "{\"ColorCalibrationResultCode\":0}");
                }
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, ColorCalibration = true });

        using (hardware)
        {
            var service = new MechrevoService(hardware, () => enabled, () => actualMode, () => false);

            Assert.True(await service.SetColorCalibration(3));
            Assert.Single(published);
            Assert.Equal("COLOR_CALIBRATION_ON_P3", GetPayloadString(published[0], "Action"));
            Assert.DoesNotContain(published, item => GetPayloadString(item, "Action") == "COLOR_CALIBRATION_ON");
        }
    }

    [Fact]
    public async Task ColorCalibration_HdrBlocksEnableWithoutPublishing()
    {
        var published = new List<object>();
        using var hardware = new MechrevoHw((_, payload) =>
        {
            published.Add(payload);
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, ColorCalibration = true });
        var service = new MechrevoService(hardware, () => false, () => 1, () => true);

        Assert.False(await service.SetColorCalibration(1));
        Assert.Empty(published);
    }

    [Fact]
    public async Task ColorCalibration_DisableUsesCurrentProfileAndConfirmsRegistryState()
    {
        bool enabled = true;
        int actualMode = 3;
        var published = new List<IDictionary<string, object>>();
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Setting/Control" && payload is IDictionary<string, object> values)
            {
                published.Add(new Dictionary<string, object>(values));
                if (TryGetAction(payload, out string? action) && action == "COLOR_CALIBRATION_OFF")
                {
                    enabled = false;
                    hardware!.HandleMessage("Setting/Status", "{\"ColorCalibrationResultCode\":0}");
                }
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, ColorCalibration = true });

        using (hardware)
        {
            var service = new MechrevoService(hardware, () => enabled, () => actualMode, () => false);

            Assert.True(await service.SetColorCalibration(0));
            Assert.Collection(
                published,
                first =>
                {
                    Assert.Equal("COLOR_CALIBRATION_OFF", GetPayloadString(first, "Action"));
                    Assert.Equal("P3", GetPayloadString(first, "FileName"));
                },
                second => Assert.Equal("DISPLAY_FEATURE_STATUS_ON", GetPayloadString(second, "Action")));
        }
    }

    /// <summary>
    /// 真机 2026-09-11：点 sRGB 后档位回读立刻变 2（屏幕确实变色），但 GCU 的开关回读
    /// 恒为 False。要求"开关也对上"会让每次成功切换都判失败——界面先闪"失败"再被回显
    /// 纠正回"当前：sRGB"（用户报的就是这个）。开方向只认档位。
    /// </summary>
    [Fact]
    public async Task ColorCalibration_ConfirmsOnTheModeReadbackEvenWhenTheSwitchStaysOff()
    {
        int actualMode = 1;
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Setting/Control" && TryGetAction(payload, out string? action) &&
                action == "COLOR_CALIBRATION_ON_SRGB")
            {
                actualMode = 2;
                hardware!.HandleMessage("Setting/Status", "{\"ColorCalibrationResultCode\":0}");
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, ColorCalibration = true });

        using (hardware)
        {
            var service = new MechrevoService(hardware, () => false, () => actualMode, () => false);

            Assert.True(await service.SetColorCalibration(2));
        }
    }

    /// <summary>固件明确报错误码时，即使档位回读看起来对上了也必须判失败。</summary>
    [Fact]
    public async Task ColorCalibration_FirmwareErrorCodeStillFailsEvenWhenTheModeMatches()
    {
        int actualMode = 1;
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Setting/Control" && TryGetAction(payload, out string? action) &&
                action == "COLOR_CALIBRATION_ON_P3")
            {
                actualMode = 3;
                hardware!.HandleMessage("Setting/Status", "{\"ColorCalibrationResultCode\":3}");
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, ColorCalibration = true });

        using (hardware)
        {
            var service = new MechrevoService(hardware, () => false, () => actualMode, () => false);

            Assert.False(await service.SetColorCalibration(3));
        }
    }

    [Fact]
    public async Task ColorCalibration_RejectsRegistryMatchWithoutFreshGcuCalibrationStatus()
    {
        bool enabled = false;
        int actualMode = 1;
        var published = new List<IDictionary<string, object>>();
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Setting/Control" && payload is IDictionary<string, object> values)
            {
                published.Add(new Dictionary<string, object>(values));
                if (TryGetAction(payload, out string? action) && action == "COLOR_CALIBRATION_ON_P3")
                {
                    enabled = true;
                    actualMode = 3;
                }
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, ColorCalibration = true });
        var service = new MechrevoService(hardware, () => enabled, () => actualMode, () => false);

        Assert.False(await service.SetColorCalibration(3));
        Assert.Single(published, item => GetPayloadString(item, "Action") == "COLOR_CALIBRATION_ON_P3");
    }

    private static bool TryGetAction(object payload, out string? action)
    {
        action = payload is IDictionary<string, object> values && values.TryGetValue("Action", out object? value)
            ? value?.ToString()
            : null;
        return action is not null;
    }

    private static string? GetPayloadString(IDictionary<string, object> payload, string key) =>
        payload.TryGetValue(key, out object? value) ? value?.ToString() : null;

    private static bool TryGetOperatingMode(string? action, out int operatingMode)
    {
        operatingMode = action switch
        {
            "OPERATING_OFFICE_MODE" => 0,
            "OPERATING_GAMING_MODE" => 1,
            "OPERATING_TURBO_MODE" => 2,
            "OPERATING_CUSTOM_MODE" => 3,
            _ => -1,
        };
        return operatingMode >= 0;
    }

    [Fact]
    public void NormalizeFanCurve_ClampsAndEnforcesMonotonicCurve()
    {
        int[] requested = [0, 10, 55, 40, 200, 20, 60, 70, 80, 90, 95, 1, 2, 3, 4, 5];
        byte[] temperatures = [30, 40, 50, 60, 70, 80, 90, 100, 255, 255, 255, 255, 255, 255, 255, 255];

        int[] actual = MechrevoHw.NormalizeFanCurve(requested, temperatures);

        Assert.Equal(0, actual[0]);
        Assert.Equal(10, actual[1]);
        Assert.Equal(55, actual[2]);
        Assert.Equal(55, actual[3]);
        Assert.Equal(100, actual[7]);
        Assert.All(actual.Skip(8), value => Assert.Equal(100, value));
    }

    [Fact]
    public void NormalizeFanCurve_AllowsLastEffectiveTemperatureBelowFullDuty()
    {
        int[] requested = [20, 30, 40, 50, 60, 65, 70, 80];
        byte[] temperatures = [30, 40, 50, 60, 65, 70, 75, 81, 255, 255, 255, 255, 255, 255, 255, 255];

        int[] actual = MechrevoHw.NormalizeFanCurve(requested, temperatures);

        Assert.Equal(80, actual[7]);
        Assert.All(actual.Skip(8), value => Assert.Equal(80, value));
    }

    [Fact]
    public void HandleMessage_ParsesCapabilitiesAndPublishesImmutableSnapshots()
    {
        using var hardware = new MechrevoHw();
        hardware.HandleMessage("Fan/Status", """
            {"OperatingMode":3,"CPU_PL1":45,"CPU_PL2":80,"CPU_TccOffset":90,
             "CPU_PL1Minimum":10,"CPU_PL1Maximum":65,"CPU_PL2Minimum":20,"CPU_PL2Maximum":100,
             "TjMax":105,"GPU_ConfigurableTGPMinimum":80,"GPU_ConfigurableTGPMaximum":140,
             "GPU_DynamicBoostMinimum":5,"GPU_DynamicBoostMaximum":25,"FanControlRespective":"true"}
            """);
        hardware.HandleMessage("GPUDevice/Status", "{\"currentHZList\":[60,240,60,0],\"currentHZ\":240}");

        Assert.Equal(10, hardware.Pl1Minimum);
        Assert.Equal(65, hardware.Pl1Maximum);
        Assert.Equal(75, hardware.TccMinimum);
        Assert.Equal(95, hardware.TccMaximum);
        Assert.Equal(75, hardware.TccTarget);
        Assert.True(hardware.FanRespective);
        Assert.Equal([240, 60], hardware.HzList);
        Assert.IsAssignableFrom<IReadOnlyList<int>>(hardware.HzList);
    }

    [Fact]
    public void IntelTcc_ZeroRawRangeKeepsOfficialTargetRangeAdjustable()
    {
        using var hardware = new MechrevoHw();

        hardware.HandleMessage("Fan/Status",
            "{\"CPU_TccOffset\":10,\"CPU_TccOffsetMinimum\":0,\"CPU_TccOffsetMaximum\":0,\"TjMax\":100}");

        Assert.True(hardware.TccAdjustable);
        Assert.Equal(75, hardware.TccMinimum);
        Assert.Equal(95, hardware.TccMaximum);
        Assert.Equal(90, hardware.TccTarget);
    }

    [Fact]
    public void FanTable_ReportsIndependentFanCapabilityBeforeFanStatus()
    {
        using var hardware = new MechrevoHw();

        hardware.HandleMessage("Fan/Table",
            "{\"Name\":\"M4T1\",\"FanControlRespective\":true,\"CPU\":[],\"GPU\":[]}");

        Assert.True(hardware.SupportsFanRespective);
        Assert.True(hardware.FanRespective);
    }

    [Fact]
    public void FanTable_PreservesAValidFlatCustomCpuCurve()
    {
        using var hardware = new MechrevoHw();
        int[] cpuDuties = [0, 60, 60, 60, 60, 65, 75, 90];
        object[] Points(int[] duties) => Enumerable.Range(0, 16)
            .Select(index => (object)new
            {
                UpT = index < duties.Length ? 30 + index * 10 : 255,
                Duty = index < duties.Length ? duties[index] : 255,
            })
            .ToArray();
        string payload = JsonSerializer.Serialize(new
        {
            Name = "M4T1",
            CPU = Points(cpuDuties),
            GPU = Points([0, 35, 40, 50, 60, 70, 85, 100]),
        });

        hardware.HandleMessage("Fan/Table", payload);

        Assert.Equal(cpuDuties, hardware.CpuCurveDuty.Take(cpuDuties.Length).Select(value => (int)value));
    }

    [Fact]
    public void KeyboardConfig_ParsesTextKeysBeforeNumericSettings()
    {
        string directory = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var keyboard = new KeyboardRgb(Path.Combine(directory, "rgb.cfg"));

            keyboard.LoadConfigLines(["staticColors=255,65280", "brightness=999", "targetFps=1", "closeTimer=-5"]);

            // 文本行必须先于数值解析被消费，后续数值行照常应用并夹取。
            Assert.Equal(Color.FromArgb(255), keyboard.StaticColors[0]);
            Assert.Equal(100, keyboard.Brightness);
            Assert.Equal(15, keyboard.TargetFps);
            Assert.Equal(0, keyboard.CloseTimerMinutes);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Instances_UseBrokerReservedLMechrevoIdentity()
    {
        using var first = new MechrevoHw();
        using var second = new MechrevoHw();

        Assert.Equal("UWPClient_4", first.ClientId);
        Assert.Equal(first.ClientId, second.ClientId);
    }

    [Fact]
    public void HandleMessage_KeepsLogoAndLightbarPowerIndependent()
    {
        using var hardware = new MechrevoHw();

        hardware.HandleMessage("HidLightbar/Status", "{\"powerStatus\":\"On\"}");
        hardware.HandleMessage("HidLightbar_Logo/Status", "{\"powerStatus\":\"Off\"}");

        Assert.True(hardware.QuickSwitches["lightbar"]);
        Assert.False(hardware.QuickSwitches["logolight"]);
        Assert.True(hardware.SupportsLogoLight);
    }

    [Fact]
    public async Task StaticLogoCapability_AllowsLogoEffectBeforeRuntimeStatusArrives()
    {
        string? publishedTopic = null;
        using var hardware = new MechrevoHw((topic, _) =>
        {
            publishedTopic = topic;
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, LogoLight = true });
        var service = new MechrevoService(hardware);

        Assert.True(hardware.SupportsLogoLight);
        Assert.True(await service.SetLightEffect("HidLightbar_Logo/Ctrl", "Single"));
        Assert.Equal("HidLightbar_Logo/Ctrl", publishedTopic);
    }

    [Fact]
    public void HandleMessage_TracksKeyboardSpeedForDiagnostics()
    {
        using var hardware = new MechrevoHw();

        hardware.HandleMessage("Keyboard/Status",
            "{\"effect\":\"Wave\",\"light\":\"3\",\"brightNess\":\"75\",\"speed\":\"2\",\"direction\":\"Left\",\"powerStatus\":\"On\"}");

        Assert.Equal("Wave", hardware.KeyboardEffect);
        Assert.Equal(3, hardware.KeyboardLight);
        Assert.Equal(75, hardware.KeyboardBrightness);
        Assert.Equal(2, hardware.KeyboardSpeed);
        Assert.Equal("Left", hardware.KeyboardDirection);
        Assert.True(hardware.KeyboardPower);
    }

    [Fact]
    public void HandleMessage_IncrementsKeyboardStatusVersionForEachReport()
    {
        using var hardware = new MechrevoHw();

        Assert.Equal(0, hardware.KeyboardStatusVersion);
        hardware.HandleMessage("Keyboard/Status", "{\"light\":\"3\"}");
        Assert.Equal(1, hardware.KeyboardStatusVersion);
        hardware.HandleMessage("Keyboard/Status", "{\"light\":\"2\"}");
        Assert.Equal(2, hardware.KeyboardStatusVersion);
    }

    [Fact]
    public void HandleMessage_UsesLegacyLightWhenBrightnessPercentIsAbsent()
    {
        using var hardware = new MechrevoHw();

        hardware.HandleMessage("Keyboard/Status", "{\"light\":\"3\"}");
        Assert.Equal(75, hardware.KeyboardBrightness);
        hardware.HandleMessage("Keyboard/Status", "{\"light\":\"2\"}");
        Assert.Equal(50, hardware.KeyboardBrightness);
    }

    [Fact]
    public void HandleMessage_ConvertsFiveLevelBrightnessStatusToPercent()
    {
        using var hardware = new MechrevoHw();

        hardware.HandleMessage("Keyboard/Status", "{\"brightNess\":\"4\",\"light\":\"4\"}");

        Assert.Equal(100, hardware.KeyboardBrightness);
    }

    [Fact]
    public void HandleMessage_RaisesStateChangedAfterKeyboardBrightnessIsParsed()
    {
        using var hardware = new MechrevoHw();
        string? topic = null;
        hardware.StateChanged += changedTopic => topic = changedTopic;

        hardware.HandleMessage("Keyboard/Status", "{\"brightNess\":\"2\"}");

        Assert.Equal("Keyboard/Status", topic);
        Assert.Equal(50, hardware.KeyboardBrightness);
    }

    [Theory]
    [InlineData(false, "NOT_SAVE")]
    [InlineData(true, "SAVE")]
    public async Task SetKeyboardEffect_ControlsFirmwarePersistence(bool save, string expected)
    {
        IDictionary<string, object>? published = null;
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Keyboard/Ctrl") published = Assert.IsAssignableFrom<IDictionary<string, object>>(payload);
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { Keyboard = true });
        var service = new MechrevoService(hardware);

        Assert.True(await service.SetKeyboardEffect("Single", 4, 2, "None", Color.FromArgb(0, 180, 255), save));
        Assert.NotNull(published);
        Assert.Equal(expected, published!["nv_save"]?.ToString());
    }

    [Theory]
    [InlineData(0, "KEYBOARD_LIGHTBAR_TIMER_OFF")]
    [InlineData(45, "KEYBOARD_LIGHTBAR_TIMER_ON")]
    public async Task SwitchCloseTimer_PublishesAndConfirmsRequestedMinutes(int minutes, string expectedAction)
    {
        IDictionary<string, object>? published = null;
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Setting/Control" && payload is IDictionary<string, object> values)
            {
                published = new Dictionary<string, object>(values);
                hardware!.HandleMessage("Setting/Status", $"{{\"CloseTimer\":{minutes}}}");
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { Keyboard = true });

        using (hardware)
        {
            var service = new MechrevoService(hardware);

            Assert.True(await service.SwitchCloseTimer(minutes));
            Assert.NotNull(published);
            Assert.Equal(expectedAction, published!["Action"]?.ToString());
            if (minutes > 0) Assert.Equal(minutes.ToString(), published["Mins"]?.ToString());
            else Assert.False(published.ContainsKey("Mins"));
        }
    }

    [Theory]
    [InlineData(40, 40, 1)]
    [InlineData(60, 60, 1)]
    [InlineData(100, 100, 1)]
    [InlineData(255, 100, 1)]
    [InlineData(0, 0, 0)]
    public void WaterCoolerFanFrame_UsesPercentNotByteScaledPwm(int requested, byte expectedDuty, byte expectedOn)
    {
        byte[] frame = WaterCoolerBle.BuildFanFrame(requested);

        Assert.Equal(new byte[] { 0xFE, 0x1B, expectedOn, expectedDuty, 0, 0, 0, 0xEF }, frame);
    }

    [Theory]
    [InlineData(45, WaterCoolerBle.PumpV7)]
    [InlineData(60, WaterCoolerBle.PumpV8)]
    [InlineData(90, WaterCoolerBle.PumpV11)]
    [InlineData(100, WaterCoolerBle.PumpV12)]
    public void WaterCoolerPumpFrame_PreservesDutyAndNonMonotonicVoltageCode(byte duty, byte voltage)
    {
        byte[] frame = WaterCoolerBle.BuildPumpFrame(duty, voltage);

        Assert.Equal(new byte[] { 0xFE, 0x1C, 1, duty, voltage, 0, 0, 0xEF }, frame);
    }

    [Theory]
    [InlineData(35, 0, 0)]
    [InlineData(50, 1, 1)]
    [InlineData(65, 2, 2)]
    [InlineData(78, 3, 3)]
    [InlineData(90, 3, 4)]
    public void WaterCoolerAutomaticProfiles_FollowTemperatureBands(int temperature, int pump, int fan)
    {
        Assert.Equal(pump, WaterCoolerBle.SelectAutomaticPumpProfile(temperature));
        Assert.Equal(fan, WaterCoolerBle.SelectAutomaticFanProfile(temperature));
    }

    [Fact]
    public void WaterCoolerAutomaticProfiles_UseHysteresisNearBoundary()
    {
        Assert.Equal(1, WaterCoolerBle.SelectAutomaticFanProfile(56, current: 1));
        Assert.Equal(2, WaterCoolerBle.SelectAutomaticFanProfile(58, current: 1));
        Assert.Equal(2, WaterCoolerBle.SelectAutomaticFanProfile(54, current: 2));
        Assert.Equal(1, WaterCoolerBle.SelectAutomaticFanProfile(53, current: 2));
    }

    [Theory]
    [InlineData(true, false, 1, 12, false)]
    [InlineData(true, false, 2, 7, false)]
    [InlineData(true, false, 2, 8, true)]
    [InlineData(true, true, 4, 20, false)]
    [InlineData(false, false, 4, 20, false)]
    public void WaterCoolerFlowFault_RequiresStableRepeatedFaultsAfterStartupGrace(
        bool connected, bool meterNormal, int consecutiveFaults, int connectedSeconds, bool expected)
    {
        Assert.Equal(expected, WaterCoolerBle.HasConfirmedFlowFault(
            connected, meterNormal, consecutiveFaults, TimeSpan.FromSeconds(connectedSeconds)));
    }

    [Theory]
    [InlineData(0, 45, WaterCoolerBle.PumpV7)]
    [InlineData(1, 60, WaterCoolerBle.PumpV8)]
    [InlineData(2, 90, WaterCoolerBle.PumpV11)]
    [InlineData(3, 100, WaterCoolerBle.PumpV12)]
    public void WaterCoolerPumpProfiles_KeepProtocolDutyAndVoltageTogether(
        int profile, int expectedDuty, byte expectedVoltage)
    {
        var setting = WaterCoolerBle.PumpSetting(profile);

        Assert.Equal(expectedDuty, setting.Duty);
        Assert.Equal(expectedVoltage, setting.Voltage);
    }

    [Fact]
    public void WaterCoolerLightingFrames_KeepHeadAndMk2FanEffectsDistinct()
    {
        byte[] colorful = WaterCoolerBle.BuildRgbFrame(
            WaterCoolerBle.CmdHeadRgb, true, WaterCoolerBle.RgbColorful, 0, 0, 0);
        byte[] breatheColor = WaterCoolerBle.BuildRgbFrame(
            WaterCoolerBle.CmdHeadRgb, true, WaterCoolerBle.RgbBreatheColor, 0, 0, 0);
        byte[] rotate = WaterCoolerBle.BuildRgbFrame(
            WaterCoolerBle.CmdFanRgb, true, WaterCoolerBle.FanRgbRotate, 0, 0, 0);
        byte[] rainbow = WaterCoolerBle.BuildRgbFrame(
            WaterCoolerBle.CmdFanRgb, true, WaterCoolerBle.FanRgbRainbow, 0, 0, 0);
        byte[] off = WaterCoolerBle.BuildRgbFrame(
            WaterCoolerBle.CmdHeadRgb, false, WaterCoolerBle.RgbStatic, 0, 0, 0);

        Assert.Equal(0x1E, colorful[1]);
        Assert.Equal(2, colorful[6]);
        Assert.Equal(3, breatheColor[6]);
        Assert.Equal(0x33, rotate[1]);
        Assert.Equal(4, rotate[6]);
        Assert.Equal(5, rainbow[6]);
        Assert.Equal(0, off[2]);
        Assert.Equal(5, new[] { colorful, breatheColor, rotate, rainbow, off }.Distinct(new ByteArrayComparer()).Count());
    }

    [Theory]
    [InlineData("LCT22002", "", true)]
    [InlineData("水冷箱", "CoolingSystem LCT22002 1.0", true)]
    [InlineData("水冷箱", "CoolingSystem LCT21001 1.0", false)]
    public void WaterCoolerFanLighting_UsesDeviceNameOrFirmwareModel(
        string deviceName, string firmwareVersion, bool expected)
    {
        Assert.Equal(expected, WaterCoolerBle.IsFanLedModel(deviceName, firmwareVersion));
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.SequenceEqual(y);
        public int GetHashCode(byte[] obj) => obj.Aggregate(17, (hash, value) => hash * 31 + value);
    }

    [Fact]
    public async Task SetPowerLimits_RejectsUnknownDeviceCapabilitiesWithoutPublishing()
    {
        using var hardware = new MechrevoHw();

        Assert.False(await hardware.SetPl1Pl2(45, 80));
    }

    [Theory]
    [InlineData("13688", true)]
    [InlineData("80, 13688, 443", true)]
    [InlineData("13000-14000", true)]
    [InlineData("13689", false)]
    [InlineData(null, false)]
    public void FirewallPortParser_HandlesListsAndRanges(string? ports, bool expected)
    {
        Assert.Equal(expected, MqttSecurity.PortListContains(ports, 13688));
    }
}
