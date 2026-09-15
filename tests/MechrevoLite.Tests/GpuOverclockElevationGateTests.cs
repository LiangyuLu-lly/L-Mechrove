using MechrevoLite.Gpu;
using MechrevoLite.Gpu.NVidia;
using MechrevoLite.Hardware;
using MechrevoLite.UI;
using System.Reflection;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// F1 回归（run6）：NVIDIA 直连超频写入需要提权。非提权进程里 NVAPI 的
/// <c>SetPerformanceStates20</c> 恒定返回 <c>NVAPI_INVALID_USER_PRIVILEGE</c>——
/// 能力探测能读到可调范围并不代表能写。契约：
/// <list type="bullet">
/// <item>未提权 + 直连后端存在 ⇒ <see cref="MechrevoHw.GpuOverclockRequiresElevation"/> = true，
///       <see cref="MechrevoHw.GpuOverclockWritable"/> = false（UI 显示「超频需要管理员权限」）；</item>
/// <item>未提权写入不得尝试 NVAPI、不得触发提权助手（无 UAC）、不得把直连后端静默禁用；</item>
/// <item>未提权的启动档位恢复（<see cref="MechrevoHw.RestoreDirectGpuOverclockProfile"/>）必须跳过而不是写失败；</item>
/// <item>已提权 ⇒ 正常写入并回读确认。</item>
/// </list>
/// </summary>
public class GpuOverclockElevationGateTests
{
    /// <summary>模拟真实 NVAPI：写入被拒后把它自己标成不可用（旧行为会静默禁用后端）。</summary>
    sealed class RejectingOcDriver : IGpuOverclockControl
    {
        bool _writeAccessDenied;
        public int CoreWrites { get; private set; }
        public int MemoryWrites { get; private set; }
        public RejectingOcDriver()
        {
            CoreOffset = new GpuClockOffsetRange(-500, 500, 0);
            MemoryOffset = new GpuClockOffsetRange(-1000, 3000, 0);
        }
        public string Name => "Rejecting NVIDIA GPU";
        public bool IsAvailable => !_writeAccessDenied && (CoreOffset.IsAdjustable || MemoryOffset.IsAdjustable);
        // 模拟真实 NVAPI：非提权进程里写入需要提权。
        public bool WritesRequireElevation => true;
        public GpuClockOffsetRange CoreOffset { get; private set; }
        public GpuClockOffsetRange MemoryOffset { get; private set; }
        public bool Refresh() => IsAvailable;
        public bool SetCoreOffset(int value) { CoreWrites++; _writeAccessDenied = true; return false; }
        public bool SetMemoryOffset(int value) { MemoryWrites++; _writeAccessDenied = true; return false; }
        public void Dispose() { }
    }

    sealed class RecordingOcDriver : IGpuOverclockControl
    {
        public int CoreWrites { get; private set; }
        public int MemoryWrites { get; private set; }
        public RecordingOcDriver(bool writesRequireElevation = false)
        {
            WritesRequireElevation = writesRequireElevation;
            CoreOffset = new GpuClockOffsetRange(-500, 500, 0);
            MemoryOffset = new GpuClockOffsetRange(-1000, 3000, 0);
        }
        public string Name => "Recording NVIDIA GPU";
        public bool IsAvailable => CoreOffset.IsAdjustable || MemoryOffset.IsAdjustable;
        public bool WritesRequireElevation { get; }
        public GpuClockOffsetRange CoreOffset { get; private set; }
        public GpuClockOffsetRange MemoryOffset { get; private set; }
        public bool Refresh() => IsAvailable;
        public bool SetCoreOffset(int value) { CoreWrites++; if (!CoreOffset.Contains(value)) return false; CoreOffset = CoreOffset with { Current = value }; return true; }
        public bool SetMemoryOffset(int value) { MemoryWrites++; if (!MemoryOffset.Contains(value)) return false; MemoryOffset = MemoryOffset with { Current = value }; return true; }
        public void Dispose() { }
    }

