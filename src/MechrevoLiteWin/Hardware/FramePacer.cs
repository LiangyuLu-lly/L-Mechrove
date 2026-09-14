using System.Diagnostics;

namespace MechrevoLite.Hardware;

/// <summary>
/// 帧节拍等待原语 seam：真实实现走 <see cref="Stopwatch"/> + <see cref="Thread.Sleep"/> + <see cref="Thread.SpinWait"/>；
/// 测试注入假时钟，用于模拟 Windows ~15.6ms 的粗粒度睡眠并断言节拍稳定性。
/// </summary>
internal interface IFrameClock
{
    long Frequency { get; }
    long Timestamp { get; }
    void Sleep(int milliseconds);
    void Spin();
}

internal sealed class SystemFrameClock : IFrameClock
{
    public long Frequency => Stopwatch.Frequency;
    public long Timestamp => Stopwatch.GetTimestamp();
    public void Sleep(int milliseconds) => Thread.Sleep(milliseconds);
    public void Spin() => Thread.SpinWait(20);
}

/// <summary>
/// 效果帧节拍器：睡眠为主 + 有限自旋收尾，把相邻帧起始时刻推进到目标 fps。
///
/// Windows 默认计时器粒度约 15.6ms，<see cref="Thread.Sleep(int)"/> 会把唤醒点量化到粒度边界。
/// 纯睡眠循环在目标 30fps（33.3ms）时会周期性地冲到 ~46.8ms，玩家看到的就是卡顿/闪烁。
/// 这里先睡到「截止点 - 自旋预算」，再用极短自旋吸收睡眠量化误差；自旋预算自适应观测到的
/// 睡眠粒度（配合效果播放期内的 timeBeginPeriod(1) 通常只有 1~2ms），并硬性封顶在半个帧间隔——
/// 即使计时器粒度粗如 15.6ms 也绝不用忙等占满 CPU，只会轻微掉帧。
/// </summary>
internal sealed class FramePacer
{
    readonly IFrameClock _clock;
    long _nextTick;
    long _spinMarginTicks;   // 自适应自旋预算：约等于观测到的睡眠粒度

    internal FramePacer(IFrameClock clock) => _clock = clock;

    internal void Pace(int fps)
    {
        long frequency = _clock.Frequency;
        long interval = frequency / Math.Clamp(fps, 15, 60);
        long now = _clock.Timestamp;
        if (_nextTick == 0 || now > _nextTick + interval * 2) _nextTick = now;   // 落后过多（睡眠/挂起）则重新对齐
        _nextTick += interval;

        long maxSpin = interval / 2;                                    // 自旋预算上限：半帧，保证不占满 CPU
        if (_spinMarginTicks == 0) _spinMarginTicks = frequency / 1000; // 初值 1ms，随观测到的粒度增长
        long margin = Math.Min(_spinMarginTicks, maxSpin);

        long remaining = _nextTick - now;
        if (remaining > margin)
        {
            long sleepTicks = remaining - margin;
            int sleepMs = (int)Math.Max(1, sleepTicks * 1000 / frequency);
            long before = _clock.Timestamp;
            _clock.Sleep(sleepMs);
            long actual = _clock.Timestamp - before;
            if (actual > sleepTicks)
            {
                long overshoot = actual - sleepTicks;
                if (overshoot > _spinMarginTicks) _spinMarginTicks = overshoot;   // 学习睡眠粒度
            }
        }

        long spinStart = _clock.Timestamp;
        while (_clock.Timestamp < _nextTick)
        {
            _clock.Spin();
            if (_clock.Timestamp - spinStart >= maxSpin) break;   // 预算耗尽：宁可轻微掉帧也不忙等
        }
    }
}
