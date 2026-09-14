using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 深度审计后逐项修复的回归锁。
///
/// 这一批缺陷有个共同特征：**编译没问题、测试也没红，只有真机上"点了没反应"**。
/// 原因是它们全部落在「代码里的字段名/主题/类型」与「服务端真实载荷」之间的缝里，
/// 而单元测试如果只喂自己造的载荷，就会跟着错的那份名字一起错。
///
/// 所以这一组测试的载荷片段全部取自开发机实测的原始报文转储
/// （FunctionVerifier 的 RawMessageObserved），不是照代码反推出来的。
/// </summary>
public class AuditFixRegressionTests
{
    static (MechrevoHw Hardware, MechrevoService Service, List<(string Topic, Dictionary<string, object> Payload)> Written) NewRig(
        MechrevoDeviceCapabilities? capabilities = null)
    {
        var written = new List<(string, Dictionary<string, object>)>();
        var hardware = new MechrevoHw((topic, payload) =>
        {
            if (payload is IDictionary<string, object> values)
                written.Add((topic, new Dictionary<string, object>(values)));
            return Task.CompletedTask;
        }, capabilities ?? new MechrevoDeviceCapabilities());
        return (hardware, new MechrevoService(hardware), written);
    }

    /// <summary>
    /// 找出针对某个主题下发的、带指定 Action 的那一条。
    ///
    /// 不能用 Assert.Single：确认流程会额外补发 GETSTATUS，
    /// 断言"只发了一条"会把正确实现也判红。
    /// </summary>
    static Dictionary<string, object> CommandFor(
        List<(string Topic, Dictionary<string, object> Payload)> written, string topic, string action) =>
        Assert.Single(written.Where(w =>
            w.Topic == topic &&
            w.Payload.TryGetValue("Action", out object? a) &&
            (a as string) == action).Select(w => w.Payload));

    // ------------------------------------------------- CPU 高级性能 / 超频菜单

    /// <summary>
    /// 状态字段名是 <c>CPU_PerformanceAndOverClockMenuSwitch</c>——带下划线。
    ///
    /// 这里曾经写成没有下划线的 CPUPerformanceAndOverClockMenuSwitch，
    /// 那是 GCUService 内部结构体的成员名，不是发出来的 JSON 键名。
    /// 后果：HasField 永远为假，整项在界面上从来没出现过，
    /// 而且因为「没出现」也就没人报障，缺陷藏了很久。
    /// </summary>
    [Fact]
    public void CpuAdvancedPerformance_ReadsTheUnderscoredFieldNameTheServerActuallySends()
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            // 取自实测 Fan/Status（同帧还带 OcSupport=True，那是官方的门禁依据）。
            hardware.HandleMessage("Fan/Status", """
                {"CPU_PerformanceAndOverClockMenuSwitch":1,"OcSupport":true}
                """);

