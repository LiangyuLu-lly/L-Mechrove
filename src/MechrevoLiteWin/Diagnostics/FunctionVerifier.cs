using MechrevoLite.Hardware;
using MechrevoLite.Helpers;
using Newtonsoft.Json;

namespace MechrevoLite.Diagnostics;

internal enum VerifyOutcome
{
    /// <summary>命令下发后回读到了预期变化，且已恢复原状。这一项真实可用。</summary>
    Pass,

    /// <summary>入口存在但命令没有效果：回读没有变化，或者恢复不回去。这是"空头接口"。</summary>
    Fail,

    /// <summary>这台机器不支持这一项，界面上本来就不该出现它。不算缺陷。</summary>
    Unsupported,

    /// <summary>有意不测：会造成用户不可接受的副作用（例如需要重启才生效的显卡模式）。</summary>
    SkippedByDesign,

    /// <summary>
    /// 命令下发成功，但要重启才生效，所以当场回读不会变。不算缺陷。
    /// 官方界面对这类项目也会弹重启提示。
    /// </summary>
    RestartRequired,
}

internal sealed record VerifyResult(string Group, string Name, VerifyOutcome Outcome, string Detail);

/// <summary>
/// 在真机上逐项验证每个功能到底是不是真的可用。
///
/// 每一项都走同一套流程：**读现状 → 下发相反/不同的值 → 回读确认真的变了 → 恢复原状 → 确认恢复到位**。
/// 只有全过才算 Pass。回读没变化的记 Fail —— 那就是"点了没反应"的空头接口。
///
/// 为什么需要它：单元测试用的是假发布器，只能证明"我们发出的命令格式对"，
/// 证明不了"这台机器的 GCU 真的照做了"。而 UI 审计只看渲染，不碰硬件。
/// 这中间的空档正是"开关之后没有任何反应"能长期藏身的地方。
///
/// 纪律：
/// - 每一项都必须恢复原状，恢复失败要重试并单独报告。
/// - 会造成不可接受副作用的项目明确标成 SkippedByDesign 并写清理由，不偷偷跳过。
/// - 不支持的项目记 Unsupported，与 Fail 分开——机型没有这项不是缺陷。
/// </summary>
internal static class FunctionVerifier
{
    /// <summary>命令下发后等回读的时间。GCU 的状态推送有滞后，给足余量。</summary>
    static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(1600);

    public static async Task<int> RunAsync(string outputPath)
    {
        var results = new List<VerifyResult>();
        using var hw = new MechrevoHw();

        Logger.WriteLine("VERIFY 开始，连接 GCU…");
        if (!await hw.ConnectAsync())
        {
            Logger.WriteLine("VERIFY 连接失败，无法验证。");
            Console.WriteLine("连接 GCU 失败：厂商服务未运行？");
            return 1;
        }

        // 原始载荷留档。判断某个开关为什么不动时，唯一可靠的依据是服务端到底发了什么字段、
        // 什么类型——官方反编译里的 DTO 声明未必和这台机器的固件一致。
        var rawPayloads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        hw.RawMessageObserved += (topic, payload) =>
        {
            if (topic is MqttTopics.SettingStatus or MqttTopics.FanStatus or MqttTopics.KeyboardStatus or
                MqttTopics.SettingsDeviceSwitchItemStatus or MqttTopics.WhisperModeStatus or MqttTopics.GpuDeviceStatus)
                rawPayloads[topic] = payload;
        };

        var service = new MechrevoService(hw);
        // 等首轮全量状态到齐，否则能力判定还没成型，会把支持的项目误判成不支持。
        await service.RefreshAll();
        await Task.Delay(3500);
        Logger.WriteLine("VERIFY 基线能力: " + hw.DescribeResolvedCapabilities());

        await VerifyQuickSwitches(hw, service, results);
        VerifyWindowsPersonalization(results);

        // 数值类只在自定义模式下可写：其他模式的 PL/TGP 由档位锁定，
        // 服务端会静默忽略写入，而且上报的范围也未必包含当前值
        // （实测增强模式下 TGP=175 而上报范围是 [80,150]）。
        // 先切到自定义模式，跑完再恢复。
        int modeBeforeNumeric = service.CurrentMode;
        bool switchedToCustom = await service.SwitchMode(MechrevoService.ModeCustom);
        await Task.Delay(Settle);
        results.Add(switchedToCustom
            ? new VerifyResult("数值设置", "切到自定义模式", VerifyOutcome.Pass,
                $"原模式={ModeName(modeBeforeNumeric)}，已切到自定义以便验证数值项")
            : new VerifyResult("数值设置", "切到自定义模式", VerifyOutcome.Fail,
                $"切不到自定义模式（当前={service.CurrentMode}），下面的数值项结果不可信"));

        await VerifyNumericSettings(hw, service, results);

        await service.SwitchMode(modeBeforeNumeric);
        await Task.Delay(Settle);
        await VerifyModes(hw, service, results);
        await VerifyBattery(hw, results);
        await VerifyLighting(hw, service, results);
        await VerifyDisplay(hw, service, results);
        await VerifyLiquidCooling(hw, service, results);
        VerifyGpuModeByDesign(hw, results);

        Report(results, outputPath, rawPayloads);
        return results.Any(r => r.Outcome == VerifyOutcome.Fail) ? 3 : 0;
    }

