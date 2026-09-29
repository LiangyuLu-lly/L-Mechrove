using MechrevoLite.Mode;

namespace MechrevoLite.Tests;

/// <summary>
/// 模式持久化：「键不存在 = 未自定义」、初始只有一个自定义模式、新建以当前模式为起点、
/// 删除会释放固件档位。
/// </summary>
public class PerfModeStoreTests
{
    static void Clean()
    {
        foreach (string key in AppConfig.Snapshot().Keys
                     .Where(k => k.StartsWith("perf_", StringComparison.Ordinal)).ToArray())
            AppConfig.Remove(key);
    }

    [Fact]
    public void AFreshInstallHasFourBuiltInsAndExactlyOneCustomMode()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            Clean();

            IReadOnlyList<PerfModeDefinition> modes = PerfModeStore.Load();

            Assert.Equal(5, modes.Count);
            Assert.Equal(
                new[] { PerfModeKind.Silent, PerfModeKind.Balanced, PerfModeKind.SilentTurbo, PerfModeKind.Turbo },
                modes.Take(4).Select(m => m.Kind));
            Assert.Single(modes, m => m.IsCustom);
            Assert.Equal("custom1", modes[^1].Id);
            // 全新安装时每个模式都还是官方行为。
            Assert.All(modes, m => Assert.True(m.Settings.IsUntouched));
        });
    }

    [Fact]
    public void AnUnsetFieldStaysNullAndResetRemovesTheKeys()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            Clean();

            PerfModeStore.SaveSettings("balanced", new PerfModeSettings { Pl1 = 60, WindowsPowerMode = 2 });
            PerfModeSettings loaded = PerfModeStore.LoadSettings("balanced");

            Assert.Equal(60, loaded.Pl1);
            Assert.Equal(2, loaded.WindowsPowerMode);
            Assert.Null(loaded.Pl2);
            Assert.Null(loaded.GpuTgp);
            Assert.Null(loaded.PowerPlanGuid);

            PerfModeStore.ResetSettings("balanced");
            Assert.True(PerfModeStore.LoadSettings("balanced").IsUntouched);
            Assert.False(AppConfig.Exists("perf_balanced_pl1"));
        });
    }

    [Fact]
    public void FanCurvesRoundTripAsSixteenClampedPoints()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            Clean();
            var duty = new[] { 0, 30, 40, 50, 60, 70, 80, 90, 100, 100, 100, 0, 0, 0, 0, 0 };

            PerfModeStore.SaveSettings("custom1", new PerfModeSettings { CpuFanDuty = duty });
            int[]? loaded = PerfModeStore.LoadSettings("custom1").CpuFanDuty;

            Assert.NotNull(loaded);
            Assert.Equal(16, loaded!.Length);
            Assert.Equal(duty, loaded);
        });
    }

    [Fact]
    public void AddingACustomModeCopiesTheSeedModesParameters()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            Clean();
            PerfModeStore.SaveSettings("turbo", new PerfModeSettings { Pl1 = 150, GpuTgp = 175 });

            PerfModeDefinition? added = PerfModeStore.AddCustom("turbo");

            Assert.NotNull(added);
            Assert.Equal("custom2", added!.Id);
            Assert.Equal(150, added.Settings.Pl1);
            Assert.Equal(175, added.Settings.GpuTgp);
            Assert.Equal(2, PerfModeStore.Load().Count(m => m.IsCustom));
        });
    }

    [Fact]
    public void RemovingACustomModeFreesItsFirmwareSlot()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            Clean();
            PerfModeStore.AddCustom();   // custom2
            PerfModeStore.SaveSlots(new List<FirmwareSlotState>
            {
                new(0, "custom2", "sig", 5),
                new(1, "custom1", "sig", 6),
            });

            Assert.True(PerfModeStore.RemoveCustom("custom2"));

            IReadOnlyList<FirmwareSlotState> slots = PerfModeStore.LoadSlots(2);
            Assert.True(slots[0].IsFree);
            Assert.Equal("custom1", slots[1].OwnerModeId);
            Assert.DoesNotContain("custom2", PerfModeStore.Load().Select(m => m.Id));
        });
    }

    [Fact]
    public void RemovingTheLastCustomModeIsRefused()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            Clean();
            Assert.False(PerfModeStore.RemoveCustom("custom1"));
            Assert.Single(PerfModeStore.Load(), m => m.IsCustom);
        });
    }

    [Fact]
    public void RemovingTheActiveModeFallsBackToBalanced()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            Clean();
            PerfModeStore.AddCustom();
            PerfModeStore.ActiveModeId = "custom2";

            Assert.True(PerfModeStore.RemoveCustom("custom2"));
            Assert.Equal("balanced", PerfModeStore.ActiveModeId);
        });
    }

    [Fact]
    public void NamesAreOptionalTrimmedAndRemovable()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            Clean();
            PerfModeStore.SaveName("custom1", "  游戏  ");
            Assert.Equal("游戏", PerfModeStore.Load().First(m => m.Id == "custom1").UserName);

            PerfModeStore.SaveName("custom1", "   ");
            Assert.Null(PerfModeStore.Load().First(m => m.Id == "custom1").UserName);
        });
    }

    [Fact]
    public void SlotTableRoundTripsAndFreeSlotsLeaveNoKeys()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            Clean();
            PerfModeStore.SaveSlots(new List<FirmwareSlotState>
            {
                new(0, "custom1", "sigA", 11),
                new(1, null, null, 0),
            });

            IReadOnlyList<FirmwareSlotState> slots = PerfModeStore.LoadSlots(2);
            Assert.Equal("custom1", slots[0].OwnerModeId);
            Assert.Equal("sigA", slots[0].Signature);
            Assert.Equal(11, slots[0].LastUsedTicks);
            Assert.True(slots[1].IsFree);
            Assert.False(AppConfig.Exists("perf_slot_owner_1"));
        });
    }

    [Fact]
    public void TheUsageStampFitsInAnIntConfigValue()
    {
        // DateTime.Ticks 会溢出 int 配置项，档位表的 LRU 因此会全部塌成同一个值。
        long stamp = PerfModeStore.NowStamp();
        Assert.InRange(stamp, 1, int.MaxValue);
    }

    [Fact]
    public void ACorruptCustomListFallsBackToASingleCustomMode()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            Clean();
            AppConfig.Set(PerfModeStore.CustomListKey, "abc,,99999,-3");

            IReadOnlyList<PerfModeDefinition> modes = PerfModeStore.Load();
            Assert.Single(modes, m => m.IsCustom);
            Assert.Equal("custom1", modes[^1].Id);
        });
    }
}

