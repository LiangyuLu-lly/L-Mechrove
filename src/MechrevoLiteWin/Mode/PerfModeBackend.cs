using MechrevoLite.Hardware;

namespace MechrevoLite.Mode;

/// <summary>
/// 视觉模式枚举（<see cref="MechrevoService"/>）与用户模式种类之间的映射。
/// 静音狂暴与狂暴共用固件 Turbo 档，靠子模式区分。
/// </summary>
public static class PerfModeMapping
{
    /// <summary>用户模式种类 → <see cref="MechrevoService"/> 的模式枚举（<c>SwitchMode</c> 的参数）。</summary>
    public static int ToServiceMode(PerfModeKind kind) => kind switch
    {
        PerfModeKind.Silent => MechrevoService.ModeOffice,
        PerfModeKind.Balanced => MechrevoService.ModeGaming,
        PerfModeKind.SilentTurbo => MechrevoService.ModeTurbo,
        PerfModeKind.Turbo => MechrevoService.ModeTurbo,
        PerfModeKind.Custom => MechrevoService.ModeCustom,
        _ => MechrevoService.ModeGaming,
    };

    /// <summary>固件上报的 <c>OperatingMode</c>（0=办公 1=游戏 2=狂暴 3=自定义）。</summary>
    public static int ToFirmwareOperatingMode(PerfModeKind kind) => kind switch
    {
        PerfModeKind.Silent => 0,
        PerfModeKind.Balanced => 1,
        PerfModeKind.SilentTurbo => 2,
        PerfModeKind.Turbo => 2,
        PerfModeKind.Custom => 3,
        _ => 1,
    };

    /// <summary>固件回报的模式 + 子模式 → 用户模式种类。</summary>
    public static PerfModeKind FromFirmware(int operatingMode, bool silentTurbo) => operatingMode switch
    {
        0 => PerfModeKind.Silent,
        1 => PerfModeKind.Balanced,
        2 => silentTurbo ? PerfModeKind.SilentTurbo : PerfModeKind.Turbo,
        3 => PerfModeKind.Custom,
        _ => PerfModeKind.Balanced,
    };
}

/// <summary>
/// 真实下发后端：接 GCU（MQTT）、NVIDIA 驱动与 Windows 电源 API。
///
/// <para>判据纪律：<see cref="VerifyAsync"/> 只在拿到**硬件侧**证据时返回 true/false；
/// 拿不到证据返回 <c>null</c>，让执行器报「已下发、此刻判不出」而不是谎称成功。
/// 服务端 <c>Fan/Status</c> 的回显**不算**硬件证据（见 gcu-modes-and-profiles.md §1.3）。</para>
/// </summary>
internal sealed class PerfModeBackend : IPerfModeBackend
{
    readonly MechrevoService _service;
    readonly MechrevoHw _hw;