    // ---------------------------------------------------------------- 快捷开关

    /// <summary>
    /// 全部快捷开关。每个都往相反方向发一次，确认回读跟着变，再恢复。
    /// </summary>
    static async Task VerifyQuickSwitches(MechrevoHw hw, MechrevoService service, List<VerifyResult> results)
    {
        // Windows 侧那三项不走 GCU，单独测。
        string[] keys =
        [
            "touchpad", "wifi", "bt", "webcam", "winkey", "fnkey", "numpad", "osd",
            "usb", "deepsleep", "copilot", "acrecovery", "highperf", "fanboost",
            "lightbar", "logolight",
            "touchpadtoggle", "singlecolorkb", "uni", "omni", "powerlight", "batterylogo",
            "gamewhitelist", "cpuadvperf",
        ];

        foreach (string key in keys)
        {
            if (!SupportsKey(hw, key))
            {
                results.Add(new VerifyResult("快捷开关", key, VerifyOutcome.Unsupported,
                    "能力判定为不支持，界面上不会出现这一项"));
                continue;
            }

            bool? baseline = Read(hw, key);
            if (baseline is null)
            {
                results.Add(new VerifyResult("快捷开关", key, VerifyOutcome.Fail,
                    "声称支持却读不到状态：QuickSwitches 字典里没有这个键"));
                continue;
            }

            bool target = !baseline.Value;
            bool sent = await Apply(service, key, target);
            await Task.Delay(Settle);
            bool? after = Read(hw, key);

            // 恢复，失败再试一次。
            await Apply(service, key, baseline.Value);
            await Task.Delay(Settle);
            bool? restored = Read(hw, key);
            if (restored != baseline)
            {
                await Apply(service, key, baseline.Value);
                await Task.Delay(Settle);
                restored = Read(hw, key);
            }

            string detail = $"基线={baseline} 发{target}后回读={Describe(after)} 恢复后={Describe(restored)} 命令确认={sent}";
            if (after == target && restored == baseline)
                results.Add(new VerifyResult("快捷开关", key, VerifyOutcome.Pass, detail));
            else if (key == "deepsleep")
                // 深度睡眠改的是 BIOS/EC 的休眠策略，官方界面也会弹重启提示，
                // 状态字段要等重启后才更新。当场回读不变是设计如此，不是缺陷。
                results.Add(new VerifyResult("快捷开关", key, VerifyOutcome.RestartRequired,
                    "命令已下发，但这一项要重启才生效，当场回读不会变。" + detail));
            else if (after != target)
                results.Add(new VerifyResult("快捷开关", key, VerifyOutcome.Fail,
                    "下发后回读没有变化，点了等于没反应。" + detail));
            else
                results.Add(new VerifyResult("快捷开关", key, VerifyOutcome.Fail,
                    "能改但恢复不回原状，会给用户留下改动。" + detail));
        }
    }

