namespace MechrevoLite.Tests;

public class BootChargeLimitTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(39)]
    [InlineData(101)]
    public async Task MissingOrInvalidPercentage_DoesNotReplay(int? limit)
    {
        string? previous = AppConfig.GetString("charge_limit");
        try
        {
            if (limit.HasValue) AppConfig.Set("charge_limit", limit.Value);
            else AppConfig.Remove("charge_limit");
            Assert.False(await Program.ApplyBatteryLimitAtBootAsync());
        }
        finally { Restore("charge_limit", previous); }
    }

    [Fact]
    public async Task DisabledPercentageControl_DoesNotReplay()
    {
        string? previousLimit = AppConfig.GetString("charge_limit");
        string? previousEnabled = AppConfig.GetString("ec_charge_limit");
        try
        {
            AppConfig.Set("charge_limit", 80);
            AppConfig.Set("ec_charge_limit", "0");
            Assert.False(await Program.ApplyBatteryLimitAtBootAsync());
        }
        finally
        {
            Restore("charge_limit", previousLimit);
            Restore("ec_charge_limit", previousEnabled);
        }
    }

    static void Restore(string key, string? value)
    {
        if (value is null) AppConfig.Remove(key);
        else AppConfig.Set(key, value);
    }
}
