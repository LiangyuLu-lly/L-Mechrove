using MechrevoLite.Mode;

namespace MechrevoLite.Tests;

/// <summary>
/// 下发执行器：三态结果、切换失败即中止、逐字段节流。
///
/// 「已下发但判不出生效」必须独立成一态——把它算成成功就是伪功能。
/// </summary>
public class PerfModeRunnerTests
{
    internal sealed class FakeBackend : IPerfModeBackend
    {
        public List<string> Calls { get; } = new();
        public List<int> Delays { get; } = new();
        public HashSet<string> FailIssue { get; } = new();
        public Dictionary<string, bool?> Verdicts { get; } = new();
        public bool? DefaultVerdict { get; set; } = true;
        public Exception? ThrowOn { get; set; }
        public string? ThrowForKey { get; set; }
        public string? ThrowVerifyForKey { get; set; }

        static string Key(PerfApplyStep s) => s.ToString();

        Task<bool> Record(PerfApplyStep step)
        {
            string key = Key(step);
            Calls.Add(key);
            if (ThrowOn is not null && ThrowForKey == key) throw ThrowOn;
            return Task.FromResult(!FailIssue.Contains(key));
        }

        public Task<bool> SwitchBuiltInAsync(PerfModeKind kind, CancellationToken ct) =>
            Record(new PerfApplyStep(PerfApplyStepKind.SwitchBuiltIn, Number: (int)kind));

        public Task<bool> SwitchTurboSubModeAsync(bool silent, CancellationToken ct) =>
            Record(new PerfApplyStep(PerfApplyStepKind.SwitchTurboSubMode, Number: silent ? 1 : 0));

        public Task<bool> SwitchFirmwareSlotAsync(int slotIndex, CancellationToken ct) =>
            Record(new PerfApplyStep(PerfApplyStepKind.SwitchFirmwareSlot, Number: slotIndex));

        public Task<bool> WriteDetailFieldAsync(string wireKey, string value, CancellationToken ct) =>
            Record(new PerfApplyStep(PerfApplyStepKind.WriteFirmwareField, wireKey, value));

        public Task<bool> WriteFanCurveAsync(bool cpu, int[] duty, CancellationToken ct) =>
            Record(new PerfApplyStep(PerfApplyStepKind.WriteFanCurve, IsCpuCurve: cpu, Curve: duty));

        public Task<bool> ApplyGpuOverclockAsync(bool on, int core, int memory, CancellationToken ct) =>
            Record(new PerfApplyStep(PerfApplyStepKind.ApplyGpuOverclock, Number: on ? 1 : 0, GpuOffsets: (core, memory)));

        public List<string> SlotNames { get; } = new();

        public Task<bool> SetSlotNameAsync(string name, CancellationToken ct)
        {
            SlotNames.Add(name);
            return Task.FromResult(true);
        }

        public Task<bool> SetFanBoostAsync(bool on, CancellationToken ct) =>
            Record(new PerfApplyStep(PerfApplyStepKind.ApplyFanBoost, Number: on ? 1 : 0));

        public Task<bool> SetRefreshRateAsync(int hz, CancellationToken ct) =>
            Record(new PerfApplyStep(PerfApplyStepKind.ApplyRefreshRate, Number: hz));

        public bool SetPowerPlan(string guid)
        {
            Calls.Add($"{PerfApplyStepKind.ApplyPowerPlan}:0");
            return !FailIssue.Contains($"{PerfApplyStepKind.ApplyPowerPlan}:0");
        }

        public bool SetCpuBoost(int index)
        {
            Calls.Add($"{PerfApplyStepKind.ApplyCpuBoost}:{index}");
            return !FailIssue.Contains($"{PerfApplyStepKind.ApplyCpuBoost}:{index}");
        }

        public bool SetPowerOverlay(int mode)
        {
            Calls.Add($"{PerfApplyStepKind.ApplyPowerOverlay}:{mode}");
            return !FailIssue.Contains($"{PerfApplyStepKind.ApplyPowerOverlay}:{mode}");
        }

