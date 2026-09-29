using MechrevoLite.Mode;

namespace MechrevoLite.Tests;

/// <summary>
/// 下发计划：路线、顺序、线上字段名、能力门控。
///
/// 最关键的一条：内置路线**绝不下发**被固件门控的项（PL/TGP/DB/风扇曲线）——
/// 真机实测这些项在内置模式下写了不生效，发出去就是伪功能。
/// </summary>
public class PerfModeApplyPlanTests
{
    static PerfModeDefinition BuiltIn(PerfModeKind kind, PerfModeSettings s) =>
        new(PerfModeDefinition.BuiltInId(kind), kind, null, s);

    static PerfModeDefinition Custom(PerfModeSettings s) =>
        new("custom1", PerfModeKind.Custom, null, s);

    static FirmwareSlotPlan Fresh(int slot = 0) => new(slot, true, "free slot");
    static FirmwareSlotPlan Hit(int slot = 0) => new(slot, false, "slot hit");

    static IReadOnlyList<PerfApplyStep> Build(PerfModeDefinition mode, FirmwareSlotPlan? plan = null,
        PerfModeCapabilities? caps = null) =>
        PerfModeApplyPlanner.Build(mode, plan, caps ?? PerfModeCapabilities.All);

    [Fact]
    public void AnUntouchedBuiltInModeIsJustTheVendorSwitch()
    {
        IReadOnlyList<PerfApplyStep> steps = Build(BuiltIn(PerfModeKind.Balanced, PerfModeSettings.Default));

        Assert.Single(steps);
        Assert.Equal(PerfApplyStepKind.SwitchBuiltIn, steps[0].Kind);
        Assert.Equal(PerfApplyVerify.ModeReadback, steps[0].Verify);
    }

    [Fact]
    public void TheBuiltInRouteNeverSendsFirmwareGatedFields()
    {
        // 这些项在内置模式下写了不生效；真正的做法是让这个模式改走自定义档。
        // 这里刻意构造一个「路线是内置、但参数里有门控项」的非法组合来锁死行为。
        var settings = new PerfModeSettings { TccOn = true, TccTarget = 90, GpuCoreOffset = 105 };
        IReadOnlyList<PerfApplyStep> steps = Build(BuiltIn(PerfModeKind.Turbo, settings));

        string[] keys = steps.Where(s => s.Kind == PerfApplyStepKind.WriteFirmwareField)
            .Select(s => s.WireKey!).ToArray();

        Assert.DoesNotContain("PL1", keys);
        Assert.DoesNotContain("GpuConfigurableTGPTarget", keys);
        Assert.DoesNotContain("GpuDynamicBoost", keys);
        Assert.DoesNotContain(steps, s => s.Kind == PerfApplyStepKind.WriteFanCurve);
        // 内置模式下确实生效的项照常下发。
        Assert.Contains("CpuTccOffsetSwitch", keys);
        Assert.Contains("CpuTccOffset", keys);
        // 显卡超频在内置模式下只走 GCU（直连 NVAPI 被挂起）：总闸 + 两个偏移合成一步，
        // 未设的显存偏移按 0 一起带上，不能让它残留上一个模式的值。
        PerfApplyStep oc = Assert.Single(steps, s => s.Kind == PerfApplyStepKind.ApplyGpuOverclock);
        Assert.Equal(1, oc.Number);
        Assert.Equal((105, 0), oc.GpuOffsets);
        Assert.Equal(PerfApplyVerify.GpuClockOffset, oc.Verify);
        Assert.DoesNotContain("GpuCoreClockOffsetOC", keys);
    }

    [Fact]
    public void TurningBuiltInOverclockOffZeroesBothOffsets()
    {
        var settings = new PerfModeSettings { GpuOverclockOn = false, GpuCoreOffset = 150, GpuMemoryOffset = 400 };

        PerfApplyStep oc = Assert.Single(Build(BuiltIn(PerfModeKind.Turbo, settings)),
            s => s.Kind == PerfApplyStepKind.ApplyGpuOverclock);

        Assert.Equal(0, oc.Number);
        Assert.Equal((0, 0), oc.GpuOffsets);
    }