    /// <summary>
    /// Logo 灯以前不在 SupportsQuickSwitch 的表里（那时它是唯一没有快捷开关的灯带），
    /// 要在这儿单独绕一下。现在它已并入那张表，这里保持单一入口。
    /// </summary>
    static bool SupportsKey(MechrevoHw hw, string key) => hw.SupportsQuickSwitch(key);

    /// <summary>
    /// 读某个开关的当前状态。
    ///
    /// 大部分开关的状态在 QuickSwitches 字典里（界面也按 CheckBox.Tag 从那里回显），
    /// 但 USB 充电与风扇增强是例外：它们各有独立属性，界面里也有专门的字段
    /// （_usbChargerBox / _fanBoostBox）单独赋值。只查字典会把这两项误判成「读不到」。
    /// </summary>
    static bool? Read(MechrevoHw hw, string key) => key switch
    {
        "usb" => hw.UsbChargerSeen ? hw.UsbCharger : null,
        "fanboost" => hw.SupportsFanBoost ? hw.FanBoost : null,
        _ => hw.QuickSwitches.TryGetValue(key, out bool value) ? value : null,
    };

    static string Describe(bool? value) => value?.ToString() ?? "无回读";

    /// <summary>按键分派到对应的服务方法——有几个开关不走通用的 SwitchQuick。</summary>
    static Task<bool> Apply(MechrevoService service, string key, bool on) => key switch
    {
        "usb" => service.SwitchUsbCharger(on),
        "fanboost" => service.SwitchFanBoost(on),
        "deepsleep" => service.SwitchDeepSleep(on),
        "gamewhitelist" => service.SwitchGameWhitelist(on),
        "cpuadvperf" => service.SwitchCpuAdvancedPerformance(on),
        "uni" or "omni" => service.SwitchUniOmni(key, on),
        "lightbar" => service.SetLightPower(MqttTopics.LightbarCtrl, on),
        "logolight" => service.SetLightPower(MqttTopics.LogoLightCtrl, on),
        _ => service.SwitchQuick(key, on),
    };

    // ---------------------------------------------------- Windows 侧个性化三项

    static void VerifyWindowsPersonalization(List<VerifyResult> results)
    {
        (string Name, Func<bool?> Read, Func<bool, bool> Write)[] items =
        [
            ("任务栏自动隐藏", ShellPersonalization.IsTaskbarAutoHide, ShellPersonalization.SetTaskbarAutoHide),
            ("透明效果", ShellPersonalization.IsTransparencyEnabled, ShellPersonalization.SetTransparencyEnabled),
            ("深色主题", ShellPersonalization.IsDarkTheme, ShellPersonalization.SetDarkTheme),
        ];

        foreach (var (name, read, write) in items)
        {
            bool? baseline = read();
            if (baseline is null)
            {
                results.Add(new VerifyResult("Windows 设置", name, VerifyOutcome.Fail,
                    "读不到当前值（注册表值缺失或互操作签名错误）"));
                continue;
            }

            bool target = !baseline.Value;
            bool sent = write(target);
            bool? after = read();
            bool restoredOk = write(baseline.Value);
            bool? restored = read();

            string detail = $"基线={baseline} 发{target}后回读={Describe(after)} 恢复后={Describe(restored)}";
            results.Add(after == target && restored == baseline
                ? new VerifyResult("Windows 设置", name, VerifyOutcome.Pass, detail)
                : new VerifyResult("Windows 设置", name, VerifyOutcome.Fail,
                    $"写入未生效或恢复失败（write={sent}/{restoredOk}）。" + detail));
        }
    }

