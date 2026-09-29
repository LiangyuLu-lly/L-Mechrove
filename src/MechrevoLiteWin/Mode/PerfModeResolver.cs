namespace MechrevoLite.Mode;

/// <summary>固件当前状态对应到哪个用户模式，以及要不要把固件拉回该模式真正的承载位置。</summary>
public sealed record PerfModeResolution(string ModeId, bool Redirect, string Reason);

/// <summary>
/// 运行时回读值快照（<c>Fan/Status</c> + <c>Fan/Table</c>）。只含**这台机器可调**的项；
/// 不可调或没报过的项为 null。用于把自定义档里现有的参数「落」进模式配置，
/// 使模式与档位解耦：模式换了档（被轮转挤掉后重新装入）也还是原来那组参数。
/// </summary>
public sealed record PerfLiveSnapshot(
    int? Pl1 = null,
    int? Pl2 = null,
    int? Pl4 = null,
    bool? TccOn = null,
    int? TccTarget = null,
    int? GpuTgp = null,
    bool? GpuDynamicBoostOn = null,
    int? GpuDynamicBoost = null,
    bool? FanSwitchSpeedOn = null,
    int? FanSwitchSpeedMs = null,
    int[]? CpuFanDuty = null,
    int[]? GpuFanDuty = null,
    bool? GpuOverclockOn = null,
    int? GpuCoreOffset = null,
    int? GpuMemoryOffset = null)
{
    /// <summary>只填用户没设过的项，用户设过的值永远优先。</summary>
    public PerfModeSettings FillNulls(PerfModeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with
        {
            Pl1 = settings.Pl1 ?? Pl1,
            Pl2 = settings.Pl2 ?? Pl2,
            Pl4 = settings.Pl4 ?? Pl4,
            TccOn = settings.TccOn ?? TccOn,
            TccTarget = settings.TccTarget ?? TccTarget,
            GpuTgp = settings.GpuTgp ?? GpuTgp,
            GpuDynamicBoostOn = settings.GpuDynamicBoostOn ?? GpuDynamicBoostOn,
            GpuDynamicBoost = settings.GpuDynamicBoost ?? GpuDynamicBoost,
            FanSwitchSpeedOn = settings.FanSwitchSpeedOn ?? FanSwitchSpeedOn,
            FanSwitchSpeedMs = settings.FanSwitchSpeedMs ?? FanSwitchSpeedMs,
            CpuFanDuty = settings.CpuFanDuty ?? CpuFanDuty,
            GpuFanDuty = settings.GpuFanDuty ?? GpuFanDuty,
            // 显卡超频三项作为一组：用户设过任一项就整组保留用户的，否则整组取回读。
            GpuOverclockOn = UserTouchedOverclock(settings) ? settings.GpuOverclockOn : GpuOverclockOn,
            GpuCoreOffset = UserTouchedOverclock(settings) ? settings.GpuCoreOffset : GpuCoreOffset,
            GpuMemoryOffset = UserTouchedOverclock(settings) ? settings.GpuMemoryOffset : GpuMemoryOffset,
        };
    }

    static bool UserTouchedOverclock(PerfModeSettings s) =>
        s.GpuOverclockOn.HasValue || s.GpuCoreOffset.HasValue || s.GpuMemoryOffset.HasValue;
}

