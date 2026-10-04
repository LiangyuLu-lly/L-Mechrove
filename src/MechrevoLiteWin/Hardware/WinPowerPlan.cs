using System.Runtime.InteropServices;

namespace MechrevoLite.Hardware;

/// <summary>
/// Windows 电源计划 / 睿频模式（PROCESSOR_BOOST_MODE）的读写与回读确认。按模式保存与应用由 PerfModeService 负责。
/// 机制（原 G-Helper PowerNative，仅借用协议不用其 UI）：
///   电源计划 = PowerSetActiveScheme(GUID)；睿频 = 修改当前活动计划的 CPU 子组 BOOST 索引（0-6）。
/// </summary>
public static class WinPowerPlan
{
    const int ACCESS_SCHEME = 16;

    [DllImport("powrprof.dll")]
    static extern uint PowerEnumerate(IntPtr RootPowerKey, IntPtr SchemeGuid, IntPtr SubGroupOfPowerSettingsGuid, uint AccessFlags, uint Index, IntPtr Buffer, ref uint BufferSize);

    [DllImport("powrprof.dll")]
    static extern uint PowerSetActiveScheme(IntPtr RootPowerKey, ref Guid SchemeGuid);

    [DllImport("powrprof.dll")]
    static extern uint PowerGetActiveScheme(IntPtr UserPowerKey, out IntPtr ActivePolicyGuid);

    [DllImport("powrprof.dll")]
    static extern uint PowerGetEffectiveOverlayScheme(out Guid EffectiveOverlayGuid);

