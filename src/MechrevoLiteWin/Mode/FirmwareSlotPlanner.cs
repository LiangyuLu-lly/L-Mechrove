using System.Globalization;

namespace MechrevoLite.Mode;

/// <summary>一个固件自定义档的当前归属。<see cref="OwnerModeId"/> 为空表示这个档还没被应用侧占用。</summary>
public sealed record FirmwareSlotState(int Index, string? OwnerModeId, string? Signature, long LastUsedTicks)
{
    public bool IsFree => string.IsNullOrEmpty(OwnerModeId);
}

/// <summary>档位决策结果。</summary>
public sealed record FirmwareSlotPlan(int SlotIndex, bool NeedsParameterWrite, string Reason);

/// <summary>
/// 把用户模式映射到固件自定义档。
///
/// <para>固件只有 5 个自定义档（<c>CustomizeCtrl.GetCustomId()==9</c> 的机型 3 个，
/// 见 <c>MyFanManager_RamFan1p5.RefreshCurrentProfile</c>），但用户模式可以更多：
/// 「自定义 N」与「被改过固件级参数的内置模式」都要占档。策略是按最近使用轮转——
/// 命中已归属的档则切换即时，否则先重写该档参数（逐字段下发，约 1-2 秒）。</para>
///
/// <para>纯逻辑，不碰配置与硬件。</para>
/// </summary>
public static class FirmwareSlotPlanner
{
    /// <summary>固件自定义档数量：绝大多数机型 5 个。</summary>
    public const int DefaultSlotCount = 5;

    /// <summary>厂商 <c>CustomId==9</c> 机型的档位数。</summary>
    public const int ReducedSlotCount = 3;

    /// <summary>厂商档位上限（`M4P1`..`M4P5`）。</summary>
    public const int MaxSlotCount = 5;

    /// <summary>按厂商 CustomizeId 得到档位数。</summary>
    public static int SlotCountFor(int customizeId) =>
        customizeId == 9 ? ReducedSlotCount : DefaultSlotCount;

    /// <summary>
    /// 为 <paramref name="modeId"/> 选一个档。
    /// </summary>
    /// <param name="signature">该模式固件级参数的指纹（<see cref="PerfModeSettings.FirmwareSignature"/>）。</param>
    /// <param name="slots">当前各档归属，长度 = 机型档位数。</param>
    /// <param name="runningModeId">当前正在运行的模式 Id；它占的档不会被抢走。</param>
    public static FirmwareSlotPlan Plan(
        string modeId,
        string signature,
        IReadOnlyList<FirmwareSlotState> slots,
        string? runningModeId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(modeId);
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count == 0)
            throw new ArgumentException("该机型没有固件自定义档，调用方必须先判断可用性", nameof(slots));

        // 1) 已经有档归属这个模式：指纹一致就不必重写参数。
        foreach (FirmwareSlotState slot in slots)
        {
            if (!string.Equals(slot.OwnerModeId, modeId, StringComparison.Ordinal)) continue;
            bool stale = !string.Equals(slot.Signature, signature, StringComparison.Ordinal);
            return new FirmwareSlotPlan(slot.Index, stale,
                stale ? "slot owned but parameters changed" : "slot hit");
        }

        // 2) 有空档就用空档（取序号最小的，行为可预测）。
        FirmwareSlotState? free = slots.Where(s => s.IsFree).OrderBy(s => s.Index).FirstOrDefault();
        if (free is not null)
            return new FirmwareSlotPlan(free.Index, true, "free slot");

        // 3) 全满：抢最久未用的档，但绝不抢正在运行的模式占的档
        //    （抢了会把当前模式的参数改掉，用户会看到运行中的模式突然变样）。
        List<FirmwareSlotState> candidates = slots
            .Where(s => runningModeId is null
                || !string.Equals(s.OwnerModeId, runningModeId, StringComparison.Ordinal))
            .ToList();
        if (candidates.Count == 0) candidates = slots.ToList();   // 只有一个档且正在运行

        FirmwareSlotState victim = candidates
            .OrderBy(s => s.LastUsedTicks)
            .ThenBy(s => s.Index)
            .First();
        return new FirmwareSlotPlan(victim.Index, true, "evicted least-recently-used slot");
    }

    /// <summary>应用决策后的新归属表（调用方落盘用）。</summary>
    public static IReadOnlyList<FirmwareSlotState> Assign(
        IReadOnlyList<FirmwareSlotState> slots, int slotIndex, string modeId, string? signature, long nowTicks)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentException.ThrowIfNullOrEmpty(modeId);
        var result = new List<FirmwareSlotState>(slots.Count);
        foreach (FirmwareSlotState slot in slots)
        {
            if (slot.Index == slotIndex)
                result.Add(new FirmwareSlotState(slot.Index, modeId, signature, nowTicks));
            else if (string.Equals(slot.OwnerModeId, modeId, StringComparison.Ordinal))
                // 同一个模式不能同时占两个档（换档后旧档要释放，否则档位表会漂）。
                result.Add(new FirmwareSlotState(slot.Index, null, null, slot.LastUsedTicks));
            else
                result.Add(slot);
        }
        return result;
    }

    /// <summary>删除模式后释放它占的档。</summary>
    public static IReadOnlyList<FirmwareSlotState> Release(IReadOnlyList<FirmwareSlotState> slots, string modeId)
    {
        ArgumentNullException.ThrowIfNull(slots);
        return slots
            .Select(s => string.Equals(s.OwnerModeId, modeId, StringComparison.Ordinal)
                ? new FirmwareSlotState(s.Index, null, null, s.LastUsedTicks)
                : s)
            .ToList();
    }

    internal static string OwnerKey(int slotIndex) =>
        "perf_slot_owner_" + slotIndex.ToString(CultureInfo.InvariantCulture);

    internal static string SignatureKey(int slotIndex) =>
        "perf_slot_sig_" + slotIndex.ToString(CultureInfo.InvariantCulture);

    internal static string LastUsedKey(int slotIndex) =>
        "perf_slot_used_" + slotIndex.ToString(CultureInfo.InvariantCulture);
}
