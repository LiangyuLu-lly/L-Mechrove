using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// HandleMessage 调度器（switch 路由 + 单 try）的逐主题表征测试。
///
/// 存在的理由：把每个 case 体抽成独立方法前后，行为必须逐字节等价。这组测试在
/// 抽取前对旧代码运行并全绿，抽取后必须仍然全绿——它们锁的是「哪个主题写哪些字段、
/// 哪些 *Seen 会置位、共享 case 标签如何路由、帧失败时通知如何收敛」。
///
/// 与既有测试的分工：PayloadParsingTests 锁解析语义，ProtocolFuzzTests 锁容器性，
/// StateFreshnessTests 锁版本号与事件顺序；这里补的是每个主题分支的端到端缓存状态。
/// </summary>
public class HandleMessageDispatcherCharacterizationTests
{
    static MechrevoHw NewHardware() =>
        new((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities());

    // ---------- System/* 遥测分支 ----------

    [Fact]
    public void SystemCpuInfo_ParsesAllReadings()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("System/CpuInfo",
            """{"CpuTemperature":55,"CpuUsage":12,"CpuFrequency":3200}""");

        Assert.Equal(55, hardware.CpuTemp);
        Assert.Equal(12, hardware.CpuUsage);
        Assert.Equal(3200, hardware.CpuFrequency);
    }

    [Fact]
    public void SystemGpuInfo_ParsesAllReadings()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("System/GpuInfo",
            """{"GpuTemperature":61,"GpuUsage":30,"GpuCoreFreq":2100,"GpuMem":1024}""");

