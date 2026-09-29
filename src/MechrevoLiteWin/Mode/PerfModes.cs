using System.Globalization;
using System.Text;

namespace MechrevoLite.Mode;

/// <summary>
/// 用户可见的性能模式种类。四个内置种类各自唯一，<see cref="Custom"/> 可以有多个实例。
/// </summary>
public enum PerfModeKind
{
    Silent = 0,
    Balanced = 1,
    SilentTurbo = 2,
    Turbo = 3,
    Custom = 4,
}

/// <summary>
/// 一个模式要走哪条下发路线。
///
/// <para>判据来自 <c>docs/hardware/gcu-modes-and-profiles.md</c> 的真机实证：固件只在
/// 「自定义模式标志位 EC <c>0x726</c> bit7 置位」时才采用功耗墙设置寄存器、RAM 风扇表与
/// NVAPI 的 TGP 目标值，而该 bit 只有 <c>SetUserProfile(3)</c>（自定义模式）会置位。
/// 所以内置模式里改这些项会「回读通过、硬件不生效」——必须改由自定义档承载。</para>
/// </summary>
public enum PerfModeRoute
{
    /// <summary>原样下发 <c>OPERATING_*_MODE</c>，完全保持官方行为。</summary>
    BuiltIn = 0,

    /// <summary>切到固件自定义档承载（自定义模式本身，或被用户改过固件级参数的内置模式）。</summary>
    FirmwareSlot = 1,
}

/// <summary>
/// 一个模式的完整参数集。**<c>null</c> = 该项未被用户自定义**，沿用官方出厂行为。
///
/// <para>字段分三层：<see cref="NeedsFirmwareSlot"/> 里的固件门控项、内置模式下也生效的项、
/// 以及与 GCU 无关的应用侧项。分层依据见 <c>docs/hardware/gcu-modes-and-profiles.md</c> §5。</para>
/// </summary>
public sealed record PerfModeSettings
{
    public static readonly PerfModeSettings Default = new();

    // ---- 固件门控（只有自定义档里才真正生效）----

    /// <summary>CPU 长时功耗墙（AMD 机型映射到 SPL）。</summary>
    public int? Pl1 { get; init; }

    /// <summary>CPU 短时功耗墙（AMD 机型映射到 sPPT）。</summary>
    public int? Pl2 { get; init; }

    /// <summary>CPU 瞬时功耗墙（AMD 机型映射到 fPPT）。</summary>
    public int? Pl4 { get; init; }

    /// <summary>显卡 cTGP 目标瓦数。</summary>
    public int? GpuTgp { get; init; }

    public bool? GpuDynamicBoostOn { get; init; }
    public int? GpuDynamicBoost { get; init; }

    /// <summary>CPU 风扇曲线的 16 点占空比；温度点由固件表决定，不可改。</summary>
    public int[]? CpuFanDuty { get; init; }

    public int[]? GpuFanDuty { get; init; }

    // ---- 内置模式下也生效 ----

    public bool? TccOn { get; init; }
    public int? TccTarget { get; init; }
    public bool? GpuOverclockOn { get; init; }
    public int? GpuCoreOffset { get; init; }
    public int? GpuMemoryOffset { get; init; }
    public bool? FanBoost { get; init; }
    public bool? FanSwitchSpeedOn { get; init; }
    public int? FanSwitchSpeedMs { get; init; }

    // ---- 应用侧（与 GCU 无关）----

    /// <summary>Windows 电源模式覆盖层：0=最佳能效 1=平衡 2=最佳性能。</summary>
    public int? WindowsPowerMode { get; init; }

    /// <summary>处理器睿频模式索引 0..6。</summary>
    public int? CpuBoost { get; init; }

    /// <summary>Windows 电源计划 GUID。</summary>
    public string? PowerPlanGuid { get; init; }

    public int? RefreshHz { get; init; }

    /// <summary>用户一项都没改过。</summary>
    public bool IsUntouched =>
        !NeedsFirmwareSlot && !HasBuiltInSafeOverride && !HasAppSideOverride;

    /// <summary>
    /// 是否存在**固件门控**项。为 true 时这个模式必须跑在固件自定义档上，
    /// 否则写进去会被固件忽略（真机实证：平衡模式下 PL1=40 实测功耗仍 82.8 W）。
    /// </summary>
    public bool NeedsFirmwareSlot =>
        Pl1.HasValue || Pl2.HasValue || Pl4.HasValue ||
        GpuTgp.HasValue || GpuDynamicBoostOn.HasValue || GpuDynamicBoost.HasValue ||
        CpuFanDuty is not null || GpuFanDuty is not null;

    /// <summary>内置模式下也能就地生效的项里有没有覆盖。</summary>
    public bool HasBuiltInSafeOverride =>
        TccOn.HasValue || TccTarget.HasValue ||
        GpuOverclockOn.HasValue || GpuCoreOffset.HasValue || GpuMemoryOffset.HasValue ||
        FanBoost.HasValue || FanSwitchSpeedOn.HasValue || FanSwitchSpeedMs.HasValue;

    public bool HasAppSideOverride =>
        WindowsPowerMode.HasValue || CpuBoost.HasValue ||
        !string.IsNullOrEmpty(PowerPlanGuid) || RefreshHz.HasValue;

