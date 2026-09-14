// TODO(nullable-migration): 这个文件继承自 g-helper 的 UI/原生互操作代码，尚未完成可空性标注。
// 项目已开启完整的可空性检查（csproj 里的 Nullable=enable），核心硬件层与 Helpers 均已清零；
// 这里显式关闭，是为了让剩余债务可见且局部化，而不是靠项目级 annotations 把它藏起来。
// 迁移某个文件时删掉下面这行（保留注解上下文，只关闭警告），然后把该文件的 CS86xx 告警修干净即可。
#nullable disable warnings
using MechrevoLite.Battery;
using MechrevoLite.Display;
using MechrevoLite.Gpu;
using MechrevoLite.Helpers;
using MechrevoLite.Hardware;
using MechrevoLite.Mode;
using MechrevoLite.Overlay;
using MechrevoLite.UI;
using MechrevoLite.Update;
using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using static NativeMethods;

namespace MechrevoLite
{

    static class Program
    {
        public static NotifyIcon trayIcon;
        public static AsusACPI acpi;
        public static MechrevoLite.Hardware.MechrevoHw hw;
        public static MechrevoLite.Hardware.MechrevoService service;
        public static MechrevoLite.Hardware.KeyboardRgb rgb;
        public static MechrevoLite.Hardware.WaterCoolerBle ble;

        public static SettingsForm settingsForm;

        public static ModeControl modeControl;
        public static GPUModeControl gpuControl;
        //         public static ClamshellModeControl clamshellControl;

        public static ToastForm toast;

        public static HardwareOverlay? hardwareOverlay;

        public static IntPtr unRegPowerNotify, unRegPowerNotifyLid, unRegPowerNotifyEnergy, unRegSuspendResume;
        public static int WM_TASKBARCREATED = 0;

        private static long lastAuto;
        private static readonly object autoLock = new();
        private static long lastTheme;
        private static int _exitStarted;
        private static System.Windows.Forms.Timer? _trayRetryTimer;
        private static bool _showGuideOnFirstRestore;
        private static readonly SemaphoreSlim _hardwareRecoveryLock = new(1, 1);
        private static readonly SemaphoreSlim _keyboardRestoreLock = new(1, 1);
        private static readonly SemaphoreSlim _lightingStateLock = new(1, 1);
        private static readonly LightingRestoreCoordinator _lightingRestoreCoordinator = new();
        private static readonly SemaphoreSlim _officialUiSettleLock = new(1, 1);
        private static int _officialUiSettled;
        private static readonly SemaphoreSlim _telemetryRecoveryLock = new(1, 1);
        private static int _keyboardStatusRecoveryPending;
        private static long _keyboardStatusBaselineVersion;
        private static int _keyboardStatusBaselineBrightness = -1;
        private static string _keyboardStatusBaselineEffect = "";
        // Fn 热键（固件）接管键盘的代际：每次检测推进一代，只有仍处于当前代际的重申才写 HID，
        // 迟到的旧检测不得覆盖更新的状态（与 SettingsForm._kbCmdGen 同一守卫语义）。
        private static int _keyboardFirmwareTakeoverGeneration;
        private static int _resumeKeyboardRestorePending;
        private static System.Threading.Timer? _lightingIdleTimer;
        private static int _lightingIdleSuspended;
        private static int _keyboardPowerTemporarilySuspended;
        // 一次「临时熄灯」请求 = 一次允许重放外置灯效的令牌。空闲恢复/唤醒恢复/连接恢复/
        // 电源握手会相继触发同一个恢复；令牌相同则只应用一次，避免灯带/Logo 反复上电闪烁。
        private static int _lightingRestoreRequestId;
        // 外置灯带/Logo 每条通道各自记账：只有该通道**确认**上电成功才写入本轮令牌。
        // 单条通道确认失败时，重试只补刷这条未确认的通道，已确认的通道不再重复上电（避免闪烁）。
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _lightingExternalRestoreAppliedIds = new(StringComparer.OrdinalIgnoreCase);
        // 键盘本地 HID 效果与外置通道同一套令牌语义：每个恢复周期（requestId）只写一次电源、
        // 只重进一次自定义帧模式。空闲恢复/连接恢复/唤醒恢复/Fn 重申会相继触发同一次恢复，
        // 重复写电源或重进帧模式会让键盘闪烁。GCU 电源回读未确认不阻止本周期落地。
        private static int _keyboardRestoreAppliedRequestId = int.MinValue;
        // 临时熄灯前各条外置灯带的电源态，按控制主题存。过去是两个独立的 bool?，
        // 每加一条灯带就要多一组字符串分支；改成字典后四条通道走同一段代码。
        private static readonly Dictionary<string, bool> _externalPowerBeforeTemporarySuspend = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan TelemetryStaleAfter = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan TelemetryRecoveryCooldown = TimeSpan.FromSeconds(60);
        private static System.Threading.Timer? _telemetryRecoveryTimer;
        private static long _lastTelemetryRecoveryRequest;
        private static int _lastRecoveredConnectionGeneration;
        // SetAutoModes can run before the GCU MQTT session is ready. Keep the
        // selected startup profile until that connection has one chance to apply it.
        private static int _pendingStartupPerformanceMode = -1;

        internal static string NormalizeReleaseLabel(string informationalVersion)
        {
            string version = informationalVersion.Split('+')[0];
            int separator = version.IndexOf('-');
            if (separator < 0) return version;
            string label = version[(separator + 1)..];
            return label.StartsWith("beta.", StringComparison.OrdinalIgnoreCase)
                ? "beta" + label[5..]
                : label;
        }

        /// <summary>程序集信息版本（"0.289.0-beta13"，可能带 +build 元数据）。</summary>
        static string InformationalVersion =>
            Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";

        /// <summary>界面显示与命名用的短标签（"beta13"）。</summary>
        internal static string ReleaseLabel => NormalizeReleaseLabel(InformationalVersion);

        /// <summary>
        /// 送服务端检测的版本串：完整点分 semver（"0.289.0-beta13"，去掉 +build 元数据）。
        /// 服务端只收形如 9.6.2 / v9.6-beta 的版本号，裸标签 "beta13" 会被判格式错误、
        /// update_available 恒为 false；而裸 AssemblyVersion "0.289.0.0" 又会丢掉 beta 后缀。
        /// </summary>
        internal static string NormalizeReleaseVersion(string informationalVersion) =>
            informationalVersion.Split('+')[0];

        internal static string ReleaseVersion => NormalizeReleaseVersion(InformationalVersion);
        internal static bool UiAuditMode { get; set; }
        internal static bool UiAuditUseReportedCapabilities { get; set; }

        
        // The main entry point for the application
        [STAThread]
        public static void Main(string[] args)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 异常路径也要尽力把亮度还回去：息屏期间崩掉会把用户留在黑屏里（best-effort，绝不抛）。
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                TryRestoreScreenAfterFailure();
                Logger.WriteLine("Unhandled: " + e.ExceptionObject);
            };
            TaskScheduler.UnobservedTaskException += (s, e) => { Logger.WriteLine("Unobserved: " + e.Exception); e.SetObserved(); };
            Application.ThreadException += (_, e) =>
            {
                TryRestoreScreenAfterFailure();
                Logger.WriteLine("UI thread exception: " + e.Exception);
            };

            string action = "";
            if (args.Length > 0) action = args[0];

            if (action == "--gpu-oc-helper")
            {
                Environment.ExitCode = MechrevoLite.Gpu.NVidia.ElevatedGpuOverclockApplier.Run(args.Skip(1).ToArray());
                Logger.Close();
                return;
            }

            if (action == "--kill-process")
            {
                Environment.ExitCode = MechrevoLite.Gpu.ElevatedProcessKiller.RunHelper(args.Skip(1).ToArray());
                Logger.Close();
                return;
            }

            // 更新器模式：等主进程退出 → 替换 exe → 重启。跑在临时目录里的自身副本中，
            // 所以目标 exe 不被占用（详见 UpdateInstaller）。
            if (action == "--apply-update")
            {
                Environment.ExitCode = MechrevoLite.Update.UpdateInstaller.RunUpdater(args.Skip(1).ToArray());
                Logger.Close();
                return;
            }

            // 更新链路自测：检测 → 下载 → 校验 → 解压 → 拉起更新器（与界面按钮同一套函数）。
            if (action == "--updatetest")
            {
                Environment.ExitCode = MechrevoLite.Update.UpdateSelfTest.Run(args.Skip(1).ToArray());
                Logger.Close();
                return;
            }

            bool startMinimized = IsStartupLaunch(action);
            _showGuideOnFirstRestore = startMinimized;

            if (action is "--secure-mqtt" or "--secure-mqtt-remove")
            {
                // 厂商 broker 监听 0.0.0.0:13688 且凭据固定。此前程序只在日志里警告，
                // 没有任何处置手段；这两个入口提供实际的入站阻断规则创建与撤销。
                MqttSecurity.OperationResult mqttResult = action == "--secure-mqtt"
                    ? MqttSecurity.TryCreateInboundBlockRule()
                    : MqttSecurity.TryRemoveInboundBlockRule();
                Logger.WriteLine(mqttResult.Message);
                Console.WriteLine(mqttResult.Message);
                Environment.ExitCode = mqttResult.Success ? 0 : 1;
                Logger.Close();
                return;
            }

            if (action is "--official-isolate" or "--official-restore")
            {
                if (!ProcessHelper.IsUserAdministrator())
                {
                    Logger.WriteLine("Official console action requires administrator rights.");
                    Environment.ExitCode = 5;
                    Logger.Close();
                    return;
                }

                OfficialConsoleIsolation.OperationResult result = action == "--official-isolate"
                    ? OfficialConsoleIsolation.Enable()
                    : OfficialConsoleIsolation.Restore();
                Logger.WriteLine(result.Message);
                Environment.ExitCode = result.Success ? 0 : 1;
                Logger.Close();
                return;
            }

            if (action == "--ui-audit")
            {
                UiAuditMode = true;
                string output = args.Length > 1
                    ? Path.GetFullPath(args[1])
                    : Path.Combine(AppContext.BaseDirectory, "ui-audit");
                Environment.ExitCode = UiAuditRunner.Run(output);
                Logger.Close();
                return;
            }