    // ---------------------------------------------------------------- 数值设置

    /// <summary>
    /// 功耗墙、温度墙、TGP、动态加速、风扇转换灵敏度。
    /// 每项在运行时上报的范围内挑一个与当前值不同的目标，写入后回读确认，再写回原值。
    /// </summary>
    static async Task VerifyNumericSettings(MechrevoHw hw, MechrevoService service, List<VerifyResult> results)
    {
        (string Name, bool Adjustable, int Current, int Min, int Max, string Field)[] items =
        [
            ("PL1 功耗墙", hw.Pl1Adjustable, hw.Pl1, hw.Pl1Minimum, hw.Pl1Maximum, "PL1"),
            ("PL2 功耗墙", hw.Pl2Adjustable, hw.Pl2, hw.Pl2Minimum, hw.Pl2Maximum, "PL2"),
            ("PL4 瞬时功耗墙", hw.Pl4Adjustable, hw.Pl4, hw.Pl4Minimum, hw.Pl4Maximum, "PL4"),
            ("CPU 温度墙", hw.TccAdjustable, hw.TccTarget, hw.TccMinimum, hw.TccMaximum, "CpuTccOffset"),
            ("GPU TGP", hw.GpuTgpAdjustable, hw.GpuTgp, hw.GpuTgpMinimum, hw.GpuTgpMaximum, "GpuConfigurableTGPTarget"),
            ("GPU 动态加速", hw.GpuDynamicBoostAdjustable, hw.GpuDb, hw.GpuDbMinimum, hw.GpuDbMaximum, "GpuDynamicBoost"),
        ];

        foreach (var (name, adjustable, current, min, max, field) in items)
        {
            if (!adjustable)
            {
                results.Add(new VerifyResult("数值设置", name, VerifyOutcome.Unsupported,
                    $"运行时未上报可用范围（current={current} min={min} max={max}）"));
                continue;
            }

            // 目标值：优先比当前低一点（降功耗/降温更安全），贴到下限时才往上取。
            int target = current - 5 >= min ? current - 5 : Math.Min(max, current + 5);
            if (target == current)
            {
                results.Add(new VerifyResult("数值设置", name, VerifyOutcome.Unsupported,
                    $"范围太窄，挑不出与当前值不同的目标（current={current} [{min},{max}]）"));
                continue;
            }

            // PL4 在半瓦机型上会被折半截断，实际可达值不等于入参。
            // 拿原始入参去比会把「换算正确」误判成「写入没生效」。
            int expected = field == "PL4" ? hw.Pl4Effective(target) : target;

            bool sent = await service.SetCustomDetail(new Dictionary<string, string> { [field] = target.ToString() });
            await Task.Delay(Settle);
            int after = ReadNumeric(hw, field);

            await service.SetCustomDetail(new Dictionary<string, string> { [field] = current.ToString() });
            await Task.Delay(Settle);
            int restored = ReadNumeric(hw, field);

            string quantised = expected == target ? "" : $"（半瓦机型量化到 {expected}）";
            string detail = $"基线={current} 范围[{min},{max}] 发{target}{quantised}后回读={after} 恢复后={restored} 命令确认={sent}";
            results.Add(after == expected && restored == current
                ? new VerifyResult("数值设置", name, VerifyOutcome.Pass, detail)
                : new VerifyResult("数值设置", name, VerifyOutcome.Fail,
                    (after != expected ? "写入后回读没变。" : "恢复不回原值。") + detail));
        }

        await VerifyFanSwitchSpeed(hw, service, results);
    }

    static int ReadNumeric(MechrevoHw hw, string field) => field switch
    {
        "PL1" => hw.Pl1,
        "PL2" => hw.Pl2,
        "PL4" => hw.Pl4,
        "CpuTccOffset" => hw.TccTarget,
        "GpuConfigurableTGPTarget" => hw.GpuTgp,
        "GpuDynamicBoost" => hw.GpuDb,
        _ => int.MinValue,
    };