            Assert.True(hardware.CpuAdvancedPerformanceSeen);
            Assert.True(hardware.QuickSwitches["cpuadvperf"]);
            Assert.True(hardware.SupportsQuickSwitch("cpuadvperf"));
        }
    }

    /// <summary>
    /// 旧的无下划线写法要继续认：万一某个固件版本真用那个名字，不能因为改名把它丢了。
    /// </summary>
    [Fact]
    public void CpuAdvancedPerformance_StillAcceptsTheLegacyFieldNameAsAnAlias()
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Fan/Status", """
                {"CPUPerformanceAndOverClockMenuSwitch":1,"OcSupport":true}
                """);

            Assert.True(hardware.SupportsQuickSwitch("cpuadvperf"));
            Assert.True(hardware.QuickSwitches["cpuadvperf"]);
        }
    }

    /// <summary>
    /// 门禁要认同帧的 <c>OcSupport</c>。
    ///
    /// 过去只认构造时从注册表读的 Capabilities.CpuPerformanceTuning
    /// （键名 CPUPerformanceAndOverClockMenuSupport）。开发机的注册表里没有那个键，
    /// 于是门禁恒假——就算字段名改对了，入口还是出不来。
    /// 官方的判据是 IsOcSettingsSupport || HWOCSupport（CCUWinUI L52736）。
    /// </summary>
    [Fact]
    public void CpuAdvancedPerformance_GateAcceptsTheSupportBitFromTheStatusFrame()
    {
        // 静态能力画像全空：只靠注册表的话这台机器永远看不到入口。
        var (hardware, _, _) = NewRig(new MechrevoDeviceCapabilities());
        using (hardware)
        {
            hardware.HandleMessage("Fan/Status", """{"CPU_PerformanceAndOverClockMenuSwitch":1}""");
            Assert.False(hardware.SupportsQuickSwitch("cpuadvperf"));   // 还没有任何支持证据

            hardware.HandleMessage("Fan/Status", """
                {"CPU_PerformanceAndOverClockMenuSwitch":1,"OcSupport":true}
                """);
            Assert.True(hardware.SupportsQuickSwitch("cpuadvperf"));
        }
    }

    /// <summary>服务端明确报不支持时，静态能力画像不能把它顶回来。</summary>
    [Fact]
    public void CpuAdvancedPerformance_ExplicitFalseSupportOverridesTheStaticCapability()
    {
        var (hardware, _, _) = NewRig(MechrevoDeviceCapabilities.FromValues(
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["CPUPerformanceAndOverClockMenuSupport"] = 1,
                ["OcSettingsSupport"] = 1,
            }));
        using (hardware)
        {
            hardware.HandleMessage("Fan/Status", """
                {"CPU_PerformanceAndOverClockMenuSwitch":1,"OcSupport":false}
                """);

            Assert.False(hardware.SupportsOverclockMenu);
            Assert.False(hardware.SupportsQuickSwitch("cpuadvperf"));
        }
    }

    /// <summary>
    /// 下发形状：<c>Fan/Control</c> + <c>Action=SET_OPERATING_MODE_DETAIL</c>，
    /// 开与关的区别在**载荷字段名**上，值是字符串 "1" / "0"。
    ///
    /// 过去把 CPUPerformanceAndOverClockMenuSwitch_ON 当成 Action 发到了
    /// Setting/Control，还用轮询 Setting 通道的确认器去等一个在 Fan/Status 里的状态。
    /// 主题、动作、确认通道三处都错，所以这一项在真机上是纯粹的空头开关。
    /// 依据：官方 CpuAdvancedPerformanceSwitchCommand（CCUWinUI L49989-50014）。
    /// </summary>
    [Theory]
    [InlineData(true, "CPUPerformanceAndOverClockMenuSwitch_ON", "1")]
    [InlineData(false, "CPUPerformanceAndOverClockMenuSwitch_OFF", "0")]
    public async Task CpuAdvancedPerformance_SendsTheOfficialFanChannelShape(
        bool on, string expectedField, string expectedValue)
    {
        var (hardware, service, written) = NewRig();
        using (hardware)
        {
            // 先让能力成立，否则命令会在门禁处直接返回 false 而不下发。
            hardware.HandleMessage("Fan/Status", $$"""
                {"CPU_PerformanceAndOverClockMenuSwitch":{{(on ? 0 : 1)}},"OcSupport":true}
                """);
            written.Clear();

            await service.SwitchCpuAdvancedPerformance(on);

            var command = CommandFor(written, "Fan/Control", "SET_OPERATING_MODE_DETAIL");
            Assert.Equal(expectedValue, Assert.IsType<string>(command[expectedField]));
            // 动作名里不该出现开关后缀——那是它过去被误当 Action 用的痕迹。
            Assert.DoesNotContain(written, w => w.Payload.TryGetValue("Action", out object? a) &&
                a is string s && s.StartsWith("CPUPerformanceAndOverClockMenuSwitch", StringComparison.Ordinal));
            // 确认必须走 Fan 通道的 GETSTATUS，不能去刷 Setting/Control。
            Assert.Contains(written, w => w.Topic == "Fan/Control" &&
                w.Payload.TryGetValue("Action", out object? a) && (a as string) == "GETSTATUS");
            Assert.DoesNotContain(written, w => w.Topic == "Setting/Control");
        }
    }

    // ------------------------------------------------------------ 电池 Logo 灯

    /// <summary>
    /// <c>BatteryLogo_Status</c> 此前完全缺失：既没解析也没下发，有这条灯的机型
    /// 在我们这边根本看不到入口。判定与官方一致（L73881 <c>!Contains("OFF")</c>）。
    /// </summary>
    [Fact]
    public void BatteryLogo_IsParsedFromTheStatusStringTheServerSends()
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status", """{"BatteryLogo_Status":"BATTERYLOGO_TOGGLE_OFF"}""");
            Assert.True(hardware.BatteryLogoSeen);
            Assert.True(hardware.SupportsQuickSwitch("batterylogo"));
            Assert.False(hardware.QuickSwitches["batterylogo"]);

            hardware.HandleMessage("Setting/Status", """{"BatteryLogo_Status":"BATTERYLOGO_TOGGLE_ON"}""");
            Assert.True(hardware.QuickSwitches["batterylogo"]);
        }
    }

    /// <summary>没报这个字段的机型不能长出入口。</summary>
    [Fact]
    public void BatteryLogo_StaysHiddenWhenTheServerNeverReportsIt()
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status", """{"UsbCharger":"USB_CHARGER_STATUS_ON"}""");
            Assert.False(hardware.BatteryLogoSeen);
            Assert.False(hardware.SupportsQuickSwitch("batterylogo"));
        }
    }

    [Theory]
    [InlineData(true, "BATTERYLOGO_TOGGLE_ON")]
    [InlineData(false, "BATTERYLOGO_TOGGLE_OFF")]
    public async Task BatteryLogo_SendsTheOfficialToggleAction(bool on, string expectedAction)
    {
        var (hardware, service, written) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status",
                $$"""{"BatteryLogo_Status":"BATTERYLOGO_TOGGLE_{{(on ? "OFF" : "ON")}}"}""");
            written.Clear();

            await service.SwitchQuick("batterylogo", on);

            Assert.Contains(written, w => w.Topic == "Setting/Control" &&
                w.Payload.TryGetValue("Action", out object? a) && (a as string) == expectedAction);
        }
    }

    // ------------------------------------------- 状态字符串判定与官方逐字对齐

    /// <summary>
    /// 官方读的是「状态串里有没有那个否定词」，不是拿整串比常量
    /// （L31096 WinKey / L31192 NumPad / L31241 FnKey 都是 <c>!Contains("UNLOCK")</c>，
    /// L73865 UsbCharger 与 L31146 OSD 是 <c>!Contains("OFF")</c>）。
    ///
    /// 差别只在别的机型上显出来：固件一旦把 USB_CHARGER_STATUS_ON 写成
    /// USB_CHARGER_ON，精确匹配就永久判成关闭，用户看到的是「点了自己跳回去」。
    /// 这组测试特意用**变体名**，用实测的那个名字反而测不出退化。
    /// </summary>
    [Theory]
    [InlineData("USB_CHARGER_STATUS_ON", true)]
    [InlineData("USB_CHARGER_ON", true)]           // 变体：精确匹配会漏
    [InlineData("USB_CHARGER_STATUS_OFF", false)]
    [InlineData("USB_CHARGER_OFF", false)]
    public void UsbCharger_JudgesByTheAbsenceOfOff(string reported, bool expected)
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status", $$"""{"UsbCharger":"{{reported}}"}""");
            Assert.True(hardware.UsbChargerSeen);
            Assert.Equal(expected, hardware.UsbCharger);
        }
    }

    [Theory]
    [InlineData("WinKey", "winkey", "WINKEY_STATUS_LOCK", true)]
    [InlineData("WinKey", "winkey", "WINKEY_LOCK", true)]            // 变体
    [InlineData("WinKey", "winkey", "WINKEY_STATUS_UNLOCK", false)]
    [InlineData("WinKey", "winkey", "WINKEY_UNLOCK", false)]
    [InlineData("FnKey", "fnkey", "FNKEY_LOCK", true)]
    [InlineData("FnKey", "fnkey", "FNKEY_STATUS_LOCK", true)]        // 变体
    [InlineData("FnKey", "fnkey", "FNKEY_UNLOCK", false)]
    [InlineData("NumPad", "numpad", "NUMPAD_LOCK", true)]
    [InlineData("NumPad", "numpad", "NUMPAD_STATUS_LOCK", true)]     // 变体
    [InlineData("NumPad", "numpad", "NUMPAD_UNLOCK", false)]
    public void KeyLocks_JudgeByTheAbsenceOfUnlock(string field, string key, string reported, bool expected)
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status", $$"""{"{{field}}":"{{reported}}"}""");
            Assert.Equal(expected, hardware.QuickSwitches[key]);
        }
    }

    /// <summary>
    /// OSD 的极性：官方的 OSDSwitch 语义是「隐藏 OSD」，
    /// 我们的 QuickSwitches["osd"] 语义是「显示 OSD」（下发 true => OSD_HIDDEN_OFF）。
    /// 所以判定要取反，而且同样按含不含 OFF 来判，不是比常量。
    /// </summary>
    [Theory]
    [InlineData("OSD_HIDDEN_ON", false)]          // 已隐藏 => 「显示」为假
    [InlineData("OSD_HIDDEN_STATUS_ON", false)]   // 变体
    [InlineData("OSD_HIDDEN_OFF", true)]
    public void Osd_KeepsTheDisplayPolarityWhileMatchingTheOfficialRule(string reported, bool expectedVisible)
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status", $$"""{"OSD":"{{reported}}"}""");
            Assert.Equal(expectedVisible, hardware.QuickSwitches["osd"]);
        }
    }

    /// <summary>
    /// 下发方向必须与回显方向自洽：勾上 = 显示 OSD = 发 OSD_HIDDEN_OFF。
    /// 这两处极性只要有一处翻了，开关就会自己弹回去。
    /// </summary>
    [Theory]
    [InlineData(true, "OSD_HIDDEN_OFF")]
    [InlineData(false, "OSD_HIDDEN_ON")]
    public async Task Osd_SendDirectionMatchesTheReadbackPolarity(bool visible, string expectedAction)
    {
        var (hardware, service, written) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status",
                $$"""{"OSD":"OSD_HIDDEN_{{(visible ? "ON" : "OFF")}}"}""");
            written.Clear();

            await service.SwitchQuick("osd", visible);

            Assert.Contains(written, w => w.Topic == "Setting/Control" &&
                w.Payload.TryGetValue("Action", out object? a) && (a as string) == expectedAction);
        }
    }

    /// <summary>
    /// 灯带电源回读放宽到忽略大小写。官方是精确比 "On"，值域也只有 On/Off，
    /// 所以忽略大小写不会引入误判，但能兜住固件写成 "ON" 的情况——
    /// 原来那种写法下，四条灯带的开关回显会一起变成恒关。
    /// </summary>
    [Theory]
    [InlineData("On", true)]
    [InlineData("ON", true)]
    [InlineData("on", true)]
    [InlineData("Off", false)]
    [InlineData("OFF", false)]
    public void LightbarPowerReadbackIsCaseInsensitive(string reported, bool expected)
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("HidLightbar/Status",
                $$"""{"type":"1","powerStatus":"{{reported}}","brightNess":"50"}""");
            Assert.Equal(expected, hardware.QuickSwitches["lightbar"]);
        }
    }

    // --------------------------------------------------- 支持位一票否决

    /// <summary>
    /// 服务端对不支持的机型照样发状态字段：开发机实测
    /// <c>LCDOverdriveSwitch=LCDOverdrive_OFF</c> 与 <c>LCDOverdriveSupport=NotSupport</c>
    /// 同时出现。过去无条件置 LcdOverdriveSeen，于是界面上长出一个点了永远不生效的开关，
    /// 确认逻辑也永远等不到回读变化——正是"空头接口"的典型形态。
    /// </summary>
    [Fact]
    public void LcdOverdrive_StaysUnsupportedWhenTheServerSaysNotSupport()
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status", """
                {"LCDOverdriveSwitch":"LCDOverdrive_OFF","LCDOverdriveSupport":"NotSupport"}
                """);

            Assert.False(hardware.LcdOverdriveSeen);
            Assert.False(hardware.SupportsLcdOverdrive);
        }
    }

    /// <summary>报了支持位的机型正常暴露入口。</summary>
    [Fact]
    public void LcdOverdrive_BecomesAvailableWhenTheServerSaysSupport()
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status", """
                {"LCDOverdriveSwitch":"LCDOverdrive_ON","LCDOverdriveSupport":"Support"}
                """);

            Assert.True(hardware.SupportsLcdOverdrive);
            Assert.True(hardware.LcdOverdrive);
        }
    }

    /// <summary>
    /// 支持位一票否决静态能力画像：注册表里的 ItemSupport 是机型出厂画像，
    /// 可能与实际装的屏不一致，而状态帧里的 *Support 是这台机器此刻的答案。
    /// </summary>
    [Fact]
    public void LcdOverdrive_ServerSupportBitOverridesTheRegistryCapability()
    {
        var (hardware, _, _) = NewRig(MechrevoDeviceCapabilities.FromValues(
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["LCDOverdriveSupport"] = 1,
            }));
        using (hardware)
        {
            Assert.True(hardware.SupportsLcdOverdrive);   // 只有注册表时按注册表来

            hardware.HandleMessage("Setting/Status", """{"LCDOverdriveSupport":"NotSupport"}""");
            Assert.False(hardware.SupportsLcdOverdrive);
        }
    }

    /// <summary>没报支持位的老固件不能因此丢掉这项能力——只有显式 false 才否决。</summary>
    [Fact]
    public void LcdOverdrive_MissingSupportBitFallsBackToTheStatusField()
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status", """{"LCDOverdriveSwitch":"LCDOverdrive_ON"}""");
            Assert.True(hardware.SupportsLcdOverdrive);
        }
    }

    // ------------------------------------------------------------ 模式切换

    /// <summary>
    /// 切模式要发两条：<c>Fan/Control</c> 的模式动作，紧跟 <c>LCHWOC/Control</c>
    /// 的运行标记（官方 ModeSwitchCommand，CCUWinUI L55090-55146）。
    ///
    /// 过去只在 SwitchCustomProfile 里发 IsCustomRun、从不发 IsNormalRun，
    /// 于是从自定义模式切回普通模式时超频通道还留在「自定义运行」，
    /// 硬件那边的超频参数不复位——表现是切回办公模式后功耗/频率还是自定义档的值。
    /// </summary>
    [Theory]
    [InlineData(0, "OPERATING_GAMING_MODE", 1)]
    [InlineData(1, "OPERATING_TURBO_MODE", 2)]
    [InlineData(2, "OPERATING_OFFICE_MODE", 0)]
    public void ModeSwitchPayloads_CarryTheOverclockRunFlagForNormalModes(
        int _, string action, int operatingMode)
    {
        var (fan, overclock) = MechrevoHw.BuildModeSwitchPayloads(action, operatingMode, 0, custom: false);

        Assert.Equal(action, fan["Action"]);
        Assert.Equal(operatingMode, Assert.IsType<int>(overclock["IsNormalRun"]));
        Assert.False(overclock.ContainsKey("IsCustomRun"));
    }

    [Fact]
    public void ModeSwitchPayloads_UseIsCustomRunForTheCustomProfile()
    {
        var (fan, overclock) = MechrevoHw.BuildModeSwitchPayloads(
            "OPERATING_CUSTOM_MODE", 3, profileIndex: 2, custom: true);

        Assert.Equal(2, Assert.IsType<int>(fan["ProfileIndex"]));
        Assert.True(Assert.IsType<bool>(overclock["IsCustomRun"]));
        Assert.False(overclock.ContainsKey("IsNormalRun"));
    }

    /// <summary>
    /// <c>ProfileIndex</c> 是 JSON 数字。官方 ModeSwitchCommand 发的是
    /// 整数字面量 0 / int 型的 CustomProfileIndex，我们过去发字符串 "0"。
    /// </summary>
    [Fact]
    public void ModeSwitchPayloads_SendProfileIndexAsANumberNotAString()
    {
        var (fan, _) = MechrevoHw.BuildModeSwitchPayloads("OPERATING_GAMING_MODE", 1, 0, custom: false);

        Assert.IsType<int>(fan["ProfileIndex"]);
        Assert.IsNotType<string>(fan["ProfileIndex"]);
    }

    /// <summary>
    /// <see cref="MechrevoHw.SetMode"/> 与 MechrevoService.SwitchMode 共用同一份
    /// 载荷构造。曾经并存两份手写载荷，其中一份漏了 LCHWOC 那条、ProfileIndex 还是字符串。
    /// </summary>
    [Fact]
    public async Task SetMode_AlsoSendsTheOverclockRunFlag()
    {
        var (hardware, _, written) = NewRig();
        using (hardware)
        {
            await hardware.SetMode(2);   // G-Helper 语义的 Silent => 办公模式

            var fan = CommandFor(written, "Fan/Control", "OPERATING_OFFICE_MODE");
            Assert.Equal(0, Assert.IsType<int>(fan["ProfileIndex"]));
            var overclock = Assert.Single(written.Where(w => w.Topic == "LCHWOC/Control").Select(w => w.Payload));
            Assert.Equal(0, Assert.IsType<int>(overclock["IsNormalRun"]));
        }
    }

    // -------------------------------------------------------- Uni / Omni 互斥

    /// <summary>
    /// 关伙伴成功、开自己失败时必须把伙伴还原。
    ///
    /// 不还原的话用户点一下 Omni，结果 Omni 没开、Uni 反而被关掉了，
    /// 落到「两个都关」——而这一组是互斥单选，本来不该出现这种组合。
    ///
    /// 这里让 Omni 的写入必然失败（服务端不回状态，确认超时），
    /// 同时手工把 Uni 的关闭确认喂进去，复现"半成功"这个窗口。
    /// </summary>
    [Fact]
    public async Task SwitchUniOmni_RestoresThePartnerWhenTurningItselfOnFails()
    {
        var written = new List<(string Topic, Dictionary<string, object> Payload)>();
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (payload is IDictionary<string, object> values)
            {
                var copy = new Dictionary<string, object>(values);
                written.Add((topic, copy));
                // 只让 Uni 的开关命令"生效"：把对应状态回喂给解析层。
                // Omni 的命令一律不回状态，于是它的确认必然超时。
                if (copy.TryGetValue("Action", out object? a) && a is string action &&
                    action.StartsWith("Uni_", StringComparison.Ordinal))
                {
                    hardware!.HandleMessage("Setting/Status",
                        $$"""{"UniSwitch":"{{action}}","OmniSwitch":"Omni_OFF"}""");
                }
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities());

        using (hardware)
        {
            var service = new MechrevoService(hardware);
            hardware.HandleMessage("Setting/Status", """{"UniSwitch":"Uni_ON","OmniSwitch":"Omni_OFF"}""");
            Assert.True(hardware.QuickSwitches["uni"]);
            written.Clear();

            bool confirmed = await service.SwitchUniOmni("omni", true);

            Assert.False(confirmed);
            // 关掉 Uni 与还原 Uni 各一次。
            Assert.Contains(written, w => w.Payload.TryGetValue("Action", out object? a) && (a as string) == "Uni_OFF");
            Assert.Contains(written, w => w.Payload.TryGetValue("Action", out object? a) && (a as string) == "Uni_ON");
            Assert.True(hardware.QuickSwitches["uni"]);   // 伙伴回到了原状
        }
    }

    /// <summary>伙伴本来就是关的时候不要多发一条还原命令。</summary>
    [Fact]
    public async Task SwitchUniOmni_DoesNotTouchThePartnerThatWasAlreadyOff()
    {
        var (hardware, service, written) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status", """{"UniSwitch":"Uni_OFF","OmniSwitch":"Omni_OFF"}""");
            written.Clear();

            await service.SwitchUniOmni("omni", true);

            Assert.DoesNotContain(written, w =>
                w.Payload.TryGetValue("Action", out object? a) &&
                a is string s && s.StartsWith("Uni_", StringComparison.Ordinal));
        }
    }

    // ---------------------------------------------------------- 死代码清除

    /// <summary>
    /// <c>MechrevoHw</c> 上不该再有第二张快捷开关动作表。
    ///
    /// 那些方法（SetQuickSwitch / SetUsbCharger / SetFanBoost / SetRefreshRate）
    /// 与 MechrevoService 的带确认版本平行存在、只发不确认、零调用方。
    /// 害处不是占空间而是会静默腐化：SetQuickSwitch 那张表停在 11 个开关，
    /// 后来补的 8 个官方开关一个都没有，其中 gamewhitelist 与 cpuadvperf
    /// 根本不该走 Setting/Control。谁照它接线就会得到一个点了没反应的开关。
    /// </summary>
    [Theory]
    [InlineData("SetQuickSwitch")]
    [InlineData("SetUsbCharger")]
    [InlineData("SetFanBoost")]
    [InlineData("SetRefreshRate")]
    public void MechrevoHw_NoLongerExposesAParallelUnconfirmedCommandTable(string removed)
    {
        Assert.Null(typeof(MechrevoHw).GetMethod(removed,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance));
    }

    /// <summary>
    /// 每个能出现在界面上的快捷开关都必须有下发实现。
    ///
    /// numpad 就是反例：解析、能力判定、下发动作三段一直都齐，唯独漏了界面入口，
    /// 于是 Capabilities.Numpad 和 SwitchQuick 里的 "numpad" 分支都成了死代码。
    /// 这个测试从相反方向锁住：只要 SupportsQuickSwitch 认这个键，
    /// SwitchQuick 或专用方法就得能把它发出去。
    /// </summary>
    [Fact]
    public async Task EverySupportedSwitchHasAWorkingSendPath()
    {
        // 一帧塞进所有已知开关字段，让能力判定全部成立。
        const string settingStatus = """
            {"WinKey":"WINKEY_STATUS_LOCK","FnKey":"FNKEY_LOCK","NumPad":"NUMPAD_LOCK",
             "OSD":"OSD_HIDDEN_ON","UsbCharger":"USB_CHARGER_STATUS_ON",
             "CopilotKey":"COPILOTKEY_LOCK","DeepSleepSwitch":"DEEPSLEEP_OFF",
             "AcRecoverySwitch_Status":"ACRECOVERY_TOGGLE_OFF",
             "HighPerformancePowerModeSwitch":"HIGHPERFORMANCEPOWERMODE_OFF",
             "TouchpadToggle":"TOUCHPAD_TOGGLE_ON","SingleColorKBBL":"SINGLE_COLOR_KBBL_STATUS_ON",
             "UniSwitch":"Uni_OFF","OmniSwitch":"Omni_OFF",
             "PowerLightSwitch":"PowerLight_ON","PowerLightBrightness":100,
             "BatteryLogo_Status":"BATTERYLOGO_TOGGLE_OFF"}
            """;
        const string fanStatus = """
            {"FanBoostEnable":0,"GameWhitelistSwitch":0,
             "CPU_PerformanceAndOverClockMenuSwitch":0,"OcSupport":true}
            """;

        const string lightbarStatus = """{"type":"1","powerStatus":"Off","brightNess":"50"}""";

        string[] candidates =
        [
            "touchpad", "wifi", "bt", "webcam", "winkey", "fnkey", "numpad", "osd", "usb",
            "deepsleep", "copilot", "acrecovery", "highperf", "fanboost",
            "lightbar", "logolight",
            "touchpadtoggle", "singlecolorkb", "uni", "omni", "powerlight", "batterylogo",
            "gamewhitelist", "cpuadvperf",
        ];

        foreach (string key in candidates)
        {
            var (hardware, service, written) = NewRig();
            using (hardware)
            {
                hardware.HandleMessage("Setting/Status", settingStatus);
                // 字段名照实测载荷抄：TochpadEnable 少一个 u、WIFIEnable / BTEnable 是全大写缩写。
                // 写成"看起来对"的 TouchPadEnable / WifiEnable 会让这四项被静默跳过，
                // 测试照样绿，但什么都没验证。
                hardware.HandleMessage("Settings/DeviceSwitchItemStatus", """
                    {"TochpadEnable":true,"WIFIEnable":true,"BTEnable":true,"WebCamEnable":true}
                    """);
                hardware.HandleMessage("Fan/Status", fanStatus);
                foreach (string lightTopic in new[] { "HidLightbar/Status", "HidLightbar_Logo/Status" })
                    hardware.HandleMessage(lightTopic, lightbarStatus);
                if (!hardware.SupportsQuickSwitch(key)) continue;
                written.Clear();

                Task<bool> send = key switch
                {
                    "usb" => service.SwitchUsbCharger(true),
                    "fanboost" => service.SwitchFanBoost(true),
                    "deepsleep" => service.SwitchDeepSleep(true),
                    "gamewhitelist" => service.SwitchGameWhitelist(true),
                    "cpuadvperf" => service.SwitchCpuAdvancedPerformance(true),
                    "uni" or "omni" => service.SwitchUniOmni(key, true),
                    "lightbar" => service.SetLightPower("HidLightbar/Ctrl", true),
                    "logolight" => service.SetLightPower("HidLightbar_Logo/Ctrl", true),
                    _ => service.SwitchQuick(key, true),
                };
                await send;

                Assert.True(written.Count > 0,
                    $"{key} 声称支持却没有下发任何命令——这就是一个空头开关。");
            }
        }
    }
}