            // 进程内硬件自测默认禁用：它会真实改写模式、曲线、灯效。
            // 这个守卫必须放在 HARDWARE_DIAGNOSTICS 的取反条件里——此前它无条件 return，
            // 使得下面整段 #if HARDWARE_DIAGNOSTICS 代码即便定义了该常量也永远不可达，
            // 编译开关形同虚设。
#if !HARDWARE_DIAGNOSTICS
            if (action is "selftest" or "--selftest" or "rgbtest" or "--rgbtest" or "lctest" or "--lctest" or "customtest" or "--customtest" or "colortest" or "--colortest" or "--verify-functions")
            {
                Logger.WriteLine($"The in-process hardware test command '{action}' is disabled. Rebuild with -p:DefineConstants=HARDWARE_DIAGNOSTICS to enable it, or use the isolated test project with explicit fixtures.");
                Environment.ExitCode = 2;
                Logger.Close();
                return;
            }
#endif

#if HARDWARE_DIAGNOSTICS
            if (action == "--verify-functions")
            {
                // 逐项验证每个功能在这台机器上是不是真的可用：
                // 读现状 → 下发不同的值 → 回读确认真的变了 → 恢复原状。
                // 会真实改写硬件状态（每项都恢复），所以和其他自测一样受编译开关保护。
                string report = args.Length > 1
                    ? Path.GetFullPath(args[1])
                    : Path.Combine(AppContext.BaseDirectory, "verify-functions.json");
                // Main 是同步的（[STAThread] + WinForms 消息循环），这里阻塞等待即可：
                // 这条路径不显示 UI，跑完就退出。
                Environment.ExitCode = MechrevoLite.Diagnostics.FunctionVerifier
                    .RunAsync(report).GetAwaiter().GetResult();
                Logger.Close();
                return;
            }
#endif

#if HARDWARE_DIAGNOSTICS
            if (action == "selftest" || action == "--selftest")
            {
                // 自测模式：自动执行模式切换循环，记录结果后退出（不显示 UI）
                _ = Task.Run(async () =>
                {
                    try
                    {
                    hw = new MechrevoLite.Hardware.MechrevoHw();
                    var ok = await hw.ConnectAsync();
                    Logger.WriteLine($"SELFTEST connected={ok}");
                    if (!ok) { Environment.Exit(1); return; }
                    await Task.Delay(1500);
                    int initOp = hw.OperatingMode;   // 记录初始模式（结束时恢复）
                    Logger.WriteLine($"SELFTEST 初始 opMode={initOp}");
                    int[] modes = { 2, 0, 1 };   // Silent(办公), Balanced(游戏), Turbo(增强)
                    for (int r = 0; r < 3; r++)
                    {
                        Logger.WriteLine($"SELFTEST ========== ROUND {r + 1} ==========");
                        foreach (int m in modes)
                        {
                            Logger.WriteLine($"SELFTEST --- 切模式 {m} ---");
                            await hw.SetMode(m);
                            await Task.Delay(1500);
                            Logger.WriteLine($"SELFTEST 回读: opMode={hw.OperatingMode} GHelperMode={hw.GHelperMode} table={hw.TableName} curve={hw.CurveName} cpuDuty={string.Join(",", hw.CpuCurveDuty.Take(8))}");
                        }
                    }
                    // 保存→读回验证：向当前表写一条阶梯曲线，读回确认
                    Logger.WriteLine("SELFTEST --- 保存曲线验证 ---");
                    var testDuty = new int[] { 0, 40, 45, 50, 55, 60, 70, 80, 0, 0, 0, 0, 0, 0, 0, 0 };
                    await hw.SetFanCurve(0, testDuty);
                    await Task.Delay(2000);
                    await hw.Publish("Fan/Control", new Dictionary<string, object> { ["Action"] = "GET_FAN_SPEED_CURVE_SETTING" });
                    await Task.Delay(1500);
                    Logger.WriteLine($"SELFTEST 保存后读回: table={hw.CurveName} cpuDuty={string.Join(",", hw.CpuCurveDuty.Take(8))}");
                    // ===== 模式切换全链路验证（走 MechrevoService 确认链）=====
                    Logger.WriteLine("SELFTEST === 模式切换全链路 ===");
                    service = new MechrevoLite.Hardware.MechrevoService(hw);
                    int[] svcModes = { 2, 0, 1 };   // 办公, 游戏, 增强
                    foreach (int sm in svcModes)
                    {
                        bool modeOk = await service.SwitchMode(sm);
                        Logger.WriteLine($"SELFTEST 模式 {sm} 切换确认: ok={modeOk} actual={service.CurrentMode}");
                    }

                    // ===== 显卡状态回读验证（不切换——切换会改写 BIOS 设置重启生效，测试不做副作用）=====
                    Logger.WriteLine("SELFTEST === 显卡状态 ===");
                    await hw.Publish("Setting/Control", new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                    await Task.Delay(1500);
                    Logger.WriteLine($"SELFTEST 显卡状态回读: GpuMode={service.CurrentGpuMode} (0=核显 1=标准 2=独显直连 3=自动)");

                    // ===== 电池充电保护三档验证 =====
                    Logger.WriteLine("SELFTEST === 电池三档 ===");
                    foreach (int bp in new[] { 0, 1, 2 })
                    {
                        await hw.SetBatteryProtection(bp);
                        await Task.Delay(2000);
                        await hw.Publish("BatteryProtection/Control", new Dictionary<string, object> { ["Report"] = "GET" });
                        await Task.Delay(1000);
                        Logger.WriteLine($"SELFTEST 电池档 {bp} -> 回读 HealthProtectionStatus={hw.BatteryProtection} ok={hw.BatteryProtection == bp}");
                    }
                    // 电池恢复平衡档
                    await hw.SetBatteryProtection(1);

                    // ===== 新增快捷开关验证（发命令→回读→恢复原状态，不留改动）=====
                    Logger.WriteLine("SELFTEST === 新增快捷开关 ===");
                    await hw.Publish("Setting/Control", new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                    await Task.Delay(1200);
                    var newKeys = new[] { "copilot", "acrecovery", "highperf", "deepsleep" };
                    var bl = newKeys.ToDictionary(k => k, k => hw.QuickSwitches.TryGetValue(k, out bool v) ? (bool?)v : null);
                    Logger.WriteLine($"SELFTEST 基线: {string.Join(", ", bl.Select(kv => kv.Key + "=" + (kv.Value?.ToString() ?? "无回读")))}");
                    async Task<bool?> ReadBack(string k)
                    {
                        await hw.Publish("Setting/Control", new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                        await Task.Delay(1200);
                        return hw.QuickSwitches.TryGetValue(k, out bool nv) ? (bool?)nv : null;
                    }
                    foreach (string k in newKeys)
                    {
                        bool target = bl[k] != true;   // 当前非开→发开；当前开→发关（验证双向命令）
                        Logger.WriteLine($"SELFTEST --- {k} 发 {target} ---");
                        switch (k)
                        {
                            case "deepsleep":
                                await service.SwitchDeepSleep(target);
                                break;
                            default:
                                await service.SwitchQuick(k, target);
                                break;
                        }
                        bool? after = await ReadBack(k);   // 每步独立回读，定位双向命令各自是否生效
                        await service.SwitchQuick(k, !target);
                        bool? restored = await ReadBack(k);
                        if (restored != bl[k])   // 恢复失败则重试一次基线状态
                        {
                            Logger.WriteLine($"SELFTEST {k}: 恢复未达基线({bl[k]}), 重试 {!target}");
                            await service.SwitchQuick(k, bl[k] ?? false);
                            restored = await ReadBack(k);
                        }
                        Logger.WriteLine($"SELFTEST {k}: 发{target}后={after?.ToString() ?? "无回读"} 恢复后={restored?.ToString() ?? "无回读"} DeepSleepTime={hw.DeepSleepTime}");
                    }

                    // ===== 原有快捷开关双向测试（切换→恢复，不留改动）=====
                    Logger.WriteLine("SELFTEST === 原有快捷开关 ===");
                    var oldKeys = new[] { "touchpad", "wifi", "bt", "webcam", "winkey", "fnkey", "osd" };
                    var oldBl = oldKeys.ToDictionary(k => k, k => hw.QuickSwitches.TryGetValue(k, out bool v) ? (bool?)v : null);
                    Logger.WriteLine($"SELFTEST 基线: {string.Join(", ", oldBl.Select(kv => kv.Key + "=" + (kv.Value?.ToString() ?? "无回读")))}");
                    foreach (string k in oldKeys)
                    {
                        bool target = oldBl[k] != true;
                        await service.SwitchQuick(k, target);
                        await Task.Delay(1500);
                        await service.SwitchQuick(k, !target);   // 恢复原状态
                        await Task.Delay(1500);
                        Logger.WriteLine($"SELFTEST 快捷 {k}: 基线={oldBl[k]?.ToString() ?? "-"} 已发{target}并恢复");
                    }

                    // ===== USB 充电 / 风扇增强双向测试 =====
                    bool usbBl = hw.UsbCharger;
                    await service.SwitchUsbCharger(!usbBl);
                    await Task.Delay(1500);
                    await service.SwitchUsbCharger(usbBl);
                    Logger.WriteLine($"SELFTEST USB充电: 基线={usbBl} 已双向测试并恢复");
                    await hw.Publish("Fan/Control", new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                    await Task.Delay(1200);
                    bool fanBoostBl = hw.FanBoost;
                    await service.SwitchFanBoost(!fanBoostBl);
                    await Task.Delay(1500);
                    await service.SwitchFanBoost(fanBoostBl);
                    Logger.WriteLine($"SELFTEST 风扇增强: 基线={fanBoostBl} 已双向测试并恢复");

                    // ===== 刷新率幂等发送 =====
                    if (hw.CurrentHz > 0)
                    {
                        await service.SwitchRefreshRate(hw.CurrentHz);
                        Logger.WriteLine($"SELFTEST 刷新率: 幂等发送 {hw.CurrentHz}Hz OK");
                    }
                    else Logger.WriteLine("SELFTEST 刷新率: CurrentHz=0 跳过");

                    // ===== CloseTimer 回读 =====
                    Logger.WriteLine($"SELFTEST CloseTimer={hw.CloseTimerMinutes} 分钟");

                    // ===== 遥测字段完整性（等推流后检查）=====
                    await Task.Delay(5000);
                    Logger.WriteLine($"SELFTEST 遥测: CpuTemp={hw.CpuTemp} CpuUsage={hw.CpuUsage} GpuTemp={hw.GpuTemp} GpuUsage={hw.GpuUsage} CpuFanDuty={hw.CpuFanDuty} CpuFanRpm={hw.CpuFanRpm} GpuFanDuty={hw.GpuFanDuty} GpuFanRpm={hw.GpuFanRpm} Battery={hw.BatteryPercent} IsAC={hw.IsAC}");

                    // ===== 恢复初始模式 =====
                    int restoreG = initOp switch { 0 => 2, 1 => 0, 2 => 1, _ => -1 };   // opMode→GHelper 枚举
                    if (restoreG >= 0) { await service.SwitchMode(restoreG); Logger.WriteLine($"SELFTEST 已恢复初始模式 opMode={initOp}"); }

                    Logger.WriteLine("SELFTEST done");
                    Environment.Exit(0);
                    }
                    catch (Exception ex)   // 任何异常都记录并退出，避免空消息循环挂死
                    {
                        Logger.WriteLine("SELFTEST FAIL: " + ex);
                        Environment.Exit(2);
                    }
                });
                Application.Run();
                return;
            }

            if (action == "rgbtest" || action == "--rgbtest")
            {
                // 灯效链路自测：枚举 → 连接 → 静态红 2.5s → 清屏退出
                _ = Task.Run(() =>
                {
                    try
                    {
                        Logger.WriteLine("RGBTEST === HID 枚举 (全部) ===");
                        var all = MechrevoLite.Hardware.HidDeviceWin.Enumerate(0);
                        Logger.WriteLine($"RGBTEST 枚举总数={all.Count}");
                        foreach (var d in all)
                            Logger.WriteLine($"RGBTEST device: {d.DeviceInfo} path={d.Path}");
                        Logger.WriteLine("RGBTEST === HID 枚举 (048D) ===");
                        foreach (var d in MechrevoLite.Hardware.HidDeviceWin.Enumerate(0x048D))
                            Logger.WriteLine($"RGBTEST ite: {d.DeviceInfo}");
                        var rgb = new MechrevoLite.Hardware.KeyboardRgb();
                        bool ok = rgb.Connect();
                        Logger.WriteLine($"RGBTEST connect={ok} dev={rgb.DeviceInfo} err={rgb.LastError}");
                        if (ok)
                        {
                            // 三效果各 1.2s 轮播验证（键盘实际点亮，结束时清屏）
                            int[] allModes = { 9, 0, 1 };
                            foreach (int m in allModes)
                            {
                                rgb.StartMode(m);
                                Thread.Sleep(1200);
                                Logger.WriteLine($"RGBTEST 效果 {m} 运行 OK");
                            }
                            rgb.StopCurrentEffect();
                            Logger.WriteLine("RGBTEST 三效果轮播 → blank 清屏完成");
                        }
                        Logger.WriteLine("RGBTEST done");
                        Environment.Exit(0);
                    }
                    catch (Exception ex) { Logger.WriteLine("RGBTEST FAIL: " + ex); Environment.Exit(2); }
                });
                Application.Run();
                return;
            }

            if (action == "lctest" || action == "--lctest")
            {
                // 液冷全链路自测：状态回读 + 泵速/风扇/灯光命令验证（底层闭环）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var lcHw = new MechrevoLite.Hardware.MechrevoHw();
                        bool ok = await lcHw.ConnectAsync();
                        Logger.WriteLine($"LCTEST connected={ok}");
                        if (!ok) { Environment.Exit(1); return; }
                        var lcSvc = new MechrevoLite.Hardware.MechrevoService(lcHw);

                        // 1. 状态回读链路
                        await lcHw.Publish("BT_LC/Control", new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                        await Task.Delay(2500);
                        Logger.WriteLine($"LCTEST 状态: connected={lcHw.LcConnected} pump={lcHw.LcPumpDuty} fan={lcHw.LcFanDuty} fw={lcHw.LcFwVersion} macs=[{string.Join(",", lcHw.LcDeviceMacs)}] curMac={lcHw.LcCurrentMac}");

                        // 2. 泵速三档循环（发档位→回读占空比）
                        for (int i = 0; i <= 2; i++)
                        {
                            await lcSvc.SwitchLcPump(i);
                            await Task.Delay(1800);
                            Logger.WriteLine($"LCTEST 泵速档{i} → 回读 PumpDuty={lcHw.LcPumpDuty}");
                        }

                        // 3. 风扇四档循环
                        for (int i = 0; i <= 3; i++)
                        {
                            await lcSvc.SwitchLcFan(i);
                            await Task.Delay(1800);
                            Logger.WriteLine($"LCTEST 风扇档{i} → 回读 FanDuty={lcHw.LcFanDuty}");
                        }

                        // 4. 灯光效果命令（呼吸→多彩→旋转→彩虹→关闭）
                        foreach (var (name, mode) in new[] { ("呼吸", 1), ("多彩", 2), ("旋转色", 4), ("彩虹", 5) })
                        {
                            await lcSvc.LcLight(mode);
                            await Task.Delay(1500);
                            Logger.WriteLine($"LCTEST 灯光[{name}] 已发送");
                        }
                        await lcSvc.LcLight(0);
                        Logger.WriteLine("LCTEST 灯光[关闭] 已发送");

                        // 5. 最终状态
                        await lcHw.Publish("BT_LC/Control", new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                        await Task.Delay(2500);
                        Logger.WriteLine($"LCTEST 最终状态: connected={lcHw.LcConnected} pump={lcHw.LcPumpDuty} fan={lcHw.LcFanDuty}");
                        Logger.WriteLine($"LCTEST 结论: {(lcHw.LcConnected ? "已连接——参数命令闭环验证完成" : "未连接——命令已发送（服务端接受），连接后参数才作用于设备")}");
                        Logger.WriteLine("LCTEST done");
                        Environment.Exit(0);
                    }
                    catch (Exception ex) { Logger.WriteLine("LCTEST FAIL: " + ex); Environment.Exit(2); }
                });
                Application.Run();
                return;
            }

            if (action == "customtest" || action == "--customtest")
            {
                // 自定义性能模式全链路自测：4 档切换回读 + 参数写回读验证 + 恢复原状态
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var cHw = new MechrevoLite.Hardware.MechrevoHw();
                        bool ok = await cHw.ConnectAsync();
                        Logger.WriteLine($"CUSTOMTEST connected={ok}");
                        if (!ok) { Environment.Exit(1); return; }
                        var cSvc = new MechrevoLite.Hardware.MechrevoService(cHw);
                        await Task.Delay(1500);

                        // 基线：记录原模式与原参数
                        int origMode = cHw.OperatingMode;
                        int origPl1 = cHw.Pl1, origPl2 = cHw.Pl2, origTcc = cHw.TccOffset, origTgp = cHw.GpuTgp, origDb = cHw.GpuDb;
                        int origCpi = cHw.CustomProfileIndex;
                        Logger.WriteLine($"CUSTOMTEST 基线: opMode={origMode} cpi={origCpi} PL1={origPl1} PL2={origPl2} TCC={origTcc} TGP={origTgp} DB={origDb} TjMax={cHw.TjMax}");

                        // 1. 逐档切换（0-3）→ 回读 CustomProfileIndex
                        var validProfiles = new List<int>();
                        for (int i = 0; i <= 3; i++)
                        {
                            await cSvc.SwitchCustomProfile(i);
                            await Task.Delay(2500);
                            Logger.WriteLine($"CUSTOMTEST 档{i}: opMode={cHw.OperatingMode} cpi={cHw.CustomProfileIndex} PL1={cHw.Pl1} PL2={cHw.Pl2} TCC={cHw.TccOffset} TGP={cHw.GpuTgp} DB={cHw.GpuDb}");
                            if (cHw.OperatingMode == 3 && cHw.CustomProfileIndex == i) validProfiles.Add(i);
                        }
                        Logger.WriteLine($"CUSTOMTEST 有效档位: [{string.Join(",", validProfiles)}]");

                        // 2. 参数写回读验证（在最后一个有效档上）：PL1 微调 ±1 后恢复
                        if (validProfiles.Count > 0)
                        {
                            int testIdx = validProfiles[^1];
                            await cSvc.SwitchCustomProfile(testIdx);
                            await Task.Delay(2500);
                            int basePl1 = cHw.Pl1 > 0 ? cHw.Pl1 : 210;
                            int testPl1 = Math.Clamp(basePl1 - 5, 10, 210);
                            await cSvc.SetCustomDetail(new Dictionary<string, string> { ["PL1"] = testPl1.ToString() });
                            await Task.Delay(2500);
                            Logger.WriteLine($"CUSTOMTEST 写 PL1={testPl1} → 回读 PL1={cHw.Pl1} (期望 {testPl1})");
                            await cSvc.SetCustomDetail(new Dictionary<string, string> { ["PL1"] = basePl1.ToString() });
                            await Task.Delay(2500);
                            Logger.WriteLine($"CUSTOMTEST 恢复 PL1={basePl1} → 回读 PL1={cHw.Pl1}");
                            // 关键修正：每次写后必须 GETSTATUS 主动拉状态（服务端对 PL 会自发推送，TCC/TGP/DB 可能不推）
                            async Task<string> WriteAndRead(string field, string value, string tag)
                            {
                                await cSvc.SetCustomDetail(new Dictionary<string, string> { [field] = value });
                                await Task.Delay(1200);
                                await cHw.Publish("Fan/Control", new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                                await Task.Delay(1500);
                                string state = $"TCC={cHw.TccOffset} TCC开关={cHw.TccSwitch} TGP={cHw.GpuTgp} DB开关={cHw.GpuDbSwitch} DB={cHw.GpuDb} PL1={cHw.Pl1} PL2={cHw.Pl2} 超频开关={cHw.OcSwitch} 核心偏移={cHw.GpuCoreClockOffset} 显存偏移={cHw.GpuMemClockOffset}";
                                Logger.WriteLine($"CUSTOMTEST {tag} 写{field}={value} → {state}");
                                return state;
                            }

                            await cSvc.SwitchCustomProfile(1);
                            await Task.Delay(2500);
                            Logger.WriteLine($"CUSTOMTEST 档1 初始: TCC={cHw.TccOffset} TCC开关={cHw.TccSwitch} TGP={cHw.GpuTgp} DB开关={cHw.GpuDbSwitch} DB={cHw.GpuDb} PL1={cHw.Pl1} PL2={cHw.Pl2}");
                            int diagnosticTjMax = cHw.TjMax > 0 ? cHw.TjMax : 100;
                            int diagnosticTccHigh = Math.Clamp(diagnosticTjMax - 5, cHw.TccMinimum, cHw.TccMaximum);
                            int diagnosticTccLow = Math.Clamp(diagnosticTjMax - 15, cHw.TccMinimum, cHw.TccMaximum);

                            // TCC 开关单独字段验证（温度墙是否生效取决于开关状态）
                            await WriteAndRead("CpuTccOffsetSwitch", "1", "TCC开关开");
                            await WriteAndRead("CpuTccOffsetSwitch", "0", "TCC开关关");
                            await WriteAndRead("CpuTccOffsetSwitch", "1", "TCC开关开");

                            // TCC（目标温度语义，原版发 TjMax−偏移）
                            await WriteAndRead("CpuTccOffset", diagnosticTccHigh.ToString(), "TCC");
                            // TGP
                            await WriteAndRead("GpuConfigurableTGPTarget", "140", "TGP");
                            // DB 开关
                            await WriteAndRead("GpuDynamicBoostSwitch", "1", "DB开关");
                            // DB 值
                            await WriteAndRead("GpuDynamicBoost", "10", "DB值");
                            // PL2 验证
                            await WriteAndRead("PL2", "80", "PL2");

                            // 超频区验证（OverClockingSwitch + 核心/显存偏移，需 LCHWOC 支持）
                            Logger.WriteLine($"CUSTOMTEST 超频区: LchwocSupport={cHw.LchwocSupport} OcSwitch={cHw.OcSwitch} 核心偏移={cHw.GpuCoreClockOffset} 显存偏移={cHw.GpuMemClockOffset}");
                            if (cHw.LchwocSupport)
                            {
                                await WriteAndRead("OverClockingSwitch", "1", "超频开关");
                                await WriteAndRead("GpuCoreClockOffsetOC", "160", "核心偏移");
                                await WriteAndRead("GpuMemoryClockOffsetOC", "200", "显存偏移");
                                await WriteAndRead("GpuCoreClockOffsetOC", "150", "核心偏移恢复");
                                await WriteAndRead("GpuMemoryClockOffsetOC", "0", "显存偏移恢复");
                            }

                            // 恢复
                            await cSvc.SetCustomDetail(new Dictionary<string, string>
                            {
                                ["CpuTccOffset"] = diagnosticTccLow.ToString(),
                                ["GpuConfigurableTGPTarget"] = "150",
                                ["GpuDynamicBoostSwitch"] = "0",
                                ["GpuDynamicBoost"] = "5",
                                ["PL2"] = "85",
                            });
                            await Task.Delay(1200);
                            await cHw.Publish("Fan/Control", new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                            await Task.Delay(1500);
                            Logger.WriteLine($"CUSTOMTEST 恢复后: TCC={cHw.TccOffset} TGP={cHw.GpuTgp} DB开关={cHw.GpuDbSwitch} DB={cHw.GpuDb} PL1={cHw.Pl1} PL2={cHw.Pl2}");
                        }

                        // 3. 恢复原模式
                        if (origMode == 3)
                        {
                            if (origCpi >= 0) { await cSvc.SwitchCustomProfile(origCpi); await Task.Delay(2000); }
                        }
                        else if (origMode >= 0)
                        {
                            await cSvc.SwitchMode(origMode == 0 ? 2 : origMode == 2 ? 1 : 0);   // 服务层枚举：0=游戏 1=增强 2=办公
                            await Task.Delay(2000);
                        }
                        Logger.WriteLine($"CUSTOMTEST 已恢复模式 opMode={cHw.OperatingMode}");
                        Logger.WriteLine("CUSTOMTEST done");
                        Environment.Exit(0);
                    }
                    catch (Exception ex) { Logger.WriteLine("CUSTOMTEST FAIL: " + ex); Environment.Exit(2); }
                });
                Application.Run();
                return;
            }

            if (action == "colortest" || action == "--colortest")
            {
                // 屏幕校色全链路自测：4 色域档位切换 + 关闭 + 回读注册表 + 恢复原档
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ccHw = new MechrevoLite.Hardware.MechrevoHw();
                        bool ok = await ccHw.ConnectAsync();
                        Logger.WriteLine($"COLORTEST connected={ok}");
                        if (!ok) { Environment.Exit(1); return; }
                        var ccSvc = new MechrevoLite.Hardware.MechrevoService(ccHw);
                        await Task.Delay(1500);

                        int origMode = MechrevoLite.Hardware.MechrevoService.GetColorCalibrationMode();
                        bool origOn = MechrevoLite.Hardware.MechrevoService.IsColorCalibrationOn();
                        Logger.WriteLine($"COLORTEST 基线: mode={origMode} on={origOn}");

                        // Gamma LUT 检测：切档前后读显示器 gamma 表（校色实际落地的直接证据）
                        string rampBefore = NativeMethods.GetGammaRampHash();
                        Logger.WriteLine($"COLORTEST ramp 基线={rampBefore}");

                        foreach (int m in new[] { 1, 2, 3, 4 })
                        {
                            await ccSvc.SetColorCalibration(m);
                            await Task.Delay(4000);
                            string ramp = NativeMethods.GetGammaRampHash();
                            await ccHw.Publish("Setting/Control", new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                            await Task.Delay(1200);
                            int read = MechrevoLite.Hardware.MechrevoService.GetColorCalibrationMode();
                            Logger.WriteLine($"COLORTEST 切档{m} → ramp={ramp} (基线 {rampBefore}) 注册表={read} 开关={MechrevoLite.Hardware.MechrevoService.IsColorCalibrationOn()} Result={ccHw.ColorCalibrationResult}");
                        }

                        // 关闭 → 恢复
                        await ccSvc.SetColorCalibration(0);
                        await Task.Delay(2500);
                        Logger.WriteLine($"COLORTEST 关闭 → 开关={MechrevoLite.Hardware.MechrevoService.IsColorCalibrationOn()} (期望 False)");
                        await ccSvc.SetColorCalibration(origOn ? (origMode >= 1 ? origMode : 1) : 0);
                        await Task.Delay(2500);
                        Logger.WriteLine($"COLORTEST 恢复档{origMode} → 回读={MechrevoLite.Hardware.MechrevoService.GetColorCalibrationMode()} 开关={MechrevoLite.Hardware.MechrevoService.IsColorCalibrationOn()}");
                        Logger.WriteLine("COLORTEST done");
                        Environment.Exit(0);
                    }
                    catch (Exception ex) { Logger.WriteLine("COLORTEST FAIL: " + ex); Environment.Exit(2); }
                });
                Application.Run();
                return;
            }
#endif

