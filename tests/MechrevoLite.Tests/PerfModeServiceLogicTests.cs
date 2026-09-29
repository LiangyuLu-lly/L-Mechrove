using MechrevoLite.Mode;

namespace MechrevoLite.Tests;

/// <summary>
/// 编排层的纯逻辑：固件状态 → 用户模式（跟随 / 拉回）、编辑增量、内置模式转自定义档时的种子合成、
/// 旧配置迁移、厂商出厂值解析。
/// </summary>
public class PerfModeServiceLogicTests
{
    static PerfModeDefinition BuiltIn(PerfModeKind kind, PerfModeSettings? s = null) =>
        new(PerfModeDefinition.BuiltInId(kind), kind, null, s ?? PerfModeSettings.Default);

    static PerfModeDefinition Custom(int n, PerfModeSettings? s = null) =>
        new(PerfModeDefinition.CustomId(n), PerfModeKind.Custom, null, s ?? PerfModeSettings.Default);

    static List<PerfModeDefinition> Modes(params PerfModeDefinition[] extra)
    {
        var list = PerfModeCollection.BuiltInOrder.Select(k => BuiltIn(k)).ToList();
        list.Add(Custom(1));
        foreach (PerfModeDefinition m in extra)
        {
            int i = list.FindIndex(x => x.Id == m.Id);
            if (i >= 0) list[i] = m; else list.Add(m);
        }
        return list;
    }

    static IReadOnlyList<FirmwareSlotState> Slots(params (int Index, string? Owner)[] owners) =>
        Enumerable.Range(0, 5)
            .Select(i => new FirmwareSlotState(i, owners.FirstOrDefault(o => o.Index == i).Owner, null, 0))
            .ToList();

    // ---- Resolve ----

    [Theory]
    [InlineData(0, false, "silent")]
    [InlineData(1, false, "balanced")]
    [InlineData(2, false, "turbo")]
    [InlineData(2, true, "silentturbo")]
    public void AnUntouchedBuiltInIsFollowed(int op, bool silentTurbo, string expected)
    {
        PerfModeResolution? r = PerfModeResolver.Resolve(Modes(), Slots(), op, 0, silentTurbo, null);

        Assert.NotNull(r);
        Assert.Equal(expected, r.ModeId);
        Assert.False(r.Redirect);
    }

    [Fact]
    public void FnIntoACustomizedBuiltInIsPulledBackOntoItsSlot()
    {
        // 用户改过「平衡」的功耗墙：按 Fn 进了官方平衡，参数就不是用户的了——必须拉回它的自定义档。
        var modes = Modes(BuiltIn(PerfModeKind.Balanced, new PerfModeSettings { Pl1 = 60 }));

        PerfModeResolution? r = PerfModeResolver.Resolve(modes, Slots(), 1, 0, false, null);

        Assert.Equal("balanced", r!.ModeId);
        Assert.True(r.Redirect);
    }

    [Fact]
    public void ACustomSlotMapsToItsOwner_IncludingAnEmulatedBuiltIn()
    {
        var modes = Modes(BuiltIn(PerfModeKind.Silent, new PerfModeSettings { GpuTgp = 60 }), Custom(2));
        IReadOnlyList<FirmwareSlotState> slots = Slots((0, "custom1"), (1, "silent"), (3, "custom2"));

        Assert.Equal("custom1", PerfModeResolver.Resolve(modes, slots, 3, 0, false, null)!.ModeId);
        Assert.Equal("silent", PerfModeResolver.Resolve(modes, slots, 3, 1, false, null)!.ModeId);
        PerfModeResolution? third = PerfModeResolver.Resolve(modes, slots, 3, 3, false, null);
        Assert.Equal("custom2", third!.ModeId);
        Assert.False(third.Redirect);
    }

    [Fact]
    public void AnUnownedCustomSlotRedirectsToTheLastUsedCustomMode()
    {
        var modes = Modes(Custom(2));
        IReadOnlyList<FirmwareSlotState> slots = Slots((0, "custom1"), (1, "custom2"));

        PerfModeResolution? r = PerfModeResolver.Resolve(modes, slots, 3, 4, false, lastCustomId: "custom2");

        Assert.Equal("custom2", r!.ModeId);
        Assert.True(r.Redirect);
    }

