using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 帧节拍契约：Windows 默认计时器粒度 ~15.6ms，Thread.Sleep(1) 的唤醒点被量化到 15.6ms 边界，
/// 目标 30fps（33.3ms）时会出现 ~46.8ms 的卡顿尖峰。节拍器必须把稳态帧间隔收回到目标附近，
/// 且不得用忙等（100% CPU）换取精度。
/// </summary>
public class KeyboardRgbPacingTests
{
    /// <summary>
    /// 模拟 Windows 粗粒度睡眠：唤醒点量化到 15.6ms 的绝对边界（不是相对取整），
    /// Spin 推进 0.2ms 的极小时片。该模型复现了真实 Release 日志里 p95≈46ms 的尖峰。
    /// </summary>
    sealed class CoarseSleepClock : IFrameClock
    {
        const double GranularityMs = 15.6;
        const int SpinTicksPerCall = 200;          // 0.2ms
        public long Frequency => 1_000_000;        // 1 tick = 1µs
        public long Timestamp { get; private set; }
        public long SpinTicks { get; private set; }

        public void Sleep(int milliseconds)
        {
            if (milliseconds < 1) milliseconds = 1;
            long request = Timestamp + milliseconds * 1000L;
            long granularity = (long)(GranularityMs * 1000);
            long ticks = (request + granularity - 1) / granularity;   // 向上取整到下一个绝对粒度边界
            Timestamp = ticks * granularity;
        }

        public void Spin()
        {
            Timestamp += SpinTicksPerCall;
            SpinTicks += SpinTicksPerCall;
        }
    }

    [Fact]
    public void CoarseSleepGranularity_SteadyFramesStayNearTargetWithoutFullDutySpin()
    {
        var clock = new CoarseSleepClock();
        var pacer = new FramePacer(clock);
        const int frames = 90;
        const double targetMs = 1000.0 / 30.0;
        var intervals = new List<double>(frames);

        long previous = clock.Timestamp;
        for (int i = 0; i < frames; i++)
        {
            pacer.Pace(30);
            long now = clock.Timestamp;
            intervals.Add((now - previous) * 1000.0 / clock.Frequency);
            previous = now;
        }

        var steady = intervals.Skip(20).OrderBy(value => value).ToList();   // 跳过自适应预热帧
        double averageFps = steady.Count * 1000.0 / steady.Sum();
        double p95 = steady[(int)(steady.Count * 0.95)];
        double spinDuty = clock.SpinTicks / (double)clock.Timestamp;

        Assert.InRange(averageFps, 28.0, 36.0);
        Assert.True(p95 < targetMs * 1.3,
            $"粗粒度睡眠下稳态帧间隔 p95={p95:F1}ms 超出目标 {targetMs:F1}ms 的 1.3 倍：仍存在可感知的卡顿尖峰。");
        Assert.True(spinDuty < 0.6,
            $"自旋占比 {spinDuty:P0} 超过 60%：不得以忙等占满 CPU 换取节拍精度。");
    }
}
