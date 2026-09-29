using MechrevoLite.Mode;

namespace MechrevoLite.Tests;

/// <summary>
/// 性能模式重做的核心逻辑：路线判定（内置 vs 固件自定义档）与档位映射。
///
/// 路线判据来自真机实证（docs/hardware/gcu-modes-and-profiles.md §2）：固件只在自定义模式
/// 标志位置位时才采用功耗墙/风扇表/TGP，所以内置模式里改这些项必须改由自定义档承载，
/// 否则就是「回读通过、硬件不生效」的伪功能。
/// </summary>
public class PerfModeModelTests
{
    static PerfModeDefinition BuiltIn(PerfModeKind kind, PerfModeSettings? settings = null) =>
        new(PerfModeDefinition.BuiltInId(kind), kind, null, settings ?? PerfModeSettings.Default);

    static PerfModeDefinition Custom(int ordinal, PerfModeSettings? settings = null) =>
        new(PerfModeDefinition.CustomId(ordinal), PerfModeKind.Custom, null, settings ?? PerfModeSettings.Default);

    [Fact]
    public void AnUntouchedBuiltInModeKeepsTheVendorPath()
    {
        PerfModeDefinition mode = BuiltIn(PerfModeKind.Balanced);

        Assert.True(mode.Settings.IsUntouched);
        Assert.Equal(PerfModeRoute.BuiltIn, mode.Route);
        Assert.False(mode.IsEmulatedBuiltIn);
    }

    [Theory]
    [InlineData("Pl1")]
    [InlineData("Pl2")]
    [InlineData("Pl4")]
    [InlineData("GpuTgp")]
    [InlineData("GpuDynamicBoost")]
    [InlineData("CpuFanDuty")]
    [InlineData("GpuFanDuty")]
    public void AFirmwareGatedOverrideMovesABuiltInModeOntoACustomSlot(string field)
    {
        PerfModeSettings settings = field switch
        {
            "Pl1" => new PerfModeSettings { Pl1 = 60 },
            "Pl2" => new PerfModeSettings { Pl2 = 60 },
            "Pl4" => new PerfModeSettings { Pl4 = 90 },
            "GpuTgp" => new PerfModeSettings { GpuTgp = 100 },
            "GpuDynamicBoost" => new PerfModeSettings { GpuDynamicBoost = 15 },
            "CpuFanDuty" => new PerfModeSettings { CpuFanDuty = new int[16] },
            _ => new PerfModeSettings { GpuFanDuty = new int[16] },
        };

        PerfModeDefinition mode = BuiltIn(PerfModeKind.Silent, settings);

        Assert.True(settings.NeedsFirmwareSlot);
        Assert.Equal(PerfModeRoute.FirmwareSlot, mode.Route);
        Assert.True(mode.IsEmulatedBuiltIn);
    }

    [Theory]
    [InlineData("TccTarget")]
    [InlineData("GpuCoreOffset")]
    [InlineData("FanBoost")]
    [InlineData("FanSwitchSpeedMs")]
    [InlineData("WindowsPowerMode")]
    [InlineData("CpuBoost")]
    [InlineData("RefreshHz")]
    public void AnInPlaceSafeOverrideKeepsTheBuiltInPath(string field)
    {
        PerfModeSettings settings = field switch
        {
            "TccTarget" => new PerfModeSettings { TccOn = true, TccTarget = 90 },
            "GpuCoreOffset" => new PerfModeSettings { GpuOverclockOn = true, GpuCoreOffset = 105 },
            "FanBoost" => new PerfModeSettings { FanBoost = true },
            "FanSwitchSpeedMs" => new PerfModeSettings { FanSwitchSpeedOn = true, FanSwitchSpeedMs = 500 },
            "WindowsPowerMode" => new PerfModeSettings { WindowsPowerMode = 2 },
            "CpuBoost" => new PerfModeSettings { CpuBoost = 2 },
            _ => new PerfModeSettings { RefreshHz = 60 },
        };

        PerfModeDefinition mode = BuiltIn(PerfModeKind.Turbo, settings);

        Assert.False(settings.NeedsFirmwareSlot);
        Assert.False(settings.IsUntouched);
        Assert.Equal(PerfModeRoute.BuiltIn, mode.Route);
    }