    [Fact]
    public void AnOwnerThatIsNoLongerASlotModeDoesNotClaimTheSlot()
    {
        // 「平衡」恢复出厂后不再占档；档位表里残留的归属不能把固件的自定义档认成平衡。
        IReadOnlyList<FirmwareSlotState> slots = Slots((2, "balanced"));

        PerfModeResolution? r = PerfModeResolver.Resolve(Modes(), slots, 3, 2, false, null);

        Assert.Equal("custom1", r!.ModeId);
        Assert.True(r.Redirect);
    }

    [Fact]
    public void AnUnknownOperatingModeResolvesToNothing() =>
        Assert.Null(PerfModeResolver.Resolve(Modes(), Slots(), -1, 0, false, null));

    // ---- IsFirmwareOn ----

    [Fact]
    public void FirmwareIsOnASlotModeOnlyWhenInCustomModeOnTheOwnedSlot()
    {
        PerfModeDefinition c1 = Custom(1);
        IReadOnlyList<FirmwareSlotState> slots = Slots((2, "custom1"));

        Assert.True(PerfModeResolver.IsFirmwareOn(c1, slots, 3, 2, false, true));
        Assert.False(PerfModeResolver.IsFirmwareOn(c1, slots, 3, 1, false, true));
        Assert.False(PerfModeResolver.IsFirmwareOn(c1, slots, 1, 2, false, true));
    }

    [Fact]
    public void TurboAndSilentTurboAreDistinguishedBySubModeOnlyWhenTheMachineHasIt()
    {
        PerfModeDefinition turbo = BuiltIn(PerfModeKind.Turbo);
        PerfModeDefinition silent = BuiltIn(PerfModeKind.SilentTurbo);

        Assert.True(PerfModeResolver.IsFirmwareOn(turbo, Slots(), 2, 0, silentTurbo: false, turboSubModeSupported: true));
        Assert.False(PerfModeResolver.IsFirmwareOn(turbo, Slots(), 2, 0, silentTurbo: true, turboSubModeSupported: true));
        Assert.True(PerfModeResolver.IsFirmwareOn(silent, Slots(), 2, 0, silentTurbo: true, turboSubModeSupported: true));
        Assert.True(PerfModeResolver.IsFirmwareOn(turbo, Slots(), 2, 0, silentTurbo: true, turboSubModeSupported: false));
    }

    // ---- Delta ----

    [Fact]
    public void TheDeltaContainsOnlyChangedFields()
    {
        var before = new PerfModeSettings { Pl1 = 60, Pl2 = 70, TccOn = true, WindowsPowerMode = 1 };
        var after = before with { Pl2 = 80, WindowsPowerMode = 2 };

        PerfModeSettings d = PerfModeResolver.Delta(before, after);

        Assert.Null(d.Pl1);
        Assert.Equal(80, d.Pl2);
        Assert.Null(d.TccOn);
        Assert.Equal(2, d.WindowsPowerMode);
    }

    [Fact]
    public void AnyGpuOverclockChangeCarriesTheWholeTrio()
    {
        var before = new PerfModeSettings { GpuOverclockOn = true, GpuCoreOffset = 100, GpuMemoryOffset = 400 };
        var after = before with { GpuCoreOffset = 150 };

        PerfModeSettings d = PerfModeResolver.Delta(before, after);

        Assert.True(d.GpuOverclockOn);
        Assert.Equal(150, d.GpuCoreOffset);
        Assert.Equal(400, d.GpuMemoryOffset);
    }

    [Fact]
    public void FanCurveDeltaComparesPointsNotReferences()
    {
        int[] curve = Enumerable.Range(0, 16).Select(i => i * 5).ToArray();
        var before = new PerfModeSettings { CpuFanDuty = curve };
        var same = before with { CpuFanDuty = curve.ToArray() };
        var changed = before with { CpuFanDuty = curve.Select(v => v + 1).ToArray() };

        Assert.Null(PerfModeResolver.Delta(before, same).CpuFanDuty);
        Assert.NotNull(PerfModeResolver.Delta(before, changed).CpuFanDuty);
    }

