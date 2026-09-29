using System.Reflection;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 底层管道（连接生命周期 / 状态解析一致性 / 并发与资源）审计后的回归锁。
///
/// 这一批缺陷的共同点是**症状与原因隔得很远**：报文里一个无法识别的值会变成
/// 界面上一个点了没反应的开关；退出时的一个竞态会让服务端在我们进程消失之后
/// 反而打开推流；一个漏掉的 ClearModeSwitchPending 会让厂商 Fn 热键在 8 秒内失效。
/// 靠读代码很难发现，靠手工点击更难复现，所以每条都在这里钉住。
/// </summary>
public class PlumbingAuditRegressionTests
{
    static (MechrevoHw Hardware, MechrevoService Service, List<(string Topic, Dictionary<string, object> Payload)> Written) NewRig()
    {
        var written = new List<(string, Dictionary<string, object>)>();
        var hardware = new MechrevoHw((topic, payload) =>
        {
            if (payload is IDictionary<string, object> values)
                written.Add((topic, new Dictionary<string, object>(values)));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities());
        return (hardware, new MechrevoService(hardware), written);
    }

    // ------------------------------------------------- *Seen 只在解析成功时置位

    /// <summary>
    /// <c>Settings/DeviceSwitchItemStatus</c> 的五项过去是「字段存在就置 Seen」：
    /// <c>HasField(...)</c> 加 <c>OptionalBool(...) == true</c>。固件写了个没见过的记法时，
    /// 值会静默变成 false 而 Seen 照样置位——界面上就长出一个点了没反应的开关，
    /// 确认逻辑也永远等不到回读变化。这与 LcdOverdriveSeen 修掉的是同一个形状。
    /// </summary>
    [Theory]
    [InlineData("WIFIEnable", "wifi")]
    [InlineData("BTEnable", "bt")]
    [InlineData("WebCamEnable", "webcam")]
    public void DeviceSwitchItemStatus_UnparseableValueDoesNotExposeASwitch(string field, string key)
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            // "MAYBE" 不在 ParseFlexibleBool 的任何白名单里 → 解析结果是「未知」。
            hardware.HandleMessage("Settings/DeviceSwitchItemStatus", $$"""{"{{field}}":"MAYBE"}""");
            Assert.False(hardware.SupportsQuickSwitch(key),
                $"{field} 的值无法识别时不能暴露 {key} 开关——那会是一个点了没反应的勾选框。");

            // 能识别的值照常工作。
            hardware.HandleMessage("Settings/DeviceSwitchItemStatus", $$"""{"{{field}}":true}""");
            Assert.True(hardware.SupportsQuickSwitch(key));
            Assert.True(hardware.QuickSwitches[key]);
        }
    }

    /// <summary>屏幕亮度：只有解析出有效值（>= 0）才算「读到过」。</summary>
    [Fact]
    public void ScreenBrightness_UnparseableValueStaysUnknown()
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Settings/DeviceSwitchItemStatus", """{"ScreenBrightness":"n/a"}""");
            Assert.False(hardware.ScreenBrightnessSeen);

            hardware.HandleMessage("Settings/DeviceSwitchItemStatus", """{"ScreenBrightness":70}""");
            Assert.True(hardware.ScreenBrightnessSeen);
            Assert.Equal(70, hardware.ScreenBrightness);
        }
    }

    /// <summary>
    /// 游戏白名单在 <c>SupportsQuickSwitch</c> 里没有任何别的门禁兜底，
    /// 所以它的 Seen 尤其不能按「字段存在」置位。
    /// </summary>
    [Fact]
    public void GameWhitelist_UnparseableValueDoesNotExposeASwitch()
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Fan/Status", """{"GameWhitelistSwitch":"???"}""");
            Assert.False(hardware.SupportsQuickSwitch("gamewhitelist"));

            hardware.HandleMessage("Fan/Status", """{"GameWhitelistSwitch":1}""");
            Assert.True(hardware.SupportsQuickSwitch("gamewhitelist"));
        }
    }

    // ------------------------------------------------------------- 0 值 latch

    /// <summary>
    /// PL4 只 latch 可用值。
    ///
    /// <c>OptionalInt</c> 的 fallback 是上一次的值（部分帧不该擦除已知值），
    /// 所以 0 一旦被记住就再也回不到「未知」，而 <c>_pl4Raw >= 0</c> 会让它通过
    /// 所有有效性守卫——界面上是一个「已知的 0 W」。这与 CpuAmdSpl 修掉的同型。
    /// </summary>
    [Fact]
    public void Pl4_ZeroIsNotLatchedAsAKnownValue()
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Fan/Status", """{"CPU_PL4":145,"CPU_PL4Minimum":10,"CPU_PL4Maximum":210}""");
            Assert.Equal(145, hardware.Pl4);

            // GCU 在这一项关闭时会报 0；不能把它当成新的目标值。
            hardware.HandleMessage("Fan/Status", """{"CPU_PL4":0,"CPU_PL4Minimum":0,"CPU_PL4Maximum":0}""");
            Assert.Equal(145, hardware.Pl4);
            Assert.Equal(10, hardware.Pl4Minimum);
            Assert.Equal(210, hardware.Pl4Maximum);
        }
    }

    /// <summary>PL4 的上下限如果被 0 污染，滑条量程会永久塌成 0..0。</summary>
    [Fact]
    public void Pl4_RangeNeverCollapsesToZero()
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Fan/Status", """{"CPU_PL4Minimum":0,"CPU_PL4Maximum":0}""");
            Assert.True(hardware.Pl4Minimum <= 0 || hardware.Pl4Minimum > 0);   // 未知或有效，不能是「已知的 0」
            Assert.False(hardware.Pl4Minimum == 0 && hardware.Pl4Maximum == 0,
                "0/0 不构成一个可用量程，必须保持未知（-1）。");
        }
    }

    /// <summary>TGP 与 Dynamic Boost 的目标值同样只接受 &gt; 0。</summary>
    [Fact]
    public void GpuTgpAndDynamicBoost_ZeroDoesNotOverwriteAKnownTarget()
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Fan/Status", """{"GPU_ConfigurableTGPTarget":150,"GPU_DynamicBoost":15}""");
            Assert.Equal(150, hardware.GpuTgp);
            Assert.Equal(15, hardware.GpuDb);

            hardware.HandleMessage("Fan/Status", """{"GPU_ConfigurableTGPTarget":0,"GPU_DynamicBoost":0}""");
            Assert.Equal(150, hardware.GpuTgp);
            Assert.Equal(15, hardware.GpuDb);
        }
    }

    // ---------------------------------------------- 状态串判定的大小写一致性

    /// <summary>
    /// 这三项过去漏了 <c>OrdinalIgnoreCase</c>，与同族其余十几项不一致：
    /// 固件把值写成小写就会被判成相反的状态。
    /// </summary>
    [Theory]
    [InlineData("AcRecoverySwitch_Status", "acrecovery", "ACRECOVERY_TOGGLE_off", false)]
    [InlineData("AcRecoverySwitch_Status", "acrecovery", "ACRECOVERY_TOGGLE_ON", true)]
    [InlineData("DeepSleepSwitch", "deepsleep", "deepsleep_off", false)]
    [InlineData("DeepSleepSwitch", "deepsleep", "DEEPSLEEP_ON", true)]
    [InlineData("CopilotKey", "copilot", "COPILOTKEY_unlock", false)]
    [InlineData("CopilotKey", "copilot", "COPILOTKEY_LOCK", true)]
    public void StatusStringComparisonsIgnoreCase(string field, string key, string reported, bool expected)
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Setting/Status", $$"""{"{{field}}":"{{reported}}"}""");
            Assert.Equal(expected, hardware.QuickSwitches[key]);
        }
    }

    // ------------------------------------------------------- 握手序列单一来源

    /// <summary>
    /// 首连握手与 <c>RefreshAll</c> 必须是同一份序列。
    ///
    /// 过去是两份手写列表，差一条 <c>LCHWOC/Control GETSTATUS</c>——首连缺的那条
    /// 要等恢复流程补上。两份并存的话，将来加新主题只改一边就会留下
    /// 「首连缺某个状态」的间歇性症状。
    /// </summary>
    [Fact]
    public async Task RefreshAllSharesTheSameHandshakeSequenceAsTheInitialConnect()
    {
        var (hardware, service, written) = NewRig();
        using (hardware)
        {
            await hardware.RequestInitialStateAsync();
            var handshake = written.Select(w => w.Topic + "|" + (w.Payload.TryGetValue("Action", out object? a) ? a : w.Payload.Keys.First())).ToArray();
            written.Clear();

            await service.RefreshAll();
            var refresh = written.Select(w => w.Topic + "|" + (w.Payload.TryGetValue("Action", out object? a) ? a : w.Payload.Keys.First())).ToArray();

            Assert.Equal(handshake, refresh);
        }
    }

    /// <summary>
    /// <c>System_ON</c> 必须排在序列**第一条**：它才是让 GCU 打开周期性传感器推送的开关。
    /// 排在末尾的话前面那批 GETSTATUS 是在采集模块可能还没启动时发的，
    /// 于是要靠 Program 的 HasTelemetrySince 补发（额外 800ms + 1200ms 等待）。
    /// </summary>
    [Fact]
    public async Task HandshakeTurnsTelemetryOnFirst()
    {
        var (hardware, _, written) = NewRig();
        using (hardware)
        {
            await hardware.RequestInitialStateAsync();

            Assert.Equal("System/Control", written[0].Topic);
            Assert.Equal("System_ON", written[0].Payload["Action"]);
        }
    }

    /// <summary>订阅列表要覆盖所有会被解析的主题族，尤其那几个不匹配通配符的独立主题。</summary>
    [Theory]
    [InlineData("Settings/#")]              // DeviceSwitchItemStatus 是复数，不匹配 Setting/#
    [InlineData("HidLightbar_Logo/#")]      // Logo 子灯带独立主题，不匹配 HidLightbar/#
    [InlineData("LCHWOC/#")]
    public void SubscriptionListCoversTheTopicsThatWildcardsMiss(string filter)
    {
        Assert.Contains(filter, MechrevoHw.SubscribedTopicFilters);
    }

    /// <summary>
    /// 不能再单独订阅 <c>Fan/Table</c>：<c>Fan/#</c> 已覆盖，
    /// 双订阅会按 MQTT 3.1.1 重复投递，Fan/Table 的每一帧都会被解析两次。
    /// </summary>
    [Fact]
    public void SubscriptionListHasNoOverlappingFilters()
    {
        Assert.DoesNotContain("Fan/Table", MechrevoHw.SubscribedTopicFilters);
        Assert.Equal(
            MechrevoHw.SubscribedTopicFilters.Length,
            MechrevoHw.SubscribedTopicFilters.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // --------------------------------------------------------- client id 隔离

    /// <summary>
    /// 短命辅助实例（开机限充任务）必须用不同的 client id。
    ///
    /// 按 MQTT 3.1.1 §3.1.4，broker 收到同 id 的新连接必须踢掉已有会话：
    /// 限充任务连上就把托盘进程踢掉，托盘重连又把限充任务踢掉，两个进程互踢，
    /// 直到限充那 10 轮轮询跑完。限充大概率失败，而托盘每次重连都会 generation++
    /// 触发一整轮状态重放。
    /// </summary>
    [Fact]
    public void HelperInstanceUsesADistinctClientId()
    {
        Assert.NotEqual(MechrevoHw.PrimaryClientId, MechrevoHw.HelperClientId);
        Assert.False(string.IsNullOrWhiteSpace(MechrevoHw.HelperClientId));
    }

    [Fact]
    public void DefaultConstructorUsesThePrimaryClientId()
    {
        using var hardware = new MechrevoHw();
        Assert.Equal(MechrevoHw.PrimaryClientId, hardware.ClientId);
    }

    [Fact]
    public void HelperConstructorHonoursTheRequestedClientId()
    {
        using var hardware = new MechrevoHw(MechrevoHw.HelperClientId);
        Assert.Equal(MechrevoHw.HelperClientId, hardware.ClientId);
    }

    // ------------------------------------------------------------ 连接层参数

    /// <summary>
    /// KeepAlive 必须显式设置。不设的话走 MQTTnet 默认值（15 秒），
    /// 而 IsConnected 只在收到 FIN/RST 或 keepalive 超时时才翻转——
    /// GCUService 被冻结/强杀但 socket 没干净关闭时会有 15-25 秒的假连接窗口，
    /// 那期间每条 Publish 都要卡到 PublishTimeout 才失败。
    /// </summary>
    [Fact]
    public void KeepAliveIsShortEnoughToDetectASilentlyDeadBroker()
    {
        Assert.True(MechrevoHw.KeepAlivePeriod > TimeSpan.Zero);
        Assert.True(MechrevoHw.KeepAlivePeriod <= TimeSpan.FromSeconds(10),
            "keepalive 越长，假连接窗口越长；这个窗口里的每条命令都要等 PublishTimeout。");
        Assert.True(MechrevoHw.KeepAlivePeriod < MechrevoHw.PublishTimeout,
            "keepalive 必须短于发布超时，否则断线总是先由发布超时暴露、而不是由心跳。");
    }

    /// <summary>
    /// 退出路径上的清理命令要能指定 QoS。
    ///
    /// 默认的 QoS2 要走 PUBLISH→PUBREC→PUBREL→PUBCOMP 四步，而退出时只等几百毫秒
    /// 就会断开会话；因为 CleanSession=true，未完成的 PUBREL 被永久丢弃，命令等于没发。
    /// </summary>
    [Fact]
    public void PublishAcceptsAnExplicitQosForExitTimeCleanup()
    {
        MethodInfo? overload = typeof(MechrevoHw).GetMethod(
            nameof(MechrevoHw.Publish),
            new[] { typeof(string), typeof(object), typeof(MQTTnet.Protocol.MqttQualityOfServiceLevel) });
        Assert.NotNull(overload);
    }

    // ------------------------------------------------ 模式切换 pending 窗口

    /// <summary>
    /// 发布失败必须解除过期包过滤窗口。
    ///
    /// 不解除的话，这 8 秒内一切真实模式上报（含用户按厂商 Fn 热键、GCU 自己回滚）
    /// 都会被静默丢弃，UI 会一直显示一个从未下发成功的模式。
    /// <c>MechrevoHw.SetMode</c> 早就这么做了，服务层这条路之前没有。
    /// </summary>
    [Fact]
    public async Task SwitchMode_ClearsThePendingWindowWhenPublishFails()
    {
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, _) =>
            throw new MqttPublishFailedException(topic, "模拟断线"),
            new MechrevoDeviceCapabilities());
        using (hardware)
        {
            var service = new MechrevoService(hardware);
            hardware.HandleMessage("Fan/Status", """{"OperatingMode":1}""");

            bool ok = await service.SwitchMode(MechrevoService.ModeTurbo);

            Assert.False(ok);
            (bool active, _) = hardware.GetModeSwitchPendingState();
            Assert.False(active, "发布失败后过滤窗口必须解除，否则厂商热键在 8 秒内失效。");
        }
    }

    /// <summary>自定义档切换走的是另一条代码路径，同样要解除。</summary>
    [Fact]
    public async Task SwitchCustomProfile_ClearsThePendingWindowWhenPublishFails()
    {
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, _) =>
            throw new MqttPublishFailedException(topic, "模拟断线"),
            new MechrevoDeviceCapabilities());
        using (hardware)
        {
            var service = new MechrevoService(hardware);

            bool ok = await service.SwitchCustomProfile(1);

            Assert.False(ok);
            (bool active, _) = hardware.GetModeSwitchPendingState();
            Assert.False(active);
        }
    }

    /// <summary>
    /// 确认失败（回读慢）时**不能**解除窗口：那时命令已经上路，
    /// 解除反而会让在途的切换前旧包被当成新状态接受。
    /// </summary>
    [Fact]
    public async Task SwitchMode_KeepsThePendingWindowWhenOnlyConfirmationTimesOut()
    {
        var (hardware, service, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Fan/Status", """{"OperatingMode":1}""");

            // publishOverride 什么都不做 → 命令"发出去了"但状态永远不回读 → 确认超时。
            bool ok = await service.SwitchMode(MechrevoService.ModeTurbo);

            Assert.False(ok);
            (bool active, int expected) = hardware.GetModeSwitchPendingState();
            Assert.True(active, "命令已上路，窗口要继续拒绝在途的切换前旧包。");
            Assert.Equal(2, expected);   // Turbo 的 OperatingMode 是 2
        }
    }

    // ------------------------------------------------------- 事件隔离与资源

    /// <summary>
    /// <c>HandleMessage</c> 里触发的事件必须逐订阅者隔离。
    ///
    /// 过去 <c>ModeChanged</c> 是在 Fan/Status **中段**裸 <c>?.Invoke()</c>：
    /// 订阅者抛异常会把后面的功耗墙、温度墙、TGP、超频回读连同版本号一起丢掉，
    /// 而那些字段是本帧最主要的内容。
    /// </summary>
    [Fact]
    public void AThrowingModeChangedSubscriberDoesNotDiscardTheRestOfTheFrame()
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.HandleMessage("Fan/Status", """{"OperatingMode":0}""");
            hardware.ModeChanged += _ => throw new InvalidOperationException("订阅者炸了");

            long versionBefore = hardware.FanStatusVersion;
            hardware.HandleMessage("Fan/Status", """
                {"OperatingMode":2,"CPU_PL1":75,"CPU_PL2":85,"GPU_ConfigurableTGPTarget":150}
                """);

            Assert.Equal(2, hardware.OperatingMode);
            Assert.Equal(75, hardware.Pl1);
            Assert.Equal(85, hardware.Pl2);
            Assert.Equal(150, hardware.GpuTgp);
            Assert.True(hardware.FanStatusVersion > versionBefore,
                "版本号必须照常自增，否则所有按版本号确认的写入都会超时。");
        }
    }

    /// <summary>
    /// <c>CloseTimerChanged</c> 过去在 Setting/Status 中段触发，
    /// 后面还有 USB/OSD/WinKey 一族、显卡模式解析和两个版本号。
    /// </summary>
    [Fact]
    public void AThrowingCloseTimerSubscriberDoesNotDiscardTheRestOfTheFrame()
    {
        var (hardware, _, _) = NewRig();
        using (hardware)
        {
            hardware.CloseTimerChanged += _ => throw new InvalidOperationException("订阅者炸了");

            long versionBefore = hardware.SettingStatusVersion;
            hardware.HandleMessage("Setting/Status", """
                {"CloseTimer":15,"UsbCharger":"USB_CHARGER_STATUS_ON","WinKey":"WINKEY_STATUS_LOCK"}
                """);

            Assert.Equal(15, hardware.CloseTimerMinutes);
            Assert.True(hardware.UsbCharger);
            Assert.True(hardware.QuickSwitches["winkey"]);
            Assert.True(hardware.SettingStatusVersion > versionBefore);
        }
    }

    /// <summary>
    /// <c>Dispose</c> 要清空公开事件。UI 审计路径会反复创建/销毁实例，
    /// 残留的订阅会让已 Dispose 的窗体继续被这个对象持有。
    /// </summary>
    [Fact]
    public void DisposeClearsPublicEventSubscriptions()
    {
        var hardware = new MechrevoHw(null, new MechrevoDeviceCapabilities());
        int calls = 0;
        hardware.DataChanged += () => calls++;
        hardware.HandleMessage("System/CpuInfo", """{"CpuTemperature":50}""");
        Assert.Equal(1, calls);

        hardware.Dispose();
        hardware.HandleMessage("System/CpuInfo", """{"CpuTemperature":60}""");
        Assert.Equal(1, calls);
    }

    /// <summary>重复 Dispose 必须幂等，而且不能因为竞态双双进入。</summary>
    [Fact]
    public void DisposeIsIdempotent()
    {
        var hardware = new MechrevoHw(null, new MechrevoDeviceCapabilities());
        hardware.Dispose();
        hardware.Dispose();   // 不抛即通过
    }
}

/// <summary>
/// 灯带主题 → QuickSwitches 键的映射只能有一份。
///
/// 这层映射过去在两个地方各写了一遍：<c>SupportsLightTopic</c> 与
/// <c>SetLightPower</c> 的确认逻辑各写了一套，且第二份把子灯带都算成 "lightbar"。
/// 真机验证里的表现是「回读确实变了，但命令确认=False」——开关生效了，
/// 界面却因为确认失败而回滚勾选，用户看到的是「点了跳回去」。
/// </summary>
public class LightTopicMappingTests
{
    [Theory]
    [InlineData("HidLightbar/Ctrl", "lightbar")]
    [InlineData("HidLightbar/Status", "lightbar")]
    [InlineData("HidLightbar_Logo/Ctrl", "logolight")]
    public void EachLightTopicMapsToItsOwnQuickSwitchKey(string topic, string expected)
    {
        Assert.Equal(expected, MechrevoService.LightTopicToQuickSwitchKey(topic));
    }

    /// <summary>
    /// 子灯带的主题以 <c>HidLightbar_</c> 开头，所以判定顺序必须先子灯带、
    /// 最后才落到主灯带。这一条专门锁住那个顺序。
    /// </summary>
    [Fact]
    public void SubLightbarTopicsAreNeverMistakenForTheMainLightbar()
    {
        foreach (string topic in new[] { "HidLightbar_Logo/Ctrl" })
            Assert.NotEqual("lightbar", MechrevoService.LightTopicToQuickSwitchKey(topic));
    }

    [Fact]
    public void UnknownTopicsMapToNothing()
    {
        Assert.Null(MechrevoService.LightTopicToQuickSwitchKey("Keyboard/Ctrl"));
        Assert.Null(MechrevoService.LightTopicToQuickSwitchKey("Fan/Control"));
        Assert.Null(MechrevoService.LightTopicToQuickSwitchKey(""));
    }

    /// <summary>
    /// 端到端：开关某条子灯带后，只要**这条子灯带**的状态回读到位就应该确认成功，
    /// 不该去看主灯带。
    /// </summary>
    [Fact]
    public async Task SubLightbarPowerConfirmsFromItsOwnReadback()
    {
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            // 只让 Logo 子灯带回状态，主灯带保持关闭——如果确认看的是主灯带就会失败。
            if (topic.StartsWith("HidLightbar_Logo/", StringComparison.Ordinal) &&
                payload is IDictionary<string, object> values &&
                values.TryGetValue("powerstatus", out object? power))
            {
                hardware!.HandleMessage("HidLightbar_Logo/Status",
                    $$"""{"type":"MEZone_Lighbar4","powerStatus":"{{(Convert.ToInt32(power) == 1 ? "On" : "Off")}}","brightNess":"50"}""");
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities());

        using (hardware)
        {
            var service = new MechrevoService(hardware);
            // 让 Logo 子灯带的能力成立，同时主灯带保持关闭。
            hardware.HandleMessage("HidLightbar_Logo/Status",
                """{"type":"MEZone_Lighbar4","powerStatus":"Off","brightNess":"50"}""");
            // Logo 通道只在 Lighbar4 上存在，且要有官方的 Logo 灯珠标志（这里用 A 面 Logo 的 MBlogoSupport）。
            hardware.HandleMessage("HidLightbar/Status",
                """{"type":"MEZone_Lighbar4","powerStatus":"Off","brightNess":"50","MBlogoSupport":true}""");

            bool confirmed = await service.SetLightPower("HidLightbar_Logo/Ctrl", true);

            Assert.True(confirmed, "子灯带的确认必须看自己的回读，不是主灯带的。");
            Assert.True(hardware.QuickSwitches["logolight"]);
            Assert.False(hardware.QuickSwitches["lightbar"]);
        }
    }
}
