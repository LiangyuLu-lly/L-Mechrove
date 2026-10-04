namespace MechrevoLite.Mode;

/// <summary>
/// 一步的结果。三态是刻意的：<see cref="Sent"/> 表示命令确实发出去了、但**此刻拿不到硬件判据**
/// （例如功耗墙要有负载才判得出来）。UI 必须把它和 <see cref="Confirmed"/> 区分显示——
/// 把「已下发」说成「已生效」正是伪功能的定义。
/// </summary>
public enum PerfApplyResult
{
    Confirmed,
    Sent,
    Failed,
}

/// <summary>一步的执行结果。</summary>
public sealed record PerfApplyStepOutcome(PerfApplyStep Step, PerfApplyResult Result, string Detail = "")
{
    public override string ToString() =>
        Step + " -> " + Result + (string.IsNullOrEmpty(Detail) ? "" : $" ({Detail})");
}

/// <summary>整次应用的结果。</summary>
public sealed record PerfApplyOutcome(string ModeId, IReadOnlyList<PerfApplyStepOutcome> Steps)
{
    public bool ModeSwitched => Steps.Any(s => IsSwitch(s.Step)) && Steps
        .Where(s => IsSwitch(s.Step)).All(s => s.Result == PerfApplyResult.Confirmed);

    public bool FirmwareParametersIssued => Steps
        .Where(s => s.Step.Kind is PerfApplyStepKind.WriteFirmwareField
            or PerfApplyStepKind.WriteFanCurve or PerfApplyStepKind.ApplyGpuOverclock)
        .All(s => s.Result != PerfApplyResult.Failed);

    internal static bool IsSwitch(PerfApplyStep step) => step.Kind is PerfApplyStepKind.SwitchBuiltIn
        or PerfApplyStepKind.SwitchFirmwareSlot or PerfApplyStepKind.SwitchTurboSubMode;

    public bool AnyFailed => Steps.Any(s => s.Result == PerfApplyResult.Failed);

    public IReadOnlyList<PerfApplyStepOutcome> Failed =>
        Steps.Where(s => s.Result == PerfApplyResult.Failed).ToList();

    /// <summary>有下发但拿不到判据的项（UI 要如实说明，不能报「已生效」）。</summary>
    public IReadOnlyList<PerfApplyStepOutcome> Unverified =>
        Steps.Where(s => s.Result == PerfApplyResult.Sent).ToList();

    public string Describe() => Steps.Count == 0
        ? "(no steps)"
        : string.Join("  ", Steps.Select(s => s.ToString()));
}

/// <summary>
/// 下发后端。真实实现接到 GCU / NVAPI / Windows 电源 API；测试用假实现。
/// 每个方法返回「这一步是否成功」，硬件侧判据由 <c>Verify*</c> 提供。
/// </summary>
public interface IPerfModeBackend
{
    Task<bool> SwitchBuiltInAsync(PerfModeKind kind, CancellationToken ct);
    Task<bool> SwitchTurboSubModeAsync(bool silent, CancellationToken ct);
    Task<bool> SwitchFirmwareSlotAsync(int slotIndex, CancellationToken ct);
    /// <summary>单字段 <c>SET_OPERATING_MODE_DETAIL</c>（MQTT → GCU → EC）。不是 NVRAM/固件变量写入。</summary>
    Task<bool> WriteDetailFieldAsync(string wireKey, string value, CancellationToken ct);
    Task<bool> WriteFanCurveAsync(bool cpu, int[] duty, CancellationToken ct);

    /// <summary>内置模式的显卡超频（GCU 路径）。</summary>
    Task<bool> ApplyGpuOverclockAsync(bool on, int core, int memory, CancellationToken ct);

    /// <summary>当前自定义档的厂商屏显名（<c>SET_CUSTOM_PROFILE_OSD_STRING</c>，以 <c>ProfileName</c> 回读确认）。</summary>
    Task<bool> SetSlotNameAsync(string name, CancellationToken ct);
    Task<bool> SetFanBoostAsync(bool on, CancellationToken ct);
    Task<bool> SetRefreshRateAsync(int hz, CancellationToken ct);

    bool SetPowerPlan(string guid);
    bool SetCpuBoost(int index);
    bool SetPowerOverlay(int mode);

    /// <summary>逐字段下发之间的节流（服务端实测连发会丢字段）。</summary>
    Task DelayAsync(int milliseconds, CancellationToken ct);

    /// <summary>
    /// 硬件侧判据。返回 <c>null</c> = **此刻判不出来**（例如轻载时功耗墙无从判断），
    /// 调用方据此给出 <see cref="PerfApplyResult.Sent"/> 而不是谎称成功。
    /// </summary>
    Task<bool?> VerifyAsync(PerfApplyStep step, CancellationToken ct);
}

