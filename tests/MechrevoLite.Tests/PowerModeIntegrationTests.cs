using MechrevoLite.Hardware;
using MechrevoLite.Mode;

namespace MechrevoLite.Tests;

public class PowerModeIntegrationTests
{
    const string BalancedPlan = "381b4222-f694-41f0-9685-ff5bb260df2e";

    [Fact]
    public void SetActivePlan_Ultimate_DoesNotReportSuccessIfOverlayBalanced()
    {
        string ultimate = WinPowerPlan.UltimatePerformancePlanId;
        string balancedOverlay = WinPowerPlan.BalancedOverlayId;

        Assert.False(WinPowerPlan.IsPlanConfirmed(ultimate, ultimate, balancedOverlay));
        Assert.False(WinPowerPlan.IsPlanConfirmed(ultimate, BalancedPlan, balancedOverlay));
        Assert.True(WinPowerPlan.IsPlanConfirmed(ultimate, ultimate, overlay: null));
        Assert.True(WinPowerPlan.IsPlanConfirmed(BalancedPlan, BalancedPlan, balancedOverlay));
    }

    [Fact]
    public void SetActivePlan_RequiresReadbackMatch()
    {
        string ultimate = WinPowerPlan.UltimatePerformancePlanId;
        Assert.True(WinPowerPlan.IsPlanConfirmed(ultimate, ultimate));
        Assert.False(WinPowerPlan.IsPlanConfirmed(ultimate, BalancedPlan));
        Assert.False(WinPowerPlan.IsPlanConfirmed("not-a-power-plan", ultimate));
    }

    [Fact]
    public void PowerModeAutomation_DoesNotSilentlyUndoUltimate()
    {
        Guid ultimate = new(WinPowerPlan.UltimatePerformancePlanId);
        Guid balanced = new(BalancedPlan);
        Assert.True(PowerNative.WouldSilentlyUndo(ultimate, balanced));
        Assert.False(PowerNative.WouldSilentlyUndo(balanced, balanced));
        Assert.False(PowerNative.WouldSilentlyUndo(balanced, ultimate));
    }

    [Fact]
    public void DisableHighPerformanceAutomation_ConfirmsWindowsBalancedState()
    {
        if (Environment.GetEnvironmentVariable("LMECHREVO_RUN_POWER_INTEGRATION") != "1")
            return;

        Assert.True(PowerNative.ApplyMechrevoPowerModeAutomation(enabled: false, operatingMode: 2));
    }
}
