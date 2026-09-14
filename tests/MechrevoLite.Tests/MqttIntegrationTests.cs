using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

public class MqttIntegrationTests
{
    [GcuIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task Connect_WithLocalGcuBroker_ReceivesTelemetry()
    {
        using var hardware = new MechrevoHw();
        var telemetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hardware.DataChanged += () =>
        {
            if (hardware.CpuTemp > 0) telemetry.TrySetResult();
        };

        Assert.True(await hardware.ConnectAsync());
        Assert.True(hardware.IsConnected);
        Assert.Same(telemetry.Task, await Task.WhenAny(telemetry.Task, Task.Delay(TimeSpan.FromSeconds(15))));
        Assert.True(hardware.CpuTemp > 0, $"Expected a valid CPU temperature, got {hardware.CpuTemp}");
    }

}

sealed class GcuIntegrationFactAttribute : FactAttribute
{
    public GcuIntegrationFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("LMECHREVO_RUN_INTEGRATION"), "1", StringComparison.Ordinal))
            Skip = "Set LMECHREVO_RUN_INTEGRATION=1 to test the installed local GCU broker.";
    }
}
