using MechrevoLite.Hardware;
using System.Drawing;

namespace MechrevoLite.Tests;

/// <summary>
/// 灯光通道识别（与官方 GCU 服务 / CCUWinUI 同一口径）与键盘软件 → 官方回退策略。
/// 出处见 docs/hardware/lighting-channels.md；真机样本取自本机（耀世 16 Ultra，BIOS IDY）。
/// </summary>
public class LightingChannelDetectionTests
{
    static MechrevoHw NewHardware() => new(null, new MechrevoDeviceCapabilities());

    // ---- 本机真机抓包（2026 真机，UWPClient_3 只读 GETSTATUS）----
    const string RealKeyboardStatus = """{"solution":"ITE","type":"MEZone_3p1nd_101","brightNess":"4","powerStatus":"Off","LogoSupport":false,"HingeSupport":false,"BaseSupport":false,"NewlogoSupport":false,"MBlogoSupport":false,"currentPowerMode":"AC"}""";
    const string RealLightbarStatus = """{"solution":"ITE","type":"MEZone_Lighbar4","brightNess":"1","powerStatus":"Off","LogoSupport":false,"HingeSupport":false,"BaseSupport":false,"NewlogoSupport":false,"MBlogoSupport":true,"currentPowerMode":"AC"}""";
    const string RealLogoStatus = """{"solution":"ITE","type":"MEZone_Lighbar4","brightNess":"4","powerStatus":"Off","welcomeStatus":null,"welcomeDirectionStatus":"LeftRight","currentPowerMode":"AC"}""";
    const string RealHingeStatus = """{"solution":"ITE","type":"MEZone_Lighbar4","brightNess":"0","powerStatus":"Off","LogoSupport":false,"HingeSupport":false,"BaseSupport":false,"currentPowerMode":"None"}""";
    const string RealSyncStatus = """{"solution":null,"type":null,"brightNess":null,"powerStatus":null,"currentPowerMode":null}""";
    const string RealCustomizeInfo = """{"LightbarType":"3","KeyboardType":"1","LEDLightbarColorful":"1","LEDLightbarBreathEffect":"1","ProjectID":"26"}""";
    const string RealSupportInfo = """{"CustomizeTarget":1,"ProjectID":26,"ServiceReady":1,"LightbarSupport":1,"RGBLightbarSupport":1,"APVersionCheck":24,"BIOS_PROJECT_ID":"IDY"}""";

    [Fact]
    public void RealMachineEvidence_DetectsPerKeyKeyboardMainLightbarAndALogoOnly()
    {
        using MechrevoHw hw = NewHardware();
        foreach (var (topic, payload) in new[]
        {
            ("Keyboard/Status", RealKeyboardStatus), ("HidLightbar/Status", RealLightbarStatus),
            ("HidLightbar_Logo/Status", RealLogoStatus), ("HidLightbar_Hinge/Status", RealHingeStatus),
            ("HidLightbar_Sync/Status", RealSyncStatus), ("Customize/Info", RealCustomizeInfo),
            ("Customize/SupportInfo", RealSupportInfo),
        })
            hw.HandleMessage(topic, payload);

        LightingChannelSet set = hw.Lighting;
        Assert.Equal(KeyboardLightKind.PerKey, set.Keyboard);
        Assert.Equal(LightbarGeneration.Lighbar4, set.LightbarGeneration);
        Assert.True(set.MainLightbar);
        // MBlogoSupport=true（1.2 版服务字段）→ A 面 Logo：单色/呼吸/混合。
        Assert.Equal(LogoFlavor.MbaLogo, set.Logo);
        // 服务对每台 Lighbar4 都回铰链状态壳（type 有值），但 IDY 不做 A2 探测、HingeSupport=false → 没有铰链灯珠。
        Assert.False(set.Hinge);
        Assert.False(set.Sync);
        Assert.False(set.EcLightbar);   // RGBLightbarSupport=1 是陈旧位：没有 MyRgbLightbar/Status 且有 HID 灯条
        Assert.True(hw.SupportsKeyboard);
        Assert.True(hw.SupportsLightbar);
        Assert.True(hw.SupportsLogoLight);
        Assert.False(hw.SupportsHingeLight);
        Assert.False(hw.SupportsSyncLight);
        Assert.False(hw.SupportsEcLightbar);
        Assert.Equal("IDY", hw.BiosProjectId);
    }