            if (action == "charge")
            {
                bool applied = ApplyBatteryLimitAtBootAsync().GetAwaiter().GetResult();
                Environment.ExitCode = applied ? 0 : 1;
                AppConfig.Flush();
                Logger.Close();
                return;
            }

            string language = AppConfig.GetString("language");
            try
            {
                if (language != null && language.Length > 0)
                    Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                else
                {
                    var culture = CultureInfo.CurrentUICulture;
                    if (culture.ToString() == "kr") culture = CultureInfo.GetCultureInfo("ko");
                    Thread.CurrentThread.CurrentUICulture = culture;
                }
            } catch
            {
                Logger.WriteLine("Unknown Language: " + language);
            }

            Logger.WriteLine("----------------------");
            Logger.WriteLine("App launched: " + Assembly.GetExecutingAssembly().GetName().Version.ToString() + CultureInfo.CurrentUICulture + (ProcessHelper.IsUserAdministrator() ? "." : ""));

            // Mechrevo：不再请求管理员权限（FPS ETW 本机不可用，Overlay 使用率低——普通权限即可）
            // 单实例检查必须先于窗体、硬件和托盘创建，重复启动不能留下半初始化资源。
            if (!ProcessHelper.CheckAlreadyRunning(action))
            {
                Logger.Close();
                return;
            }

