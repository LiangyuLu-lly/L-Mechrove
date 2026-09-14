using System.Globalization;
using System.Numerics;
using MechrevoLite.Hardware;
using Newtonsoft.Json.Linq;

namespace MechrevoLite.Tests;

/// <summary>
/// 协议层暴力测试（fuzz）。GCU 发来的每条 MQTT 消息都经过 MechrevoHw.HandleMessage，
/// 这是应用最大的不受信输入面。这里的假设是「对端可能发任何字节」——固件升级引入
/// 新记法、GCU 自身有 bug、或本地其他进程向 broker 注入消息时，解析层必须保证：
/// 1. 单条畸形消息绝不抛异常（异常发生在 MQTT 接收回调里，会让本帧后续通知全部丢失）；
/// 2. 结构不变量在任意输入后仍然成立（曲线数组恒 16 点、TCC 区间不倒挂等）；
/// 3. 坏帧不留毒：紧随其后的合法帧必须完整生效。
/// 与 PayloadParsingTests 的区别：那边针对「已知畸形形状」写确定性断言，这里按
/// 组合暴力扫（主题 × 载荷 × 逐字段类型混淆），覆盖的是没被想到过的形状。
/// </summary>
public class ProtocolFuzzTests
{
    static readonly string[] Topics =
    {
        // HandleMessage 有分支的全部主题
        "System/CpuInfo", "System/GpuInfo", "System/MemoryInfo", "System/FanInfo",
        "System/BatteryInfo", "System/NetworkInfo", "System/HardwareInfo",
        "System/FanErrorInfo", "System/BatteryProtection",
        "Fan/Table", "Fan/Status", "GPUDevice/Status", "Settings/DeviceSwitchItemStatus",
        "Setting/Status", "LCHWOC/Status", "BT_LC/Status", "Keyboard/Status",
        "HidLightbar/Status", "HidLightbar_Logo/Status",
        // 订阅了但解析器没有分支的主题，以及大小写错乱——同样必须无害
        "System/Unknown", "Customize/Whatever", "fan/status", "FAN/STATUS", "",
    };

    /// <summary>结构级畸形载荷：不是对象、截断、深度炸弹、超大、BOM、重复键……</summary>
    static List<string> BuildGarbagePayloads() =>
    [
        "", " ", "\t\r\n", "null", "true", "false", "42", "-0.5", "\"just a string\"",
        "[1,2,3]", "[]", "{}", "{", "}", "{]", "{\"a\":}", "{,}", "{\"a\":1,}",
        "{\"a\":undefined}", "{\"a\":+5}", "{\"a\":05}", "{\"a\":.5}", "{\"a\":0x10}",
        "{\"a\":\"\\u0000\"}", "{\"a\":\"unterminated}", "{'a':1}",
        "\uFEFF{\"CpuTemperature\":55}",
        "{\"CpuTemperature\":55}\u0000",
        new string('{', 4096),
        "{\"a\":" + new string('[', 5000) + new string(']', 5000) + "}",
        string.Concat(Enumerable.Repeat("{\"a\":", 3000)) + "1" + string.Concat(Enumerable.Repeat("}", 3000)),
        new string('x', 1_048_576),
        "{\"CpuTemperature\":" + new string('9', 100_000) + "}",
        "{\"CpuTemperature\":1,\"CpuTemperature\":2,\"CpuTemperature\":3}",
        "{\"CpuTemperature\":\"\",\"CpuUsage\":null,\"CpuFrequency\":[]}",
    ];