    // ---- Materialize ----

    [Fact]
    public void MaterializeKeepsUserValuesAndFillsTheRestFromTheVendorThenLive()
    {
        var user = new PerfModeSettings { Pl1 = 40, WindowsPowerMode = 0 };
        var vendor = new PerfModeSettings { Pl1 = 45, Pl2 = 45, Pl4 = 145, GpuTgp = 150 };
        var live = new PerfLiveSnapshot(Pl1: 99, Pl2: 99, GpuDynamicBoost: 25, GpuDynamicBoostOn: true);

        PerfModeSettings m = PerfModeResolver.Materialize(user, vendor, live);

        Assert.Equal(40, m.Pl1);                 // 用户的值永远优先
        Assert.Equal(45, m.Pl2);                 // 厂商出厂值其次
        Assert.Equal(145, m.Pl4);
        Assert.Equal(150, m.GpuTgp);
        Assert.Equal(25, m.GpuDynamicBoost);     // 两者都没有才用运行时回读
        Assert.True(m.GpuDynamicBoostOn);
        Assert.Equal(0, m.WindowsPowerMode);     // 应用侧项原样保留
        Assert.True(m.NeedsFirmwareSlot);
    }

    [Fact]
    public void FillNullsNeverOverridesAUserValue()
    {
        var s = new PerfModeSettings { GpuTgp = 90 };
        var live = new PerfLiveSnapshot(GpuTgp: 150, Pl1: 60);

        PerfModeSettings filled = live.FillNulls(s);

        Assert.Equal(90, filled.GpuTgp);
        Assert.Equal(60, filled.Pl1);
    }

    // ---- 迁移 ----

    static void CleanPerf()
    {
        foreach (string key in AppConfig.Snapshot().Keys
                     .Where(k => k.StartsWith("perf_", StringComparison.Ordinal) || k.StartsWith("mode_tune_", StringComparison.Ordinal))
                     .ToArray())
            AppConfig.Remove(key);
    }