    [Fact]
    public void ADeltaBuildNeverSwitchesAndUsesTheFullModesRoute()
    {
        // 编辑器增量：只含 Tcc 变化的增量本身推不出「自定义档」路线，必须用完整模式的路线。
        PerfModeDefinition delta = BuiltIn(PerfModeKind.Balanced, new PerfModeSettings { TccTarget = 88 });

        IReadOnlyList<PerfApplyStep> steps = PerfModeApplyPlanner.Build(delta, Fresh(1), PerfModeCapabilities.All,
            includeSwitch: false, routeOverride: PerfModeRoute.FirmwareSlot);

        Assert.DoesNotContain(steps, s => s.Kind is PerfApplyStepKind.SwitchBuiltIn or PerfApplyStepKind.SwitchFirmwareSlot);
        Assert.Equal("CpuTccOffset", Assert.Single(steps).WireKey);
    }

    [Fact]
    public void ACustomisedBuiltInModeSwitchesToASlotAndWritesEverything()
    {
        var settings = new PerfModeSettings { Pl1 = 60, Pl2 = 70, GpuTgp = 100 };
        PerfModeDefinition mode = BuiltIn(PerfModeKind.Silent, settings);

        IReadOnlyList<PerfApplyStep> steps = Build(mode, Fresh(2));

        Assert.Equal(PerfApplyStepKind.SwitchFirmwareSlot, steps[0].Kind);
        Assert.Equal(2, steps[0].Number);
        string[] keys = steps.Where(s => s.Kind == PerfApplyStepKind.WriteFirmwareField)
            .Select(s => s.WireKey!).ToArray();
        Assert.Equal(new[] { "PL1", "PL2", "GpuConfigurableTGPTarget" }, keys);
    }

    [Fact]
    public void HittingAnAlreadyOwnedSlotSkipsTheParameterWrite()
    {
        var settings = new PerfModeSettings { Pl1 = 60, GpuTgp = 100 };

        IReadOnlyList<PerfApplyStep> steps = Build(Custom(settings), Hit(1));

        Assert.Single(steps);
        Assert.Equal(PerfApplyStepKind.SwitchFirmwareSlot, steps[0].Kind);
    }

    [Fact]
    public void GateFieldsComeBeforeTheirValueFields()
    {
        var settings = new PerfModeSettings
        {
            TccOn = true, TccTarget = 90,
            GpuDynamicBoostOn = true, GpuDynamicBoost = 20,
            GpuOverclockOn = true, GpuCoreOffset = 105,
            FanSwitchSpeedOn = true, FanSwitchSpeedMs = 400,
        };

        IReadOnlyList<PerfApplyStep> steps = Build(Custom(settings), Fresh());
        List<string> keys = steps.Where(s => s.Kind == PerfApplyStepKind.WriteFirmwareField)
            .Select(s => s.WireKey!).ToList();

        void Before(string gate, string value) =>
            Assert.True(keys.IndexOf(gate) >= 0 && keys.IndexOf(gate) < keys.IndexOf(value),
                $"{gate} 必须排在 {value} 之前");

        Before("CpuTccOffsetSwitch", "CpuTccOffset");
        Before("GpuDynamicBoostSwitch", "GpuDynamicBoost");
        Before("OverClockingSwitch", "GpuCoreClockOffsetOC");
        Before("FanSwitchSpeedEnabled", "FanSwitchSpeed");
    }

    [Fact]
    public void PowerLimitsAreSentInAscendingOrderSoTheVendorClampDoesNotFight()
    {
        var settings = new PerfModeSettings { Pl1 = 100, Pl2 = 120, Pl4 = 140 };

        List<string> keys = Build(Custom(settings), Fresh())
            .Where(s => s.Kind == PerfApplyStepKind.WriteFirmwareField)
            .Select(s => s.WireKey!).ToList();

        Assert.Equal(new[] { "PL1", "PL2", "PL4" }, keys);
    }