        public Task DelayAsync(int milliseconds, CancellationToken ct)
        {
            Delays.Add(milliseconds);
            return Task.CompletedTask;
        }

        public Task<bool?> VerifyAsync(PerfApplyStep step, CancellationToken ct)
        {
            if (ThrowVerifyForKey == Key(step)) throw new InvalidOperationException("readback unavailable");
            return Task.FromResult(Verdicts.TryGetValue(Key(step), out bool? v) ? v : DefaultVerdict);
        }
    }

    static PerfApplyStep Switch(int slot = 0) =>
        new(PerfApplyStepKind.SwitchFirmwareSlot, Number: slot, Verify: PerfApplyVerify.ModeReadback);

    [Fact]
    public async Task FailedTurboSubModeDoesNotApplyParametersOrClaimTheModeSwitched()
    {
        var backend = new FakeBackend();
        backend.FailIssue.Add("SwitchTurboSubMode:1");
        var steps = new[] { Switch(), new PerfApplyStep(PerfApplyStepKind.SwitchTurboSubMode, Number: 1), Field("PL1", "45") };
        PerfApplyOutcome result = await PerfModeRunner.RunAsync("silentturbo", steps, backend);
        Assert.False(result.ModeSwitched);
        Assert.Equal(2, result.Steps.Count);
    }

    [Fact]
    public async Task FieldReadbackExceptionIsReportedAndLeavesFirmwareUnsynchronized()
    {
        var backend = new FakeBackend { ThrowVerifyForKey = "WriteFirmwareField:PL1=45" };
        PerfApplyOutcome result = await PerfModeRunner.RunAsync("custom1", new[] { Switch(), Field("PL1", "45"), Field("PL2", "90") }, backend);
        Assert.True(result.ModeSwitched);
        Assert.False(result.FirmwareParametersIssued);
        Assert.Single(result.Failed);
        Assert.Equal(3, result.Steps.Count);
    }

    static PerfApplyStep Field(string key, string value, PerfApplyVerify v = PerfApplyVerify.ServiceEcho) =>
        new(PerfApplyStepKind.WriteFirmwareField, key, value, Verify: v);

    [Fact]
    public async Task EveryStepRunsInOrderAndAConfirmedRunHasNoFailures()
    {
        var backend = new FakeBackend();
        var steps = new[] { Switch(1), Field("PL1", "60"), Field("PL2", "70") };

        PerfApplyOutcome outcome = await PerfModeRunner.RunAsync("custom1", steps, backend);

        Assert.Equal(new[] { "SwitchFirmwareSlot:1", "WriteFirmwareField:PL1=60", "WriteFirmwareField:PL2=70" },
            backend.Calls);
        Assert.False(outcome.AnyFailed);
        Assert.True(outcome.ModeSwitched);
        Assert.Empty(outcome.Unverified);
    }

    [Fact]
    public async Task AnUnverifiableStepIsReportedAsSentNotConfirmed()
    {
        var backend = new FakeBackend();
        backend.Verdicts["WriteFirmwareField:PL1=60"] = null;   // 轻载时功耗墙判不出来
        var steps = new[] { Switch(), Field("PL1", "60", PerfApplyVerify.CpuPackagePower) };

        PerfApplyOutcome outcome = await PerfModeRunner.RunAsync("custom1", steps, backend);

        Assert.False(outcome.AnyFailed);
        PerfApplyStepOutcome pl1 = Assert.Single(outcome.Unverified);
        Assert.Equal(PerfApplyResult.Sent, pl1.Result);
        Assert.Contains("无法判定", pl1.Detail);
    }

    [Fact]
    public async Task AReadbackThatDisagreesIsAFailureNotASuccess()
    {
        var backend = new FakeBackend();
        backend.Verdicts["WriteFirmwareField:GpuConfigurableTGPTarget=100"] = false;
        var steps = new[] { Switch(), Field("GpuConfigurableTGPTarget", "100", PerfApplyVerify.GpuPowerLimit) };

        PerfApplyOutcome outcome = await PerfModeRunner.RunAsync("custom1", steps, backend);

        Assert.True(outcome.AnyFailed);
        Assert.Equal("回读与目标不一致", outcome.Failed[0].Detail);
    }