/// <summary>模式种类与固件枚举之间的映射，以及功耗墙的 RAPL 判据。</summary>
public class PerfModeMappingTests
{
    [Theory]
    [InlineData(PerfModeKind.Silent, 0)]
    [InlineData(PerfModeKind.Balanced, 1)]
    [InlineData(PerfModeKind.SilentTurbo, 2)]
    [InlineData(PerfModeKind.Turbo, 2)]
    [InlineData(PerfModeKind.Custom, 3)]
    public void KindsMapOntoTheFirmwareOperatingModes(PerfModeKind kind, int expected) =>
        Assert.Equal(expected, PerfModeMapping.ToFirmwareOperatingMode(kind));

    [Fact]
    public void TurboAndSilentTurboShareTheFirmwareSlotButNotTheKind()
    {
        Assert.Equal(PerfModeMapping.ToFirmwareOperatingMode(PerfModeKind.Turbo),
            PerfModeMapping.ToFirmwareOperatingMode(PerfModeKind.SilentTurbo));
        Assert.Equal(PerfModeKind.SilentTurbo, PerfModeMapping.FromFirmware(2, silentTurbo: true));
        Assert.Equal(PerfModeKind.Turbo, PerfModeMapping.FromFirmware(2, silentTurbo: false));
    }

    [Fact]
    public void TheVisualModeMappingMatchesTheServiceEnum()
    {
        Assert.Equal(MechrevoLite.Hardware.MechrevoService.ModeOffice,
            PerfModeMapping.ToServiceMode(PerfModeKind.Silent));
        Assert.Equal(MechrevoLite.Hardware.MechrevoService.ModeGaming,
            PerfModeMapping.ToServiceMode(PerfModeKind.Balanced));
        Assert.Equal(MechrevoLite.Hardware.MechrevoService.ModeTurbo,
            PerfModeMapping.ToServiceMode(PerfModeKind.Turbo));
        Assert.Equal(MechrevoLite.Hardware.MechrevoService.ModeCustom,
            PerfModeMapping.ToServiceMode(PerfModeKind.Custom));
    }

    [Fact]
    public void ALightLoadCannotProveOrDisproveAPowerLimit()
    {
        // 轻载时功耗本来就远低于墙，既不能报成功也不能报失败。
        Assert.Null(PerfModeBackend.EvaluateCpuPowerLimit(80, 12f));
    }

    [Fact]
    public void PowerSittingAtTheLimitProvesTheLimitIsEnforced()
    {
        Assert.True(PerfModeBackend.EvaluateCpuPowerLimit(40, 39f));
        Assert.True(PerfModeBackend.EvaluateCpuPowerLimit(40, 42f));
    }

    [Fact]
    public void PowerWellAboveTheLimitProvesItIsNotEnforced()
    {
        // 真机实测就是这个形状：平衡模式设 40 W，满载实测 82.8 W。
        Assert.False(PerfModeBackend.EvaluateCpuPowerLimit(40, 82.8f));
    }

    [Fact]
    public void MissingMeasurementsAreUnverifiableRatherThanFailures()
    {
        Assert.Null(PerfModeBackend.EvaluateCpuPowerLimit(40, null));
        Assert.Null(PerfModeBackend.EvaluateCpuPowerLimit(0, 50f));
    }
}