    [Theory]
    [InlineData("MEZone_3p1nd_101", "PerKey")]
    [InlineData("MEZone_2nd_102", "PerKey")]
    [InlineData("MEZone_2p1nd_85", "PerKey")]
    [InlineData("MEZone_2p2nd_99", "PerKey")]
    [InlineData("MEZone_3nd_98", "PerKey")]
    [InlineData("FourZone", "FourZone")]
    [InlineData("FourZoneSingleColor", "FourZoneSingleColor")]
    [InlineData("SingleZone", "SingleZone")]
    [InlineData("MEZone_1st", "PerKeyLegacy")]
    public void KeyboardStatusType_MapsToOfficialKind(string type, string expected) =>
        Assert.Equal(Enum.Parse<KeyboardLightKind>(expected), LightingChannelDetector.DetectKeyboard(new LightingEvidence { KeyboardStatusType = type }));

    [Fact]
    public void KeyboardStatusNormal_WithoutHidKeyboard_IsNone()
    {
        var evidence = new LightingEvidence
        {
            KeyboardStatusType = "Normal",
            HidKeyboard = HidKeyboardInterfaceKind.None,
        };
        Assert.Equal(KeyboardLightKind.None, LightingChannelDetector.DetectKeyboard(evidence));
        Assert.False(LightingChannelDetector.Detect(evidence).KeyboardPresent);
    }

