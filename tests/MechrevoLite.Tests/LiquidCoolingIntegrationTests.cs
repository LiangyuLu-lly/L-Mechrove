using System.Collections.Concurrent;
using System.Drawing;
using System.Windows.Forms;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

public class LiquidCoolingIntegrationTests
{
    [Fact]
    public void GcuLiquidCoolingStatus_RequiresConnectedConnectStringAndTracksOfficialLightingFields()
    {
        using var hardware = new MechrevoHw();

        hardware.HandleMessage("BT_LC/Status", """
            {
              "connected": true,
              "ConnectString": "Connected",
              "AutoConnect": true,
              "LC_action": true,
              "LC_CoolingAuto": true,
              "LC_MeterNormal": true,
              "DevFWVersion": "CoolingSystem LCT22002 1.0",
              "PumpDuty": "60",
              "FanDuty": "90",
              "LC_PumpCtrl": "1",
              "LC_FanCtrl": "3",
              "LCLED_R": "12",
              "LCLED_G": "34",
              "LCLED_B": "56",
              "LCLED_RMinimum": "1",
              "LCLED_RMaximum": "200",
              "LCLED_GMinimum": "2",
              "LCLED_GMaximum": "201",
              "LCLED_BMinimum": "3",
              "LCLED_BMaximum": "202",
              "LCLED_Mode": "1",
              "LCFanLED_Mode": "5",
              "DevMACString": "AA:BB:CC:DD:EE:FF",
              "DeviceMacList": ["AA:BB:CC:DD:EE:FF"]
            }
            """);

        Assert.True(hardware.LcStatusSeen);
        Assert.True(hardware.LcReportedConnected);
        Assert.True(hardware.LcGcuControllable);
        Assert.Equal("Connected", hardware.LcConnectString);
        Assert.True(hardware.LcAutoConnect);
        Assert.True(hardware.LcActionSupported);
        Assert.True(hardware.LcActionSupportReported);
        Assert.True(hardware.LcCoolingAutoSupported);
        Assert.True(hardware.LcMeterNormal);
        Assert.Equal(1, hardware.LcPumpControl);
        Assert.Equal(3, hardware.LcFanControl);
        Assert.Equal(12, hardware.LcLedRed);
        Assert.Equal(34, hardware.LcLedGreen);
        Assert.Equal(56, hardware.LcLedBlue);
        Assert.Equal(1, hardware.LcLedRedMinimum);
        Assert.Equal(200, hardware.LcLedRedMaximum);
        Assert.Equal(1, hardware.LcHeadLightMode);
        Assert.Equal(5, hardware.LcFanLightMode);
        Assert.True(hardware.LcLightingStatusSeen);
        Assert.True(hardware.LcFanLightingSupported);
        Assert.Equal("AA:BB:CC:DD:EE:FF", hardware.LcCurrentMac);
        Assert.True(hardware.LcStatusVersion > 0);
    }

    [Fact]
    public void GcuLiquidCoolingStatus_DoesNotTreatDevicePreparationAsControllable()
    {
        using var hardware = new MechrevoHw();

        hardware.HandleMessage("BT_LC/Status", """
            { "connected": true, "ConnectString": "DeviceIsReady", "DevFWVersion": "CoolingSystem LCT21001" }
            """);

        Assert.True(hardware.LcReportedConnected);
        Assert.False(hardware.LcConnected);
        Assert.False(hardware.LcGcuControllable);
        Assert.False(hardware.LcActionSupportReported);
        Assert.False(hardware.LcFanLightingSupported);
    }

    [Fact]
    public void GcuLiquidCoolingStatus_OlderPayloadCanUseConnectedStateWithoutAConnectedFlag()
    {
        using var hardware = new MechrevoHw();

        hardware.HandleMessage("BT_LC/Status", """
            { "ConnectString": "Connected", "PumpDuty": "45" }
            """);

        Assert.True(hardware.LcReportedConnected);
        Assert.True(hardware.LcGcuControllable);

        hardware.HandleMessage("BT_LC/Status", """
            { "ConnectString": "Disconnected" }
            """);

        Assert.False(hardware.LcReportedConnected);
        Assert.False(hardware.LcGcuControllable);

        hardware.HandleMessage("BT_LC/Status", """
            { "connected": false, "ConnectString": "Connected" }
            """);

        Assert.False(hardware.LcReportedConnected);
        Assert.False(hardware.LcGcuControllable);
    }

    [Fact]
    public void GcuLiquidCoolingStatus_PartialUpdatesDoNotEraseTheLastConnectionState()
    {
        using var hardware = new MechrevoHw();

        hardware.HandleMessage("BT_LC/Status", """
            { "connected": true, "ConnectString": "Connected", "LC_action": true }
            """);
        hardware.HandleMessage("BT_LC/Status", """
            { "LCLED_RMinimum": "10", "LCLED_RMaximum": "240" }
            """);

        Assert.True(hardware.LcConnectionStateReported);
        Assert.True(hardware.LcGcuControllable);
        Assert.Equal(10, hardware.LcLedRedMinimum);
    }