            settingsForm = new SettingsForm();
            modeControl = new ModeControl();
            gpuControl = settingsForm.gpuControl;
            toast = new ToastForm();

            hardwareOverlay = new HardwareOverlay();

            Application.ApplicationExit += OnExit;   // 退出时释放灯效等资源

            ProcessHelper.SetPriority();
            OfficialConsoleIsolation.StartGuardIfNeeded();

            CleanupLegacyFiles();

            var startCount = AppConfig.Get("start_count") + 1;
            AppConfig.Set("start_count", startCount);
            Logger.WriteLine("Start Count: " + startCount);

            acpi = new AsusACPI();
            hw = new MechrevoLite.Hardware.MechrevoHw();
            service = new MechrevoLite.Hardware.MechrevoService(hw);   // 同步创建，避免点击按钮 NRE/无响应
            hw.StateChanged += OnHardwareStateChanged;
            hw.CapabilitiesChanged += () =>
            {
                try
                {
                    if (settingsForm is not null && !settingsForm.IsDisposed)
                        settingsForm.BeginInvoke(settingsForm.RefreshDeviceCapabilities);
                }
                catch (Exception ex) { Logger.WriteLine("Capability UI refresh failed: " + ex.Message); }
            };
            settingsForm.RefreshDeviceCapabilities();
            rgb = new MechrevoLite.Hardware.KeyboardRgb();   // 灯效引擎（打开灯效窗口时连接）
            rgb.LoadConfig();   // 必须先于任何连接/状态回读加载，防止开机默认值覆盖用户速度
            ble = new MechrevoLite.Hardware.WaterCoolerBle();   // 蓝牙水冷直连（打开连接窗口时扫描）
            hw.ConnectionReady += generation => _ = RestoreAfterHardwareConnectionAsync(generation);
            StartTelemetryRecoveryMonitor();
            // 接线不依赖首次连接成败：重连成功后 UI 数据/事件照常工作（幂等，只执行一次）
            _ = Task.Run(async () =>
            {
                try
                {
                    HardwareControl.AttachMechrevoHw(hw);
                    service.ModeChanged += m =>
                    {
                        try
                        {
                            if (settingsForm is not null && !settingsForm.IsDisposed)
                                settingsForm.BeginInvoke(() => { settingsForm.ShowMode(MechrevoLite.Hardware.MechrevoService.ToVisualMode(m)); settingsForm.SetContextMenu(); });
                        }
                        catch (Exception ex) { Logger.WriteLine("Mode UI refresh failed: " + ex.Message); }
                    };
                    service.GpuModeChanged += m =>
                    {
                        try
                        {
                            int logicalMode = AppConfig.Is("gpu_auto") && hw.SupportsIgpuOnly &&
                                m is MechrevoLite.Hardware.MechrevoService.GpuIGpu or MechrevoLite.Hardware.MechrevoService.GpuStandard
                                    ? MechrevoLite.Hardware.MechrevoService.GpuAuto
                                    : m;
                            AppConfig.Set("gpu_mode", logicalMode);
                            AppConfig.Set("gpu_auto", logicalMode == MechrevoLite.Hardware.MechrevoService.GpuAuto ? 1 : 0);
                            if (settingsForm is not null && !settingsForm.IsDisposed)
                                settingsForm.BeginInvoke(() => { settingsForm.VisualiseGPUMode(logicalMode); settingsForm.SetContextMenu(); });
                        }
                        catch (Exception ex) { Logger.WriteLine("GPU UI refresh failed: " + ex.Message); }
                    };
                    bool ok = await hw.ConnectAsync();
                    Logger.WriteLine("MechrevoHw connected: " + ok);
                }
                catch (Exception ex) { Logger.WriteLine("MechrevoHw connect fail: " + ex.Message); }
            });

            HardwareControl.RecreateGpuControl();