    /// <summary>逐字段类型混淆用的替身值：越界数、超大整数、溢出浮点、奇怪字符串、容器。</summary>
    static List<JToken> BuildAdversarialScalars() =>
    [
        new JValue((object?)null),
        JValue.CreateUndefined(),
        new JValue(true),
        new JValue(-1),
        new JValue(int.MaxValue),
        new JValue(int.MinValue),
        new JValue((long)int.MaxValue + 1),
        new JValue((long)int.MinValue - 1),
        new JValue(BigInteger.Parse(new string('9', 40))),
        new JValue(1.5),
        new JValue(-2.75),
        new JValue(1e300),
        new JValue(-1e300),
        new JValue(double.MaxValue),
        new JValue(""),
        new JValue(" "),
        new JValue("0"),
        new JValue("-1"),
        new JValue("62.0"),
        new JValue("1e309"),
        new JValue("-1e309"),
        new JValue("NaN"),
        new JValue("Infinity"),
        new JValue("0x1F"),
        new JValue("62.0.0"),
        new JValue("999999999999999999999999"),
        new JValue("OPERATING_GAMING_MODE"),
        new JValue("NOT_SUPPORT"),
        new JValue("\ud83d\ude00\u0001\u001f"),
        new JArray(),
        new JArray(1, 2, 3),
        new JObject(),
        new JObject { ["a"] = 1 },
    ];

    static string BuildCurveJson() =>
    "{\"Name\":\"M1T1\",\"FanControlRespective\":false,\"CPU\":[" +
    string.Join(",", Enumerable.Range(0, 16).Select(i => $"{{\"UpT\":{40 + i * 2},\"Duty\":{i * 6 + 5}}}")) +
    "],\"GPU\":[" +
    string.Join(",", Enumerable.Range(0, 16).Select(i => $"{{\"UpT\":{45 + i * 2},\"Duty\":{i * 5 + 8}}}")) +
    "]}";