    [Fact]
    public void GcuLiquidCoolingStatus_ConnectedFlagCanRestoreAfterDisconnectedString()
    {
        using var hardware = new MechrevoHw();

        hardware.HandleMessage("BT_LC/Status", """
            { "ConnectString": "Disconnected" }
            """);
        hardware.HandleMessage("BT_LC/Status", """
            { "connected": true, "LC_action": true }
            """);

        Assert.True(hardware.LcConnected);
        Assert.True(hardware.LcGcuControllable);
        Assert.Equal("Connected", hardware.LcConnectString);
    }

    [Fact]
    public async Task UnsupportedActionInPartialStatusRemainsReadOnly()
    {
        var commands = new ConcurrentQueue<Dictionary<string, object>>();
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "BT_LC/Control")
                commands.Enqueue(new Dictionary<string, object>(Assert.IsAssignableFrom<IDictionary<string, object>>(payload)));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        hardware.HandleMessage("BT_LC/Status", """
            { "LC_action": false }
            """);
        var service = new MechrevoService(hardware);

        Assert.False(await service.SwitchLcPump(1));
        Assert.Empty(commands);
    }

    [Fact]
    public void GcuLiquidCoolingStatus_ExplicitlyUnsupportedActionsRemainReadOnly()
    {
        using var hardware = new MechrevoHw();

        hardware.HandleMessage("BT_LC/Status", """
            { "connected": true, "ConnectString": "Connected", "LC_action": false }
            """);

        Assert.True(hardware.LcConnected);
        Assert.True(hardware.LcActionSupportReported);
        Assert.False(hardware.LcGcuControllable);
    }

    [Fact]
    public void GcuLiquidCoolingStatus_ParsesCaseInsensitiveJsonStringMacLists()
    {
        using var hardware = new MechrevoHw();

        hardware.HandleMessage("BT_LC/Status", """
            { "devicemaclist": "[\"AA:BB:CC:DD:EE:FF\"]", "connectstring": "DeviceIsReady" }
            """);

        Assert.Single(hardware.LcDeviceMacs);
        Assert.Equal("AA:BB:CC:DD:EE:FF", hardware.LcDeviceMacs[0]);
    }

    [Fact]
    public void GcuLiquidCoolingStatus_RequiresTwoConnectedMeterFailuresBeforeReportingAFlowFault()
    {
        using var hardware = new MechrevoHw();
        const string fault = """
            { "connected": true, "ConnectString": "Connected", "PumpDuty": "60", "LC_MeterNormal": false }
            """;

        hardware.HandleMessage("BT_LC/Status", fault);
        Assert.False(hardware.LcMeterFaultConfirmed);

        hardware.HandleMessage("BT_LC/Status", fault);
        Assert.True(hardware.LcMeterFaultConfirmed);

        hardware.HandleMessage("BT_LC/Status", """
            { "connected": true, "ConnectString": "Connected", "PumpDuty": "60", "LC_MeterNormal": true }
            """);
        Assert.False(hardware.LcMeterFaultConfirmed);
    }

    [Theory]
    [InlineData(true, true, true, LiquidCoolingControlRoute.DirectBle)]
    [InlineData(false, true, true, LiquidCoolingControlRoute.Gcu)]
    [InlineData(false, false, true, LiquidCoolingControlRoute.BluetoothObserved)]
    [InlineData(false, false, false, LiquidCoolingControlRoute.None)]
    public void ConnectionRoute_PreservesTheExistingControlOwner(
        bool directBle, bool gcuControllable, bool bluetoothObserved, LiquidCoolingControlRoute expected)
    {
        Assert.Equal(expected, LiquidCoolingConnectionPolicy.ResolveRoute(
            directBle, gcuControllable, bluetoothObserved));
    }

    /// <summary>
    /// 本应用替代官方控制中心：GCU 通道拿不到控制权时必须自动回落我们自己的直连，
    /// 不得因为官方 GCU/服务在线或已报告状态而阻止回落。唯一不必回落的情形是已持有直连。
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AutomaticDirectFallback_RunsUnlessWeAlreadyOwnTheDirectRoute(
        bool directBleConnected, bool expected)
    {
        Assert.Equal(expected, LiquidCoolingConnectionPolicy.ShouldUseAutomaticDirectFallback(
            directBleConnected));
    }

    [Theory]
    [InlineData(true, true, LiquidCoolingControlRoute.None, false, true, 0, 8000)]
    [InlineData(true, true, LiquidCoolingControlRoute.BluetoothObserved, false, true, 0, 8000)]
    [InlineData(true, true, LiquidCoolingControlRoute.Gcu, false, true, 0, 8000)]
    [InlineData(true, true, LiquidCoolingControlRoute.None, true, false, 0, 8000)]
    [InlineData(true, false, LiquidCoolingControlRoute.None, false, true, 0, 8000)]
    [InlineData(true, true, LiquidCoolingControlRoute.None, false, true, 5, 8000)]
    [InlineData(true, true, LiquidCoolingControlRoute.None, false, true, 0, 7999)]
    public void GcuAutoConnectRetry_RequiresARecoverablePendingRoute(
        bool hardwareConnected,
        bool serviceAvailable,
        LiquidCoolingControlRoute route,
        bool actionSupportReported,
        bool actionSupported,
        int attempts,
        long sinceLastAttemptMs)
    {
        bool expected = (route is LiquidCoolingControlRoute.None or LiquidCoolingControlRoute.BluetoothObserved) &&
            hardwareConnected && serviceAvailable &&
            !(actionSupportReported && !actionSupported) &&
            attempts < 5 && sinceLastAttemptMs >= 8000;

        Assert.Equal(expected, LiquidCoolingConnectionPolicy.ShouldRetryGcuConnection(
            hardwareConnected, serviceAvailable, route,
            actionSupportReported, actionSupported, attempts, sinceLastAttemptMs));
    }

    [Theory]
    [InlineData(false, true, true, "cyan_static", true)]
    [InlineData(true, true, true, "cyan_static", false)]
    [InlineData(false, false, true, "cyan_static", false)]
    [InlineData(false, true, false, "cyan_static", false)]
    [InlineData(false, true, true, "", false)]
    public void SavedGcuLighting_RestoresOnlyForAControllableFreshGcuRoute(
        bool directBleConnected,
        bool gcuControllable,
        bool gcuStatusFresh,
        string savedProfile,
        bool expected)
    {
        Assert.Equal(expected, LiquidCoolingConnectionPolicy.ShouldRestoreSavedGcuLighting(
            directBleConnected, gcuControllable, gcuStatusFresh, savedProfile));
    }

    /// <summary>
    /// 泵速自动档：厂商协议没有自动档（客户端按温度挑一档下发），回读永远是具体档位，
    /// 因此自动意图必须压过回读，否则选完自动几秒后下拉又跳回具体档位。
    /// </summary>
    [Theory]
    [InlineData(WaterCoolerBle.ProfileAutomatic, 0, WaterCoolerBle.ProfileAutomatic)]
    [InlineData(WaterCoolerBle.ProfileAutomatic, 2, WaterCoolerBle.ProfileAutomatic)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 0, 0)]
    [InlineData(WaterCoolerBle.ProfileUnset, 2, 2)]
    public void PumpDisplayProfile_KeepsTheAutomaticIntentOverTheReportedGear(int intent, int reported, int expected)
    {
        Assert.Equal(expected, LiquidCoolingDisplayPolicy.PumpProfile(intent, reported));
    }

    /// <summary>
    /// 风扇自动档：GCU 回读 4 是厂商自动档，而 BLE 直连通道的「100%」在档位表里同样是 4，
    /// 照回读查表会把自动显示成 100%。
    /// </summary>
    [Theory]
    [InlineData(WaterCoolerBle.ProfileAutomatic, LiquidCoolingDisplayPolicy.GcuFanAutoIndex, WaterCoolerBle.ProfileAutomatic)]
    [InlineData(WaterCoolerBle.ProfileAutomatic, 1, WaterCoolerBle.ProfileAutomatic)]
    [InlineData(2, LiquidCoolingDisplayPolicy.GcuFanAutoIndex, WaterCoolerBle.ProfileAutomatic)]
    [InlineData(2, 2, 2)]
    [InlineData(LiquidCoolingDisplayPolicy.GcuFanAutoIndex, 3, 3)]
    public void FanDisplayProfile_MapsTheVendorAutoIndexToAutomatic(int intent, int reported, int expected)
    {
        Assert.Equal(expected, LiquidCoolingDisplayPolicy.FanProfile(intent, reported));
    }

    /// <summary>
    /// 自动泵速的下发判据：记忆与厂商回读必须同时命中目标档位才算「已应用」。
    /// 只比记忆会漏掉手动档改写设备的情形（实测：手动选低 45% 后切回自动，再没有任何下发，
    /// 泵永远停在手动档），这正是用户说的「切回自动没反应」。
    /// </summary>
    [Theory]
    [InlineData(1, 1, 1, true)]     // 记忆与设备都在目标档位 → 不必下发
    [InlineData(1, 1, 0, false)]    // 设备被手动档改写 → 必须重新下发
    [InlineData(1, -1, 0, false)]   // 记忆为空（首次/重连）→ 必须下发
    [InlineData(1, 1, -1, false)]   // 设备档位未知 → 宁可多发一次
    [InlineData(2, 1, 2, false)]    // 温度升档 → 必须下发
    public void AutomaticPumpGear_SkipsOnlyWhenMemoryAndDeviceAgree(
        int target, int remembered, int reported, bool expected)
    {
        Assert.Equal(expected, WaterCoolerBle.AutomaticGearAlreadyApplied(target, remembered, reported));
    }

    /// <summary>
    /// 端到端：上面两条规则落到真实档位下拉的项上（锁定项表本身——最高档是「最大」，
    /// 表里没有 100%，自动档只能靠意图/厂商自动索引解析到「自动」）。
    /// </summary>
    [Fact]
    public void LiquidCoolingCombos_ShowAutomaticForTheGcuAutomaticState()
    {
        using var form = new SettingsForm();
        ComboBox fan = form.Controls.Find("comboLcFan", true).OfType<ComboBox>().Single();
        ComboBox pump = form.Controls.Find("comboLcPump", true).OfType<ComboBox>().Single();

        Assert.Equal("自动", DisplayedProfile(fan, LiquidCoolingDisplayPolicy.FanProfile(
            WaterCoolerBle.ProfileAutomatic, LiquidCoolingDisplayPolicy.GcuFanAutoIndex)));
        Assert.Equal("自动", DisplayedProfile(pump, LiquidCoolingDisplayPolicy.PumpProfile(
            WaterCoolerBle.ProfileAutomatic, 2)));
        Assert.Equal("60%", DisplayedProfile(fan, LiquidCoolingDisplayPolicy.FanProfile(2, 2)));
        // 厂商协议没有 100% 档（风扇 4 档 40/50/60/90 、泵速 3 档 45/60/90），档位表里不该再有 100%：
        // 风扇的值 4 只能是 GCU 自动档标记，落到任何一个手动档上都是错的（曾经落到「100%」）。
        Assert.Equal("", DisplayedProfile(fan, LiquidCoolingDisplayPolicy.GcuFanAutoIndex));
        Assert.Equal("", DisplayedProfile(pump, 3));
        Assert.Equal("", DisplayedProfile(pump, 4));
        // 2026-09-13 用户要求：最高档显示「最大」而非 90%。
        Assert.Equal("最大", DisplayedProfile(fan, WaterCoolerBle.TopFanProfile));
        Assert.Equal("最大", DisplayedProfile(pump, WaterCoolerBle.TopPumpProfile));

        // 界面最高档与配置层接受的最高档必须是同一个：否则会出现「界面上没有这一档、
        // 旧配置却仍按它下发」（100% 就是这样被删掉的）。
        Assert.Equal(WaterCoolerBle.TopFanProfile, HighestGear(fan));
        Assert.Equal(WaterCoolerBle.TopPumpProfile, HighestGear(pump));
        Assert.Equal(WaterCoolerBle.ProfileUnset,
            WaterCoolerBle.NormalizeProfile(WaterCoolerBle.TopPumpProfile + 1, WaterCoolerBle.TopPumpProfile));
        Assert.Equal(WaterCoolerBle.ProfileUnset,
            WaterCoolerBle.NormalizeProfile(WaterCoolerBle.TopFanProfile + 1, WaterCoolerBle.TopFanProfile));
        Assert.Equal(WaterCoolerBle.ProfileAutomatic,
            WaterCoolerBle.NormalizeProfile(WaterCoolerBle.ProfileAutomatic, WaterCoolerBle.TopFanProfile));
    }

    static int HighestGear(ComboBox combo)
    {
        int highest = WaterCoolerBle.ProfileUnset;
        foreach (object item in combo.Items)
            if (item is KeyValuePair<string, int> pair && pair.Value > highest) highest = pair.Value;
        return highest;
    }

    static string DisplayedProfile(ComboBox combo, int profile)
    {
        foreach (object item in combo.Items)
            if (item is KeyValuePair<string, int> pair && pair.Value == profile) return pair.Key;
        return "";
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", "AABBCCDDEEFF", true)]
    [InlineData("aa-bb-cc-dd-ee-ff", "AABBCCDDEEFF", true)]
    [InlineData("AA:BB:CC:DD:EE:00", "AABBCCDDEEFF", false)]
    [InlineData("not-an-address", "AABBCCDDEEFF", false)]
    public void SystemBluetoothAddressMatcher_NormalizesOnlyValidAddresses(string address, string known, bool expected)
    {
        Assert.Equal(expected, WaterCoolerBle.IsKnownWaterCoolerAddress(address, [known]));
    }

    [Fact]
    public async Task OfficialRgbAndBreathingCommands_ArePublishedInTheOfficialOrder()
    {
        var commands = new ConcurrentQueue<Dictionary<string, object>>();
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "BT_LC/Control")
                commands.Enqueue(new Dictionary<string, object>(Assert.IsAssignableFrom<IDictionary<string, object>>(payload)));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        var service = new MechrevoService(hardware);

        Assert.True(await service.LcApplyLightProfile(
            WaterCoolerBle.LightCustomBreath, Color.FromArgb(12, 34, 56)));

        Dictionary<string, object>[] published = commands.ToArray();
        Assert.Collection(published,
            command =>
            {
                Assert.Equal("LEDColorful", command["Action"]);
                Assert.Equal(0, command["LedMode"]);
            },
            command =>
            {
                Assert.Equal("LEDControl", command["Action"]);
                Assert.Equal("12", command["LCLED_R"]);
                Assert.Equal("34", command["LCLED_G"]);
                Assert.Equal("56", command["LCLED_B"]);
            },
            command =>
            {
                Assert.Equal("LEDBreathing", command["Action"]);
                Assert.Equal(1, command["LedMode"]);
            });
    }

    [Fact]
    public async Task GcuLightProfile_IsRejectedWhenFreshReadbackStillShowsTheOldMode()
    {
        MechrevoHw hardware = null!;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "BT_LC/Control")
            {
                var values = Assert.IsAssignableFrom<IDictionary<string, object>>(payload);
                if (values.TryGetValue("Action", out object? action) && action?.ToString() == "GETSTATUS")
                    hardware.HandleMessage("BT_LC/Status", """
                        { "LCLED_Mode": 2, "LCFanLED_Mode": 0, "LCLED_R": 0, "LCLED_G": 255, "LCLED_B": 255 }
                        """);
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        using (hardware)
        {
            hardware.HandleMessage("BT_LC/Status", """
                { "connected": true, "ConnectString": "Connected", "LC_action": true,
                  "LCLED_Mode": 2, "LCFanLED_Mode": 0, "LCLED_R": 0, "LCLED_G": 255, "LCLED_B": 255 }
                """);
            var service = new MechrevoService(hardware);

            Assert.False(await service.LcApplyLightProfile(
                WaterCoolerBle.LightCyanStatic, Color.Empty));
        }
    }

    [Fact]
    public async Task GcuLightProfile_IsConfirmedByFreshModeAndColorReadback()
    {
        MechrevoHw hardware = null!;
        hardware = new MechrevoHw((topic, _) =>
        {
            if (topic == "BT_LC/Control")
                hardware.HandleMessage("BT_LC/Status", """
                    { "LCLED_Mode": 0, "LCFanLED_Mode": 0, "LCLED_R": 0, "LCLED_G": 255, "LCLED_B": 255 }
                    """);
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        using (hardware)
        {
            hardware.HandleMessage("BT_LC/Status", """
                { "connected": true, "ConnectString": "Connected", "LC_action": true,
                  "LCLED_Mode": 2, "LCFanLED_Mode": 0, "LCLED_R": 255, "LCLED_G": 0, "LCLED_B": 0 }
                """);
            var service = new MechrevoService(hardware);

            Assert.True(await service.LcApplyLightProfile(
                WaterCoolerBle.LightCyanStatic, Color.Empty));
        }
    }

    /// <summary>
    /// 从不回报 LED 字段的固件（如 LCT22002 v2.0.0.4）永远无法满足回读：这是「无法验证」而不是
    /// 「写入失败」。该设备类必须回落到只下发，并真的把灯效命令发出去；否则 GCU 路由会把它当成
    /// 终局失败并停止恢复（beta17 回归）。
    /// </summary>
    [Fact]
    public async Task GcuLightProfile_RequiredReadbackFallsBackToSendOnlyForAStatuslessFirmware()
    {
        var commands = new ConcurrentQueue<Dictionary<string, object>>();
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "BT_LC/Control")
                commands.Enqueue(new Dictionary<string, object>(Assert.IsAssignableFrom<IDictionary<string, object>>(payload)));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        hardware.HandleMessage("BT_LC/Status", """
            { "connected": true, "ConnectString": "Connected", "LC_action": true }
            """);
        var service = new MechrevoService(hardware);

        Assert.True(await service.LcApplyLightProfile(
            WaterCoolerBle.LightCyanStatic, Color.Empty, requireReadback: true));
        Assert.Contains(commands, command =>
            command.TryGetValue("Action", out object? action) && action?.ToString() == "LEDControl");
    }

    /// <summary>
    /// 会回报 LED 状态的固件不得被上面的只下发回落削弱：写后回读仍是旧档位就说明设备忽略了写入，
    /// 必须继续判失败——该回读存在的意义正是抓这种静默 no-op。
    /// </summary>
    [Fact]
    public async Task GcuLightProfile_RequiredReadbackStillRejectsAMismatchFromAReportingFirmware()
    {
        MechrevoHw hardware = null!;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "BT_LC/Control")
            {
                var values = Assert.IsAssignableFrom<IDictionary<string, object>>(payload);
                if (values.TryGetValue("Action", out object? action) && action?.ToString() == "GETSTATUS")
                    hardware.HandleMessage("BT_LC/Status", """
                        { "LCLED_Mode": 2, "LCFanLED_Mode": 0, "LCLED_R": 0, "LCLED_G": 255, "LCLED_B": 255 }
                        """);
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        using (hardware)
        {
            hardware.HandleMessage("BT_LC/Status", """
                { "connected": true, "ConnectString": "Connected", "LC_action": true,
                  "LCLED_Mode": 2, "LCFanLED_Mode": 0, "LCLED_R": 0, "LCLED_G": 255, "LCLED_B": 255 }
                """);
            var service = new MechrevoService(hardware);

            Assert.False(await service.LcApplyLightProfile(
                WaterCoolerBle.LightCyanStatic, Color.Empty, requireReadback: true));
        }
    }

    [Fact]
    public async Task OfficialColorfulBreathingAndFanLighting_UseSeparateOfficialCommands()
    {
        var commands = new ConcurrentQueue<Dictionary<string, object>>();
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "BT_LC/Control")
                commands.Enqueue(new Dictionary<string, object>(Assert.IsAssignableFrom<IDictionary<string, object>>(payload)));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        var service = new MechrevoService(hardware);

        Assert.True(await service.LcApplyLightProfile(WaterCoolerBle.LightColorfulBreath, Color.Empty));
        Assert.True(await service.LcApplyLightProfile(WaterCoolerBle.LightFanRainbow, Color.Empty));

        Dictionary<string, object>[] published = commands.ToArray();
        Assert.Collection(published,
            command =>
            {
                Assert.Equal("LEDColorful", command["Action"]);
                Assert.Equal(2, command["LedMode"]);
            },
            command =>
            {
                Assert.Equal("LEDBreathing", command["Action"]);
                Assert.Equal(1, command["LedMode"]);
            },
            command =>
            {
                Assert.Equal("FanLEDEffect", command["Action"]);
                Assert.Equal(5, command["LedMode"]);
            });
    }

    [Fact]
    public async Task StaticHeadLightProfile_ClearsDynamicHeadEffectsBeforeWritingTheColor()
    {
        var commands = new ConcurrentQueue<Dictionary<string, object>>();
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "BT_LC/Control")
                commands.Enqueue(new Dictionary<string, object>(Assert.IsAssignableFrom<IDictionary<string, object>>(payload)));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        var service = new MechrevoService(hardware);

        Assert.True(await service.LcApplyLightProfile(WaterCoolerBle.LightCyanStatic, Color.Empty));

        Assert.Collection(commands,
            command =>
            {
                Assert.Equal("LEDColorful", command["Action"]);
                Assert.Equal(0, command["LedMode"]);
            },
            command =>
            {
                Assert.Equal("LEDBreathing", command["Action"]);
                Assert.Equal(0, command["LedMode"]);
            },
            command =>
            {
                Assert.Equal("LEDControl", command["Action"]);
                Assert.Equal("0", command["LCLED_R"]);
                Assert.Equal("255", command["LCLED_G"]);
                Assert.Equal("255", command["LCLED_B"]);
        });
    }

    [Fact]
    public async Task GcuLightProfiles_KeepEachMultiCommandSequenceTogether()
    {
        var commands = new ConcurrentQueue<string?>();
        var firstProfileEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstProfile = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fanEffectPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int colorfulCommandCount = 0;
        using var hardware = new MechrevoHw(async (topic, payload) =>
        {
            if (topic != "BT_LC/Control") return;
            var values = Assert.IsAssignableFrom<IDictionary<string, object>>(payload);
            string? action = values.TryGetValue("Action", out object? value) ? value?.ToString() : null;
            commands.Enqueue(action);
            if (action == "LEDColorful" && Interlocked.Increment(ref colorfulCommandCount) == 1)
            {
                firstProfileEntered.TrySetResult();
                await releaseFirstProfile.Task;
            }
            if (action == "FanLEDEffect") fanEffectPublished.TrySetResult();
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        var service = new MechrevoService(hardware);

        Task<bool> staticProfile = service.LcApplyLightProfile(WaterCoolerBle.LightCyanStatic, Color.Empty);
        await firstProfileEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Task<bool> fanProfile = service.LcApplyLightProfile(WaterCoolerBle.LightFanRainbow, Color.Empty);

        try
        {
            Task completed = await Task.WhenAny(fanEffectPublished.Task, Task.Delay(150));
            Assert.NotSame(fanEffectPublished.Task, completed);
        }
        finally
        {
            releaseFirstProfile.TrySetResult();
        }

        Assert.True(await staticProfile);
        Assert.True(await fanProfile);
        Assert.Collection(commands.ToArray(),
            action => Assert.Equal("LEDColorful", action),
            action => Assert.Equal("LEDBreathing", action),
            action => Assert.Equal("LEDControl", action),
            action => Assert.Equal("FanLEDEffect", action));
    }

    [Fact]
    public async Task GcuColorCommand_ClampsToTheRuntimeReportedColorRange()
    {
        var commands = new ConcurrentQueue<Dictionary<string, object>>();
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "BT_LC/Control")
                commands.Enqueue(new Dictionary<string, object>(Assert.IsAssignableFrom<IDictionary<string, object>>(payload)));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        hardware.HandleMessage("BT_LC/Status", """
            {
              "LCLED_RMinimum": "10", "LCLED_RMaximum": "20",
              "LCLED_GMinimum": "30", "LCLED_GMaximum": "40",
              "LCLED_BMinimum": "50", "LCLED_BMaximum": "60"
            }
            """);
        var service = new MechrevoService(hardware);

        Assert.True(await service.LcSetLightColor(Color.FromArgb(12, 250, 200)));

        Dictionary<string, object> command = Assert.Single(commands);
        Assert.Equal("LEDControl", command["Action"]);
        Assert.Equal("12", command["LCLED_R"]);
        Assert.Equal("40", command["LCLED_G"]);
        Assert.Equal("60", command["LCLED_B"]);
    }

    [Fact]
    public async Task ExplicitlyUnsupportedGcuActionsDoNotPublishControlCommands()
    {
        var commands = new ConcurrentQueue<Dictionary<string, object>>();
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "BT_LC/Control")
                commands.Enqueue(new Dictionary<string, object>(Assert.IsAssignableFrom<IDictionary<string, object>>(payload)));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        hardware.HandleMessage("BT_LC/Status", """
            { "connected": true, "ConnectString": "Connected", "LC_action": false }
            """);
        var service = new MechrevoService(hardware);

        Assert.False(await service.SwitchLcPump(1));
        Assert.False(await service.LcApplyLightProfile(WaterCoolerBle.LightCyanStatic, Color.Empty));
        Assert.Empty(commands);
    }

    [Fact]
    public async Task SwitchLcPump_ConfirmsOnlyAfterFreshMatchingStatus()
    {
        var actions = new ConcurrentQueue<string?>();
        MechrevoHw hardware = null!;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic != "BT_LC/Control") return Task.CompletedTask;
            var values = Assert.IsAssignableFrom<IDictionary<string, object>>(payload);
            string? action = values.TryGetValue("Action", out object? value) ? value?.ToString() : null;
            actions.Enqueue(action);
            if (action == "GETSTATUS")
                hardware.HandleMessage("BT_LC/Status", LiquidCoolingStatus(pump: 1, fan: 0));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        using (hardware)
        {
            hardware.HandleMessage("BT_LC/Status", LiquidCoolingStatus(pump: 0, fan: 0));
            var service = new MechrevoService(hardware);

            Assert.True(await service.SwitchLcPump(1));
            Assert.Contains("LC_PumpCtrl", actions);
            Assert.Contains("GETSTATUS", actions);
        }
    }

    [Fact]
    public async Task SwitchLcPump_RejectsFreshMismatchedStatus()
    {
        var actions = new ConcurrentQueue<string?>();
        MechrevoHw hardware = null!;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic != "BT_LC/Control") return Task.CompletedTask;
            var values = Assert.IsAssignableFrom<IDictionary<string, object>>(payload);
            string? action = values.TryGetValue("Action", out object? value) ? value?.ToString() : null;
            actions.Enqueue(action);
            if (action == "GETSTATUS")
                hardware.HandleMessage("BT_LC/Status", LiquidCoolingStatus(pump: 0, fan: 0));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        using (hardware)
        {
            hardware.HandleMessage("BT_LC/Status", LiquidCoolingStatus(pump: 0, fan: 0));
            var service = new MechrevoService(hardware);

            Assert.False(await service.SwitchLcPump(1));
            Assert.Contains("GETSTATUS", actions);
        }
    }

    [Fact]
    public async Task SwitchLcPump_QueriesAgainAfterAnInFlightMismatchedStatus()
    {
        var actions = new ConcurrentQueue<string?>();
        MechrevoHw hardware = null!;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic != "BT_LC/Control") return Task.CompletedTask;
            var values = Assert.IsAssignableFrom<IDictionary<string, object>>(payload);
            string? action = values.TryGetValue("Action", out object? value) ? value?.ToString() : null;
            actions.Enqueue(action);
            if (action == "LC_PumpCtrl")
                hardware.HandleMessage("BT_LC/Status", LiquidCoolingStatus(pump: 0, fan: 0));
            else if (action == "GETSTATUS")
                hardware.HandleMessage("BT_LC/Status", LiquidCoolingStatus(pump: 1, fan: 0));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        using (hardware)
        {
            hardware.HandleMessage("BT_LC/Status", LiquidCoolingStatus(pump: 0, fan: 0));
            var service = new MechrevoService(hardware);

            Assert.True(await service.SwitchLcPump(1));
            Assert.Contains("GETSTATUS", actions);
        }
    }

    [Fact]
    public async Task SwitchLcFan_ConfirmsOnlyAfterFreshMatchingStatus()
    {
        var actions = new ConcurrentQueue<string?>();
        MechrevoHw hardware = null!;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic != "BT_LC/Control") return Task.CompletedTask;
            var values = Assert.IsAssignableFrom<IDictionary<string, object>>(payload);
            string? action = values.TryGetValue("Action", out object? value) ? value?.ToString() : null;
            actions.Enqueue(action);
            if (action == "GETSTATUS")
                hardware.HandleMessage("BT_LC/Status", LiquidCoolingStatus(pump: 0, fan: 3));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        using (hardware)
        {
            hardware.HandleMessage("BT_LC/Status", LiquidCoolingStatus(pump: 0, fan: 0));
            var service = new MechrevoService(hardware);

            Assert.True(await service.SwitchLcFan(3));
            Assert.Contains("LC_FanCtrl", actions);
            Assert.Contains("GETSTATUS", actions);
        }
    }

    [Fact]
    public async Task SwitchLcFanAuto_SendsVendorAutoIndexAndConfirmsFromStatus()
    {
        var actions = new ConcurrentQueue<string?>();
        string? fanCtrl = null;
        MechrevoHw hardware = null!;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic != "BT_LC/Control") return Task.CompletedTask;
            var values = Assert.IsAssignableFrom<IDictionary<string, object>>(payload);
            string? action = values.TryGetValue("Action", out object? value) ? value?.ToString() : null;
            actions.Enqueue(action);
            if (action == "LC_FanCtrl")
                fanCtrl = values.TryGetValue("FanCtrl", out object? ctrl) ? ctrl?.ToString() : null;
            // 实测行为：设备接受索引 4 后回读 LC_FanCtrl=4（且 LC_CoolingAuto=true）。
            if (action == "GETSTATUS" || action == "LC_FanCtrl")
                hardware.HandleMessage("BT_LC/Status", LiquidCoolingStatus(pump: 0, fan: 4));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        using (hardware)
        {
            hardware.HandleMessage("BT_LC/Status", LiquidCoolingStatus(pump: 0, fan: 3));
            var service = new MechrevoService(hardware);

            Assert.True(await service.SwitchLcFanAuto());
            Assert.Equal("4", fanCtrl);
            Assert.Contains("LC_FanCtrl", actions);
        }
    }

    [Fact]
    public async Task RefreshAll_RequestsLiquidCoolingStatus()
    {
        var commands = new ConcurrentQueue<(string Topic, string? Action)>();
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            var values = Assert.IsAssignableFrom<IDictionary<string, object>>(payload);
            commands.Enqueue((topic, values.TryGetValue("Action", out object? action) ? action?.ToString() : null));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = false });
        var service = new MechrevoService(hardware);

        await service.RefreshAll();

        Assert.Contains(("BT_LC/Control", "GETSTATUS"), commands);
    }

    [Fact]
    public async Task GcuConnectionRequest_AllowsRuntimeDiscoveryBeforeLiquidCoolingStatusArrives()
    {
        var commands = new ConcurrentQueue<(string Topic, string? Action)>();
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            var values = Assert.IsAssignableFrom<IDictionary<string, object>>(payload);
            commands.Enqueue((topic, values.TryGetValue("Action", out object? action) ? action?.ToString() : null));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = false });
        var service = new MechrevoService(hardware);

        Assert.True(await service.LcConnect());
        Assert.Contains(("BT_LC/Control", "Connect"), commands);
    }

    /// <summary>
    /// 官方 UI 的实测连接序列是「先选定目标设备再 Connect」：目标未选定（DevMACString 为空）时
    /// Connect 没有设备可连，状态永远停在 IsConnectable。选择必须是 GCU 状态里 DeviceMacList
    /// 的原样字符串（BluetoothLE#BluetoothLE&lt;addr&gt;-&lt;addr&gt;），不能换成裸 MAC。
    /// </summary>
    [Fact]
    public async Task GcuConnectionRequest_ArmsTheTargetDeviceFromTheStatusListBeforeConnecting()
    {
        const string mac = "BluetoothLE#BluetoothLEe4:4a:e0:48:16:d7-e8:35:36:c7:2e:c1";
        var commands = new ConcurrentQueue<Dictionary<string, object>>();
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "BT_LC/Control")
                commands.Enqueue(new Dictionary<string, object>(Assert.IsAssignableFrom<IDictionary<string, object>>(payload)));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        hardware.HandleMessage("BT_LC/Status", $$"""
            { "connected": false, "ConnectString": "IsConnectable", "LC_action": true,
              "DevMACString": "", "DeviceMacList": ["{{mac}}"] }
            """);
        var service = new MechrevoService(hardware);

        Assert.True(await service.LcConnect());

        Dictionary<string, object>[] published = commands.ToArray();
        Assert.Collection(published,
            command =>
            {
                Assert.Equal("DeviceMacSetting", command["Action"]);
                Assert.Equal(mac, command["DeviceMac"]);
            },
            command => Assert.Equal("Connect", command["Action"]));
    }

    /// <summary>选定目标后再 Connect 必须能到达可控制状态（厂商固件在 arm+connect 后才报告 Connected）。</summary>
    [Fact]
    public async Task GcuConnectionRequest_ReachesControllableAfterArmingAndConnecting()
    {
        const string mac = "BluetoothLE#BluetoothLEe4:4a:e0:48:16:d7-e8:35:36:c7:2e:c1";
        MechrevoHw hardware = null!;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic != "BT_LC/Control") return Task.CompletedTask;
            var values = Assert.IsAssignableFrom<IDictionary<string, object>>(payload);
            string? action = values.TryGetValue("Action", out object? value) ? value?.ToString() : null;
            if (action == "DeviceMacSetting")
                hardware.HandleMessage("BT_LC/Status", $$"""
                    { "connected": false, "ConnectString": "IsConnectable", "LC_action": true,
                      "DevMACString": "{{mac}}", "DeviceMacList": ["{{mac}}"] }
                    """);
            else if (action == "Connect")
                hardware.HandleMessage("BT_LC/Status", $$"""
                    { "connected": true, "ConnectString": "Connected", "LC_action": true,
                      "DevMACString": "{{mac}}", "DeviceMacList": ["{{mac}}"] }
                    """);
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        using (hardware)
        {
            hardware.HandleMessage("BT_LC/Status", $$"""
                { "connected": false, "ConnectString": "IsConnectable", "LC_action": true,
                  "DevMACString": "", "DeviceMacList": ["{{mac}}"] }
                """);
            var service = new MechrevoService(hardware);

            Assert.True(await service.LcConnect());

            Assert.Equal(mac, hardware.LcCurrentMac);
            Assert.True(hardware.LcGcuControllable);
        }
    }

    /// <summary>目标已选定（DevMACString 非空）时不再重复 arm，只发 Connect——重连不应改写用户已选的设备。</summary>
    [Fact]
    public async Task GcuConnectionRequest_DoesNotRearmWhenATargetIsAlreadySelected()
    {
        const string mac = "BluetoothLE#BluetoothLEe4:4a:e0:48:16:d7-e8:35:36:c7:2e:c1";
        var actions = new ConcurrentQueue<string?>();
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "BT_LC/Control")
            {
                var values = Assert.IsAssignableFrom<IDictionary<string, object>>(payload);
                actions.Enqueue(values.TryGetValue("Action", out object? value) ? value?.ToString() : null);
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { LiquidCooling = true });
        hardware.HandleMessage("BT_LC/Status", $$"""
            { "connected": false, "ConnectString": "IsConnectable", "LC_action": true,
              "DevMACString": "{{mac}}", "DeviceMacList": ["{{mac}}"] }
            """);
        var service = new MechrevoService(hardware);

        Assert.True(await service.LcConnect());

        Assert.Equal((IEnumerable<string?>)["Connect"], actions);
    }

    static string LiquidCoolingStatus(int pump, int fan) => $$"""
        {
          "connected": true,
          "ConnectString": "Connected",
          "LC_action": true,
          "LC_PumpCtrl": "{{pump}}",
          "LC_FanCtrl": "{{fan}}"
        }
        """;
}