    /// <summary>
    /// 固件门控项的指纹。档位映射用它判断「这个档现在装的参数是不是这个模式要的」，
    /// 相同就不必重写参数（省掉 1-2 秒的逐字段下发）。
    /// </summary>
    public string FirmwareSignature()
    {
        var sb = new StringBuilder(96);
        void N(int? v) { sb.Append(v?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|'); }
        void B(bool? v) { sb.Append(v is null ? "-" : v.Value ? "1" : "0").Append('|'); }
        void A(int[]? v) { sb.Append(v is null ? "-" : string.Join(',', v)).Append('|'); }

        N(Pl1); N(Pl2); N(Pl4);
        N(GpuTgp); B(GpuDynamicBoostOn); N(GpuDynamicBoost);
        A(CpuFanDuty); A(GpuFanDuty);
        // Tcc / GPU 偏移在自定义档里也是档位参数的一部分，换档要重写。
        B(TccOn); N(TccTarget);
        B(GpuOverclockOn); N(GpuCoreOffset); N(GpuMemoryOffset);
        B(FanSwitchSpeedOn); N(FanSwitchSpeedMs);
        return sb.ToString();
    }
}

/// <summary>
/// 一个用户模式的定义。<see cref="Id"/> 是稳定标识（配置键与档位归属都用它），
/// 内置模式的 Id 固定，自定义模式的 Id 在新建时分配且不复用。
/// </summary>
public sealed record PerfModeDefinition(string Id, PerfModeKind Kind, string? UserName, PerfModeSettings Settings)
{
    /// <summary>内置模式的固定 Id。</summary>
    public static string BuiltInId(PerfModeKind kind) => kind switch
    {
        PerfModeKind.Silent => "silent",
        PerfModeKind.Balanced => "balanced",
        PerfModeKind.SilentTurbo => "silentturbo",
        PerfModeKind.Turbo => "turbo",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a built-in mode kind"),
    };

    public static string CustomId(int ordinal) =>
        "custom" + ordinal.ToString(CultureInfo.InvariantCulture);

    public bool IsCustom => Kind == PerfModeKind.Custom;

    /// <summary>这个模式该走哪条路线。</summary>
    public PerfModeRoute Route =>
        IsCustom || Settings.NeedsFirmwareSlot ? PerfModeRoute.FirmwareSlot : PerfModeRoute.BuiltIn;

    /// <summary>
    /// 被用户改过固件级参数的内置模式（「模拟内置模式」）。这类模式跑在自定义档上，
    /// 厂商 OSD/托盘会显示为自定义——必须如实告知用户。
    /// </summary>
    public bool IsEmulatedBuiltIn => !IsCustom && Settings.NeedsFirmwareSlot;

    public PerfModeDefinition With(PerfModeSettings settings) => this with { Settings = settings };
}

/// <summary>
/// 模式集合的纯逻辑操作：新增 / 改名 / 删除 / 排序。不碰配置与硬件，便于单测。
/// </summary>
public static class PerfModeCollection
{
    /// <summary>内置模式的固定顺序（与主界面分段顺序一致）。</summary>
    public static readonly PerfModeKind[] BuiltInOrder =
    {
        PerfModeKind.Silent, PerfModeKind.Balanced, PerfModeKind.SilentTurbo, PerfModeKind.Turbo,
    };

    /// <summary>自定义模式数量上限。固件档位只有 5 个，但应用侧可以多存、按最近使用轮转。</summary>
    public const int MaxCustomModes = 12;

    /// <summary>新建自定义模式时分配的序号：取当前未占用的最小序号，绝不复用在用序号。</summary>
    public static int NextCustomOrdinal(IEnumerable<PerfModeDefinition> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var used = new HashSet<int>();
        foreach (PerfModeDefinition mode in existing)
        {
            if (!mode.IsCustom) continue;
            if (TryParseCustomOrdinal(mode.Id, out int ordinal)) used.Add(ordinal);
        }
        for (int i = 1; i <= MaxCustomModes; i++)
            if (!used.Contains(i)) return i;
        return -1;
    }

    internal static bool TryParseCustomOrdinal(string id, out int ordinal)
    {
        ordinal = -1;
        if (string.IsNullOrEmpty(id) || !id.StartsWith("custom", StringComparison.Ordinal)) return false;
        return int.TryParse(id.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out ordinal);
    }

    /// <summary>
    /// 能不能再加一个自定义模式。上限不是固件档位数——超出档位数的模式靠映射轮转承载。
    /// </summary>
    public static bool CanAdd(IReadOnlyCollection<PerfModeDefinition> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        return existing.Count(m => m.IsCustom) < MaxCustomModes;
    }

    /// <summary>
    /// 能不能删。内置模式永远不能删；**最后一个自定义模式也不能删**——
    /// 「自定义」这一段必须始终有内容，否则主界面那一段会变成空按钮。
    /// </summary>
    public static bool CanRemove(IReadOnlyCollection<PerfModeDefinition> existing, string id)
    {
        ArgumentNullException.ThrowIfNull(existing);
        PerfModeDefinition? target = existing.FirstOrDefault(m => m.Id == id);
        if (target is null || !target.IsCustom) return false;
        return existing.Count(m => m.IsCustom) > 1;
    }

    /// <summary>用户名为空时的显示名由调用方本地化；这里只做裁剪与长度限制。</summary>
    public const int MaxUserNameLength = 16;

    public static string? SanitizeUserName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string trimmed = name.Trim();
        if (trimmed.Length > MaxUserNameLength) trimmed = trimmed[..MaxUserNameLength];
        return trimmed;
    }
}