    [Fact]
    public void MigrationCarriesBeta20TuningAndAdoptsTheLastCustomSlotWithoutRewritingIt()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            CleanPerf();
            AppConfig.Set("mode_tune_gaming_power", 2);
            AppConfig.Set("mode_tune_gaming_boost", 4);
            AppConfig.Set("mode_tune_office_fanboost", 1);
            AppConfig.Set("mode_tune_turbo_autooc", 0);
            AppConfig.Set("custom_last_profile", 2);
            AppConfig.Set("custom2_plan", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
            AppConfig.Set("custom2_boost", 3);
            AppConfig.Set("performance_mode", 3);

            PerfModeStore.MigrateLegacy();

            PerfModeSettings balanced = PerfModeStore.LoadSettings("balanced");
            Assert.Equal(2, balanced.WindowsPowerMode);
            Assert.Equal(4, balanced.CpuBoost);
            Assert.True(PerfModeStore.LoadSettings("silent").FanBoost);
            PerfModeSettings turbo = PerfModeStore.LoadSettings("turbo");
            Assert.False(turbo.GpuOverclockOn);
            Assert.Equal(0, turbo.GpuCoreOffset);
            Assert.False(PerfModeService.TurboAutoOcEnabled());

            PerfModeSettings c1 = PerfModeStore.LoadSettings("custom1");
            Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", c1.PowerPlanGuid);
            Assert.Equal(3, c1.CpuBoost);
            // 自定义 1 接管旧版最后用的档，且指纹与它的（空）固件参数一致 → 下次激活不会改写档里原有参数。
            IReadOnlyList<FirmwareSlotState> slots = PerfModeStore.LoadSlots(5);
            Assert.Equal("custom1", slots[2].OwnerModeId);
            Assert.Equal(c1.FirmwareSignature(), slots[2].Signature);
            FirmwareSlotPlan plan = FirmwareSlotPlanner.Plan("custom1", c1.FirmwareSignature(), slots);
            Assert.Equal(2, plan.SlotIndex);
            Assert.False(plan.NeedsParameterWrite);

            Assert.Equal("custom1", PerfModeStore.ActiveModeId);
            Assert.Equal("custom1", PerfModeStore.LastCustomId);
            Assert.False(AppConfig.Exists("mode_tune_gaming_power"));
        });
    }

    [Fact]
    public void MigrationRunsOnlyOnce()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            CleanPerf();
            PerfModeStore.MigrateLegacy();
            AppConfig.Set("mode_tune_gaming_power", 0);

            PerfModeStore.MigrateLegacy();

            Assert.Null(PerfModeStore.LoadSettings("balanced").WindowsPowerMode);
        });
    }

    [Fact]
    public void TheOfficialTurboAutoOverclockStaysOnUntilTheUserTouchesTurboOverclocking()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            CleanPerf();
            Assert.True(PerfModeService.TurboAutoOcEnabled());

            PerfModeStore.SaveSettings("turbo", new PerfModeSettings { GpuCoreOffset = 150 });
            Assert.False(PerfModeService.TurboAutoOcEnabled());
        });
    }

    // ---- 厂商出厂值 ----

    const string OfficeProfile = """
        {"OverClockingEnabled":0,
         "CPU":{"PL1":45,"PL2":45,"PL4":145,"TccOffsetSwitch":0,"TccOffset":25,"AmdSPL":45,"AmdSPPT":45,"AmdFPPT":145,"AmdTccTarget":25},
         "GPU":{"CoreClockOffsetOC":0,"MemoryClockOffsetOC":0,"ConfigurableTGPSwitch":0,"ConfigurableTGPTarget":80,"DynamicBoostSwitch":0,"DynamicBoost":5},
         "FAN":{"TableName":"M2T1","FanSwitchSpeedEnabled":0,"FanSwitchSpeed":300}}
        """;

    static string FanTable(int cpuBase, int gpuBase)
    {
        string Points(int b) => string.Join(",", Enumerable.Range(0, 16)
            .Select(i => $"{{\"ID\":{i},\"UpT\":{40 + i},\"DownT\":{38 + i},\"Duty\":{Math.Min(100, b + i * 5)}}}"));
        return $"{{\"CPU\":[{Points(cpuBase)}],\"GPU\":[{Points(gpuBase)}]}}";
    }

    static VendorModeDefaults.SeedContext Context(bool amd = false) => new(
        Amd: amd, Pl4Scale: 2, TccRawToTarget: raw => 100 - raw,
        TgpAdjustable: true, TgpMaximum: 150, DbAdjustable: true, DbMaximum: 25,
        TccAdjustable: true, GpuOverclockSupported: true, FanSwitchSpeedSupported: true, FanCurveSupported: true);

    [Fact]
    public void SilentSeedsComeFromTheEcAndTheVendorProfile()
    {
        PerfModeSettings s = VendorModeDefaults.Compose(PerfModeKind.Silent, Context(), OfficeProfile, FanTable(20, 25),
            new PlDefaultSet(44, 46, 70, 25));

        Assert.Equal(44, s.Pl1);                  // EC 出厂值优先于档位存档
        Assert.Equal(46, s.Pl2);
        Assert.Equal(140, s.Pl4);                 // EC 里是线上值，折半机型要 ×2
        Assert.False(s.TccOn);
        Assert.Equal(75, s.TccTarget);            // TjMax 100 − 原始偏移 25
        Assert.Equal(150, s.GpuTgp);              // cTGP 开关为 0 的档实际跑满额 TGP
        Assert.False(s.GpuDynamicBoostOn);
        Assert.Equal(5, s.GpuDynamicBoost);
        Assert.False(s.GpuOverclockOn);
        Assert.False(s.FanSwitchSpeedOn);
        Assert.Equal(300, s.FanSwitchSpeedMs);
        Assert.Equal(16, s.CpuFanDuty!.Length);
        Assert.Equal(20, s.CpuFanDuty[0]);
        Assert.Equal(25, s.GpuFanDuty![0]);
        Assert.True(s.NeedsFirmwareSlot);
    }

    [Fact]
    public void TurboSeedsForceDynamicBoostToTheMaximumLikeTheVendor()
    {
        const string turbo = """
            {"OverClockingEnabled":0,"CPU":{"PL1":210,"PL2":210,"PL4":210,"TccOffsetSwitch":0,"TccOffset":7},
             "GPU":{"CoreClockOffsetOC":105,"MemoryClockOffsetOC":500,"ConfigurableTGPSwitch":1,"ConfigurableTGPTarget":150,"DynamicBoostSwitch":0,"DynamicBoost":10},
             "FAN":{"FanSwitchSpeedEnabled":0,"FanSwitchSpeed":300}}
            """;

        PerfModeSettings s = VendorModeDefaults.Compose(PerfModeKind.Turbo, Context(), turbo, null, null);

        Assert.True(s.GpuDynamicBoostOn);
        Assert.Equal(25, s.GpuDynamicBoost);
        Assert.Equal(150, s.GpuTgp);
        // 官方狂暴自动超频的偏移记在狂暴档存档里。
        Assert.True(s.GpuOverclockOn);
        Assert.Equal(105, s.GpuCoreOffset);
        Assert.Equal(500, s.GpuMemoryOffset);
        Assert.Equal(210, s.Pl1);                // 没有 EC 值时退回档位存档
        Assert.Null(s.CpuFanDuty);               // 默认风扇表读不到就留空，由运行时回读兜底
    }

    [Fact]
    public void AmdSeedsUseTheAmdFieldsAndNoPl4Doubling()
    {
        PerfModeSettings s = VendorModeDefaults.Compose(PerfModeKind.Silent, Context(amd: true), OfficeProfile, null, null);

        Assert.Equal(45, s.Pl1);
        Assert.Equal(145, s.Pl4);
        Assert.Equal(25, s.TccTarget);           // AMD 的 AmdTccTarget 本身就是目标温度
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("""{"CPU":[{"Duty":10}]}""")]
    public void BrokenFanTablesYieldNoCurve(string? json) =>
        Assert.Null(VendorModeDefaults.ParseFanDuty(json, cpu: true));

    [Fact]
    public void BrokenProfilesYieldNoProfile()
    {
        Assert.Null(VendorModeDefaults.ParseProfile(null));
        Assert.Null(VendorModeDefaults.ParseProfile("{broken"));
    }

    // ---- 文案 ----

    [Fact]
    public void NamesPreferTheUserNameAndNumberCustomModes()
    {
        Assert.Equal(Properties.Strings.Balanced, PerfModeText.Name(BuiltIn(PerfModeKind.Balanced)));
        Assert.Equal(string.Format(Properties.Strings.CustomProfileN, 3), PerfModeText.Name(Custom(3)));
        Assert.Equal("游戏", PerfModeText.Name(Custom(3) with { UserName = "游戏" }));
        Assert.Equal(string.Format(Properties.Strings.PerfModeCustomized, Properties.Strings.Silent),
            PerfModeText.ComboText(BuiltIn(PerfModeKind.Silent, new PerfModeSettings { Pl1 = 30 })));
    }

    [Fact]
    public void TheSummaryCountsConfirmedSentAndFailedSeparately()
    {
        var outcome = new PerfApplyOutcome("custom1", new[]
        {
            new PerfApplyStepOutcome(new PerfApplyStep(PerfApplyStepKind.SwitchFirmwareSlot), PerfApplyResult.Confirmed),
            new PerfApplyStepOutcome(new PerfApplyStep(PerfApplyStepKind.WriteFirmwareField, "PL1", "60"), PerfApplyResult.Sent),
            new PerfApplyStepOutcome(new PerfApplyStep(PerfApplyStepKind.WriteFirmwareField, "GpuConfigurableTGPTarget", "90"), PerfApplyResult.Failed, "x"),
        });

        Assert.Equal(string.Format(Properties.Strings.PerfModeOutcome, 1, 1, 1), PerfModeText.Summary(outcome));
        Assert.Contains(Properties.Strings.GpuTgp, PerfModeText.FailedItems(outcome, amd: false));
        Assert.DoesNotContain(Properties.Strings.CpuPl1, PerfModeText.FailedItems(outcome, amd: false));
    }
}