    static readonly Dictionary<string, string> RepresentativePayloads = new()
    {
        ["System/CpuInfo"] = """{"CpuTemperature":55,"CpuUsage":12,"CpuFrequency":3200}""",
        ["System/GpuInfo"] = """{"GpuTemperature":61,"GpuUsage":30,"GpuCoreFreq":2100,"GpuMem":1024}""",
        ["System/MemoryInfo"] = """{"MemoryUsage":45,"TotalUsingMemory":9.6}""",
        ["System/FanInfo"] = """{"CpuFanDuty":30,"GpuFanDuty":40,"CpuFanRpm":1800,"GpuFanRpm":2100}""",
        ["System/BatteryInfo"] = """{"BatteryLifePercent":88,"BatteryCycleCount":120,"BatteryAbnormal":false,"BatteryCapacity":"57Wh"}""",
        ["System/NetworkInfo"] = """{"NetworkDownload":"232 Kbps","NetworkUpload":"88 Kbps"}""",
        ["System/HardwareInfo"] = """{"ECVersion":"N.1.32MRO60"}""",
        ["System/FanErrorInfo"] = """{"CpuFanError":false}""",
        ["System/BatteryProtection"] = """{"HealthProtectionStatus":1}""",
        ["Fan/Table"] = BuildCurveJson(),
        ["Fan/Status"] = """
            {"OperatingMode":2,"IsAC":true,"FanBoostEnable":true,"CPU_PL1":45,"CPU_PL2":65,
            "CPU_PL1Minimum":25,"CPU_PL1Maximum":120,"CPU_PL2Minimum":35,"CPU_PL2Maximum":140,
            "CPU_TccOffset":15,"CPU_TccOffsetSwitch":"1","CPU_TccOffsetMinimum":5,"CPU_TccOffsetMaximum":15,
            "TjMax":100,"FAN_TableName":"M3T1","CustomProfileIndex":0,
            "GPU_ConfigurableTGPTarget":140,"GPU_ConfigurableTGPMinimum":60,"GPU_ConfigurableTGPMaximum":175,
            "GPU_DynamicBoostSwitch":"1","GPU_DynamicBoost":10,
            "GPU_CoreClockOffsetOC":150,"GPU_MemoryClockOffsetOC":200,
            "GPU_CoreClockOffsetMinimumHWOC":-500,"GPU_CoreClockOffsetMaximumHWOC":500,
            "GPU_MemoryClockOffsetMinimumHWOC":-500,"GPU_MemoryClockOffsetMaximumHWOC":500,
            "GameWhitelistSwitch":0,"CPU_PerformanceAndOverClockMenuSwitch":1,"OcSupport":true,
            "FanControlRespective":false,"GPU_WhisperModeSupport":true}
            """,
        ["GPUDevice/Status"] = """{"currentHZList":[60,165,240],"currentHZ":240,"DisconnectMonitor":"0","DC_Once":"0","DC_HZ":false,"currentSaveingMode":1}""",
        ["Settings/DeviceSwitchItemStatus"] = """{"ScreenBrightness":80,"TochpadEnable":true,"WIFIEnable":true,"BTEnable":false,"WebCamEnable":true}""",
        ["Setting/Status"] = """
            {"LocalDimmingSwitch":"LOCALDIMMING_ON","LCDOverdriveSwitch":"LCDOverdrive_OFF",
            "LCDOverdriveSupport":"Support","LocalDimmingSupport":"Support",
            "ColorCalibrationSwitch":"ColorCalibrationSwitch_ON","CurrentColorCalibration":"sRGB","ColorCalibrationResultCode":0,
            "CloseTimer":30,"UsbCharger":"USB_CHARGER_STATUS_ON","OSD":"OSD_ON",
            "WinKey":"WINKEY_LOCK","FnKey":"FNKEY_LOCK","NumPad":"NUMPAD_UNLOCK",
            "PowerLightSwitch":"PowerLight_ON","PowerLightBrightness":50,
            "HighPerformancePowerModeSwitch":"HIGH_PERFORMANCE_ON",
            "AcRecoverySwitch_Status":"AC_RECOVERY_ON","DeepSleepSwitch":"DEEPSLEEP_OFF",
            "CopilotKey":"COPILOTKEY_UNLOCK","DeepSleepTime":0,
            "DGpu":"NVIDIA","CheckDGpuStatusforIGpuOnlyOnSuccess":"1",
            "DiscreteGpuDirectConnectionSwitch_Status":"DGPU_DIRECT_ON",
            "IGpuOnlyConnectionSwitch_Status":"IGPU_ONLY_OFF"}
            """,
        ["LCHWOC/Status"] = """{"Support":true,"Enable":false,"GPU_CoreClockOffsetOC":150,"GPU_MemoryClockOffsetOC":200,"OverClockingSwitch":"1","GPU_CoreClockOffsetMinimumHWOC":-500,"GPU_CoreClockOffsetMaximumHWOC":500,"GPU_MemoryClockOffsetMinimumHWOC":-500,"GPU_MemoryClockOffsetMaximumHWOC":500}""",
        ["BT_LC/Status"] = """
            {"connected":true,"ConnectString":"Connected","AutoConnect":true,"LC_action":true,
            "LC_CoolingAuto":true,"LC_MeterNormal":true,"PumpDuty":60,"FanDuty":50,
            "LC_PumpCtrl":1,"LC_FanCtrl":1,"DevFWVersion":"1.0","DevMACString":"AA:BB:CC:DD:EE:FF",
            "DeviceMacList":["AA:BB:CC:DD:EE:FF"],"LCLED_R":255,"LCLED_G":128,"LCLED_B":0,
            "LCLED_RMinimum":0,"LCLED_RMaximum":255,"LCLED_GMinimum":0,"LCLED_GMaximum":255,
            "LCLED_BMinimum":0,"LCLED_BMaximum":255,"LCLED_Mode":1,"LCFanLED_Mode":2}
            """,
        ["Keyboard/Status"] = """{"effect":"Rainbow","light":"3","brightNess":"62.5","speed":"3","direction":"L2R","powerStatus":"On"}""",
        ["HidLightbar/Status"] = """{"type":"1","powerStatus":"On","brightNess":50,"LogoSupport":true,"HingeSupport":false}""",
        ["HidLightbar_Logo/Status"] = """{"type":"1","powerStatus":"Off","brightNess":30}""",
    };

