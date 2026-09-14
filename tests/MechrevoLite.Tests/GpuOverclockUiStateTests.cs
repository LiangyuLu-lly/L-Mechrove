using MechrevoLite.Gpu;
using MechrevoLite.Hardware;
using MechrevoLite.UI;
using System.Reflection;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// GPU 超频开关状态回归（run5 UI hardening, item 3）。
///
/// (a) 首次打开时开关与依赖行必须来自同一次硬件快照：开关 OFF 时两个数值行（滑条 + 数值框 +
///     ± 键）都不可拖动。旧实现里 ApplyRange 只按范围可调性设 Enabled，而开关状态在另一处
///     只覆盖滑条本体，数值框仍可拖动 → 「开关 OFF 但数值行可用」。
/// (b) 关闭开关绝不能把用户数值清零：只有开启时才 ApplyRange（关闭时设备把偏移报成 0，
///     套用会把用户刚设的值清零）。
/// </summary>
public class GpuOverclockUiStateTests
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
        public bool SetCoreOffset(int value) => CoreOffset.Contains(value);
        public bool SetMemoryOffset(int value) => MemoryOffset.Contains(value);
        public void ReportCoreOffset(int value) => CoreOffset = CoreOffset with { Current = value };
        public void ReportMemoryOffset(int value) => MemoryOffset = MemoryOffset with { Current = value };
        public void Dispose() { }
    }

    sealed class Scoped(bool previousAudit, MechrevoHw? previousHardware) : IDisposable
    {
        public void Dispose()
        {
            Program.UiAuditMode = previousAudit;
            Program.hw = previousHardware!;
        }
    }

    static IDisposable UseHardware(MechrevoHw hardware)
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = hardware;
        return new Scoped(previousAudit, previousHardware);
    }

    static MechrevoHw NewOcHardware(bool ocSwitch, int coreCurrent = 0, int memCurrent = 0)
    {
        var driver = new FakeOcDriver(-500, 500, -1000, 3000, coreCurrent, memCurrent);
        var hardware = new MechrevoHw((_, _) => Task.CompletedTask,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true },
            driver);
        hardware.HandleMessage("Fan/Status", $$"""
            {"OverclockingSwitch":{{(ocSwitch ? "true" : "false")}},"GpuCoreClockOffset":{{coreCurrent}},"GpuMemoryClockOffset":{{memCurrent}}}
            """);
        return hardware;
    }

    static T Field<T>(CustomModeForm form, string name) =>
        (T)typeof(CustomModeForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    static void InvokeOnCustomChanged(CustomModeForm form) =>
        typeof(CustomModeForm).GetMethod("OnCustomChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, null);

    [Fact]
    public void OcSwitchOff_DisablesSlidersAndNumericInputs_Consistently()
    {
        var driver = new FakeOcDriver(-500, 500, -1000, 3000, 0, 0);
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true },
            driver);
        hardware.HandleMessage("Fan/Status", """{"OverclockingSwitch":false,"GpuCoreClockOffset":0,"GpuMemoryClockOffset":0}""");
        Assert.False(hardware.GpuOverclockEnabled, "前置：超频关闭。");
        Assert.True(hardware.SupportsGpuOverclock, "前置：驱动报了可调范围。");

        using var _ = UseHardware(hardware);
        using var form = new CustomModeForm();
        form.CreateControl();

        Assert.False(Field<RCheckBox>(form, "_ocChk").Checked, "开关必须显示 OFF。");
        Assert.False(Field<RSlider>(form, "_coreOc").Enabled, "开关 OFF 时核心滑条必须不可拖动。");
        Assert.False(Field<RNumericUpDown>(form, "_coreOcVal").Enabled, "开关 OFF 时核心数值框必须不可输入。");
        Assert.False(Field<RSlider>(form, "_memOc").Enabled, "开关 OFF 时显存滑条必须不可拖动。");
        Assert.False(Field<RNumericUpDown>(form, "_memOcVal").Enabled, "开关 OFF 时显存数值框必须不可输入。");
    }

    [Fact]
    public void OcSwitchOn_EnablesDependentsAndShowsTheHardwareValue()
    {
        using var hardware = NewOcHardware(ocSwitch: true, coreCurrent: 250, memCurrent: 600);
        Assert.True(hardware.GpuOverclockEnabled, "前置：超频开启。");

        using var _ = UseHardware(hardware);
        using var form = new CustomModeForm();
        form.CreateControl();

        Assert.True(Field<RCheckBox>(form, "_ocChk").Checked, "开关必须显示 ON。");
        Assert.True(Field<RSlider>(form, "_coreOc").Enabled);
        Assert.True(Field<RNumericUpDown>(form, "_coreOcVal").Enabled);
        Assert.Equal(250, Field<RSlider>(form, "_coreOc").Value);
    }

    [Fact]
    public void TurningTheSwitchOff_PreservesTheUserValues()
    {
        var driver = new FakeOcDriver(-500, 500, -1000, 3000, 0, 0);
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true },
            driver);
        hardware.HandleMessage("Fan/Status", """{"OverclockingSwitch":false,"GpuCoreClockOffset":0,"GpuMemoryClockOffset":0}""");

        using var _ = UseHardware(hardware);
        using var form = new CustomModeForm();
        form.CreateControl();

        var core = Field<RSlider>(form, "_coreOc");
        var coreVal = Field<RNumericUpDown>(form, "_coreOcVal");
        var memory = Field<RSlider>(form, "_memOc");
        core.Value = 120;
        coreVal.Value = 120;
        memory.Value = 300;
        Assert.Equal(120, core.Value);
        Assert.Equal(300, memory.Value);

        // 用户拨到 ON 再拨回 OFF（真实副作用不涉及 OS，这里直接驱动控件状态）。
        var oc = Field<RCheckBox>(form, "_ocChk");
        oc.Checked = true;
        Assert.True(core.Enabled);
        oc.Checked = false;
        Assert.False(core.Enabled);

        // 设备确认关闭：驱动把偏移报回 0，回读完成 → 旧实现会把用户值清零。
        driver.ReportCoreOffset(0);
        driver.ReportMemoryOffset(0);
        hardware.HandleMessage("Fan/Status", """{"OverclockingSwitch":false,"GpuCoreClockOffset":0,"GpuMemoryClockOffset":0}""");
        InvokeOnCustomChanged(form);

        Assert.Equal(120, core.Value);
        Assert.Equal(300, memory.Value);
        Assert.False(core.Enabled);
        Assert.False(coreVal.Enabled);
    }

    [Fact]
    public void ReEnablingTheSwitch_ClampsToTheFreshHardwareRange()
    {
        var driver = new FakeOcDriver(-500, 500, -1000, 3000, 0, 0);
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = true },
            driver);
        hardware.HandleMessage("Fan/Status", """{"OverclockingSwitch":false,"GpuCoreClockOffset":0,"GpuMemoryClockOffset":0}""");

        using var _ = UseHardware(hardware);
        using var form = new CustomModeForm();
        form.CreateControl();

        var core = Field<RSlider>(form, "_coreOc");
        core.Value = 120;
        Field<RNumericUpDown>(form, "_coreOcVal").Value = 120;

        // 开启：设备确认后回读把数值 clamp 到硬件当前值（250）。
        driver.ReportCoreOffset(250);
        hardware.HandleMessage("Fan/Status", """{"OverclockingSwitch":true,"GpuCoreClockOffset":250,"GpuMemoryClockOffset":0}""");
        InvokeOnCustomChanged(form);

        Assert.True(Field<RCheckBox>(form, "_ocChk").Checked);
        Assert.Equal(250, core.Value);
        Assert.True(core.Enabled);
    }
}