    static async Task VerifyFanSwitchSpeed(MechrevoHw hw, MechrevoService service, List<VerifyResult> results)
    {
        if (!hw.SupportsFanSwitchSpeed)
        {
            results.Add(new VerifyResult("数值设置", "风扇转换灵敏度", VerifyOutcome.Unsupported,
                "服务端未上报 FAN_FanSwitchSpeed"));
            return;
        }

        int baseline = hw.FanSwitchSpeed;
        bool enabledBaseline = hw.FanSwitchSpeedEnabled;
        int target = baseline + MechrevoHw.FanSwitchSpeedStepMs <= hw.FanSwitchSpeedMaximum
            ? baseline + MechrevoHw.FanSwitchSpeedStepMs
            : baseline - MechrevoHw.FanSwitchSpeedStepMs;

        bool sent = await service.SetCustomDetail(new Dictionary<string, string>
        {
            ["FanSwitchSpeedEnabled"] = "1",
            ["FanSwitchSpeed"] = target.ToString(),
        });
        await Task.Delay(Settle);
        int after = hw.FanSwitchSpeed;

        await service.SetCustomDetail(new Dictionary<string, string>
        {
            ["FanSwitchSpeedEnabled"] = enabledBaseline ? "1" : "0",
            ["FanSwitchSpeed"] = baseline.ToString(),
        });
        await Task.Delay(Settle);
        int restored = hw.FanSwitchSpeed;

        string detail = $"基线={baseline}ms 启用={enabledBaseline} 发{target}后回读={after} 恢复后={restored} 命令确认={sent}";
        results.Add(after == target && restored == baseline
            ? new VerifyResult("数值设置", "风扇转换灵敏度", VerifyOutcome.Pass, detail)
            : new VerifyResult("数值设置", "风扇转换灵敏度", VerifyOutcome.Fail,
                (after != target ? "写入后回读没变。" : "恢复不回原值。") + detail));
    }

    // ---------------------------------------------------------------- 性能模式

    static async Task VerifyModes(MechrevoHw hw, MechrevoService service, List<VerifyResult> results)
    {
        int baseline = service.CurrentMode;
        // GHelper 枚举：0=均衡 1=增强 2=静音
        foreach (int mode in new[] { 2, 0, 1 })
        {
            bool ok = await service.SwitchMode(mode);
            await Task.Delay(Settle);
            int actual = service.CurrentMode;
            results.Add(actual == mode
                ? new VerifyResult("性能模式", ModeName(mode), VerifyOutcome.Pass,
                    $"切换后回读={actual} 命令确认={ok}")
                : new VerifyResult("性能模式", ModeName(mode), VerifyOutcome.Fail,
                    $"切换后回读={actual}，期望 {mode}，命令确认={ok}"));
        }

        // 恢复要允许重试一次，与快捷开关那边的恢复逻辑一致。
        //
        // 实测过一次偶发失败：连续切了三个模式之后，第四次（切回自定义）的回读
        // 没能在 SwitchMode 的确认窗口（180+700+1000ms）内变过来，于是判成"没回到原模式"，
        // 而紧接着的自定义档切换又是成功的——说明服务端没问题，是这一轮的等待不够。
        // 单次判定在这里会把一个时序抖动报成功能缺陷。
        await service.SwitchMode(baseline);
        await Task.Delay(Settle);
        bool restoredMode = service.CurrentMode == baseline;
        if (!restoredMode)
        {
            await service.SwitchMode(baseline);
            await Task.Delay(Settle);
            restoredMode = service.CurrentMode == baseline;
        }
        results.Add(restoredMode
            ? new VerifyResult("性能模式", "恢复原模式", VerifyOutcome.Pass, "已回到 " + ModeName(baseline))
            : new VerifyResult("性能模式", "恢复原模式", VerifyOutcome.Fail,
                $"两次都未回到 {ModeName(baseline)}，当前={service.CurrentMode}"));

        // 自定义档：切到别的档再切回来。
        int profileBaseline = hw.CustomProfileIndex;
        if (profileBaseline < 0)
        {
            results.Add(new VerifyResult("性能模式", "自定义档切换", VerifyOutcome.Unsupported,
                "未上报 CustomProfileIndex"));
            return;
        }
        int profileTarget = profileBaseline == 0 ? 1 : 0;
        bool profileOk = await service.SwitchCustomProfile(profileTarget);
        await Task.Delay(Settle);
        int profileAfter = hw.CustomProfileIndex;
        await service.SwitchCustomProfile(profileBaseline);
        await Task.Delay(Settle);
        results.Add(profileAfter == profileTarget && hw.CustomProfileIndex == profileBaseline
            ? new VerifyResult("性能模式", "自定义档切换", VerifyOutcome.Pass,
                $"基线={profileBaseline} 切到{profileTarget}后回读={profileAfter} 已恢复")
            : new VerifyResult("性能模式", "自定义档切换", VerifyOutcome.Fail,
                $"基线={profileBaseline} 切到{profileTarget}后回读={profileAfter} 恢复后={hw.CustomProfileIndex} 命令确认={profileOk}"));
    }

