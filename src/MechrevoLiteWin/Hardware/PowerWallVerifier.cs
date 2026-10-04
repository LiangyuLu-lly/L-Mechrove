namespace MechrevoLite.Hardware;

/// <summary>功耗墙是否真的在生效。</summary>
internal enum PowerWallVerdict
{
    /// <summary>负载太轻或采样不足，判不出来。这是默认答案，不是"有问题"。</summary>
    Unknown,

    /// <summary>持续功耗贴着上限走，说明这道墙真的在限。</summary>
    Enforced,

    /// <summary>持续功耗明显超过上限，说明写下去的值没被应用。</summary>
    NotEnforced,
}

/// <summary>
/// 校验 CPU 功耗墙有没有真的生效。
///
/// 为什么需要这个：GCU 回读一个值只证明"服务端记下了"，不证明"CPU 照着做了"。
/// 我们所有写入都做回读确认，但 PL1 这条的回读来自服务端自己的状态，
/// 拿实测功耗对一下才是端到端的证据。
///
/// 为什么不能直接比大小：PL1 是**持续**功耗限值，带一个时间常数（Tau，
/// Intel 默认约 28 秒）。短时冲到 PL2 是设计行为而不是故障，
/// 所以只能看足够长窗口上的**平均值**。反过来，轻载时功耗远低于上限，
/// 这种情况什么都证明不了，必须老实回答"判不出来"而不是"正常"。
///
/// 这个类是纯逻辑：喂样本、给结论，不碰硬件，可以完整单测。
/// </summary>
internal sealed class PowerWallVerifier
{
    /// <summary>
    /// 判定窗口。取 60 秒是为了明显长于常见的 Tau（Intel 默认约 28 秒），
    /// 这样一次合法的 PL2 冲高被平均进来也不会翻转结论。
    /// </summary>
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>窗口内最少样本数。太少的话一次采样毛刺就能定结论。</summary>
    internal const int MinimumSamples = 20;
    internal static readonly TimeSpan MaximumSampleGap = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 超过上限多少才算"没生效"。15% 的余量用来吸收计量误差
    /// （RAPL 与 EC 的口径不完全一致）和被平均进来的合法冲高。
    /// </summary>
    internal const double ExceedRatio = 1.15;

    /// <summary>
    /// 达到上限多少才算"贴着墙走"。低于这条线说明负载没把墙顶起来，
    /// 结论只能是判不出来。
    /// </summary>
    internal const double EngagedRatio = 0.85;

    readonly object _sync = new();
    readonly Queue<(DateTime At, double Watts)> _samples = new();
    DateTime? _lastSampleAt;
    int _limitWatts = -1;

    /// <summary>当前判定所针对的功耗上限。上限一变，历史样本立即作废。</summary>
    public int LimitWatts
    {
        get { lock (_sync) return _limitWatts; }
    }

    /// <summary>
    /// 喂一个功耗样本。limitWatts 是当前生效的 PL1（瓦）。
    /// </summary>
    public void Observe(double watts, int limitWatts, DateTime now)
    {
        lock (_sync)
        {
            // 上限改了，之前的样本是针对旧墙的，留着会得出错误结论。
            if (limitWatts != _limitWatts)
            {
                _samples.Clear();
                _lastSampleAt = null;
                _limitWatts = limitWatts;
            }
            if (limitWatts <= 0 || !double.IsFinite(watts) || watts < 0) return;
            if (_lastSampleAt is { } last && (now < last || now - last > MaximumSampleGap))
                _samples.Clear();
            _lastSampleAt = now;
            _samples.Enqueue((now, watts));
            // 留一个跨越窗口起点的样本，定时器抖动时仍能证明已经连续采满一整窗。
            while (_samples.Count > 1 && now - _samples.ElementAt(1).At >= Window) _samples.Dequeue();
        }
    }

    /// <summary>丢弃已有样本（例如切换模式或档位之后）。</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _samples.Clear();
            _lastSampleAt = null;
            _limitWatts = -1;
        }
    }

    /// <summary>
    /// 当前结论。样本不足、窗口没铺满、或者负载没把墙顶起来时一律 Unknown。
    /// </summary>
    public PowerWallVerdict Evaluate(DateTime now)
    {
        lock (_sync) return EvaluateCore(now);
    }

    PowerWallVerdict EvaluateCore(DateTime now)
    {
        if (_limitWatts <= 0 || _samples.Count < MinimumSamples) return PowerWallVerdict.Unknown;
        if (_lastSampleAt is not { } last || now < last || now - last > MaximumSampleGap)
            return PowerWallVerdict.Unknown;

        (DateTime At, double Watts) oldest = _samples.Peek();
        // 窗口必须真的铺满：刚开始采样时哪怕样本数够了也不能下结论。
        if (now - oldest.At < Window) return PowerWallVerdict.Unknown;

        double average = _samples.Sum(sample => sample.Watts) / _samples.Count;
        if (average > _limitWatts * ExceedRatio) return PowerWallVerdict.NotEnforced;
        if (average >= _limitWatts * EngagedRatio) return PowerWallVerdict.Enforced;
        return PowerWallVerdict.Unknown;
    }

    /// <summary>窗口内的平均功耗，样本不足时返回 null。用于把结论说清楚。</summary>
    public double? AverageWatts
    {
        get { lock (_sync) return AverageWattsCore(); }
    }

    double? AverageWattsCore() => _samples.Count < MinimumSamples
        ? null
        : _samples.Sum(sample => sample.Watts) / _samples.Count;

    /// <summary>给用户看的一句话。</summary>
    public string Describe(DateTime now)
    {
        lock (_sync)
        {
            PowerWallVerdict verdict = EvaluateCore(now);
            double? average = AverageWattsCore();
            return verdict switch
            {
                PowerWallVerdict.Enforced =>
                    $"功耗墙生效中（{Window.TotalSeconds:F0}s 均值 {average!.Value:F1}W / 上限 {_limitWatts}W）",
                PowerWallVerdict.NotEnforced =>
                    $"功耗墙未生效：{Window.TotalSeconds:F0}s 均值 {average!.Value:F1}W 超过上限 {_limitWatts}W",
                _ => "功耗墙状态未知（需要一段持续高负载才能判定）",
            };
        }
    }
}
