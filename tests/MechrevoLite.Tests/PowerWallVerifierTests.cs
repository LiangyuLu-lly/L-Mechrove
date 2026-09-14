using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 功耗墙生效校验。
///
/// GCU 回读一个 PL1 只证明"服务端记下了"，不证明"CPU 照着做了"。
/// 这个判定器拿实测封装功耗去对，是这条链路唯一的端到端证据。
///
/// 最容易写错的地方是把它做成简单比大小：PL1 是**持续**限值且带时间常数
/// （Intel 默认约 28 秒），短时冲到 PL2 是设计行为不是故障；
/// 而轻载时功耗远低于上限，这种情况什么都证明不了。
/// 所以下面的测试重点全在"什么时候必须回答判不出来"。
/// </summary>
public class PowerWallVerifierTests
{
    static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>按固定间隔灌满一个完整窗口。</summary>
    static DateTime FillWindow(PowerWallVerifier verifier, double watts, int limit, int samples = 40)
    {
        double stepSeconds = PowerWallVerifier.Window.TotalSeconds / (samples - 1);
        DateTime now = Origin;
        for (int i = 0; i < samples; i++)
        {
            now = Origin.AddSeconds(stepSeconds * i);
            verifier.Observe(watts, limit, now);
        }
        return now;
    }

    [Fact]
    public void AFreshVerifierKnowsNothing()
    {
        var verifier = new PowerWallVerifier();

        Assert.Equal(PowerWallVerdict.Unknown, verifier.Evaluate(Origin));
        Assert.Null(verifier.AverageWatts);
    }

    /// <summary>持续功耗明显超过上限 → 这道墙没生效。</summary>
    [Fact]
    public void SustainedPowerWellAboveTheLimitMeansNotEnforced()
    {
        var verifier = new PowerWallVerifier();

        DateTime now = FillWindow(verifier, watts: 80, limit: 45);

        Assert.Equal(PowerWallVerdict.NotEnforced, verifier.Evaluate(now));
    }

    /// <summary>持续功耗贴着上限走 → 这道墙在限。</summary>
    [Fact]
    public void SustainedPowerAtTheLimitMeansEnforced()
    {
        var verifier = new PowerWallVerifier();

        DateTime now = FillWindow(verifier, watts: 44, limit: 45);

        Assert.Equal(PowerWallVerdict.Enforced, verifier.Evaluate(now));
    }

    /// <summary>
    /// 轻载必须回答判不出来。报"生效"是错的——负载根本没把墙顶起来，
    /// 就算上限没被应用也看不出来。
    /// </summary>
    [Fact]
    public void LightLoadMeansUnknownRatherThanEnforced()
    {
        var verifier = new PowerWallVerifier();

        DateTime now = FillWindow(verifier, watts: 12, limit: 45);

        Assert.Equal(PowerWallVerdict.Unknown, verifier.Evaluate(now));
    }

    /// <summary>
    /// 略微超出上限不算没生效：RAPL 与 EC 的计量口径本就不完全一致，
    /// 合法的 PL2 冲高被平均进来也会抬高均值。
    /// </summary>
    [Fact]
    public void SlightlyOverTheLimitIsStillConsideredEnforced()
    {
        var verifier = new PowerWallVerifier();

        DateTime now = FillWindow(verifier, watts: 45 * 1.05, limit: 45);

        Assert.Equal(PowerWallVerdict.Enforced, verifier.Evaluate(now));
    }

    /// <summary>
    /// 窗口没铺满就不能下结论：样本数够了但时间跨度不够时仍是判不出来。
    /// 不设这条的话，一次几秒的 PL2 冲高就会被判成"没生效"。
    /// </summary>
    [Fact]
    public void AShortBurstDoesNotProduceAVerdict()
    {
        var verifier = new PowerWallVerifier();

        DateTime now = Origin;
        for (int i = 0; i < 60; i++)
        {
            now = Origin.AddSeconds(i * 0.2);   // 60 个样本只铺了 12 秒
            verifier.Observe(120, 45, now);
        }

        Assert.Equal(PowerWallVerdict.Unknown, verifier.Evaluate(now));
    }

