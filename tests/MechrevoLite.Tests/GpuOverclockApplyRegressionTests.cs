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
}