    [Fact]
    public void ACustomModeAlwaysNeedsASlotEvenWithNoOverrides()
    {
        PerfModeDefinition mode = Custom(1);

        Assert.Equal(PerfModeRoute.FirmwareSlot, mode.Route);
        // 它本来就是自定义档，不算「模拟内置模式」。
        Assert.False(mode.IsEmulatedBuiltIn);
    }

    [Fact]
    public void TheFirmwareSignatureIgnoresAppSideOverrides()
    {
        var a = new PerfModeSettings { Pl1 = 60, WindowsPowerMode = 0, CpuBoost = 1, RefreshHz = 60 };
        var b = new PerfModeSettings { Pl1 = 60, WindowsPowerMode = 2, CpuBoost = 5, RefreshHz = 165 };

        // 应用侧项换了不必重写固件档参数，否则每次切换都白等 1-2 秒。
        Assert.Equal(a.FirmwareSignature(), b.FirmwareSignature());
    }

    [Fact]
    public void TheFirmwareSignatureTracksEveryFirmwareField()
    {
        var baseline = new PerfModeSettings { Pl1 = 60 };
        Assert.NotEqual(baseline.FirmwareSignature(), (baseline with { Pl2 = 70 }).FirmwareSignature());
        Assert.NotEqual(baseline.FirmwareSignature(), (baseline with { GpuTgp = 90 }).FirmwareSignature());
        Assert.NotEqual(baseline.FirmwareSignature(), (baseline with { TccTarget = 85 }).FirmwareSignature());
        Assert.NotEqual(baseline.FirmwareSignature(),
            (baseline with { CpuFanDuty = new[] { 1, 2, 3 } }).FirmwareSignature());
    }

    [Fact]
    public void CustomOrdinalsAreTheLowestFreeNumberAndAreNeverReused()
    {
        var modes = new List<PerfModeDefinition> { Custom(1), Custom(3) };

        Assert.Equal(2, PerfModeCollection.NextCustomOrdinal(modes));

        modes.Add(Custom(2));
        Assert.Equal(4, PerfModeCollection.NextCustomOrdinal(modes));
    }

    [Fact]
    public void TheFirstRunHasExactlyOneCustomMode()
    {
        // 需求：一开始只放一个自定义模式，之后由用户自己添加。
        Assert.Equal(1, PerfModeCollection.NextCustomOrdinal(Array.Empty<PerfModeDefinition>()));
    }

    [Fact]
    public void TheLastCustomModeCannotBeRemoved()
    {
        var single = new[] { BuiltIn(PerfModeKind.Balanced), Custom(1) };
        Assert.False(PerfModeCollection.CanRemove(single, PerfModeDefinition.CustomId(1)));

        var two = new[] { Custom(1), Custom(2) };
        Assert.True(PerfModeCollection.CanRemove(two, PerfModeDefinition.CustomId(1)));
    }

    [Fact]
    public void BuiltInModesCanNeverBeRemoved()
    {
        var modes = new[] { BuiltIn(PerfModeKind.Balanced), Custom(1), Custom(2) };
        Assert.False(PerfModeCollection.CanRemove(modes, PerfModeDefinition.BuiltInId(PerfModeKind.Balanced)));
    }

    [Fact]
    public void UserNamesAreTrimmedAndLengthLimited()
    {
        Assert.Null(PerfModeCollection.SanitizeUserName("   "));
        Assert.Equal("游戏", PerfModeCollection.SanitizeUserName("  游戏  "));
        Assert.Equal(PerfModeCollection.MaxUserNameLength,
            PerfModeCollection.SanitizeUserName(new string('x', 40))!.Length);
    }
}

/// <summary>固件档位映射：命中、空档、抢占、释放。</summary>
public class FirmwareSlotPlannerTests
{
    static List<FirmwareSlotState> Slots(int count) =>
        Enumerable.Range(0, count).Select(i => new FirmwareSlotState(i, null, null, 0)).ToList();

    [Fact]
    public void MostMachinesHaveFiveSlotsAndCustomId9HasThree()
    {
        // beta20 之前硬编码 4 档，实际是 5 档（真机已切到 M4P5 验证）。
        Assert.Equal(5, FirmwareSlotPlanner.SlotCountFor(26));
        Assert.Equal(3, FirmwareSlotPlanner.SlotCountFor(9));
    }

