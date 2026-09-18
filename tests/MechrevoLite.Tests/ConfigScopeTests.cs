using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T5（Wave A）happy 路径：按机型隔离的配置作用域 + 一次性迁移。旧机器级值归属到**迁移时识别到的机型**；
/// 迁移幂等；换机型不继承旧作用域值；迁移经 <see cref="AppConfig.Flush"/> 落盘并留下可回滚的 .bak。
/// 失败/边界断言见 <see cref="ConfigScopeFailTests"/>。
/// </summary>
public class ConfigScopeTests
{
    const string Model = "PH4TRX1";
    const string OtherModel = "PH6TRX1";

    [Fact]
    public void MachineScopedKeysAreClassifiedAndOthersAreNot()
    {
        foreach (string key in new[]
                 {
                     "charge_limit", "gpu_mode", "gpu_auto", "ec_charge_limit",
                     "fan_profile_cpu_0", "fan_profile_gpu_3", "fan_profile_mid_1",
                     "lc_fan_profile", "LC_CoolingAuto",
                 })
            Assert.True(ModelScopedConfig.IsMachineScopedKey(key), key);

        foreach (string key in new[]
                 {
                     "log_level", "performance_mode", "model_override",
                     "model_config_scope", "oled", "gpu_mode_force_set",
                 })
            Assert.False(ModelScopedConfig.IsMachineScopedKey(key), key);
    }

    [Fact]
    public void ScopedKeysCarryTheModelAndNeverCollide()
    {
        Assert.Equal("model.PH4TRX1.gpu_mode", ModelScopedConfig.ScopedKey(Model, "gpu_mode"));
        Assert.NotEqual(
            ModelScopedConfig.ScopedKey(Model, "gpu_mode"),
            ModelScopedConfig.ScopedKey(OtherModel, "gpu_mode"));
    }

    [Fact]
    public void PlanMovesGlobalValuesUnderTheModelAndMarksTheMigration()
    {
        var source = new Dictionary<string, object> { ["charge_limit"] = 80, ["log_level"] = "off" };

        ModelScopeMigration plan = ModelScopedConfig.Plan(source, Model);

        Assert.True(plan.Migrated);
        Assert.Equal(Model, plan.Model);
        Assert.Equal(new[] { "charge_limit" }, plan.RemovedKeys);
        KeyValuePair<string, object> moved = Assert.Single(plan.ScopedValues);
        Assert.Equal("model.PH4TRX1.charge_limit", moved.Key);
        Assert.Equal(80, moved.Value);
    }

    [Fact]
    public void PlanIsIdempotentOnceTheMarkerIsPresent()
    {
        var source = new Dictionary<string, object>
        {
            ["charge_limit"] = 80,
            [ModelScopedConfig.ScopeMarkerKey] = Model,
        };

        ModelScopeMigration plan = ModelScopedConfig.Plan(source, OtherModel);

        Assert.False(plan.Migrated);
        Assert.Empty(plan.RemovedKeys);
        Assert.Empty(plan.ScopedValues);
    }

    [Fact]
    public void MigrationAttributesOldValuesToTheModelIdentifiedAtMigrationTime()
    {
        WithConfigSnapshot(() =>
        {
            ClearModelScopes();
            AppConfig.Remove(ModelScopedConfig.ScopeMarkerKey);
            AppConfig.Set("charge_limit", 80);

            ModelScopeMigration plan = ModelScopedConfig.MigrateToModelScope(Model);

            Assert.True(plan.Migrated);
            Assert.Equal(80, AppConfig.Get(ModelScopedConfig.ScopedKey(Model, "charge_limit")));
            Assert.Equal(Model, AppConfig.GetString(ModelScopedConfig.ScopeMarkerKey));
            Assert.False(AppConfig.Exists("charge_limit"));
        });
    }

    [Fact]
    public void SwitchingModelDoesNotInheritTheOldScope()
    {
        WithConfigSnapshot(() =>
        {
            ClearModelScopes();
            AppConfig.Remove(ModelScopedConfig.ScopeMarkerKey);
            AppConfig.Set("gpu_mode", 2);

            Assert.True(ModelScopedConfig.MigrateToModelScope(Model).Migrated);

            Assert.Equal(-1, AppConfig.Get(ModelScopedConfig.ScopedKey(OtherModel, "gpu_mode")));
            Assert.Null(AppConfig.GetString(ModelScopedConfig.ScopedKey(OtherModel, "ec_charge_limit")));
        });
    }

    [Fact]
    public void MigrationIsIdempotentAndDoesNotRetargetTheMarker()
    {
        WithConfigSnapshot(() =>
        {
            ClearModelScopes();
            AppConfig.Remove(ModelScopedConfig.ScopeMarkerKey);
            AppConfig.Set("gpu_auto", 1);

            Assert.True(ModelScopedConfig.MigrateToModelScope(Model).Migrated);
            Assert.False(ModelScopedConfig.MigrateToModelScope(OtherModel).Migrated);

            Assert.Equal(Model, AppConfig.GetString(ModelScopedConfig.ScopeMarkerKey));
            Assert.Equal(1, AppConfig.Get(ModelScopedConfig.ScopedKey(Model, "gpu_auto")));
            Assert.Equal(-1, AppConfig.Get(ModelScopedConfig.ScopedKey(OtherModel, "gpu_auto")));
        });
    }

    [Fact]
    public void MigrationLeavesARollbackBakHoldingThePreMigrationGlobalKey()
    {
        WithConfigSnapshot(() =>
        {
            ClearModelScopes();
            AppConfig.Remove(ModelScopedConfig.ScopeMarkerKey);
            AppConfig.Set("charge_limit", 80);
            AppConfig.Flush();
            // 第二次 flush 保证 .bak 就是"迁移前的干净状态"，而不是更早的残留内容。
            AppConfig.Flush();

            ModelScopedConfig.MigrateToModelScope(Model);

            string configFile = Environment.GetEnvironmentVariable("LMECHREVO_CONFIG_FILE")!;
            string bak = configFile + ".bak";
            Assert.True(File.Exists(bak), $"expected a rollback backup at {bak}");

            string bakText = File.ReadAllText(bak);
            Assert.Contains("\"charge_limit\": 80", bakText);
            Assert.DoesNotContain("model.PH4TRX1.charge_limit", bakText);

            string migrated = File.ReadAllText(configFile);
            Assert.Contains("model.PH4TRX1.charge_limit", migrated);
            Assert.Contains("model_config_scope", migrated);
        });
    }

    internal static void WithConfigSnapshot(Action body)
    {
        IReadOnlyDictionary<string, object> before = AppConfig.Snapshot();
        try
        {
            body();
        }
        finally
        {
            foreach (string key in AppConfig.Snapshot().Keys.Where(key => !before.ContainsKey(key)).ToArray())
                AppConfig.Remove(key);
            foreach ((string key, object value) in before.Where(pair => !AppConfig.Exists(pair.Key)))
                if (value is int number) AppConfig.Set(key, number);
                else AppConfig.Set(key, value?.ToString() ?? "");
        }
    }

    /// <summary>
    /// 清掉所有机型作用域键。测试用的临时 config.json 会跨运行保留，残留的 model.* 会让
    /// "不继承旧作用域"这类断言在同一台机器上第二次运行时假失败。
    /// </summary>
    internal static void ClearModelScopes()
    {
        foreach (string key in AppConfig.Snapshot().Keys
                     .Where(key => key.StartsWith("model.", StringComparison.Ordinal)).ToArray())
            AppConfig.Remove(key);
    }
}
