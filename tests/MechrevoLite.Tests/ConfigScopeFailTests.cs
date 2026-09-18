using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T5（Wave A）失败 / 边界路径：迁移只碰机器级键、只跑一次、不给别的机型兜底。
/// happy 断言见 <see cref="ConfigScopeTests"/>。
/// </summary>
public class ConfigScopeFailTests
{
    const string Model = "PH4TRX1";
    const string OtherModel = "PH6TRX1";

    [Fact]
    public void NonMachineScopedKeysAreLeftAlone()
    {
        var source = new Dictionary<string, object>
        {
            ["log_level"] = "off",
            ["performance_mode"] = 2,
            ["model_override"] = "PH4PUxx",
            ["charge_limit"] = 80,
        };

        ModelScopeMigration plan = ModelScopedConfig.Plan(source, Model);

        Assert.Equal(new[] { "charge_limit" }, plan.RemovedKeys);
        Assert.DoesNotContain("log_level", plan.RemovedKeys);
        Assert.DoesNotContain("performance_mode", plan.RemovedKeys);
        Assert.DoesNotContain("model_override", plan.RemovedKeys);
    }

    [Fact]
    public void AnAlreadyMigratedConfigIsNeverRekeyedToAnotherModel()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            ConfigScopeTests.ClearModelScopes();
            AppConfig.Remove(ModelScopedConfig.ScopeMarkerKey);
            AppConfig.Set("charge_limit", 70);
            Assert.True(ModelScopedConfig.MigrateToModelScope(Model).Migrated);

            ModelScopeMigration second = ModelScopedConfig.MigrateToModelScope(OtherModel);

            Assert.False(second.Migrated);
            Assert.Equal(Model, AppConfig.GetString(ModelScopedConfig.ScopeMarkerKey));
            Assert.Equal(70, AppConfig.Get(ModelScopedConfig.ScopedKey(Model, "charge_limit")));
            Assert.Equal(-1, AppConfig.Get(ModelScopedConfig.ScopedKey(OtherModel, "charge_limit")));
        });
    }

    [Fact]
    public void AMigrationWithoutAnyMachineKeyStillMarksOnce()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            ConfigScopeTests.ClearModelScopes();
            AppConfig.Remove(ModelScopedConfig.ScopeMarkerKey);
            foreach (string key in AppConfig.Snapshot().Keys
                         .Where(ModelScopedConfig.IsMachineScopedKey).ToArray())
                AppConfig.Remove(key);

            ModelScopeMigration first = ModelScopedConfig.MigrateToModelScope(Model);
            ModelScopeMigration second = ModelScopedConfig.MigrateToModelScope(Model);

            Assert.True(first.Migrated);
            Assert.Empty(first.RemovedKeys);
            Assert.False(second.Migrated);
            Assert.Equal(Model, AppConfig.GetString(ModelScopedConfig.ScopeMarkerKey));
        });
    }

    [Fact]
    public void AScopedLookupNeverFallsBackToTheGlobalKey()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            ConfigScopeTests.ClearModelScopes();
            AppConfig.Set("gpu_mode", 3);

            // 未迁移时，机型作用域读法不得"顺带"读到机器级键——那正是换机型会继承的来源。
            Assert.Equal(-1, AppConfig.Get(ModelScopedConfig.ScopedKey(Model, "gpu_mode")));
            Assert.Equal(3, AppConfig.Get("gpu_mode"));
        });
    }

    [Fact]
    public void AnEmptyModelNameStillProducesAStableScopeKeyAndMigratesOnce()
    {
        ModelScopeMigration plan = ModelScopedConfig.Plan(
            new Dictionary<string, object> { ["gpu_auto"] = 1 }, "");

        Assert.True(plan.Migrated);
        Assert.Equal("model..gpu_auto", Assert.Single(plan.ScopedValues).Key);
    }
}
