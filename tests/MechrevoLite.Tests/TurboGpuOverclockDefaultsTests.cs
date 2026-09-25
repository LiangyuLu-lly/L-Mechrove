using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// Yilong 15 Pro field report: 狂暴 has no core+105 / memory+500 auto-OC.
/// Official HomePage writes those via SET_OPERATING_MODE_DETAIL; we only sent
/// OPERATING_TURBO_MODE + IsNormalRun and then suspended NvAPI.
/// </summary>
public class TurboGpuOverclockDefaultsTests
{
    static (MechrevoHw Hardware, MechrevoService Service, List<(string Topic, Dictionary<string, object> Payload)> Written) NewRig(
        MechrevoDeviceCapabilities? capabilities = null)
    {
        var written = new List<(string, Dictionary<string, object>)>();
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (payload is IDictionary<string, object> values)
            {
                written.Add((topic, new Dictionary<string, object>(values)));
                if (values.TryGetValue("Action", out object? action) && action as string == "OPERATING_TURBO_MODE")
                    hardware!.HandleMessage("Fan/Status", """{"OperatingMode":2,"OcSupport":true}""");
            }
            return Task.CompletedTask;
        }, capabilities ?? new MechrevoDeviceCapabilities { OverclockSettings = true, TurboMode = true });
        return (hardware, new MechrevoService(hardware), written);
    }

    [Theory]
    [InlineData(MechrevoService.ModeTurbo, false, true)]
    [InlineData(MechrevoService.ModeTurbo, true, false)]
    [InlineData(MechrevoService.ModeGaming, false, false)]
    [InlineData(MechrevoService.ModeOffice, false, false)]
    public void ModeSwitchAppliesOfficialTurboOcOnlyWithoutSubModes(
        int mode, bool turboSubModeSupported, bool expected) =>
        Assert.Equal(expected,
            MechrevoService.ShouldApplyTurboGpuOverclockDefaultsOnModeSwitch(mode, turboSubModeSupported));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SubModeAppliesOfficialTurboOcOnlyForExtreme(bool silent, bool expected) =>
        Assert.Equal(expected, MechrevoService.ShouldApplyTurboGpuOverclockDefaultsOnSubMode(silent));

    [Fact]
    public void OfficialTurboOffsetsAre105CoreAnd500Memory()
    {
        Assert.Equal(105, MechrevoHw.TurboGpuCoreOffsetMhz);
        Assert.Equal(500, MechrevoHw.TurboGpuMemoryOffsetMhz);
    }

    [Fact]
    public async Task ApplyTurboGpuOverclockDefaults_PublishesSwitchThenOffsetsOneFieldPerPacket()
    {
        var (hardware, service, written) = NewRig();
        using (hardware)
        {
            Assert.True(await service.ApplyTurboGpuOverclockDefaults());

            var details = written
                .Where(w => w.Topic == "Fan/Control"
                    && w.Payload.TryGetValue("Action", out object? action)
                    && action as string == "SET_OPERATING_MODE_DETAIL")
                .Select(w => w.Payload)
                .ToArray();
            Assert.Equal(3, details.Length);
            Assert.Equal("1", details[0]["OverClockingSwitch"]);
            Assert.Equal("105", details[1]["GpuCoreClockOffsetOC"]);
            Assert.Equal("500", details[2]["GpuMemoryClockOffsetOC"]);
        }
    }

    [Fact]
    public async Task ApplyTurboGpuOverclockDefaults_IsSkippedWhenOverclockIsUnsupported()
    {
        var (hardware, service, written) = NewRig(new MechrevoDeviceCapabilities
        {
            OverclockSettings = false,
            TurboMode = true,
            ProfileAvailable = true,
        });
        using (hardware)
        {
            Assert.False(await service.ApplyTurboGpuOverclockDefaults());
            Assert.DoesNotContain(written, w =>
                w.Payload.TryGetValue("Action", out object? action)
                && action as string == "SET_OPERATING_MODE_DETAIL");
        }
    }

    [Fact]
    public async Task SwitchModeTurboWithoutSubMode_SendsOfficialGpuOcAfterTheModeCommand()
    {
        var (hardware, service, written) = NewRig(new MechrevoDeviceCapabilities
        {
            OverclockSettings = true,
            TurboMode = true,
            TurboSubMode = false,
            ProfileAvailable = true,
        });
        using (hardware)
        {
            hardware.HandleMessage("Fan/Status", """{"OperatingMode":1,"OcSupport":true}""");
            Assert.True(await service.SwitchMode(MechrevoService.ModeTurbo));

            int turbo = written.FindIndex(w =>
                w.Topic == "Fan/Control"
                && w.Payload.TryGetValue("Action", out object? action)
                && action as string == "OPERATING_TURBO_MODE");
            int ocSwitch = written.FindIndex(w =>
                w.Payload.ContainsKey("OverClockingSwitch"));
            Assert.True(turbo >= 0, "turbo mode command missing");
            Assert.True(ocSwitch > turbo, "turbo GPU OC must follow the mode switch");
            Assert.Contains(written, w =>
                w.Payload.TryGetValue("GpuCoreClockOffsetOC", out object? core)
                && core as string == "105");
            Assert.Contains(written, w =>
                w.Payload.TryGetValue("GpuMemoryClockOffsetOC", out object? memory)
                && memory as string == "500");
        }
    }
}

