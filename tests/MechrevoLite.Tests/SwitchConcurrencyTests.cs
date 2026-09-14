using System.Collections.Concurrent;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 切换操作的并发与生命周期。这里的失败模式都是「UI 看起来没反应」或
/// 「显示一个从未真正下发成功的状态」，不会有异常抛到用户面前。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class SwitchConcurrencyTests
{
    static MechrevoHw NewHardware(Func<string, object, Task>? publish = null) =>
        new(publish ?? ((_, _) => Task.CompletedTask), new MechrevoDeviceCapabilities());

    // ---- M2：模式切换的过期包过滤窗口 ----

    /// <summary>
    /// 期望模式与截止时刻必须原子地一起读出。过去它们是两个独立字段，
    /// 连点两个模式按钮时 MQTT 线程会读到「旧期望 + 新截止」的组合。
    /// </summary>
    [Fact]
    public void PendingWindowExposesTheExpectedModeAndDeadlineAsOneAtomicPair()
    {
        using MechrevoHw hardware = NewHardware();

        Assert.Equal((false, -1), hardware.GetModeSwitchPendingState());

        hardware.MarkModeSwitchPending(2);
        Assert.Equal((true, 2), hardware.GetModeSwitchPendingState());

        hardware.MarkModeSwitchPending(0);
        Assert.Equal((true, 0), hardware.GetModeSwitchPendingState());

        hardware.ClearModeSwitchPending();
        Assert.Equal((false, -1), hardware.GetModeSwitchPendingState());
    }

    /// <summary>
    /// 武装期间，与期望模式不一致的上报被当作过期包丢弃——这是有意保留的行为。
    /// </summary>
    [Fact]
    public void ArmedWindowRejectsAModeReportThatDoesNotMatchTheExpectedTarget()
    {
        using MechrevoHw hardware = NewHardware();
        hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":1}");
        hardware.MarkModeSwitchPending(2);

        hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":0}");

        Assert.Equal(1, hardware.OperatingMode);

        hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":2}");
        Assert.Equal(2, hardware.OperatingMode);
    }

    /// <summary>
    /// M2 主回归：命令没能发出去时必须解除武装，否则接下来 8 秒内一切真实模式上报
    /// （含用户按厂商 Fn 热键、GCU 回滚）都被静默丢弃，UI 显示一个从未生效的模式。
    /// </summary>
    [Fact]
    public async Task FailedPublishDisarmsTheStaleModeFilter()
    {
        using MechrevoHw hardware = NewHardware(
            (_, _) => throw new InvalidOperationException("MQTT 未连接"));
        hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":1}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => hardware.SetMode(1));

        Assert.Equal((false, -1), hardware.GetModeSwitchPendingState());

        // 发布失败之后，硬件报上来的真实模式必须能被接受。
        hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":0}");
        Assert.Equal(0, hardware.OperatingMode);
    }

    /// <summary>
    /// 发布成功时窗口保持武装：这是拒绝在途旧包的机制，不能被上一个测试的修复顺带改掉。
    /// </summary>
    [Fact]
    public async Task SuccessfulPublishKeepsTheStaleModeFilterArmed()
    {
        using MechrevoHw hardware = NewHardware();

        await hardware.SetMode(1);

        (bool active, int expected) = hardware.GetModeSwitchPendingState();
        Assert.True(active);
        Assert.Equal(2, expected);
    }

    // ---- M3：Dispose 的有界排空 ----

    /// <summary>
    /// 退出时若控制操作仍持有信号量，过去直接 Dispose 会让那个操作的 finally
    /// 抛 ObjectDisposedException。现在有界等待、拿不到就不释放，退出流程不再抛异常。
    /// </summary>
    [Fact]
    public async Task DisposeWhileAControlOperationIsInFlightDoesNotFaultThatOperation()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hardware = NewHardware(async (_, _) =>
        {
            entered.TrySetResult();
            await released.Task;
        });

        // SetBatteryProtection 在 _controlLock 内发布，会卡在上面的 publish 回调里。
        Task<bool> inFlight = hardware.SetBatteryProtection(2);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Dispose 有界等待后放弃释放信号量，不抛异常。
        hardware.Dispose();

        released.TrySetResult();
        // 关键：进行中的操作正常收敛，不会因为信号量被释放而抛 ObjectDisposedException。
        Assert.False(await inFlight);
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var hardware = NewHardware();

        hardware.Dispose();
        hardware.Dispose();
    }

    // ---- M4：直连超频后端的读取一致性 ----

    /// <summary>
    /// 没有直连超频后端时，所有相关属性都必须给出确定答案而不是抛空引用。
    /// 过去 GetMergedGpuOffsetRange 是「无锁检查 + _gpuOverclock! 解引用」。
    /// </summary>
    [Fact]
    public void OffsetRangePropertiesAreSafeWhenThereIsNoDirectBackend()
    {
        using MechrevoHw hardware = NewHardware();

        Assert.False(hardware.DirectGpuOverclockAvailable);
        Assert.False(hardware.DirectGpuCoreRangeAvailable);
        Assert.False(hardware.DirectGpuMemoryRangeAvailable);
        Assert.False(hardware.GpuOverclockEnabled);
        _ = hardware.GpuCoreOffsetUserMinimum;
        _ = hardware.GpuCoreOffsetUserMaximum;
        _ = hardware.GpuMemoryOffsetUserMinimum;
        _ = hardware.GpuMemoryOffsetUserMaximum;
    }

    /// <summary>
    /// Dispose 会在锁内把后端置 null。此后并发读取这些 UI 属性不得抛异常。
    /// </summary>
    [Fact]
    public async Task OffsetRangePropertiesStayConsistentWhileTheBackendIsTornDown()
    {
        var hardware = NewHardware();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        Task reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                _ = hardware.DirectGpuOverclockAvailable;
                _ = hardware.DirectGpuCoreRangeAvailable;
                _ = hardware.GpuCoreOffsetUserMinimum;
                _ = hardware.GpuMemoryOffsetUserMaximum;
                _ = hardware.GpuOverclockEnabled;
            }
        });

        await Task.Delay(50);
        hardware.Dispose();

        // 没有未处理异常即为通过。
        await reader;
    }

    // ---- H6：共享切换锁的让位 ----

    /// <summary>
    /// 让位超时必须显著小于热切换的最长确认时间，否则性能模式切换仍会被压住两分钟。
    /// </summary>
    [Fact]
    public void SwitchLockHandoverIsFasterThanTheLongestHotSwitchConfirmation()
    {
        TimeSpan worstCaseHotSwitch = TimeSpan.FromSeconds(2) * MechrevoService.HotSwitchStatusPollLimit;

        Assert.True(MechrevoService.SwitchLockHandoverTimeout < worstCaseHotSwitch);
        Assert.True(
            MechrevoService.SwitchLockHandoverTimeout + MechrevoService.SwitchLockAcquireTimeout <
            worstCaseHotSwitch,
            "让位 + 重试的总上限必须远小于热切换最坏时长，否则等于没有让位");
    }

    /// <summary>
    /// H6 主回归：一个长时间占用切换锁的操作在飞时，性能模式切换不会被无限期挂住，
    /// 而是在有界时间内拿到锁（长操作让位）或明确失败。
    /// </summary>
    [Fact]
    public async Task ModeSwitchDoesNotWaitForeverBehindALongRunningGpuSwitch()
    {
        var actions = new ConcurrentQueue<string?>();
        var hotSwitchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using MechrevoHw hardware = new(
            (_, payload) =>
            {
                if (payload is IDictionary<string, object> values &&
                    values.TryGetValue("Action", out object? action))
                {
                    string? text = action?.ToString();
                    actions.Enqueue(text);
                    if (text is not null && text.Contains("IGPU_ONLY", StringComparison.Ordinal))
                        hotSwitchEntered.TrySetResult();
                }
                return Task.CompletedTask;
            },
            new MechrevoDeviceCapabilities { GpuHotSwap = true, IgpuOnly = true, NvidiaGpu = true });
        hardware.HandleMessage("Setting/Status",
            "{\"IGpuOnlyConnectionSwitch_Status\":\"IGPU_ONLY_CONNECT_RB_OFF\"}");
        var service = new MechrevoService(hardware);

        // 这个热切换永远不会被确认，会一直轮询并持有 _switchLock。
        Task<bool> hotSwitch = service.SwitchGpuMode(MechrevoService.GpuIGpu);
        await hotSwitchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        bool modeSwitched = await service.SwitchMode(MechrevoService.ModeTurbo);
        stopwatch.Stop();

        // 不关心成败，只要求在有界时间内返回，而不是被压两分钟。
        Assert.True(
            stopwatch.Elapsed <
                MechrevoService.SwitchLockHandoverTimeout + MechrevoService.SwitchLockAcquireTimeout +
                TimeSpan.FromSeconds(5),
            $"性能模式切换等待了 {stopwatch.Elapsed}，让位机制没有生效");
        _ = modeSwitched;

        // 让位之后热切换应当以「被取代」收敛。
        Assert.False(await hotSwitch.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
