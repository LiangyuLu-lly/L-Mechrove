using System.Globalization;

namespace MechrevoLite.Mode;

/// <summary>一步下发动作的种类。</summary>
public enum PerfApplyStepKind
{
    /// <summary>原样切内置模式（<c>OPERATING_*_MODE</c>）。</summary>
    SwitchBuiltIn,

    /// <summary>狂暴子模式（静音狂暴 / 超频狂暴）。</summary>
    SwitchTurboSubMode,

    /// <summary>切到固件自定义档。</summary>
    SwitchFirmwareSlot,

    /// <summary>单字段 <c>SET_OPERATING_MODE_DETAIL</c>。</summary>
    WriteFirmwareField,

    /// <summary>风扇曲线（16 点占空比）。</summary>
    WriteFanCurve,

    /// <summary>风扇增强开关。</summary>
    ApplyFanBoost,

    /// <summary>Windows 电源计划。</summary>
    ApplyPowerPlan,

    /// <summary>处理器睿频模式。</summary>
    ApplyCpuBoost,

    /// <summary>Windows 电源模式覆盖层。</summary>
    ApplyPowerOverlay,

    /// <summary>屏幕刷新率。</summary>
    ApplyRefreshRate,

    /// <summary>内置模式的显卡超频（GCU 路径：总闸 + 核心/显存偏移）。</summary>
    ApplyGpuOverclock,
}

/// <summary>
/// 一步下发动作。<see cref="Verify"/> 说明这一步该用什么独立判据确认生效——
/// 绝不用 <c>Fan/Status</c> 回读当作硬件生效的证明（见 gcu-modes-and-profiles.md §1.3）。
/// </summary>
public sealed record PerfApplyStep(
    PerfApplyStepKind Kind,
    string? WireKey = null,
    string? Value = null,
    int Number = 0,
    bool IsCpuCurve = false,
    int[]? Curve = null,
    PerfApplyVerify Verify = PerfApplyVerify.ServiceEcho,
    (int Core, int Memory)? GpuOffsets = null)
{
    public override string ToString() => Kind switch
    {
        PerfApplyStepKind.WriteFirmwareField => $"{Kind}:{WireKey}={Value}",
        PerfApplyStepKind.WriteFanCurve => $"{Kind}:{(IsCpuCurve ? "CPU" : "GPU")}",
        PerfApplyStepKind.ApplyGpuOverclock => $"{Kind}:{Number}:{GpuOffsets?.Core}/{GpuOffsets?.Memory}",
        _ => $"{Kind}:{Number}",
    };
}

/// <summary>这一步的生效判据。</summary>
public enum PerfApplyVerify
{
    /// <summary>只有服务端回显可用（记录为「已下发」，不谎称已生效）。</summary>
    ServiceEcho,

    /// <summary>模式/档位回读（<c>OperatingMode</c> + <c>CustomProfileIndex</c>）。</summary>
    ModeReadback,

    /// <summary>CPU 封装功耗（RAPL）——只有负载足够时才判得出来。</summary>
    CpuPackagePower,

    /// <summary>NVAPI / NVML 的显卡功率墙回读。</summary>
    GpuPowerLimit,

    /// <summary>NVIDIA 驱动的频率偏移回读。</summary>
    GpuClockOffset,

    /// <summary>风扇占空比 / 转速跟随。</summary>
    FanDuty,

    /// <summary><c>PowerGetEffectiveOverlayScheme</c>。</summary>
    PowerOverlay,

    /// <summary>活动电源计划 GUID 回读。</summary>
    PowerPlan,

    /// <summary>睿频索引回读。</summary>
    CpuBoost,

    /// <summary>当前刷新率回读。</summary>
    RefreshRate,
}

/// <summary>
/// 下发时需要知道的机器能力快照。纯数据，便于测试构造。
/// </summary>
public sealed record PerfModeCapabilities
{
    /// <summary>AMD 平台（只影响界面标签；线上键名由 <c>MechrevoService.SetCustomDetail</c> 换算）。</summary>
    public bool UsesAmdPowerFields { get; init; }

