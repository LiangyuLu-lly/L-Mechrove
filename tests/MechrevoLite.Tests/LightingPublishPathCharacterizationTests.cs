using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.Hardware;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// 灯效下发路径特征化锁（beta17 计划 §2 Wave D：灯效下发路径收敛）。
///
/// 固件危害：同一灯态重复下发会让设备重新初始化/闪烁——这正是 Program 层一整套静态去重位
/// 存在的原因。本文件把**当前**每个灯效触发点的精确下发序列（topic + payload + 顺序 + 次数）
/// 原样钉死，作为「只改谁调用、不改内容/顺序/次数」收敛时的安全网：序列或次数只要变化，
/// 这里必须先红，收敛的那一组改动就必须回滚。
///
/// 覆盖的触发点：
/// 1) service 灯开关接缝（RgbForm / Settings.V2 的开关都走 <c>SetLightPower</c>）；
/// 2) SettingsForm 灯行开关（用户可见的灯条/Logo 开关）；
/// 3) LightForm 打开（GETSTATUS + 电源 + 存档效果）；
/// 4) Program 空闲熄灯周期；
/// 5) Program 空闲恢复周期（含单周期去重：重复触发不得补发）。
/// </summary>
public class LightingPublishPathCharacterizationTests
{
    const string LightbarTopic = "HidLightbar/Ctrl";
    const string LogoTopic = "HidLightbar_Logo/Ctrl";
    const string KeyboardTopic = "Keyboard/Ctrl";

    /// <summary>把一条下发压成「topic | 关键字段」的稳定描述串，用于精确顺序断言。</summary>
    static string Describe((string Topic, Dictionary<string, object> Payload) entry)
    {
        var parts = new List<string> { entry.Topic };
        foreach (string key in new[] { "function", "Action", "powerstatus", "effect" })
            if (entry.Payload.TryGetValue(key, out object? value)) parts.Add($"{key}={value}");
        return string.Join(" | ", parts);
    }

    static string[] Sequence(IEnumerable<(string Topic, Dictionary<string, object> Payload)> written) =>
        written.Select(Describe).ToArray();

    static int PowerCount(
        IEnumerable<(string Topic, Dictionary<string, object> Payload)> written, string topic, int status) =>
        written.Count(entry =>
            entry.Topic == topic &&
            entry.Payload.TryGetValue("powerstatus", out object? value) &&
            Convert.ToInt32(value) == status);

    static int EffectAllCount(
        IEnumerable<(string Topic, Dictionary<string, object> Payload)> written, string topic) =>
        written.Count(entry =>
            entry.Topic == topic &&
            entry.Payload.TryGetValue("function", out object? function) && Equals(function, "SetEffectALL"));

