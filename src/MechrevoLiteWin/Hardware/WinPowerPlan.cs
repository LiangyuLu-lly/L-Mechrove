using System.Runtime.InteropServices;

namespace MechrevoLite.Hardware;

/// <summary>
/// Windows 电源计划 / 睿频模式（PROCESSOR_BOOST_MODE）——按自定义性能档独立保存与应用。
/// 机制（原 G-Helper PowerNative，仅借用协议不用其 UI）：
///   电源计划 = PowerSetActiveScheme(GUID)；睿频 = 修改当前活动计划的 CPU 子组 BOOST 索引（0-6）。
/// 每档配置持久化在 AppConfig：custom{idx}_plan / custom{idx}_boost。
/// </summary>
public static class WinPowerPlan
{
    const int ACCESS_SCHEME = 16;
    internal const int CustomProfileCount = 4;

    internal readonly record struct ProfileSettings(string Plan, int Boost);

    [DllImport("powrprof.dll")]
    static extern uint PowerEnumerate(IntPtr RootPowerKey, IntPtr SchemeGuid, IntPtr SubGroupOfPowerSettingsGuid, uint AccessFlags, uint Index, IntPtr Buffer, ref uint BufferSize);

    [DllImport("powrprof.dll")]
    static extern uint PowerSetActiveScheme(IntPtr RootPowerKey, ref Guid SchemeGuid);

    [DllImport("powrprof.dll")]
    static extern uint PowerGetActiveScheme(IntPtr UserPowerKey, out IntPtr ActivePolicyGuid);

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
        ("禁用睿频（CPU 不超基础频率）", 0),
        ("启用睿频（默认，系统自动）", 1),
        ("激进睿频（性能最强，发热最高）", 2),
        ("效率睿频（省电优先，发热低）", 3),
        ("高效激进（性能与省电平衡）", 4),
        ("激进·保底（先保基准频率再加速）", 5),
        ("高效·保底（保底 + 省电）", 6),
    };

    internal static string GetProfilePlanKey(int index) => "custom" + ValidateProfileIndex(index) + "_plan";

    internal static string GetProfileBoostKey(int index) => "custom" + ValidateProfileIndex(index) + "_boost";

    internal static ProfileSettings GetOrCreateProfileSettings(int index)
    {
        string planKey = GetProfilePlanKey(index);
        string boostKey = GetProfileBoostKey(index);
        string plan = AppConfig.GetString(planKey) ?? "";
        int boost = AppConfig.Get(boostKey, -1);
        bool changed = false;

        if (string.IsNullOrWhiteSpace(plan))
        {
            plan = GetActivePlan();
            if (!string.IsNullOrWhiteSpace(plan))
            {
                AppConfig.Set(planKey, plan);
                changed = true;
            }
        }

        if (boost is < 0 or >= 7)
        {
            boost = Math.Clamp(GetBoost(), 0, BoostModes.Length - 1);
            AppConfig.Set(boostKey, boost);
            changed = true;
        }

        if (changed) AppConfig.Flush();
        return new ProfileSettings(plan, boost);
    }

    internal static void EnsureProfileSettings()
    {
        for (int index = 0; index < CustomProfileCount; index++)
            _ = GetOrCreateProfileSettings(index);
    }

    private static int ValidateProfileIndex(int index)
    {
        if (index is < 0 or >= CustomProfileCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return index;
    }

    // 内置常用计划（Win11 上 PowerEnumerate 可能只返回当前可见计划——合并补充，去重）
    static readonly (string Guid, string Name)[] WellKnownPlans =
    {
        ("381b4222-f694-41f0-9685-ff5bb260df2e", "平衡"),
        ("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "高性能"),
        ("a1841308-3541-4fab-bc81-f71556f20b4a", "节能"),
        ("e9a42b02-d5df-448d-aa00-03f14749eb61", "卓越性能"),
    };

    /// <summary>枚举系统全部电源计划（GUID, 名称），合并内置常用计划；名称只用中文（绝不显示 GUID/系统原始名）。</summary>
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
        foreach (var wk in WellKnownPlans)
            if (!plans.Any(p => p.Guid.Equals(wk.Guid, StringComparison.OrdinalIgnoreCase)))
                plans.Add(wk);
        return plans;
    }

    public static string GetActivePlan()
    {
        return TryGetActivePlan(out Guid plan) ? plan.ToString() : "";
    }

    internal static bool IsPlanConfirmed(string requested, string actual) =>
        Guid.TryParse(requested, out Guid requestedGuid) &&
        Guid.TryParse(actual, out Guid actualGuid) &&
        requestedGuid == actualGuid;

    internal static bool IsBoostConfirmed(int requested, int ac, int dc) =>
        requested is >= 0 and < 7 && ac == requested && dc == requested;

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

            if (TryGetActivePlan(out Guid current) && current == target)
                return true;

            uint status = PowerSetActiveScheme(IntPtr.Zero, ref target);
            bool readBack = TryGetActivePlan(out Guid actual);
            bool confirmed = status == 0 && readBack && actual == target;
            Logger.WriteLine($"WinPowerPlan SetActivePlan {guid} -> status={status} actual={(readBack ? actual : "unavailable")} confirmed={confirmed}");
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

    /// <summary>测试接缝：非 null 时代替真实 powrprof 应用（测试绝不改动机器当前电源计划）。</summary>
    internal static Func<int, bool>? ApplyProfileOverride { get; set; }

    /// <summary>应用指定自定义档保存的电源计划 + 睿频（切换档位时调用）。</summary>
    public static bool ApplyProfile(int index)
    {
        Func<int, bool>? overrideAction = ApplyProfileOverride;
        if (overrideAction is not null) return overrideAction(index);
        ProfileSettings settings = GetOrCreateProfileSettings(index);
        bool planConfirmed = string.IsNullOrEmpty(settings.Plan) || SetActivePlan(settings.Plan);
        bool boostConfirmed = planConfirmed && SetBoost(settings.Boost);
        bool confirmed = planConfirmed && boostConfirmed;
        Logger.WriteLine($"WinPowerPlan ApplyProfile({index}): plan={settings.Plan} boost={settings.Boost} confirmed={confirmed}");
        return confirmed;
    }
}