    public bool SupportsPl1 { get; init; } = true;
    public bool SupportsPl2 { get; init; } = true;
    public bool SupportsPl4 { get; init; } = true;
    public bool SupportsTcc { get; init; } = true;
    public bool SupportsTgp { get; init; } = true;
    public bool SupportsDynamicBoost { get; init; } = true;
    public bool SupportsGpuOverclock { get; init; } = true;
    public bool SupportsFanCurve { get; init; } = true;
    public bool SupportsFanBoost { get; init; } = true;
    public bool SupportsFanSwitchSpeed { get; init; } = true;
    public bool SupportsRefreshRate { get; init; } = true;

    /// <summary>该机型是否有静音狂暴子模式。</summary>
    public bool SupportsTurboSubMode { get; init; } = true;

    public static readonly PerfModeCapabilities All = new();
}

/// <summary>
/// 把一个模式定义翻译成有序的下发步骤。**纯函数**，不碰硬件、不读配置。
///
/// <para>顺序纪律（依据 gcu-modes-and-profiles.md）：</para>
/// <list type="number">
/// <item>先切模式/档位——参数是写给「当前运行的那个档」的；</item>
/// <item>开关字段排在数值字段前面（关着的时候服务端会忽略数值）；</item>
/// <item>每个固件字段单独一步：同一包里带多个后段字段只有第一个生效；</item>
/// <item>风扇增强排在模式切换之后——切内置模式会把它强制清零；</item>
/// <item>应用侧项最后，且电源计划在睿频之前（睿频写的是活动计划的索引）。</item>
/// </list>
/// </summary>
public static class PerfModeApplyPlanner
{
    /// <summary>逐字段下发的最小间隔。服务端实测同包多字段会丢，连发太快也会丢。</summary>
    public const int FieldDelayMs = 120;