        Assert.Equal(61, hardware.GpuTemp);
        Assert.Equal(30, hardware.GpuUsage);
        Assert.Equal(2100, hardware.GpuCoreFreq);
        Assert.Equal(1024, hardware.VramUsedMb);
    }

    [Fact]
    public void SystemMemoryInfo_KeepsLastUsedBytesWhenFieldIsAbsent()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("System/MemoryInfo", """{"MemoryUsage":45,"TotalUsingMemory":9.6}""");
        Assert.Equal(45, hardware.RamUsage);
        Assert.Equal(9.6, hardware.RamUsedGb, 3);

        // TotalUsingMemory 缺失时保留上一次的值（Double 的 fallback 语义）。
        hardware.HandleMessage("System/MemoryInfo", """{"MemoryUsage":50}""");
        Assert.Equal(50, hardware.RamUsage);
        Assert.Equal(9.6, hardware.RamUsedGb, 3);
    }

    [Fact]
    public void SystemFanInfo_ParsesBothFansDutyAndRpm()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("System/FanInfo",
            """{"CpuFanDuty":25,"GpuFanDuty":25,"CpuFanRpm":1509,"GpuFanRpm":1746}""");

        Assert.Equal(25, hardware.CpuFanDuty);
        Assert.Equal(25, hardware.GpuFanDuty);
        Assert.Equal(1509, hardware.CpuFanRpm);
        Assert.Equal(1746, hardware.GpuFanRpm);
    }

    [Fact]
    public void SystemBatteryInfo_ParsesPercentCyclesCapacityAndAbnormal()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("System/BatteryInfo", """
            {"BatteryLifePercent":"89","BatteryAbnormal":"1",
             "BatteryCapacity":"57 Wh","BatteryCycleCount":"42"}
            """);

        Assert.Equal(89, hardware.BatteryPercent);
        Assert.Equal(42, hardware.BatteryCycleCount);
        Assert.Equal("57 Wh", hardware.BatteryCapacityText);
        Assert.True(hardware.BatteryAbnormal);
    }

    [Fact]
    public void SystemNetworkInfo_ParsesBothDirectionsAndMarksSeen()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("System/NetworkInfo",
            """{"NetworkDownload":"232 Kbps","NetworkUpload":"3.9 Mbps"}""");

        Assert.True(hardware.NetworkInfoSeen);
        Assert.Equal("232 Kbps", hardware.NetworkDownload);
        Assert.Equal("3.9 Mbps", hardware.NetworkUpload);
    }

    [Fact]
    public void SystemHardwareInfo_ParsesEcVersionAndMarksSeen()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("System/HardwareInfo", """{"ECVersion":"N.1.32MRO60"}""");

        Assert.True(hardware.HardwareInfoSeen);
        Assert.Equal("N.1.32MRO60", hardware.EcFirmwareVersion);
    }

    [Fact]
    public void SystemFanErrorInfo_FlagsErrorAndClearsOnHealthyFrame()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("System/FanErrorInfo", """{"CpuFanError":1,"GpuFanError":0}""");
        Assert.True(hardware.FanErrorSeen);
        Assert.True(hardware.FanError);

        hardware.HandleMessage("System/FanErrorInfo", """{"CpuFanError":0,"GpuFanError":0}""");
        Assert.False(hardware.FanError);
    }

    // ---------- 共享 case 标签：HidLightbar/Status + HidLightbar_Logo/Status ----------

    [Fact]
    public void SharedLightbarArm_RoutesLightbarStatusToLightbarStateOnly()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("HidLightbar/Status",
            """{"type":"1","powerStatus":"On","brightNess":50,"LogoSupport":true,"BaseSupport":false}""");

        Assert.True(hardware.LightbarStatusSeen);
        Assert.False(hardware.LogoLightStatusSeen);
        Assert.True(hardware.QuickSwitches["lightbar"]);
        Assert.False(hardware.QuickSwitches.ContainsKey("logolight"));
        // 能力位只在主灯带臂里解析。
        Assert.True(hardware.LightbarLogoSupport);
        Assert.False(hardware.LightbarBaseSupport);
    }

    [Fact]
    public void SharedLightbarArm_RoutesLogoStatusToLogoStateOnly()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("HidLightbar_Logo/Status",
            """{"type":"2","powerStatus":"Off","brightNess":30,"LogoSupport":true}""");

        Assert.True(hardware.LogoLightStatusSeen);
        Assert.False(hardware.LightbarStatusSeen);
        Assert.False(hardware.QuickSwitches["logolight"]);
        Assert.False(hardware.QuickSwitches.ContainsKey("lightbar"));
        // Logo 臂不写能力位——即便载荷里带着 LogoSupport。
        Assert.Null(hardware.LightbarLogoSupport);
    }

    [Fact]
    public void SharedLightbarArm_EmptyStatusDoesNotClaimHardware()
    {
        using var hardware = NewHardware();

        // 开发机实测过的「不存在的灯带也推空状态」：type / powerStatus / brightNess 全空。
        hardware.HandleMessage("HidLightbar/Status",
            """{"type":"","powerStatus":"","brightNess":""}""");

        Assert.False(hardware.LightbarStatusSeen);
        // 空串 powerStatus 仍然不是 null，所以开关回显会被写成 false——这是当前行为，
        // 抽取后必须原样保留。
        Assert.False(hardware.QuickSwitches["lightbar"]);
    }

    // ---------- Keyboard/Status ----------

    [Fact]
    public void KeyboardStatus_ParsesAllFieldsAndBumpsVersionOnce()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("Keyboard/Status",
            """{"effect":"Rainbow","light":"3","brightNess":"62.5","speed":"3","direction":"L2R","powerStatus":"On"}""");

        Assert.Equal("Rainbow", hardware.KeyboardEffect);
        Assert.Equal(3, hardware.KeyboardLight);
        Assert.Equal(62, hardware.KeyboardBrightness);
        Assert.Equal(3, hardware.KeyboardSpeed);
        Assert.Equal("L2R", hardware.KeyboardDirection);
        Assert.True(hardware.KeyboardPower);
        Assert.Equal(1, hardware.KeyboardStatusVersion);
    }

    // ---------- BT_LC/Status ----------

    [Fact]
    public void BtLcStatus_ParsesConnectionPumpFanAndFirmware()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("BT_LC/Status", """
            {"connected":true,"ConnectString":"Connected","AutoConnect":true,"LC_action":true,
             "LC_CoolingAuto":true,"LC_MeterNormal":true,"PumpDuty":60,"FanDuty":50,
             "LC_PumpCtrl":1,"LC_FanCtrl":1,"DevFWVersion":"1.0","DevMACString":"AA:BB:CC:DD:EE:FF"}
            """);

        Assert.True(hardware.LcStatusSeen);
        Assert.True(hardware.LcReportedConnected);
        Assert.True(hardware.LcConnected);
        Assert.Equal("Connected", hardware.LcConnectString);
        Assert.True(hardware.LcAutoConnect);
        Assert.True(hardware.LcActionSupported);
        Assert.True(hardware.LcCoolingAutoSupported);
        Assert.Equal(60, hardware.LcPumpDuty);
        Assert.Equal(50, hardware.LcFanDuty);
        Assert.Equal(1, hardware.LcPumpControl);
        Assert.Equal(1, hardware.LcFanControl);
        Assert.Equal("1.0", hardware.LcFwVersion);
        Assert.Equal("AA:BB:CC:DD:EE:FF", hardware.LcCurrentMac);
        Assert.Equal(1, hardware.LcStatusVersion);
    }

    [Fact]
    public void BtLcStatus_MeterFaultConfirmsAfterTwoBadFramesAndClearsOnGoodFrame()
    {
        using var hardware = NewHardware();
        const string badMeter = """
            {"connected":true,"ConnectString":"Connected","LC_action":true,"PumpDuty":60,"LC_MeterNormal":false}
            """;

        hardware.HandleMessage("BT_LC/Status", badMeter);
        Assert.False(hardware.LcMeterFaultConfirmed);

        hardware.HandleMessage("BT_LC/Status", badMeter);
        Assert.True(hardware.LcMeterFaultConfirmed);

        hardware.HandleMessage("BT_LC/Status",
            """{"connected":true,"ConnectString":"Connected","LC_action":true,"PumpDuty":60,"LC_MeterNormal":true}""");
        Assert.False(hardware.LcMeterFaultConfirmed);
    }

    // ---------- System/BatteryProtection ----------

    [Fact]
    public void SystemBatteryProtection_UpdatesOnlyOnNonNegativeValues()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("System/BatteryProtection", """{"HealthProtectionStatus":2}""");
        Assert.Equal(2, hardware.BatteryProtection);

        // Int() 对无法解析的值返回 -1，负值不得覆盖已知档位。
        hardware.HandleMessage("System/BatteryProtection", """{"HealthProtectionStatus":"HEALTHYMODE"}""");
        Assert.Equal(2, hardware.BatteryProtection);
    }

    // ---------- Fan/Table ----------

    [Fact]
    public void FanTable_PublishesNameRespectiveFlagAndCurve()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("Fan/Table", """
            {"Name":"M1T1","FanControlRespective":false,
             "CPU":[{"UpT":40,"Duty":30}],"GPU":[{"UpT":50,"Duty":40}]}
            """);

        Assert.Equal("M1T1", hardware.CurveName);
        Assert.True(hardware.FanRespectiveSeen);
        Assert.False(hardware.FanRespective);
        Assert.Equal(40, hardware.CpuCurveUpT[0]);
        Assert.Equal(30, hardware.CpuCurveDuty[0]);
        Assert.Equal(40, hardware.GpuCurveDuty[0]);
    }

    // ---------- GPUDevice/Status ----------

    [Fact]
    public void GpuDeviceStatus_ParsesRefreshListSaveModeAndDcHz()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("GPUDevice/Status",
            """{"currentHZList":[144,60,240,60],"currentHZ":144,"currentSaveingMode":1,"DC_HZ":"ON"}""");

        Assert.Equal(new[] { 240, 144, 60 }, hardware.HzList);
        Assert.Equal(144, hardware.CurrentHz);
        Assert.Equal(1, hardware.GpuSaveMode);
        Assert.True(hardware.DcHzSeen);
        Assert.True(hardware.DcHz);
    }

    // ---------- Settings/DeviceSwitchItemStatus ----------

    [Fact]
    public void SettingsDeviceSwitchItemStatus_PinsAllFiveSwitchesAndSeenFlags()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("Settings/DeviceSwitchItemStatus", """
            {"ScreenBrightness":80,"TochpadEnable":true,"WIFIEnable":true,"BTEnable":false,"WebCamEnable":true}
            """);

        Assert.True(hardware.DeviceSwitchStatusSeen);
        Assert.True(hardware.ScreenBrightnessSeen);
        Assert.Equal(80, hardware.ScreenBrightness);
        Assert.True(hardware.TouchpadSeen);
        Assert.True(hardware.QuickSwitches["touchpad"]);
        Assert.True(hardware.WifiSeen);
        Assert.True(hardware.QuickSwitches["wifi"]);
        Assert.True(hardware.BluetoothSeen);
        Assert.False(hardware.QuickSwitches["bt"]);
        Assert.True(hardware.WebcamSeen);
        Assert.True(hardware.QuickSwitches["webcam"]);
    }

    // ---------- Setting/Status ----------

    [Fact]
    public void SettingStatus_PinsQuickSwitchFamilyAndCloseTimer()
    {
        using var hardware = NewHardware();
        var stateTopics = new List<string>();
        hardware.StateChanged += stateTopics.Add;

        hardware.HandleMessage("Setting/Status", """
            {"UsbCharger":"USB_CHARGER_STATUS_ON","OSD":"OSD_HIDDEN_ON",
             "WinKey":"WINKEY_LOCK","FnKey":"FNKEY_UNLOCK","NumPad":"NUMPAD_UNLOCK",
             "PowerLightSwitch":"PowerLight_ON","PowerLightBrightness":50,
             "HighPerformancePowerModeSwitch":"HIGH_PERFORMANCE_ON",
             "AcRecoverySwitch_Status":"AC_RECOVERY_OFF","DeepSleepSwitch":"DEEPSLEEP_ON",
             "CopilotKey":"COPILOTKEY_UNLOCK","CloseTimer":30}
            """);

        Assert.True(hardware.UsbCharger);
        Assert.True(hardware.UsbChargerSeen);
        Assert.True(hardware.OsdSeen);
        Assert.False(hardware.QuickSwitches["osd"]);           // 含 OFF 的判定按官方逐字对齐
        Assert.True(hardware.WinKeySeen);
        Assert.True(hardware.QuickSwitches["winkey"]);         // 勾选=锁定
        Assert.True(hardware.FnKeySeen);
        Assert.False(hardware.QuickSwitches["fnkey"]);
        Assert.True(hardware.NumpadSeen);
        Assert.False(hardware.QuickSwitches["numpad"]);
        Assert.True(hardware.PowerLightSeen);
        Assert.True(hardware.QuickSwitches["powerlight"]);
        Assert.Equal(50, hardware.PowerLightBrightness);
        Assert.True(hardware.QuickSwitches["highperf"]);
        Assert.False(hardware.QuickSwitches["acrecovery"]);
        Assert.True(hardware.QuickSwitches["deepsleep"]);
        Assert.False(hardware.QuickSwitches["copilot"]);
        Assert.Equal(30, hardware.CloseTimerMinutes);
        Assert.Equal(1, hardware.SettingStatusVersion);
        Assert.Equal(["Setting/Status"], stateTopics);
    }

    [Fact]
    public void SettingStatus_DisplayColorFieldsAreRecorded()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("Setting/Status", """
            {"DisplayMode":"sRGB","DisplayFeatureStatus":"DISPLAY_FEATURE_OFF",
             "DGpu":"NVIDIA","GamingBrightness":80}
            """);

        Assert.Equal("sRGB", hardware.DisplayColorMode);
        Assert.False(hardware.DisplayFeatureOn);
        Assert.Equal("NVIDIA", hardware.NvControlPanelPreference);
    }

    // ---------- Fan/Status ----------

    [Fact]
    public void FanStatus_PinsPowerLimitsTccTgpPl4AndOffsets()
    {
        using var hardware = NewHardware();

        hardware.HandleMessage("Fan/Status", """
            {"IsAC":true,"OperatingMode":2,"CPU_PL1":45,"CPU_PL2":65,
             "CPU_TccOffset":15,"CPU_TccOffsetSwitch":"1","TjMax":100,
             "GPU_ConfigurableTGPTarget":140,"GPU_CoreClockOffsetOC":150,
             "GPU_MemoryClockOffsetOC":200,"OverClockingSwitch":"1",
             "FanControlRespective":true,"CPU_PL4":50,"CPU_PL4_Double_Flag":"1"}
            """);

        Assert.Equal(45, hardware.Pl1);
        Assert.Equal(65, hardware.Pl2);
        Assert.True(hardware.TccSwitch);
        Assert.Equal(100, hardware.TjMax);
        Assert.Equal(140, hardware.GpuTgp);
        Assert.Equal(150, hardware.GpuCoreClockOffset);
        Assert.Equal(200, hardware.GpuMemClockOffset);
        Assert.True(hardware.OcSwitch);
        Assert.True(hardware.FanRespectiveSeen);
        Assert.True(hardware.FanRespective);
        Assert.True(hardware.Pl4Double);
        Assert.Equal(100, hardware.Pl4);   // Double_Flag 时线上值 ×2
        Assert.Equal(1, hardware.FanStatusVersion);
    }

    // ---------- LCHWOC/Status ----------

    [Fact]
    public void LchwocStatus_PinsSupportEnableOffsetsAndRaisesCustomModeChanged()
    {
        using var hardware = NewHardware();
        int customModeChanged = 0;
        hardware.CustomModeChanged += () => customModeChanged++;

        hardware.HandleMessage("LCHWOC/Status", """
            {"Support":true,"Enable":true,"GPU_CoreClockOffsetOC":150,
             "GPU_MemoryClockOffsetOC":200,"OverClockingSwitch":"1"}
            """);

        Assert.True(hardware.LchwocSupportReported);
        Assert.True(hardware.LchwocSupport);
        Assert.True(hardware.LchwocEnable);

        // Support:false 也必须被当作「已上报的 false」写进去，而不是被忽略。
        hardware.HandleMessage("LCHWOC/Status", """{"Support":false}""");
        Assert.False(hardware.LchwocSupportReported);
        Assert.False(hardware.LchwocSupport);

        Assert.Equal(150, hardware.GpuCoreClockOffset);
        Assert.Equal(200, hardware.GpuMemClockOffset);
        Assert.True(hardware.OcSwitch);
        Assert.Equal(2, customModeChanged);   // 每帧 LCHWOC/Status 各通知一次
    }

    // ---------- 单 try 契约：坏帧的收敛方式 ----------

    /// <summary>
    /// 解析失败（JObject.Parse 抛异常）时本帧既不写字段、也不发任何通知——这正是
    /// 「整个 switch 外面只有一个 try」的可观测后果。若未来有人把 Handler 拆成
    /// 每个都自带 try/catch，或者把通知挪出 try，这条测试会失败。
    /// </summary>
    [Fact]
    public void MalformedJsonSuppressesNotificationsAndStateForThatFrameOnly()
    {
        using var hardware = NewHardware();
        int stateChanges = 0;
        int dataChanges = 0;
        hardware.StateChanged += _ => stateChanges++;
        hardware.DataChanged += () => dataChanges++;

        hardware.HandleMessage("Fan/Status", """{"OperatingMode":1}""");
        Assert.Equal(1, stateChanges);
        Assert.Equal(0, dataChanges);

        hardware.HandleMessage("Fan/Status", "{ not json");
        Assert.Equal(1, stateChanges);          // 坏帧不发通知
        Assert.Equal(1, hardware.OperatingMode); // 也不写字段

        hardware.HandleMessage("Fan/Status", """{"OperatingMode":2}""");
        Assert.Equal(2, stateChanges);          // 接收器仍然活着
        Assert.Equal(2, hardware.OperatingMode);
    }

    [Fact]
    public void MalformedPayloadOnOneTopicLeavesOtherTopicsIntact()
    {
        using var hardware = NewHardware();
        hardware.HandleMessage("System/CpuInfo", """{"CpuTemperature":55}""");
        hardware.HandleMessage("Fan/Status", """{"OperatingMode":2}""");

        hardware.HandleMessage("Setting/Status", "{ broken");

        Assert.Equal(55, hardware.CpuTemp);
        Assert.Equal(2, hardware.OperatingMode);
        Assert.Equal(0, hardware.SettingStatusVersion);

        hardware.HandleMessage("Setting/Status", """{"UsbCharger":"USB_CHARGER_STATUS_ON"}""");
        Assert.True(hardware.UsbCharger);
        Assert.Equal(1, hardware.SettingStatusVersion);
    }

    /// <summary>
    /// RawMessageObserved 是旁路观察点，订阅者抛异常不得影响解析路径——
    /// 这是 HandleMessage 里位于主 try 之外的那个独立 try/catch 的契约。
    /// </summary>
    [Fact]
    public void RawObserverSubscriberFailureDoesNotAbortParsing()
    {
        using var hardware = NewHardware();
        int stateChanges = 0;
        hardware.RawMessageObserved += (_, _) => throw new InvalidOperationException("观察者故意抛异常");
        hardware.StateChanged += _ => stateChanges++;

        hardware.HandleMessage("Fan/Status", """{"OperatingMode":3}""");

        Assert.Equal(3, hardware.OperatingMode);
        Assert.Equal(1, stateChanges);
        Assert.Equal(1, hardware.FanStatusVersion);
    }
}