    /// <summary>每个主题在收到合法代表帧之后必然成立的状态断言。</summary>
    static readonly Dictionary<string, Action<MechrevoHw>> RepresentativeAssertions = new()
    {
        ["System/CpuInfo"] = hw => Assert.Equal(55, hw.CpuTemp),
        ["System/BatteryProtection"] = hw => Assert.Equal(1, hw.BatteryProtection),
        ["Fan/Status"] = hw =>
        {
            Assert.Equal(2, hw.OperatingMode);
            Assert.True(hw.Pl1 > 0);
            Assert.True(hw.TccMinimum <= hw.TccMaximum, $"TCC range inverted: {hw.TccMinimum}..{hw.TccMaximum}");
        },
        ["GPUDevice/Status"] = hw => Assert.Equal(240, hw.CurrentHz),
        ["Keyboard/Status"] = hw =>
        {
            Assert.Equal("Rainbow", hw.KeyboardEffect);
            Assert.Equal(3, hw.KeyboardSpeed);
        },
        ["BT_LC/Status"] = hw => Assert.Equal(60, hw.LcPumpDuty),
        ["Fan/Table"] = hw => Assert.Equal(23, hw.CpuCurveDuty[3]),
        ["LCHWOC/Status"] = hw => Assert.Equal(150, hw.GpuCoreClockOffset),
        ["Setting/Status"] = hw =>
        {
            Assert.True(hw.UsbCharger);
            Assert.True(hw.QuickSwitches["winkey"]);    // 勾选=锁定语义：WINKEY_LOCK → true
            Assert.False(hw.QuickSwitches["numpad"]);   // NUMPAD_UNLOCK → false
        },
    };

    static List<(JToken Parent, JToken Leaf)> CollectLeaves(JToken root)
    {
        var leaves = new List<(JToken, JToken)>();
        void Walk(JToken node)
        {
            switch (node)
            {
                case JObject obj:
                    foreach (JProperty property in obj.Properties()) Walk(property.Value);
                    break;
                case JArray array:
                    foreach (JToken item in array) Walk(item);
                    break;
                default:
                    if (node.Parent is not null) leaves.Add((node.Parent, node));
                    break;
            }
        }
        Walk(root);
        return leaves;
    }

    static void AssertStructuralInvariants(MechrevoHw hardware)
    {
        Assert.Equal(16, hardware.CpuCurveUpT.Length);
        Assert.Equal(16, hardware.CpuCurveDuty.Length);
        Assert.Equal(16, hardware.GpuCurveUpT.Length);
        Assert.Equal(16, hardware.GpuCurveDuty.Length);
        Assert.True(hardware.TccMinimum <= hardware.TccMaximum,
            $"TCC range inverted after fuzz: {hardware.TccMinimum}..{hardware.TccMaximum}");
        Assert.True(hardware.KeyboardBrightness is -1 or >= 0 and <= 100,
            $"KeyboardBrightness escaped its domain: {hardware.KeyboardBrightness}");
        // CurrentHz 初始就是 0（还没收到过 GPUDevice/Status），不能当不变量；
        // 解析层显式过滤非正数的是 HzList——它必须在整个 fuzz 过程中保持纯净。
        Assert.True(hardware.HzList.All(value => value > 0),
            $"HzList contained non-positive values: [{string.Join(",", hardware.HzList)}]");
    }