    [Fact]
    public void NoEvidenceAtAll_IsUnknownAndHidden()
    {
        LightingChannelSet set = LightingChannelDetector.Detect(new LightingEvidence());
        Assert.Equal(KeyboardLightKind.Unknown, set.Keyboard);
        Assert.False(set.KeyboardPresent);
        Assert.False(set.MainLightbar);
        Assert.False(set.LogoPresent);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "SingleZone")]
    [InlineData(2, "FourZone")]
    [InlineData(3, "FourZoneSingleColor")]
    [InlineData(4, "PerKeyLegacy")]
    [InlineData(7, "PerKey")]
    [InlineData(22, "PerKey")]
    [InlineData(25, null)]   // MEZone_Lighbar4 不是键盘
    public void RegistryKeyboardTypeOrdinal_FollowsOfficialEnumOrder(int ordinal, string? expected) =>
        Assert.Equal(expected is null ? null : Enum.Parse<KeyboardLightKind>(expected),
            LightingChannelDetector.KindFromTypeName(LightingChannelDetector.TypeNameFromOrdinal(ordinal)));

    [Fact]
    public void CustomizeKeyboardType2_IsSingleColorBacklight_UnlessRgbTypeReported()
    {
        Assert.Equal(KeyboardLightKind.SingleColorBacklight, LightingChannelDetector.DetectKeyboard(
            new LightingEvidence { KeyboardStatusType = "Normal", CustomizeKeyboardType = "2" }));
        Assert.Equal(KeyboardLightKind.SingleZone, LightingChannelDetector.DetectKeyboard(
            new LightingEvidence { KeyboardStatusType = "SingleZone", CustomizeKeyboardType = "2" }));
    }

    [Theory]
    [InlineData(0x600B, 1, 0xFF03, "PerKey")]
    [InlineData(0x6004, 1, 0xFF12, "FourZone")]
    [InlineData(0xCE00, 1, 0xFF02, "PerKeyLegacy")]
    [InlineData(0x600B, 0x10, 0xFF89, "None")]   // 本机 MI_00 固件接口
    [InlineData(0x7001, 2, 0xFF03, "None")]      // 本机灯条接口
    [InlineData(0x600B, 1, 0xFF55, "None")]      // 官方遇到未知 UsagePage 判无键盘
    public void HidKeyboardInterface_FollowsOfficialPidUsageAndUsagePage(int pid, int usage, int usagePage, string expected) =>
        Assert.Equal(Enum.Parse<HidKeyboardInterfaceKind>(expected), LightingChannelDetector.ClassifyKeyboardInterface(0x048D, (ushort)pid, (ushort)usage, (ushort)usagePage));

    [Fact]
    public void HidScanAlone_IdentifiesKeyboardWhenServiceIsAbsent()
    {
        Assert.Equal(KeyboardLightKind.PerKey,
            LightingChannelDetector.DetectKeyboard(new LightingEvidence { HidKeyboard = HidKeyboardInterfaceKind.PerKey }));
        Assert.Equal(KeyboardLightKind.FourZone,
            LightingChannelDetector.DetectKeyboard(new LightingEvidence { HidKeyboard = HidKeyboardInterfaceKind.FourZone }));
    }

    // ---- 软件路径候选：绝不把灯条 / 四区接口当逐键键盘 ----

    [Fact]
    public void SoftwareCandidate_RejectsRealLightbarInterface()
    {
        // 本机 048D:7001 MI_01（FF03/Usage 2/65/9）与键盘接口描述符几乎一样，旧评分给了 34 分。
        Assert.Equal(0, KeyboardRgb.ScoreCandidate(0x7001, 0xFF03, 2, 1, 65, 9));
        Assert.Equal(0, KeyboardRgb.ScoreCandidate(0x7001, 0xFF03, 1, 65, 9));
    }

    [Fact]
    public void SoftwareCandidate_RejectsFourZoneController()
    {
        Assert.Equal(0, KeyboardRgb.ScoreCandidate(0x6004, 0xFF12, 1, 1, 65, 9));
    }

    [Fact]
    public void SoftwareCandidate_OfficialKeyboardPidBeatsUnknownCompatibleInterface()
    {
        int official = KeyboardRgb.ScoreCandidate(0x6004, 0xFF03, 1, 1, 65, 9);
        int generic = KeyboardRgb.ScoreCandidate(0x7002, 0xFF03, 1, 1, 65, 9);
        Assert.True(official > generic);
        Assert.True(KeyboardRgb.ScoreCandidate(0x600B, 0xFF03, 1, 1, 65, 9) > 0);   // 本机键盘接口
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x88, 0x02, 0x33, 0x00, 0x32, 0x00, 0x00, 0x00 }, true)]    // 本机真机回读
    [InlineData(new byte[] { 0x00, 0x88, 0x02, 0x02, 0x07, 0x32, 0x00, 0x00, 0x00 }, false)]   // 固件呼吸效果
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, null)]    // 无回读
    public void CustomModeReadback_ComparesControlAndEffectBytes(byte[] report, bool? expected) =>
        Assert.Equal(expected, KeyboardRgb.IsCustomModeReadback(report));

    // ---- 灯条子通道：状态壳不等于灯珠 ----

    [Fact]
    public void Lighbar4OnZoneProbedBios_FollowsA2SupportBits()
    {
        var evidence = new LightingEvidence
        {
            LightbarStatusType = "MEZone_Lighbar4",
            LightbarStatusContent = true,
            BiosProjectId = "IDZ",
            BaseSupport = true,
            HingeSupport = true,
            LogoSupport = false,
            SyncStatusType = "MEZone_Lighbar4",
        };
        LightingChannelSet set = LightingChannelDetector.Detect(evidence);
        Assert.True(set.MainLightbar);
        Assert.True(set.Hinge);
        Assert.True(set.Sync);
        Assert.Equal(LogoFlavor.None, set.Logo);

        set = LightingChannelDetector.Detect(evidence with { BaseSupport = false, HingeSupport = false, LogoSupport = true });
        Assert.False(set.MainLightbar);   // A2 说没有 Base 分区 → 没有主灯条灯珠
        Assert.False(set.Hinge);
        Assert.Equal(LogoFlavor.Standard, set.Logo);
    }

    [Fact]
    public void SyncRequiresIdzAndRealStatus()
    {
        var evidence = new LightingEvidence
        {
            LightbarStatusType = "MEZone_Lighbar4", LightbarStatusContent = true,
            BiosProjectId = "IDX", HingeSupport = true, SyncStatusType = "MEZone_Lighbar4",
        };
        Assert.False(LightingChannelDetector.Detect(evidence).Sync);
        Assert.True(LightingChannelDetector.Detect(evidence).Hinge);
        Assert.False(LightingChannelDetector.Detect(evidence with { BiosProjectId = "IDZ", SyncStatusType = null }).Sync);
    }

    [Fact]
    public void OneWireLogo_OnIdaIdb()
    {
        var set = LightingChannelDetector.Detect(new LightingEvidence
        {
            LightbarStatusType = "MEZone_Lighbar4", LightbarStatusContent = true, BiosProjectId = "IDB",
            BaseSupport = true, NewlogoSupport = true,
        });
        Assert.Equal(LogoFlavor.OneWire, set.Logo);
        Assert.Equal(new[] { "Single", "Breathing", "Wave", "Impact", "Raindrop", "ColorMarquee" },
            LightingEffectCatalog.Logo(set.Logo).Select(e => e.Id).ToArray());
    }

    [Fact]
    public void LogoStatusShellWithoutLogoFlags_IsNotALogo()
    {
        using MechrevoHw hw = NewHardware();
        hw.HandleMessage("HidLightbar/Status", """{"type":"MEZone_Lighbar4","powerStatus":"On","LogoSupport":false}""");
        hw.HandleMessage("HidLightbar_Logo/Status", """{"type":"MEZone_Lighbar4","powerStatus":"Off"}""");
        Assert.True(hw.LogoLightStatusSeen);    // 原始证据照记
        Assert.False(hw.SupportsLogoLight);     // 但没有官方认的 Logo 灯珠标志 → 不显示
    }

    [Fact]
    public void EcLightbar_OnlyWhenServiceManagesItAndNoHidLightbar()
    {
        using MechrevoHw hw = NewHardware();
        hw.HandleMessage("MyRgbLightbar/Status",
            """{"POWER":"1","RL":"9","GL":"9","BL":"9","RL_DC":"0","GL_DC":"0","BL_DC":"0","COLORFUL":"1","BREATHINGLIGHT":"1","ACLINESTATUS":"1"}""");
        Assert.True(hw.SupportsEcLightbar);
        Assert.True(hw.EcLightbarColorful);
        Assert.Equal(9, hw.EcLightbarRed);
        Assert.False(hw.SupportsLightbar);

        hw.HandleMessage("HidLightbar/Status", """{"type":"MEZone_Lighbar4","powerStatus":"On"}""");
        Assert.False(hw.SupportsEcLightbar);
        Assert.True(hw.SupportsLightbar);
    }

    // ---- 效果目录：只列官方真支持的效果和参数 ----

    [Fact]
    public void FourZoneKeyboard_GetsItsOwnSixEffects_NotTheSingleZoneList()
    {
        // 旧实现把注册表序号 2（FourZone）当「单区」，四区键盘只剩单色/呼吸。
        Assert.False(KeyboardFirmwareEffects.IsSingleZoneRgb(2));
        Assert.True(KeyboardFirmwareEffects.IsSingleZoneRgb(1));
        Assert.Equal(new[] { "Single", "Breathing", "Wave", "Rainbow", "Mix", "Flash" },
            KeyboardFirmwareEffects.Visible(2).Select(e => e.Id).ToArray());
        Assert.Equal(11, KeyboardFirmwareEffects.Visible(KeyboardLightKind.PerKey).Length);
        Assert.Equal(new[] { "Single" }, KeyboardFirmwareEffects.Visible(KeyboardLightKind.FourZoneSingleColor).Select(e => e.Id).ToArray());
    }

    [Fact]
    public void EffectSpecs_ExposeOnlyParametersTheFirmwareUses()
    {
        LightEffectSpec single = LightingEffectCatalog.Find(LightingEffectCatalog.PerKeyKeyboard(), "Single")!;
        LightEffectSpec wave = LightingEffectCatalog.Find(LightingEffectCatalog.PerKeyKeyboard(), "Wave")!;
        LightEffectSpec rainbow = LightingEffectCatalog.Find(LightingEffectCatalog.PerKeyKeyboard(), "Rainbow")!;
        Assert.False(single.Speed);
        Assert.Equal(1, single.ColorSlots);
        Assert.False(wave.UsesColor);
        Assert.True(wave.UsesDirection);
        Assert.False(rainbow.Speed);
        Assert.False(rainbow.UsesColor);

        // 本机 A 面 Logo：呼吸只吃六种预设色、没有亮度和速度；混合什么参数都没有。
        LightEffectSpec[] logo = LightingEffectCatalog.Logo(LogoFlavor.MbaLogo);
        Assert.Equal(new[] { "Single", "Breathing", "Mix" }, logo.Select(e => e.Id).ToArray());
        Assert.False(logo[1].Brightness);
        Assert.NotNull(logo[1].PresetColors);
        Assert.False(logo[2].UsesColor || logo[2].Speed || logo[2].Brightness);

        // Lighbar4 主灯条：流星单色（旧实现给它发 7 色默认盘，颜色按钮点了无效）。
        LightEffectSpec raindrop = LightingEffectCatalog.Find(LightingEffectCatalog.Lighbar4("IDY"), "Raindrop")!;
        Assert.Equal(1, raindrop.ColorSlots);
        Assert.Equal(3, LightingEffectCatalog.Lighbar4("ID5").Length);
    }

    [Fact]
    public void ColorForSpec_HonoursPaletteSingleAndPresets()
    {
        var settings = new LightChannelSettings("Breathing", 4, 1, Color.FromArgb(10, 20, 30).ToArgb(), true);
        LightEffectSpec breathing = LightingEffectCatalog.Find(LightingEffectCatalog.PerKeyKeyboard(), "Breathing")!;
        Assert.Equal(Color.FromArgb(10, 20, 30).ToArgb(), LightingSettingsStore.ColorForSpec(breathing, settings)!.Value.ToArgb());
        Assert.Null(LightingSettingsStore.ColorForSpec(breathing, settings with { UsePalette = true }));

        LightEffectSpec wave = LightingEffectCatalog.Find(LightingEffectCatalog.PerKeyKeyboard(), "Wave")!;
        Assert.Null(LightingSettingsStore.ColorForSpec(wave, settings));

        LightEffectSpec mbaBreathing = LightingEffectCatalog.Logo(LogoFlavor.MbaLogo)[1];
        Color snapped = LightingSettingsStore.ColorForSpec(mbaBreathing, settings with { ColorArgb = Color.FromArgb(250, 10, 10).ToArgb() })!.Value;
        Assert.Equal(Color.FromArgb(255, 0, 0).ToArgb(), snapped.ToArgb());
    }

    [Fact]
    public void DirectionForSpec_StaysInsideOfficialSet()
    {
        LightEffectSpec wave = LightingEffectCatalog.Find(LightingEffectCatalog.PerKeyKeyboard(), "Wave")!;
        var settings = new LightChannelSettings("Wave", 4, 1, Color.White.ToArgb(), true, "OnKeyPressed");
        Assert.Equal("LeftRight", LightingSettingsStore.DirectionForSpec(wave, settings));
        Assert.Equal("UpDown", LightingSettingsStore.DirectionForSpec(wave, settings with { Direction = "UpDown" }));
        LightEffectSpec single = LightingEffectCatalog.Find(LightingEffectCatalog.PerKeyKeyboard(), "Single")!;
        Assert.Equal("None", LightingSettingsStore.DirectionForSpec(single, settings with { Direction = "UpDown" }));
    }

    [Theory]
    [InlineData("Single", 1)]
    [InlineData("Breathing", 2)]
    [InlineData("Raindrop", 10)]
    [InlineData("Impact", 13)]
    [InlineData("Mix", 19)]
    [InlineData("Gaming", 21)]
    [InlineData("Dawn", 7)]
    [InlineData("Bogus", -1)]
    public void FirmwareEffectIds_MatchServiceTranslateTable(string effect, int expected) =>
        Assert.Equal(expected, LightingEffectCatalog.FirmwareEffectId(effect));

    // ---- 结果三态 ----

    [Theory]
    [InlineData(false, null, "Failed")]
    [InlineData(false, true, "Failed")]
    [InlineData(true, null, "Sent")]
    [InlineData(true, false, "Sent")]
    [InlineData(true, true, "Confirmed")]
    public void Outcome_OnlyConfirmedWithMatchingReadback(bool published, bool? matched, string expected) =>
        Assert.Equal(Enum.Parse<LightApplyOutcome>(expected), LightingEffectCatalog.ResolveOutcome(published, matched));

    [Fact]
    public void OutcomeText_IsHonestChinese()
    {
        Assert.Equal("已确认（设备回读）", LightingEffectCatalog.OutcomeText(LightApplyOutcome.Confirmed, LightReadbackSource.Device));
        Assert.Equal("已确认（官方服务回读）", LightingEffectCatalog.OutcomeText(LightApplyOutcome.Confirmed, LightReadbackSource.Service));
        Assert.Equal("已下发（未收到回读）", LightingEffectCatalog.OutcomeText(LightApplyOutcome.Sent, LightReadbackSource.None));
        Assert.Equal("失败", LightingEffectCatalog.OutcomeText(LightApplyOutcome.Failed, LightReadbackSource.None));
    }

    [Fact]
    public void ServiceLastEffectRecord_ParsesRealRegistryJson()
    {
        // 本机 HKLM\...\RGBKeyboard\MEZone_3p1nd_101\26_LastEffect 原文。
        const string json = """{"save_mode":1,"bSaved":true,"save_effect":2,"save_light":50,"save_speed":7,"save_direction":0,"save_layout_color":{"isCircular":true,"ColorBlocks":1,"ColorBuffer":[{"ID":0,"R":255,"G":255,"B":255}]},"save_layout_backgroundcolor":0,"save_layout_alphbet":"","save_power_status":0}""";
        Assert.True(GcuLightingReadback.TryParseLastEffect(json, out GcuLightEffectRecord record));
        Assert.Equal(2, record.Effect);
        Assert.Equal(50, record.Light);
        Assert.Equal(7, record.Speed);
        Assert.False(GcuLightingReadback.TryParseLastEffect("not json", out _));
    }

    // ---- 载荷与回退 ----

    [Fact]
    public async Task EffectPayload_EveryColorBlockCarriesAnId()
    {
        object? published = null;
        using var hw = new MechrevoHw((_, payload) => { published = payload; return Task.CompletedTask; }, new MechrevoDeviceCapabilities());
        hw.HandleMessage("HidLightbar/Status", RealLightbarStatus);
        var service = new MechrevoService(hw);

        Assert.True(await service.SetLightEffect("HidLightbar/Ctrl", "Breathing", 3, 2, "None", null));
        var payload = Assert.IsType<Dictionary<string, object>>(published);
        var color = Assert.IsType<Dictionary<string, object>>(payload["color"]);
        var blocks = Assert.IsType<object[]>(color["ColorBuffer"]);
        Assert.Equal(7, color["ColorBlocks"]);
        for (int i = 0; i < blocks.Length; i++)
            Assert.Equal(i, ((Dictionary<string, object>)blocks[i])["ID"]);
    }

    [Fact]
    public async Task SingleZoneKeyboardPayload_CarriesTheFieldsTheServiceRequires()
    {
        object? published = null;
        using var hw = new MechrevoHw((_, payload) => { published = payload; return Task.CompletedTask; }, new MechrevoDeviceCapabilities());
        hw.HandleMessage("Keyboard/Status", """{"solution":"EC","type":"SingleZone","powerStatus":"On","brightNess":"2"}""");
        var service = new MechrevoService(hw);

        Assert.True(await service.SetKeyboardEffect("Single", 2, 1, "None", Color.FromArgb(0, 0, 255)));
        var payload = Assert.IsType<Dictionary<string, object>>(published);
        Assert.Equal("21", payload["MonochromeIndex"]);   // 调色板 21 = 纯蓝
        foreach (string key in new[] { "ManualIndex1", "ManualIndex6", "ManualInterval", "BreathingIndex" })
            Assert.True(payload.ContainsKey(key), key);
    }

    [Fact]
    public async Task KeyboardCtrl_IsNotSentWhenServiceHasNoRgbController()
    {
        int publishes = 0;
        using var hw = new MechrevoHw((_, _) => { publishes++; return Task.CompletedTask; }, new MechrevoDeviceCapabilities());
        hw.HandleMessage("Keyboard/Status", """{"solution":"UNKNOWN","type":"Normal","powerStatus":"Off"}""");
        var service = new MechrevoService(hw);

        Assert.False(await service.SetKeyboardEffect("Wave"));
        Assert.Equal(0, publishes);
        Assert.False(hw.SupportsKeyboard);
    }

    [Theory]
    // HID 判定 × HID 连接 × 服务在线 × 亮度写入生效 → 是否回退官方通道
    [InlineData("Unsupported", false, true, true, true)]    // 四区 / HID 打不开 → 官方通道
    [InlineData("Unsupported", false, false, true, false)]  // 没服务：绝不假装回退
    [InlineData("Supported", true, true, true, false)]      // 逐键可直控 → 软件灯效优先
    [InlineData("Supported", true, true, false, true)]      // 设备在但亮度写入被拒 → 官方通道
    [InlineData("Unknown", false, true, true, false)]       // 未探测：不改道
    public void FallbackPolicy_SoftwareFirstGcuWhenUnsupported(string hid, bool connected, bool service,
        bool brightnessTookEffect, bool expected) =>
        Assert.Equal(expected, KeyboardLightPathPolicy.ShouldUseGcuKeyboardFallback(
            Enum.Parse<FeatureAvailability>(hid), connected, service, brightnessTookEffect));

    [Fact]
    public void SingleColorQuickSwitch_HiddenOnRgbMachines()
    {
        using MechrevoHw hw = NewHardware();
        hw.HandleMessage("Setting/Status", """{"SingleColorKBBL":"SINGLE_COLOR_KBBL_STATUS_ON"}""");
        hw.HandleMessage("Keyboard/Status", RealKeyboardStatus);
        hw.HandleMessage("Customize/Info", RealCustomizeInfo);
        Assert.False(hw.SupportsQuickSwitch("singlecolorkb"));

        using MechrevoHw single = NewHardware();
        single.HandleMessage("Setting/Status", """{"SingleColorKBBL":"SINGLE_COLOR_KBBL_STATUS_ON"}""");
        single.HandleMessage("Keyboard/Status", """{"solution":"UNKNOWN","type":"Normal"}""");
        single.HandleMessage("Customize/Info", """{"LightbarType":"0","KeyboardType":"2"}""");
        Assert.True(single.SupportsQuickSwitch("singlecolorkb"));
        Assert.Equal(KeyboardLightKind.SingleColorBacklight, single.Lighting.Keyboard);
    }
}