            trayIcon = new NotifyIcon
            {
                Text = "L-Mechrevo",
                Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "favicon.ico")),
                Visible = true
            };
            if (!MqttSecurity.HasInboundBlockRule())
            {
                Logger.WriteLine(MqttSecurity.MissingRuleWarning());
            }

            _trayRetryTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _trayRetryTimer.Tick += (_, _) =>
            {
                _trayRetryTimer?.Stop();
                _trayRetryTimer?.Dispose();
                _trayRetryTimer = null;
                trayIcon.Visible = false;
                trayIcon.Visible = true;
            };
            _trayRetryTimer.Start();

            WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");
            Logger.WriteLine($"Tray Icon: {trayIcon.Visible} | {WM_TASKBARCREATED}");

            settingsForm.SetContextMenu();
            trayIcon.MouseClick += TrayIcon_MouseClick;
            trayIcon.MouseMove += TrayIcon_MouseMove;


            

            powerSettleTimer.Elapsed += (s, e) => OnSystemEvent(() => OnPowerSettled(s, e));

            // Subscribing for system power change events
            SystemEvents.PowerModeChanged += (s, e) => OnSystemEvent(() => SystemEvents_PowerModeChanged(s, e));
            SystemEvents.UserPreferenceChanged += (s, e) => OnSystemEvent(() => SystemEvents_UserPreferenceChanged(s, e));

            SystemEvents.SessionSwitch += (s, e) => OnSystemEvent(() => SystemEvents_SessionSwitch(s, e));
            SystemEvents.SessionEnding += (s, e) => OnSystemEvent(() => SystemEvents_SessionEnding(s, e));


            // Subscribing for monitor power on events
            unRegPowerNotify = NativeMethods.RegisterPowerSettingNotification(settingsForm.Handle, PowerSettingGuid.ConsoleDisplayState, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);
            unRegPowerNotifyLid = NativeMethods.RegisterPowerSettingNotification(settingsForm.Handle, PowerSettingGuid.LIDSWITCH_STATE_CHANGE, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);
            unRegPowerNotifyEnergy = NativeMethods.RegisterPowerSettingNotification(settingsForm.Handle, PowerSettingGuid.EnergySaverStatus, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);
            unRegSuspendResume = NativeMethods.RegisterSuspendResumeNotification(settingsForm.Handle, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);


            Task task = Task.Run((Action)PeripheralsProvider.DetectAllAsusMice);
            PeripheralsProvider.RegisterForDeviceEvents();

            // 登录任务只驻留托盘；桌面快捷方式和手动启动仍直接显示主界面。
            if (!startMinimized)
                SettingsToggle(false);
            else
                Logger.WriteLine("Startup task launched minimized to tray.");

            // Let the first frame paint before display enumeration and compatibility initialization.
            EventHandler? deferredInitialization = null;
            deferredInitialization = (_, _) =>
            {
                Application.Idle -= deferredInitialization;
                if (!startMinimized) settingsForm.ShowFirstRunGuideIfNeeded();
                settingsForm.InitAura();
                SetAutoModes(init: true);
                StartLightingIdleMonitor();
                if (AppConfig.IsOverlay()) hardwareOverlay?.StartOverlay();
            };
            Application.Idle += deferredInitialization;

            switch (action)
            {
                case "cpu":
                case "gpu":
                case "services":
                    break;
                case "uv":
                    Startup.ReScheduleAdmin();
                    modeControl.SetRyzen();
                    break;
                case "colors":
                    // Legacy G-Helper task argument. Color state must only change after
                    // an explicit user action in the calibration window.
                    Logger.WriteLine("Ignored legacy 'colors' startup action.");
                    break;
                default:
                    Task.Run(Startup.StartupCheck);
                    break;
            }

            // HID 设备可能早于或晚于 GCU 出现；独立重试，连接成功事件还会再补做一次状态同步。
            _ = Task.Run(() => RestoreLightingWithRetryAsync());

            StartSilentUpdateCheck();

            Application.Run();
        }

        internal static bool IsStartupLaunch(string? action) =>
            action?.Equals("startup", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>
        /// 启动后延迟静默检测一次更新：失败/无新版都什么都不做，有新版只给按钮加个角标，
        /// **绝不自动下载或安装**。这是我们唯一的出站请求，<c>check_updates=0</c> 可关闭，
        /// 跨会话按 UpdateChecker.AutoCheckInterval 节流。
        /// </summary>
        static void StartSilentUpdateCheck()
        {
            if (UiAuditMode || !UpdateChecker.AutoCheckEnabled) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(8));   // 别和启动高峰期抢资源
                    UpdateInfo? info = await UpdateChecker.CheckAsync();
                    if (info is not { UpdateAvailable: true }) return;
                    SettingsForm? form = settingsForm;
                    if (form is null || form.IsDisposed) return;
                    form.BeginInvoke(() => form.MarkUpdateAvailable(info));
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("静默更新检测失败：" + ex.Message);
                }
            });
        }

        /// <summary>更新安装前的退出路径：与"退出"按钮完全一致（释放灯效、收托盘、退出消息循环）。</summary>
        internal static void RequestShutdownForUpdate()
        {
            try
            {
                AsusLampArray.Release();
                settingsForm?.Close();
                if (trayIcon is not null) trayIcon.Visible = false;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("更新退出清理失败：" + ex.Message);
            }
            Application.Exit();
        }

        static async Task RestoreAfterHardwareConnectionAsync(int generation)
        {
            await _hardwareRecoveryLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _exitStarted) != 0 ||
                    generation <= Volatile.Read(ref _lastRecoveredConnectionGeneration)) return;

                Interlocked.Increment(ref _lightingRestoreRequestId);   // 重连后允许把外置灯效重新落地一次
                Logger.WriteLine($"硬件连接恢复开始: generation={generation}");
                long refreshStarted = Environment.TickCount64;
                await service.RefreshAll().ConfigureAwait(false);
                await Task.Delay(800).ConfigureAwait(false);
                if (!hw.HasTelemetrySince(refreshStarted))
                {
                    // MQTT 已连接不代表 GCU 的硬件采集模块已经完成启动；补发一次 System_ON。
                    Logger.WriteLine("硬件连接已建立但尚无温度遥测，补发全量状态请求");
                    await service.RefreshAll().ConfigureAwait(false);
                    await Task.Delay(1200).ConfigureAwait(false);
                }

                bool lightingRestored = await RestoreLightingForGenerationAsync(generation, force: false)
                    .ConfigureAwait(false);
                Logger.WriteLine($"灯效连接恢复结果: generation={generation}, restored={lightingRestored}");
                int pendingPerformanceMode = Interlocked.Exchange(ref _pendingStartupPerformanceMode, -1);
                void ReapplyPerformanceOnUiThread()
                {
                    // The recovery continuation runs on a worker thread. Keep all
                    // ModeControl/WinForms mutations on the UI thread.
                    if (ShouldReapplyPendingPerformanceMode(pendingPerformanceMode, Modes.GetCurrent()))
                    {
                        Logger.WriteLine($"GCU 连接恢复后重放启动性能档: mode={pendingPerformanceMode}");
                        modeControl.AutoPerformance(
                            preserveActiveCustom: pendingPerformanceMode == MechrevoLite.Hardware.MechrevoService.ModeCustom);
                    }
                    else if (pendingPerformanceMode < 0 &&
                        hw.OperatingMode == MechrevoLite.Hardware.MechrevoService.ModeCustom)
                    {
                        // A custom mode selected after startup still needs its own profile
                        // replay, but must not overwrite a newer non-custom user choice.
                        modeControl.AutoPerformance(preserveActiveCustom: true);
                    }
                }
                try
                {
                    if (settingsForm is { IsDisposed: false } && settingsForm.IsHandleCreated &&
                        settingsForm.InvokeRequired)
                        settingsForm.BeginInvoke(ReapplyPerformanceOnUiThread);
                    else
                        ReapplyPerformanceOnUiThread();
                }
                catch (Exception ex) { Logger.WriteLine("Performance recovery UI dispatch failed: " + ex.Message); }
                service.RestoreCurrentDirectGpuOverclock();
                try
                {
                    if (settingsForm is not null && !settingsForm.IsDisposed)
                    {
                        settingsForm.BeginInvoke(() =>
                        {
                            settingsForm.RefreshSensors();
                            settingsForm.RefreshDeviceCapabilities();
                            if (hw.OperatingMode >= 0)
                                settingsForm.ShowMode(hw.GHelperMode);
                            gpuControl.InitGPUMode();
                            settingsForm.SetContextMenu();
                        });
                    }
                }
                catch (Exception ex) { Logger.WriteLine("Hardware recovery UI refresh failed: " + ex.Message); }

                if (AppConfig.Is("gpu_auto")) gpuControl.AutoGPUMode(delay: 250);
                Volatile.Write(ref _lastRecoveredConnectionGeneration, generation);
                Logger.WriteLine($"硬件连接恢复完成: generation={generation}, telemetry={hw.HasTelemetrySince(refreshStarted)}");
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"硬件连接恢复失败 generation={generation}: {ex.Message}");
            }
            finally { _hardwareRecoveryLock.Release(); }
        }

        static void StartTelemetryRecoveryMonitor()
        {
            _telemetryRecoveryTimer = new System.Threading.Timer(
                _ => _ = RecoverStaleTelemetryAsync(),
                null,
                TelemetryStaleAfter,
                TimeSpan.FromSeconds(15));
        }

        static async Task RecoverStaleTelemetryAsync()
        {
            if (Volatile.Read(ref _exitStarted) != 0 ||
                hw is not { IsConnected: true } ||
                service is null ||
                !hw.IsTelemetryStale(TelemetryStaleAfter)) return;

            if (!await _telemetryRecoveryLock.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                if (Volatile.Read(ref _exitStarted) != 0 ||
                    hw is not { IsConnected: true } ||
                    !hw.IsTelemetryStale(TelemetryStaleAfter)) return;

                long now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _lastTelemetryRecoveryRequest) < TelemetryRecoveryCooldown.TotalMilliseconds)
                    return;

                Interlocked.Exchange(ref _lastTelemetryRecoveryRequest, now);
                Logger.WriteLine("温度遥测超时，补发全量状态请求");
                await service.RefreshAll().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("温度遥测恢复请求失败: " + ex.Message);
            }
            finally
            {
                _telemetryRecoveryLock.Release();
            }
        }

        /// <summary>
        /// 有界退避重试的恢复入口：启动、GCU 重连、系统唤醒、空闲唤醒共用。
        /// force=true 时每轮都强制重跑（不被协调器「本代次已完成」的门挡住），
        /// 这样外置灯带一次确认失败后下一轮仍会补刷，而不是直接判定恢复完成。
        /// </summary>
        internal static async Task RestoreLightingWithRetryAsync(bool force = false)
        {
            int delayMs = 250;
            for (int attempt = 1; attempt <= 20 && Volatile.Read(ref _exitStarted) == 0; attempt++)
            {
                int generation = hw?.ConnectionGeneration ?? 0;
                if (await RestoreLightingForGenerationAsync(
                    generation,
                    force).ConfigureAwait(false)) return;
                if (attempt == 1 || attempt % 5 == 0)
                    Logger.WriteLine($"灯效开机恢复等待设备: attempt={attempt}, mqtt={hw?.IsConnected == true}");
                await Task.Delay(delayMs).ConfigureAwait(false);
                delayMs = Math.Min(delayMs * 2, 3000);
            }
            Logger.WriteLine("灯效开机恢复暂未完成，将在硬件连接成功时继续");
        }

        static async Task<bool> RestoreLightingForGenerationAsync(int generation, bool force)
        {
            for (int wait = 0; wait < 30 && Volatile.Read(ref _exitStarted) == 0; wait++)
            {
                LightingRestoreDecision decision = _lightingRestoreCoordinator.TryBegin(generation, force);
                if (decision == LightingRestoreDecision.SkipCompleted) return true;
                if (decision == LightingRestoreDecision.DeferInFlight)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                    continue;
                }

                bool success = false;
                try
                {
                    if (!force && Volatile.Read(ref _officialUiSettled) == 0)
                        await EnsureOfficialUiSettledAsync().ConfigureAwait(false);
                    success = await ReconcileLightingPowerAsync(force).ConfigureAwait(false);
                    return success;
                }
                finally
                {
                    _lightingRestoreCoordinator.Complete(generation, success);
                }
            }

            Logger.WriteLine($"灯效恢复协调器等待超时: generation={generation}, force={force}");
            return false;
        }

        static async Task EnsureOfficialUiSettledAsync()
        {
            if (Volatile.Read(ref _officialUiSettled) != 0) return;
            await _officialUiSettleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _officialUiSettled) != 0) return;
                bool settled;
                try
                {
                    settled = await OfficialConsoleIsolation.WaitForUiSettledAsync(
                        TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    settled = false;
                    Logger.WriteLine("官方 UI 安静窗口检查失败: " + ex.Message);
                }

                Volatile.Write(ref _officialUiSettled, 1);
                Logger.WriteLine($"官方 UI 安静窗口完成: settled={settled}");
            }
            finally
            {
                _officialUiSettleLock.Release();
            }
        }

        static void StartLightingIdleMonitor()
        {
            if (UiAuditMode || _lightingIdleTimer is not null) return;
            _lightingIdleTimer = new System.Threading.Timer(
                _ => _ = EvaluateLightingIdleAsync(),
                null,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2));
        }

        internal static void MarkKeyboardCustomStatusBaseline()
        {
            if (hw is null) return;
            Interlocked.Exchange(ref _keyboardStatusBaselineVersion, hw.KeyboardStatusVersion);
            Volatile.Write(ref _keyboardStatusBaselineBrightness,
                MechrevoLite.Hardware.KeyboardRgb.MapReportedHardwareBrightness(hw.KeyboardBrightness, hw.KeyboardLight));
            Interlocked.Exchange(ref _keyboardStatusBaselineEffect, hw.KeyboardEffect ?? "");
        }

        /// <summary>
        /// 固件（Fn 热键）接管判定：只有版本前进、且亮度或效果相对基线发生变化的那一帧才算接管。
        /// 同值状态帧（GCU 周期回显）与过期版本一律不算，因此一次真实变化只重申一次。
        /// 抽成静态内部方法以便单测锁定该契约。
        /// </summary>
        internal static bool ShouldReassertKeyboardCustomMode(
            long statusVersion, long baselineVersion,
            int reportedBrightness, int baselineBrightness,
            string effect, string baselineEffect)
        {
            if (statusVersion <= baselineVersion) return false;
            bool lightChanged = baselineBrightness is >= 0 and <= 100 && reportedBrightness is >= 0 and <= 100 &&
                reportedBrightness != baselineBrightness;
            bool effectChanged = !string.IsNullOrWhiteSpace(effect) &&
                !string.Equals(effect, baselineEffect, StringComparison.OrdinalIgnoreCase);
            return lightChanged || effectChanged;
        }

        /// <summary>
        /// 把 HID 渲染亮度同步到固件上报值——保留固件的新亮度、绝不写回旧值。
        /// 帧未带有效亮度（-1/越界）时保持现有 HID 亮度不变。返回是否发生变化。
        /// </summary>
        internal static bool SyncKeyboardBrightnessForFirmwareChange(
            int reportedBrightness, Func<int> currentHidBrightness, Action<int> setHidBrightness)
        {
            if (reportedBrightness is < 0 or > 100) return false;
            if (currentHidBrightness() == reportedBrightness) return false;
            setHidBrightness(reportedBrightness);
            return true;
        }

        /// <summary>
        /// Fn 热键（固件）接管后的立即重申按代际守卫：只有仍处于当前代际的重申才写 HID。
        /// 迟到的旧检测（或期间推进代际的新事件）不得再重进模式，否则会覆盖更新的状态——
        /// 与 SettingsForm.StopHidEffectForCurrentGeneration 同一守卫语义。返回是否执行了重申。
        /// </summary>
        internal static bool ReassertKeyboardCustomModeForCurrentGeneration(
            int generation, Func<int> currentGeneration, Action reassert)
        {
            if (generation != currentGeneration()) return false;
            reassert();
            return true;
        }

        /// <summary>
        /// 立即重进 ITE 自定义帧模式并确保效果线程在跑（与开关 ON / RgbForm 路径同一 ReInit+StartMode 语义）。
        /// </summary>
        static void ReassertKeyboardCustomMode()
        {
            KeyboardRgb? kb = rgb;
            if (kb is null || !kb.KbPowerOn) return;
            kb.ReInitCustomMode();
            kb.StartMode(kb.KbHidMode);
            Logger.WriteLine($"RGB 固件接管，立即重申自定义帧模式：mode={kb.KbHidMode}");
        }

        static void OnHardwareStateChanged(string topic)
        {
            if (!string.Equals(topic, "Keyboard/Status", StringComparison.Ordinal) ||
                Volatile.Read(ref _exitStarted) != 0 || rgb is null || !rgb.KbPowerOn ||
                !rgb.IsConnected || hw is null ||
                Volatile.Read(ref _lightingIdleSuspended) != 0)
                return;

            int reportedBrightness = MechrevoLite.Hardware.KeyboardRgb.MapReportedHardwareBrightness(
                hw.KeyboardBrightness, hw.KeyboardLight);
            if (!ShouldReassertKeyboardCustomMode(
                    hw.KeyboardStatusVersion, Interlocked.Read(ref _keyboardStatusBaselineVersion),
                    reportedBrightness, Volatile.Read(ref _keyboardStatusBaselineBrightness),
                    hw.KeyboardEffect ?? "", Volatile.Read(ref _keyboardStatusBaselineEffect)))
                return;

            // 亮度同步先于单飞门：Fn 连发时每一档都要落到渲染器，否则灯停在旧档（固件新亮度必须保留）。
            if (SyncKeyboardBrightnessForFirmwareChange(reportedBrightness, () => rgb.Brightness,
                    value => { rgb.Brightness = value; rgb.QueueSaveConfig(); }))
                Logger.WriteLine($"RGB 硬件亮度同步：GCU={reportedBrightness}% HID={reportedBrightness}%");

            if (Interlocked.Exchange(ref _keyboardStatusRecoveryPending, 1) != 0) return;

            int takeoverGeneration = Interlocked.Increment(ref _keyboardFirmwareTakeoverGeneration);
            _ = Task.Run(async () =>
            {
                try
                {
                    // 固件重新进入官方效果模式后控制器已退出 ITE 自定义帧模式，HID 帧被静默忽略
                    //（WriteFile 仍返回成功，帧循环不会察觉）。必须**立即**重进自定义帧模式：
                    // 等下一帧不会重进（帧循环只管发帧），等去抖只会让官方效果多可见一段。
                    // 重申单飞、代际守卫，不加重试循环。
                    ReassertKeyboardCustomModeForCurrentGeneration(
                        takeoverGeneration, () => Volatile.Read(ref _keyboardFirmwareTakeoverGeneration),
                        ReassertKeyboardCustomMode);

                    bool restored = await RestoreKeyboardLightingAsync(force: false).ConfigureAwait(false);
                    if (!restored)
                    {
                        Logger.WriteLine("RGB 硬件亮度事件恢复失败，将等待下一次硬件连接恢复");
                        _ = RestoreLightingWithRetryAsync(force: true);
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("RGB 硬件亮度事件处理失败: " + ex.Message);
                }
                finally
                {
                    Volatile.Write(ref _keyboardStatusRecoveryPending, 0);
                }
            });
        }

        internal static void NotifyLightingUserIntent()
        {
            if (Interlocked.Exchange(ref _lightingIdleSuspended, 0) != 0)
                _ = RestoreLightingWithRetryAsync(force: true);
        }

        /// <summary>三条通道当前是否处于临时熄灯（空闲休眠或离电关灯）。开关回显与恢复判据共用这一份。</summary>
        internal static bool IsLightingTemporarilySuspended =>
            LightingState.IsTemporarilySuspended(
                AppConfig.Is("lighting_off_on_battery"),
                currentSource == PowerSource.Battery,
                Volatile.Read(ref _lightingIdleSuspended) != 0);

        /// <summary>测试 seam：读写应用内空闲休眠标志，驱动恢复路径而不依赖真实空闲计时。</summary>
        internal static bool LightingIdleSuspended
        {
            get => Volatile.Read(ref _lightingIdleSuspended) != 0;
            set => Volatile.Write(ref _lightingIdleSuspended, value ? 1 : 0);
        }

        /// <summary>测试 seam：当前「允许外置灯效重放一次」的恢复令牌。</summary>
        internal static int LightingRestoreRequestId => Volatile.Read(ref _lightingRestoreRequestId);

        /// <summary>测试 seam：指定外置通道已确认上电的恢复令牌；-1 = 本轮尚未确认。</summary>
        internal static int ExternalRestoreAppliedRequestId(string topic) =>
            _lightingExternalRestoreAppliedIds.TryGetValue(topic, out int appliedId) ? appliedId : -1;

        /// <summary>时钟线程入口：读取配置与系统空闲时长的单一决策点，三条通道共用。</summary>
        internal static Task<LightingIdleAction> EvaluateLightingIdleAsync() =>
            EvaluateLightingIdleAsync(
                AppConfig.Get("lighting_idle_seconds", Math.Max(0, rgb?.CloseTimerMinutes ?? 0) * 60),
                Math.Max(0, (long)NativeMethods.GetIdleTime().TotalMilliseconds));

        /// <summary>
        /// 单一熄灯/恢复决策：键盘、灯带、Logo 三条通道都只由这一次判定的结果驱动，
        /// 不存在任何按设备各自计时、各自到期熄灭的第二条时钟。
        /// </summary>
        internal static async Task<LightingIdleAction> EvaluateLightingIdleAsync(int timeoutSeconds, long idleMilliseconds)
        {
            if (Volatile.Read(ref _exitStarted) != 0 || rgb is null) return LightingIdleAction.None;
            try
            {
                LightingIdleAction action = LightingState.ResolveIdleAction(
                    timeoutSeconds,
                    idleMilliseconds,
                    Volatile.Read(ref _lightingIdleSuspended) != 0);
                if (action == LightingIdleAction.Suspend)
                {
                    if (Interlocked.Exchange(ref _lightingIdleSuspended, 1) == 0)
                    {
                        Logger.WriteLine($"灯效空闲休眠：{timeoutSeconds} 秒");
                        await ReconcileLightingPowerAsync().ConfigureAwait(false);
                    }
                }
                else if (action == LightingIdleAction.Restore &&
                    Interlocked.Exchange(ref _lightingIdleSuspended, 0) != 0)
                {
                    Logger.WriteLine("灯效空闲恢复：检测到用户输入");
                    // 走与启动/唤醒相同的重试通道：外置灯带确认失败时有限次退避补刷，
                    // 否则灯带/Logo 会一直暗到下一次无关事件触发恢复。
                    await RestoreLightingWithRetryAsync(force: true).ConfigureAwait(false);
                }
                return action;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("灯效空闲检测失败: " + ex.Message);
                return LightingIdleAction.None;
            }
        }

        internal static async Task<bool> ReconcileLightingPowerAsync(bool forceKeyboardRestore = false)
        {
            await _lightingStateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _exitStarted) != 0 || rgb is null) return false;
                bool temporarySuspend = IsLightingTemporarilySuspended;
                if (temporarySuspend)
                {
                    await SuspendLightingTemporarilyAsync().ConfigureAwait(false);
                    return true;
                }

                // 对齐规则：一次恢复周期同时驱动全部三条通道。键盘本地 HID 与外置 GCU 电源
                // 在这一轮**并行**下发，两侧都只发命令、不等厂商回读——谁都不得比谁早/晚约 2 秒，
                // 也不得因回读不来而把对方推进重试。可见变化因此落在同一时间窗内。
                Task<bool> keyboardTask = RestoreKeyboardLightingAsync(forceKeyboardRestore);
                Task<bool> externalTask = RestoreExternalLightingAsync();
                await Task.WhenAll(keyboardTask, externalTask).ConfigureAwait(false);
                bool keyboardRestored = await keyboardTask.ConfigureAwait(false);
                bool externalRestored = await externalTask.ConfigureAwait(false);
                return keyboardRestored && externalRestored;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("灯效状态恢复失败: " + ex.Message);
                return false;
            }
            finally { _lightingStateLock.Release(); }
        }

        static async Task SuspendLightingTemporarilyAsync()
        {
            Interlocked.Increment(ref _lightingRestoreRequestId);   // 新熄灯周期 = 允许下一次恢复重放一次
            rgb.StopCurrentEffect();
            if (hw is not { IsConnected: true } || service is null)
            {
                Logger.WriteLine($"灯效临时熄灭（同一到期，键盘已停）：{DescribeLightingChannels()}");
                return;
            }

            CaptureExternalPowerBeforeTemporarySuspend();
            if (rgb.KbPowerOn)
            {
                // 熄灭同样只下发不等回读：键盘本地效果已在上面立即停掉，外置关灯紧随同一轮发出。
                bool issued = await service.PublishLightPower("Keyboard/Ctrl", false).ConfigureAwait(false);
                service.ObserveLightPower("Keyboard/Ctrl", false);
                if (issued) Interlocked.Exchange(ref _keyboardPowerTemporarilySuspended, 1);
            }
            await SetExternalLightingPowerAsync(on: false, restoreEffect: false, useTemporarySnapshot: false)
                .ConfigureAwait(false);
            Logger.WriteLine($"灯效临时熄灭（同一到期）：{DescribeLightingChannels()}");
        }

        /// <summary>灯效三通道的软件可观测快照（键盘=HID 效果是否在跑，灯带/Logo=GCU 回读电源），用于真机核对同一到期与单次恢复。</summary>
        internal static string DescribeLightingChannels()
        {
            string lightbar = hw is not null && hw.QuickSwitches.TryGetValue("lightbar", out bool lb) ? (lb ? "On" : "Off") : "n/a";
            string logo = hw is not null && hw.QuickSwitches.TryGetValue("logolight", out bool lg) ? (lg ? "On" : "Off") : "n/a";
            string keyboard = rgb is not null && rgb.ActiveMode >= 0 ? $"On(mode={rgb.ActiveMode})" : "Off";
            return $"keyboard={keyboard} lightbar={lightbar} logo={logo}";
        }

        /// <summary>
        /// 外置灯带的两条通道：主灯条、Logo。
        /// 每条的控制主题、快捷开关键、以及"这台机器有没有这条灯带"的判据。
        /// </summary>
        static readonly (string Topic, string SwitchKey, Func<bool> Supported)[] ExternalLightChannels =
        [
            ("HidLightbar/Ctrl", "lightbar", () => hw.SupportsLightbar),
            ("HidLightbar_Logo/Ctrl", "logolight", () => hw.SupportsLogoLight),
        ];

        static void CaptureExternalPowerBeforeTemporarySuspend()
        {
            foreach (var (topic, switchKey, _) in ExternalLightChannels)
            {
                if (_externalPowerBeforeTemporarySuspend.ContainsKey(topic)) continue;
                if (hw.QuickSwitches.TryGetValue(switchKey, out bool power))
                    _externalPowerBeforeTemporarySuspend[topic] = power;
            }
        }

        static async Task<bool> RestoreExternalLightingAsync()
        {
            int requestId = Volatile.Read(ref _lightingRestoreRequestId);
            // 同一熄灯周期内每条通道只落地一次：空闲恢复/唤醒恢复/连接恢复/电源握手会相继触发，
            // 重复上电会让固件重新初始化，灯带与 Logo 反复闪烁。只有命令**发布失败**（不是回读未确认）
            // 的通道才不记账，交给重试只补刷它；已成功下发的通道保持沉默。
            (bool allIssued, bool applied, string issuedSummary) = await SetExternalLightingPowerAsync(
                on: true, restoreEffect: true, useTemporarySnapshot: true, restoreRequestId: requestId)
                .ConfigureAwait(false);
            // 记录的是本轮**下发的目标**而非回读快照：回读现在是异步遥测，同步读到的状态必然滞后，
            // 用它当证据只会误导（旧日志里出现 keyboard=Off 的假象）。真实状态由随后的两行遥测给出。
            if (allIssued && applied)
                Logger.WriteLine($"灯效恢复（单次下发）：keyboard={(rgb?.KbPowerOn == true ? "On" : "Off")} {issuedSummary}");
            return allIssued;
        }

        /// <summary>
        /// 通道通电后重放已保存的灯效：先等固件完成上电初始化，再下发用户选择的效果，
        /// 否则固件的上电默认效果（常亮）会盖掉用户选择。恢复路径与设置页电源开关共用这一份。
        /// shouldApply 在真正下发前再确认一次（例如电源已被快速切回 OFF）。
        /// </summary>
        internal static async Task<bool> ApplyLightChannelEffectAsync(
            string topic, LightChannelSettings settings, int delayMs = 120, Func<bool>? shouldApply = null)
        {
            if (delayMs > 0) await Task.Delay(delayMs).ConfigureAwait(false);
            if (shouldApply is not null && !shouldApply()) return false;
            if (service is null) return false;
            return await service.SetLightEffect(topic, settings.Effect, settings.Light, settings.Speed,
                "None", settings.Effect == "Single" ? Color.FromArgb(settings.ColorArgb) : null, save: false)
                .ConfigureAwait(false);
        }

        static async Task<(bool AllIssued, bool Attempted, string IssuedSummary)> SetExternalLightingPowerAsync(
            bool on, bool restoreEffect, bool useTemporarySnapshot, int? restoreRequestId = null)
        {
            if (hw is not { IsConnected: true } || service is null) return (false, false, "");

            bool allSucceeded = true;
            bool attempted = false;
            var issuedSummary = new List<string>();
            var effectTasks = new List<Task<bool>>();
            var effectTopics = new List<string>();
            foreach (var (topic, _, supported) in ExternalLightChannels)
            {
                if (!supported()) continue;

                // 恢复路径：这条通道已经在本轮落地过了，跳过——重复上电会让灯带/Logo 反复闪烁。
                if (on && restoreRequestId is int requestId &&
                    _lightingExternalRestoreAppliedIds.TryGetValue(topic, out int appliedId) &&
                    appliedId == requestId)
                    continue;

                bool hasSavedSettings = LightingSettingsStore.TryLoad(topic, out LightChannelSettings settings);
                bool? capturedPower = _externalPowerBeforeTemporarySuspend.TryGetValue(topic, out bool snapshot)
                    ? snapshot
                    : null;
                if (on && !hasSavedSettings && (!useTemporarySnapshot || !capturedPower.HasValue)) continue;

                bool requested = on && (hasSavedSettings ? settings.PowerOn : capturedPower == true);
                attempted = true;
                // 与 SupportsLightTopic/确认逻辑共用唯一一份 topic→开关键映射，不写第二份。
                string? switchKey = MechrevoService.LightTopicToQuickSwitchKey(topic);
                if (switchKey is not null) issuedSummary.Add($"{switchKey}={(requested ? "On" : "Off")}");
                // 只下发不等回读：命令一旦成功发布就视为已应用。厂商确认降级为后台遥测，
                // 既不阻塞本条通道的效果重放，也不把「已下发、灯已亮」判成失败去触发重发。
                bool issued = await service.PublishLightPower(topic, requested).ConfigureAwait(false);
                if (issued) service.ObserveLightPower(topic, requested);
                bool channelSucceeded = issued;
                if (issued && requested && restoreEffect && hasSavedSettings)
                {
                    // 效果重放带着各自的上电初始化延迟启动，稍后并行等待——两条灯带同时亮，
                    // 而不是串行各差一个 120ms 窗口。注意这里挂在 `issued` 上，不再挂在回读确认上。
                    effectTasks.Add(ApplyLightChannelEffectAsync(topic, settings));
                    effectTopics.Add(topic);
                }
                allSucceeded &= channelSucceeded;
                if (on && useTemporarySnapshot && channelSucceeded)
                    _externalPowerBeforeTemporarySuspend.Remove(topic);
                // 只有这条通道的命令成功下发才记账；发布失败的通道保持未记账，交给重试补刷。
                if (on && restoreRequestId is int successRequestId && channelSucceeded)
                    _lightingExternalRestoreAppliedIds[topic] = successRequestId;
            }

            for (int i = 0; i < effectTasks.Count; i++)
            {
                if (await effectTasks[i].ConfigureAwait(false)) continue;
                // 效果重放发布失败：撤掉该通道本轮的记账，让下一轮只补刷它。
                allSucceeded = false;
                if (on && restoreRequestId is not null)
                    _lightingExternalRestoreAppliedIds.TryRemove(effectTopics[i], out _);
            }
            return (!attempted || allSucceeded, attempted, string.Join(' ', issuedSummary));
        }

        /// <summary>键盘电源命令与本地 HID 效果之间的固定上电静置；替代过去「等 GCU 回读确认」的约 2 秒阻塞。</summary>
        const int KeyboardPowerSettleMs = 120;

        static async Task<bool> RestoreKeyboardLightingAsync(bool force = false)
        {
            await _keyboardRestoreLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _exitStarted) != 0 || rgb is null) return false;
                bool temporaryPowerOff = Volatile.Read(ref _keyboardPowerTemporarilySuspended) != 0;
                bool forceEffectRestore = force || temporaryPowerOff;
                if (!rgb.KbPowerOn)
                {
                    rgb.StopCurrentEffect();
                    if (hw is not { IsConnected: true } || service is null) return false;
                    // 只下发不等回读：键盘本就该灭（StopCurrentEffect 已就地生效），回读未确认不得
                    // 把这条通道判成失败、把整个周期拖进 20 轮重发。
                    bool issued = await service.PublishLightPower("Keyboard/Ctrl", false).ConfigureAwait(false);
                    service.ObserveLightPower("Keyboard/Ctrl", false);
                    if (issued)
                    {
                        Interlocked.Exchange(ref _keyboardPowerTemporarilySuspended, 0);
                        Logger.WriteLine("RGB 自动恢复：按用户设置保持关闭");
                    }
                    return issued;
                }
                if (temporaryPowerOff && (hw is not { IsConnected: true } || service is null)) return false;

                int requestId = Volatile.Read(ref _lightingRestoreRequestId);
                bool appliedThisCycle = Volatile.Read(ref _keyboardRestoreAppliedRequestId) == requestId;
                // 本周期键盘已落地且效果在跑：同一熄灯周期的恢复会相继触发（重试/连接/唤醒），
                // 不再重复写电源或重进帧模式（避免闪烁）。
                if (appliedThisCycle && rgb.IsConnected && rgb.ActiveMode == rgb.KbHidMode)
                {
                    MarkKeyboardCustomStatusBaseline();
                    return true;
                }

                if (hw is { IsConnected: true } && service is not null)
                {
                    if (!appliedThisCycle && LightingState.ShouldRestoreKeyboardPower(
                            rgb.KbPowerOn, temporaryPowerOff, hw.KeyboardPower))
                    {
                        // 只下发不等回读：GCU 键盘电源回读本机长期 not confirmed，等待它会凭空给
                        // 本地 HID 效果加上约 2 秒延迟，与外置通道错开。命令成功发布即继续。
                        bool issued = await service.PublishLightPower("Keyboard/Ctrl", true).ConfigureAwait(false);
                        service.ObserveLightPower("Keyboard/Ctrl", true);
                        if (issued) await Task.Delay(KeyboardPowerSettleMs).ConfigureAwait(false);
                        else Logger.WriteLine("RGB 自动恢复：键盘电源未能下发，继续恢复本地 HID 效果");
                    }
                    // 设备侧计时关闭：睡眠由应用内空闲检测实现（每个恢复周期只需设置一次）。
                    // 只下发不等确认——这条设置的回读同样可能永远不确认（本机 GCU 既有现象），
                    // await 它会把本地 HID 效果再推迟约 2 秒，正是两条路径错位的第二个来源。
                    if (!appliedThisCycle) _ = service.SwitchCloseTimer(0);
                }
                if (!forceEffectRestore && rgb.IsConnected && rgb.ActiveMode == rgb.KbHidMode)
                {
                    Volatile.Write(ref _keyboardRestoreAppliedRequestId, requestId);
                    MarkKeyboardCustomStatusBaseline();
                    return true;
                }

                bool wasConnected = rgb.IsConnected;
                if (!wasConnected && !rgb.Connect()) return false;
                if (wasConnected && forceEffectRestore && !rgb.ReInitCustomMode())
                {
                    // 固件抢占后 HID 句柄可能仍存在但已不能接收帧，重新枚举一次设备。
                    if (!rgb.Connect()) return false;
                }
                rgb.StartMode(rgb.KbHidMode);
                MarkKeyboardCustomStatusBaseline();
                Interlocked.Exchange(ref _keyboardPowerTemporarilySuspended, 0);
                Volatile.Write(ref _keyboardRestoreAppliedRequestId, requestId);
                Logger.WriteLine($"RGB 自动恢复：HID 效果 {rgb.KbHidMode} 已运行");
                return true;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("RGB 自动恢复异常: " + ex.Message);
                return false;
            }
            finally { _keyboardRestoreLock.Release(); }
        }


        private static void SystemEvents_SessionEnding(object sender, SessionEndingEventArgs e)
        {
            // 注销/关机/重启时兜底还原亮度（best-effort，不能因为一个 Win32 调用失败而中断会话结束流程）。
            TryRestoreScreenAfterFailure();
            gpuControl.StandardModeFix();
            modeControl.ShutdownReset();
            BatteryControl.AutoBattery();
            InputDispatcher.ShutdownStatusLed();
        }

        private static void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLogon || e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect)
            {
                Logger.WriteLine("Session:" + e.Reason.ToString());
                ProcessHelper.KillSmartDisplayControl();
                bool wasLocked = Aura.sessionLock;
                Aura.sessionLock = false;
                ScreenControl.AutoScreen();
                Aura.ApplyAura();
                if (wasLocked) _ = ReapplyCpuTempAfterUnlockAsync();
            }
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                Logger.WriteLine("Session:" + e.Reason.ToString());
                Aura.sessionLock = true;
            }
        }

        private static async Task ReapplyCpuTempAfterUnlockAsync()
        {
            try
            {
                await Task.Delay(2000).ConfigureAwait(false);
                if (Math.Abs(DateTimeOffset.Now.ToUnixTimeMilliseconds() - lastAuto) < 10000) return;
                modeControl.AutoCPUTemp();
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Session unlock CPU temperature reapply failed: " + ex.Message);
            }
        }

        static void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {

            if (Math.Abs(DateTimeOffset.Now.ToUnixTimeMilliseconds() - lastTheme) < 2000) return;

            switch (e.Category)
            {
                case UserPreferenceCategory.General:
                    bool changed = settingsForm.InitTheme();
                    settingsForm.InitContextMenuTheme();
                    settingsForm.VisualiseIcon(true);
                    settingsForm.VisualiseFnLock();
                    settingsForm.VisualiseBatteryFull();

                    if (changed)
                    {
                        Debug.WriteLine("Theme Changed");
                        lastTheme = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                    }

                    if (settingsForm.updatesForm is not null && settingsForm.updatesForm.Text != "")
                        settingsForm.updatesForm.InitTheme();

                    break;
            }
        }



        public static bool SetAutoModes(bool powerChanged = false, bool init = false, bool wakeup = false)
        {
            // A settled AC/DC transition is already debounced by powerSettleTimer
            // and must never be discarded by the generic initialization throttle.
            int skipDelay = powerChanged ? 250 : wakeup ? 10000 : 3000;

            if (init) gpuControl.CaptureNvBootState();

            lock (autoLock)
            {
                if (Math.Abs(DateTimeOffset.Now.ToUnixTimeMilliseconds() - lastAuto) < skipDelay) return false;
                lastAuto = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            }

            PowerSource detectedSource = ReadPowerSource();
            // 首次观测只建立基线：没有「上一次」可比，就不存在变化。
            bool sourceChanged = lastObservedSource.HasValue && detectedSource != lastObservedSource.Value;
            lastObservedSource = detectedSource;
            Logger.WriteLine($"AutoSetting for {SystemInformation.PowerStatus.PowerLineStatus} (source={detectedSource}, changed={sourceChanged})");

            if ((init || powerChanged || wakeup) &&
                (hw is not { IsConnected: true } || service is null))
            {
                int configuredMode = AppConfig.Get("performance_" + PerformanceKey());
                _pendingStartupPerformanceMode = Modes.Exists(configuredMode)
                    ? configuredMode
                    : Modes.GetCurrent();
            }

            BatteryControl.AutoBattery(init);
            if (init) InputDispatcher.InitScreenpad();
            DynamicLightingHelper.Init();
            ScreenControl.InitOptimalBrightness();

            
            //HardwareControl.ReadSensors(true);

            if (ShouldReapplyPerformanceMode(powerChanged, wakeup, sourceChanged))
                modeControl.AutoPerformance(powerChanged || sourceChanged, preserveActiveCustom: init || wakeup);
            else
                Logger.WriteLine("Display wake without a power-source change; preserving the active performance mode.");

            InputDispatcher.InitStatusLed();
            if (init) NumberPad.Init();

            InputDispatcher.AutoKeyboard();

            bool switched = gpuControl.AutoGPUMode(delay: 1000);
            if (!switched)
            {
                gpuControl.InitGPUMode();
                ScreenControl.AutoScreen();
            }

            ScreenControl.InitMiniled();
            // VisualControl.InitBrightness() 已删除（连同整个 ASUS Splendid 通路）。
            // 它调的是 Splendid 的 gamma 假调光，不是屏幕亮度——后者走
            // Settings/DeviceSwitchItemStatus 的 ScreenBrightness + BrightnessCommitQueue。

            return true;
        }

        internal static bool ShouldReapplyPerformanceMode(bool powerChanged, bool wakeup, bool sourceChanged) =>
            powerChanged || sourceChanged || !wakeup;

        internal static bool ShouldReapplyPendingPerformanceMode(int pendingMode, int selectedMode) =>
            pendingMode >= 0 && pendingMode == selectedMode;

        public enum PowerSource { Battery, Barrel, USBC }

        /// <summary>
        /// 上一次观测到的供电方式。
        ///
        /// 这里曾经硬编码初始值为 <see cref="PowerSource.Battery"/>，于是插着电的机器在
        /// 第一次 SetAutoModes 时必然算出 sourceChanged=true —— 一次并不存在的「电源切换」，
        /// 白白多发一轮模式命令与 Windows 电源方案写入。<see cref="PowerSource"/> 未知用
        /// null 表示，首次观测只是建立基线，不算变化。
        /// </summary>
        public static PowerSource? lastObservedSource;

        /// <summary>兼容既有调用方：未观测过时按当前实际供电方式回答，不再默认电池。</summary>
        public static PowerSource currentSource
        {
            get => lastObservedSource ?? ReadPowerSource();
            set => lastObservedSource = value;
        }
        private static PowerLineStatus lastLineStatus = SystemInformation.PowerStatus.PowerLineStatus;
        private static readonly System.Timers.Timer powerSettleTimer = new() { AutoReset = false };

        public static PowerSource ReadPowerSource()
        {
            if (SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Online)
                return PowerSource.Battery;

            int chargerMode = acpi?.DeviceGet(AsusACPI.ChargerMode) ?? 0;
            if (chargerMode > 0 && (chargerMode & AsusACPI.ChargerBarrel) == 0)
                return PowerSource.USBC;

            return PowerSource.Barrel;
        }

        public static bool usbcProfile = AppConfig.Is("usbc_profile");

        public static int PerformanceKey() =>
            usbcProfile ? (int)ReadPowerSource() : (int)SystemInformation.PowerStatus.PowerLineStatus;

        /// <summary>SystemEvents/定时器线程 → UI 线程封送（跨线程直接改控件在 Release 下表现为内存竞争/偶发崩溃）。</summary>
        static void OnSystemEvent(Action action)
        {
            try
            {
                if (settingsForm is { IsDisposed: false } && settingsForm.InvokeRequired)
                    settingsForm.BeginInvoke(action);
                else action();
            }
            catch (Exception ex) { Logger.WriteLine("System event dispatch failed: " + ex.Message); }
        }

        public static void SchedulePowerCheck()
        {
            if (AppConfig.Is("disable_power_event")) return;
            powerSettleTimer.Interval = Math.Max(AppConfig.Get("charger_delay"), 2000);
            powerSettleTimer.Stop();
            powerSettleTimer.Start();
        }

        private static void OnPowerSettled(object? sender, System.Timers.ElapsedEventArgs e)
        {
            PowerSource source = ReadPowerSource();
            if (source == currentSource) return;

            Logger.WriteLine($"Power source: {currentSource} -> {source}");
            currentSource = source;
            SetAutoModes(powerChanged: true);
            _ = ReconcileLightingPowerAsync();
        }

        public static void OnChargerEvent() => SchedulePowerCheck();

        private static void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                Logger.WriteLine("Power Mode Changed:" + e.Mode.ToString());
                gpuControl.StandardModeFix();
                modeControl.ShutdownReset();
                InputDispatcher.ShutdownStatusLed();
                return;
            }

            PowerLineStatus status = SystemInformation.PowerStatus.PowerLineStatus;
            if (status != lastLineStatus)
            {
                lastLineStatus = status;
                Logger.WriteLine($"Power Mode {e.Mode}: {status}");
            }

            if (e.Mode == PowerModes.Resume)
                ScheduleKeyboardLightingRestoreAfterResume();

            SchedulePowerCheck();
        }

        static void ScheduleKeyboardLightingRestoreAfterResume()
        {
            if (Interlocked.Exchange(ref _resumeKeyboardRestorePending, 1) != 0) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1200).ConfigureAwait(false);
                    Interlocked.Increment(ref _lightingRestoreRequestId);   // 系统唤醒后允许把外置灯效重新落地一次
                    await RestoreLightingWithRetryAsync(force: true).ConfigureAwait(false);
                }
                catch (Exception ex) { Logger.WriteLine("RGB 唤醒恢复失败: " + ex.Message); }
                finally { Volatile.Write(ref _resumeKeyboardRestorePending, 0); }
            });
        }

        public static void SettingsToggle(bool checkForFocus = true, bool trayClick = false)
        {
            if (settingsForm.Visible)
            {
                // If helper window is not on top, this just focuses on the app again
                // Pressing the ghelper button again will hide the app
                if (checkForFocus && !settingsForm.HasAnyFocus(trayClick) && !AppConfig.Is("topmost"))
                {
                    settingsForm.ShowAll();
                }
                else
                {
                    settingsForm.HideAll();
                }
            }
            else
            {
                // Keep the existing backing surface on its current monitor. Moving a hidden
                // top-level window to the primary display during Show() produces a visible
                // intermediate DWM frame on multi-monitor systems.
                var screen = Screen.FromRectangle(settingsForm.Bounds);

                settingsForm.WindowState = FormWindowState.Normal;
                settingsForm.VisualiseGPUMode();
                settingsForm.ShowAll(screen.WorkingArea);
                if (_showGuideOnFirstRestore)
                {
                    _showGuideOnFirstRestore = false;
                    settingsForm.BeginInvoke(settingsForm.ShowFirstRunGuideIfNeeded);
                }
            }
        }

        static void TrayIcon_MouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                SettingsToggle(trayClick: true);

        }

        static void TrayIcon_MouseMove(object? sender, MouseEventArgs e)
        {
            settingsForm.RefreshSensors();
        }

        /// <summary>退出/会话结束/未处理异常共用的亮度兜底：绝不抛，息屏状态下一律还原。</summary>
        internal static void TryRestoreScreenAfterFailure()
        {
            try { ScreenBlankController.RestoreImmediate(); }
            catch (Exception ex) { Debug.WriteLine("Screen blank restore on failure failed: " + ex.Message); }
        }

        static void OnExit(object sender, EventArgs e)
        {
            if (Interlocked.Exchange(ref _exitStarted, 1) != 0) return;
            TryRestoreScreenAfterFailure();
            _telemetryRecoveryTimer?.Dispose();
            _telemetryRecoveryTimer = null;
            _lightingIdleTimer?.Dispose();
            _lightingIdleTimer = null;
            powerSettleTimer.Stop();               // 退出竞态：停用前会在 ThreadPool 上继续写已 Dispose 的 trayIcon
            powerSettleTimer.Dispose();
            _trayRetryTimer?.Stop();
            _trayRetryTimer?.Dispose();
            _trayRetryTimer = null;
            settingsForm.StopRuntimeTimers();
            hardwareOverlay?.StopOverlay();  // 停 Overlay 定时器与窗口钩子（ETW 已移除；防御性确保干净退出）
            hardwareOverlay?.Dispose();
            rgb?.Dispose();   // 停止灯效 + 清屏 + 释放 HID
            ble?.Dispose();
            HardwareControl.lhm?.Dispose();   // 释放功耗传感器
            // 退出前告诉服务端停止推流。官方在窗口关闭/失活时都会发这一条
            // （CCUWinUI 共 7 处：MainWindow_Closed、Cleanup、窗口 Deactivated 等）。
            // 不发的话我们这边进程都结束了，GCU 还在按 5s 周期采集并推送传感器数据，
            // 白耗 CPU 和电。必须在 hw.Dispose() 之前——之后连接就断了。
            StopTelemetryBeforeExit();
            if (hw is not null) hw.StateChanged -= OnHardwareStateChanged;
            hw?.Dispose();
            modeControl?.Dispose();
            OfficialConsoleIsolation.StopGuard();

            if (trayIcon is not null)
            {
                Icon? trayImage = trayIcon.Icon;
                trayIcon.Icon = null;
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayImage?.Dispose();
            }

            PeripheralsProvider.UnregisterForDeviceEvents();
            NativeMethods.UnregisterPowerSettingNotification(unRegPowerNotify);
            NativeMethods.UnregisterPowerSettingNotification(unRegPowerNotifyLid);
            NativeMethods.UnregisterPowerSettingNotification(unRegPowerNotifyEnergy);
            NativeMethods.UnregisterSuspendResumeNotification(unRegSuspendResume);
            AppConfig.Shutdown();
            Logger.Close();
        }

        /// <summary>
        /// 尽力发出 <c>System/Control {Action=System_OFF}</c>，让 GCU 停止推送遥测。
        ///
        /// 退出路径不能 await（<see cref="Application.ApplicationExit"/> 是同步的），
        /// 也不能无限等：broker 可能已经先于我们关掉。给一个短超时，超时就放弃——
        /// 停推流是清理动作，为它拖慢退出不值得。
        /// </summary>
        static void StopTelemetryBeforeExit()
        {
            if (hw is not { IsConnected: true } connected) return;
            try
            {
                // QoS0：这一条要在 700ms 内落地，而 QoS2 要走四步握手，超时之后
                // hw.Dispose() 会断开会话，CleanSession=true 让未完成的 PUBREL 被丢弃，
                // 命令等于没发——正是这段代码要解决的问题。停推流丢一次的代价
                // 远低于为一个清理动作卡住退出。
                var publish = connected.Publish("System/Control",
                    new Dictionary<string, object> { ["Action"] = "System_OFF" },
                    MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce);
                if (!publish.Wait(TimeSpan.FromMilliseconds(700)))
                {
                    Logger.WriteLine("System_OFF timed out on exit; skipping.");
                    // 超时后没人再观察这个 Task。它稍后抛异常会走
                    // TaskScheduler.UnobservedTaskException，而那时 Logger.Close() 很可能
                    // 已经执行，finalizer 线程上的日志写入会失败。
                    _ = publish.ContinueWith(
                        t => _ = t.Exception,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                }
            }
            catch (Exception ex) { Logger.WriteLine("System_OFF on exit failed: " + ex.Message); }
        }

        static async Task<bool> ApplyBatteryLimitAtBootAsync()
        {
            try
            {
                int limit = AppConfig.Get("charge_limit", 100);
                int mode = limit >= 95 ? 0 : limit >= 80 ? 1 : 2;   // 95+→性能(满充) 80-94→平衡 <80→健康
                // 必须用辅助 client id：这个进程由计划任务在开机时拉起，会和常驻托盘进程
                // 重叠。共用同一个 id 时 broker 按协议要踢掉已有会话，于是两个进程互踢，
                // 限充失败而托盘那边每次重连都触发一整轮状态重放。
                using var chargeHw = new MechrevoLite.Hardware.MechrevoHw(
                    MechrevoLite.Hardware.MechrevoHw.HelperClientId);
                if (!await chargeHw.ConnectAsync())
                {
                    Logger.WriteLine("BatteryLimit failed: MQTT broker unavailable");
                    return false;
                }

                await chargeHw.SetBatteryProtection(mode);
                for (int i = 0; i < 10; i++)
                {
                    await chargeHw.Publish("BatteryProtection/Control", new Dictionary<string, object> { ["Report"] = "GET" });
                    await Task.Delay(300);
                    if (chargeHw.BatteryProtection == mode)
                    {
                        Logger.WriteLine($"BatteryLimit confirmed: limit={limit} mode={mode}");
                        return true;
                    }
                }
                Logger.WriteLine($"BatteryLimit not confirmed: limit={limit} expected={mode} actual={chargeHw.BatteryProtection}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("BatteryLimit fail: " + ex.Message);
                return false;
            }
        }

        static void CleanupLegacyFiles()
        {
            string appDir = Path.GetDirectoryName(Application.ExecutablePath) ?? "";
            string[] legacyFiles = ["WinRing0x64.sys", "WinRing0x64.dll"];

            foreach (string fileName in legacyFiles)
            {
                string filePath = Path.Combine(appDir, fileName);
                if (File.Exists(filePath))
                {
                    try
                    {
                        File.Delete(filePath);
                        Logger.WriteLine($"Deleted legacy file: {fileName}");
                    }
                    catch (Exception ex)
                    {
                        Logger.WriteLine($"Failed to delete legacy file {fileName}: {ex.Message}");
                    }
                }
            }
        }

    }
}