    /// <param name="includeSwitch">
    /// false = 模式已经在运行、只下发参数（编辑器的增量改动）。此时 <paramref name="mode"/> 的
    /// <see cref="PerfModeDefinition.Settings"/> 只应包含变化了的项（<see cref="PerfModeResolver.Delta"/>）。
    /// </param>
    public static IReadOnlyList<PerfApplyStep> Build(
        PerfModeDefinition mode,
        FirmwareSlotPlan? slotPlan,
        PerfModeCapabilities caps,
        bool includeSwitch = true,
        PerfModeRoute? routeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(caps);

        var steps = new List<PerfApplyStep>(16);
        PerfModeSettings s = mode.Settings;
        // 增量下发时 Settings 只含变化项，由它推出的路线不可靠：调用方传入完整模式的路线。
        PerfModeRoute route = routeOverride ?? mode.Route;

        // ---- 1. 模式 / 档位 ----
        if (route == PerfModeRoute.FirmwareSlot)
        {
            if (slotPlan is null)
                throw new ArgumentNullException(nameof(slotPlan),
                    "走固件自定义档的模式必须先做档位决策");
            if (includeSwitch)
                steps.Add(new PerfApplyStep(PerfApplyStepKind.SwitchFirmwareSlot,
                    Number: slotPlan.SlotIndex, Verify: PerfApplyVerify.ModeReadback));
        }
        else if (includeSwitch)
        {
            steps.Add(new PerfApplyStep(PerfApplyStepKind.SwitchBuiltIn,
                Number: (int)mode.Kind, Verify: PerfApplyVerify.ModeReadback));
            // 静音狂暴与狂暴共用固件 Turbo 档，靠子模式区分。
            if (caps.SupportsTurboSubMode
                && mode.Kind is PerfModeKind.Turbo or PerfModeKind.SilentTurbo)
                steps.Add(new PerfApplyStep(PerfApplyStepKind.SwitchTurboSubMode,
                    Number: mode.Kind == PerfModeKind.SilentTurbo ? 1 : 0));
        }

        // ---- 2. 固件字段 ----
        // 只有「需要重写档位参数」时才逐字段下发；命中已归属的档就跳过，省掉 1-2 秒。
        bool writeFirmware = route == PerfModeRoute.FirmwareSlot
            && slotPlan is { NeedsParameterWrite: true };

        if (writeFirmware)
        {
            // 开关先行
            if (caps.SupportsTcc && s.TccOn.HasValue)
                steps.Add(Field("CpuTccOffsetSwitch", s.TccOn.Value ? 1 : 0));
            if (caps.SupportsDynamicBoost && s.GpuDynamicBoostOn.HasValue)
                steps.Add(Field("GpuDynamicBoostSwitch", s.GpuDynamicBoostOn.Value ? 1 : 0));
            if (caps.SupportsGpuOverclock && s.GpuOverclockOn.HasValue)
                steps.Add(Field("OverClockingSwitch", s.GpuOverclockOn.Value ? 1 : 0));
            if (caps.SupportsFanSwitchSpeed && s.FanSwitchSpeedOn.HasValue)
                steps.Add(Field("FanSwitchSpeedEnabled", s.FanSwitchSpeedOn.Value ? 1 : 0));

            // 功耗墙：厂商会夹逼 PL2>=PL1、PL4>=PL2，所以升序下发，避免中间态被改写。
            // 键名一律用服务层键（PL1/PL2/PL4/CpuTccOffset）：MechrevoService.SetCustomDetail
            // 负责换成 AMD 键、PL4 折半与 Tcc 目标温度→原始偏移，这里再换一遍就会被拒或折半两次。
            if (caps.SupportsPl1 && s.Pl1.HasValue)
                steps.Add(Field("PL1", s.Pl1.Value, PerfApplyVerify.CpuPackagePower));
            if (caps.SupportsPl2 && s.Pl2.HasValue)
                steps.Add(Field("PL2", s.Pl2.Value, PerfApplyVerify.CpuPackagePower));
            if (caps.SupportsPl4 && s.Pl4.HasValue)
                steps.Add(Field("PL4", s.Pl4.Value, PerfApplyVerify.CpuPackagePower));

            if (caps.SupportsTcc && s.TccTarget.HasValue)
                steps.Add(Field("CpuTccOffset", s.TccTarget.Value));
            if (caps.SupportsTgp && s.GpuTgp.HasValue)
                steps.Add(Field("GpuConfigurableTGPTarget", s.GpuTgp.Value, PerfApplyVerify.GpuPowerLimit));
            if (caps.SupportsDynamicBoost && s.GpuDynamicBoost.HasValue)
                steps.Add(Field("GpuDynamicBoost", s.GpuDynamicBoost.Value, PerfApplyVerify.GpuPowerLimit));
            if (caps.SupportsGpuOverclock && s.GpuCoreOffset.HasValue)
                steps.Add(Field("GpuCoreClockOffsetOC", s.GpuCoreOffset.Value, PerfApplyVerify.GpuClockOffset));
            if (caps.SupportsGpuOverclock && s.GpuMemoryOffset.HasValue)
                steps.Add(Field("GpuMemoryClockOffsetOC", s.GpuMemoryOffset.Value, PerfApplyVerify.GpuClockOffset));
            if (caps.SupportsFanSwitchSpeed && s.FanSwitchSpeedMs.HasValue)
                steps.Add(Field("FanSwitchSpeed", s.FanSwitchSpeedMs.Value));

            if (caps.SupportsFanCurve && s.CpuFanDuty is { Length: 16 })
                steps.Add(new PerfApplyStep(PerfApplyStepKind.WriteFanCurve,
                    IsCpuCurve: true, Curve: s.CpuFanDuty, Verify: PerfApplyVerify.FanDuty));
            if (caps.SupportsFanCurve && s.GpuFanDuty is { Length: 16 })
                steps.Add(new PerfApplyStep(PerfApplyStepKind.WriteFanCurve,
                    IsCpuCurve: false, Curve: s.GpuFanDuty, Verify: PerfApplyVerify.FanDuty));
        }
        else if (route == PerfModeRoute.BuiltIn)
        {
            // 内置路线：只下发那些在内置模式下**确实生效**的项
            // （PL/TGP/DB/风扇曲线被固件门控，写了会被忽略——绝不在这里发）。
            if (caps.SupportsTcc && s.TccOn.HasValue)
                steps.Add(Field("CpuTccOffsetSwitch", s.TccOn.Value ? 1 : 0));
            if (caps.SupportsFanSwitchSpeed && s.FanSwitchSpeedOn.HasValue)
                steps.Add(Field("FanSwitchSpeedEnabled", s.FanSwitchSpeedOn.Value ? 1 : 0));
            if (caps.SupportsTcc && s.TccTarget.HasValue)
                steps.Add(Field("CpuTccOffset", s.TccTarget.Value));
            if (caps.SupportsFanSwitchSpeed && s.FanSwitchSpeedMs.HasValue)
                steps.Add(Field("FanSwitchSpeed", s.FanSwitchSpeedMs.Value));

            // 显卡超频在内置模式下只能走 GCU（直连 NVAPI 在非自定义模式下挂起），
            // 三个字段合成一步：总闸 + 两个偏移，与官方狂暴自动超频同一路径。
            if (caps.SupportsGpuOverclock
                && (s.GpuOverclockOn.HasValue || s.GpuCoreOffset.HasValue || s.GpuMemoryOffset.HasValue))
            {
                bool on = s.GpuOverclockOn ?? true;
                steps.Add(new PerfApplyStep(PerfApplyStepKind.ApplyGpuOverclock,
                    Number: on ? 1 : 0,
                    GpuOffsets: on ? (s.GpuCoreOffset ?? 0, s.GpuMemoryOffset ?? 0) : (0, 0),
                    Verify: PerfApplyVerify.GpuClockOffset));
            }
        }

        // ---- 3. 风扇增强：必须排在模式切换之后 ----
        // 厂商 UserSet_Mode1/2/3 里每次切内置模式都会 UserSet_FanBoost(0)。
        if (caps.SupportsFanBoost && s.FanBoost.HasValue)
            steps.Add(new PerfApplyStep(PerfApplyStepKind.ApplyFanBoost,
                Number: s.FanBoost.Value ? 1 : 0));

        // ---- 4. 应用侧 ----
        // 顺序：电源计划 → 电源模式覆盖层 → 睿频。覆盖层只在「平衡」计划下生效（设置它会先切回平衡计划），
        // 睿频写的是活动计划的 AC/DC 索引，所以必须最后写，写到最终的那个计划上。
        bool customPlan = !string.IsNullOrEmpty(s.PowerPlanGuid);
        if (customPlan)
            steps.Add(new PerfApplyStep(PerfApplyStepKind.ApplyPowerPlan,
                Value: s.PowerPlanGuid, Verify: PerfApplyVerify.PowerPlan));
        // 用户明确选了平衡以外的计划时不再设覆盖层：设覆盖层会把计划切回平衡，等于悄悄撤销用户的选择。
        if (s.WindowsPowerMode.HasValue && (!customPlan || IsBalancedPlan(s.PowerPlanGuid)))
            steps.Add(new PerfApplyStep(PerfApplyStepKind.ApplyPowerOverlay,
                Number: s.WindowsPowerMode.Value, Verify: PerfApplyVerify.PowerOverlay));
        if (s.CpuBoost.HasValue)
            steps.Add(new PerfApplyStep(PerfApplyStepKind.ApplyCpuBoost,
                Number: s.CpuBoost.Value, Verify: PerfApplyVerify.CpuBoost));
        if (caps.SupportsRefreshRate && s.RefreshHz is > 0)
            steps.Add(new PerfApplyStep(PerfApplyStepKind.ApplyRefreshRate,
                Number: s.RefreshHz.Value, Verify: PerfApplyVerify.RefreshRate));

        return steps;
    }

    /// <summary>Windows「平衡」电源计划（覆盖层唯一生效的计划）。</summary>
    internal const string BalancedPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

    internal static bool IsBalancedPlan(string? guid) =>
        Guid.TryParse(guid, out Guid g) && g == new Guid(BalancedPlanGuid);

    static PerfApplyStep Field(string wireKey, int value, PerfApplyVerify verify = PerfApplyVerify.ServiceEcho) =>
        new(PerfApplyStepKind.WriteFirmwareField, wireKey,
            value.ToString(CultureInfo.InvariantCulture), value, Verify: verify);

    /// <summary>
    /// 一个字段是不是「开关」（必须排在同族数值字段之前）。给测试与诊断用。
    /// </summary>
    internal static bool IsGateField(string? wireKey) => wireKey is
        "CpuTccOffsetSwitch" or "GpuDynamicBoostSwitch" or "OverClockingSwitch" or "FanSwitchSpeedEnabled";
}
