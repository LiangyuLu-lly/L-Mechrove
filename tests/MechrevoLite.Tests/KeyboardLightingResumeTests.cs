using System.Drawing;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// T32（Wave G）happy 路径：休眠唤醒后键盘灯效恢复。
///
/// 缺陷（#4 耀世16u）：resume 链 <c>Program.cs:1469→1474-1488→649</c> 到达恢复入口，但
/// "效果线程 IsAlive / ActiveMode 已等于目标"并不代表灯还亮着——固件唤醒后可能已退出帧模式、
/// 旧线程可能卡在失效句柄上。契约：用户开着灯时**恰好强制重放一次**；用户关着灯时**一次都不重放**。
///
/// 失败路径见 <see cref="KeyboardLightingResumeFailTests"/>。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class KeyboardLightingResumeTests
{
    const string LightbarTopic = "HidLightbar/Ctrl";
    const string LogoTopic = "HidLightbar_Logo/Ctrl";
    const string KeyboardTopic = "Keyboard/Ctrl";

    internal sealed class FakeKeyboardHid : HidDeviceWin
    {
        public override bool SetFeature(byte[] report) => true;
        public override bool Write(byte[] report) => true;
        public override void Dispose() { }
    }

    internal static string TempConfigDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal sealed class Harness : IDisposable
    {
        public readonly MechrevoHw Hardware;
        public readonly List<(string Topic, Dictionary<string, object> Payload)> Written = new();
        public readonly KeyboardRgb Keyboard;
        public readonly FakeKeyboardHid KeyboardHid = new();

        readonly object _gate = new();
        readonly string _directory;
        readonly string? _previousLightDir;
        readonly MechrevoHw? _previousHardware;
        readonly MechrevoService? _previousService;
        readonly KeyboardRgb? _previousRgb;
        readonly Program.PowerSource? _previousSource;
        readonly bool _previousSuspended;

        public Harness(bool kbPowerOn)
        {
            _previousLightDir = Environment.GetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable);
            _directory = TempConfigDirectory();
            Environment.SetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable, _directory);

            LightingSettingsStore.Save(LightbarTopic,
                new LightChannelSettings("Wave", 3, 1, Color.White.ToArgb(), PowerOn: true));
            LightingSettingsStore.Save(LogoTopic,
                new LightChannelSettings("Breathing", 4, 1, Color.White.ToArgb(), PowerOn: true));

            MechrevoHw? hardware = null;
            hardware = new MechrevoHw((topic, payload) =>
            {
                if (payload is IDictionary<string, object> values)
                {
                    var copy = new Dictionary<string, object>(values);
                    lock (_gate) Written.Add((topic, copy));
                    if (copy.TryGetValue("powerstatus", out object? power) &&
                        (topic == LightbarTopic || topic == LogoTopic || topic == KeyboardTopic))
                    {
                        string statusTopic = topic switch
                        {
                            LogoTopic => "HidLightbar_Logo/Status",
                            LightbarTopic => "HidLightbar/Status",
                            _ => "Keyboard/Status",
                        };
                        string state = Convert.ToInt32(power) == 1 ? "On" : "Off";
                        hardware!.HandleMessage(statusTopic,
                            $"{{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"{state}\"}}");
                    }
                }
                return Task.CompletedTask;
            }, new MechrevoDeviceCapabilities { Lightbar = true, LogoLight = true, Keyboard = true });
            hardware.HandleMessage("HidLightbar/Status", "{\"type\":\"MEZone_Lighbar4\"}");
            hardware.HandleMessage("HidLightbar_Logo/Status", "{\"type\":\"MEZone_Lighbar4\"}");
            Hardware = hardware;

            Keyboard = new KeyboardRgb(Path.Combine(_directory, "rgb.cfg"), KeyboardHid) { KbPowerOn = kbPowerOn };

            _previousHardware = Program.hw;
            _previousService = Program.service;
            _previousRgb = Program.rgb;
            _previousSource = Program.currentSource;
            _previousSuspended = Program.LightingIdleSuspended;

            Program.currentSource = Program.PowerSource.Barrel;
            Program.LightingIdleSuspended = false;
            Program.hw = Hardware;
            Program.service = new MechrevoService(Hardware);
            Program.rgb = Keyboard;
        }

        public List<(string Topic, Dictionary<string, object> Payload)> Snapshot()
        {
            lock (_gate) return new List<(string, Dictionary<string, object>)>(Written);
        }

        public int PowerOnCount(string topic) => Snapshot().Count(entry =>
            entry.Topic == topic &&
            entry.Payload.TryGetValue("powerstatus", out object? value) && Convert.ToInt32(value) == 1);

        public void Dispose()
        {
            Program.rgb = _previousRgb!;
            Program.service = _previousService!;
            Program.hw = _previousHardware!;
            Program.lastObservedSource = _previousSource;
            Program.LightingIdleSuspended = _previousSuspended;
            Environment.SetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable, _previousLightDir);
            Keyboard.Dispose();
            Hardware.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    internal static async Task WaitForResumeRestoreAsync()
    {
        for (int i = 0; i < 160 && Program.ResumeKeyboardRestorePending; i++)
            await Task.Delay(50);
        await Task.Delay(50);
    }

    [Fact]
    public void TheResumePolicyRestoresOnlyWhenTheUserLeftTheLightOn()
    {
        Assert.True(KeyboardResumePolicy.ShouldRestoreEffect(true));
        Assert.False(KeyboardResumePolicy.ShouldRestoreEffect(false));
        Assert.Equal(1, KeyboardResumePolicy.EffectRestoreRequests(true));
        Assert.Equal(0, KeyboardResumePolicy.EffectRestoreRequests(false));
    }

    [Fact]
    public void ForcedStartRestartsTheEffectEvenWhenTheThreadLooksAlive()
    {
        using var harness = new Harness(kbPowerOn: true);
        harness.Keyboard.StartMode(harness.Keyboard.KbHidMode);
        int generation = harness.Keyboard.EffectGeneration;
        Assert.Equal(harness.Keyboard.KbHidMode, harness.Keyboard.ActiveMode);

        harness.Keyboard.StartModeForced(harness.Keyboard.KbHidMode);

        Assert.Equal(generation + 1, harness.Keyboard.EffectGeneration);
        Assert.Equal(harness.Keyboard.KbHidMode, harness.Keyboard.ActiveMode);
    }

    [Fact]
    public async Task ResumeRestoresTheEffectExactlyOnceWhenTheUserLeftItOn()
    {
        using var harness = new Harness(kbPowerOn: true);
        harness.Keyboard.StartMode(harness.Keyboard.KbHidMode);
        int generationBefore = harness.Keyboard.EffectGeneration;
        int requestBefore = Program.LightingRestoreRequestId;

        Program.ScheduleKeyboardLightingRestoreAfterResume();
        await WaitForResumeRestoreAsync();

        Assert.Equal(generationBefore + 1, harness.Keyboard.EffectGeneration);
        Assert.Equal(harness.Keyboard.KbHidMode, harness.Keyboard.ActiveMode);
        Assert.Equal(requestBefore + 1, Program.LightingRestoreRequestId);
    }

    [Fact]
    public async Task ResumeWithTheLightOffDoesNotLightItUp()
    {
        using var harness = new Harness(kbPowerOn: false);
        harness.Keyboard.StopCurrentEffect();
        int generationBefore = harness.Keyboard.EffectGeneration;
        int requestBefore = Program.LightingRestoreRequestId;

        Program.ScheduleKeyboardLightingRestoreAfterResume();
        await WaitForResumeRestoreAsync();

        Assert.Equal(generationBefore, harness.Keyboard.EffectGeneration);
        Assert.Equal(-1, harness.Keyboard.ActiveMode);
        Assert.Equal(requestBefore, Program.LightingRestoreRequestId);
        Assert.Equal(0, harness.PowerOnCount(KeyboardTopic));
    }
}
