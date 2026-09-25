using System.Runtime.CompilerServices;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

public class MqttReconnectCoordinatorTests
{
    [Fact]
    public void ADisconnectDuringAnInFlightLoopMustReenterAfterExit()
    {
        var coordinator = new MqttReconnectCoordinator();
        coordinator.MarkDisconnectRequested();
        Assert.True(coordinator.TryEnterLoop());
        Assert.False(coordinator.TryEnterLoop());

        coordinator.MarkDisconnectRequested();
        Assert.True(coordinator.ExitLoop());
        Assert.True(coordinator.TryEnterLoop());
    }

    [Fact]
    public void ConcurrentExitLoopAndMarkDisconnect_StillAllowsTryEnterAfterExit()
    {
        for (int i = 0; i < 20_000; i++)
        {
            var coordinator = new MqttReconnectCoordinator();
            coordinator.MarkDisconnectRequested();
            Assert.True(coordinator.TryEnterLoop());

            var entered = new StrongBox<int>(0);
            var exitRetry = new StrongBox<int>(0);
            using var started = new ManualResetEventSlim(false);
            var marker = new Thread(() =>
            {
                started.Wait();
                coordinator.MarkDisconnectRequested();
                if (coordinator.TryEnterLoop())
                    Interlocked.Exchange(ref entered.Value, 1);
            });
            marker.Start();
            started.Set();
            if (coordinator.ExitLoop())
                Interlocked.Exchange(ref exitRetry.Value, 1);
            marker.Join();

            Assert.True(entered.Value == 1 || exitRetry.Value == 1);
            if (entered.Value == 0)
                Assert.True(coordinator.TryEnterLoop());
        }
    }

    [Fact]
    public void ShouldContinue_StopsWhenSessionReadyOrDisposed()
    {
        var coordinator = new MqttReconnectCoordinator();
        Assert.True(coordinator.ShouldContinue(sessionReady: false, disposed: false));
        Assert.False(coordinator.ShouldContinue(sessionReady: true, disposed: false));
        Assert.False(coordinator.ShouldContinue(sessionReady: false, disposed: true));
    }

    [Fact]
    public void PublishOverrideHardware_StaysConnectedWithoutABroker()
    {
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask);
        Assert.True(hardware.IsConnected);
    }
}