    /// <summary>样本数不足时也不下结论。</summary>
    [Fact]
    public void TooFewSamplesProduceNoVerdict()
    {
        var verifier = new PowerWallVerifier();

        DateTime now = FillWindow(verifier, watts: 90, limit: 45,
            samples: PowerWallVerifier.MinimumSamples - 1);

        Assert.Equal(PowerWallVerdict.Unknown, verifier.Evaluate(now));
    }

    /// <summary>
    /// 上限一变，之前的样本是针对旧墙测的，必须立即作废。
    /// 留着会拿旧墙下的功耗去判新墙，得出完全错误的结论。
    /// </summary>
    [Fact]
    public void ChangingTheLimitDiscardsSamplesTakenAgainstTheOldWall()
    {
        var verifier = new PowerWallVerifier();
        DateTime now = FillWindow(verifier, watts: 90, limit: 45);
        Assert.Equal(PowerWallVerdict.NotEnforced, verifier.Evaluate(now));

        // 用户把上限提到 100 W：同样的 90 W 现在是完全正常的。
        verifier.Observe(90, 100, now.AddSeconds(1));

        Assert.Equal(100, verifier.LimitWatts);
        Assert.Equal(PowerWallVerdict.Unknown, verifier.Evaluate(now.AddSeconds(1)));
    }

    /// <summary>窗口外的旧样本要被丢掉，否则负载变化后结论会一直粘着旧值。</summary>
    [Fact]
    public void SamplesOlderThanTheWindowAreDropped()
    {
        var verifier = new PowerWallVerifier();
        FillWindow(verifier, watts: 90, limit: 45);

        // 隔了很久之后重新开始采样：旧的高功耗样本不该再参与判定。
        DateTime later = Origin.AddMinutes(10);
        verifier.Observe(20, 45, later);

        Assert.Equal(PowerWallVerdict.Unknown, verifier.Evaluate(later));
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var verifier = new PowerWallVerifier();
        DateTime now = FillWindow(verifier, watts: 90, limit: 45);

        verifier.Reset();

        Assert.Equal(PowerWallVerdict.Unknown, verifier.Evaluate(now));
        Assert.Equal(-1, verifier.LimitWatts);
        Assert.Null(verifier.AverageWatts);
    }

    /// <summary>没有上限（GCU 还没报 PL1）时不下结论，也不该崩。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NoLimitMeansNoVerdict(int limit)
    {
        var verifier = new PowerWallVerifier();

        DateTime now = FillWindow(verifier, watts: 90, limit: limit);

        Assert.Equal(PowerWallVerdict.Unknown, verifier.Evaluate(now));
    }

    /// <summary>非法功耗读数（NaN / 负数）要被忽略，不能污染均值。</summary>
    [Fact]
    public void NonFiniteAndNegativeSamplesAreIgnored()
    {
        var verifier = new PowerWallVerifier();
        DateTime now = FillWindow(verifier, watts: 44, limit: 45);
        double? before = verifier.AverageWatts;

        verifier.Observe(double.NaN, 45, now.AddSeconds(1));
        verifier.Observe(double.PositiveInfinity, 45, now.AddSeconds(2));
        verifier.Observe(-50, 45, now.AddSeconds(3));

        Assert.Equal(before, verifier.AverageWatts);
        Assert.Equal(PowerWallVerdict.Enforced, verifier.Evaluate(now.AddSeconds(3)));
    }

    /// <summary>结论文案要带上均值与上限，否则用户没法判断该不该信。</summary>
    [Fact]
    public void TheDescriptionNamesBothTheMeasuredAverageAndTheLimit()
    {
        var verifier = new PowerWallVerifier();
        DateTime now = FillWindow(verifier, watts: 80, limit: 45);

        string text = verifier.Describe(now);

        Assert.Contains("45", text);
        Assert.Contains("80", text);
    }

    [Fact]
    public async Task ConcurrentSamplingAndEvaluationDoNotCorruptTheWindow()
    {
        var verifier = new PowerWallVerifier();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        Task[] workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 2000; i++)
            {
                DateTime now = Origin.AddMilliseconds(worker * 2000L + i);
                try
                {
                    verifier.Observe(44, 45, now);
                    _ = verifier.Evaluate(now);
                    _ = verifier.AverageWatts;
                    _ = verifier.Describe(now);
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);

        Assert.Empty(failures);
        Assert.Equal(45, verifier.LimitWatts);
    }
}