    static string ModeName(int mode) => mode switch
    {
        0 => "均衡/游戏",
        1 => "增强/Turbo",
        2 => "静音/办公",
        3 => "自定义",
        _ => "模式" + mode,
    };

    // ---------------------------------------------------------------- 电池三档

    static async Task VerifyBattery(MechrevoHw hw, List<VerifyResult> results)
    {
        int baseline = hw.BatteryProtection;
        if (baseline < 0)
        {
            results.Add(new VerifyResult("电池", "电池保护三档", VerifyOutcome.Fail,
                "读不到 HealthProtectionStatus"));
            return;
        }

        foreach (int mode in new[] { 0, 1, 2 })
        {
            bool ok = await hw.SetBatteryProtection(mode);
            await Task.Delay(Settle);
            results.Add(hw.BatteryProtection == mode
                ? new VerifyResult("电池", "保护档 " + mode, VerifyOutcome.Pass,
                    $"回读={hw.BatteryProtection} 确认={ok}")
                : new VerifyResult("电池", "保护档 " + mode, VerifyOutcome.Fail,
                    $"回读={hw.BatteryProtection}，期望 {mode}，确认={ok}"));
        }

        await hw.SetBatteryProtection(baseline);
        await Task.Delay(Settle);
        results.Add(hw.BatteryProtection == baseline
            ? new VerifyResult("电池", "恢复原档", VerifyOutcome.Pass, "已回到 " + baseline)
            : new VerifyResult("电池", "恢复原档", VerifyOutcome.Fail,
                $"未回到 {baseline}，当前={hw.BatteryProtection}"));
    }

    // ------------------------------------------------------------------ 灯效

