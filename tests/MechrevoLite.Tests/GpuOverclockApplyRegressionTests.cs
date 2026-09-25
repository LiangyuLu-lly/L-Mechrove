using MechrevoLite.Gpu;
using MechrevoLite.Hardware;
using MechrevoLite.UI;
using System.Reflection;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// GPU 超频写入路径回归（run5 gpuoc hotfix）。
///
/// 根因：零偏移的开启无法被驱动回读确认（<see cref="MechrevoHw.IsGpuOverclockEnableConfirmed"/>
/// 要求至少一个非零偏移），所以开启命令本身返回 false；<c>OnCustomChanged</c> 随后用
/// 硬件回读覆盖开关 → 下一次状态帧把开关弹回 OFF，<c>SyncGpuOverclockDependents</c> 随之禁用
/// 数值行，用户来不及拨值 → 超频永远不生效、状态永远停在失败文案。
///
/// 这里锁死三件事：
/// (1) 开启后拨值必须到达硬件且状态变为「参数已确认」；
/// (2) 开启后到来的状态回读帧不得把开关弹回或禁用数值行；
/// (3) 关闭开关仍然保留用户已拨的数值（原始需求，不允许回退成清零）。
/// </summary>
public class GpuOverclockApplyRegressionTests
{
    sealed class FakeOcDriver : IGpuOverclockControl
    {
        public FakeOcDriver(int coreMin, int coreMax, int memMin, int memMax, int coreCurrent, int memCurrent)
        {
            CoreOffset = new GpuClockOffsetRange(coreMin, coreMax, coreCurrent);
            MemoryOffset = new GpuClockOffsetRange(memMin, memMax, memCurrent);
        }

        public string Name => "Fake OC";
        public bool IsAvailable => CoreOffset.IsAdjustable || MemoryOffset.IsAdjustable;
        public GpuClockOffsetRange CoreOffset { get; private set; }
        public GpuClockOffsetRange MemoryOffset { get; private set; }
        public bool Refresh() => IsAvailable;
        public bool SetCoreOffset(int value)
        {
            if (!CoreOffset.Contains(value)) return false;
            CoreOffset = CoreOffset with { Current = value };
            return true;
        }

        public bool SetMemoryOffset(int value)
        {
            if (!MemoryOffset.Contains(value)) return false;
            MemoryOffset = MemoryOffset with { Current = value };
            return true;
        }

        /// <summary>GCU/firmware wrote the P-state; NVAPI readback sees it even when Set* would reject the range.</summary>
        public void ReportCoreOffset(int value) => CoreOffset = CoreOffset with { Current = value };
        public void ReportMemoryOffset(int value) => MemoryOffset = MemoryOffset with { Current = value };

        public void Dispose() { }
    }

    sealed class Scoped : IDisposable
    {
        readonly MechrevoHw? _previousHardware;
        readonly MechrevoService? _previousService;

        public Scoped(MechrevoHw hardware)
        {
            _previousHardware = Program.hw;
            _previousService = Program.service;
            Program.hw = hardware;
            Program.service = new MechrevoService(hardware);
        }

        public void Dispose()
        {
            Program.hw = _previousHardware!;
            Program.service = _previousService!;
        }
    }