    [Fact]
    public void PlannerAlwaysEmitsServiceLevelKeysAndLeavesWireTranslationToTheService()
    {
        // MechrevoService.SetCustomDetail 只认 PL1/PL2/PL4/CpuTccOffset，并在发布前统一换成
        // AMD 键、PL4 折半、Tcc 目标温度→原始偏移。计划器若先换一遍，AMD 键会被服务的
        // 能力校验拒掉（未知键），PL4 会被折半两次。
        var settings = new PerfModeSettings { Pl1 = 60, Pl2 = 70, Pl4 = 90, TccOn = true, TccTarget = 90 };

        List<string> keys = Build(Custom(settings), Fresh(), PerfModeCapabilities.All)
            .Where(s => s.Kind == PerfApplyStepKind.WriteFirmwareField)
            .Select(s => s.WireKey!).ToList();

        Assert.Contains("PL1", keys);
        Assert.Contains("PL2", keys);
        Assert.Contains("PL4", keys);
        Assert.Contains("CpuTccOffset", keys);
        Assert.DoesNotContain(keys, k => k.StartsWith("CpuAmd", StringComparison.Ordinal));
    }

    [Fact]
    public void Pl4TravelsAsUserWattsBecauseTheServiceHalvesItOnTheWire()
    {
        var settings = new PerfModeSettings { Pl4 = 200 };

        PerfApplyStep step = Build(Custom(settings), Fresh(), PerfModeCapabilities.All)
            .First(s => s.WireKey == "PL4");

        Assert.Equal("200", step.Value);
    }

    [Fact]
    public void FanBoostIsAppliedAfterTheModeSwitchBecauseTheVendorClearsIt()
    {
        // 厂商 UserSet_Mode1/2/3 每次都 UserSet_FanBoost(0)，所以顺序反了就永远是关。
        var settings = new PerfModeSettings { FanBoost = true };

        IReadOnlyList<PerfApplyStep> steps = Build(BuiltIn(PerfModeKind.Balanced, settings));

        int switchIndex = steps.ToList().FindIndex(s => s.Kind == PerfApplyStepKind.SwitchBuiltIn);
        int boostIndex = steps.ToList().FindIndex(s => s.Kind == PerfApplyStepKind.ApplyFanBoost);
        Assert.True(switchIndex >= 0 && boostIndex > switchIndex);
    }

    [Fact]
    public void ThePowerPlanIsAppliedBeforeCpuBoost()
    {
        // 睿频写的是「当前活动电源计划」的索引，计划换晚了会把睿频写到旧计划上。
        var settings = new PerfModeSettings
        {
            PowerPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e",
            CpuBoost = 2,
            WindowsPowerMode = 2,
        };

        List<PerfApplyStepKind> kinds = Build(BuiltIn(PerfModeKind.Balanced, settings))
            .Select(s => s.Kind).ToList();

        // 计划 → 覆盖层（设置它会先切回平衡计划）→ 睿频（写到最终的那个计划上）。
        Assert.True(kinds.IndexOf(PerfApplyStepKind.ApplyPowerPlan)
            < kinds.IndexOf(PerfApplyStepKind.ApplyPowerOverlay));
        Assert.True(kinds.IndexOf(PerfApplyStepKind.ApplyPowerOverlay)
            < kinds.IndexOf(PerfApplyStepKind.ApplyCpuBoost));
    }

    [Fact]
    public void ANonBalancedPlanIsNotUndoneByThePowerModeOverlay()
    {
        // 覆盖层只在平衡计划下生效，设置它会把计划切回平衡——用户明确选了高性能计划时不能悄悄撤销。
        var settings = new PerfModeSettings
        {
            PowerPlanGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            WindowsPowerMode = 2,
        };

        List<PerfApplyStepKind> kinds = Build(BuiltIn(PerfModeKind.Balanced, settings))
            .Select(s => s.Kind).ToList();

        Assert.Contains(PerfApplyStepKind.ApplyPowerPlan, kinds);
        Assert.DoesNotContain(PerfApplyStepKind.ApplyPowerOverlay, kinds);
    }