/// <summary>
/// 纯逻辑：固件状态 → 用户模式。
///
/// <para>规则（依据 <c>docs/hardware/gcu-modes-and-profiles.md</c> 与厂商 Fn 键循环
/// <c>MyFanManager_RamFan1p5.ModeSwitchChanged</c>：平衡 → 狂暴 → 自定义 → 静音 → 平衡）：</para>
/// <list type="number">
/// <item>固件在内置模式 K：用户的 K 没被改成自定义档承载 → 跟随；被改过 → 拉回它的自定义档
///       （否则按 Fn 进了「平衡」却跑着官方平衡参数，用户的改动悄悄失效）。</item>
/// <item>固件在自定义档 n：档位归属表里有主人 → 跟随主人；没有主人（Fn 键或外部把固件切到了
///       应用没用过的档）→ 拉回最近用过的自定义模式。</item>
/// </list>
/// </summary>
public static class PerfModeResolver
{
    public static PerfModeResolution? Resolve(
        IReadOnlyList<PerfModeDefinition> modes,
        IReadOnlyList<FirmwareSlotState> slots,
        int operatingMode,
        int customProfileIndex,
        bool silentTurbo,
        string? lastCustomId)
    {
        ArgumentNullException.ThrowIfNull(modes);
        ArgumentNullException.ThrowIfNull(slots);
        if (operatingMode is < 0 or > 3) return null;

        if (operatingMode == 3)
        {
            string? owner = slots.FirstOrDefault(s => s.Index == customProfileIndex)?.OwnerModeId;
            PerfModeDefinition? ownerMode = owner is null
                ? null
                : modes.FirstOrDefault(m => m.Id == owner && m.Route == PerfModeRoute.FirmwareSlot);
            if (ownerMode is not null)
                return new PerfModeResolution(ownerMode.Id, false, $"custom slot {customProfileIndex} belongs to {ownerMode.Id}");

            PerfModeDefinition? fallback =
                modes.FirstOrDefault(m => m.IsCustom && m.Id == lastCustomId)
                ?? modes.FirstOrDefault(m => m.IsCustom);
            return fallback is null
                ? null
                : new PerfModeResolution(fallback.Id, true, $"custom slot {customProfileIndex} has no owner");
        }

        PerfModeKind kind = PerfModeMapping.FromFirmware(operatingMode, silentTurbo);
        PerfModeDefinition? builtIn = modes.FirstOrDefault(m => m.Kind == kind);
        if (builtIn is null) return null;
        return builtIn.Route == PerfModeRoute.FirmwareSlot
            ? new PerfModeResolution(builtIn.Id, true, $"{builtIn.Id} is customized and runs on a firmware slot")
            : new PerfModeResolution(builtIn.Id, false, "built-in mode");
    }

