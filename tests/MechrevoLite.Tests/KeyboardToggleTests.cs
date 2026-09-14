using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.Hardware;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// 灯光组键盘开关回归：键盘的可见光由应用内 HID 渲染器（KeyboardRgb）掌控，
/// 固件 MQTT 通道只是电源旁路。开关必须同时驱动两条通道，否则会出现
/// 「点了开/关没反应」——HID 帧继续写亮，固件断电被 HID 渲染立刻覆盖。
/// </summary>
public class KeyboardToggleTests
{
    const string KeyboardTopic = "Keyboard/Ctrl";

    static string TempConfigPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "rgb.cfg");
    }

    static bool IsKeyboardPower((string Topic, Dictionary<string, object> Payload) entry, int status) =>
        entry.Topic == KeyboardTopic &&
        entry.Payload.TryGetValue("powerstatus", out object? value) &&
        Convert.ToInt32(value) == status;

    static async Task WaitUntil(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
            await Task.Delay(10);
    }

    [Fact]
    public void KeyboardRow_Readback_FollowsAppHidIntent()
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        MechrevoService? previousService = Program.service;
        KeyboardRgb? previousRgb = Program.rgb;

        string configPath = TempConfigPath();
        string directory = Path.GetDirectoryName(configPath)!;
        var keyboard = new KeyboardRgb(configPath) { KbPowerOn = false };
        try
        {
            Program.UiAuditMode = true;
            Program.hw = null!;
            Program.service = null!;
            Program.rgb = keyboard;

            using var form = new SettingsForm();
            form.CreateControl();
            form.PerformLayout();

            MethodInfo? sync = typeof(SettingsForm).GetMethod("SyncLightRows",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(sync);
            sync!.Invoke(form, null);

            var row = form.Controls.Find("rowKeyboard", true).OfType<TableLayoutPanel>().Single();
            var powerSwitch = row.Controls.OfType<RCheckBox>().Single();

            Assert.False(powerSwitch.Checked,
                "键盘开关回显必须跟随应用内 HID 意图（KbPowerOn=false），不能读固件回读（无设备时默认 true）。");
        }
        finally
        {
            Program.rgb = previousRgb!;
            Program.service = previousService!;
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
            keyboard.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task KeyboardToggle_DrivesHidIntentAndFirmwarePower()
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        MechrevoService? previousService = Program.service;
        KeyboardRgb? previousRgb = Program.rgb;

        string configPath = TempConfigPath();
        string directory = Path.GetDirectoryName(configPath)!;
        var gate = new object();
        var written = new List<(string Topic, Dictionary<string, object> Payload)>();
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (payload is IDictionary<string, object> values)
            {
                var copy = new Dictionary<string, object>(values);
                lock (gate) written.Add((topic, copy));
                // 回显电源状态：固件确认链路（SetLightPower 的 WaitForStateAsync）据此收敛。
                if (topic == KeyboardTopic && copy.TryGetValue("powerstatus", out object? power))
                    hardware!.HandleMessage("Keyboard/Status",
                        $"{{\"powerStatus\":\"{(Convert.ToInt32(power) == 1 ? "On" : "Off")}\"}}");
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { Keyboard = true });

        var keyboard = new KeyboardRgb(configPath) { KbPowerOn = false };
        bool HasPower(int status)
        {
            lock (gate) return written.Any(entry => IsKeyboardPower(entry, status));
        }

        try
        {
            Program.UiAuditMode = true;
            Program.hw = hardware!;
            Program.service = new MechrevoService(hardware!);
            Program.rgb = keyboard;

            using var form = new SettingsForm();
            form.CreateControl();
            form.PerformLayout();
            var row = form.Controls.Find("rowKeyboard", true).OfType<TableLayoutPanel>().Single();
            var powerSwitch = row.Controls.OfType<RCheckBox>().Single();

            // ON：应用内意图置位，且固件通道收到 powerstatus=1（HID 未连时走固件补开）。
            powerSwitch.Checked = true;
            await WaitUntil(() => keyboard.KbPowerOn && HasPower(1));
            Assert.True(keyboard.KbPowerOn, "打开键盘开关后应用内 HID 意图 KbPowerOn 应为 true。");
            Assert.True(HasPower(1), "打开键盘开关应发布 Keyboard/Ctrl powerstatus=1。");

            // OFF：应用内意图复位（HID 渲染停止），且固件通道收到 powerstatus=0。
            powerSwitch.Checked = false;
            await WaitUntil(() => !keyboard.KbPowerOn && HasPower(0));
            Assert.False(keyboard.KbPowerOn, "关闭键盘开关后应用内 HID 意图 KbPowerOn 应为 false。");
            Assert.True(HasPower(0), "关闭键盘开关应发布 Keyboard/Ctrl powerstatus=0。");
        }
        finally
        {
            Program.rgb = previousRgb!;
            Program.service = previousService!;
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
            keyboard.Dispose();
            hardware.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// I1：键盘行处于关态时切换灯效下拉，只能记住选择（KbHidMode），绝不能把键盘点亮、也不得下发固件命令。
    /// 与灯带/Logo 行的 persist-only 规则一致。
    /// </summary>
    [Fact]
    public async Task KeyboardCombo_WhileChannelOff_PersistsWithoutPublishing()
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        MechrevoService? previousService = Program.service;
        KeyboardRgb? previousRgb = Program.rgb;

        string configPath = TempConfigPath();
        string directory = Path.GetDirectoryName(configPath)!;
        var gate = new object();
        var written = new List<(string Topic, Dictionary<string, object> Payload)>();
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (payload is IDictionary<string, object> values)
            {
                var copy = new Dictionary<string, object>(values);
                lock (gate) written.Add((topic, copy));
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { Keyboard = true });

        int target = RgbForm.HidEffects.Length > 1 ? 1 : 0;
        var keyboard = new KeyboardRgb(configPath)
        {
            KbPowerOn = false,
            KbHidMode = RgbForm.HidEffects[0].Mode,
        };

        try
        {
            Program.UiAuditMode = true;
            Program.hw = hardware!;
            Program.service = new MechrevoService(hardware!);
            Program.rgb = keyboard;

            using var form = new SettingsForm();
            form.CreateControl();
            form.PerformLayout();

            MethodInfo? sync = typeof(SettingsForm).GetMethod("SyncLightRows",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(sync);
            sync!.Invoke(form, null);

            var row = form.Controls.Find("rowKeyboard", true).OfType<TableLayoutPanel>().Single();
            var powerSwitch = row.Controls.OfType<RCheckBox>().Single();
            var combo = row.Controls.OfType<ComboBox>().Single();
            Assert.False(powerSwitch.Checked, "前置条件：键盘开关应为关（KbPowerOn=false）。");

            lock (gate) written.Clear();
            Program.UiAuditMode = false;   // 审计门只挡处理器入口，这里要真正走一遍下拉处理器
            combo.SelectedIndex = target;
            await WaitUntil(() => keyboard.KbHidMode == RgbForm.HidEffects[target].Mode);

            Assert.Equal(RgbForm.HidEffects[target].Mode, keyboard.KbHidMode);
            Assert.False(keyboard.KbPowerOn,
                "关态下切换键盘灯效不得把键盘点亮：KbPowerOn 必须保持 false（不能无条件置 true）。");
            lock (gate)
            {
                Assert.DoesNotContain(written, entry => entry.Topic == KeyboardTopic);
            }
        }
        finally
        {
            Program.rgb = previousRgb!;
            Program.service = previousService!;
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
            keyboard.Dispose();
            hardware.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// I2：HID 必须先行出帧，不能被「固件电源旁路」的确认挡住——本机固件键盘通道永不确认
    /// （SetLightPower 最坏 ~2.08s），挡在 StartMode 前面就是开关慢数倍的根因。
    /// </summary>
    [Fact]
    public async Task KeyboardHidOn_StartsHidBeforeTheFirmwarePowerBypassConfirms()
    {
        var bypassGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool hidStarted = false;

        MethodInfo? seam = typeof(SettingsForm).GetMethod(
            "RunHidWhileFirmwarePowerBypassCompletesAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(seam);
        var run = (Task)seam!.Invoke(null, new object?[] { bypassGate.Task, (Action)(() => hidStarted = true) })!;

        Assert.True(hidStarted,
            "StartMode 必须先行：HID 出帧不得等待固件电源旁路的确认（本机永不确认）。");
        Assert.False(run.IsCompleted,
            "固件旁路尚未完成时，等待任务不应提前完成（保持旁路的并发语义）。");

        bypassGate.SetResult(true);
        await run;
    }

    /// <summary>
    /// I3：固件电源落地后必须重申自定义帧模式。SetKeyboardPower(true) 会把控制器踢出 ITE 自定义帧模式，
    /// ON 路径若只在旁路进行中重进一次（I2 的快速出帧），固件命令落地后 HID 帧就被静默忽略——
    /// 用户看到「亮几秒后常暗」。锁定契约：delegate 先立即执行一次（保住快速出帧），
    /// 固件旁路落地后必须再执行一次（重新进自定义帧模式 + 重启效果）。
    /// </summary>
    [Fact]
    public async Task KeyboardHidOn_ReentersCustomFrameModeAfterTheFirmwarePowerBypassLands()
    {
        var bypass = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int hidStarts = 0;

        MethodInfo? seam = typeof(SettingsForm).GetMethod(
            "RunHidWhileFirmwarePowerBypassCompletesAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(seam);
        var run = (Task)seam!.Invoke(null, new object?[] { bypass.Task, (Action)(() => hidStarts++) })!;

        Assert.Equal(1, hidStarts);          // 快速出帧：先执行一次，不等固件
        Assert.False(run.IsCompleted);       // 旁路未落地前 helper 不应完成

        bypass.SetResult(true);
        await run;

        Assert.Equal(2, hidStarts);          // 固件电源落地后重申自定义帧模式
    }

    /// <summary>
    /// I4：固件旁路任务失败（MqttPublishFailedException 等）时，重申自定义帧模式仍必须发生，
    /// 且不得把异常从即发即弃的任务里抛出去。
    /// </summary>
    [Fact]
    public async Task KeyboardHidOn_ReassertsCustomFrameModeEvenWhenTheFirmwarePowerBypassFaults()
    {
        Task bypass = Task.FromException(
            new MqttPublishFailedException("Keyboard/Ctrl", "模拟断线"));
        int hidStarts = 0;

        MethodInfo? seam = typeof(SettingsForm).GetMethod(
            "RunHidWhileFirmwarePowerBypassCompletesAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(seam);
        var run = (Task)seam!.Invoke(null, new object?[] { bypass, (Action)(() => hidStarts++) })!;

        await run;                            // 旁路失败不得从 helper 里抛出

        Assert.Equal(2, hidStarts);           // 先快速出帧一次，故障后重申一次
    }

    /// <summary>
    /// I5：迟到的 OFF 不得停掉后来 ON 启动的效果。OFF 的 StopCurrentEffect 必须按代际守卫：
    /// 仅当代际仍是本次 OFF 的代际时才停止；用户若在 OFF 后马上又开（代际前进），
    /// 迟到的停止会清掉新代际刚启动的帧，必须放弃。
    /// </summary>
    [Fact]
    public void KeyboardHidOff_StaleGenerationDoesNotStopTheEffectStartedByALaterOn()
    {
        MethodInfo? guard = typeof(SettingsForm).GetMethod(
            "StopHidEffectForCurrentGeneration",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(guard);

        int currentGeneration = 2;
        int stops = 0;

        guard!.Invoke(null, new object?[] { 1, (Func<int>)(() => currentGeneration), (Action)(() => stops++) });
        Assert.Equal(0, stops);   // 代际 1 的迟到 OFF 必须放弃停止（当前是代际 2）

        guard.Invoke(null, new object?[] { 2, (Func<int>)(() => currentGeneration), (Action)(() => stops++) });
        Assert.Equal(1, stops);   // 仍处于当前代际的 OFF 正常停止
    }
}