    sealed class CountingElevatedApplier : IGpuOverclockElevatedApplier
    {
        public int Calls { get; private set; }
        public Task<GpuOverclockApplyResult> ApplyAsync(GpuOverclockApplyRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new GpuOverclockApplyResult(true, request.CoreOffset ?? 0, request.MemoryOffset ?? 0, null));
        }
    }

    static MechrevoDeviceCapabilities Capabilities => new()
    {
        ProfileAvailable = true,
        OverclockSettings = true,
    };

    [Fact]
    public void Unelevated_WithDirectBackend_ReportsNeedsElevationAndNotWritable()
    {
        var driver = new RejectingOcDriver();
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask, Capabilities, driver,
            new CountingElevatedApplier(), () => false);

        Assert.True(hardware.SupportsGpuOverclock, "前置：驱动报了可调范围。");
        Assert.True(hardware.GpuOverclockRequiresElevation, "非提权 + 直连后端 ⇒ 必须报「需要提权」。");
        Assert.False(hardware.GpuOverclockWritable, "非提权 ⇒ 不可写，UI 不得显示可编辑。");
    }

    [Fact]
    public async Task Unelevated_DirectWrite_IsRefusedWithoutWritingOrDisablingBackendOrPromptingElevation()
    {
        var driver = new RejectingOcDriver();
        var elevated = new CountingElevatedApplier();
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask, Capabilities, driver, elevated, () => false);
        var service = new MechrevoService(hardware);

        bool confirmed = await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "100" });

        Assert.False(confirmed, "非提权写入必须诚实失败。");
        Assert.Equal(0, driver.CoreWrites);
        Assert.Equal(0, elevated.Calls);
        Assert.True(hardware.DirectGpuOverclockAvailable, "拒绝不得把直连后端静默禁用（旧行为：NVAPI_INVALID_USER_PRIVILEGE 后禁用本会话后端）。");
    }

    [Fact]
    public async Task Elevated_DirectWrite_AppliesAndConfirmsWithoutTheHelper()
    {
        var driver = new RecordingOcDriver();
        var elevated = new CountingElevatedApplier();
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask, Capabilities, driver, elevated, () => true);
        var service = new MechrevoService(hardware);

        Assert.False(hardware.GpuOverclockRequiresElevation);
        Assert.True(hardware.GpuOverclockWritable);

        bool confirmed = await service.SetCustomDetail(new() { ["GpuCoreClockOffsetOC"] = "100" });

        Assert.True(confirmed, "已提权时直连写入必须生效并回读确认。");
        Assert.True(driver.CoreWrites >= 1);
        Assert.Equal(100, driver.CoreOffset.Current);
        Assert.Equal(0, elevated.Calls);
    }

    [Fact]
    public void Unelevated_RestoreSavedProfile_IsSkippedWithoutWriting()
    {
        var driver = new RecordingOcDriver(writesRequireElevation: true);
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask, Capabilities, () => driver, () => false);
        AppConfig.Set("direct_gpu_oc_enabled_0", 1);
        AppConfig.Set("direct_gpu_oc_core_0", 111);
        AppConfig.Set("direct_gpu_oc_memory_0", 0);

        bool restored = hardware.RestoreDirectGpuOverclockProfile(0);

        Assert.False(restored, "非提权时启动恢复必须跳过，而不是写出 NVAPI_INVALID_USER_PRIVILEGE。");
        Assert.Equal(0, driver.CoreWrites);
    }

    [Fact]
    public void Elevated_RestoreSavedProfile_AppliesTheOffset()
    {
        var driver = new RecordingOcDriver();
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask, Capabilities, () => driver, () => true);
        AppConfig.Set("direct_gpu_oc_enabled_0", 1);
        AppConfig.Set("direct_gpu_oc_core_0", 111);
        AppConfig.Set("direct_gpu_oc_memory_0", 0);

        bool restored = hardware.RestoreDirectGpuOverclockProfile(0);

        Assert.True(restored);
        Assert.Equal(111, driver.CoreOffset.Current);
    }

    // ------------------------------------------------------------ UI 诚实状态

    /// <summary>
    /// RED→GREEN 锚（F1）：非提权时自定义模式的超频开关必须不可编辑，并显示
    /// 「超频需要管理员权限」+「以管理员身份重启」入口——旧实现把开关画成可编辑，
    /// 用户拨上去却写不动（NVAPI_INVALID_USER_PRIVILEGE）。
    /// </summary>
    [Fact]
    public void Unelevated_CustomModeUi_DoesNotClaimOverclockIsEditable_AndOffersRestartAsAdmin()
    {
        var driver = new RejectingOcDriver();
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask, Capabilities, driver,
            new CountingElevatedApplier(), () => false);
        using var _ = UseHardware(hardware);
        using var form = new CustomModeForm();
        form.CreateControl();
        form.Show();
        Application.DoEvents();

        var oc = Field<RCheckBox>(form, "_ocChk");
        var adminRow = Field<FlowLayoutPanel>(form, "_gpuOcAdminRow");

        Assert.False(oc.Enabled, "非提权时超频开关必须不可编辑（不得假装可用）。");
        Assert.False(oc.Checked);
        Assert.True(adminRow.Visible, "非提权时必须显示「超频需要管理员权限」。");
        Assert.NotNull(form.Controls.Find("buttonRestartAsAdmin", true).FirstOrDefault());
    }

    [Fact]
    public void Elevated_CustomModeUi_EnablesOverclock_AndHidesAdminRow()
    {
        var driver = new RecordingOcDriver();
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask, Capabilities, driver,
            new CountingElevatedApplier(), () => true);
        using var _ = UseHardware(hardware);
        using var form = new CustomModeForm();
        form.CreateControl();
        form.Show();
        Application.DoEvents();

        var oc = Field<RCheckBox>(form, "_ocChk");
        var adminRow = Field<FlowLayoutPanel>(form, "_gpuOcAdminRow");

        Assert.True(oc.Enabled, "已提权时超频开关必须可编辑。");
        Assert.False(adminRow.Visible, "已提权时不应再显示「需要管理员权限」。");
    }

    /// <summary>
    /// 「以管理员身份重启」同样受新鲜输入守卫：程序化点击不触发，真实点击才触发。
    /// </summary>
    [Fact]
    public void RestartAsAdmin_RequiresFreshUserInput()
    {
        var driver = new RejectingOcDriver();
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask, Capabilities, driver,
            new CountingElevatedApplier(), () => false);
        using var _ = UseHardware(hardware);
        using var form = new CustomModeForm();
        form.CreateControl();

        var button = form.Controls.Find("buttonRestartAsAdmin", true).OfType<Button>().Single();
        Action? previousOverride = CustomModeForm.RestartAsAdminOverride;
        Func<TimeSpan>? previousIdle = NativeMethods.IdleTimeProvider;
        int calls = 0;
        try
        {
            CustomModeForm.RestartAsAdminOverride = () => calls++;

            NativeMethods.IdleTimeProvider = static () => TimeSpan.FromSeconds(5);
            RaiseClick(button);
            Assert.Equal(0, calls);

            NativeMethods.IdleTimeProvider = static () => TimeSpan.Zero;
            RaiseClick(button);
            Assert.Equal(1, calls);
        }
        finally
        {
            NativeMethods.IdleTimeProvider = previousIdle;
            CustomModeForm.RestartAsAdminOverride = previousOverride;
        }
    }

    static T Field<T>(CustomModeForm form, string name) =>
        (T)typeof(CustomModeForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    static void RaiseClick(Button button) =>
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(button, new object[] { EventArgs.Empty });

    static IDisposable UseHardware(MechrevoHw hardware)
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = hardware;
        return new Scoped(previousAudit, previousHardware);
    }

    sealed class Scoped(bool previousAudit, MechrevoHw? previousHardware) : IDisposable
    {
        public void Dispose()
        {
            Program.UiAuditMode = previousAudit;
            Program.hw = previousHardware!;
        }
    }
}
