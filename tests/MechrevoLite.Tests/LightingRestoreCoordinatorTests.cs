using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

public class LightingRestoreCoordinatorTests
{
    [Fact]
    public void SuccessfulGeneration_IsSkippedOnDuplicateStartupRequest()
    {
        var coordinator = new LightingRestoreCoordinator();

        Assert.Equal(LightingRestoreDecision.Run, coordinator.TryBegin(1, force: false));
        coordinator.Complete(1, success: true);

        Assert.Equal(LightingRestoreDecision.SkipCompleted,
            coordinator.TryBegin(1, force: false));
    }

    [Fact]
    public void InFlightGeneration_DefersDuplicateUntilTheFirstAttemptCompletes()
    {
        var coordinator = new LightingRestoreCoordinator();

        Assert.Equal(LightingRestoreDecision.Run, coordinator.TryBegin(1, force: false));
        Assert.Equal(LightingRestoreDecision.DeferInFlight,
            coordinator.TryBegin(1, force: false));

        coordinator.Complete(1, success: false);
        Assert.Equal(LightingRestoreDecision.Run, coordinator.TryBegin(1, force: false));
    }

    [Fact]
    public void NewConnectionGeneration_CanRestoreAfterEarlierGeneration()
    {
        var coordinator = new LightingRestoreCoordinator();

        Assert.Equal(LightingRestoreDecision.Run, coordinator.TryBegin(1, force: false));
        coordinator.Complete(1, success: true);

        Assert.Equal(LightingRestoreDecision.Run, coordinator.TryBegin(2, force: false));
    }

    [Fact]
    public void ForceRestore_BypassesCompletedGeneration()
    {
        var coordinator = new LightingRestoreCoordinator();

        Assert.Equal(LightingRestoreDecision.Run, coordinator.TryBegin(1, force: false));
        coordinator.Complete(1, success: true);

        Assert.Equal(LightingRestoreDecision.Run, coordinator.TryBegin(1, force: true));
    }
}
