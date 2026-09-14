using MechrevoLite.Mode;

namespace MechrevoLite.Tests;

public class PowerModeIntegrationTests
{
    [Fact]
    public void DisableHighPerformanceAutomation_ConfirmsWindowsBalancedState()
    {
        if (Environment.GetEnvironmentVariable("LMECHREVO_RUN_POWER_INTEGRATION") != "1")
            return;

        Assert.True(PowerNative.ApplyMechrevoPowerModeAutomation(enabled: false, operatingMode: 2));
    }
}