    [Fact]
    public void AFreeSlotIsTakenInIndexOrderAndNeedsAWrite()
    {
        FirmwareSlotPlan plan = FirmwareSlotPlanner.Plan("custom1", "sig", Slots(5));

        Assert.Equal(0, plan.SlotIndex);
        Assert.True(plan.NeedsParameterWrite);
    }

    [Fact]
    public void AnOwnedSlotWithTheSameSignatureSwitchesWithoutRewriting()
    {
        var slots = new List<FirmwareSlotState>
        {
            new(0, "custom2", "sigB", 10),
            new(1, "custom1", "sigA", 20),
        };

        FirmwareSlotPlan plan = FirmwareSlotPlanner.Plan("custom1", "sigA", slots);

        Assert.Equal(1, plan.SlotIndex);
        Assert.False(plan.NeedsParameterWrite);
    }

    [Fact]
    public void AnOwnedSlotWithAChangedSignatureIsRewritten()
    {
        var slots = new List<FirmwareSlotState> { new(0, "custom1", "old", 10) };

        FirmwareSlotPlan plan = FirmwareSlotPlanner.Plan("custom1", "new", slots);

        Assert.Equal(0, plan.SlotIndex);
        Assert.True(plan.NeedsParameterWrite);
    }

    [Fact]
    public void WhenFullTheLeastRecentlyUsedSlotIsEvicted()
    {
        var slots = new List<FirmwareSlotState>
        {
            new(0, "custom1", "a", 500),
            new(1, "custom2", "b", 100),   // 最久未用
            new(2, "custom3", "c", 300),
        };

        FirmwareSlotPlan plan = FirmwareSlotPlanner.Plan("balanced", "sig", slots);

        Assert.Equal(1, plan.SlotIndex);
        Assert.True(plan.NeedsParameterWrite);
    }

    [Fact]
    public void TheRunningModesSlotIsNeverEvicted()
    {
        // 抢走正在运行的模式那个档，会把用户眼前正在生效的参数改掉。
        var slots = new List<FirmwareSlotState>
        {
            new(0, "custom1", "a", 1),     // 最久未用，但正在运行
            new(1, "custom2", "b", 900),
        };

        FirmwareSlotPlan plan = FirmwareSlotPlanner.Plan("turbo", "sig", slots, runningModeId: "custom1");

        Assert.Equal(1, plan.SlotIndex);
    }

    [Fact]
    public void ASingleSlotMachineStillGetsAPlanEvenWhenItIsRunning()
    {
        var slots = new List<FirmwareSlotState> { new(0, "custom1", "a", 1) };

        FirmwareSlotPlan plan = FirmwareSlotPlanner.Plan("custom2", "sig", slots, runningModeId: "custom1");

        Assert.Equal(0, plan.SlotIndex);
        Assert.True(plan.NeedsParameterWrite);
    }

    [Fact]
    public void AssigningAModeReleasesTheSlotItPreviouslyOwned()
    {
        var slots = new List<FirmwareSlotState>
        {
            new(0, "custom1", "a", 10),
            new(1, null, null, 0),
        };

        IReadOnlyList<FirmwareSlotState> after =
            FirmwareSlotPlanner.Assign(slots, 1, "custom1", "b", 99);

        Assert.True(after[0].IsFree);
        Assert.Equal("custom1", after[1].OwnerModeId);
        Assert.Equal("b", after[1].Signature);
        Assert.Equal(99, after[1].LastUsedTicks);
    }

    [Fact]
    public void ReleasingADeletedModeFreesItsSlot()
    {
        var slots = new List<FirmwareSlotState> { new(0, "custom2", "a", 10), new(1, "custom1", "b", 20) };

        IReadOnlyList<FirmwareSlotState> after = FirmwareSlotPlanner.Release(slots, "custom2");

        Assert.True(after[0].IsFree);
        Assert.Equal("custom1", after[1].OwnerModeId);
    }

    [Fact]
    public void AMachineWithoutCustomSlotsIsARejectedCall()
    {
        // 不能静默退回 0 号档：没有自定义档的机型根本不该走到这里。
        Assert.Throws<ArgumentException>(() =>
            FirmwareSlotPlanner.Plan("custom1", "sig", Array.Empty<FirmwareSlotState>()));
    }
}