    /// <summary>
    /// 固件此刻是不是已经处在 <paramref name="mode"/> 的承载位置上。
    /// 内置路线看 OperatingMode（狂暴/静音狂暴再看子模式），自定义档路线看档位归属。
    /// </summary>
    public static bool IsFirmwareOn(
        PerfModeDefinition mode,
        IReadOnlyList<FirmwareSlotState> slots,
        int operatingMode,
        int customProfileIndex,
        bool silentTurbo,
        bool turboSubModeSupported)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(slots);
        if (mode.Route == PerfModeRoute.FirmwareSlot)
        {
            if (operatingMode != 3) return false;
            return slots.Any(s => s.Index == customProfileIndex
                && string.Equals(s.OwnerModeId, mode.Id, StringComparison.Ordinal));
        }
        if (operatingMode != PerfModeMapping.ToFirmwareOperatingMode(mode.Kind)) return false;
        if (!turboSubModeSupported || mode.Kind is not (PerfModeKind.Turbo or PerfModeKind.SilentTurbo)) return true;
        return silentTurbo == (mode.Kind == PerfModeKind.SilentTurbo);
    }

    /// <summary>
    /// 编辑器改动的增量：只含变化了的项。显卡超频三项在内置路线上是一步下发的，
    /// 任一项变化就三项一起带上，否则另外两项会被当成 0 写回去。
    /// </summary>
    public static PerfModeSettings Delta(PerfModeSettings before, PerfModeSettings after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        static T? Pick<T>(T? a, T? b) where T : struct => Nullable.Equals(a, b) ? null : b;
        static int[]? PickCurve(int[]? a, int[]? b) =>
            a is null && b is null ? null
            : a is not null && b is not null && a.SequenceEqual(b) ? null
            : b;

        bool ocChanged = !Nullable.Equals(before.GpuOverclockOn, after.GpuOverclockOn)
            || !Nullable.Equals(before.GpuCoreOffset, after.GpuCoreOffset)
            || !Nullable.Equals(before.GpuMemoryOffset, after.GpuMemoryOffset);

        return new PerfModeSettings
        {
            Pl1 = Pick(before.Pl1, after.Pl1),
            Pl2 = Pick(before.Pl2, after.Pl2),
            Pl4 = Pick(before.Pl4, after.Pl4),
            GpuTgp = Pick(before.GpuTgp, after.GpuTgp),
            GpuDynamicBoostOn = Pick(before.GpuDynamicBoostOn, after.GpuDynamicBoostOn),
            GpuDynamicBoost = Pick(before.GpuDynamicBoost, after.GpuDynamicBoost),
            CpuFanDuty = PickCurve(before.CpuFanDuty, after.CpuFanDuty),
            GpuFanDuty = PickCurve(before.GpuFanDuty, after.GpuFanDuty),
            TccOn = Pick(before.TccOn, after.TccOn),
            TccTarget = Pick(before.TccTarget, after.TccTarget),
            GpuOverclockOn = ocChanged ? after.GpuOverclockOn : null,
            GpuCoreOffset = ocChanged ? after.GpuCoreOffset : null,
            GpuMemoryOffset = ocChanged ? after.GpuMemoryOffset : null,
            FanBoost = Pick(before.FanBoost, after.FanBoost),
            FanSwitchSpeedOn = Pick(before.FanSwitchSpeedOn, after.FanSwitchSpeedOn),
            FanSwitchSpeedMs = Pick(before.FanSwitchSpeedMs, after.FanSwitchSpeedMs),
            WindowsPowerMode = Pick(before.WindowsPowerMode, after.WindowsPowerMode),
            CpuBoost = Pick(before.CpuBoost, after.CpuBoost),
            PowerPlanGuid = string.Equals(before.PowerPlanGuid, after.PowerPlanGuid, StringComparison.OrdinalIgnoreCase)
                ? null
                : after.PowerPlanGuid,
            RefreshHz = Pick(before.RefreshHz, after.RefreshHz),
        };
    }

    /// <summary>
    /// 把一个内置模式转成自定义档承载时要装进档位的完整参数：用户已设的项不动，
    /// 其余固件级项用厂商出厂值（<see cref="VendorModeDefaults"/>）补齐，再用运行时回读兜底。
    /// </summary>
    public static PerfModeSettings Materialize(PerfModeSettings user, PerfModeSettings vendorSeed, PerfLiveSnapshot? live)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(vendorSeed);
        PerfModeSettings merged = user with
        {
            Pl1 = user.Pl1 ?? vendorSeed.Pl1,
            Pl2 = user.Pl2 ?? vendorSeed.Pl2,
            Pl4 = user.Pl4 ?? vendorSeed.Pl4,
            GpuTgp = user.GpuTgp ?? vendorSeed.GpuTgp,
            GpuDynamicBoostOn = user.GpuDynamicBoostOn ?? vendorSeed.GpuDynamicBoostOn,
            GpuDynamicBoost = user.GpuDynamicBoost ?? vendorSeed.GpuDynamicBoost,
            CpuFanDuty = user.CpuFanDuty ?? vendorSeed.CpuFanDuty,
            GpuFanDuty = user.GpuFanDuty ?? vendorSeed.GpuFanDuty,
            TccOn = user.TccOn ?? vendorSeed.TccOn,
            TccTarget = user.TccTarget ?? vendorSeed.TccTarget,
            GpuOverclockOn = user.GpuOverclockOn ?? vendorSeed.GpuOverclockOn,
            GpuCoreOffset = user.GpuCoreOffset ?? vendorSeed.GpuCoreOffset,
            GpuMemoryOffset = user.GpuMemoryOffset ?? vendorSeed.GpuMemoryOffset,
            FanSwitchSpeedOn = user.FanSwitchSpeedOn ?? vendorSeed.FanSwitchSpeedOn,
            FanSwitchSpeedMs = user.FanSwitchSpeedMs ?? vendorSeed.FanSwitchSpeedMs,
        };
        return live is null ? merged : live.FillNulls(merged);
    }
}