    [Fact]
    public void HandleMessage_ToleratesStructuralGarbage_OnEveryTopic()
    {
        using var hardware = new MechrevoHw();
        List<string> failures = [];
        foreach (string topic in Topics)
        {
            foreach (string payload in BuildGarbagePayloads())
            {
                try
                {
                    hardware.HandleMessage(topic, payload);
                    AssertStructuralInvariants(hardware);
                }
                catch (Exception ex)
                {
                    failures.Add($"{topic} <- {Truncate(payload)}\n    {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        Assert.True(failures.Count == 0, $"{failures.Count} adversarial payloads escaped containment:\n" + string.Join("\n", failures.Take(20)));
    }

    [Fact]
    public void HandleMessage_TypeConfusion_OnEveryLeafValue_StaysContained()
    {
        using var hardware = new MechrevoHw();
        List<string> failures = [];
        List<JToken> scalars = BuildAdversarialScalars();
        foreach ((string topic, string validPayload) in RepresentativePayloads)
        {
            JObject template;
            try { template = JObject.Parse(validPayload); }
            catch (Exception ex) { failures.Add($"test bug: representative payload for {topic} is invalid JSON: {ex.Message}"); continue; }

            foreach ((_, JToken leaf) in CollectLeaves(template))
            {
                foreach (JToken replacement in scalars)
                {
                    JObject mutated = (JObject)template.DeepClone();
                    JToken? target = mutated.SelectToken(leaf.Path);
                    if (target is null) continue;
                    target.Replace(replacement.DeepClone());
                    try
                    {
                        hardware.HandleMessage(topic, mutated.ToString(Newtonsoft.Json.Formatting.None));
                        AssertStructuralInvariants(hardware);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{topic} leaf={leaf.Path} replaced with {replacement.Type}\n    payload={Truncate(mutated.ToString(Newtonsoft.Json.Formatting.None))}\n    {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }
        Assert.True(failures.Count == 0, $"{failures.Count} type-confusion payloads escaped containment:\n" + string.Join("\n", failures.Take(20)));
    }

    /// <summary>坏帧不能永久毒化状态：紧随其后的合法帧必须完整生效。</summary>
    [Fact]
    public void HandleMessage_GarbageDoesNotPoisonTheNextValidFrame()
    {
        using var hardware = new MechrevoHw();
        List<string> failures = [];
        string worst = new('x', 65536);
        foreach ((string topic, string validPayload) in RepresentativePayloads)
        {
            hardware.HandleMessage(topic, "{");
            hardware.HandleMessage(topic, worst);
            hardware.HandleMessage(topic, "[[[[[");
            hardware.HandleMessage(topic, "{\"CPU_TccOffset\":1e400}");
            hardware.HandleMessage(topic, validPayload);
            try
            {
                if (RepresentativeAssertions.TryGetValue(topic, out var assert)) assert(hardware);
                AssertStructuralInvariants(hardware);
            }
            catch (Exception ex)
            {
                failures.Add($"{topic}: valid frame after garbage did not fully apply — {ex.Message}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>解析失败（或解析中途抛出）时，版本号必须停在原地——
    /// 按「版本号变了」轮询状态的调用方会把坏帧当成新状态。</summary>
    [Fact]
    public void HandleMessage_ParseFailureDoesNotBumpVersionCounters()
    {
        using var hardware = new MechrevoHw();
        long keyboardBefore = hardware.KeyboardStatusVersion;

        hardware.HandleMessage("Keyboard/Status", "{ not json");
        Assert.Equal(keyboardBefore, hardware.KeyboardStatusVersion);

        hardware.HandleMessage("Keyboard/Status", """{"effect":"Static"}""");
        Assert.Equal(keyboardBefore + 1, hardware.KeyboardStatusVersion);
    }

    /// <summary>单字段溢出边界：brightNess 带一个 double 能表达但 int 装不下的值。</summary>
    [Fact]
    public void KeyboardStatus_BrightNessBeyondIntRange_StaysClampedOrUnknown()
    {
        using var hardware = new MechrevoHw();
        hardware.HandleMessage("Keyboard/Status", """{"brightNess":"1e300"}""");
        Assert.True(hardware.KeyboardBrightness is -1 or >= 0 and <= 100,
            $"KeyboardBrightness = {hardware.KeyboardBrightness}");
    }

    /// <summary>
    /// brightNess 是小数（百分比），解析必须与 locale 无关：GCU 恒发 "62.5"（点分隔），
    /// 逗号小数文化的机器上若走 CurrentCulture，TryParse 失败会退化到五档旧字段，
    /// 亮度回显静默错档。文件内其余数值解析（Int/Double/ParseOptionalInt）全部显式
    /// InvariantCulture，这一处也必须是。
    /// </summary>
    [Fact]
    public void KeyboardStatus_BrightNessParsesUnderCommaDecimalCulture()
    {
        using var hardware = new MechrevoHw();
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            hardware.HandleMessage("Keyboard/Status", """{"brightNess":"62.5","light":"4"}""");
            Assert.Equal(62, hardware.KeyboardBrightness);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>键名大小写必须无关（文件契约是 GetValue 全部 OrdinalIgnoreCase）。</summary>
    [Fact]
    public void HandleMessage_KeyMatchingIsCaseInsensitive_AfterFuzzedCasing()
    {
        using var hardware = new MechrevoHw();
        hardware.HandleMessage("System/CpuInfo", """{"cPuTeMpErAtUrE":77,"CPUUSAGE":33}""");
        Assert.Equal(77, hardware.CpuTemp);
        Assert.Equal(33, hardware.CpuUsage);

        hardware.HandleMessage("Fan/Status", """{"oPeRaTiNgMoDe":1}""");
        Assert.Equal(1, hardware.OperatingMode);
    }

    /// <summary>深度炸弹必须被解析器自己的深度上限挡下（Newtonsoft 默认 MaxDepth=128），
    /// 不能靠运气：这一条钉住「嵌套再深也只是被拒收」的行为。</summary>
    [Fact]
    public void HandleMessage_DeeplyNestedPayloadsAreRejectedNotFatal()
    {
        using var hardware = new MechrevoHw();
        string arrayBomb = "{\"a\":" + new string('[', 5000) + new string(']', 5000) + "}";
        string objectBomb = string.Concat(Enumerable.Repeat("{\"a\":", 5000)) + "1" + string.Concat(Enumerable.Repeat("}", 5000));
        hardware.HandleMessage("Fan/Status", arrayBomb);
        hardware.HandleMessage("Fan/Status", objectBomb);
        AssertStructuralInvariants(hardware);
    }

    /// <summary>显卡模式状态串的判定面对任意字符串只能产出已知模式，绝不抛异常。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DGPU_DIRECT_ON")]
    [InlineData("igpu_only_on")]
    [InlineData("IGPU_ONLY_CONNECT_RB_AUTO")]
    [InlineData("NOT_SUPPORT")]
    [InlineData("UNKNOWN_GARBAGE")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("\ud83d\ude00")]
    public void ResolveGpuModeStatus_AlwaysProducesAKnownModeOrKeepsCurrent(string? status)
    {
        int resolved = MechrevoHw.ResolveGpuModeStatus(MechrevoService.GpuStandard, status, status);
        Assert.True(resolved is MechrevoService.GpuIGpu or MechrevoService.GpuStandard or MechrevoService.GpuDgpu or MechrevoService.GpuAuto,
            $"ResolveGpuModeStatus produced unknown mode {resolved} for '{status}'");
    }

    /// <summary>数值解析的单一入口对任意文本只能产出 int 或「未知」，绝不抛异常。</summary>
    [Theory]
    [InlineData("2147483647", 2147483647)]
    [InlineData("-2147483648", -2147483648)]
    [InlineData("2147483648", null)]
    [InlineData("-2147483649", null)]
    [InlineData("99999999999999999999", null)]
    [InlineData("1e309", null)]
    [InlineData("NaN", null)]
    [InlineData("Infinity", null)]
    [InlineData("0x10", null)]
    [InlineData(" ", null)]
    [InlineData("", null)]
    [InlineData("62.499", 62)]
    [InlineData("62.500", 62)]
    [InlineData("62.501", 63)]
    public void ParseOptionalInt_AdversarialTextNeverThrows(string? text, int? expected)
    {
        Assert.Equal(expected, MechrevoHw.ParseOptionalInt(text));
    }

    // ---------- 服务层命令构造：SwitchQuick 是 UI → MQTT 的最后一道闸 ----------

    /// <summary>SwitchQuick 对不认识的键必须在发布前拒绝：一个动作名都不许出去。</summary>
    [Fact]
    public async Task SwitchQuick_UnknownKeysAreRejectedWithoutPublishing()
    {
        var published = new List<(string Topic, object Payload)>();
        using var hw = new MechrevoHw((topic, payload) =>
        {
            published.Add((topic, payload));
            return Task.CompletedTask;
        }, capabilities: null, gpuOverclock: null);
        var service = new MechrevoService(hw);

        string[] hostileKeys =
        {
            "", " ", "garbage", "WINKEY", "winkey ", " touchpad", "touchpad;",
            "😀", "cpuadvperf", "gamewhitelist", "fanboost", "usb",
            "lightbar", "logolight", "hingelight", "synclight",
        };
        foreach (string key in hostileKeys)
        {
            Assert.False(await service.SwitchQuick(key, true), $"key '{key}' was accepted");
            Assert.False(await service.SwitchQuick(key, false), $"key '{key}' (off) was accepted");
        }

        Assert.Empty(published);
    }

    /// <summary>
    /// 完整命令环：发布 → 状态回显 → 版本号新鲜度 → 确认。动作名与语义逐字对齐
    /// （winkey 勾选=锁定；osd 的 QuickSwitches 语义是「显示」，命令却发 OSD_HIDDEN_OFF）。
    /// 状态回显在发布回调里同步注入，确认立即完成，不让测试吃几秒的确认超时。
    /// </summary>
    [Fact]
    public async Task SwitchQuick_CommandLoopPublishesTheDocumentedActionAndConfirmsFromStatus()
    {
        var published = new List<(string Topic, Dictionary<string, object> Payload)>();
        MechrevoHw hw = null!;
        hw = new MechrevoHw((topic, payload) =>
        {
            var dict = (Dictionary<string, object>)payload;
            published.Add((topic, dict));
            string action = (string)dict["Action"];
            hw.HandleMessage("Setting/Status", action == "WINKEY_LOCK"
                ? """{"WinKey":"WINKEY_LOCK"}"""
                : """{"OSD":"OSD_HIDDEN_OFF"}""");
            return Task.CompletedTask;
        }, capabilities: null, gpuOverclock: null);
        var service = new MechrevoService(hw);

        // 门禁基线：SupportsQuickSwitch 要求服务端报过该字段（*Seen）。先给一帧
        // 「未锁定」基线，命令发出后的目标状态由发布回调里的回显注入。
        hw.HandleMessage("Setting/Status", """{"WinKey":"WINKEY_UNLOCK","OSD":"OSD_ON"}""");
        Assert.False(hw.QuickSwitches["winkey"]);
        Assert.False(hw.QuickSwitches["osd"]);

        Assert.True(await service.SwitchQuick("winkey", true));
        Assert.True(await service.SwitchQuick("osd", true));
        Assert.True(hw.QuickSwitches["winkey"]);
        Assert.True(hw.QuickSwitches["osd"]);

        string[] actions = published
            .Where(entry => entry.Topic == "Setting/Control")
            .Select(entry => (string)entry.Payload["Action"])
            .ToArray();
        Assert.Contains("WINKEY_LOCK", actions);
        Assert.Contains("OSD_HIDDEN_OFF", actions);
    }

    static string Truncate(string text) =>
        text.Length <= 120 ? text.ReplaceLineEndings("\\n") : text[..120].ReplaceLineEndings("\\n") + "…";
}