    [Theory]
    [InlineData(PerfModeKind.Turbo, 0)]
    [InlineData(PerfModeKind.SilentTurbo, 1)]
    public void TurboAndSilentTurboShareTheFirmwareSlotAndDifferBySubMode(PerfModeKind kind, int expected)
    {
        IReadOnlyList<PerfApplyStep> steps = Build(BuiltIn(kind, PerfModeSettings.Default));

        PerfApplyStep sub = steps.Single(s => s.Kind == PerfApplyStepKind.SwitchTurboSubMode);
        Assert.Equal(expected, sub.Number);
    }

    [Fact]
    public void MachinesWithoutTurboSubModesGetNoSubModeStep()
    {
        IReadOnlyList<PerfApplyStep> steps = Build(
            BuiltIn(PerfModeKind.Turbo, PerfModeSettings.Default),
            caps: PerfModeCapabilities.All with { SupportsTurboSubMode = false });

        Assert.DoesNotContain(steps, s => s.Kind == PerfApplyStepKind.SwitchTurboSubMode);
    }

    [Fact]
    public void UnsupportedCapabilitiesDropTheirStepsInsteadOfFailing()
    {
        var settings = new PerfModeSettings
        {
            GpuTgp = 100, GpuDynamicBoost = 20, GpuCoreOffset = 105,
            TccTarget = 90, FanSwitchSpeedMs = 400, FanBoost = true,
            CpuFanDuty = new int[16], RefreshHz = 60,
        };
        var caps = new PerfModeCapabilities
        {
            SupportsTcc = false, SupportsTgp = false, SupportsDynamicBoost = false,
            SupportsGpuOverclock = false, SupportsFanCurve = false, SupportsFanBoost = false,
            SupportsFanSwitchSpeed = false, SupportsRefreshRate = false, SupportsTurboSubMode = false,
        };

        IReadOnlyList<PerfApplyStep> steps = Build(Custom(settings), Fresh(), caps);

        Assert.Single(steps);
        Assert.Equal(PerfApplyStepKind.SwitchFirmwareSlot, steps[0].Kind);
    }

    [Fact]
    public void EveryFirmwareGatedStepCarriesAHardwareSideVerdict()
    {
        // 「不做伪功能」的机器可读版本：这些项不能用服务端回显当成功判据。
        var settings = new PerfModeSettings
        {
            Pl1 = 60, GpuTgp = 100, GpuCoreOffset = 105, CpuFanDuty = new int[16],
        };

        IReadOnlyList<PerfApplyStep> steps = Build(Custom(settings), Fresh());

        Assert.Equal(PerfApplyVerify.CpuPackagePower, steps.First(s => s.WireKey == "PL1").Verify);
        Assert.Equal(PerfApplyVerify.GpuPowerLimit,
            steps.First(s => s.WireKey == "GpuConfigurableTGPTarget").Verify);
        Assert.Equal(PerfApplyVerify.GpuClockOffset,
            steps.First(s => s.WireKey == "GpuCoreClockOffsetOC").Verify);
        Assert.Equal(PerfApplyVerify.FanDuty,
            steps.First(s => s.Kind == PerfApplyStepKind.WriteFanCurve).Verify);
    }

    [Fact]
    public void ASlotRouteWithoutAPlanIsARejectedCall()
    {
        Assert.Throws<ArgumentNullException>(() => Build(Custom(PerfModeSettings.Default)));
    }

    [Fact]
    public void AFanCurveThatIsNotSixteenPointsIsIgnored()
    {
        var settings = new PerfModeSettings { CpuFanDuty = new[] { 1, 2, 3 } };

        IReadOnlyList<PerfApplyStep> steps = Build(Custom(settings), Fresh());

        Assert.DoesNotContain(steps, s => s.Kind == PerfApplyStepKind.WriteFanCurve);
    }
}