    [Fact]
    public async Task AFailedModeSwitchAbortsTheRunSoParametersNeverLandOnTheWrongSlot()
    {
        var backend = new FakeBackend();
        backend.FailIssue.Add("SwitchFirmwareSlot:0");
        var steps = new[] { Switch(), Field("PL1", "60") };

        PerfApplyOutcome outcome = await PerfModeRunner.RunAsync("custom1", steps, backend);

        Assert.Single(outcome.Steps);
        Assert.True(outcome.AnyFailed);
        Assert.False(outcome.ModeSwitched);
        Assert.DoesNotContain("WriteFirmwareField:PL1=60", backend.Calls);
    }

    [Fact]
    public async Task AnUnconfirmedModeSwitchAlsoAbortsTheRun()
    {
        var backend = new FakeBackend();
        backend.Verdicts["SwitchFirmwareSlot:0"] = false;
        var steps = new[] { Switch(), Field("PL1", "60") };

        PerfApplyOutcome outcome = await PerfModeRunner.RunAsync("custom1", steps, backend);

        Assert.Single(outcome.Steps);
        Assert.DoesNotContain("WriteFirmwareField:PL1=60", backend.Calls);
    }

    [Fact]
    public async Task AFailedParameterDoesNotStopTheRemainingParameters()
    {
        var backend = new FakeBackend();
        backend.FailIssue.Add("WriteFirmwareField:PL1=60");
        var steps = new[] { Switch(), Field("PL1", "60"), Field("PL2", "70") };

        PerfApplyOutcome outcome = await PerfModeRunner.RunAsync("custom1", steps, backend);

        Assert.Equal(3, outcome.Steps.Count);
        Assert.Single(outcome.Failed);
        Assert.Contains("WriteFirmwareField:PL2=70", backend.Calls);
    }

    [Fact]
    public async Task ConsecutiveFirmwareFieldsAreThrottledButOtherStepsAreNot()
    {
        var backend = new FakeBackend();
        var steps = new[]
        {
            Switch(), Field("PL1", "60"), Field("PL2", "70"),
            new PerfApplyStep(PerfApplyStepKind.ApplyFanBoost, Number: 1),
            new PerfApplyStep(PerfApplyStepKind.ApplyPowerOverlay, Number: 2),
        };

        await PerfModeRunner.RunAsync("custom1", steps, backend);

        // 只有 PL1 -> PL2 之间需要等；切换后的第一个字段和非固件步骤都不等。
        Assert.Equal(new[] { PerfModeApplyPlanner.FieldDelayMs }, backend.Delays);
    }

    [Fact]
    public async Task AnExceptionInOneParameterIsContainedAndRecorded()
    {
        var backend = new FakeBackend
        {
            ThrowOn = new InvalidOperationException("MQTT 断开"),
            ThrowForKey = "WriteFirmwareField:PL1=60",
        };
        var steps = new[] { Switch(), Field("PL1", "60"), Field("PL2", "70") };

        PerfApplyOutcome outcome = await PerfModeRunner.RunAsync("custom1", steps, backend);

        Assert.Single(outcome.Failed);
        Assert.Contains("MQTT 断开", outcome.Failed[0].Detail);
        Assert.Contains("WriteFirmwareField:PL2=70", backend.Calls);
    }

    [Fact]
    public async Task CancellationPropagatesInsteadOfBeingSwallowed()
    {
        var backend = new FakeBackend();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PerfModeRunner.RunAsync("custom1", new[] { Switch() }, backend, cts.Token));
    }

    [Fact]
    public async Task AnEmptyPlanIsAValidNoOp()
    {
        var backend = new FakeBackend();

        PerfApplyOutcome outcome = await PerfModeRunner.RunAsync("balanced", Array.Empty<PerfApplyStep>(), backend);

        Assert.Empty(outcome.Steps);
        Assert.False(outcome.AnyFailed);
        Assert.False(outcome.ModeSwitched);
        Assert.Equal("(no steps)", outcome.Describe());
    }
}
