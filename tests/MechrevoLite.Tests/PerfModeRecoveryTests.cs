using MechrevoLite.Hardware;
using MechrevoLite.Mode;

namespace MechrevoLite.Tests;

public class PerfModeRecoveryTests
{
    internal static void WithModes(Action body)
    {
        void Clear()
        {
            foreach (string key in AppConfig.Snapshot().Keys.Where(k => k.StartsWith("perf_", StringComparison.Ordinal)).ToArray())
                AppConfig.Remove(key);
        }
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            bool audit = Program.UiAuditMode;
            Program.UiAuditMode = true;
            Clear();
            try { body(); }
            finally { Clear(); Program.UiAuditMode = audit; }
        });
    }

    static MechrevoHw Hardware()
    {
        var hw = new MechrevoHw((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities { ProfileAvailable = true });
        hw.HandleMessage("Fan/Status", "{\"OperatingMode\":3,\"CustomProfileIndex\":0,\"CPU_PL1Minimum\":0,\"CPU_PL1Maximum\":200,\"CPU_PL2Minimum\":0,\"CPU_PL2Maximum\":200}");
        hw.HandleMessage("Fan/Table", "{\"Name\":\"M4T1\"}");
        return hw;
    }

    [Theory]
    [InlineData(null, "silent")]
    [InlineData("turbo", "silent")]
    [InlineData("balanced", "balanced")]
    public void BatteryReplayUsesBatteryChoiceAndNeverFallsBackToAcTurbo(string? remembered, string expected)
    {
        WithModes(() =>
        {
            MechrevoHw? target = null;
            var actions = new List<string>();
            using var hw = new MechrevoHw((topic, payload) =>
            {
                var message = Newtonsoft.Json.Linq.JObject.FromObject(payload);
                string? action = message["Action"]?.ToString();
                if (topic == "Fan/Control" && action is not null)
                {
                    actions.Add(action);
                    if (action is "OPERATING_OFFICE_MODE" or "OPERATING_GAMING_MODE")
                        target!.HandleMessage("Fan/Status", action == "OPERATING_OFFICE_MODE" ? "{\"OperatingMode\":0}" : "{\"OperatingMode\":1}");
                }
                return Task.CompletedTask;
            }, new MechrevoDeviceCapabilities { ProfileAvailable = true });
            target = hw;
            hw.HandleMessage("Fan/Status", "{\"OperatingMode\":2}");
            PerfModeStore.ActiveModeId = "turbo";
            if (remembered is not null) PerfModeStore.SetActiveFor(0, remembered);
            var service = new MechrevoService(hw, () => false, () => 0, () => false, (_, _) => true);
            var perf = new PerfModeService(hw, new PerfModeBackend(service, hw), () => 0);
            perf.ReplayAsync("battery unplug", true, false).GetAwaiter().GetResult();
            Assert.Equal(expected, perf.ActiveModeId);
            Assert.Equal(expected, PerfModeStore.ActiveFor(0));
            Assert.DoesNotContain("OPERATING_TURBO_MODE", actions);
        });
    }

    [Theory]
    [InlineData(true, 2, true, true)]
    [InlineData(false, 2, false, true)]
    [InlineData(true, 2, false, false)]
    [InlineData(false, 2, true, false)]
    [InlineData(true, 0, true, false)]
    [InlineData(false, 1, false, false)]
    [InlineData(false, 2, null, null)]
    public void TurboSubModeReadbackChecksTurboAndTheSubModeRatherThanCastingTheSwitchToAMode(
        bool silent, int operatingMode, bool? actualSilent, bool? expected)
        => Assert.Equal(expected, PerfModeBackend.TurboSubModeMatches(silent, operatingMode, actualSilent));

    [Fact]
    public void FailedFirmwareWriteIsRetriedOnReplayEvenWhenTheModeIsAlreadyActive()
    {
        WithModes(() =>
        {
            using var hw = Hardware();
            var backend = new PerfModeRunnerTests.FakeBackend();
            var service = new PerfModeService(hw, backend);
            PerfModeStore.SaveSettings("custom1", new PerfModeSettings { Pl1 = 45 });
            backend.FailIssue.Add("WriteFirmwareField:PL1=45");

            Assert.True(service.ActivateAsync("custom1", "test").GetAwaiter().GetResult()!.AnyFailed);
            Assert.Equal("custom1", PerfModeStore.LoadSlots(PerfModeService.SlotCount)[0].OwnerModeId);
            Assert.True(string.IsNullOrEmpty(PerfModeStore.LoadSlots(PerfModeService.SlotCount)[0].Signature));

            backend.FailIssue.Clear();
            backend.Calls.Clear();
            service.ReplayAsync("reconnected", false, false).GetAwaiter().GetResult();
            Assert.Contains("WriteFirmwareField:PL1=45", backend.Calls);
            Assert.Equal(PerfModeStore.LoadSettings("custom1").FirmwareSignature(), PerfModeStore.LoadSlots(PerfModeService.SlotCount)[0].Signature);
        });
    }

    [Fact]
    public void AnApplicationSideFailureDoesNotInvalidateSuccessfullyIssuedFirmware()
    {
        WithModes(() =>
        {
            using var hw = Hardware();
            var backend = new PerfModeRunnerTests.FakeBackend();
            backend.FailIssue.Add("ApplyPowerPlan:0");
            var service = new PerfModeService(hw, backend);
            PerfModeStore.SaveSettings("custom1", new PerfModeSettings { Pl1 = 45, PowerPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e" });
            Assert.True(service.ActivateAsync("custom1", "test").GetAwaiter().GetResult()!.AnyFailed);
            var slots = PerfModeStore.LoadSlots(PerfModeService.SlotCount);
            Assert.False(FirmwareSlotPlanner.Plan("custom1", PerfModeStore.LoadSettings("custom1").FirmwareSignature(), slots).NeedsParameterWrite);
        });
    }

    [Fact]
    public void QueuedReplayWaitsForTheModeGateAndDoesNotApplyAnObsoleteMode()
    {
        WithModes(() =>
        {
            using var hw = Hardware();
            var backend = new PerfModeRunnerTests.FakeBackend();
            var service = new PerfModeService(hw, backend);
            PerfModeStore.SaveSettings("balanced", new PerfModeSettings { WindowsPowerMode = 2 });
            PerfModeStore.ActiveModeId = "balanced";
            var gate = (SemaphoreSlim)typeof(PerfModeService).GetField("_gate",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!;
            gate.Wait();
            Task replay;
            try
            {
                replay = service.ReplayAsync("wakeup", false, true);
                Assert.False(replay.IsCompleted);
                PerfModeStore.ActiveModeId = "turbo";
            }
            finally { gate.Release(); }
            replay.GetAwaiter().GetResult();
            Assert.Empty(backend.Calls);
            Assert.Equal("turbo", PerfModeStore.ActiveModeId);
        });
    }

    [Fact]
    public void EditingADirtySlotRetriesEarlierFailedFieldsInsteadOfOnlyWritingTheNewDelta()
    {
        WithModes(() =>
        {
            using var hw = Hardware();
            var backend = new PerfModeRunnerTests.FakeBackend();
            var service = new PerfModeService(hw, backend);
            PerfModeStore.SaveSettings("custom1", new PerfModeSettings { Pl1 = 45, Pl2 = 90 });
            PerfModeStore.ActiveModeId = "custom1";
            PerfModeStore.SaveSlots(FirmwareSlotPlanner.Assign(PerfModeStore.LoadSlots(PerfModeService.SlotCount), 0, "custom1", null, 1));
            backend.FailIssue.Add("WriteFirmwareField:PL1=45");

            service.UpdateAsync("custom1", s => s with { Pl2 = 100 }, "edit").GetAwaiter().GetResult();
            Assert.Contains("WriteFirmwareField:PL1=45", backend.Calls);
            Assert.Contains("WriteFirmwareField:PL2=100", backend.Calls);
            Assert.True(string.IsNullOrEmpty(PerfModeStore.LoadSlots(PerfModeService.SlotCount)[0].Signature));
        });
    }
}
