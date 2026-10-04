using MechrevoLite.Hardware;
using MechrevoLite.Mode;
using Newtonsoft.Json.Linq;

namespace MechrevoLite.Tests;

[Collection(nameof(SerialGpuSwitchCollection))]
public class CustomProfileLatencyTests
{
    [Fact]
    public async Task ReturningToTheReportedProfileStillSupersedesADifferentPendingTarget()
    {
        MechrevoHw? target = null;
        int commands = 0;
        using var hw = new MechrevoHw((topic, payload) =>
        {
            var message = JObject.FromObject(payload);
            if (topic == "Fan/Control" && message["Action"]?.ToString() == "OPERATING_CUSTOM_MODE")
            {
                commands++;
                target!.HandleMessage("Fan/Status", "{\"OperatingMode\":3,\"CustomProfileIndex\":0}");
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities());
        target = hw;
        hw.HandleMessage("Fan/Status", "{\"OperatingMode\":3,\"CustomProfileIndex\":0}");
        hw.MarkModeSwitchPending(3, 1);
        var service = new MechrevoService(hw, () => false, () => 0, () => false);
        Assert.True(await service.SwitchCustomProfileWithResend(0));
        Assert.Equal(1, commands);
        hw.HandleMessage("Fan/Status", "{\"OperatingMode\":3,\"CustomProfileIndex\":1}");
        Assert.Equal(0, hw.CustomProfileIndex);
    }

    [Fact]
    public void SavingBeforeSwitchingDoesNotWaitForParameterReadback()
    {
        PerfModeRecoveryTests.WithModes(() =>
        {
            int commands = 0;
            using var hw = new MechrevoHw((_, _) => { commands++; return Task.CompletedTask; },
                new MechrevoDeviceCapabilities { ProfileAvailable = true });
            hw.HandleMessage("Fan/Status", "{\"OperatingMode\":3,\"CustomProfileIndex\":0,\"CPU_PL1\":40,\"CPU_PL1Minimum\":0,\"CPU_PL1Maximum\":120}");
            PerfModeStore.ActiveModeId = "custom1";
            var service = new MechrevoService(hw, () => false, () => 0, () => false);
            var perf = new PerfModeService(hw, new PerfModeBackend(service, hw));
            Assert.Null(perf.UpdateAsync("custom1", s => s with { Pl1 = 55 }, "switch save", false).GetAwaiter().GetResult());
            Assert.Equal(55, PerfModeStore.LoadSettings("custom1").Pl1);
            Assert.Equal(0, commands);
            Assert.Equal(40, hw.Pl1);
        });
    }

    [Fact]
    public void SwitchingCancelsOldParameterConfirmationAndPreservesTheSavedEdit()
    {
        PerfModeRecoveryTests.WithModes(() =>
        {
            MechrevoHw? target = null;
            var parameterSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var hw = new MechrevoHw((topic, payload) =>
            {
                var message = JObject.FromObject(payload);
                if (topic == "Fan/Control" && message["Action"]?.ToString() == "SET_OPERATING_MODE_DETAIL")
                    parameterSent.TrySetResult();
                if (topic == "Fan/Control" && message["Action"]?.ToString() == "OPERATING_CUSTOM_MODE")
                    target!.HandleMessage("Fan/Status", "{\"OperatingMode\":3,\"CustomProfileIndex\":1,\"CPU_PL1\":40}");
                if (topic == "Fan/Control" && message["Action"]?.ToString() == "SET_CUSTOM_PROFILE_OSD_STRING")
                    target!.HandleMessage("Fan/Status", $"{{\"OperatingMode\":3,\"CustomProfileIndex\":1,\"ProfileName\":{message["ProfileName"]!.ToString(Newtonsoft.Json.Formatting.None)}}}");
                return Task.CompletedTask;
            }, new MechrevoDeviceCapabilities { ProfileAvailable = true });
            target = hw;
            hw.HandleMessage("Fan/Status", "{\"OperatingMode\":3,\"CustomProfileIndex\":0,\"CPU_PL1\":40,\"CPU_PL1Minimum\":0,\"CPU_PL1Maximum\":120}");
            PerfModeStore.ActiveModeId = "custom1";
            Assert.Equal("custom2", PerfModeStore.AddCustom()!.Id);
            PerfModeStore.SaveSettings("custom1", new PerfModeSettings { Pl1 = 40 });
            var slots = PerfModeStore.LoadSlots(PerfModeService.SlotCount);
            slots = FirmwareSlotPlanner.Assign(slots, 0, "custom1", PerfModeStore.LoadSettings("custom1").FirmwareSignature(), 1);
            slots = FirmwareSlotPlanner.Assign(slots, 1, "custom2", PerfModeSettings.Default.FirmwareSignature(), 2);
            PerfModeStore.SaveSlots(slots);
            var service = new MechrevoService(hw, () => false, () => 0, () => false);
            var perf = new PerfModeService(hw, new PerfModeBackend(service, hw));
            var editing = perf.UpdateAsync("custom1", s => s with { Pl1 = 55 }, "old edit");
            Assert.True(parameterSent.Task.Wait(TimeSpan.FromSeconds(2)));
            var switching = perf.ActivateAsync("custom2", "new selection");
            Assert.True(Task.WhenAll(editing, switching).Wait(TimeSpan.FromMilliseconds(800)));
            Assert.Null(editing.Result);
            Assert.True(switching.Result!.ModeSwitched);
            Assert.Equal(55, PerfModeStore.LoadSettings("custom1").Pl1);
            Assert.True(string.IsNullOrEmpty(PerfModeStore.LoadSlots(PerfModeService.SlotCount)[0].Signature));
            Assert.Equal("custom2", perf.ActiveModeId);
        });
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(1, 0)]
    public void ObsoleteFramesCannotOverwriteTheTargetProfileOrItsParameters(int mode, int profile)
    {
        using var hw = new MechrevoHw((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities());
        hw.HandleMessage("Fan/Status", "{\"OperatingMode\":3,\"CustomProfileIndex\":1,\"CPU_PL1\":70}");
        hw.MarkModeSwitchPending(3, 0);
        hw.HandleMessage("Fan/Status", "{\"OperatingMode\":3,\"CustomProfileIndex\":0,\"CPU_PL1\":45}");
        long version = hw.CustomProfileStatusVersion;
        hw.HandleMessage("Fan/Status", $"{{\"OperatingMode\":{mode},\"CustomProfileIndex\":{profile},\"CPU_PL1\":100}}");
        Assert.Equal(3, hw.OperatingMode);
        Assert.Equal(0, hw.CustomProfileIndex);
        Assert.Equal(45, hw.Pl1);
        Assert.Equal(version, hw.CustomProfileStatusVersion);
    }

    [Fact]
    public void AFrameWithoutAProfileCannotProvideFreshProfileConfirmation()
    {
        using var hw = new MechrevoHw((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities());
        hw.HandleMessage("Fan/Status", "{\"OperatingMode\":3,\"CustomProfileIndex\":0}");
        long before = hw.CustomProfileStatusVersion;
        hw.HandleMessage("Fan/Status", "{\"OperatingMode\":3}");
        Assert.Equal(before, hw.CustomProfileStatusVersion);
    }

    [Fact]
    public void ConfirmedCustom2ToCustom1DoesNotWaitForAnotherStatusFrame()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            MechrevoHw? target = null;
            int statusRequests = 0;
            using var hw = new MechrevoHw((topic, payload) =>
            {
                JObject message = JObject.FromObject(payload);
                if (topic == "Fan/Control" && message["Action"]?.ToString() == "OPERATING_CUSTOM_MODE")
                    target!.HandleMessage("Fan/Status", "{\"OperatingMode\":3,\"CustomProfileIndex\":0}");
                if (topic == "Fan/Control" && message["Action"]?.ToString() == "GETSTATUS") statusRequests++;
                return Task.CompletedTask;
            }, new MechrevoDeviceCapabilities());
            target = hw;
            hw.HandleMessage("Fan/Status", "{\"OperatingMode\":3,\"CustomProfileIndex\":1}");
            var service = new MechrevoService(hw, () => false, () => 0, () => false);
            Task<bool> switching = service.SwitchCustomProfileWithResend(0);
            Assert.True(switching.Wait(TimeSpan.FromMilliseconds(600)), "A confirmed switch must not add a 900 ms status wait.");
            Assert.True(switching.Result);
            Assert.Equal(0, statusRequests);
        });
    }

    [Fact]
    public async Task VendorCompletionAfterTheMergeWindowIsAcceptedWithoutAnExtraSwitch()
    {
        MechrevoHw? target = null;
        Task response = Task.CompletedTask;
        int commands = 0;
        using var hw = new MechrevoHw((topic, payload) =>
        {
            JObject message = JObject.FromObject(payload);
            if (topic == "Fan/Control" && message["Action"]?.ToString() == "OPERATING_CUSTOM_MODE")
            {
                commands++;
                response = Task.Run(async () =>
                {
                    await Task.Delay(1550);
                    target!.HandleMessage("Fan/Status", "{\"OperatingMode\":3,\"CustomProfileIndex\":0}");
                });
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities());
        target = hw;
        hw.HandleMessage("Fan/Status", "{\"OperatingMode\":3,\"CustomProfileIndex\":1}");
        var service = new MechrevoService(hw, () => false, () => 0, () => false);
        Assert.True(await service.SwitchCustomProfileWithResend(0).WaitAsync(TimeSpan.FromSeconds(6)));
        await response;
        Assert.Equal(1, commands);
    }

    [Fact]
    public async Task AnAlreadyActiveProfileDoesNotSendAnotherModeCommand()
    {
        int commands = 0;
        using var hw = new MechrevoHw((_, _) => { commands++; return Task.CompletedTask; }, new MechrevoDeviceCapabilities());
        hw.HandleMessage("Fan/Status", "{\"OperatingMode\":3,\"CustomProfileIndex\":0}");
        var service = new MechrevoService(hw, () => false, () => 0, () => false);
        Assert.True(await service.SwitchCustomProfileWithResend(0));
        Assert.Equal(0, commands);
    }

    [Fact]
    public void NewUserActivationCancelsThePreviousHardwareWaitInsteadOfWaitingForItsRetries()
    {
        PerfModeRecoveryTests.WithModes(() =>
        {
            MechrevoHw? target = null;
            var firstPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var hw = new MechrevoHw((topic, payload) =>
            {
                JObject message = JObject.FromObject(payload);
                if (topic == "Fan/Control" && message["Action"]?.ToString() == "OPERATING_CUSTOM_MODE")
                {
                    if (message["ProfileIndex"]!.Value<int>() == 0) firstPublished.TrySetResult();
                    else target!.HandleMessage("Fan/Status", "{\"OperatingMode\":3,\"CustomProfileIndex\":1}");
                }
                if (topic == "Fan/Control" && message["Action"]?.ToString() == "SET_CUSTOM_PROFILE_OSD_STRING")
                    target!.HandleMessage("Fan/Status", $"{{\"OperatingMode\":3,\"CustomProfileIndex\":1,\"ProfileName\":{message["ProfileName"]!.ToString(Newtonsoft.Json.Formatting.None)}}}");
                return Task.CompletedTask;
            }, new MechrevoDeviceCapabilities { ProfileAvailable = true });
            target = hw;
            hw.HandleMessage("Fan/Status", "{\"OperatingMode\":1,\"CustomProfileIndex\":0}");
            Assert.Equal("custom2", PerfModeStore.AddCustom()!.Id);
            var hardwareService = new MechrevoService(hw, () => false, () => 0, () => false);
            var perf = new PerfModeService(hw, new PerfModeBackend(hardwareService, hw));
            var slots = PerfModeStore.LoadSlots(PerfModeService.SlotCount);
            slots = FirmwareSlotPlanner.Assign(slots, 0, "custom1", PerfModeSettings.Default.FirmwareSignature(), 1);
            slots = FirmwareSlotPlanner.Assign(slots, 1, "custom2", PerfModeSettings.Default.FirmwareSignature(), 2);
            PerfModeStore.SaveSlots(slots);
            Task<PerfApplyOutcome?> first = perf.ActivateAsync("custom1", "first");
            Assert.True(firstPublished.Task.Wait(TimeSpan.FromSeconds(2)));
            Task<PerfApplyOutcome?> second = perf.ActivateAsync("custom2", "latest");
            Assert.True(Task.WhenAll(first, second).Wait(TimeSpan.FromMilliseconds(800)), "The latest request must not queue behind the old confirmation timeout.");
            Assert.Null(first.Result);
            Assert.True(second.Result!.ModeSwitched);
            Assert.Equal("custom2", perf.ActiveModeId);
            Assert.Null(perf.PendingModeId);
        });
    }

    [Fact]
    public void CompleteCachedFanCurvesDoNotWaitForTheOldTableToRefresh()
    {
        PerfModeRecoveryTests.WithModes(() =>
        {
            using var hw = new MechrevoHw((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities { ProfileAvailable = true });
            hw.HandleMessage("Fan/Status", "{\"OperatingMode\":3,\"CustomProfileIndex\":0}");
            hw.HandleMessage("Fan/Table", "{\"Name\":\"M4T2\",\"CPU\":[{\"Duty\":40}],\"GPU\":[{\"Duty\":40}]}");
            PerfModeStore.SaveSettings("custom1", new PerfModeSettings
                { CpuFanDuty = Enumerable.Repeat(40, 16).ToArray(), GpuFanDuty = Enumerable.Repeat(40, 16).ToArray() });
            var backend = new PerfModeRunnerTests.FakeBackend();
            var service = new PerfModeService(hw, backend);
            Task<PerfApplyOutcome?> switching = service.ActivateAsync("custom1", "cached");
            Assert.True(switching.Wait(TimeSpan.FromMilliseconds(600)), "A cache hit must not wait 1500 ms for a fan table already saved in the mode.");
            Assert.True(switching.Result!.ModeSwitched);
        });
    }
}