    static string TempConfigDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    static async Task WaitUntil(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 400 && !condition(); attempt++)
            await Task.Delay(10);
    }

    /// <summary>
    /// 假 GCU：记录每次下发（topic + payload 拷贝），并对灯电源回显状态，
    /// 让 SetLightPower 的确认链路走 180ms 快路径（真机本机键盘长期 not confirmed 是另一条路径）。
    /// </summary>
    sealed class Harness : IDisposable
    {
        public readonly MechrevoHw Hardware;
        public readonly MechrevoService Service;
        public readonly KeyboardRgb Keyboard;

        readonly List<(string Topic, Dictionary<string, object> Payload)> _written = new();
        readonly object _gate = new();
        readonly string _directory;
        readonly string? _previousLightDir;
        readonly MechrevoHw? _previousHardware;
        readonly MechrevoService? _previousService;
        readonly KeyboardRgb? _previousRgb;
        readonly Program.PowerSource? _previousSource;
        readonly bool _previousSuspended;

        public Harness()
        {
            _previousLightDir = Environment.GetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable);
            _directory = TempConfigDirectory();
            Environment.SetEnvironmentVariable(LightingSettingsStore.ConfigDirectoryOverrideVariable, _directory);

            MechrevoHw? hardware = null;
            hardware = new MechrevoHw((topic, payload) =>
            {
                if (payload is IDictionary<string, object> values)
                {
                    var copy = new Dictionary<string, object>(values);
                    lock (_gate) _written.Add((topic, copy));
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
            Service = new MechrevoService(hardware);
            Keyboard = new KeyboardRgb(Path.Combine(_directory, "rgb.cfg")) { KbPowerOn = true };

            _previousHardware = Program.hw;
            _previousService = Program.service;
            _previousRgb = Program.rgb;
            _previousSource = Program.currentSource;
            _previousSuspended = Program.LightingIdleSuspended;

            Program.currentSource = Program.PowerSource.Barrel;   // 不因离电规则意外熄灯
            Program.LightingIdleSuspended = false;
            Program.hw = Hardware;
            Program.service = Service;
            Program.rgb = Keyboard;
        }

        public List<(string Topic, Dictionary<string, object> Payload)> Snapshot()
        {
            lock (_gate) return new List<(string, Dictionary<string, object>)>(_written);
        }

        public List<(string Topic, Dictionary<string, object> Payload)> WrittenTo(params string[] topics)
            => Snapshot().Where(entry => topics.Contains(entry.Topic)).ToList();

        public void Clear()
        {
            lock (_gate) _written.Clear();
        }

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

    /// <summary>
    /// RgbForm / Settings.V2 的开关接缝：一次逻辑开关 = 恰好一条 SetPower，payload 逐字节固定。
    /// </summary>
    [Fact]
    public async Task ServiceChannelPowerSwitch_EmitsExactlyOneSetPowerWithFixedPayload()
    {
        using var harness = new Harness();

        harness.Clear();
        Assert.True(await harness.Service.SetLightPower(LightbarTopic, true));
        Assert.Equal(new[] { "HidLightbar/Ctrl | function=SetPower | powerstatus=1" }, Sequence(harness.Snapshot()));

        harness.Clear();
        Assert.True(await harness.Service.SetLightPower(LightbarTopic, false));
        Assert.Equal(new[] { "HidLightbar/Ctrl | function=SetPower | powerstatus=0" }, Sequence(harness.Snapshot()));

        harness.Clear();
        Assert.True(await harness.Service.SetLightPower(LogoTopic, true));
        Assert.Equal(new[] { "HidLightbar_Logo/Ctrl | function=SetPower | powerstatus=1" }, Sequence(harness.Snapshot()));

        // RgbForm 的键盘电源补开走 SetKeyboardPower（= SetLightPower(Keyboard/Ctrl)）。
        harness.Clear();
        Assert.True(await harness.Service.SetKeyboardPower(true));
        Assert.Equal(new[] { "Keyboard/Ctrl | function=SetPower | powerstatus=1" }, Sequence(harness.Snapshot()));
    }

    /// <summary>
    /// SettingsForm 灯行开电源（用户可见触发点）：先 SetPower，确认后按存档效果补发一次 SetEffectALL。
    /// 顺序必须是电源在前、效果在后；各恰好一次。
    /// </summary>
    [Fact]
    public async Task SettingsLightRowSwitch_PublishesPowerThenSavedEffectInOrder()
    {
        using var harness = new Harness();
        LightingSettingsStore.Save(LightbarTopic,
            new LightChannelSettings("Breathing", 4, 1, Color.White.ToArgb(), PowerOn: false));

        bool previousAudit = Program.UiAuditMode;
        SettingsForm form;
        Program.UiAuditMode = true;
        try
        {
            form = new SettingsForm();
            form.CreateControl();
            form.PerformLayout();
        }
        finally
        {
            Program.UiAuditMode = previousAudit;
        }

        using (form)
        {
            var row = form.Controls.Find("rowLightbar", true).OfType<TableLayoutPanel>().Single();
            var powerSwitch = row.Controls.OfType<RCheckBox>().Single();
            Assert.False(powerSwitch.Checked, "前置：通道关闭时开关应为未勾选。");

            harness.Clear();
            powerSwitch.Checked = true;

            await WaitUntil(() => EffectAllCount(harness.WrittenTo(LightbarTopic), LightbarTopic) >= 1);
            await Task.Delay(250);   // 收敛窗口：确认没有第二次补发

            Assert.Equal(new[]
            {
                "HidLightbar/Ctrl | function=SetPower | powerstatus=1",
                "HidLightbar/Ctrl | function=SetEffectALL | effect=Breathing",
            }, Sequence(harness.WrittenTo(LightbarTopic)));
        }
    }

    /// <summary>
    /// LightForm 打开：先裸查询 GETSTATUS，再 SetLightPower，确认后按存档效果补发一次。
    /// 这是「查询 + 电源 + 效果」的固定顺序。
    /// </summary>
    [Fact]
    public async Task LightFormOpen_PublishesStatusThenPowerThenSavedEffectInOrder()
    {
        using var harness = new Harness();
        LightingSettingsStore.Save(LightbarTopic,
            new LightChannelSettings("Wave", 3, 1, Color.White.ToArgb(), PowerOn: true));

        using var form = new LightForm(LightbarTopic, "灯条灯效", LightForm.LightbarEffects);
        form.CreateControl();
        form.PerformLayout();

        harness.Clear();
        typeof(Form).GetMethod("OnShown", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, new object[] { EventArgs.Empty });

        await WaitUntil(() => harness.Snapshot().Count >= 3);

        Assert.Equal(new[]
        {
            "HidLightbar/Ctrl | Action=GETSTATUS",
            "HidLightbar/Ctrl | function=SetPower | powerstatus=1",
            "HidLightbar/Ctrl | function=SetEffectALL | effect=Wave",
        }, Sequence(harness.Snapshot()));
    }

    /// <summary>
    /// 空闲熄灯：一次到期判定同时熄灭三条通道，顺序固定为「键盘 → 灯带 → Logo」，
    /// 每条恰好一次，且没有任何上电。
    /// </summary>
    [Fact]
    public async Task IdleSuspend_DimsAllThreeChannelsInKeyboardThenExternalOrder()
    {
        using var harness = new Harness();
        harness.Clear();

        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 10_000);
        Assert.True(Program.LightingIdleSuspended, "空闲到期后应进入临时熄灯。");

        var written = harness.Snapshot();
        Assert.Equal(new[]
        {
            "Keyboard/Ctrl | function=SetPower | powerstatus=0",
            "HidLightbar/Ctrl | function=SetPower | powerstatus=0",
            "HidLightbar_Logo/Ctrl | function=SetPower | powerstatus=0",
        }, Sequence(written));
        Assert.Equal(1, PowerCount(written, KeyboardTopic, 0));
        Assert.Equal(1, PowerCount(written, LightbarTopic, 0));
        Assert.Equal(1, PowerCount(written, LogoTopic, 0));
    }

    /// <summary>
    /// 空闲恢复：三条通道各上电一次，顺序固定为「键盘 → 灯带 → Logo」；
    /// 随后各按存档效果补发一次 SetEffectALL（并行，顺序不锁）。
    /// </summary>
    [Fact]
    public async Task IdleRestore_PowersEachChannelOnceThenReplaysEachEffectOnce()
    {
        using var harness = new Harness();
        LightingSettingsStore.Save(LightbarTopic,
            new LightChannelSettings("Wave", 3, 1, Color.White.ToArgb(), PowerOn: true));
        LightingSettingsStore.Save(LogoTopic,
            new LightChannelSettings("Breathing", 4, 1, Color.White.ToArgb(), PowerOn: true));

        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 10_000);   // 熄灯
        harness.Clear();

        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 200);       // 恢复
        Assert.False(Program.LightingIdleSuspended, "检测到输入后应解除临时熄灯。");
        await WaitUntil(() => EffectAllCount(harness.WrittenTo(LogoTopic), LogoTopic) >= 1);

        var powerSequence = harness.Snapshot()
            .Where(entry => entry.Payload.ContainsKey("powerstatus"))
            .Select(Describe).ToArray();
        // 三条上电先于效果重放：键盘 → 灯带 → Logo。
        Assert.Equal(new[]
        {
            "Keyboard/Ctrl | function=SetPower | powerstatus=1",
            "HidLightbar/Ctrl | function=SetPower | powerstatus=1",
            "HidLightbar_Logo/Ctrl | function=SetPower | powerstatus=1",
        }, powerSequence);
        Assert.Equal(1, PowerCount(harness.Snapshot(), KeyboardTopic, 1));
        Assert.Equal(1, EffectAllCount(harness.Snapshot(), LightbarTopic));
        Assert.Equal(1, EffectAllCount(harness.Snapshot(), LogoTopic));
    }

    /// <summary>
    /// 单周期去重（固件危害的直接守卫）：同一熄灯周期内重复触发恢复，
    /// 灯效三条通道都不得再补发任何 SetPower/SetEffectALL。
    /// </summary>
    [Fact]
    public async Task DuplicateRestoreTrigger_AddsNoLightChannelPublishForTheSameCycle()
    {
        using var harness = new Harness();
        LightingSettingsStore.Save(LightbarTopic,
            new LightChannelSettings("Wave", 3, 1, Color.White.ToArgb(), PowerOn: true));
        LightingSettingsStore.Save(LogoTopic,
            new LightChannelSettings("Breathing", 4, 1, Color.White.ToArgb(), PowerOn: true));

        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 10_000);   // 熄灯
        await Program.EvaluateLightingIdleAsync(timeoutSeconds: 10, idleMilliseconds: 200);       // 恢复
        await WaitUntil(() => EffectAllCount(harness.WrittenTo(LogoTopic), LogoTopic) >= 1);

        harness.Clear();
        await Program.ReconcileLightingPowerAsync(forceKeyboardRestore: true);   // 同周期重复触发
        await Task.Delay(300);   // 收敛窗口：确认没有迟到的补发

        var lightChannels = harness.WrittenTo(KeyboardTopic, LightbarTopic, LogoTopic);
        Assert.True(lightChannels.Count == 0,
            "同一熄灯周期的重复恢复不得对灯效通道补发；实际下发：\n  " +
            string.Join("\n  ", lightChannels.Select(Describe)));
    }
}
