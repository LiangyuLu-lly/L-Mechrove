namespace MechrevoLite;

/// <summary>
/// 一次性迁移的计划：要移除的机器级键 + 要写入的机型作用域键值。
/// <see cref="Migrated"/> 为假表示标记已存在，本次不迁移（幂等）。
/// </summary>
internal sealed record ModelScopeMigration(
    bool Migrated,
    string Model,
    IReadOnlyList<string> RemovedKeys,
    IReadOnlyList<KeyValuePair<string, object>> ScopedValues);

/// <summary>
/// 按机型隔离的配置作用域（T5）。
///
/// <para>机器级键（<c>charge_limit</c>/<c>gpu_mode</c>/<c>gpu_auto</c>/<c>ec_charge_limit</c>/
/// <c>fan_profile*</c>/<c>lc_*</c>）的值只对**写入时那台机型**有意义，混在一起会让换机型后沿用旧值。
/// 作用域键 = <c>model.&lt;代号&gt;.&lt;原键&gt;</c>，因此不同机型的取值天然不互相继承。</para>
///
/// <para>迁移只跑一次：<see cref="ScopeMarkerKey"/> 记录迁移时的机型，第二次调用即空转。
/// 旧值归属到迁移时识别到的机型；持久化仍只走 <see cref="AppConfig"/>（<c>WriteAtomic</c> 留下 .bak 可回滚）。</para>
/// </summary>
internal static class ModelScopedConfig
{
    /// <summary>迁移标记：值 = 迁移时识别到的机型代号。存在即表示迁移已完成。</summary>
    public const string ScopeMarkerKey = "model_config_scope";

    const string ScopePrefix = "model.";

    static readonly string[] ExactMachineScopedKeys =
    {
        "charge_limit", "gpu_mode", "gpu_auto", "ec_charge_limit",
    };

    /// <summary>该键的值是否与具体机型绑定。大小写按仓库既有键的两种写法（lc_ / LC_）都覆盖。</summary>
    public static bool IsMachineScopedKey(string key) =>
        ExactMachineScopedKeys.Contains(key, StringComparer.Ordinal)
        || key.StartsWith("fan_profile", StringComparison.Ordinal)
        || key.StartsWith("lc_", StringComparison.OrdinalIgnoreCase);

    /// <summary>机型作用域键。机型为空时仍产生稳定键（<c>model..&lt;键&gt;</c>），不抛异常。</summary>
    public static string ScopedKey(string model, string key) => ScopePrefix + model + "." + key;

    /// <summary>纯函数：给定配置快照与机型，算出迁移要做的事。不回写任何东西，便于测试。</summary>
    public static ModelScopeMigration Plan(IReadOnlyDictionary<string, object> source, string model)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.ContainsKey(ScopeMarkerKey))
            return new ModelScopeMigration(false, model, Array.Empty<string>(), Array.Empty<KeyValuePair<string, object>>());

        var removed = new List<string>();
        var scoped = new List<KeyValuePair<string, object>>();
        foreach (KeyValuePair<string, object> pair in source)
        {
            if (!IsMachineScopedKey(pair.Key)) continue;
            removed.Add(pair.Key);
            scoped.Add(new KeyValuePair<string, object>(ScopedKey(model, pair.Key), pair.Value));
        }
        return new ModelScopeMigration(true, model, removed, scoped);
    }

    /// <summary>
    /// 在真实 <see cref="AppConfig"/> 上执行一次性迁移并落盘（原子替换，旧内容留成 .bak）。
    /// 标记已存在时是空转，不会把别的机型的值改归属。
    /// </summary>
    public static ModelScopeMigration MigrateToModelScope(string model)
    {
        ArgumentNullException.ThrowIfNull(model);

        ModelScopeMigration plan = Plan(AppConfig.Snapshot(), model);
        if (!plan.Migrated) return plan;

        foreach (string key in plan.RemovedKeys) AppConfig.Remove(key);
        foreach (KeyValuePair<string, object> entry in plan.ScopedValues) Write(entry.Key, entry.Value);
        AppConfig.Set(ScopeMarkerKey, plan.Model);
        AppConfig.Flush();
        return plan;
    }

    /// <summary>回写时保留 int 与字符串两种既有类型，避免把数字变成字符串后语义漂移。</summary>
    static void Write(string key, object? value)
    {
        if (value is int number) AppConfig.Set(key, number);
        else AppConfig.Set(key, value?.ToString() ?? "");
    }
}