/// <summary>
/// 按计划逐步下发并逐项判定。本身不含硬件细节，只负责顺序、节流、结果归类。
/// </summary>
public static class PerfModeRunner
{
    public static async Task<PerfApplyOutcome> RunAsync(
        string modeId,
        IReadOnlyList<PerfApplyStep> steps,
        IPerfModeBackend backend,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modeId);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(backend);

        var outcomes = new List<PerfApplyStepOutcome>(steps.Count);
        bool previousWasFirmwareField = false;

        foreach (PerfApplyStep step in steps)
        {
            ct.ThrowIfCancellationRequested();

            // 逐字段节流：只在两个固件字段之间等，别的步骤不必空等。
            if (previousWasFirmwareField && step.Kind == PerfApplyStepKind.WriteFirmwareField)
                await backend.DelayAsync(PerfModeApplyPlanner.FieldDelayMs, ct).ConfigureAwait(false);
            previousWasFirmwareField = step.Kind == PerfApplyStepKind.WriteFirmwareField;

            bool issued;
            try
            {
                issued = await IssueAsync(step, backend, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                outcomes.Add(new PerfApplyStepOutcome(step, PerfApplyResult.Failed, ex.Message));
                // 模式/档位没切成功时后面的参数会写到错误的档上，必须整体中止。
                if (IsSwitch(step)) break;
                continue;
            }

            if (!issued)
            {
                outcomes.Add(new PerfApplyStepOutcome(step, PerfApplyResult.Failed, "命令下发失败"));
                if (IsSwitch(step)) break;
                continue;
            }

            bool? verdict;
            try { verdict = await backend.VerifyAsync(step, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                outcomes.Add(new PerfApplyStepOutcome(step, PerfApplyResult.Failed, ex.Message));
                if (IsSwitch(step)) break;
                continue;
            }
            outcomes.Add(verdict switch
            {
                true => new PerfApplyStepOutcome(step, PerfApplyResult.Confirmed),
                false => new PerfApplyStepOutcome(step, PerfApplyResult.Failed, "回读与目标不一致"),
                null => new PerfApplyStepOutcome(step, PerfApplyResult.Sent, "此刻无法判定是否生效"),
            });

            // 切换失败同样要中止：参数会落到别的档上。
            if (IsSwitch(step) && outcomes[^1].Result == PerfApplyResult.Failed) break;
        }

        return new PerfApplyOutcome(modeId, outcomes);
    }

    static bool IsSwitch(PerfApplyStep step) =>
        PerfApplyOutcome.IsSwitch(step);

    static Task<bool> IssueAsync(PerfApplyStep step, IPerfModeBackend backend, CancellationToken ct) => step.Kind switch
    {
        PerfApplyStepKind.SwitchBuiltIn =>
            backend.SwitchBuiltInAsync((PerfModeKind)step.Number, ct),
        PerfApplyStepKind.SwitchTurboSubMode =>
            backend.SwitchTurboSubModeAsync(step.Number == 1, ct),
        PerfApplyStepKind.SwitchFirmwareSlot =>
            backend.SwitchFirmwareSlotAsync(step.Number, ct),
        PerfApplyStepKind.WriteFirmwareField =>
            backend.WriteDetailFieldAsync(step.WireKey!, step.Value!, ct),
        PerfApplyStepKind.WriteFanCurve =>
            backend.WriteFanCurveAsync(step.IsCpuCurve, step.Curve!, ct),
        PerfApplyStepKind.ApplyGpuOverclock =>
            backend.ApplyGpuOverclockAsync(step.Number == 1,
                step.GpuOffsets?.Core ?? 0, step.GpuOffsets?.Memory ?? 0, ct),
        PerfApplyStepKind.ApplyFanBoost =>
            backend.SetFanBoostAsync(step.Number == 1, ct),
        PerfApplyStepKind.ApplyRefreshRate =>
            backend.SetRefreshRateAsync(step.Number, ct),
        PerfApplyStepKind.ApplyPowerPlan =>
            Task.FromResult(backend.SetPowerPlan(step.Value!)),
        PerfApplyStepKind.ApplyCpuBoost =>
            Task.FromResult(backend.SetCpuBoost(step.Number)),
        PerfApplyStepKind.ApplyPowerOverlay =>
            Task.FromResult(backend.SetPowerOverlay(step.Number)),
        _ => throw new ArgumentOutOfRangeException(nameof(step), step.Kind, "unknown apply step"),
    };
}
