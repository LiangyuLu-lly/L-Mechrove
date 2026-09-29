using MechrevoLite.Properties;

namespace MechrevoLite.Mode;

/// <summary>性能模式的界面文案：模式名、逐项结果、失败项列表。主界面、托盘与编辑器共用一份。</summary>
internal static class PerfModeText
{
    /// <summary>模式显示名：用户起的名字优先；内置模式用官方名；自定义模式默认「自定义 N」。</summary>
    public static string Name(PerfModeDefinition mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        if (!string.IsNullOrEmpty(mode.UserName)) return mode.UserName;
        return mode.Kind switch
        {
            PerfModeKind.Silent => Strings.Silent,
            PerfModeKind.Balanced => Strings.Balanced,
            PerfModeKind.SilentTurbo => Strings.SilentTurbo,
            PerfModeKind.Turbo => Strings.Turbo,
            _ => string.Format(Strings.CustomProfileN,
                PerfModeCollection.TryParseCustomOrdinal(mode.Id, out int ordinal) ? ordinal : 1),
        };
    }

    /// <summary>下拉框里的文字：被改成自定义档承载的内置模式带「已自定义」标记。</summary>
    public static string ComboText(PerfModeDefinition mode) =>
        mode.IsEmulatedBuiltIn ? string.Format(Strings.PerfModeCustomized, Name(mode)) : Name(mode);

    public static string StepName(PerfApplyStep step, bool amd)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Kind switch
        {
            PerfApplyStepKind.SwitchBuiltIn or PerfApplyStepKind.SwitchFirmwareSlot or PerfApplyStepKind.SwitchTurboSubMode
                => Strings.PerfStepSwitch,
            PerfApplyStepKind.WriteFanCurve => Strings.FanCurve + (step.IsCpuCurve ? " CPU" : " GPU"),
            PerfApplyStepKind.ApplyGpuOverclock => Strings.GpuOverclock,
            PerfApplyStepKind.ApplyFanBoost => Strings.ModeTuneFanBoost,
            PerfApplyStepKind.ApplyPowerPlan => Strings.PowerPlan,
            PerfApplyStepKind.ApplyCpuBoost => Strings.BoostMode,
            PerfApplyStepKind.ApplyPowerOverlay => Strings.ModeTunePowerMode,
            PerfApplyStepKind.ApplyRefreshRate => Strings.ModeTuneRefresh,
            PerfApplyStepKind.WriteFirmwareField => step.WireKey switch
            {
                "PL1" => Strings.CpuPl1,
                "PL2" => Strings.CpuPl2,
                "PL4" => amd ? Strings.CpuFppt : Strings.CpuPl4,
                "CpuTccOffsetSwitch" => Strings.CpuTempWall,
                "CpuTccOffset" => Strings.TempWallValue,
                "GpuConfigurableTGPTarget" => Strings.GpuTgp,
                "GpuDynamicBoostSwitch" => Strings.GpuDynamicBoost,
                "GpuDynamicBoost" => Strings.DynamicBoostValue,
                "OverClockingSwitch" => Strings.GpuOverclock,
                "GpuCoreClockOffsetOC" => Strings.CoreOffset,
                "GpuMemoryClockOffsetOC" => Strings.MemoryOffset,
                "FanSwitchSpeedEnabled" => Strings.FanSwitchSensitivity,
                "FanSwitchSpeed" => Strings.FanSwitchDelay,
                _ => step.WireKey ?? "?",
            },
            _ => step.Kind.ToString(),
        };
    }

    /// <summary>「已生效 N 项 · 已下发待验证 N 项 · 失败 N 项」。</summary>
    public static string Summary(PerfApplyOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        int confirmed = outcome.Steps.Count(s => s.Result == PerfApplyResult.Confirmed);
        int sent = outcome.Steps.Count(s => s.Result == PerfApplyResult.Sent);
        int failed = outcome.Steps.Count(s => s.Result == PerfApplyResult.Failed);
        return string.Format(Strings.PerfModeOutcome, confirmed, sent, failed);
    }

    /// <summary>失败项的名字（去重，逗号分隔）；没有失败返回空串。</summary>
    public static string FailedItems(PerfApplyOutcome outcome, bool amd)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return string.Join("、", outcome.Failed
            .Select(s => StepName(s.Step, amd) + (string.IsNullOrEmpty(s.Detail) ? "" : "（" + s.Detail + "）"))
            .Distinct());
    }

    /// <summary>逐项明细（编辑器状态行的悬停提示）。</summary>
    public static string Details(PerfApplyOutcome outcome, bool amd)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return string.Join(Environment.NewLine, outcome.Steps.Select(s =>
            (s.Result switch
            {
                PerfApplyResult.Confirmed => "✓ ",
                PerfApplyResult.Sent => "… ",
                _ => "✗ ",
            }) + StepName(s.Step, amd) + (string.IsNullOrEmpty(s.Detail) ? "" : "：" + s.Detail)));
    }
}
