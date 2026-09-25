using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.Hardware;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// 自定义模式页进入：订阅 Fan/Table，再发 GETSTATUS / GET_FAN_SPEED_CURVE_SETTING，
/// 等厂商秒级窗口而不是 100ms。30/40/50 服务不 retain Fan/Table，先发后订会丢帧。
/// </summary>
public class CustomModeFanTableTests
{
    const string FanControl = "Fan/Control";
    const string FanTable = "Fan/Table";
    const string FanTablePayload = """{"Name":"M4T1"}""";

    static string CommandOf(object payload) =>
        payload is IDictionary<string, object> values && values.TryGetValue("Action", out object? action)
            ? action?.ToString() ?? ""
            : "";

    sealed class ProgramScope : IDisposable
    {
        readonly bool _previousAuditMode;
        readonly MechrevoHw? _previousHardware;
        readonly MechrevoService? _previousService;

        ProgramScope(bool previousAuditMode, MechrevoHw? previousHardware, MechrevoService? previousService)
        {
            _previousAuditMode = previousAuditMode;
            _previousHardware = previousHardware;
            _previousService = previousService;
        }

        public static ProgramScope Enter(MechrevoHw hardware, MechrevoService? service = null)
        {
            var scope = new ProgramScope(Program.UiAuditMode, Program.hw, Program.service);
            Program.UiAuditMode = true;
            Program.hw = hardware;
            Program.service = service ?? new MechrevoService(hardware);
            return scope;
        }

        public void Dispose()
        {
            Program.UiAuditMode = _previousAuditMode;
            Program.hw = _previousHardware!;
            Program.service = _previousService!;
        }
    }

    static Label StatusOf(CustomModeForm form) =>
        (Label)typeof(CustomModeForm)
            .GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(form)!;

    static RCheckBox OcChkOf(CustomModeForm form) =>
        (RCheckBox)typeof(CustomModeForm)
            .GetField("_ocChk", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(form)!;

    [Fact]
    public async Task CustomModePageEnter_DoesNotFailWhenFanTableArrivesAfterShortWindow()
    {
        // Given: GCU 在远长于 100ms、仍落在厂商秒级窗口内才回 Fan/Table
        MechrevoHw? hardware = null;
        int curveRequests = 0;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic != FanControl) return Task.CompletedTask;
            string command = CommandOf(payload);
            if (command is "GETSTATUS" or "GET_FAN_SPEED_CURVE_SETTING"
                && Interlocked.Exchange(ref curveRequests, 1) == 0)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1_500);
                    hardware!.HandleMessage(FanTable, FanTablePayload);
                });
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true });

        using (hardware)
        {
            using var _ = ProgramScope.Enter(hardware);
            using var form = new CustomModeForm();

            // When: 打开自定义模式页（拉状态 + 等 Fan/Table）
            bool ready = await form.RefreshHardwareStateAsync()
                .WaitAsync(TimeSpan.FromSeconds(8));

            // Then: 不得只因为超过旧的短超时就失败
            Assert.True(ready, "Fan/Table 在厂商秒级窗口内到达时页进入不得判超时。");
            Assert.True(hardware.FanCurveSeen);
            Assert.True(hardware.FanTableVersion > 0);
        }
    }

    [Fact]
    public async Task CustomModePageEnter_CountsFanTableThatArrivesDuringGetStatus()
    {
        // Given: 30/40/50 不 retain Fan/Table，响应在 GETSTATUS 发布当帧同步到达
        MechrevoHw? hardware = null;
        var published = new List<string>();
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic != FanControl) return Task.CompletedTask;
            string command = CommandOf(payload);
            lock (published) published.Add(command);
            if (command is "GETSTATUS" or "GET_FAN_SPEED_CURVE_SETTING")
                hardware!.HandleMessage(FanTable, FanTablePayload);
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true });

        using (hardware)
        {
            using var _ = ProgramScope.Enter(hardware);
            using var form = new CustomModeForm();

            // When: 先订再发
            bool ready = await form.RefreshHardwareStateAsync()
                .WaitAsync(TimeSpan.FromSeconds(4));

            // Then: 发布中到达的 Fan/Table 必须算数；且必须先发 GETSTATUS / GET_FAN_SPEED_CURVE_SETTING
            Assert.True(ready, "先订阅再 GETSTATUS 时，发布当帧的 Fan/Table 不得丢失。");
            lock (published)
            {
                Assert.Contains("GETSTATUS", published);
                Assert.Contains("GET_FAN_SPEED_CURVE_SETTING", published);
            }
            Assert.True(hardware.FanCurveSeen);
        }
    }

    [Fact]
    public async Task CustomModeOpen_WhenGcuDisconnected_SurfacesReadableErrorWithoutHanging()
    {
        using var hardware = new MechrevoHw((Func<string, object, Task>?)null);
        Assert.False(hardware.IsConnected);

        using var _ = ProgramScope.Enter(hardware);
        using var form = new CustomModeForm();
        Label status = StatusOf(form);

        Task<bool> opening = form.ActivateProfileAsync(0);
        Assert.True(opening.IsCompleted, "GCU 未连接时不得走宽限期空等。");
        Assert.False(await opening);
        Assert.Equal(CustomModeForm.GcuDisconnectedText, status.Text);
    }

    [Fact]
    public void OcCheckbox_DisabledWhenOverclockSettingsDeniedEvenIfRangesWritable()
    {
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, OverclockSettings = false });
        hardware.HandleMessage("Fan/Status",
            """{"GPU_CoreClockOffsetMinimumHWOC":-200,"GPU_CoreClockOffsetMaximumHWOC":200,"GPU_MemoryClockOffsetMinimumHWOC":-200,"GPU_MemoryClockOffsetMaximumHWOC":200}""");
        Assert.True(hardware.GpuCoreOffsetAdjustable || hardware.GpuMemoryOffsetAdjustable,
            "前置：范围可写。");
        Assert.True(hardware.GpuOverclockWritable,
            "前置：GpuOverclockWritable 仍会因范围 OR 为真。");

        using var _ = ProgramScope.Enter(hardware);
        using var form = new CustomModeForm();
        form.CreateControl();

        Assert.False(OcChkOf(form).Enabled,
            "OcSettingsSupport 为 0 时超频开关必须禁用，即使驱动/GCU 报了可写范围。");
    }
}
