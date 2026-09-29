using System.Globalization;

namespace MechrevoLite.Mode;

/// <summary>
/// 性能模式的持久化。全部落在 <see cref="AppConfig"/>：
/// <list type="bullet">
/// <item><c>perf_customs</c> —— 自定义模式序号列表（逗号分隔，顺序即显示顺序）；</item>
/// <item><c>perf_name_&lt;id&gt;</c> —— 用户自定名；</item>
/// <item><c>perf_&lt;id&gt;_&lt;field&gt;</c> —— 单项覆盖，**键不存在就是「未自定义」**；</item>
/// <item><c>perf_slot_{owner,sig,used}_&lt;n&gt;</c> —— 固件档位归属表；</item>
/// <item><c>perf_active</c> —— 当前模式 Id。</item>
/// </list>
///
/// <para>「键不存在 = null = 沿用官方行为」这条语义是刻意的：用户没碰过的项绝不写入配置，
/// 于是「恢复默认」只需删键，也不会把某一版的默认值固化成用户设置。</para>
/// </summary>
public static class PerfModeStore
{
    internal const string CustomListKey = "perf_customs";
    internal const string ActiveKey = "perf_active";

    static string NameKey(string id) => "perf_name_" + id;
    static string FieldKey(string id, string field) => "perf_" + id + "_" + field;

    // ---- 单项读写：不存在即 null ----

    static int? GetInt(string id, string field)
    {
        string key = FieldKey(id, field);
        return AppConfig.Exists(key) ? AppConfig.Get(key) : null;
    }

    static bool? GetBool(string id, string field)
    {
        string key = FieldKey(id, field);
        return AppConfig.Exists(key) ? AppConfig.Get(key) != 0 : null;
    }

    static int[]? GetCurve(string id, string field)
    {
        string? text = AppConfig.GetString(FieldKey(id, field));
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var result = new int[16];
        for (int i = 0; i < result.Length; i++)
            result[i] = i < parts.Length && int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? Math.Clamp(v, 0, 100)
                : 0;
        return result;
    }

    static void PutInt(string id, string field, int? value)
    {
        string key = FieldKey(id, field);
        if (value.HasValue) AppConfig.Set(key, value.Value); else AppConfig.Remove(key);
    }

    static void PutBool(string id, string field, bool? value)
    {
        string key = FieldKey(id, field);
        if (value.HasValue) AppConfig.Set(key, value.Value ? 1 : 0); else AppConfig.Remove(key);
    }

    static void PutCurve(string id, string field, int[]? value)
    {
        string key = FieldKey(id, field);
        if (value is { Length: > 0 }) AppConfig.Set(key, string.Join(',', value)); else AppConfig.Remove(key);
    }

    /// <summary>读一个模式的参数集。</summary>
    public static PerfModeSettings LoadSettings(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        string? plan = AppConfig.GetString(FieldKey(id, "plan"));
        return new PerfModeSettings
        {
            Pl1 = GetInt(id, "pl1"),
            Pl2 = GetInt(id, "pl2"),
            Pl4 = GetInt(id, "pl4"),
            GpuTgp = GetInt(id, "tgp"),
            GpuDynamicBoostOn = GetBool(id, "dbon"),
            GpuDynamicBoost = GetInt(id, "db"),
            CpuFanDuty = GetCurve(id, "fancpu"),
            GpuFanDuty = GetCurve(id, "fangpu"),
            TccOn = GetBool(id, "tccon"),
            TccTarget = GetInt(id, "tcc"),
            GpuOverclockOn = GetBool(id, "ocon"),
            GpuCoreOffset = GetInt(id, "occore"),
            GpuMemoryOffset = GetInt(id, "ocmem"),
            FanBoost = GetBool(id, "fanboost"),
            FanSwitchSpeedOn = GetBool(id, "fsson"),
            FanSwitchSpeedMs = GetInt(id, "fss"),
            WindowsPowerMode = GetInt(id, "power"),
            CpuBoost = GetInt(id, "boost"),
            PowerPlanGuid = string.IsNullOrWhiteSpace(plan) ? null : plan,
            RefreshHz = GetInt(id, "hz"),
        };
    }