    public PerfModeBackend(MechrevoService service, MechrevoHw hw)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _hw = hw ?? throw new ArgumentNullException(nameof(hw));
    }

    /// <summary>从当前硬件状态推出下发时要用的能力快照。</summary>
    public static PerfModeCapabilities CapabilitiesFrom(MechrevoHw hw)
    {
        ArgumentNullException.ThrowIfNull(hw);
        return new PerfModeCapabilities
        {
            UsesAmdPowerFields = hw.UsesAmdPowerFields,
            SupportsPl1 = hw.Pl1Adjustable,
            SupportsPl2 = hw.Pl2Adjustable,
            SupportsPl4 = hw.Pl4Adjustable,
            SupportsTcc = hw.TccAdjustable,
            SupportsTgp = hw.GpuTgpAdjustable,
            SupportsDynamicBoost = hw.GpuDynamicBoostAdjustable,
            SupportsGpuOverclock = hw.SupportsGpuOverclock,
            SupportsFanCurve = hw.FanCurveSeen,
            SupportsFanBoost = hw.SupportsFanBoost,
            SupportsFanSwitchSpeed = hw.SupportsFanSwitchSpeed,
            SupportsRefreshRate = hw.SupportsDisplayRefresh && !hw.DcHz,
            SupportsTurboSubMode =
                hw.Capabilities.SilentTurboAvailability == FeatureAvailability.Supported,
        };
    }

    public Task<bool> SwitchBuiltInAsync(PerfModeKind kind, CancellationToken ct) =>
        _service.SwitchMode(PerfModeMapping.ToServiceMode(kind));

    public Task<bool> SwitchTurboSubModeAsync(bool silent, CancellationToken ct) =>
        _service.SwitchTurboSubMode(silent);

    public Task<bool> SwitchFirmwareSlotAsync(int slotIndex, CancellationToken ct) =>
        _service.SwitchCustomProfileWithResend(slotIndex);

    public Task<bool> WriteDetailFieldAsync(string wireKey, string value, CancellationToken ct) =>
        // 逐字段一包：同包多字段只有第一个生效（厂商 UserSet_Mode_Detail 的 else-if 链）。
        _service.SetCustomDetail(new Dictionary<string, string> { [wireKey] = value });

    public async Task<bool> WriteFanCurveAsync(bool cpu, int[] duty, CancellationToken ct)
    {
        try
        {
            await _hw.SetFanCurve(cpu ? 0 : 1, duty).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"PerfModeBackend.WriteFanCurve({(cpu ? "CPU" : "GPU")}) failed: {ex.Message}");
            return false;
        }
    }

    public Task<bool> ApplyGpuOverclockAsync(bool on, int core, int memory, CancellationToken ct) =>
        _service.ApplyGcuGpuOverclock(on, core, memory);

    public Task<bool> SetSlotNameAsync(string name, CancellationToken ct) =>
        _service.SetCustomProfileName(name);

    public Task<bool> SetFanBoostAsync(bool on, CancellationToken ct) =>
        _hw.FanBoost == on ? Task.FromResult(true) : _service.SwitchFanBoost(on);

    public Task<bool> SetRefreshRateAsync(int hz, CancellationToken ct) =>
        _hw.CurrentHz == hz ? Task.FromResult(true) : _service.SwitchRefreshRate(hz);

    public bool SetPowerPlan(string guid) => WinPowerPlan.SetActivePlan(guid);

    public bool SetCpuBoost(int index) => WinPowerPlan.SetBoost(index);

    public bool SetPowerOverlay(int mode) =>
        PowerNative.SetOverlayConfirmed(mode) is PowerOverlayResult.Confirmed
            or PowerOverlayResult.UltimatePlanKept or PowerOverlayResult.BatterySaver;

    public Task DelayAsync(int milliseconds, CancellationToken ct) => Task.Delay(milliseconds, ct);

    public async Task<bool?> VerifyAsync(PerfApplyStep step, CancellationToken ct)
    {
        switch (step.Verify)
        {
            case PerfApplyVerify.ModeReadback:
                return await VerifyModeAsync(step, ct).ConfigureAwait(false);

            case PerfApplyVerify.PowerOverlay:
                // IsOverlayActive 本身就是三态（读不到返回 null）。
                return PowerNative.IsOverlayActive(step.Number);

            case PerfApplyVerify.PowerPlan:
                string active = WinPowerPlan.GetActivePlan();
                return active.Length == 0
                    ? null
                    : string.Equals(active, step.Value, StringComparison.OrdinalIgnoreCase);

            case PerfApplyVerify.CpuBoost:
                return WinPowerPlan.TryGetBoost(out int ac, out int dc)
                    ? ac == step.Number && dc == step.Number
                    : null;

            case PerfApplyVerify.RefreshRate:
                return _hw.CurrentHz > 0 ? _hw.CurrentHz == step.Number : null;

            case PerfApplyVerify.GpuClockOffset:
                // 驱动后端不在时没有独立判据；GCU 回显不算证据。
                if (!_hw.DirectGpuOverclockBackendPresent) return null;
                if (step.Kind == PerfApplyStepKind.ApplyGpuOverclock)
                    return _hw.DriverGpuOverclockFieldMatches("GpuCoreClockOffsetOC", step.GpuOffsets?.Core ?? 0)
                        && _hw.DriverGpuOverclockFieldMatches("GpuMemoryClockOffsetOC", step.GpuOffsets?.Memory ?? 0);
                return _hw.DriverGpuOverclockFieldMatches(step.WireKey!, step.Number);

            case PerfApplyVerify.CpuPackagePower:
                return VerifyCpuPowerLimit(step.Number);

            case PerfApplyVerify.GpuPowerLimit:
            case PerfApplyVerify.FanDuty:
                // 功耗墙要有负载、风扇要等温度爬上曲线段才判得出来：
                // 这两项由后台守护逐步判定，当场一律报「已下发」。
                return null;

            default:
                return null;
        }
    }

    async Task<bool?> VerifyModeAsync(PerfApplyStep step, CancellationToken ct)
    {
        if (step.Kind == PerfApplyStepKind.SwitchFirmwareSlot)
        {
            bool ok = await _hw.WaitForStateAsync(
                () => _hw.OperatingMode == 3 && _hw.CustomProfileIndex == step.Number,
                TimeSpan.FromMilliseconds(1200), ct).ConfigureAwait(false);
            return ok || (_hw.OperatingMode == 3 && _hw.CustomProfileIndex == step.Number);
        }

        int expected = PerfModeMapping.ToFirmwareOperatingMode((PerfModeKind)step.Number);
        bool switched = await _hw.WaitForStateAsync(
            () => _hw.OperatingMode == expected,
            TimeSpan.FromMilliseconds(1200), ct).ConfigureAwait(false);
        return switched || _hw.OperatingMode == expected;
    }

    /// <summary>
    /// 功耗墙的独立判据：CPU 封装功耗（RAPL）。只有当前功耗**明显超过**目标时才算失败，
    /// 轻载下一律返回 null——轻载时功耗本来就低于墙，判不出墙是否生效。
    /// </summary>
    internal static bool? VerifyCpuPowerLimit(int watts) =>
        EvaluateCpuPowerLimit(watts, HardwareControl.cpuPower);

    /// <summary>纯函数形式，便于单测。</summary>
    internal static bool? EvaluateCpuPowerLimit(int watts, float? measured)
    {
        if (watts <= 0 || measured is not { } power || power <= 0) return null;
        // 留 15% + 5 W 余量：RAPL 是瞬时值，短时冲高不算越界。
        double ceiling = watts * 1.15 + 5;
        if (power > ceiling) return false;
        // 功耗已经贴近墙 → 墙确实在管着它。
        if (power >= watts * 0.85) return true;
        return null;   // 轻载：判不出来
    }
}