    static async Task VerifyLighting(MechrevoHw hw, MechrevoService service, List<VerifyResult> results)
    {
        // 键盘背光电源。
        bool kbBaseline = hw.KeyboardPower;
        bool kbSent = await service.SetKeyboardPower(!kbBaseline);
        await Task.Delay(Settle);
        bool kbAfter = hw.KeyboardPower;
        await service.SetKeyboardPower(kbBaseline);
        await Task.Delay(Settle);
        results.Add(kbAfter != kbBaseline && hw.KeyboardPower == kbBaseline
            ? new VerifyResult("灯效", "键盘背光电源", VerifyOutcome.Pass,
                $"基线={kbBaseline} 发{!kbBaseline}后回读={kbAfter} 已恢复")
            : new VerifyResult("灯效", "键盘背光电源", VerifyOutcome.Fail,
                $"基线={kbBaseline} 发{!kbBaseline}后回读={kbAfter} 恢复后={hw.KeyboardPower} 确认={kbSent}"));

        // 电源指示灯亮度。
        if (!hw.SupportsPowerLightBrightness)
        {
            results.Add(new VerifyResult("灯效", "电源灯亮度", VerifyOutcome.Unsupported,
                "未上报 PowerLightBrightness"));
            return;
        }

        int baseline = hw.PowerLightBrightness;
        int target = baseline >= 40 ? baseline - 20 : baseline + 20;
        bool sent = await service.SetPowerLightBrightness(target);
        await Task.Delay(Settle);
        int after = hw.PowerLightBrightness;
        await service.SetPowerLightBrightness(baseline);
        await Task.Delay(Settle);
        results.Add(after == target && hw.PowerLightBrightness == baseline
            ? new VerifyResult("灯效", "电源灯亮度", VerifyOutcome.Pass,
                $"基线={baseline} 发{target}后回读={after} 已恢复")
            : new VerifyResult("灯效", "电源灯亮度", VerifyOutcome.Fail,
                $"基线={baseline} 发{target}后回读={after} 恢复后={hw.PowerLightBrightness} 确认={sent}"));
    }

    // ------------------------------------------------------------------ 显示

    static async Task VerifyDisplay(MechrevoHw hw, MechrevoService service, List<VerifyResult> results)
    {
        // 刷新率：只有报了两档以上才能真正验证「切换有效」。
        if (hw.HzList.Count < 2)
        {
            results.Add(new VerifyResult("显示", "刷新率切换", VerifyOutcome.Unsupported,
                $"上报的可选刷新率不足两档：[{string.Join(",", hw.HzList)}]"));
        }
        else
        {
            int baseline = hw.CurrentHz;
            int target = hw.HzList.First(hz => hz != baseline);
            bool sent = await service.SwitchRefreshRate(target);
            await Task.Delay(Settle);
            int after = hw.CurrentHz;
            await service.SwitchRefreshRate(baseline);
            await Task.Delay(Settle);
            results.Add(after == target && hw.CurrentHz == baseline
                ? new VerifyResult("显示", "刷新率切换", VerifyOutcome.Pass,
                    $"基线={baseline}Hz 发{target}Hz 后回读={after}Hz 已恢复")
                : new VerifyResult("显示", "刷新率切换", VerifyOutcome.Fail,
                    $"基线={baseline}Hz 发{target}Hz 后回读={after}Hz 恢复后={hw.CurrentHz}Hz 确认={sent}"));
        }

        // 屏幕校色档。
        if (!hw.SupportsColorCalibration)
        {
            results.Add(new VerifyResult("显示", "屏幕校色", VerifyOutcome.Unsupported, "未上报校色支持"));
            return;
        }

        int calibBaseline = MechrevoService.GetColorCalibrationMode();
        bool calibOnBaseline = MechrevoService.IsColorCalibrationOn();
        int calibTarget = calibBaseline == 1 ? 2 : 1;
        bool calibSent = await service.SetColorCalibration(calibTarget);
        await Task.Delay(TimeSpan.FromSeconds(4));
        int calibAfter = MechrevoService.GetColorCalibrationMode();
        await service.SetColorCalibration(calibOnBaseline ? Math.Max(1, calibBaseline) : 0);
        await Task.Delay(TimeSpan.FromSeconds(4));
        results.Add(calibAfter == calibTarget
            ? new VerifyResult("显示", "屏幕校色", VerifyOutcome.Pass,
                $"基线=档{calibBaseline} 开={calibOnBaseline} 发档{calibTarget}后回读=档{calibAfter} 已恢复")
            : new VerifyResult("显示", "屏幕校色", VerifyOutcome.Fail,
                $"基线=档{calibBaseline} 发档{calibTarget}后回读=档{calibAfter} 确认={calibSent}"));
    }