    static MechrevoHw NewHardware(FakeOcDriver driver)
    {
        var hardware = new MechrevoHw((_, _) => Task.CompletedTask,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true }, driver);
        Frame(hardware, ocSwitch: false, core: 0, memory: 0);
        return hardware;
    }

    static void Frame(MechrevoHw hardware, bool ocSwitch, int core, int memory) =>
        hardware.HandleMessage("Fan/Status", $$"""
            {"OverclockingSwitch":{{(ocSwitch ? "true" : "false")}},"GpuCoreClockOffset":{{core}},"GpuMemoryClockOffset":{{memory}}}
            """);

    static T Field<T>(CustomModeForm form, string name) =>
        (T)typeof(CustomModeForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    static Task Flush(CustomModeForm form) =>
        (Task)typeof(CustomModeForm).GetMethod("FlushPendingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, null)!;

    [Fact]
    public async Task EnablingThenDialing_AppliesToTheHardwareAndConfirmsTheStatus()
    {
        var driver = new FakeOcDriver(-500, 500, -1000, 3000, 0, 0);
        using var hardware = NewHardware(driver);
        using var scope = new Scoped(hardware);
        using var form = new CustomModeForm();
        form.CreateControl();

        // 开启：零偏移的开启本身无法被驱动确认，这是既有的正确行为（见 GpuOverclockFallbackTests）。
        Field<RCheckBox>(form, "_ocChk").Checked = true;
        await Flush(form);

        // 拨值：这是真正把偏移写进硬件的路径。
        var core = Field<RSlider>(form, "_coreOc");
        Assert.True(core.Enabled, "开启后核心数值行必须可用。");
        core.Value = 150;
        await Flush(form);

        Assert.Equal(150, driver.CoreOffset.Current);
        Assert.Equal(150, hardware.EffectiveGpuCoreClockOffset);
        Assert.Equal("参数已确认", Field<Label>(form, "_status").Text);
    }

    [Fact]
    public async Task StatusReadBackAfterEnabling_DoesNotRevertTheSwitchOrDisableTheRows()
    {
        var driver = new FakeOcDriver(-500, 500, -1000, 3000, 0, 0);
        using var hardware = NewHardware(driver);
        using var scope = new Scoped(hardware);
        using var form = new CustomModeForm();
        form.CreateControl();

        var oc = Field<RCheckBox>(form, "_ocChk");
        var core = Field<RSlider>(form, "_coreOc");
        oc.Checked = true;
        await Flush(form);

        // 真机周期性 Fan/Status（以及 GETSTATUS）都会触发 OnCustomChanged；旧实现下这一帧
        // 会把开关弹回 OFF 并禁用数值行，用户再也没机会拨值。
        Frame(hardware, ocSwitch: false, core: 0, memory: 0);

        Assert.True(oc.Checked, "状态回读不得把用户刚打开的开关弹回。");
        Assert.True(core.Enabled, "状态回读不得禁用数值行。");

        // 拨值仍必须到达硬件并确认。
        core.Value = 150;
        await Flush(form);
        Assert.Equal(150, driver.CoreOffset.Current);
        Assert.Equal("参数已确认", Field<Label>(form, "_status").Text);
    }

    [Fact]
    public async Task TurningTheSwitchOff_AfterApplying_PreservesTheUserValues()
    {
        var driver = new FakeOcDriver(-500, 500, -1000, 3000, 0, 0);
        using var hardware = NewHardware(driver);
        using var scope = new Scoped(hardware);
        using var form = new CustomModeForm();
        form.CreateControl();

        var oc = Field<RCheckBox>(form, "_ocChk");
        var core = Field<RSlider>(form, "_coreOc");
        var memory = Field<RSlider>(form, "_memOc");
        oc.Checked = true;
        await Flush(form);
        core.Value = 120;
        await Flush(form);
        memory.Value = 300;
        await Flush(form);
        Assert.Equal(120, driver.CoreOffset.Current);
        Assert.Equal(300, driver.MemoryOffset.Current);

        oc.Checked = false;
        await Flush(form);

        // 关闭：硬件偏移归零，但用户数值必须原样保留（原始需求，不允许回退成清零）。
        Frame(hardware, ocSwitch: false, core: 0, memory: 0);
        Assert.Equal(0, driver.CoreOffset.Current);
        Assert.False(oc.Checked);
        Assert.Equal(120, core.Value);
        Assert.Equal(300, memory.Value);
        Assert.False(core.Enabled);
        Assert.False(memory.Enabled);
    }

    [Fact]
    public void FirstOpen_WithOverclockOff_KeepsTheSwitchAndDependentsConsistent()
    {
        var driver = new FakeOcDriver(-500, 500, -1000, 3000, 0, 0);
        using var hardware = NewHardware(driver);
        using var scope = new Scoped(hardware);
        using var form = new CustomModeForm();
        form.CreateControl();

        Assert.True(hardware.SupportsGpuOverclock, "前置：驱动报了可调范围。");
        Assert.False(Field<RCheckBox>(form, "_ocChk").Checked, "首次打开超频关闭时开关必须是 OFF。");
        Assert.False(Field<RSlider>(form, "_coreOc").Enabled, "开关 OFF 时核心滑条必须禁用。");
        Assert.False(Field<RNumericUpDown>(form, "_coreOcVal").Enabled, "开关 OFF 时核心数值框必须禁用。");
        Assert.False(Field<RSlider>(form, "_memOc").Enabled, "开关 OFF 时显存滑条必须禁用。");
        Assert.False(Field<RNumericUpDown>(form, "_memOcVal").Enabled, "开关 OFF 时显存数值框必须禁用。");
    }

    // ------------------------------------------------------------ GCU-extended persist (T-W0-OC)

    /// <summary>
    /// Characterization: <see cref="MechrevoHw.MarkGcuGpuOverclockActive"/> currently only
    /// nulls <c>_directGpuOverclockEnabled</c>. It does not call
    /// <c>SaveDirectGpuOverclockValue</c>, so a confirmed GCU-extended write never
    /// lands in AppConfig. Desired contract: the same
    /// <c>direct_gpu_oc_{enabled,core,memory}_{slot}</c> keys as the direct NVAPI path.
    /// </summary>
    [Fact]
    public async Task SaveDirectGpuOverclockValue_AlwaysOnGcuExtended()
    {
        ClearDirectGpuOcKeys();
        var driver = new FakeOcDriver(-150, 150, -1000, 3000, 0, 0);
        MechrevoHw? hardware = null;
        hardware = NewPersistingGcuHardware(driver, (topic, payload) =>
        {
            HandleGcuOcPublish(hardware!, driver, topic, payload, resetClocksOnProfileSwitch: false);
            return Task.CompletedTask;
        });

        using (hardware)
        {
            PrimeGcuExtended(hardware);
            var service = new MechrevoService(hardware);

            Assert.True(await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "200" }),
                "前置：200 超出驱动 ±150，必须走 GCU-extended 并被驱动读回确认。");
            Assert.Equal(200, driver.CoreOffset.Current);

            Assert.True(AppConfig.Is("direct_gpu_oc_enabled_0"),
                "GCU-extended 确认后必须把 direct_gpu_oc_enabled_{slot} 写成 1（MarkGcuGpuOverclockActive 今天不写）。");
            Assert.Equal(200, AppConfig.Get("direct_gpu_oc_core_0"));
        }
    }

    /// <summary>
    /// After a custom-slot switch, GCU firmware drops the P-state. Desired:
    /// <see cref="MechrevoService.SwitchCustomProfile"/> reapplies the persisted
    /// offsets for the target slot. Today Restore sees no AppConfig keys because
    /// the GCU-extended path never saved them.
    /// </summary>
    [Fact]
    public async Task SwitchCustomProfile_ReappliesPersistedOffsets()
    {
        ClearDirectGpuOcKeys();
        var driver = new FakeOcDriver(-150, 150, -1000, 3000, 0, 0);
        MechrevoHw? hardware = null;
        hardware = NewPersistingGcuHardware(driver, (topic, payload) =>
        {
            HandleGcuOcPublish(hardware!, driver, topic, payload, resetClocksOnProfileSwitch: true);
            return Task.CompletedTask;
        });

        using (hardware)
        {
            PrimeGcuExtended(hardware);
            var service = new MechrevoService(hardware);

            Assert.True(await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "200" }));
            Assert.Equal(200, driver.CoreOffset.Current);

            Assert.True(await service.SwitchCustomProfile(1), "切到自定义 2 必须确认。");
            Assert.Equal(0, driver.CoreOffset.Current);

            Assert.True(await service.SwitchCustomProfile(0), "切回自定义 1 必须确认。");
            Assert.Equal(200, driver.CoreOffset.Current);
        }
    }

    static MechrevoHw NewPersistingGcuHardware(FakeOcDriver driver, Func<string, object, Task> publish) =>
        new(publish,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true },
            () => driver,
            () => false);

    static void PrimeGcuExtended(MechrevoHw hardware)
    {
        hardware.HandleMessage("Fan/Status",
            "{\"OperatingMode\":3,\"CustomProfileIndex\":0," +
            "\"GPU_CoreClockOffsetMinimumHWOC\":-500,\"GPU_CoreClockOffsetMaximumHWOC\":500," +
            "\"GPU_CoreClockOffsetOC\":0,\"OverClockingSwitch\":0}");
        hardware.HandleMessage("LCHWOC/Status", "{\"Support\":true,\"Enable\":true}");
    }

    static void HandleGcuOcPublish(
        MechrevoHw hardware,
        FakeOcDriver driver,
        string topic,
        object payload,
        bool resetClocksOnProfileSwitch)
    {
        if (topic != "Fan/Control" || payload is not IDictionary<string, object> values) return;
        string action = values.TryGetValue("Action", out object? raw) ? raw?.ToString() ?? "" : "";
        if (action == "SET_OPERATING_MODE_DETAIL" &&
            values.TryGetValue("GpuCoreClockOffsetOC", out object? coreValue) &&
            int.TryParse(coreValue.ToString(), out int core))
        {
            driver.ReportCoreOffset(core);
            int slot = hardware.CustomProfileIndex is >= 0 and <= 3 ? hardware.CustomProfileIndex : 0;
            hardware.HandleMessage("Fan/Status", $$"""
                {"OperatingMode":3,"CustomProfileIndex":{{slot}},
                 "GPU_CoreClockOffsetMinimumHWOC":-500,"GPU_CoreClockOffsetMaximumHWOC":500,
                 "GPU_CoreClockOffsetOC":{{core}},"OverClockingSwitch":1}
                """);
            return;
        }

        if (action == "OPERATING_CUSTOM_MODE")
        {
            int profile = values.TryGetValue("ProfileIndex", out object? profileValue) &&
                          int.TryParse(profileValue?.ToString(), out int parsed)
                ? parsed
                : 0;
            if (resetClocksOnProfileSwitch)
            {
                driver.ReportCoreOffset(0);
                driver.ReportMemoryOffset(0);
            }
            hardware.HandleMessage("Fan/Status", $$"""
                {"OperatingMode":3,"CustomProfileIndex":{{profile}},
                 "GPU_CoreClockOffsetMinimumHWOC":-500,"GPU_CoreClockOffsetMaximumHWOC":500,
                 "GPU_CoreClockOffsetOC":{{driver.CoreOffset.Current}},
                 "OverClockingSwitch":{{(driver.CoreOffset.Current != 0 ? 1 : 0)}}}
                """);
            return;
        }

        if (action == "GETSTATUS")
        {
            int slot = hardware.CustomProfileIndex is >= 0 and <= 3 ? hardware.CustomProfileIndex : 0;
            hardware.HandleMessage("Fan/Status", $$"""
                {"OperatingMode":3,"CustomProfileIndex":{{slot}},
                 "GPU_CoreClockOffsetMinimumHWOC":-500,"GPU_CoreClockOffsetMaximumHWOC":500,
                 "GPU_CoreClockOffsetOC":{{driver.CoreOffset.Current}},
                 "OverClockingSwitch":{{(driver.CoreOffset.Current != 0 ? 1 : 0)}}}
                """);
        }
    }

    static void ClearDirectGpuOcKeys()
    {
        for (int i = 0; i < 4; i++)
        {
            AppConfig.Remove($"direct_gpu_oc_enabled_{i}");
            AppConfig.Remove($"direct_gpu_oc_core_{i}");
            AppConfig.Remove($"direct_gpu_oc_memory_{i}");
        }
    }
}