    [DllImport("powrprof.dll")]
    static extern uint PowerWriteACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroup, ref Guid SettingGuid, int AcValueIndex);

    [DllImport("powrprof.dll")]
    static extern uint PowerWriteDCValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroup, ref Guid SettingGuid, int AcValueIndex);

    [DllImport("powrprof.dll")]
    static extern uint PowerReadACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroup, ref Guid SettingGuid, out IntPtr AcValueIndex);

    [DllImport("powrprof.dll")]
    static extern uint PowerReadDCValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroup, ref Guid SettingGuid, out IntPtr DcValueIndex);

    static readonly Guid GuidCpuSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");
    static readonly Guid GuidBoostSetting = new("be337238-0d82-4146-a960-4f3749d470c7");

    public static readonly (string Name, int Value)[] BoostModes =
    {
        // 短名：下拉框在 100% 缩放下也不截断（原长描述 204px > 下拉可用 201px，UI 审计实测）。
        ("禁用睿频", 0),
        ("启用睿频（默认）", 1),
        ("激进睿频", 2),
        ("效率睿频", 3),
        ("高效激进", 4),
        ("激进（保底）", 5),
        ("高效（保底）", 6),
    };

    internal const string UltimatePerformancePlanId = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    internal const string BalancedOverlayId = "00000000-0000-0000-0000-000000000000";
    static readonly Guid UltimatePerformanceGuid = new(UltimatePerformancePlanId);

    // 名称映射不代表该计划已经安装或受到当前系统支持。
    static readonly (string Guid, string Name)[] WellKnownPlans =
    {
        ("381b4222-f694-41f0-9685-ff5bb260df2e", "平衡"),
        ("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "高性能"),
        ("a1841308-3541-4fab-bc81-f71556f20b4a", "节能"),
        (UltimatePerformancePlanId, "卓越性能"),
    };

    /// <summary>枚举系统实际存在的电源计划（GUID, 名称）。</summary>
    public static List<(string Guid, string Name)> GetPlans()
    {
        var plans = new List<(string Guid, string Name)>();
        try
        {
            uint i = 0;
            while (true)
            {
                uint size = 16;
                IntPtr buf = Marshal.AllocHGlobal(16);
                uint hr = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, i, buf, ref size);
                if (hr != 0) { Marshal.FreeHGlobal(buf); break; }
                var guid = (Guid)Marshal.PtrToStructure(buf, typeof(Guid))!;
                Marshal.FreeHGlobal(buf);
                var wk = WellKnownPlans.FirstOrDefault(p => p.Guid.Equals(guid.ToString(), StringComparison.OrdinalIgnoreCase));
                plans.Add((guid.ToString(), wk.Guid is not null ? wk.Name : $"电源计划 {i + 1}"));
                i++;
            }
        }
        catch (Exception ex) { Logger.WriteLine("WinPowerPlan enum fail: " + ex.Message); }
        return plans;
    }

    public static string GetActivePlan()
    {
        return TryGetActivePlan(out Guid plan) ? plan.ToString() : "";
    }

    internal static bool IsPlanConfirmed(string requested, string actual, string? overlay = null)
    {
        if (!Guid.TryParse(requested, out Guid requestedGuid) ||
            !Guid.TryParse(actual, out Guid actualGuid) ||
            requestedGuid != actualGuid)
            return false;

        // 25H2 overlay slider can stay Balanced while PowerGetActiveScheme still reports 卓越性能.
        if (requestedGuid == UltimatePerformanceGuid &&
            Guid.TryParse(overlay, out _))
            return false;

        return true;
    }

    internal static bool IsBoostConfirmed(int requested, int ac, int dc) =>
        requested is >= 0 and < 7 && ac == requested && dc == requested;

    static bool TryGetEffectiveOverlay(out Guid overlay, out uint status)
    {
        overlay = Guid.Empty;
        status = uint.MaxValue;
        try
        {
            status = PowerGetEffectiveOverlayScheme(out overlay);
            return status == 0;
        }
        catch (EntryPointNotFoundException)
        {
            Logger.WriteLine("WinPowerPlan overlay API unavailable");
            return false;
        }
    }

    static bool TryGetActivePlan(out Guid plan)
    {
        plan = Guid.Empty;
        IntPtr pointer = IntPtr.Zero;
        try
        {
            uint status = PowerGetActiveScheme(IntPtr.Zero, out pointer);
            if (status != 0 || pointer == IntPtr.Zero)
            {
                Logger.WriteLine($"WinPowerPlan GetActivePlan failed: {status}");
                return false;
            }

            plan = Marshal.PtrToStructure<Guid>(pointer);
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("WinPowerPlan GetActivePlan fail: " + ex.Message);
            return false;
        }
        finally
        {
            if (pointer != IntPtr.Zero) Marshal.FreeHGlobal(pointer);
        }
    }

    static bool TryReadBoost(Guid plan, bool ac, out int value)
    {
        var subgroup = GuidCpuSubgroup;
        var setting = GuidBoostSetting;
        uint status = ac
            ? PowerReadACValueIndex(IntPtr.Zero, ref plan, ref subgroup, ref setting, out IntPtr raw)
            : PowerReadDCValueIndex(IntPtr.Zero, ref plan, ref subgroup, ref setting, out raw);
        value = status == 0 ? raw.ToInt32() : -1;
        return status == 0;
    }

    public static bool SetActivePlan(string guid)
    {
        try
        {
            if (!Guid.TryParse(guid, out Guid target))
            {
                Logger.WriteLine($"WinPowerPlan SetActivePlan rejected invalid GUID: {guid}");
                return false;
            }

            uint status = 0;
            if (!(TryGetActivePlan(out Guid current) && current == target))
                status = PowerSetActiveScheme(IntPtr.Zero, ref target);

            bool readBack = TryGetActivePlan(out Guid actual);
            bool overlayRead = TryGetEffectiveOverlay(out Guid overlay, out uint overlayStatus);
            bool confirmed = status == 0 && readBack &&
                IsPlanConfirmed(target.ToString(), actual.ToString(), overlayRead ? overlay.ToString() : null);
            Logger.WriteLine($"WinPowerPlan SetActivePlan FIELD-LOG requested={guid} status={status} actual={(readBack ? actual.ToString() : "unavailable")} overlay={(overlayRead ? overlay.ToString() : "unavailable")} overlayStatus={overlayStatus} confirmed={confirmed}");
            return confirmed;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("WinPowerPlan SetActivePlan fail: " + ex.Message);
            return false;
        }
    }

    /// <summary>写当前活动计划的睿频模式（AC+DC）。</summary>
    public static bool SetBoost(int boost)
    {
        try
        {
            if (boost is < 0 or >= 7 || !TryGetActivePlan(out Guid plan))
            {
                Logger.WriteLine($"WinPowerPlan SetBoost({boost}) rejected");
                return false;
            }

            var subgroup = GuidCpuSubgroup;
            var setting = GuidBoostSetting;
            uint acStatus = PowerWriteACValueIndex(IntPtr.Zero, ref plan, ref subgroup, ref setting, boost);
            uint dcStatus = PowerWriteDCValueIndex(IntPtr.Zero, ref plan, ref subgroup, ref setting, boost);
            uint activateStatus = PowerSetActiveScheme(IntPtr.Zero, ref plan);
            bool acRead = TryReadBoost(plan, ac: true, out int ac);
            bool dcRead = TryReadBoost(plan, ac: false, out int dc);
            bool confirmed = acStatus == 0 && dcStatus == 0 && activateStatus == 0 &&
                acRead && dcRead && IsBoostConfirmed(boost, ac, dc);
            Logger.WriteLine($"WinPowerPlan SetBoost({boost}) AC={acStatus}/{ac} DC={dcStatus}/{dc} activate={activateStatus} confirmed={confirmed}");
            return confirmed;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("WinPowerPlan SetBoost fail: " + ex.Message);
            return false;
        }
    }

    public static int GetBoost()
    {
        try
        {
            return TryGetActivePlan(out Guid plan) && TryReadBoost(plan, ac: true, out int value)
                ? value
                : 2;
        }
        catch { return 2; }
    }

    /// <summary>只读：活动计划的睿频模式（AC 与 DC 都读到才算）；读不到返回 false，不猜值。</summary>
    internal static bool TryGetBoost(out int ac, out int dc)
    {
        ac = dc = -1;
        try
        {
            return TryGetActivePlan(out Guid plan) &&
                TryReadBoost(plan, ac: true, out ac) &&
                TryReadBoost(plan, ac: false, out dc);
        }
        catch { return false; }
    }


}