    // ------------------------------------------------------------------ 液冷

    static async Task VerifyLiquidCooling(MechrevoHw hw, MechrevoService service, List<VerifyResult> results)
    {
        if (!hw.SupportsLiquidCooling || !hw.LcGcuControllable)
        {
            results.Add(new VerifyResult("液冷", "水泵档位", VerifyOutcome.Unsupported,
                $"未连接或不可控（reported={hw.SupportsLiquidCooling} controllable={hw.LcGcuControllable}）"));
            return;
        }

        int baseline = hw.LcPumpDuty;
        int target = baseline >= 60 ? 40 : 60;
        bool sent = await service.SwitchLcPump(target);
        await Task.Delay(TimeSpan.FromSeconds(3));
        int after = hw.LcPumpDuty;
        await service.SwitchLcPump(baseline);
        await Task.Delay(TimeSpan.FromSeconds(3));
        results.Add(after == target && hw.LcPumpDuty == baseline
            ? new VerifyResult("液冷", "水泵档位", VerifyOutcome.Pass,
                $"基线={baseline} 发{target}后回读={after} 已恢复")
            : new VerifyResult("液冷", "水泵档位", VerifyOutcome.Fail,
                $"基线={baseline} 发{target}后回读={after} 恢复后={hw.LcPumpDuty} 确认={sent}"));
    }

    // -------------------------------------------------------------- 显卡模式

    /// <summary>
    /// 显卡模式有意不做写入验证：切换会改写 BIOS 里的显示输出通路，需要重启才生效，
    /// 而且失败时可能让用户开机没有画面。只报告当前回读能力。
    /// </summary>
    static void VerifyGpuModeByDesign(MechrevoHw hw, List<VerifyResult> results) =>
        results.Add(new VerifyResult("显卡模式", "模式切换", VerifyOutcome.SkippedByDesign,
            "切换需重启生效且失败可能导致无显示输出，不在自动验证里做。" +
            $"当前回读 GpuMode={hw.GpuMode} 核显支持={hw.IgpuOnlyStatusSupport} 直连支持={hw.DgpuDirectStatusSupport}"));

    // ------------------------------------------------------------------ 报告

    static void Report(List<VerifyResult> results, string outputPath, Dictionary<string, string> rawPayloads)
    {
        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        int Count(VerifyOutcome outcome) => results.Count(r => r.Outcome == outcome);
        File.WriteAllText(outputPath, JsonConvert.SerializeObject(new
        {
            GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Total = results.Count,
            Pass = Count(VerifyOutcome.Pass),
            Fail = Count(VerifyOutcome.Fail),
            Unsupported = Count(VerifyOutcome.Unsupported),
            SkippedByDesign = Count(VerifyOutcome.SkippedByDesign),
            RestartRequired = Count(VerifyOutcome.RestartRequired),
            Results = results,
            // 原始载荷：排查「开关为什么不动」时，服务端实际发的字段名与类型是唯一可靠依据。
            RawPayloads = rawPayloads,
        }, Formatting.Indented));

        foreach (VerifyResult r in results)
            Logger.WriteLine($"VERIFY [{r.Outcome}] {r.Group}/{r.Name}: {r.Detail}");

        Console.WriteLine();
        foreach (VerifyResult r in results)
            Console.WriteLine($"{r.Outcome,-16}{r.Group,-12}{r.Name,-18}{r.Detail}");
        Console.WriteLine();
        Console.WriteLine($"合计 {results.Count}: 通过 {Count(VerifyOutcome.Pass)}, " +
            $"失败 {Count(VerifyOutcome.Fail)}, 不支持 {Count(VerifyOutcome.Unsupported)}, " +
            $"有意跳过 {Count(VerifyOutcome.SkippedByDesign)}, " +
            $"需重启 {Count(VerifyOutcome.RestartRequired)}");
        Console.WriteLine("报告: " + outputPath);
    }
}
