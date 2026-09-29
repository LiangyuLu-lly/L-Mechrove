using MechrevoLite.Hardware;

namespace MechrevoLite.Battery;

/// <summary>
/// 充电上限的效果证据（纯逻辑，便于测试）。
///
/// 寄存器回读一致只说明字节写进去了——CGLM 0x78F 写得进、读得回，却完全不控制充电。
/// 唯一可信的证据是 Windows 报告的真实充电状态：
/// <list type="bullet">
/// <item><b>生效</b>：插着电，电量已在「复充下限 ~ 上限」附近，却没有在充电；持续三次采样（间隔 ≥20 秒）。
///   电量 ≥98% 不算：电池充满本来就会停充，那不能证明上限起了作用。</item>
/// <item><b>无效</b>：插着电，电量比上限高 2 个点以上，仍在持续充电满 3 分钟。</item>
/// </list>
/// 其余情况（未插电、电量低于上限正常充电、满充档）不产生结论。
/// </summary>
internal sealed class ChargeLimitEvidence
{
    internal static readonly TimeSpan IneffectiveAfter = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan HeldSampleSpacing = TimeSpan.FromSeconds(20);
    internal const int HeldSamplesRequired = 3;
    internal const int FullBatteryPercent = 98;

    DateTime? _chargingAboveSince;
    int _heldSamples;
    DateTime _lastHeldSample;

    public void Reset()
    {
        _chargingAboveSince = null;
        _heldSamples = 0;
    }

    public ChargeLimitVerdict? Observe(int limit, bool onAc, bool charging, int percent, DateTime now)
    {
        if (limit is < EcChargeLimit.MinimumPercent or >= EcChargeLimit.MaximumPercent || !onAc)
        {
            Reset();
            return null;
        }

        if (charging && percent >= limit + 2)
        {
            _heldSamples = 0;
            _chargingAboveSince ??= now;
            return now - _chargingAboveSince.Value >= IneffectiveAfter ? ChargeLimitVerdict.Ineffective : null;
        }
        _chargingAboveSince = null;

        int rechargeFloor = limit - EcChargeLimit.RechargeHysteresis;
        if (!charging && percent >= rechargeFloor && percent < FullBatteryPercent)
        {
            if (_heldSamples == 0 || now - _lastHeldSample >= HeldSampleSpacing)
            {
                _heldSamples++;
                _lastHeldSample = now;
            }
            return _heldSamples >= HeldSamplesRequired ? ChargeLimitVerdict.Verified : null;
        }

        _heldSamples = 0;
        return null;
    }
}

/// <summary>
/// 运行期监视：定时读取 Windows 充电状态喂给 <see cref="ChargeLimitEvidence"/>，
/// 判定变化时持久化到本机，并在判为无效时自动恢复满充、关闭本机的充电上限入口。
/// </summary>
internal static class ChargeLimitMonitor
{
    static readonly ChargeLimitEvidence Evidence = new();
    static readonly object Gate = new();
    static DateTime _lastTick = DateTime.MinValue;
    static int _activeLimit = -1;

    /// <summary>测试接缝：代替真实的 Windows 充电状态读取。</summary>
    internal static Func<(bool OnAc, bool Charging, int Percent)?>? ChargeStateOverride { get; set; }

    /// <summary>判定变化时通知界面（在调用线程上触发）。</summary>
    internal static event Action<ChargeLimitVerdict>? VerdictChanged;

    /// <summary>新的上限写入成功（回读一致）后调用：重新开始收集证据。</summary>
    public static void Arm(int limit)
    {
        lock (Gate)
        {
            _activeLimit = limit;
            Evidence.Reset();
        }
    }

    /// <summary>定时调用（任意线程）。内部节流到每 10 秒最多评估一次。</summary>
    public static void Tick(DateTime now)
    {
        ChargeLimitVerdict? verdict;
        int limit;
        lock (Gate)
        {
            if (now - _lastTick < TimeSpan.FromSeconds(10)) return;
            _lastTick = now;
            limit = _activeLimit;
            if (limit is < EcChargeLimit.MinimumPercent or >= EcChargeLimit.MaximumPercent) return;
            if (EcChargeLimit.Verdict == ChargeLimitVerdict.Ineffective) return;
            var state = ChargeStateOverride is { } seam ? seam() : Mode.PowerNative.GetChargeState();
            if (state is not { } s) return;
            verdict = Evidence.Observe(limit, s.OnAc, s.Charging, s.Percent, now);
            if (verdict is null) return;
            if (verdict == EcChargeLimit.Verdict) return;
            Evidence.Reset();
        }

        EcChargeLimit.RecordVerdict(verdict.Value);
        Logger.WriteLine($"充电上限效果判定：{verdict.Value}（上限 {limit}%）");
        if (verdict == ChargeLimitVerdict.Ineffective)
        {
            // 本机这对寄存器不控制充电：恢复出厂满充（0/0），不留一个假装生效的上限。
            bool restored = EcChargeLimit.TrySet(EcChargeLimit.MaximumPercent, out _);
            AppConfig.Set("charge_limit", EcChargeLimit.MaximumPercent);
            AppConfig.Flush();
            lock (Gate) _activeLimit = -1;
            Logger.WriteLine($"充电上限判定无效，已恢复满充：{restored}");
        }
        try { VerdictChanged?.Invoke(verdict.Value); }
        catch (Exception ex) { Logger.WriteLine("Charge-limit verdict handler failed: " + ex.Message); }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _activeLimit = -1;
            _lastTick = DateTime.MinValue;
            Evidence.Reset();
        }
    }
}
