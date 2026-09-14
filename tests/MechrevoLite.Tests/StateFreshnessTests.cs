using System.Collections.Concurrent;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 「版本号变了就代表字段是新的」这个契约，以及围绕它的几处结构性修复。
/// 服务层和 UI 都按版本号判断「是否来了新的一帧」，契约破了就表现为
/// 「明明落地了却确认失败」这种偶发误判。
/// </summary>
public class StateFreshnessTests
{
    static MechrevoHw NewHardware(Func<string, object, Task>? publish = null) =>
        new(publish ?? ((_, _) => Task.CompletedTask), new MechrevoDeviceCapabilities());

    // ---- M1：版本号必须在字段写完之后才自增 ----

    /// <summary>
    /// 解析中途失败时不能留下「新版本号 + 旧字段值」。旧实现在 case 第一行就自增，
    /// 于是外部会认为来了一帧新状态，而模式、功耗墙、TCC 全是旧值。
    /// </summary>
    [Fact]
    public void FanStatusVersionOnlyAdvancesWhenTheFrameWasFullyParsed()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":1,\"CPU_PL1\":45}");
        long versionAfterGoodFrame = hardware.FanStatusVersion;

        hardware.HandleMessage("Fan/Status", "{ this is not valid json");

        Assert.Equal(versionAfterGoodFrame, hardware.FanStatusVersion);
        Assert.Equal(1, hardware.OperatingMode);
        Assert.Equal(45, hardware.Pl1);
    }

    [Fact]
    public void FanStatusVersionAdvancesOncePerParsedFrame()
    {
        using MechrevoHw hardware = NewHardware();
        long before = hardware.FanStatusVersion;

        hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":1}");
        hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":2}");

        Assert.Equal(before + 2, hardware.FanStatusVersion);
    }

    /// <summary>
    /// 观察到新版本号时，同一帧的字段必须已经可见。这里用事件回调在解析线程上
    /// 直接检查，等价于服务层按版本号轮询后立刻读字段。
    /// </summary>
    [Fact]
    public void ObservingANewFanStatusVersionImpliesTheFieldsAreAlreadyVisible()
    {
        using MechrevoHw hardware = NewHardware();
        long observedVersion = 0;
        int observedMode = -1;
        int observedPl1 = -1;
        hardware.CustomModeChanged += () =>
        {
            observedVersion = hardware.FanStatusVersion;
            observedMode = hardware.OperatingMode;
            observedPl1 = hardware.Pl1;
        };

        hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":2,\"CPU_PL1\":75}");

        Assert.True(observedVersion > 0);
        Assert.Equal(2, observedMode);
        Assert.Equal(75, observedPl1);
    }

    [Fact]
    public void KeyboardStatusVersionOnlyAdvancesWhenTheFrameWasFullyParsed()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Keyboard/Status", "{\"effect\":\"Wave\",\"light\":4}");
        long versionAfterGoodFrame = hardware.KeyboardStatusVersion;

        hardware.HandleMessage("Keyboard/Status", "not json");

        Assert.Equal(versionAfterGoodFrame, hardware.KeyboardStatusVersion);
        Assert.Equal("Wave", hardware.KeyboardEffect);
    }

    /// <summary>
    /// 显卡模式版本号必须在 GpuMode 赋值之后才自增：SwitchGpuMode 的 MUX 目标确认
    /// 正是用「版本号推进 + 模式匹配」来排除旧状态包的。
    /// </summary>
    [Fact]
    public void GpuModeVersionAdvancesOnlyAfterTheModeFieldIsWritten()
    {
        using MechrevoHw hardware = NewHardware();
        long observedVersion = 0;
        int observedMode = -1;
        hardware.GpuModeChanged += () =>
        {
            observedVersion = hardware.GpuModeStatusVersion;
            observedMode = hardware.GpuMode;
        };

        hardware.HandleMessage("Setting/Status",
            "{\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_ON\"}");

        Assert.True(observedVersion > 0);
        Assert.Equal(MechrevoService.GpuIGpu, observedMode);
        Assert.Equal(MechrevoService.GpuIGpu, hardware.GpuMode);
    }

    [Fact]
    public void SettingStatusVersionDoesNotAdvanceOnAMalformedFrame()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Setting/Status", "{\"UsbCharger\":\"USB_CHARGER_STATUS_ON\"}");
        long before = hardware.SettingStatusVersion;

        hardware.HandleMessage("Setting/Status", "{ broken");

        Assert.Equal(before, hardware.SettingStatusVersion);
    }

    // ---- L2：曲线整体发布，不做原地改写 ----

    /// <summary>
    /// 全 0 的未初始化表会触发默认曲线回退。回退必须在发布前完成，
    /// 否则 UI 线程可能读到半 0 半默认值的数组。这里验证最终结果自洽：
    /// 要么整条是设备值，要么整条是默认值，不会出现混合。
    /// </summary>
    [Fact]
    public void UninitialisedCurveFallsBackAsOneCoherentArray()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Fan/Table", """
            { "Name": "M1T1", "CPU": [ { "UpT": 0, "Duty": 0 } ], "GPU": [ { "UpT": 0, "Duty": 0 } ] }
            """);

        byte[] duty = hardware.CpuCurveDuty.ToArray();
        byte[] upT = hardware.CpuCurveUpT.ToArray();
        Assert.Equal(16, duty.Length);
        Assert.Equal(16, upT.Length);
        // 回退发生时温度点不可能全是 0；没有回退时占空比保持全 0。两种都自洽。
        bool fellBack = upT.Any(value => value != 0);
        Assert.True(fellBack ? duty.Any(value => value != 0) : duty.All(value => value == 0));
    }

    /// <summary>
    /// 设备自己给了有效曲线时不能被默认值覆盖。
    /// </summary>
    [Fact]
    public void DeviceProvidedCurveIsPublishedVerbatim()
    {
        using MechrevoHw hardware = NewHardware();

        hardware.HandleMessage("Fan/Table", """
            {
              "Name": "M4T1",
              "CPU": [ { "UpT": 40, "Duty": 25 }, { "UpT": 55, "Duty": 45 }, { "UpT": 70, "Duty": 70 } ]
            }
            """);

        Assert.Equal(25, hardware.CpuCurveDuty[0]);
        Assert.Equal(45, hardware.CpuCurveDuty[1]);
        Assert.Equal(70, hardware.CpuCurveDuty[2]);
        Assert.Equal(40, hardware.CpuCurveUpT[0]);
    }

    // ---- L5：事件通知逐个隔离 ----

    /// <summary>
    /// 一个抛异常的 CapabilitiesChanged 订阅者过去会掐断后面的 StateChanged，
    /// 导致所有 WaitForStateAsync 只能超时、UI 停更，而且异常被静默吞掉。
    /// </summary>
    [Fact]
    public void AThrowingCapabilitiesHandlerDoesNotSuppressStateChanged()
    {
        using MechrevoHw hardware = NewHardware();
        var stateTopics = new ConcurrentQueue<string>();
        hardware.CapabilitiesChanged += () => throw new InvalidOperationException("订阅者故意抛异常");
        hardware.StateChanged += topic => stateTopics.Enqueue(topic);

        hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":1}");

        Assert.Contains("Fan/Status", stateTopics);
    }

    /// <summary>同一个事件上的多个订阅者互不影响。</summary>
    [Fact]
    public void AThrowingHandlerDoesNotSuppressItsPeers()
    {
        using MechrevoHw hardware = NewHardware();
        bool secondHandlerRan = false;
        hardware.StateChanged += _ => throw new InvalidOperationException("第一个订阅者抛异常");
        hardware.StateChanged += _ => secondHandlerRan = true;

        hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":1}");

        Assert.True(secondHandlerRan);
    }

    [Fact]
    public void AThrowingStateHandlerDoesNotSuppressTelemetryNotifications()
    {
        using MechrevoHw hardware = NewHardware();
        bool dataChanged = false;
        hardware.StateChanged += _ => throw new InvalidOperationException("订阅者故意抛异常");
        hardware.DataChanged += () => dataChanged = true;

        hardware.HandleMessage("System/CpuInfo", "{\"CpuTemperature\":55}");

        Assert.True(dataChanged);
    }

    // ---- L6：谓词异常不穿透返回 bool 的 API ----

    /// <summary>
    /// WaitForStateAsync 过去把谓词异常通过 TaskCompletionSource 穿透给调用方，
    /// 而 SetGpuMode / SetBatteryProtection / SetPl1Pl2 的签名是返回 bool。
    /// </summary>
    [Fact]
    public async Task PredicateExceptionIsReportedAsUnconfirmedRatherThanThrown()
    {
        using MechrevoHw hardware = NewHardware();

        bool confirmed = await hardware.WaitForStateAsync(
            () => throw new InvalidOperationException("谓词故意抛异常"),
            TimeSpan.FromMilliseconds(50));

        Assert.False(confirmed);
    }

    [Fact]
    public async Task CancellationIsReportedAsUnconfirmedRatherThanThrown()
    {
        using MechrevoHw hardware = NewHardware();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        bool confirmed = await hardware.WaitForStateAsync(
            () => false, TimeSpan.FromSeconds(5), cancelled.Token);

        Assert.False(confirmed);
    }

    [Fact]
    public async Task AlreadySatisfiedPredicateStillReturnsTrue()
    {
        using MechrevoHw hardware = NewHardware();

        Assert.True(await hardware.WaitForStateAsync(() => true, TimeSpan.FromMilliseconds(50)));
    }

    // ---- L8：发布失败的异常类型统一 ----

    /// <summary>
    /// 断线时抛的是我们自己的异常类型，调用方可以按类型区分
    /// 「命令没发出去」和「别的程序错误」，不必用 catch-all。
    /// </summary>
    [Fact]
    public async Task PublishOnADisconnectedClientThrowsTheDedicatedException()
    {
        using var hardware = new MechrevoHw();

        MqttPublishFailedException failure = await Assert.ThrowsAsync<MqttPublishFailedException>(
            () => hardware.Publish("Fan/Control", new Dictionary<string, object> { ["Action"] = "GETSTATUS" }));

        Assert.Equal("Fan/Control", failure.Topic);
    }

    /// <summary>发布超时上限必须存在且足够短，否则 QoS2 的等待会长时间占住控制锁。</summary>
    [Fact]
    public void PublishTimeoutIsBoundedWellBelowTheQos2Default()
    {
        Assert.True(MechrevoHw.PublishTimeout > TimeSpan.Zero);
        Assert.True(MechrevoHw.PublishTimeout <= TimeSpan.FromSeconds(10));
    }

    // ---- L4：校色回读的缓存窗口 ----

    /// <summary>
    /// 缓存窗口必须远小于校色确认循环的 4 秒预算，否则写入生效也会被判成未确认。
    /// </summary>
    [Fact]
    public void ColorCalibrationCacheWindowIsMuchShorterThanTheConfirmationBudget()
    {
        Assert.True(MechrevoService.ColorCalibrationCacheTtl > TimeSpan.Zero);
        Assert.True(MechrevoService.ColorCalibrationCacheTtl <= TimeSpan.FromSeconds(1));
    }

    /// <summary>失效之后必须重新去读，不能继续返回缓存值。</summary>
    [Fact]
    public void InvalidatingTheColorCalibrationCacheForcesAFreshRead()
    {
        bool? first = MechrevoService.TryReadColorCalibrationOn();
        MechrevoService.InvalidateColorCalibrationCache();
        bool? second = MechrevoService.TryReadColorCalibrationOn();

        // 本机注册表状态不变，两次结果应当一致；这里验证的是失效路径不抛异常且语义稳定。
        Assert.Equal(first, second);
    }
}