/// <summary>
/// Yilong 15 Pro: turbo GPU fan stuck at 100%. GCU-only M3T1 ships GPU duty 100 from 0 °C
/// with linked fans. Repair by writing built-in DefaultCurve_Turbo.
/// </summary>
public class TurboFanCurveRepairTests
{
    [Fact]
    public void GcuOnlyM3t1GpuCurveIsPathological()
    {
        byte[] upT = [0, 46, 50, 54, 58, 62, 66, 70, 74, 78, 81, 255];
        byte[] duty = [100, 99, 100, 100, 100, 97, 100, 97, 100, 90, 100, 100];
        Assert.True(MechrevoHw.IsPathologicalTurboFanCurve(upT, duty));
    }

    [Fact]
    public void OfficialTurboGpuCurveIsNotPathological()
    {
        byte[] upT = [0, 46, 50, 54, 58, 62, 66, 70, 74, 78, 81, 255];
        byte[] duty = [0, 30, 35, 40, 50, 55, 60, 70, 80, 90, 100, 100];
        Assert.False(MechrevoHw.IsPathologicalTurboFanCurve(upT, duty));
    }

    [Fact]
    public void AggressiveButHighTempTurboCurveIsNotTreatedAsGarbage()
    {
        byte[] upT = [0, 54, 62, 70, 76, 81, 255];
        byte[] duty = [0, 30, 45, 70, 80, 100, 100];
        Assert.False(MechrevoHw.IsPathologicalTurboFanCurve(upT, duty));
    }

    [Fact]
    public async Task RepairPathologicalTurboFanCurve_WritesBuiltInTurboDuties()
    {
        var written = new List<(string Topic, Dictionary<string, object> Payload)>();
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            if (payload is IDictionary<string, object> values)
                written.Add((topic, new Dictionary<string, object>(values)));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { FanSettings = true, TurboMode = true });

        hardware.HandleMessage("Fan/Table", """
            {"Name":"M3T1","FanControlRespective":false,
             "CPU":[{"ID":0,"UpT":0,"Duty":0},{"ID":1,"UpT":48,"Duty":30},{"ID":2,"UpT":56,"Duty":98}],
             "GPU":[{"ID":0,"UpT":0,"Duty":100},{"ID":1,"UpT":46,"Duty":99},{"ID":2,"UpT":50,"Duty":100}]}
            """);
        Assert.True(hardware.TurboFanCurveIsPathological);
        Assert.True(await hardware.RepairPathologicalTurboFanCurveAsync());

        var gpuSet = Assert.Single(written, w =>
            w.Topic == "Fan/Control"
            && w.Payload.TryGetValue("Action", out object? action)
            && action as string == "SET_FAN_SPEED_CURVE_SETTING"
            && w.Payload.TryGetValue("Type", out object? type)
            && type as string == "GPU");
        Assert.Equal("0", gpuSet.Payload["T0"]?.ToString());
        Assert.NotEqual("100", gpuSet.Payload["T1"]?.ToString());
    }
}