    /// <summary>写一个模式的参数集（null 项会把对应键删掉）。</summary>
    public static void SaveSettings(string id, PerfModeSettings settings)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(settings);
        PutInt(id, "pl1", settings.Pl1);
        PutInt(id, "pl2", settings.Pl2);
        PutInt(id, "pl4", settings.Pl4);
        PutInt(id, "tgp", settings.GpuTgp);
        PutBool(id, "dbon", settings.GpuDynamicBoostOn);
        PutInt(id, "db", settings.GpuDynamicBoost);
        PutCurve(id, "fancpu", settings.CpuFanDuty);
        PutCurve(id, "fangpu", settings.GpuFanDuty);
        PutBool(id, "tccon", settings.TccOn);
        PutInt(id, "tcc", settings.TccTarget);
        PutBool(id, "ocon", settings.GpuOverclockOn);
        PutInt(id, "occore", settings.GpuCoreOffset);
        PutInt(id, "ocmem", settings.GpuMemoryOffset);
        PutBool(id, "fanboost", settings.FanBoost);
        PutBool(id, "fsson", settings.FanSwitchSpeedOn);
        PutInt(id, "fss", settings.FanSwitchSpeedMs);
        PutInt(id, "power", settings.WindowsPowerMode);
        PutInt(id, "boost", settings.CpuBoost);
        string planKey = FieldKey(id, "plan");
        if (string.IsNullOrWhiteSpace(settings.PowerPlanGuid)) AppConfig.Remove(planKey);
        else AppConfig.Set(planKey, settings.PowerPlanGuid);
        PutInt(id, "hz", settings.RefreshHz);
        AppConfig.Flush();
    }

    /// <summary>把一个模式恢复到官方出厂行为（删掉它的所有覆盖项）。</summary>
    public static void ResetSettings(string id) => SaveSettings(id, PerfModeSettings.Default);

    // ---- 模式集合 ----

    /// <summary>
    /// 自定义模式的序号列表。**第一次运行返回单个 1**：需求明确要求初始只放一个自定义模式。
    /// </summary>
    internal static List<int> LoadCustomOrdinals()
    {
        string? text = AppConfig.GetString(CustomListKey);
        if (string.IsNullOrWhiteSpace(text)) return new List<int> { 1 };
        var result = new List<int>();
        foreach (string part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                && n is > 0 and <= PerfModeCollection.MaxCustomModes
                && !result.Contains(n))
                result.Add(n);
        return result.Count > 0 ? result : new List<int> { 1 };
    }

    static void SaveCustomOrdinals(IEnumerable<int> ordinals)
    {
        AppConfig.Set(CustomListKey, string.Join(',', ordinals));
        AppConfig.Flush();
    }

    /// <summary>全部模式，按显示顺序：4 个内置 + 自定义若干。</summary>
    public static IReadOnlyList<PerfModeDefinition> Load()
    {
        var result = new List<PerfModeDefinition>(8);
        foreach (PerfModeKind kind in PerfModeCollection.BuiltInOrder)
        {
            string id = PerfModeDefinition.BuiltInId(kind);
            result.Add(new PerfModeDefinition(id, kind, LoadName(id), LoadSettings(id)));
        }
        foreach (int ordinal in LoadCustomOrdinals())
        {
            string id = PerfModeDefinition.CustomId(ordinal);
            result.Add(new PerfModeDefinition(id, PerfModeKind.Custom, LoadName(id), LoadSettings(id)));
        }
        return result;
    }

    static string? LoadName(string id) => PerfModeCollection.SanitizeUserName(AppConfig.GetString(NameKey(id)));

    public static void SaveName(string id, string? name)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        string? clean = PerfModeCollection.SanitizeUserName(name);
        if (clean is null) AppConfig.Remove(NameKey(id)); else AppConfig.Set(NameKey(id), clean);
        AppConfig.Flush();
    }

    /// <summary>
    /// 新建一个自定义模式，参数以 <paramref name="seedFromId"/> 为起点复制
    /// （需求：新建时以当前模式为起点，而不是给一张空表）。返回 null 表示已达上限。
    /// </summary>
    public static PerfModeDefinition? AddCustom(string? seedFromId = null)
    {
        IReadOnlyList<PerfModeDefinition> existing = Load();
        if (!PerfModeCollection.CanAdd(existing)) return null;
        int ordinal = PerfModeCollection.NextCustomOrdinal(existing);
        if (ordinal < 0) return null;

        string id = PerfModeDefinition.CustomId(ordinal);
        PerfModeSettings seed = string.IsNullOrEmpty(seedFromId)
            ? PerfModeSettings.Default
            : LoadSettings(seedFromId);
        SaveSettings(id, seed);

        List<int> ordinals = LoadCustomOrdinals();
        if (!ordinals.Contains(ordinal)) ordinals.Add(ordinal);
        SaveCustomOrdinals(ordinals);
        return new PerfModeDefinition(id, PerfModeKind.Custom, null, seed);
    }

    /// <summary>删除一个自定义模式。最后一个自定义模式与内置模式不可删。</summary>
    public static bool RemoveCustom(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        IReadOnlyList<PerfModeDefinition> existing = Load();
        if (!PerfModeCollection.CanRemove(existing, id)) return false;
        if (!PerfModeCollection.TryParseCustomOrdinal(id, out int ordinal)) return false;

        List<int> ordinals = LoadCustomOrdinals();
        ordinals.Remove(ordinal);
        SaveCustomOrdinals(ordinals);
        ResetSettings(id);
        AppConfig.Remove(NameKey(id));
        SaveSlots(Release(LoadSlots(FirmwareSlotPlanner.MaxSlotCount), id));
        if (string.Equals(ActiveModeId, id, StringComparison.Ordinal))
            ActiveModeId = PerfModeDefinition.BuiltInId(PerfModeKind.Balanced);
        AppConfig.Flush();
        return true;
    }

    static IReadOnlyList<FirmwareSlotState> Release(IReadOnlyList<FirmwareSlotState> slots, string id) =>
        FirmwareSlotPlanner.Release(slots, id);

    /// <summary>当前模式 Id。未设置时视为平衡。</summary>
    public static string ActiveModeId
    {
        get
        {
            string? id = AppConfig.GetString(ActiveKey);
            return string.IsNullOrWhiteSpace(id) ? PerfModeDefinition.BuiltInId(PerfModeKind.Balanced) : id;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            AppConfig.Set(ActiveKey, value);
            AppConfig.Flush();
        }
    }

    /// <summary>是否记录过当前模式（全新安装时没有：启动时应跟随固件现状而不是强行切到平衡）。</summary>
    public static bool HasStoredActive => !string.IsNullOrWhiteSpace(AppConfig.GetString(ActiveKey));

    internal const string LastCustomKey = "perf_last_custom";

    /// <summary>最近用过的自定义模式（主界面「自定义」段点下去进的就是它）。</summary>
    public static string? LastCustomId
    {
        get
        {
            string? id = AppConfig.GetString(LastCustomKey);
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value)) AppConfig.Remove(LastCustomKey);
            else AppConfig.Set(LastCustomKey, value);
            AppConfig.Flush();
        }
    }

    static string SourceKey(int powerLineStatus) =>
        "perf_active_src" + powerLineStatus.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 按供电方式记住的模式（与 G-Helper 一致：插电和用电池各记各的）。
    /// <paramref name="powerLineStatus"/> 是 <c>PowerLineStatus</c> 的整数值。
    /// </summary>
    public static string? ActiveFor(int powerLineStatus)
    {
        string? id = AppConfig.GetString(SourceKey(powerLineStatus));
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    public static void SetActiveFor(int powerLineStatus, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        AppConfig.Set(SourceKey(powerLineStatus), id);
        AppConfig.Flush();
    }

    // ---- 旧版配置迁移 ----

    internal const string SchemaKey = "perf_schema";
    internal const int CurrentSchema = 1;

    /// <summary>
    /// 从 beta20 及更早版本迁移（只做一次）：
    /// <list type="bullet">
    /// <item>beta20 的内置模式二级自定义 <c>mode_tune_{office|gaming|turbo|silentturbo}_*</c>
    ///       → 对应模式的应用侧项（电源模式/睿频/风扇增强/刷新率），狂暴「显卡自动超频」关 → 狂暴超频关；</item>
    /// <item>旧自定义档的电源计划/睿频（<c>custom{n}_plan/boost</c>）→ 「自定义 1」；</item>
    /// <item>「自定义 1」接管旧版最后使用的固件档（<c>custom_last_profile</c>），
    ///       档里原有的参数原样保留——绝不因升级改写用户的自定义档；</item>
    /// <item>旧版记住的性能模式（<c>performance_mode</c>）→ 当前模式。</item>
    /// </list>
    /// </summary>
    public static void MigrateLegacy()
    {
        if (AppConfig.Get(SchemaKey, 0) >= CurrentSchema) return;

        (string Legacy, PerfModeKind Kind)[] tuned =
        {
            ("office", PerfModeKind.Silent),
            ("gaming", PerfModeKind.Balanced),
            ("turbo", PerfModeKind.Turbo),
            ("silentturbo", PerfModeKind.SilentTurbo),
        };
        foreach ((string legacy, PerfModeKind kind) in tuned)
        {
            string prefix = "mode_tune_" + legacy + "_";
            string id = PerfModeDefinition.BuiltInId(kind);
            PerfModeSettings s = LoadSettings(id);
            int power = AppConfig.Get(prefix + "power", -1);
            int boost = AppConfig.Get(prefix + "boost", -1);
            int fanBoost = AppConfig.Get(prefix + "fanboost", -1);
            int hz = AppConfig.Get(prefix + "hz", 0);
            bool autoOcOff = kind == PerfModeKind.Turbo && AppConfig.Exists(prefix + "autooc")
                && AppConfig.Get(prefix + "autooc", 1) == 0;
            s = s with
            {
                WindowsPowerMode = power is >= 0 and <= 2 ? power : s.WindowsPowerMode,
                CpuBoost = boost is >= 0 and <= 6 ? boost : s.CpuBoost,
                FanBoost = fanBoost is 0 or 1 ? fanBoost == 1 : s.FanBoost,
                RefreshHz = hz > 0 ? hz : s.RefreshHz,
                GpuOverclockOn = autoOcOff ? false : s.GpuOverclockOn,
                GpuCoreOffset = autoOcOff ? 0 : s.GpuCoreOffset,
                GpuMemoryOffset = autoOcOff ? 0 : s.GpuMemoryOffset,
            };
            SaveSettings(id, s);
            foreach (string suffix in new[] { "power", "boost", "fanboost", "hz", "autooc" })
                AppConfig.Remove(prefix + suffix);
        }

        int legacySlot = Math.Clamp(AppConfig.Get("custom_last_profile", 0), 0, FirmwareSlotPlanner.MaxSlotCount - 1);
        string custom1 = PerfModeDefinition.CustomId(1);
        PerfModeSettings c1 = LoadSettings(custom1);
        if (legacySlot <= 3)
        {
            string? plan = AppConfig.GetString("custom" + legacySlot.ToString(CultureInfo.InvariantCulture) + "_plan");
            int planBoost = AppConfig.Get("custom" + legacySlot.ToString(CultureInfo.InvariantCulture) + "_boost", -1);
            c1 = c1 with
            {
                PowerPlanGuid = Guid.TryParse(plan, out Guid g) ? g.ToString() : c1.PowerPlanGuid,
                CpuBoost = planBoost is >= 0 and <= 6 ? planBoost : c1.CpuBoost,
            };
            SaveSettings(custom1, c1);
        }
        if (!AppConfig.Exists(CustomListKey)) SaveCustomOrdinals(new[] { 1 });

        IReadOnlyList<FirmwareSlotState> slots = LoadSlots(FirmwareSlotPlanner.MaxSlotCount);
        if (slots.All(s => s.IsFree))
            SaveSlots(FirmwareSlotPlanner.Assign(slots, legacySlot, custom1, c1.FirmwareSignature(), NowStamp()));
        LastCustomId ??= custom1;

        if (!HasStoredActive && AppConfig.Exists("performance_mode"))
        {
            // 旧版 MechrevoService 枚举：0=游戏(平衡) 1=增强(狂暴) 2=办公(静音) 3=自定义。
            string? mapped = AppConfig.Get("performance_mode") switch
            {
                0 => PerfModeDefinition.BuiltInId(PerfModeKind.Balanced),
                1 => PerfModeDefinition.BuiltInId(PerfModeKind.Turbo),
                2 => PerfModeDefinition.BuiltInId(PerfModeKind.Silent),
                3 => custom1,
                _ => null,
            };
            if (mapped is not null) ActiveModeId = mapped;
        }

        AppConfig.Set(SchemaKey, CurrentSchema);
        AppConfig.Flush();
    }

    // ---- 固件档位归属表 ----

    public static IReadOnlyList<FirmwareSlotState> LoadSlots(int slotCount)
    {
        int count = Math.Clamp(slotCount, 1, FirmwareSlotPlanner.MaxSlotCount);
        var result = new List<FirmwareSlotState>(count);
        for (int i = 0; i < count; i++)
        {
            string? owner = AppConfig.GetString(FirmwareSlotPlanner.OwnerKey(i));
            string? sig = AppConfig.GetString(FirmwareSlotPlanner.SignatureKey(i));
            long used = AppConfig.Get(FirmwareSlotPlanner.LastUsedKey(i), 0);
            result.Add(new FirmwareSlotState(i, string.IsNullOrWhiteSpace(owner) ? null : owner, sig, used));
        }
        return result;
    }

    public static void SaveSlots(IReadOnlyList<FirmwareSlotState> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        foreach (FirmwareSlotState slot in slots)
        {
            string ownerKey = FirmwareSlotPlanner.OwnerKey(slot.Index);
            string sigKey = FirmwareSlotPlanner.SignatureKey(slot.Index);
            if (slot.IsFree)
            {
                AppConfig.Remove(ownerKey);
                AppConfig.Remove(sigKey);
            }
            else
            {
                AppConfig.Set(ownerKey, slot.OwnerModeId!);
                AppConfig.Set(sigKey, slot.Signature ?? "");
            }
            AppConfig.Set(FirmwareSlotPlanner.LastUsedKey(slot.Index), (int)Math.Clamp(slot.LastUsedTicks, 0, int.MaxValue));
        }
        AppConfig.Flush();
    }

    /// <summary>
    /// 单调递增的「使用时刻」。用秒级 Unix 时间戳而不是 <c>DateTime.Ticks</c>：
    /// 档位表要存进 int 配置项，Ticks 会溢出。
    /// </summary>
    public static long NowStamp() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1_700_000_000L;
}
