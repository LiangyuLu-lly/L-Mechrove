using MechrevoLite.Gpu;
using MechrevoLite.Gpu.NVidia;
using MQTTnet;
using MQTTnet.Formatter;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace MechrevoLite.Hardware;

/// <summary>
/// Mechrevo 硬件门面：同步 API + 快照缓存，供 G-Helper 风格 UI 轮询调用。
/// 内部走 MQTT（Phase 1），Phase 2 换直连实现时接口不变。
/// 载荷/主题依据 docs/superpowers/plans/m1-findings.md 实测。
/// </summary>
public class MechrevoHw : IDisposable
{
    // 提权判定只取一次：进程提权状态运行期不变。NVAPI 读取不需要提权，写入需要。
    static readonly bool IsElevatedProcess = new System.Security.Principal.WindowsPrincipal(
        System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public const int GpuCoreOffsetUserLimit = 500;
    // GCU 硬边界的实测值（2026-09-10 本机 Probe 逐点验证，GCU 自己上报的范围不可信）：
    // 核心 250 生效/300 被静默丢弃；显存 2000 生效/2200 被丢、-1000 生效/-1200 被丢。
    // GCU 上报显存 Maximum=1000/HWOC=1800，实测都能到 2000——上报字段只配当参考。
    const int GcuFallbackCoreOffsetMinimum = 0;
    const int GcuFallbackCoreOffsetMaximum = 250;
    const int GcuFallbackMemoryOffsetMinimum = -1000;
    const int GcuFallbackMemoryOffsetMaximum = 2000;
    const string Host = "127.0.0.1";
    const int Port = 13688;
    // GCU rejects arbitrary client IDs. The official UI occupies slot 5; slot 4 keeps L-Mechrevo compatible and separate.
    const string User = "UWPClient_User_4";
    const string Pwd = "UWPClient_Pwd888881772688_4";
    readonly string _clientId;
    internal string ClientId => _clientId;

    /// <summary>
    /// 常驻实例的 client id。GCU 拒绝任意 client id，只认它自己那几个 slot；
    /// 官方 UI 占 slot 5，我们用 slot 4。
    /// </summary>
    internal const string PrimaryClientId = "UWPClient_4";

    /// <summary>
    /// 短命辅助实例（开机限充任务）的 client id。
    ///
    /// **必须与常驻实例不同**。按 MQTT 3.1.1 §3.1.4，broker 收到同 id 的新连接必须踢掉
    /// 已有会话：限充任务连上就把托盘进程踢掉，托盘重连又把限充任务踢掉，两个进程互踢，
    /// 直到限充那 10 轮轮询跑完。附带损失是限充大概率失败，而托盘每次重连都会
    /// generation++ 触发一整轮状态重放（RefreshAll + 灯效恢复 + AutoPerformance）。
    ///
    /// slot 3 与 slot 5 之外的号在实测中同样被接受；这里选 3 是因为 5 已被官方 UI 占用。
    /// </summary>
    internal const string HelperClientId = "UWPClient_3";

    readonly MqttClientFactory _factory = new();
    IMqttClient? _client;
    readonly Func<string, object, Task>? _publishOverride;
    readonly Func<IGpuOverclockControl?>? _gpuOverclockFactory;
    readonly IGpuOverclockElevatedApplier? _elevatedGpuOverclockApplier;
    readonly object _gpuOverclockLock = new();
    IGpuOverclockControl? _gpuOverclock;
    bool? _directGpuOverclockEnabled;
    int? _elevatedGpuCoreReadback;
    int? _elevatedGpuMemoryReadback;
    readonly bool _persistDirectGpuOverclock;
    /// <summary>
    /// 进程提权判定接缝。生产取 <see cref="IsElevatedProcess"/>（进程运行期不变）；
    /// 测试注入固定值，避免依赖测试宿主的真实令牌。
    /// </summary>
    readonly Func<bool> _isProcessElevated;
    // volatile：Dispose 由 UI 线程写，ConnectAsync/ReconnectLoopAsync 由后台线程读。
    // 过去是普通 bool，Dispose 之后仍可能有一轮连接完成并建立新会话。
    volatile bool _disposed;
    /// <summary>Dispose 的原子闸门。<see cref="_disposed"/> 供各处快速读，这个只用于 CAS。</summary>
    int _disposedFlag;
    int _reconnecting;
    int _connectionGeneration;
    long _lastTelemetryTick;
    readonly SemaphoreSlim _connectLock = new(1, 1);
    readonly SemaphoreSlim _controlLock = new(1, 1);
    PendingModeSwitch? _pendingModeSwitch;
    long _settingStatusVersion;
    long _colorCalibrationStatusVersion;
    long _gpuModeStatusVersion;
    long _gpuSwitchResultVersion;
    long _fanStatusVersion;
    long _keyboardStatusVersion;
    long _lcStatusVersion;
    long _lcStatusReceivedAt;
    long _cpuInfoReceivedAt;
    long _gpuInfoReceivedAt;
    int _lcConsecutiveMeterFaults;

    /// <summary>
    /// 模式切换的过期包过滤窗口。期望模式与截止时刻必须一起读写：
    /// 过去它们是两个独立字段（Volatile.Write + Interlocked.Exchange），
    /// UI 线程连点两个模式按钮时，MQTT 线程可能读到「旧的期望模式 + 新的截止时刻」，
    /// 于是把真实到达的新模式当成过期包丢弃，最长 8 秒。
    /// </summary>
    sealed record PendingModeSwitch(int ExpectedOperatingMode, long ExpiresAtTick);

    internal const int ModeSwitchPendingWindowMs = 8000;

    /// <summary>服务层模式切换前调用：切换窗口内忽略旧命令的乱序模式回报。</summary>
    public void MarkModeSwitchPending(int expectedOperatingMode) =>
        // GCU can deliver queued pre-switch Fan/Status packets for several seconds
        // on busy 50-series systems. Keep the expected mode authoritative long
        // enough to reject those stale packets after a confirmed user switch.
        Interlocked.Exchange(
            ref _pendingModeSwitch,
            new PendingModeSwitch(expectedOperatingMode, Environment.TickCount64 + ModeSwitchPendingWindowMs));

    /// <summary>
    /// 解除过期包过滤窗口。命令没能真正发出去时必须调用：
    /// 过去 SetMode 先武装再发布，Publish 在断线时抛异常后窗口仍然生效，
    /// 这 8 秒内一切真实模式上报（含用户按厂商 Fn 热键、GCU 回滚）都被静默丢弃，
    /// UI 会显示一个从未下发成功的模式。
    /// 注意：确认成功后**不**清除窗口——那是有意设计，用于继续拒绝仍在途的切换前旧包。
    /// </summary>
    internal void ClearModeSwitchPending() => Interlocked.Exchange(ref _pendingModeSwitch, null);

    /// <summary>当前是否处于过滤窗口内，以及窗口期望的模式。两个值原子取出。</summary>
    internal (bool Active, int ExpectedOperatingMode) GetModeSwitchPendingState()
    {
        PendingModeSwitch? pending = Volatile.Read(ref _pendingModeSwitch);
        if (pending is null) return (false, -1);
        return (Environment.TickCount64 <= pending.ExpiresAtTick, pending.ExpectedOperatingMode);
    }

    public MechrevoHw() : this(null, null, null, NvidiaGpuControl.TryCreateOverclockControl)
    {
    }

    /// <summary>
    /// 用指定的 client id 创建实例。只给短命辅助进程用（见 <see cref="HelperClientId"/>）——
    /// 与常驻实例共用同一个 id 会让 broker 互踢两边的会话。
    /// </summary>
    internal MechrevoHw(string clientId)
        : this(null, null, null, NvidiaGpuControl.TryCreateOverclockControl, null, clientId)
    {
    }

    internal MechrevoHw(Func<string, object, Task>? publishOverride)
        : this(publishOverride, null, null, gpuOverclockFactory: null)
    {
    }

    internal MechrevoHw(
        Func<string, object, Task>? publishOverride,
        MechrevoDeviceCapabilities? capabilities)
        : this(publishOverride, capabilities, null, gpuOverclockFactory: null)
    {
    }

    internal MechrevoHw(
        Func<string, object, Task>? publishOverride,
        MechrevoDeviceCapabilities? capabilities,
        IGpuOverclockControl? gpuOverclock)
        : this(publishOverride, capabilities, gpuOverclock, gpuOverclockFactory: null)
    {
    }

    internal MechrevoHw(
        Func<string, object, Task>? publishOverride,
        MechrevoDeviceCapabilities? capabilities,
        IGpuOverclockControl? gpuOverclock,
        IGpuOverclockElevatedApplier elevatedGpuOverclockApplier)
        : this(publishOverride, capabilities, gpuOverclock, gpuOverclockFactory: null, elevatedGpuOverclockApplier)
    {
    }

    /// <summary>测试接缝：注入进程提权判定，验证「非提权 ⇒ 超频不可编辑且不写不弹 UAC」。</summary>
    internal MechrevoHw(
        Func<string, object, Task>? publishOverride,
        MechrevoDeviceCapabilities? capabilities,
        IGpuOverclockControl? gpuOverclock,
        IGpuOverclockElevatedApplier? elevatedGpuOverclockApplier,
        Func<bool> isProcessElevated)
        : this(publishOverride, capabilities, gpuOverclock, gpuOverclockFactory: null,
            elevatedGpuOverclockApplier, null, isProcessElevated)
    {
    }

    /// <summary>测试接缝：带直连后端工厂与提权判定（用于启动恢复档位路径）。</summary>
    internal MechrevoHw(
        Func<string, object, Task>? publishOverride,
        MechrevoDeviceCapabilities? capabilities,
        Func<IGpuOverclockControl?> gpuOverclockFactory,
        Func<bool> isProcessElevated)
        : this(publishOverride, capabilities, null, gpuOverclockFactory, null, null, isProcessElevated)
    {
    }

    MechrevoHw(
        Func<string, object, Task>? publishOverride,
        MechrevoDeviceCapabilities? capabilities,
        IGpuOverclockControl? gpuOverclock,
        Func<IGpuOverclockControl?>? gpuOverclockFactory,
        IGpuOverclockElevatedApplier? elevatedGpuOverclockApplier = null,
        string? clientId = null,
        Func<bool>? isProcessElevated = null)
    {
        _clientId = clientId ?? PrimaryClientId;
        _publishOverride = publishOverride;
        _gpuOverclock = gpuOverclock;
        _gpuOverclockFactory = gpuOverclockFactory;
        _elevatedGpuOverclockApplier = elevatedGpuOverclockApplier;
        _persistDirectGpuOverclock = gpuOverclockFactory is not null;
        _isProcessElevated = isProcessElevated ?? (static () => IsElevatedProcess);
        Capabilities = capabilities ?? MechrevoDeviceCapabilities.Load();
        // 构造函数即加载本地默认曲线（不依赖 MQTT 连接，保证 UI 曲线图始终有数据）
        LoadDefaultCurveFromDisk();
    }

    // ---- 快照缓存（MQTT 消息更新，UI getter 读取）----
    public int CpuTemp { get; private set; }
    public int GpuTemp { get; private set; }
    public int CpuUsage { get; private set; }
    public int GpuUsage { get; private set; }
    public int CpuFrequency { get; private set; }   // MHz
    public int GpuCoreFreq { get; private set; }    // MHz
    public int VramUsedMb { get; private set; }     // System/GpuInfo GpuMem
    public int RamUsage { get; private set; }       // System/MemoryInfo MemoryUsage %
    public double RamUsedGb { get; private set; }   // System/MemoryInfo TotalUsingMemory GB
    public int CpuFanRpm { get; private set; }
    public int GpuFanRpm { get; private set; }
    public int CpuFanDuty { get; private set; }

    /// <summary>
    /// 第三颗风扇的读数在这套协议里**不存在**，所以这里恒为 false。
    ///
    /// 「中置风扇」是从 g-helper 继承来的华硕概念（部分 ROG 机型有一颗中置风扇）。
    /// 机械革命侧没有对应的遥测：System/FanInfo 只有 CPU/GPU 的占空比与转速，
    /// 官方界面（全机型共用）也只显示两颗。内存风扇虽然存在，但 EC 侧只有风扇表
    /// 寄存器、没有转速寄存器，且由 GCUService 随风扇表自动管理。
    ///
    /// 保留这个属性是为了让「不存在」这件事在代码里显式可见，并作为
    /// AsusACPI.IsMidFanSupported() 的判据来源——而不是让调用方各自去猜。
    /// </summary>
    public bool MidFanSeen => false;

    // ---- 电池健康（官方推流里有，官方 UI 不展示）----
    public int BatteryCycleCount { get; private set; } = -1;
    public bool BatteryAbnormal { get; private set; }
    public string BatteryCapacityText { get; private set; } = "";

    // ---- 网络吞吐（官方推流，此前未订阅）----
    public string NetworkDownload { get; private set; } = "";
    public string NetworkUpload { get; private set; } = "";
    public bool NetworkInfoSeen { get; private set; }

    // ---- 机型/固件铭牌与风扇异常告警 ----
    public bool HardwareInfoSeen { get; private set; }
    public string EcFirmwareVersion { get; private set; } = "";
    public bool FanErrorSeen { get; private set; }
    public bool FanError { get; private set; }
    public int GpuFanDuty { get; private set; }
    public int BatteryPercent { get; private set; }
    public bool IsAC { get; private set; }
    public bool FanBoost { get; private set; }   // Fan/Status FanBoostEnable
    public System.Collections.Concurrent.ConcurrentDictionary<string, bool> QuickSwitches { get; } = new();   // 快捷开关状态（MQTT 线程写、UI 线程读——必须并发安全）
    public bool UsbCharger { get; private set; }   // Setting/Status UsbCharger
    public int DeepSleepTime { get; private set; }   // Setting/Status DeepSleepTime（秒 900-1800）
    public int CloseTimerMinutes { get; private set; } = -1;   // Setting/Status CloseTimer（键盘灯无操作熄灭分钟数，0=关闭，-1=未知）
    public event Action<int>? CloseTimerChanged;

    // ---- 当前机型能力：官方 ItemSupport 静态画像 + MQTT 运行时纠偏 ----
    public MechrevoDeviceCapabilities Capabilities { get; }
    public bool FanStatusSeen { get; private set; }
    public bool FanCurveSeen { get; private set; }
    public bool SettingStatusSeen { get; private set; }
    public bool GpuDeviceStatusSeen { get; private set; }
    public bool DeviceSwitchStatusSeen { get; private set; }
    public bool LchwocStatusSeen { get; private set; }
    public bool KeyboardStatusSeen { get; private set; }
    public bool LightbarStatusSeen { get; private set; }
    public bool LogoLightStatusSeen { get; private set; }

    // ---- HidLightbar/Status 自带的子灯带能力位 ----
    // 官方就是从这条状态里读 LogoSupport/HingeSupport/BaseSupport/NewlogoSupport 来决定
    // 灯效页显示哪几个子灯带的（CCUWinUI 里再按 BIOS_PROJECT_ID 过滤一层）。
    // 我们不复刻那张机型表：机型名单会漏掉表上没有的机器，而这几个标志位是设备自己报的。
    public bool? LightbarLogoSupport { get; private set; }
    public bool? LightbarBaseSupport { get; private set; }
    public bool? LightbarNewLogoSupport { get; private set; }
    public bool? LightbarMbLogoSupport { get; private set; }

    // ---- 显示色彩模式与显示特性（Setting/Status，只读）----
    //
    // 这一族是「协议里有、官方 5.56 界面已经不用」的遗留能力，所以只解析、不下发。
    // 判断依据：在 CCUWinUI 里 DISPLAY_STANDARD_MODE / DISPLAY_*_MODE_VALUE_SAVE /
    // NV_CTRL_PANEL_* 都只出现在动作枚举的声明里，**没有任何发送点**；那批颜色字段
    // （DisplayMode / GamingBrightness / CutomizedRed / ReadColorTemp …）也没有任何读取点。
    // 全库唯一真被发出去的是 DISPLAY_FEATURE_STATUS_ON，而它已经在 ICC 校色流程里用了。
    // 详见 docs/hardware/README.md。
    /// <summary>DisplayMode，例如 DISPLAY_STANDARD_MODE。</summary>
    public string DisplayColorMode { get; private set; } = "";
    /// <summary>DisplayFeatureStatus，例如 DISPLAY_FEATURE_STATUS_ON。</summary>
    public bool? DisplayFeatureOn { get; private set; }
    /// <summary>
    /// DGpu，值是 NVIDIA 控制面板的全局首选显卡（NV_CTRL_PANEL_AUTOSELECT /
    /// NV_CTRL_PANEL_HIGHPERFORMANCE）。只读：这台机器的显卡模式由本程序自己那套
    /// Eco/标准/独显直连管理，再叠一层全局首选项会让两边的状态互相打架。
    /// </summary>
    public string NvControlPanelPreference { get; private set; } = "";
    /// <summary>
    /// 四种色彩模式各自的细分参数，键是官方状态里的字段名。
    /// 每种模式的参数集不一样（护眼模式只有亮度/蓝光/色温，游戏与自定义才有全部六项），
    /// 所以用字典而不是固定结构——官方就是这么分的。
    /// </summary>
    public IReadOnlyDictionary<string, int> DisplayColorParameters => _displayColorParameters;
    readonly Dictionary<string, int> _displayColorParameters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 官方 Setting/Status 里的色彩参数字段名。
    /// 注意 Cutomized 这个拼写：官方状态字段少了个 s，而对应的命令名是 Customized_*。
    /// 两边不一致，照抄命令名去读状态会全部读不到。
    /// </summary>
    /// <summary>
    /// 灯带状态载荷里有没有真实内容。
    ///
    /// 判据取得宽一些（任一可识别字段非空即算），因为各代固件报的字段不完全一样；
    /// 但空载荷必须判为「没有」——开发机实测服务端对不存在的同步灯带也会推
    /// 一个 type / powerStatus / brightNess 全空的状态，只看主题到没到会造出假入口。
    /// </summary>
    /// <summary>
    /// 运行时解析出来的能力判定汇总，一行文本。
    ///
    /// 日志里原本只有静态画像（注册表 ItemSupport），但真正决定界面显示什么的是
    /// 这些「静态位 + 服务端报过的字段」合并之后的结论。缺了这一行，排查
    /// 「为什么这台机器上没有某个入口」只能靠猜。
    /// </summary>
    internal string DescribeResolvedCapabilities()
    {
        string Yn(bool value) => value ? "Y" : "n";
        string Tri(bool? value) => value is null ? "-" : value.Value ? "Y" : "n";
        return "Resolved caps: " +
            $"kb={Yn(SupportsKeyboard)} lightbar={Yn(SupportsLightbar)} logo={Yn(SupportsLogoLight)} " +
            $"(reported logo={Tri(LightbarLogoSupport)} " +
            $"base={Tri(LightbarBaseSupport)} newLogo={Tri(LightbarNewLogoSupport)} mbLogo={Tri(LightbarMbLogoSupport)}; " +
            $"statusSeen lightbar={Yn(LightbarStatusSeen)} logo={Yn(LogoLightStatusSeen)}) " +
            $"| midFan={Yn(MidFanSeen)} fanBoost={Yn(SupportsFanBoost)} fanSwitchSpeed={Yn(SupportsFanSwitchSpeed)} " +
            $"whisper={Yn(SupportsWhisperMode)} powerLightBrightness={Yn(SupportsPowerLightBrightness)} " +
            $"pl1={Yn(Pl1Adjustable)} pl2={Yn(Pl2Adjustable)} pl4={Yn(Pl4Adjustable)} " +
            $"tcc={Yn(TccAdjustable)} tgp={Yn(GpuTgpAdjustable)} lc={Yn(SupportsLiquidCooling)} " +
            $"hz={Yn(SupportsDisplayRefresh)} calib={Yn(SupportsColorCalibration)}";
    }

    internal static bool HasLightbarContent(JObject o)
    {
        // 只认这两个字段，开发机实测逼出来的结论：
        //
        // 1. 同步灯带的载荷里 type / powerStatus / brightNess 全是**空字符串**，
        //    但 effect 与 light 有值——那是从保存的设置回显出来的，背后没有硬件。
        //    所以效果名和亮度档不能当证据。
        // 2. GCU 用同一个状态 DTO 发全部灯带主题，于是 LogoSupport /
        //    MBlogoSupport 这些**设备级**能力位出现在每一条载荷里（包括不存在的那条灯带）。
        //    「字段存在」因此毫无区分度，也不能当证据。
        //
        // 剩下真正能区分的就是 type（SKU 标识符，官方拿它决定显示哪套灯带界面）
        // 与 powerStatus（真实电源态）。
        foreach (string field in new[] { "type", "powerStatus" })
            if (!string.IsNullOrWhiteSpace(o.GetValue(field, StringComparison.OrdinalIgnoreCase)?.ToString()))
                return true;
        return false;
    }

    internal static readonly string[] DisplayColorParameterFields =
    [
        "GamingBrightness", "GamingRed", "GamingGreen", "GamingBlue", "GamingColorTemp", "GamingContrast",
        "VideoBrightness", "VideoColorTemp",
        "ReadBrightness", "ReadBlue", "ReadColorTemp",
        "CutomizedBrightness", "CutomizedRed", "CutomizedGreen", "CutomizedBlue",
        "CutomizedColorTemp", "CutomizedContrast",
    ];
    public bool LcStatusSeen { get; private set; }
    public bool ScreenBrightnessSeen { get; private set; }
    public bool TouchpadSeen { get; private set; }
    public bool WifiSeen { get; private set; }
    public bool BluetoothSeen { get; private set; }
    public bool WebcamSeen { get; private set; }
    public bool UsbChargerSeen { get; private set; }
    public bool FanBoostSeen { get; private set; }
    public bool DeepSleepSeen { get; private set; }
    public bool CopilotSeen { get; private set; }
    public bool AcRecoverySeen { get; private set; }
    public bool HighPerformanceSeen { get; private set; }
    public bool WinKeySeen { get; private set; }
    public bool FnKeySeen { get; private set; }
    public bool OsdSeen { get; private set; }
    public bool NumpadSeen { get; private set; }

    // ---- 官方有、此前未移植的开关 ----
    // 判据一律是「服务端真的报了这个字段」而不是机型名单：本项目要覆盖全系机型，
    // 而这些开关在不同机型上有无各异（例如开发机报 Uni/Omni/TouchpadToggle/SingleColorKBBL，
    // 但 NumPadSupport=0）。字段没出现就等于该机型没有这项硬件。
    public bool TouchpadToggleSeen { get; private set; }
    public bool SingleColorKbSeen { get; private set; }
    public bool UniOmniSeen { get; private set; }
    public bool PowerLightSeen { get; private set; }
    public int PowerLightBrightness { get; private set; } = -1;
    public bool GameWhitelistSeen { get; private set; }
    /// <summary>电池 Logo 灯（<c>BatteryLogo_Status</c> / <c>BATTERYLOGO_TOGGLE_*</c>）。</summary>
    public bool BatteryLogoSeen { get; private set; }
    public bool CpuAdvancedPerformanceSeen { get; private set; }
    /// <summary>
    /// Fan/Status 同帧上报的超频总开关支持位（<c>OcSupport</c>）。
    /// 对应官方的 <c>IsOcSettingsSupport</c>，只是官方从注册表读、这里从状态帧读。
    /// null = 服务端没报过，退回静态能力画像判断。
    /// </summary>
    public bool? OverclockMenuSupport { get; private set; }
    /// <summary>GPU Whisper（静音）模式：仅当服务端明确上报支持位时才算存在。</summary>
    public bool? WhisperModeSupport { get; private set; }
    /// <summary>GPU_WhisperModeSwitch（开关本身）。只读——见解析处的说明。</summary>
    public bool WhisperMode { get; private set; }
    public bool WhisperModeSeen { get; private set; }
    /// <summary>GPU_WhisperModeSetting（静音档位，对应三档最低帧率），不是开关。</summary>
    public int WhisperModeLevel { get; private set; } = -1;

    public bool FanRespectiveSeen { get; private set; }
    public bool ColorCalibrationSeen { get; private set; }
    public bool ColorCalibrationSwitchSeen { get; private set; }
    public bool ColorCalibrationModeSeen { get; private set; }
    public bool TccStatusSeen { get; private set; }
    public bool? DgpuDirectStatusSupport { get; private set; }
    public bool? IgpuOnlyStatusSupport { get; private set; }
    public bool? IgpuSwitchBlocked { get; private set; }
    public bool? LchwocSupportReported { get; private set; }
    public int GpuSwitchResult { get; private set; } = -1; // 1=混合/标准已落地，2=核显-only 已落地
    public bool GpuSwitchResultReported { get; private set; }
    public long GpuSwitchResultVersion => Interlocked.Read(ref _gpuSwitchResultVersion);
    internal int ConnectionGeneration => Volatile.Read(ref _connectionGeneration);
    /// <summary>重连循环是否在飞（断线后自动重试中）。GCU 状态指示条据此区分「连接中/未连接」。</summary>
    internal bool IsReconnecting => Volatile.Read(ref _reconnecting) != 0;

    // 能力判定分两类，务必区分：
    // 1) 显卡切换（下面三行）是**破坏性**操作，误报会让用户点到本机不具备的入口。
    //    运行时报告优先于静态画像，所以只接受两种证据：显式的 _Support 位，或者
    //    能被解析成具体模式的状态字符串（见 IsRecognizedDgpuDirectStatus）。
    // 2) 其余 Supports* 是**只读展示**的可见性开关，误报只会多显示一个区块。
    //    这类沿用宽松的运行时发现（*Seen），并由各自的控制路径二次把关，
    //    例如液冷的实际控制还要过 MechrevoService.CanControlLiquidCooling
    //    （LcActionSupported / LcGcuControllable）。
    public bool SupportsDgpuDirect => DgpuDirectStatusSupport ?? Capabilities.DgpuDirect;
    public bool SupportsIgpuOnly => IgpuOnlyStatusSupport ?? Capabilities.IgpuOnly;
    public bool SupportsGpuHotSwap => Capabilities.GpuHotSwap && SupportsIgpuOnly;

    /// <summary>
    /// **轴 2** dGPU 代际（运行时探测，见 <see cref="GpuGenerationProvider"/>）。
    /// <c>Unknown</c> 与 <c>NoDgpu</c> 都是独立状态，绝不等于任何具体代际。
    /// </summary>
    public DgpuGenerationKind DgpuGeneration => GpuGenerationProvider.Current().Generation;

    /// <summary>该显示路由动作是否被代际事实表允许；<c>Unknown</c>/<c>NoDgpu</c> 不套用具体代际的限制。</summary>
    public bool IsGpuActionAllowedByGeneration(string action) =>
        DisplayRoutePolicy.AllowsAction(DgpuGeneration, action);

    public bool SupportsKeyboard => Capabilities.Keyboard || KeyboardStatusSeen;
    public bool SupportsLightbar => Capabilities.Lightbar || LightbarStatusSeen;
    public bool SupportsLogoLight => Capabilities.LogoLight || LogoLightStatusSeen || LightbarLogoSupport == true;
    public bool SupportsLiquidCooling => Capabilities.LiquidCooling || LcStatusSeen;
    public bool SupportsDisplayRefresh => Capabilities.DisplayRefresh || (GpuDeviceStatusSeen && HzList.Count > 0);
    public bool SupportsColorCalibration => Capabilities.ColorCalibration || ColorCalibrationSeen;
    // 这两项的支持位一旦被服务端显式否掉，就一票否决静态能力画像：
    // 注册表里的 ItemSupport 是机型出厂画像，可能与实际装的屏不一致，
    // 而状态帧里的 *Support 是这台机器此刻的真实答案。
    public bool SupportsLocalDimming =>
        LocalDimmingSupport != false && (Capabilities.LocalDimming || LocalDimmingSeen);
    public bool SupportsLcdOverdrive =>
        LcdOverdriveSupport != false && (Capabilities.LcdOverdrive || LcdOverdriveSeen);
    public bool SupportsFanBoost => Capabilities.FanBoost || FanBoostSeen;
    public bool SupportsFanRespective => FanRespectiveSeen;
    public bool Pl1Adjustable => IsAdjustable(Pl1Minimum, Pl1Maximum);
    public bool Pl2Adjustable => IsAdjustable(Pl2Minimum, Pl2Maximum);
    public bool Pl4Adjustable => IsAdjustable(Pl4Minimum, Pl4Maximum);
    /// <summary>
    /// 风扇转换灵敏度。判据只有「服务端报过这两个字段」——静态能力位里没有对应项，
    /// 官方界面也是拿 Fan/Status 的回读决定这一块显不显示。
    /// </summary>
    public bool SupportsFanSwitchSpeed => FanSwitchSpeedSeen;
    /// <summary>
    /// 灵敏度的可写范围。GCU 不上报 Min/Max，范围由 EC 字段宽度推导：
    /// RamFan1p5_ECSpec.ADDR_TimAP_FanSwitchSpeedT100mSec 是单字节、单位 100 ms，
    /// 所以有效毫秒值是 100..25500 的百毫秒整数倍（开发机实测 1700）。
    /// 一旦将来固件真的上报了范围字段，解析会覆盖这两个值。
    /// </summary>
    public int FanSwitchSpeedMinimum { get; private set; } = FanSwitchSpeedStepMs;
    public int FanSwitchSpeedMaximum { get; private set; } = FanSwitchSpeedStepMs * 255;
    public const int FanSwitchSpeedStepMs = 100;
    public bool TccAdjustable => TccStatusSeen && IsAdjustable(TccMinimum, TccMaximum);
    public bool GpuTgpAdjustable => IsAdjustable(GpuTgpMinimum, GpuTgpMaximum);
    public bool GpuDynamicBoostAdjustable => IsAdjustable(GpuDbMinimum, GpuDbMaximum);
    // _gpuOverclock 会被 EnsureDirectGpuOverclock 和 Dispose 在 _gpuOverclockLock 内置 null。
    // 这几个属性过去无锁读取，与 GetMergedGpuOffsetRange 的「先检查后解引用」组合起来，
    // 会在 UI 属性读取路径上偶发 NullReferenceException，或在已释放对象上取值。
    public bool DirectGpuOverclockAvailable
    {
        get { lock (_gpuOverclockLock) return _gpuOverclock?.IsAvailable == true; }
    }
    internal bool DirectGpuOverclockBackendPresent
    {
        get { lock (_gpuOverclockLock) return _gpuOverclock is not null; }
    }
    public bool DirectGpuCoreRangeAvailable => TryGetDirectOffsetRange(core: true, out _);
    public bool DirectGpuMemoryRangeAvailable => TryGetDirectOffsetRange(core: false, out _);

    /// <summary>
    /// 在锁内一次性取出直连超频的可调范围。返回 false 表示后端不存在或该范围不可调。
    /// 调用方拿到的是值快照，不再持有对 _gpuOverclock 的引用，因此不存在
    /// 「检查通过之后字段被置 null」的窗口。
    /// </summary>
    bool TryGetDirectOffsetRange(bool core, [NotNullWhen(true)] out GpuClockOffsetRange? range)
    {
        lock (_gpuOverclockLock)
        {
            if (_gpuOverclock is null)
            {
                range = null;
                return false;
            }
            range = core ? _gpuOverclock.CoreOffset : _gpuOverclock.MemoryOffset;
            return range.IsAdjustable;
        }
    }
    public bool GcuGpuOverclockAvailable => IsConnected &&
        (LchwocSupportReported ?? (Capabilities.OverclockSettings ||
            IsOffsetAdjustable(GpuCoreOffsetMinimum, GpuCoreOffsetMaximum) ||
            IsOffsetAdjustable(GpuMemoryOffsetMinimum, GpuMemoryOffsetMaximum)));
    public int GpuCoreOffsetUserMinimum => Math.Max(GetMergedGpuOffsetRange(core: true).Minimum, -GpuCoreOffsetUserLimit);
    public int GpuCoreOffsetUserMaximum => Math.Min(GetMergedGpuOffsetRange(core: true).Maximum, GpuCoreOffsetUserLimit);
    public int GpuMemoryOffsetUserMinimum => GetMergedGpuOffsetRange(core: false).Minimum;
    public int GpuMemoryOffsetUserMaximum => GetMergedGpuOffsetRange(core: false).Maximum;
    public int EffectiveGpuCoreClockOffset
    {
        get
        {
            bool directActive;
            lock (_gpuOverclockLock) directActive = _directGpuOverclockEnabled == true;
            if (directActive) return GetEffectiveDirectGpuCoreOffset();
            // GCU 回显的是「存储值」：被 GCU 拒绝的值（如核心 >250）它照样回显请求值，
            // 而驱动 delta 读取不需要提权——驱动真值优先，读取失败才退回回显。
            if (TryGetDirectGpuOverclockReadback(out int core, out _)) return core;
            return GpuCoreClockOffset;
        }
    }
    public int EffectiveGpuMemoryClockOffset
    {
        get
        {
            bool directActive;
            lock (_gpuOverclockLock) directActive = _directGpuOverclockEnabled == true;
            if (directActive) return GetEffectiveDirectGpuMemoryOffset();
            if (TryGetDirectGpuOverclockReadback(out _, out int memory)) return memory;
            return GpuMemClockOffset;
        }
    }
    public bool GpuOverclockEnabled
    {
        get
        {
            // _directGpuOverclockEnabled 在 _gpuOverclockLock 内写（EnsureDirectGpuOverclock、
            // SelectGcuGpuOverclockBackend、提权写入回读），读取也必须持锁。
            bool? direct;
            lock (_gpuOverclockLock) direct = _directGpuOverclockEnabled;
            return direct ?? OcSwitch;
        }
    }
    public bool GpuCoreOffsetAdjustable => IsOffsetAdjustable(GpuCoreOffsetUserMinimum, GpuCoreOffsetUserMaximum);
    public bool GpuMemoryOffsetAdjustable => IsOffsetAdjustable(GpuMemoryOffsetUserMinimum, GpuMemoryOffsetUserMaximum);
    public bool SupportsGpuOverclock => DirectGpuCoreRangeAvailable || DirectGpuMemoryRangeAvailable ||
        ((LchwocSupportReported ?? (Capabilities.OverclockSettings ||
            GpuCoreOffsetAdjustable || GpuMemoryOffsetAdjustable)) &&
        (GpuCoreOffsetAdjustable || GpuMemoryOffsetAdjustable));

    /// <summary>
    /// GPU 超频写入是否必须提权：NVIDIA 直连（NVAPI）后端存在且进程未提权时为 true。
    /// NVAPI 的超频写入在非提权进程里恒定返回 NVAPI_INVALID_USER_PRIVILEGE——能读到
    /// 可调范围不等于能写。UI 据此显示「超频需要管理员权限」并提供以管理员身份重启，
    /// 而不是给出一个永远写不动的可编辑控件。
    /// </summary>
    public bool GpuOverclockRequiresElevation
    {
        get
        {
            lock (_gpuOverclockLock)
            {
                if (_gpuOverclock is null) return false;
                return _gpuOverclock.WritesRequireElevation &&
                    (_gpuOverclock.CoreOffset.IsAdjustable || _gpuOverclock.MemoryOffset.IsAdjustable);
            }
        }
    }

    /// <summary>GPU 超频此刻是否真的能写入并确认（能力支持且不需要提权）。</summary>
    public bool GpuOverclockWritable => SupportsGpuOverclock && !GpuOverclockRequiresElevation;
    public bool HasAnyCustomRange => Pl1Adjustable || Pl2Adjustable || TccAdjustable
        || GpuTgpAdjustable || GpuDynamicBoostAdjustable || GpuCoreOffsetAdjustable || GpuMemoryOffsetAdjustable;

    public int ColorCalibrationMode { get; private set; }
    public long FanStatusVersion => Interlocked.Read(ref _fanStatusVersion);

    public bool CanSwitchGpuMode(int mode) => mode switch
    {
        // 40 系常规机型只暴露 MUX 直连能力，原厂仍支持纯核显目标。
        MechrevoService.GpuIGpu => SupportsDgpuDirect || SupportsIgpuOnly,
        MechrevoService.GpuStandard => SupportsDgpuDirect || SupportsIgpuOnly,
        MechrevoService.GpuDgpu => SupportsDgpuDirect,
        MechrevoService.GpuAuto => SupportsIgpuOnly,
        _ => false,
    };

    /// <summary>
    /// 独显直连状态里可识别的命令态记法。这里列出的记法必须与
    /// <see cref="ResolveGpuModeStatus"/> 实际能解析的记法保持一致：
    /// 「能解析成具体模式」正是「这套命令族在本机存在」的唯一可靠证据。
    /// </summary>
    static readonly string[] DgpuDirectStatusTokens =
    {
        // ResolveGpuModeStatus 对 TOGGLE 变体走精确匹配，对不带 TOGGLE 的变体走子串匹配。
        // 注意 "DGPU_DIRECT_CONNECT_TOGGLE_ON" 并不包含 "DIRECT_CONNECT_ON"，两组都要列。
        "DIRECT_CONNECT_TOGGLE_ON", "DIRECT_CONNECT_TOGGLE_IGPU", "DIRECT_CONNECT_TOGGLE_OFF",
        "DIRECT_CONNECT_ON", "DIRECT_CONNECTION_ON",
        "DIRECT_CONNECT_IGPU",
        "DIRECT_CONNECT_OFF", "DIRECT_CONNECTION_OFF",
    };

    /// <summary>核显直通状态里可识别的命令态记法，约束同 <see cref="DgpuDirectStatusTokens"/>。</summary>
    static readonly string[] IgpuOnlyStatusTokens =
    {
        "IGPU_ONLY_CONNECT_RB_ON", "IGPU_ONLY_ON",
        "IGPU_ONLY_CONNECT_RB_AUTO", "IGPU_ONLY_AUTO",
        "IGPU_ONLY_CONNECT_RB_OFF", "IGPU_ONLY_OFF",
    };

    /// <summary>
    /// 状态字符串是否是可识别的独显直连命令态。
    /// 过去只要状态字段「非空」就把能力推断成支持，于是 "NOT_SUPPORT"、"UNKNOWN"、"NONE"
    /// 这类值也会让 UI 暴露本机不具备的显卡切换入口；而 SupportsDgpuDirect 的语义是
    /// 运行时报告优先于 ItemSupport 静态画像，所以这个伪造的 true 还会覆盖掉正确的画像。
    /// </summary>
    internal static bool IsRecognizedDgpuDirectStatus(string? status) =>
        ContainsAnyStatusToken(status, DgpuDirectStatusTokens);

    /// <summary>状态字符串是否是可识别的核显直通命令态，理由同上。</summary>
    internal static bool IsRecognizedIgpuOnlyStatus(string? status) =>
        ContainsAnyStatusToken(status, IgpuOnlyStatusTokens);

    static bool ContainsAnyStatusToken(string? status, string[] tokens)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        string text = status.ToUpperInvariant();
        foreach (string token in tokens)
            if (text.Contains(token, StringComparison.Ordinal)) return true;
        return false;
    }

    internal static int ResolveGpuModeStatus(int currentMode, string? directStatus, string? igpuStatus)
    {
        string direct = directStatus?.ToUpperInvariant() ?? "";
        string igpu = igpuStatus?.ToUpperInvariant() ?? "";
        if (direct is "DGPU_DIRECT_CONNECT_TOGGLE_ON" ||
            direct.Contains("DIRECT_CONNECT_ON") || direct.Contains("DIRECT_CONNECTION_ON"))
            return MechrevoService.GpuDgpu;
        if (direct is "DGPU_DIRECT_CONNECT_TOGGLE_IGPU" || direct.Contains("DIRECT_CONNECT_IGPU"))
            return MechrevoService.GpuIGpu;

        int igpuMode = igpu.Contains("IGPU_ONLY_CONNECT_RB_ON") || igpu.Contains("IGPU_ONLY_ON")
            ? MechrevoService.GpuIGpu
            : igpu.Contains("IGPU_ONLY_CONNECT_RB_AUTO") || igpu.Contains("IGPU_ONLY_AUTO")
                ? MechrevoService.GpuAuto
                : igpu.Contains("IGPU_ONLY_CONNECT_RB_OFF") || igpu.Contains("IGPU_ONLY_OFF")
                    ? MechrevoService.GpuStandard
                    : -1;
        // 模式显示以独显通路（TOGGLE_*）为唯一权威，与官方三模式卡片一致。
        // RB 寄存器曾被热切换残留污染（TOGGLE_OFF + RB_ON 但硬件仍是混合），
        // 若让 RB 压过 TOGGLE_OFF，图标会一直谎报「集显」；切换策略同样以
        // 这里的结果为准，脏寄存器会导致点集显被判成 NoChange 而毫无动作。
        if (direct is "DGPU_DIRECT_CONNECT_TOGGLE_OFF" ||
            direct.Contains("DIRECT_CONNECT_OFF") || direct.Contains("DIRECT_CONNECTION_OFF"))
            return MechrevoService.GpuStandard;
        return igpuMode >= 0 ? igpuMode : currentMode;
    }

    public bool SupportsQuickSwitch(string key) => key switch
    {
        "touchpad" => TouchpadSeen,
        "wifi" => WifiSeen,
        "bt" => BluetoothSeen,
        "webcam" => WebcamSeen,
        "winkey" => WinKeySeen,
        "fnkey" => FnKeySeen,
        "numpad" => NumpadSeen,
        "osd" => OsdSeen,
        "usb" => UsbChargerSeen,
        "deepsleep" => DeepSleepSeen,
        "copilot" => CopilotSeen,
        "acrecovery" => Capabilities.AcRecovery || AcRecoverySeen,
        "highperf" => HighPerformanceSeen,
        "fanboost" => SupportsFanBoost,
        "lightbar" => SupportsLightbar,
        // Logo 灯带过去不在这张表里，所以它是四条灯带里唯一没有快捷开关的那条：
        // 解析、能力判定、SetLightPower 下发、"Logo灯效"按钮四样都齐，
        // 偏偏少一个开关入口，用户只能进灯效面板才能关它。
        "logolight" => SupportsLogoLight,
        // 新补齐的开关：一律要求服务端真的报过对应字段，没报就是这台机器没有。
        "touchpadtoggle" => TouchpadToggleSeen,
        "singlecolorkb" => SingleColorKbSeen,
        "uni" => UniOmniSeen,
        "omni" => UniOmniSeen,
        "powerlight" => PowerLightSeen,
        "batterylogo" => BatteryLogoSeen,
        "gamewhitelist" => GameWhitelistSeen,
        // 超频菜单总闸：运行时字段 + 支持位。
        //
        // 这里曾经只认 Capabilities.CpuPerformanceTuning，那份能力只在构造时从注册表
        // 读一次（键名 CPUPerformanceAndOverClockMenuSupport），而这台机器的注册表里
        // 根本没有这个键——于是门禁恒假，入口永远出不来。官方的判据是
        // IsOcSettingsSupport || HWOCSupport，也就是「注册表 OcSettingsSupport」或
        // 「LCHWOC 报的 Support（默认 true）」，任一为真即可。这里对齐成三份证据取或：
        // 状态帧的 OcSupport、注册表的两份静态能力位。
        "cpuadvperf" => CpuAdvancedPerformanceSeen && SupportsOverclockMenu,
        // whisper 不在这张表里：命令形状无从取证且真机验证证伪，入口已撤（见解析处说明）。
        _ => false,
    };

    /// <summary>
    /// 超频/高级性能菜单是否可用。对齐官方的 <c>IsOcSettingsSupport || HWOCSupport</c>：
    /// 状态帧明确报了 <c>OcSupport</c> 就以它为准（它是随帧刷新的那份证据），
    /// 否则退回构造时读到的静态能力画像。
    /// </summary>
    public bool SupportsOverclockMenu =>
        OverclockMenuSupport
        ?? (Capabilities.CpuPerformanceTuning || Capabilities.OverclockSettings);

    /// <summary>
    /// 服务端是否报告支持 GPU Whisper 模式。
    /// 注意这只表示「能读到这一族状态」，**不表示我们能写**：
    /// 官方 5.56 界面对这一族只有属性声明、没有下发点，命令形状无从取证，
    /// 按猜测实现的开关已被真机验证证伪（下发后回读毫无变化），入口因此撤掉。
    /// 保留这个属性用于诊断与将来接线。
    /// </summary>
    public bool SupportsWhisperMode => WhisperModeSupport == true;

    /// <summary>电源指示灯亮度是否可调：报了开关且报了一个有效亮度值。</summary>
    public bool SupportsPowerLightBrightness => PowerLightSeen && PowerLightBrightness >= 0;

    internal event Action? CapabilitiesChanged;

    // ---- 液冷系统（BT_LC/Status，外接水冷机）----
    public bool LcConnected { get; private set; }
    public bool LcReportedConnected { get; private set; }
    public bool LcConnectionStateReported { get; private set; }
    public bool LcConnectStringReported { get; private set; }
    public bool LcGcuControllable => LcConnected &&
        (!LcActionSupportReported || LcActionSupported);
    public string LcConnectString { get; private set; } = "";
    public bool LcAutoConnect { get; private set; }
    public bool LcActionSupported { get; private set; }
    public bool LcActionSupportReported { get; private set; }
    public bool LcCoolingAutoSupported { get; private set; }
    public bool LcMeterNormal { get; private set; } = true;
    public bool LcMeterFaultConfirmed => LcGcuControllable && LcPumpDuty > 0 &&
        Volatile.Read(ref _lcConsecutiveMeterFaults) >= 2;
    public int LcPumpDuty { get; private set; } = -1;
    public int LcFanDuty { get; private set; } = -1;
    public int LcPumpControl { get; private set; } = -1;
    public int LcFanControl { get; private set; } = -1;
    public string LcFwVersion { get; private set; } = "";
    public int LcLedRed { get; private set; } = -1;
    public int LcLedGreen { get; private set; } = -1;
    public int LcLedBlue { get; private set; } = -1;
    public int LcLedRedMinimum { get; private set; } = -1;
    public int LcLedRedMaximum { get; private set; } = -1;
    public int LcLedGreenMinimum { get; private set; } = -1;
    public int LcLedGreenMaximum { get; private set; } = -1;
    public int LcLedBlueMinimum { get; private set; } = -1;
    public int LcLedBlueMaximum { get; private set; } = -1;
    public int LcHeadLightMode { get; private set; } = -1;
    public int LcFanLightMode { get; private set; } = -1;
    public bool LcLightingStatusSeen =>
        LcHeadLightMode >= 0 || LcFanLightMode >= 0 ||
        (LcLedRed >= 0 && LcLedGreen >= 0 && LcLedBlue >= 0);
    public bool LcFanLightingSupported => LcGcuControllable &&
        (LcFanLightMode is 4 or 5 ||
         (!string.IsNullOrWhiteSpace(LcFwVersion) &&
          !LcFwVersion.StartsWith("CoolingSystem LCT21001", StringComparison.OrdinalIgnoreCase)));
    public long LcStatusVersion => Interlocked.Read(ref _lcStatusVersion);
    string[] _lcDeviceMacs = [];
    public IReadOnlyList<string> LcDeviceMacs => _lcDeviceMacs;
    public string LcCurrentMac { get; private set; } = "";
    public event Action? LcChanged;

    public bool IsLcStatusFresh(TimeSpan maximumAge, long now = -1)
    {
        long receivedAt = Interlocked.Read(ref _lcStatusReceivedAt);
        if (receivedAt == 0) return false;
        long current = now >= 0 ? now : Environment.TickCount64;
        return current - receivedAt <= maximumAge.TotalMilliseconds;
    }

    /// <summary>
    /// 最近一次 CPU 或 GPU 温度上报是否还在有效期内。液冷自动决策用它区分「真的热」和
    /// 「只是上一帧的残留温度」：过期就当 0 处理，宁可保持现状也不按陈旧读数挑档。
    /// </summary>
    public bool IsTemperatureFresh(TimeSpan maximumAge, long now = -1)
    {
        long current = now >= 0 ? now : Environment.TickCount64;
        long cpuAt = Interlocked.Read(ref _cpuInfoReceivedAt);
        long gpuAt = Interlocked.Read(ref _gpuInfoReceivedAt);
        return Within(cpuAt) || Within(gpuAt);

        bool Within(long receivedAt) =>
            receivedAt != 0 && current - receivedAt <= maximumAge.TotalMilliseconds;
    }

    // ---- 键盘灯官方状态（Keyboard/Status）----
    public string KeyboardEffect { get; private set; } = "";
    public int KeyboardLight { get; private set; } = -1;
    public int KeyboardBrightness { get; private set; } = -1;
    public int KeyboardSpeed { get; private set; } = -1;
    public string KeyboardDirection { get; private set; } = "";
    public bool KeyboardPower { get; private set; } = true;
    public long KeyboardStatusVersion => Interlocked.Read(ref _keyboardStatusVersion);
    public int ScreenBrightness { get; private set; } = -1;   // Settings/DeviceSwitchItemStatus
    int[] _hzList = [];
    public IReadOnlyList<int> HzList => _hzList;
    public int CurrentHz { get; private set; }
    public bool DcHz { get; private set; }   // GPUDevice/Status DC_HZ（自动刷新率：电池自动降刷新率省电）
    public bool DcHzSeen { get; private set; }
    public int GpuSaveMode { get; private set; }   // currentSaveingMode
    public bool LocalDimming { get; private set; }   // Setting/Status LocalDimmingSwitch（分区控光）
    public bool LocalDimmingSeen { get; private set; }
    /// <summary>
    /// 同帧上报的 <c>LocalDimmingSupport</c>。显式为 false 时不认这项能力——
    /// 服务端对不支持的机型照样会发 LocalDimmingSwitch 字段。
    /// </summary>
    public bool? LocalDimmingSupport { get; private set; }
    public bool LcdOverdrive { get; private set; }   // Setting/Status LCDOverdriveSwitch（响应加速）
    public bool LcdOverdriveSeen { get; private set; }
    /// <summary>
    /// 同帧上报的 <c>LCDOverdriveSupport</c>（实测本机为 <c>NotSupport</c>）。
    /// 官方拿注册表 ItemSupport\LCDOverdriveSupport 当门禁，这里优先用随帧刷新的这份。
    /// </summary>
    public bool? LcdOverdriveSupport { get; private set; }
    public bool ColorCalibrationSwitch { get; private set; }   // Setting/Status ColorCalibrationSwitch（屏幕校色开关）
    public int ColorCalibrationResult { get; private set; } = -1; // Setting/Status ColorCalibrationResultCode（结果码，0=成功）
    public long SettingStatusVersion => Interlocked.Read(ref _settingStatusVersion);
    public long ColorCalibrationStatusVersion => Interlocked.Read(ref _colorCalibrationStatusVersion);
    public long GpuModeStatusVersion => Interlocked.Read(ref _gpuModeStatusVersion);
    public int OperatingMode { get; private set; } = -1;
    public int Pl1 { get; private set; }
    public int Pl2 { get; private set; }
    public int CpuAmdSpl { get; private set; } = -1;
    public int CpuAmdSppt { get; private set; } = -1;
    public int CpuAmdFppt { get; private set; } = -1;
    public bool AmdPowerStatusSeen { get; private set; }
    public bool UsesAmdPowerFields => Capabilities.AmdPlatform || AmdPowerStatusSeen;
    public int TccOffset { get; private set; } = -1; // GCU raw offset
    public int TccTarget { get; private set; } = -1; // temperature shown by the official UI
    public int Pl1Minimum { get; private set; } = -1;
    public int Pl1Maximum { get; private set; } = -1;
    public int Pl2Minimum { get; private set; } = -1;
    public int Pl2Maximum { get; private set; } = -1;
    // PL4（瞬时功耗墙）。面向用户的瓦数，Pl4Double 机型上等于线上值的两倍——
    // 换算与官方一致，见下方 Pl4Double 与 SetPl4 的说明。
    public int Pl4 { get; private set; } = -1;
    public int Pl4Minimum { get; private set; } = -1;
    public int Pl4Maximum { get; private set; } = -1;
    // 线上原始值单独留存：公开属性存的是换算后的值，若拿它当下一帧的 fallback 会被反复乘 2。
    int _pl4Raw = -1;
    int _pl4RawMinimum = -1;
    int _pl4RawMaximum = -1;
    public int TccMinimum { get; private set; } = -1;
    public int TccMaximum { get; private set; } = -1;
    public int TccRawMinimum { get; private set; } = -1;
    public int TccRawMaximum { get; private set; } = -1;
    public int GpuTgpMinimum { get; private set; } = -1;
    public int GpuTgpMaximum { get; private set; } = -1;
    public int GpuDbMinimum { get; private set; } = -1;
    public int GpuDbMaximum { get; private set; } = -1;
    public int GpuCoreOffsetMinimum { get; private set; } = -1;
    public int GpuCoreOffsetMaximum { get; private set; } = -1;
    public int GpuMemoryOffsetMinimum { get; private set; } = -1;
    public int GpuMemoryOffsetMaximum { get; private set; } = -1;

    // ---- 自定义性能模式（Fan/Status，原版 CustomModeSettingPage 协议）----
    public int CustomProfileIndex { get; private set; } = -1;   // 当前自定义档 0-3
    public int GpuTgp { get; private set; } = -1;               // GPU_ConfigurableTGPTarget
    public bool GpuDbSwitch { get; private set; }               // GPU_DynamicBoostSwitch
    public int GpuDb { get; private set; } = -1;                // GPU_DynamicBoost
    public bool TccSwitch { get; private set; }                 // CPU_TccOffsetSwitch
    public int TjMax { get; private set; }                      // 目标温度 = TjMax − TccOffset；0 时官方协议按 100°C 计算
    /// <summary>
    /// CPU_PL4_Double_Flag。这类机型的 GCU 把 PL4 按「半瓦」为单位收发：
    /// 官方回读时 CPU_PL4/Minimum/Maximum 一律乘 2 才是面向用户的瓦数，
    /// 下发时再折半。我们照同一套换算，否则 PL4 的显示与写入会差一倍。
    /// </summary>
    public bool Pl4Double { get; private set; }                 // CPU_PL4_Double_Flag

    /// <summary>面向用户瓦数与线上值的倍率（仅 Intel 的 PL4 半瓦机型需要）。</summary>
    internal int Pl4Scale => Pl4Double ? 2 : 1;
    /// <summary>
    /// 「瞬时功耗墙」在两种平台上的线上键名：Intel 发 <c>PL4</c>；AMD 机型上 PL4 字段永不生效
    /// （官方 AMD 分支只读 CPU_AmdFPPT、只发 CpuAmdFPPT，见 CCUWinUI.decompiled.cs 52776/50142），
    /// 必须发 fPPT 才能被 GCU 接受。
    /// </summary>
    internal string Pl4WireKey => UsesAmdPowerFields ? "CpuAmdFPPT" : "PL4";
    /// <summary>
    /// 把面向用户的瓦数折算成线上值（整除，与官方一致）。AMD 的 fPPT 不像 PL4 那样按半瓦收发，
    /// 官方直接原样下发，所以 AMD 上是恒等换算。
    /// </summary>
    internal int Pl4ToWire(int watts) => UsesAmdPowerFields ? watts : watts / Pl4Scale;
    /// <summary>折算再还原后真正可达的瓦数。半瓦机型上奇数入参会被量化到偶数；AMD 原样。</summary>
    internal int Pl4Effective(int watts) => UsesAmdPowerFields ? watts : Pl4ToWire(watts) * Pl4Scale;

    // ---- 风扇转换灵敏度（Fan/Status 的 FAN_FanSwitchSpeed*，官方"风扇转换灵敏度"）----
    public bool FanSwitchSpeedEnabled { get; private set; }     // FAN_FanSwitchSpeedEnabled
    public int FanSwitchSpeed { get; private set; } = -1;       // FAN_FanSwitchSpeed，单位毫秒
    public bool FanSwitchSpeedSeen { get; private set; }

    // ---- GPU 超频（HWOC，原版 CustomModeSettingPage 超频区）----
    public int GpuCoreClockOffset { get; private set; }         // GPU_CoreClockOffsetOC（核心频率偏移，本机实测 150）
    public int GpuMemClockOffset { get; private set; }          // GPU_MemoryClockOffsetOC（显存频率偏移，上限 1000）
    public bool OcSwitch { get; private set; }                  // OverClockingSwitch（超频总开关）
    public bool LchwocSupport { get; private set; }             // LCHWOC/Status Support（本机是否支持超频通道）
    public bool LchwocEnable { get; private set; }              // LCHWOC/Status Enable
    public bool FanRespective { get; private set; }             // FanControlRespective（风扇独立控制：false=双风扇共用 GPU 曲线）
    public event Action? CustomModeChanged;                     // 自定义档/参数回读刷新（CustomModeForm 订阅）

    // ---- GPU 模式（核显-only 与独显直连是两套独立状态）----
    public int GpuMode { get; private set; } = -1;   // 0=核显 1=标准 2=独显直连 3=自动
    public int GpuEcoFlag => GpuMode == MechrevoService.GpuIGpu ? 1 : 0;

    // ---- 电池保护模式（System/BatteryProtection 的 HealthProtectionStatus）----
    public int BatteryProtection { get; private set; } = -1;   // 0=性能(满充) 1=平衡 2=健康
    public int GpuMuxFlag => GpuMode == MechrevoService.GpuDgpu ? 0 : 1;   // G-Helper 语义：mux=0 独显直连

    // ---- 风扇曲线缓存（Fan/Table 主题，16 档）----
    public string CurveName { get; private set; } = "";
    public string TableName { get; private set; } = "";   // Fan/Status 的 FAN_TableName（当前模式表名，写入曲线时用）
    public byte[] CpuCurveUpT { get; private set; } = new byte[16];
    public byte[] CpuCurveDuty { get; private set; } = new byte[16];
    public byte[] GpuCurveUpT { get; private set; } = new byte[16];
    public byte[] GpuCurveDuty { get; private set; } = new byte[16];

    // 默认曲线（DefaultFanTable_*.json，Duty 阶梯正常；用户曲线区全 0 时回退）
    readonly byte[][] _defaultCpuDuty = new byte[3][];
    readonly byte[][] _defaultGpuDuty = new byte[3][];
    readonly byte[][] _defaultCpuUpT = new byte[3][];
    readonly byte[][] _defaultGpuUpT = new byte[3][];

    public bool IsConnected => _publishOverride is not null || _client?.IsConnected == true;

    public event Action? DataChanged;   // UI 刷新信号（对应 G-Helper 的 Timer 轮询，改为事件触发）
    public event Action<int>? ConnectionReady;   // 首连/重连完成订阅与初始请求后触发
    internal event Action<string>? StateChanged;   // 控制回读通知（供事件驱动确认，不触发遥测刷新）
    public event Action<int>? ModeChanged;   // 机械革命 OperatingMode 变化（外部切换/快捷键）
    public event Action? CurveUpdated;   // Fan/Table 缓存更新（含 fallback）后触发——Fans 页据此重画
    string _lastCurveHash = "";   // 曲线数据指纹：相同数据不重复触发重画（防窗口闪烁）
    public event Action? GpuModeChanged;   // 显卡模式变化（Setting/Status 解析）

    /// <summary>机械革命 OperatingMode 状态（原版 UI 反编译确认：0=Office, 1=Gaming, 2=Turbo, 3=Custom）→ G-Helper 枚举。</summary>
    public int GHelperMode => OperatingMode switch
    {
        0 => AsusACPI.PerformanceSilent,    // Office → Silent
        1 => AsusACPI.PerformanceBalanced,  // Gaming → Balanced
        2 => AsusACPI.PerformanceTurbo,     // Turbo → Turbo
        3 => AsusACPI.PerformanceManual,    // Custom → Manual
        _ => AsusACPI.PerformanceBalanced,
    };

    public async Task<bool> ConnectAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
        if (_disposed) return false;
        if (IsConnected) return true;
        if (_client is null)
        {
            _client = _factory.CreateMqttClient();
            // 事件订阅只在首次创建时挂一次（重连时重复订阅会导致消息被处理 N+1 次）
            _client.ApplicationMessageReceivedAsync += e =>
            {
                HandleMessage(e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString());
                return Task.CompletedTask;
            };
            _client.DisconnectedAsync += disconnectedArgs =>
            {
                if (!_disposed) _ = ReconnectLoopAsync();   // 服务重启/断线自动重连，不占用 MQTT 回调线程
                return Task.CompletedTask;
            };
        }
        try
        {
            var res = await _client.ConnectAsync(new MqttClientOptionsBuilder()
                .WithTcpServer(Host, Port)
                .WithCredentials(User, Pwd)
                .WithClientId(_clientId)
                .WithProtocolVersion(MqttProtocolVersion.V311)
                .WithCleanSession(true)
                // 显式 KeepAlive。不设的话走 MQTTnet 的默认值（15 秒），
                // 而 IsConnected 只在收到 FIN/RST 或 keepalive 超时时才翻转——
                // GCUService 被冻结/强杀但 socket 没干净关闭时，IsConnected 最长能
                // 保持真值 15-25 秒。那个窗口里每条 Publish 都要卡到 5 秒超时才失败，
                // 而 SetGpuMode 一次发十几条且全程持锁，最坏累积到分钟级卡死。
                .WithKeepAlivePeriod(KeepAlivePeriod)
                .Build());
            if (res.ResultCode != MqttClientConnectResultCode.Success)
            {
                Logger.WriteLine($"MechrevoHw 首次连接未就绪: {res.ResultCode}");
                if (!_disposed) _ = ReconnectLoopAsync();
                return false;
            }
        }
        catch (Exception ex)
        {
            // 开机时 GCU/MQTT 服务通常晚于登录任务启动。连接拒绝会抛异常，
            // 不能只依赖 DisconnectedAsync，否则本次进程将永远不再尝试连接。
            Logger.WriteLine("MechrevoHw 连接尚未就绪: " + ex.Message);
            if (!_disposed) _ = ReconnectLoopAsync();
            return false;
        }

        try
        {
            // 一次多 filter 订阅。**必须在发任何 GETSTATUS 之前 await 完**：
            // SubscribeAsync 的 Task 完成意味着已收到 broker 的 SUBACK，此时订阅表才生效。
            // 谁要是为了加速把这里改成 fire-and-forget，首帧状态就会静默丢失，
            // 而症状是间歇性的——只在 broker 慢的那几次出现。
            var subscribeOptions = new MqttClientSubscribeOptionsBuilder();
            foreach (string filter in SubscribedTopicFilters) subscribeOptions.WithTopicFilter(filter);
            MqttClientSubscribeResult subscribeResult =
                await _client.SubscribeAsync(subscribeOptions.Build());
            // SUBACK 里的失败码（3.1.1 的 0x80）不会让 SubscribeAsync 抛异常。
            // 不检查的话，broker 对某个主题做 ACL 拒绝时我们会认为订阅成功、继续发 GETSTATUS，
            // 然后永久静默丢失那一类状态——正是这段代码想避免的"死态"的另一半。
            var rejected = subscribeResult.Items
                .Where(item => item.ResultCode > MqttClientSubscribeResultCode.GrantedQoS2)
                .Select(item => $"{item.TopicFilter.Topic}={item.ResultCode}")
                .ToArray();
            if (rejected.Length > 0)
                throw new InvalidOperationException("broker 拒绝订阅: " + string.Join(", ", rejected));
        }
        catch (Exception ex)
        {
            // 订阅失败不能静默存活（IsConnected=true 但零订阅的"死态"）：断开触发重连
            Logger.WriteLine("MechrevoHw 订阅失败: " + ex.Message);
            try { await _client.DisconnectAsync(); }
            catch (Exception disconnectEx) { Logger.WriteLine("MQTT disconnect after subscribe failure: " + disconnectEx.Message); }
            if (!_disposed) _ = ReconnectLoopAsync();
            return false;
        }

        try
        {
            // Dispose 与本方法之间存在竞态：Dispose 由 UI 线程发起，而这里跑在
            // ReconnectLoopAsync 的后台线程上。已经越过开头那次 _disposed 检查之后，
            // 如果不再复查就会出现最坏情况——进程已经决定退出、StopTelemetryBeforeExit
            // 因为看到 IsConnected=false 直接跳过了 System_OFF，而这里紧接着
            // 发出 System_ON：我们没了，GCU 反而刚被要求打开推流。
            if (_disposed) return false;
            await RequestInitialStateAsync();
        }
        catch (Exception ex)
        {
            Logger.WriteLine("MechrevoHw 初始状态请求失败: " + ex.Message);
            try { await _client.DisconnectAsync(); }
            catch (Exception disconnectEx) { Logger.WriteLine("MQTT disconnect after initialization failure: " + disconnectEx.Message); }
            if (!_disposed) _ = ReconnectLoopAsync();
            return false;
        }

        if (_disposed) return false;
        NotifyConnectionReady();
        return true;
        }
        finally { _connectLock.Release(); }
    }

    /// <summary>
    /// 订阅的主题过滤器。
    ///
    /// 注：不单独订阅 <c>Fan/Table</c>——<c>Fan/#</c> 已覆盖，双订阅会按 MQTT 3.1.1 重复投递。
    /// Logo 子灯带是独立主题，不匹配 <c>HidLightbar/#</c>。
    /// <c>Settings/#</c>（复数）只有 DeviceSwitchItemStatus 一个。
    /// </summary>
    internal static readonly string[] SubscribedTopicFilters =
    {
        MqttTopics.SystemFilter, MqttTopics.FanFilter, MqttTopics.SettingFilter, MqttTopics.SettingsFilter, MqttTopics.CustomizeFilter, MqttTopics.GpuDeviceFilter,
        MqttTopics.BtLcFilter,            // 液冷系统
        MqttTopics.KeyboardFilter,         // 键盘灯状态
        MqttTopics.LightbarFilter, MqttTopics.LogoLightFilter,
        MqttTopics.LchwocFilter,           // GPU 超频通道状态
    };

    /// <summary>
    /// 全量初始状态请求。
    ///
    /// 实测：Fan/Status、Setting/Status 需 GETSTATUS 才回；BatteryInfo 需 System_ON；
    /// 电池档需 Report=GET。
    ///
    /// <c>System_ON</c> 排在**第一条**：它才是让 GCU 打开周期性传感器推送的开关，
    /// 排在末尾的话前面那批 GETSTATUS 是在采集模块可能还没启动的状态下发的，
    /// 于是要靠 Program 的 HasTelemetrySince 补发（额外 800ms + 1200ms 等待）。
    ///
    /// 这一份序列由 <see cref="ConnectAsync"/> 与 <c>MechrevoService.RefreshAll</c> 共用。
    /// 过去是两份几乎相同但不完全一样的手写列表（RefreshAll 多一条 LCHWOC GETSTATUS），
    /// 首连时缺的那条要等恢复流程补上——将来只改一边就会留下间歇性的缺状态。
    /// </summary>
    internal async Task RequestInitialStateAsync()
    {
        await Publish(MqttTopics.SystemControl, new Dictionary<string, object> { ["Action"] = "System_ON" });
        await Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
        await Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GET_FAN_SPEED_CURVE_SETTING" });
        await Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
        await Publish(MqttTopics.LchwocControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
        await Publish(MqttTopics.KeyboardCtrl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
        await Publish(MqttTopics.LightbarCtrl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
        await Publish(MqttTopics.LogoLightCtrl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
        await Publish(MqttTopics.BtLcControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
        await Publish(MqttTopics.BatteryProtectionControl, new Dictionary<string, object> { ["Report"] = "GET" });   // 初始电池档回读（否则三档高亮不显示）
    }

    /// <summary>
    /// 使用「变化才记录」的周期性状态日志键。新连接时全部重置，让每一代连接都留下一份
    /// 完整基线；之后只有真正变化才会写入。
    /// </summary>
    internal static readonly string[] StatusLogKeys =
    {
        "lc-status", "kb-status", "hwoc-status", "fan-table", "display-color", "resolved-caps",
        "lb-status-HidLightbar/Status", "lb-status-HidLightbar_Logo/Status",
    };

    internal int NotifyConnectionReady()
    {
        int generation = Interlocked.Increment(ref _connectionGeneration);
        Logger.WriteLine($"MechrevoHw connection ready: generation={generation}");
        // 新连接要重新记录一遍基线状态，否则「变化才记录」会因为内容与断连前相同而
        // 整段跳过，日志里就看不出这一代连接到底读到了什么。
        foreach (string key in StatusLogKeys) Logger.ResetChangeTracking(key);
        var handlers = ConnectionReady;
        if (handlers is null) return generation;
        foreach (Action<int> handler in handlers.GetInvocationList())
        {
            try { handler(generation); }
            catch (Exception ex) { Logger.WriteLine("ConnectionReady handler failed: " + ex.Message); }
        }
        return generation;
    }

    internal bool HasTelemetrySince(long tick) => Interlocked.Read(ref _lastTelemetryTick) >= tick;

    internal bool IsTelemetryStale(TimeSpan maximumAge, long now = -1)
    {
        long lastTelemetryTick = Interlocked.Read(ref _lastTelemetryTick);
        if (lastTelemetryTick == 0) return true;

        long currentTick = now >= 0 ? now : Environment.TickCount64;
        return currentTick - lastTelemetryTick > maximumAge.TotalMilliseconds;
    }

    async Task ReconnectLoopAsync()
    {
        if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0) return;
        try
        {
            int retryDelayMs = 250;
            while (!IsConnected && !_disposed)
            {
                await Task.Delay(retryDelayMs).ConfigureAwait(false);
                try
                {
                    Logger.WriteLine("MQTT 断线，重连中…");
                    if (await ConnectAsync()) return;
                }
                catch (Exception ex) { Logger.WriteLine("MQTT reconnect attempt failed: " + ex.Message); }
                // 本地 GCU 通常在登录后的数秒内出现；2 秒上限兼顾响应速度与低开销。
                retryDelayMs = Math.Min(retryDelayMs * 2, 2000);
            }
        }
        finally { Volatile.Write(ref _reconnecting, 0); }
    }

    /// <summary>
    /// 是否套用内置的一刀切默认曲线（E6）。服务画像**显式否掉**风扇设置
    /// （<c>FanSettingsSupport=0</c>）时不套用——逐机型的曲线由服务按机型提供。
    /// 画像缺失（测试/审计/首次启动）时保留内置回退，保证曲线图始终有数据。
    /// </summary>
    internal static bool ShouldApplyBuiltInCurveDefaults(MechrevoDeviceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return capabilities.FanSettings || !capabilities.ProfileAvailable;
    }

    void LoadDefaultCurveFromDisk()
    {
        var files = new[] { "DefaultCurve_Gaming.json", "DefaultCurve_Office.json", "DefaultCurve_Turbo.json" };
        if (!ShouldApplyBuiltInCurveDefaults(Capabilities))
        {
            Logger.WriteLine("LoadDefaultCurve skipped: the service profile denies fan settings (FanSettingsSupport=0).");
            return;
        }
        try
        {
            // 三模式差异化默认曲线（程序目录 Resources/，随程序分发）：
            // Gaming=M1T1 阶梯、Office=M2T1 六点、Turbo=更激进
            for (int m = 0; m < 3; m++)
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Resources", files[m]);
                if (!File.Exists(path)) continue;
                var o = JObject.Parse(File.ReadAllText(path));
                var (cpuUpT, cpuDuty) = ParseCurve(o["CPU"] as JArray);
                var (gpuUpT, gpuDuty) = ParseCurve(o["GPU"] as JArray);
                _defaultCpuUpT[m] = cpuUpT;
                _defaultGpuUpT[m] = gpuUpT;
                _defaultCpuDuty[m] = cpuDuty;
                _defaultGpuDuty[m] = gpuDuty;
            }
            CurveName = "DefaultCurve_Gaming";
            // 上面的循环在文件缺失时 continue，所以 [0] 可能是 null。过去这里无条件 Array.Copy
            // 会抛 ArgumentNullException 并被下面的 catch 吞掉，结果初始曲线全 0，
            // 直到第一帧 Fan/Table 到达为止 UI 都画着一条平线。
            if (_defaultCpuUpT[0] is { } initialCpuUpT) Array.Copy(initialCpuUpT, CpuCurveUpT, 16);
            if (_defaultCpuDuty[0] is { } initialCpuDuty) Array.Copy(initialCpuDuty, CpuCurveDuty, 16);
            if (_defaultGpuUpT[0] is { } initialGpuUpT) Array.Copy(initialGpuUpT, GpuCurveUpT, 16);
            if (_defaultGpuDuty[0] is { } initialGpuDuty) Array.Copy(initialGpuDuty, GpuCurveDuty, 16);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("LoadDefaultCurve fail: " + ex.Message);
        }
        Logger.WriteLine($"LoadDefaultCurve done: files={string.Join(",", files.Select(f => File.Exists(Path.Combine(AppContext.BaseDirectory, "Resources", f)) ? "Y" : "N"))}");
    }

    /// <summary>
    /// 每条原始 MQTT 报文的旁路观察点（主题, 原始载荷）。
    /// 只给诊断用：排查「某个开关点了不动」时，服务端实际发的字段名与类型是唯一可靠依据，
    /// 而官方反编译里的 DTO 声明未必和这台机器的固件一致。
    /// 解析路径不依赖它，订阅者抛异常也不能影响正常收报。
    /// </summary>
    internal event Action<string, string>? RawMessageObserved;

    internal void HandleMessage(string topic, string payload)
    {
        try
        {
            RawMessageObserved?.Invoke(topic, payload);
        }
        catch (Exception ex) { Logger.WriteLine("RawMessageObserved subscriber failed: " + ex.Message); }

        try
        {
            var o = JObject.Parse(payload);
            switch (topic)
            {
                case MqttTopics.SystemCpuInfo:
                    OnSystemCpuInfo(o);
                    break;
                case MqttTopics.SystemGpuInfo:
                    OnSystemGpuInfo(o);
                    break;
                case MqttTopics.SystemMemoryInfo:
                    OnSystemMemoryInfo(o);
                    break;
                case MqttTopics.SystemFanInfo:
                    OnSystemFanInfo(o);
                    break;
                case MqttTopics.SystemBatteryInfo:
                    OnSystemBatteryInfo(o);
                    break;
                case MqttTopics.SystemNetworkInfo:
                    OnSystemNetworkInfo(o);
                    break;
                case MqttTopics.SystemHardwareInfo:
                    OnSystemHardwareInfo(o);
                    break;
                case MqttTopics.SystemFanErrorInfo:
                    OnSystemFanErrorInfo(o);
                    break;
                case MqttTopics.LightbarStatus:
                case MqttTopics.LogoLightStatus:
                    OnLightbarOrLogoLightStatus(topic, o);
                    break;
                case MqttTopics.KeyboardStatus:
                    OnKeyboardStatus(o);
                    break;
                case MqttTopics.BtLcStatus:
                    OnBtLcStatus(o);
                    break;
                case MqttTopics.SystemBatteryProtection:
                    OnSystemBatteryProtection(o);
                    break;
                case MqttTopics.FanTable:
                    OnFanTable(o);
                    break;
                case MqttTopics.GpuDeviceStatus:
                    OnGpuDeviceStatus(o);
                    break;
                case MqttTopics.SettingsDeviceSwitchItemStatus:
                    OnSettingsDeviceSwitchItemStatus(o);
                    break;
                case MqttTopics.SettingStatus:
                    OnSettingStatus(o);
                    break;
                case MqttTopics.FanStatus:
                    OnFanStatus(o);
                    break;
                case MqttTopics.LchwocStatus:
                    OnLchwocStatus(o);
                    break;
            }
            // 三个通知都做逐个订阅者隔离。过去是裸 ?.Invoke()，而且顺序是
            // CapabilitiesChanged → StateChanged → DataChanged：任一 CapabilitiesChanged
            // 订阅者抛异常，StateChanged 就永远不会触发 → 所有 WaitForStateAsync 只能超时、
            // UI 停更，异常还会被上面那个 catch 吞掉。NotifyConnectionReady 早就是这么写的，
            // 这里只是把同样的纪律补齐。
            if (topic is MqttTopics.LightbarStatus or MqttTopics.LogoLightStatus or MqttTopics.KeyboardStatus or
                MqttTopics.BtLcStatus or MqttTopics.FanTable or MqttTopics.GpuDeviceStatus or MqttTopics.SettingsDeviceSwitchItemStatus or
                MqttTopics.SettingStatus or MqttTopics.FanStatus or MqttTopics.LchwocStatus)
            {
                // 能力判定变化时记一行。日志里原本只有静态画像，而界面显示什么取决于
                // 「静态位 + 服务端报过的字段」合并后的结论——缺这一行，排查
                // 「为什么这台机器上没有某个入口」只能靠猜。变化才记录，不会刷屏。
                Logger.WriteLineIfChanged("resolved-caps", DescribeResolvedCapabilities());
                RaiseIsolated(CapabilitiesChanged, nameof(CapabilitiesChanged));
            }
            RaiseIsolated(StateChanged, nameof(StateChanged), topic);
            if (IsTelemetryTopic(topic))
            {
                Interlocked.Exchange(ref _lastTelemetryTick, Environment.TickCount64);
                RaiseIsolated(DataChanged, nameof(DataChanged));
            }
        }
        catch (Exception ex)
        {
            // 过去这里用 Debug.WriteLine，它带 [Conditional("DEBUG")]，Release 构建里整行调用
            // 被编译移除 —— 解析失败在生产环境完全无声。解析失败会让本帧剩余字段停留在旧值、
            // 三个通知一个都不触发（所有 WaitForStateAsync 只能超时），必须可见。
            // 按主题节流，避免周期性推送的主题持续失败时刷爆日志。
            Logger.WriteLineThrottled(
                "parse-fail-" + topic,
                $"MechrevoHw parse fail {topic}: {ex.GetType().Name}: {ex.Message}",
                5000);
        }
    }

    private void OnSystemCpuInfo(JObject o)
    {
        CpuTemp = Int(o, "CpuTemperature");
        CpuUsage = Int(o, "CpuUsage");
        CpuFrequency = Int(o, "CpuFrequency");
        Interlocked.Exchange(ref _cpuInfoReceivedAt, Environment.TickCount64);
    }

    private void OnSystemGpuInfo(JObject o)
    {
        GpuTemp = Int(o, "GpuTemperature");
        GpuUsage = Int(o, "GpuUsage");
        GpuCoreFreq = Int(o, "GpuCoreFreq");
        VramUsedMb = Int(o, "GpuMem");
        Interlocked.Exchange(ref _gpuInfoReceivedAt, Environment.TickCount64);
    }

    private void OnSystemMemoryInfo(JObject o)
    {
        RamUsage = Int(o, "MemoryUsage");
        RamUsedGb = Double(o, "TotalUsingMemory", RamUsedGb);
    }

    private void OnSystemFanInfo(JObject o)
    {
        CpuFanDuty = Int(o, "CpuFanDuty");
        GpuFanDuty = Int(o, "GpuFanDuty");
        CpuFanRpm = Int(o, "CpuFanRpm");
        GpuFanRpm = Int(o, "GpuFanRpm");
        // 这个主题只有这四个字段，没有第三颗风扇。
        //
        // 曾经在这里加过一段「第三颗风扇解析」，用了五个自己编的字段名
        // （MidFanDuty / RamFanDuty / ThirdFanDuty / Fan3Duty / MiddleFanDuty）。
        // 那是错的：官方 System/FanInfo 的处理逻辑只读 CpuFanDuty / GpuFanDuty /
        // CpuFanRpm / GpuFanRpm 四个字段，全协议 58 个主题里也只有这一个
        // 加 System/FanErrorInfo 与风扇有关，没有任何主题发第三颗风扇的读数。
        // 官方界面是全机型共用的，它只显示两颗，所以任何机型都不会报第三颗。
        //
        // 内存风扇（RamFan1p5Support）确实存在，但它在 EC 侧只有三个**风扇表**
        // 寄存器（TABLE_STATUS1/2、TABLE_CTRL），没有转速与占空比寄存器；
        // 而且它由 GCUService 随风扇表自动管理（MyFanTableCtrl 内部调用），
        // 官方控制台自己也没有读数和控制入口。详见 docs/hardware/README.md。
    }

    private void OnSystemBatteryInfo(JObject o)
    {
        BatteryPercent = Int(o, "BatteryLifePercent");
        // 电池健康信息：循环次数与设计容量。官方 UI 不展示这两项，
        // 但推流里一直带着，比 powercfg /batteryreport 实时得多。
        BatteryCycleCount = OptionalInt(o, BatteryCycleCount, "BatteryCycleCount");
        BatteryAbnormal = OptionalBool(o, "BatteryAbnormal") == true;
        var capacityText = o["BatteryCapacity"]?.ToString();
        if (capacityText is not null) BatteryCapacityText = capacityText;
        Logger.WriteLineIfChanged("battery-info",
            $"BatteryInfo percent={BatteryPercent} cycles={BatteryCycleCount} capacity={BatteryCapacityText} abnormal={BatteryAbnormal}");
    }

    private void OnSystemNetworkInfo(JObject o)
    {
        // 官方推流里的网络吞吐。此前完全没订阅，Overlay 想显示网速只能自己数网卡。
        // 服务端给的是带单位的字符串（"232 Kbps" / "3.9 Mbps"），原样保留供展示。
        NetworkDownload = o["NetworkDownload"]?.ToString() ?? NetworkDownload;
        NetworkUpload = o["NetworkUpload"]?.ToString() ?? NetworkUpload;
        NetworkInfoSeen = true;
    }

    private void OnSystemHardwareInfo(JObject o)
    {
        // 机型/固件铭牌。含 EC 固件版本——排查固件差异类问题时这是关键信息，
        // 而它此前只在官方 UI 里可见。
        HardwareInfoSeen = true;
        string? ecVersion = FirstField(o, "ECVersion", "EcVersion", "EC_Version", "ECFWVersion")?.ToString();
        if (!string.IsNullOrWhiteSpace(ecVersion)) EcFirmwareVersion = ecVersion;
        Logger.WriteLineIfChanged("hardware-info", "HardwareInfo: " + o.ToString(Newtonsoft.Json.Formatting.None));
    }

    private void OnSystemFanErrorInfo(JObject o)
    {
        // 风扇异常告警。官方用它弹提示；此前未订阅，风扇故障对用户完全不可见。
        FanErrorSeen = true;
        bool anyFanError = false;
        foreach (var property in o.Properties())
        {
            if (!property.Name.Contains("Error", StringComparison.OrdinalIgnoreCase) &&
                !property.Name.Contains("Abnormal", StringComparison.OrdinalIgnoreCase)) continue;
            if (OptionalBool(o, property.Name) == true) anyFanError = true;
        }
        FanError = anyFanError;
        Logger.WriteLineIfChanged("fan-error", "FanErrorInfo: " + o.ToString(Newtonsoft.Json.Formatting.None));
    }

    private void OnLightbarOrLogoLightStatus(string topic, JObject o)
    {
        // 主题到过不等于这条灯带存在：开发机曾实测过服务端对不存在的灯带
        // 也推一个 type / powerStatus / brightNess 全空的空状态。
        // 只认「载荷带了可识别的灯带内容」。
        bool lightbarContentPresent = HasLightbarContent(o);
        string lightKey;
        switch (topic)
        {
            case MqttTopics.LogoLightStatus:
                LogoLightStatusSeen |= lightbarContentPresent;
                lightKey = "logolight";
                break;
            default:
                LightbarStatusSeen |= lightbarContentPresent;
                lightKey = "lightbar";
                // 子灯带的能力位只在主灯带状态里带，别的主题不会报。
                LightbarLogoSupport = OptionalBool(o, "LogoSupport") ?? LightbarLogoSupport;
                LightbarBaseSupport = OptionalBool(o, "BaseSupport") ?? LightbarBaseSupport;
                LightbarNewLogoSupport = OptionalBool(o, "NewlogoSupport") ?? LightbarNewLogoSupport;
                LightbarMbLogoSupport = OptionalBool(o, "MBlogoSupport") ?? LightbarMbLogoSupport;
                break;
        }
        // 官方是 text.Equals(RGBKB_PowerStatus.On.ToString())，即精确比 "On"。
        // 这里放宽到忽略大小写：值域只有 On/Off 两个枚举名，
        // 忽略大小写不会引入误判，但能兜住固件写成 "ON"/"on" 的情况——
        // 原来那种写法下，这四条灯带的开关回显会一起变成恒关。
        var lbPower = o["powerStatus"]?.ToString();
        if (lbPower is not null)
            QuickSwitches[lightKey] = string.Equals(lbPower, "On", StringComparison.OrdinalIgnoreCase);
        // 判为「无内容」时把整个载荷打出来：三个挑出来的字段看不出服务端到底发了什么，
        // 而这正是判断某条子灯带是否真实存在时唯一的依据。
        Logger.WriteLineIfChanged("lb-status-" + topic, lightbarContentPresent
            ? $"LB {topic}: type={o["type"]?.ToString() ?? "-"} power={lbPower ?? "-"} light={o["brightNess"]?.ToString() ?? "-"}"
            : $"LB {topic}: no hardware evidence (type/powerStatus empty), raw={o.ToString(Newtonsoft.Json.Formatting.None)}");
    }

    private void OnKeyboardStatus(JObject o)
    {
        KeyboardStatusSeen = true;
        var kbEffect = o["effect"]?.ToString();
        if (kbEffect is not null) KeyboardEffect = kbEffect;
        var kbLight = o["light"]?.ToString();
        if (kbLight is not null && int.TryParse(kbLight, out int kl)) KeyboardLight = kl;
        var kbBrightness = o.GetValue("brightNess", StringComparison.OrdinalIgnoreCase)?.ToString();
        // brightNess 是小数百分比，GCU 恒发点分隔（"62.5"）。走 CurrentCulture 的
        // 逗号小数 locale 会解析失败并退化到五档旧字段，亮度回显静默错档；
        // 与本文件 Int/Double/ParseOptionalInt 一律显式 InvariantCulture 的纪律对齐。
        if (kbBrightness is not null && double.TryParse(kbBrightness, NumberStyles.Float, CultureInfo.InvariantCulture, out double brightness))
        {
            int legacyLevel = int.TryParse(kbLight, out int parsedLegacyLevel) ? parsedLegacyLevel : -1;
            KeyboardBrightness = MechrevoLite.Hardware.KeyboardRgb.MapReportedHardwareBrightness(
                (int)Math.Round(brightness), legacyLevel);
        }
        else if (int.TryParse(kbLight, out int legacyLight))
            KeyboardBrightness = MechrevoLite.Hardware.KeyboardRgb.MapReportedHardwareBrightness(-1, legacyLight);
        var kbSpeed = o["speed"]?.ToString();
        if (kbSpeed is not null && int.TryParse(kbSpeed, out int ks)) KeyboardSpeed = ks;
        var kbDirection = o["direction"]?.ToString();
        if (kbDirection is not null) KeyboardDirection = kbDirection;
        var kbPower = o["powerStatus"]?.ToString();
        if (kbPower is not null) KeyboardPower = kbPower == "On";
        // 字段写完之后才计版本：Program.OnHardwareStateChanged 正是按
        // KeyboardStatusVersion 判断「是否来了新的一帧」再去读亮度和效果的。
        Interlocked.Increment(ref _keyboardStatusVersion);
        Logger.WriteLineIfChanged("kb-status", $"KB: effect={KeyboardEffect} light={KeyboardLight} brightness={KeyboardBrightness}% speed={KeyboardSpeed} direction={KeyboardDirection} power={KeyboardPower}");
    }

    private void OnBtLcStatus(JObject o)
    {
        LcStatusSeen = true;
        string? connS = o.GetValue("connected", StringComparison.OrdinalIgnoreCase)?.ToString();
        string? connStr = o.GetValue("ConnectString", StringComparison.OrdinalIgnoreCase)?.ToString();
        if (connS is not null || connStr is not null)
            LcConnectionStateReported = true;
        if (connS is not null)
        {
            LcReportedConnected = OptionalBool(o, "connected") ??
                string.Equals(connS, "Connected", StringComparison.OrdinalIgnoreCase);
            if (connStr is null)
            {
                LcConnectString = LcReportedConnected ? "Connected" : "Disconnected";
                LcConnectStringReported = false;
            }
        }
        if (connStr is not null)
        {
            LcConnectStringReported = true;
            LcConnectString = connStr;
            if (connS is null)
                LcReportedConnected = string.Equals(connStr, "Connected", StringComparison.OrdinalIgnoreCase);
        }
        if (LcConnectionStateReported)
            LcConnected = LcReportedConnected &&
                (connStr is null || string.Equals(connStr, "Connected", StringComparison.OrdinalIgnoreCase));
        if (OptionalBool(o, "AutoConnect") is bool autoConnect) LcAutoConnect = autoConnect;
        if (OptionalBool(o, "LC_action") is bool actionSupported)
        {
            LcActionSupported = actionSupported;
            LcActionSupportReported = true;
        }
        if (OptionalBool(o, "LC_CoolingAuto") is bool coolingAuto) LcCoolingAutoSupported = coolingAuto;
        LcPumpDuty = OptionalInt(o, "PumpDuty", LcPumpDuty);
        LcFanDuty = OptionalInt(o, "FanDuty", LcFanDuty);
        if (OptionalBool(o, "LC_MeterNormal") is bool meterNormal)
        {
            LcMeterNormal = meterNormal;
            if (!meterNormal && LcGcuControllable && LcPumpDuty > 0)
                Interlocked.Increment(ref _lcConsecutiveMeterFaults);
            else
                Interlocked.Exchange(ref _lcConsecutiveMeterFaults, 0);
        }
        else if (!LcGcuControllable || LcPumpDuty <= 0)
        {
            Interlocked.Exchange(ref _lcConsecutiveMeterFaults, 0);
        }
        LcPumpControl = OptionalInt(o, "LC_PumpCtrl", LcPumpControl);
        LcFanControl = OptionalInt(o, "LC_FanCtrl", LcFanControl);
        string? fwS = o.GetValue("DevFWVersion", StringComparison.OrdinalIgnoreCase)?.ToString();
        if (!string.IsNullOrWhiteSpace(fwS)) LcFwVersion = fwS;
        LcLedRed = OptionalInt(o, "LCLED_R", LcLedRed);
        LcLedGreen = OptionalInt(o, "LCLED_G", LcLedGreen);
        LcLedBlue = OptionalInt(o, "LCLED_B", LcLedBlue);
        LcLedRedMinimum = OptionalInt(o, "LCLED_RMinimum", LcLedRedMinimum);
        LcLedRedMaximum = OptionalInt(o, "LCLED_RMaximum", LcLedRedMaximum);
        LcLedGreenMinimum = OptionalInt(o, "LCLED_GMinimum", LcLedGreenMinimum);
        LcLedGreenMaximum = OptionalInt(o, "LCLED_GMaximum", LcLedGreenMaximum);
        LcLedBlueMinimum = OptionalInt(o, "LCLED_BMinimum", LcLedBlueMinimum);
        LcLedBlueMaximum = OptionalInt(o, "LCLED_BMaximum", LcLedBlueMaximum);
        LcHeadLightMode = OptionalInt(o, "LCLED_Mode", LcHeadLightMode);
        LcFanLightMode = OptionalInt(o, "LCFanLED_Mode", LcFanLightMode);
        JToken? macListToken = o.GetValue("DeviceMacList", StringComparison.OrdinalIgnoreCase);
        if (macListToken is JArray macList)
        {
            _lcDeviceMacs = macList
                .Select(mac => mac?.ToString())
                .Where(mac => !string.IsNullOrWhiteSpace(mac))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        else if (macListToken?.Type == JTokenType.String &&
            macListToken.ToString() is { Length: > 0 } macText)
        {
            string[] parsedMacs = macText.TrimStart().StartsWith("[", StringComparison.Ordinal)
                ? TryParseMacArray(macText)
                : macText.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parsedMacs.Length > 0)
                _lcDeviceMacs = parsedMacs
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
        }
        var curMac = o.GetValue("DevMACString", StringComparison.OrdinalIgnoreCase)?.ToString();
        if (!string.IsNullOrWhiteSpace(curMac)) LcCurrentMac = curMac;
        Interlocked.Increment(ref _lcStatusVersion);
        Interlocked.Exchange(ref _lcStatusReceivedAt, Environment.TickCount64);
        // 变化才记录：GCU 每 6 秒推一次液冷状态，内容通常完全相同。
        // 只做频率节流会让这一行在一小时内重复几百次，把日志里真正需要
        // 排查的内容挤出 10 MB 上限。
        Logger.WriteLineIfChanged("lc-status", $"LC: reported={LcReportedConnected} controllable={LcGcuControllable} state={LcConnectString} pump={LcPumpDuty} fan={LcFanDuty} pumpCtrl={LcPumpControl} fanCtrl={LcFanControl} auto={LcCoolingAutoSupported} fw={LcFwVersion} macs={LcDeviceMacs.Count} curMac={LcCurrentMac}");
        RaiseIsolated(LcChanged, nameof(LcChanged));
    }

    private void OnSystemBatteryProtection(JObject o)
    {
        var prot = Int(o, "HealthProtectionStatus");
        if (prot >= 0) BatteryProtection = prot;
    }

    private void OnFanTable(JObject o)
    {
        FanCurveSeen = true;
        CurveName = o["Name"]?.ToString() ?? CurveName;
        bool? tableRespective = OptionalBool(o, "FanControlRespective");
        bool respectiveChanged = tableRespective.HasValue &&
            (!FanRespectiveSeen || FanRespective != tableRespective.Value);
        if (tableRespective.HasValue)
        {
            FanRespective = tableRespective.Value;
            FanRespectiveSeen = true;
        }
        // 先在本地数组上完成解析和 fallback，最后才整体发布。
        // 过去是「整体替换 → 再对已发布的数组做 Array.Copy」，注释声称原子替换，
        // 但 fallback 分支实际是原地改写：UI 线程在两次 copy 之间读取会看到
        // 半 0 半默认值的曲线，短暂画出一条错误的线。
        (byte[] cpuUpT, byte[] cpuDuty) = ParseCurve(o["CPU"] as JArray);
        (byte[] gpuUpT, byte[] gpuDuty) = ParseCurve(o["GPU"] as JArray);
        // 表名（M1T1/M2T1/M3T1）选默认曲线；M4T1 等未识别表名（自定义）用 opMode 兜底——
        // 防偶发直线：Fan/Table 乱序到达时旧表名（如 M4T1）最后到，若不 fallback 则异常数据（恒值）直接显示
        int fallback = TableNameToMode(CurveName);
        if (fallback < 0)
            fallback = OperatingMode switch { 0 => 1, 1 => 0, 2 => 2, _ => 0 };   // opMode→默认曲线数组索引
        Logger.WriteLineIfChanged("fan-table", $"Fan/Table [{CurveName}] fallbackMode={fallback} cpuDuty={string.Join(",", cpuDuty.Take(8))} uninitialized={IsUninitializedCurve(cpuDuty)}");
        // 只有全 0 的未初始化表才回退。平缓或恒定曲线可能是用户的有效选择，不能覆盖。
        if (fallback >= 0 && IsUninitializedCurve(cpuDuty) &&
            _defaultCpuDuty[fallback] is { } defaultCpuDuty && _defaultCpuUpT[fallback] is { } defaultCpuUpT)
        {
            Array.Copy(defaultCpuUpT, cpuUpT, 16);
            Array.Copy(defaultCpuDuty, cpuDuty, 16);
            Logger.WriteLine($"fallback CPU -> duty={string.Join(",", cpuDuty.Take(8))} upT={string.Join(",", cpuUpT.Take(8))}");
        }
        if (fallback >= 0 && IsUninitializedCurve(gpuDuty) &&
            _defaultGpuDuty[fallback] is { } defaultGpuDuty && _defaultGpuUpT[fallback] is { } defaultGpuUpT)
        {
            Array.Copy(defaultGpuUpT, gpuUpT, 16);
            Array.Copy(defaultGpuDuty, gpuDuty, 16);
            Logger.WriteLine($"fallback GPU -> duty={string.Join(",", gpuDuty.Take(8))} upT={string.Join(",", gpuUpT.Take(8))}");
        }
        // 现在才发布：UI 线程要么看到完整的旧曲线，要么看到完整的新曲线。
        CpuCurveUpT = cpuUpT;
        CpuCurveDuty = cpuDuty;
        GpuCurveUpT = gpuUpT;
        GpuCurveDuty = gpuDuty;
        // 数据指纹：相同曲线不重复触发重画（Fan/Table 周期推送 + 请求响应会导致反复 InitFans → 窗口闪烁）
        string hash = CurveName + "|" + string.Join(",", CpuCurveDuty) + "|" + string.Join(",", GpuCurveDuty);
        if (hash != _lastCurveHash)
        {
            _lastCurveHash = hash;
            RaiseIsolated(CurveUpdated, nameof(CurveUpdated));
        }
        if (respectiveChanged) RaiseIsolated(CustomModeChanged, nameof(CustomModeChanged));
    }

    private void OnGpuDeviceStatus(JObject o)
    {
        GpuDeviceStatusSeen = true;
        var hzArr = o["currentHZList"] as JArray;
        if (hzArr is not null)
        {
            _hzList = hzArr
                .Select(h => int.TryParse(h?.ToString(), out int value) ? value : 0)
                .Where(value => value > 0)
                .Distinct()
                .OrderByDescending(value => value)
                .ToArray();
        }
        CurrentHz = Int(o, "currentHZ");
        // DC_HZ 过去用 Value<bool>()，遇到 "1"/"ON" 这类固件写法会抛
        // FormatException 并中断整个 GPUDevice/Status 分支（连带 currentHZList
        // 和 currentHZ 一起失效）。改为容错解析，无法识别时保留上一次的已知值。
        GpuSaveMode = Int(o, "currentSaveingMode");
        // DcHzSeen 只在真的解析出布尔值时置位，避免把"存在但无法识别"当成已知能力。
        bool? dcHz = OptionalBool(o, "DC_HZ");
        if (dcHz.HasValue) { DcHz = dcHz.Value; DcHzSeen = true; }
    }

    private void OnSettingsDeviceSwitchItemStatus(JObject o)
    {
        DeviceSwitchStatusSeen = true;
        // 这五项过去是「字段存在就置 Seen」：`HasField(...)` 加
        // `OptionalBool(...) == true`。无法识别的值（固件写了个没见过的记法）
        // 会静默变成 false 而 Seen 照样置位，于是界面上长出一个点了没反应的开关，
        // 确认逻辑也永远等不到回读变化——和 LcdOverdriveSeen 修掉的那个完全同型。
        // 正确写法就在下面 GPUDevice/Status 的 DcHzSeen：解析成功才置位。
        if (Int(o, "ScreenBrightness") is >= 0 and int screenBrightness)
        {
            ScreenBrightness = screenBrightness;
            ScreenBrightnessSeen = true;
        }
        if (OptionalBool(o, "TochpadEnable") is bool touchpadEnabled)
        {
            QuickSwitches["touchpad"] = touchpadEnabled;
            TouchpadSeen = true;
        }
        if (OptionalBool(o, "WIFIEnable") is bool wifiEnabled)
        {
            QuickSwitches["wifi"] = wifiEnabled;
            WifiSeen = true;
        }
        if (OptionalBool(o, "BTEnable") is bool bluetoothEnabled)
        {
            QuickSwitches["bt"] = bluetoothEnabled;
            BluetoothSeen = true;
        }
        if (OptionalBool(o, "WebCamEnable") is bool webcamEnabled)
        {
            QuickSwitches["webcam"] = webcamEnabled;
            WebcamSeen = true;
        }
    }

    private void OnSettingStatus(JObject o)
    {
        SettingStatusSeen = true;
        // 局部调光 / 屏幕响应加速。
        //
        // 值的判定沿用官方的精确匹配（CCUWinUI L29397-29398 就是
        // == LOCALDIMMING_ON / == LCDOverdrive_ON），不改成 Contains——
        // 这两个动作名本身不含 OFF/UNLOCK 之类的否定词，Contains 反而没有依据。
        //
        // 但支持位必须看：服务端对不支持的机型照样发这个字段（实测本机
        // LCDOverdriveSwitch=LCDOverdrive_OFF 同时 LCDOverdriveSupport=NotSupport）。
        // 过去无条件置 *Seen，于是界面上长出一个点了永远不生效的开关，
        // 确认逻辑也永远等不到回读变化。官方是拿注册表 ItemSupport 的
        // LCDOverdriveSupport 当门禁（L29288-29292），这里优先用同帧上报的那份。
        bool? lcdOverdriveSupport = FirstOptionalBool(o, "LCDOverdriveSupport", "LcdOverdriveSupport");
        if (lcdOverdriveSupport is not null) LcdOverdriveSupport = lcdOverdriveSupport;
        bool? localDimmingSupport = FirstOptionalBool(o, "LocalDimmingSupport");
        if (localDimmingSupport is not null) LocalDimmingSupport = localDimmingSupport;

        var ldS = o["LocalDimmingSwitch"]?.ToString();
        if (ldS is not null)
        {
            LocalDimming = ldS == "LOCALDIMMING_ON";
            if (LocalDimmingSupport != false) LocalDimmingSeen = true;
        }
        var odS = o["LCDOverdriveSwitch"]?.ToString();
        if (odS is not null)
        {
            LcdOverdrive = odS == "LCDOverdrive_ON";
            if (LcdOverdriveSupport != false) LcdOverdriveSeen = true;
        }
        // Fresh GCU status must win over a registry value that can lag a profile change.
        // The registry remains the fallback for older GCU builds without this field.
        JToken? ccSwitch = FirstField(o,
            "ColorCalibrationSwitch", "ColorCalibrationSwitch_Status", "ColorCalibrationStatus");
        // 注册表读取要单独隔离：TryReadColorCalibrationOn / IsColorCalibrationOn
        // 在 MQTT 接收线程上做阻塞式注册表 I/O，而这里是 Setting/Status 的中段——
        // 它一抛，本帧后面的 USB/OSD/WinKey 一族、显卡模式、两个版本号全都丢，
        // 所有在等的 WaitForStateAsync 只能超时。校色状态读不到时按「未知」处理即可。
        bool? parsedCcSwitch = null;
        bool colorCalibrationParsed = false;
        try
        {
            parsedCcSwitch = MechrevoService.ResolveColorCalibrationSwitch(
                MechrevoService.TryReadColorCalibrationOn(),
                ParseColorCalibrationSwitch(ccSwitch));
            if (parsedCcSwitch.HasValue)
            {
                ColorCalibrationSwitch = parsedCcSwitch.Value;
                ColorCalibrationSwitchSeen = true;
                ColorCalibrationSeen = true;
                colorCalibrationParsed = true;
            }
            else if (!ColorCalibrationSwitchSeen)
                ColorCalibrationSwitch = MechrevoService.IsColorCalibrationOn();
        }
        catch (Exception ex)
        {
            Logger.WriteLineThrottled("cc-registry",
                "ColorCalibration registry read failed: " + ex.Message, 5000);
        }

        JToken? ccMode = FirstField(o,
            "CurrentColorCalibration", "ColorCalibrationMode", "ColorCalibration");
        int parsedCcMode = ParseColorCalibrationMode(ccMode);
        if (parsedCcMode is >= 1 and <= 4)
        {
            ColorCalibrationMode = parsedCcMode;
            ColorCalibrationModeSeen = true;
            ColorCalibrationSeen = true;
            colorCalibrationParsed = true;
        }
        var ccR = o["ColorCalibrationResultCode"]?.ToString();
        if (ccR is not null && int.TryParse(ccR, out int ccRV))
        {
            ColorCalibrationResult = ccRV;
            ColorCalibrationSeen = true;
            colorCalibrationParsed = true;
        }
        // 自增条件是「真解析出了值」而不是「字段存在」。
        // 按字段存在自增会让 ConfirmColorCalibrationAsync 的 freshCalibrationStatus
        // 误报新鲜——虽然它还 AND 了 stateMatches 不至于伪造成功，
        // 但一个只在字段存在时跳的版本号本身就不该被当成「状态已更新」的信号。
        if (colorCalibrationParsed)
            Interlocked.Increment(ref _colorCalibrationStatusVersion);
        // CloseTimer 的通知过去在这里裸 ?.Invoke()，位置又在本 case 中段：
        // 任一订阅者抛异常就会把后面所有字段和三个通知一起丢掉。
        // 现在只记录变化，事件挪到 case 末尾统一发。
        int? closeTimerChangedTo = null;
        var ctS = o["CloseTimer"]?.ToString();
        if (ctS is not null && int.TryParse(ctS, out int ct) && ct != CloseTimerMinutes)
        {
            CloseTimerMinutes = ct;   // 灯效睡眠时间（分钟，0=关闭）——原版 KEYBOARD_LIGHTBAR_TIMER
            Logger.WriteLine($"CloseTimer -> {ct} 分钟");
            closeTimerChangedTo = ct;
        }
        // 下面这一组的判定全部按官方逐字对齐：官方读的是「状态串里有没有
        // 那个否定词」，不是把整串跟某个常量比。差别在别的机型上才显出来——
        // 固件一旦把 USB_CHARGER_STATUS_ON 写成 USB_CHARGER_ON，
        // 精确匹配就永久判成关闭，而开关在界面上是「点了跳回去」。
        // 依据：CCUWinUI L73865 / L31146 / L31096 / L31241 / L31192。
        var usbS = o["UsbCharger"]?.ToString();
        if (usbS is not null) { UsbCharger = !usbS.Contains("OFF", StringComparison.OrdinalIgnoreCase); UsbChargerSeen = true; }
        // OSD：官方的 OSDSwitch 语义是「隐藏 OSD」，我们的 QuickSwitches["osd"]
        // 语义是「显示 OSD」（下发 ("osd", true) => OSD_HIDDEN_OFF），所以取反。
        var osdS = o["OSD"]?.ToString();
        if (osdS is not null) { QuickSwitches["osd"] = osdS.Contains("OFF", StringComparison.OrdinalIgnoreCase); OsdSeen = true; }
        var wkS = o["WinKey"]?.ToString();
        if (wkS is not null) { QuickSwitches["winkey"] = !wkS.Contains("UNLOCK", StringComparison.OrdinalIgnoreCase); WinKeySeen = true; }   // true=已锁定（勾选=锁定语义）
        var fnS = o["FnKey"]?.ToString();
        if (fnS is not null) { QuickSwitches["fnkey"] = !fnS.Contains("UNLOCK", StringComparison.OrdinalIgnoreCase); FnKeySeen = true; }   // true=已锁定（勾选=锁定语义）
        var npS = o["NumPad"]?.ToString();
        if (npS is not null) { QuickSwitches["numpad"] = !npS.Contains("UNLOCK", StringComparison.OrdinalIgnoreCase); NumpadSeen = true; }   // true=已锁定（原版勾选语义）

        // 电池 Logo 灯（BATTERYLOGO_TOGGLE_ON/OFF）。官方 L73879-73881 的判定是
        // !BatteryLogo_Status.Contains("OFF")。这一项此前完全缺失：既没解析也没下发，
        // 有这条灯的机型在我们这边根本看不到入口。
        var blS = o["BatteryLogo_Status"]?.ToString();
        if (!string.IsNullOrWhiteSpace(blS))
        {
            QuickSwitches["batterylogo"] = !blS.Contains("OFF", StringComparison.OrdinalIgnoreCase);
            BatteryLogoSeen = true;
        }

        // 触摸板切换键：与「触摸板开关」是两件事——这个控制的是 Fn 组合键能否切换触摸板。
        var tptS = o["TouchpadToggle"]?.ToString();
        if (tptS is not null) { QuickSwitches["touchpadtoggle"] = !tptS.Contains("OFF", StringComparison.OrdinalIgnoreCase); TouchpadToggleSeen = true; }

        // 单色键盘背光：走 EC 的单色通道，与 RGB 逐键渲染是并列的两套硬件。
        var sckS = o["SingleColorKBBL"]?.ToString();
        if (sckS is not null) { QuickSwitches["singlecolorkb"] = !sckS.Contains("OFF", StringComparison.OrdinalIgnoreCase); SingleColorKbSeen = true; }

        // Uni / Omni：官方语义未公开，但两者互斥。只要报了任意一个就认为这台机器有这组开关。
        var uniS = o["UniSwitch"]?.ToString();
        var omniS = o["OmniSwitch"]?.ToString();
        if (uniS is not null) { QuickSwitches["uni"] = !uniS.Contains("OFF", StringComparison.OrdinalIgnoreCase); UniOmniSeen = true; }
        if (omniS is not null) { QuickSwitches["omni"] = !omniS.Contains("OFF", StringComparison.OrdinalIgnoreCase); UniOmniSeen = true; }

        // 电源指示灯：开关 + 亮度两个字段，亮度是 0..100。
        //
        // 开关的值是状态字符串（实测 "PowerLight_ON"），不是布尔也不是 0/1。
        // 这里曾经用 OptionalBool 解析：ParseFlexibleBool 归一化后做的是**精确**匹配，
        // "PowerLight_ON" 变成 "POWERLIGHTON" 匹配不上 "ON"，于是永远返回 null，
        // 开关在界面上恒显示为关、点了也看不出变化。改成与同族开关一致的含 OFF 判定。
        var plS = o["PowerLightSwitch"]?.ToString();
        if (plS is not null)
        {
            QuickSwitches["powerlight"] = !plS.Contains("OFF", StringComparison.OrdinalIgnoreCase);
            PowerLightSeen = true;
        }
        if (HasField(o, "PowerLightBrightness"))
        {
            int plb = Int(o, "PowerLightBrightness");
            if (plb >= 0) PowerLightBrightness = Math.Clamp(plb, 0, 100);
        }
        // 电池页开关状态（原版 MySetting 结构：状态字符串含 OFF 即关；CopilotKey 含 UNLOCK 即开）
        var hpS = o["HighPerformancePowerModeSwitch"]?.ToString(); if (hpS is not null) { QuickSwitches["highperf"] = !hpS.Contains("OFF", StringComparison.OrdinalIgnoreCase); HighPerformanceSeen = true; }
        // 这三项过去漏了 OrdinalIgnoreCase，与同族其余十几项不一致：
        // 固件把值写成小写 off / unlock 就会被判成相反的状态。
        var acS = o["AcRecoverySwitch_Status"]?.ToString(); if (acS is not null) { QuickSwitches["acrecovery"] = !acS.Contains("OFF", StringComparison.OrdinalIgnoreCase); AcRecoverySeen = true; }
        var dsS = o["DeepSleepSwitch"]?.ToString(); if (dsS is not null) { QuickSwitches["deepsleep"] = !dsS.Contains("OFF", StringComparison.OrdinalIgnoreCase); DeepSleepSeen = true; }
        var cpS = o["CopilotKey"]?.ToString(); if (cpS is not null) { QuickSwitches["copilot"] = !cpS.Contains("UNLOCK", StringComparison.OrdinalIgnoreCase); CopilotSeen = true; }
        var dsT = o["DeepSleepTime"]?.ToString(); if (dsT is not null && int.TryParse(dsT, out int dst)) DeepSleepTime = dst;

        // 显示色彩模式 / 显示特性 / NVIDIA 全局首选显卡：只读记录，不下发。
        var displayMode = o["DisplayMode"]?.ToString();
        if (!string.IsNullOrWhiteSpace(displayMode)) DisplayColorMode = displayMode;
        var displayFeature = o["DisplayFeatureStatus"]?.ToString();
        if (!string.IsNullOrWhiteSpace(displayFeature))
            DisplayFeatureOn = !displayFeature.Contains("OFF", StringComparison.OrdinalIgnoreCase);
        var nvPanel = o["DGpu"]?.ToString();
        if (!string.IsNullOrWhiteSpace(nvPanel)) NvControlPanelPreference = nvPanel;
        foreach (string field in DisplayColorParameterFields)
        {
            int value = OptionalInt(o, field, int.MinValue);
            if (value != int.MinValue) _displayColorParameters[field] = value;
        }
        if (DisplayColorMode.Length > 0 || DisplayFeatureOn.HasValue ||
            NvControlPanelPreference.Length > 0 || _displayColorParameters.Count > 0)
        {
            Logger.WriteLineIfChanged("display-color",
                $"Display: mode={(DisplayColorMode.Length > 0 ? DisplayColorMode : "-")} " +
                $"feature={(DisplayFeatureOn.HasValue ? DisplayFeatureOn.Value ? "On" : "Off" : "-")} " +
                $"nvPanel={(NvControlPanelPreference.Length > 0 ? NvControlPanelPreference : "-")} " +
                $"params={{{string.Join(",", _displayColorParameters.Select(p => p.Key + "=" + p.Value))}}}");
        }
        var dgpuStatus = FirstField(o,
            "DiscreteGpuDirectConnectionSwitch_Status", "DGpuDirectConnectionSwitch_Status", "DGPU_DIRECT_STATUS")?.ToString();
        var igpuStatus = FirstField(o,
            "IGpuOnlyConnectionSwitch_Status", "IGPUOnlyConnectionSwitch_Status", "IGPU_ONLY_STATUS")?.ToString();
        bool gpuModeStatusPresent =
            !string.IsNullOrWhiteSpace(dgpuStatus) || !string.IsNullOrWhiteSpace(igpuStatus);
        DgpuDirectStatusSupport = FirstOptionalBool(o,
            "DiscreteGpuDirectConnectionSwitch_Support", "DGpuDirectConnectionSwitch_Support", "DGPU_DIRECT_SUPPORT") ?? DgpuDirectStatusSupport;
        IgpuOnlyStatusSupport = FirstOptionalBool(o,
            "IGpuOnlyConnectionSwitch_Support", "IGPUOnlyConnectionSwitch_Support", "IGPU_ONLY_SUPPORT") ?? IgpuOnlyStatusSupport;
        // Some older GCU versions expose the current switch state but omit the separate
        // capability bit. A non-empty status proves that this command family is available.
        // 只有能被解析成具体模式的状态才构成「这套命令族存在」的证据。
        // 任意非空字符串（"NOT_SUPPORT"/"UNKNOWN"/"NONE"）不算——那会让 UI 暴露
        // 本机不具备的显卡切换入口，而且会覆盖掉正确的 ItemSupport 静态画像。
        // 显式上报为 false 的支持位已经在上面写入，?? = 不会覆盖它。
        if (IsRecognizedDgpuDirectStatus(dgpuStatus)) DgpuDirectStatusSupport ??= true;
        if (IsRecognizedIgpuOnlyStatus(igpuStatus)) IgpuOnlyStatusSupport ??= true;
        string? gpuSwitchResult = o.GetValue("CheckDGpuStatusforIGpuOnlyOnSuccess", StringComparison.OrdinalIgnoreCase)?.ToString();
        bool gpuSwitchResultPresent = !string.IsNullOrWhiteSpace(gpuSwitchResult);
        if (gpuSwitchResultPresent)
        {
            GpuSwitchResult = gpuSwitchResult!.Contains('1') ? 1
                : gpuSwitchResult.Contains('2') ? 2
                : 0;
            GpuSwitchResultReported = true;
            // 版本号不在这里自增：GpuMode 要到下面才发布，中间还有
            // ResolveGpuModeStatus 与 IgpuSwitchBlocked。热切换的确认谓词
            // （SwitchGpuMode.TargetReached）会同时看 CurrentGpuMode 与
            // GpuSwitchResultVersion，先跳版本号就构成「新结果 + 旧模式」。
            // 目前它靠谓词里 `if (CurrentGpuMode != mode) return false;` 短路兜住，
            // 但 Setting/Status 应该只有一个 release 点。
        }
        int newGpu = ResolveGpuModeStatus(GpuMode, dgpuStatus, igpuStatus);
        string? cannotSwitch = FirstField(o, "IGpuCannotBeSwitchNowVisibility")?.ToString();
        if (!string.IsNullOrWhiteSpace(cannotSwitch))
            IgpuSwitchBlocked = newGpu != MechrevoService.GpuDgpu &&
                !cannotSwitch.Contains("false", StringComparison.OrdinalIgnoreCase);
        // 版本号必须在字段写完之后才自增（release 语义）。过去它在 case 中段自增，
        // 按版本号轮询的调用方会看到「新版本号 + 旧字段值」，表现为
        // 「明明落地了却确认失败」的偶发误判。GpuMode 也一样：先赋值，再计版本。
        bool gpuModeChanged = newGpu != GpuMode;
        if (gpuModeChanged) GpuMode = newGpu;
        if (gpuSwitchResultPresent) Interlocked.Increment(ref _gpuSwitchResultVersion);
        if (gpuModeStatusPresent) Interlocked.Increment(ref _gpuModeStatusVersion);
        Interlocked.Increment(ref _settingStatusVersion);
        if (gpuModeChanged)
            Logger.WriteLine($"GpuMode -> {newGpu} (dgpu={dgpuStatus ?? "-"}, igpu={igpuStatus ?? "-"})");
        // 两个事件都收进 RaiseIsolated 并放到 case 最后：过去是裸 ?.Invoke()，
        // 订阅者抛异常会让本帧后面的 Capabilities/State/Data 通知全部不发。
        if (closeTimerChangedTo is int closeTimerMinutes)
            RaiseIsolated(CloseTimerChanged, nameof(CloseTimerChanged), closeTimerMinutes);
        if (gpuModeChanged) RaiseIsolated(GpuModeChanged, nameof(GpuModeChanged));
    }

    private void OnFanStatus(JObject o)
    {
        FanStatusSeen = true;
        // IsAC 过去用 Value<bool>()，是本 case 自增版本号后的第一个赋值。
        // 这个协议在同一帧里大量使用字符串型布尔（GPU_DynamicBoostSwitch=="1" 等），
        // 一旦 IsAC 也发成 "1" 就会抛异常，导致模式/PL1/PL2/TCC/TGP/超频回读全部失效。
        IsAC = OptionalBool(o, "IsAC") ?? IsAC;
        // FanBoostEnable 过去走 Int()>0：固件发 JSON 布尔 true 时 int 解析失败得到 0，
        // 于是能力被判成"支持"而状态永远显示"关闭"。改走统一的布尔解析，
        // 且只有真的解析出布尔值才认为看见过这项能力。
        bool? fanBoost = OptionalBool(o, "FanBoostEnable");
        if (fanBoost.HasValue) { FanBoost = fanBoost.Value; FanBoostSeen = true; }
        var newMode = Int(o, "OperatingMode");
        // 事件延后到 case 末尾统一发。过去这里是裸 ModeChanged?.Invoke()，
        // 位置在本 case 中段——订阅者抛异常会把后面的功耗墙、温度墙、TGP、
        // 超频回读连同版本号一起丢掉，而那些字段是本帧最主要的内容。
        int? modeChangedTo = null;
        if (newMode != OperatingMode && newMode >= 0)
        {
            // 期望模式与截止时刻必须原子地一起取，否则会读到「旧期望 + 新截止」的组合。
            (bool pending, int pendingMode) = GetModeSwitchPendingState();
            bool expectedPendingMode = pending && pendingMode == newMode;
            if (pending && pendingMode >= 0 && newMode != pendingMode)
            {
                Logger.WriteLineThrottled("stale-op-mode", $"Ignored stale opMode {newMode}; pending={pendingMode}", 500);
            }
            else
            {
                Logger.WriteLine($"opMode {OperatingMode}->{newMode} pending={pending} expected={expectedPendingMode}");
                OperatingMode = newMode;
                // A slow GCU can report the confirmed target after the
                // service timeout. Notify the UI on that first target
                // packet, while keeping the pending window active so
                // queued pre-switch packets are still rejected.
                if (!pending || expectedPendingMode) modeChangedTo = newMode;
            }
        }
        int genericPl1 = Int(o, "CPU_PL1");
        int genericPl2 = Int(o, "CPU_PL2");
        // AMD 平台判定过去用 HasField：键存在即算，值为 0/null/无法解析也算。
        // Intel 机型上只要 GCU 报文里带了 CPU_AmdSPL: 0，就会被永久判成 AMD 平台，
        // 于是功耗墙改用 CpuAmdSPL/CpuAmdSPPT 键下发、被 GCU 忽略、确认永久失败。
        // 真实的功耗墙不可能是 0 或负数，所以只有「可用值」才构成平台证据。
        bool amdPowerFields = Capabilities.AmdPlatform ||
            HasUsablePowerValue(o, "CPU_AmdSPL") ||
            HasUsablePowerValue(o, "CPU_AmdSPPT") ||
            HasUsablePowerValue(o, "CPU_AmdFPPT");
        if (amdPowerFields) AmdPowerStatusSeen = true;
        // 同理，只latch 可用值：0 一旦被记住就再也回不到"未知"，
        // 而 OptionalInt 的 fallback 是上一次的值（部分帧不该擦除已知值）。
        CpuAmdSpl = OptionalUsablePowerValue(o, "CPU_AmdSPL", CpuAmdSpl);
        CpuAmdSppt = OptionalUsablePowerValue(o, "CPU_AmdSPPT", CpuAmdSppt);
        CpuAmdFppt = OptionalUsablePowerValue(o, "CPU_AmdFPPT", CpuAmdFppt);
        // 只有在确认是 AMD 功耗字段体系时才让 AMD 值接管 Pl1/Pl2，
        // 否则 Intel 机型上一个伪造的 0 会永久劫持显示值并丢弃真实的 CPU_PL1。
        Pl1 = UsesAmdPowerFields && CpuAmdSpl > 0 ? CpuAmdSpl : genericPl1;
        Pl2 = UsesAmdPowerFields && CpuAmdSppt > 0 ? CpuAmdSppt : genericPl2;
        TccOffset = Int(o, "CPU_TccOffset");
        if (HasField(o, "CPU_TccOffset") || HasField(o, "CPU_TccOffsetMinimum") ||
            HasField(o, "CPU_TccOffsetMaximum") || HasField(o, "CPU_TccOffsetSwitch") ||
            HasField(o, "CPU_AmdTccTarget"))
            TccStatusSeen = true;
        Pl1Minimum = OptionalInt(o, Pl1Minimum,
            "CPU_AmdSPLMinimum", "CPU_AmdSPLMin", "CPU_AmdSPL_Minimum", "CPU_AmdSPL_Min", "CPU_PL1Minimum");
        Pl1Maximum = OptionalInt(o, Pl1Maximum,
            "CPU_AmdSPLMaximum", "CPU_AmdSPLMax", "CPU_AmdSPL_Maximum", "CPU_AmdSPL_Max", "CPU_PL1Maximum");
        Pl2Minimum = OptionalInt(o, Pl2Minimum,
            "CPU_AmdSPPTMinimum", "CPU_AmdSPPTMin", "CPU_AmdSPPT_Minimum", "CPU_AmdSPPT_Min", "CPU_PL2Minimum");
        Pl2Maximum = OptionalInt(o, Pl2Maximum,
            "CPU_AmdSPPTMaximum", "CPU_AmdSPPTMax", "CPU_AmdSPPT_Maximum", "CPU_AmdSPPT_Max", "CPU_PL2Maximum");
        TccRawMinimum = OptionalInt(o, "CPU_TccOffsetMinimum", TccRawMinimum);
        TccRawMaximum = OptionalInt(o, "CPU_TccOffsetMaximum", TccRawMaximum);
        GpuTgpMinimum = OptionalInt(o, "GPU_ConfigurableTGPMinimum", GpuTgpMinimum);
        GpuTgpMaximum = OptionalInt(o, "GPU_ConfigurableTGPMaximum", GpuTgpMaximum);
        GpuDbMinimum = OptionalInt(o, "GPU_DynamicBoostMinimum", GpuDbMinimum);
        GpuDbMaximum = OptionalInt(o, "GPU_DynamicBoostMaximum", GpuDbMaximum);
        UpdateGpuOffsetRanges(o);
        TableName = o["FAN_TableName"]?.ToString() ?? TableName;
        var cpi = o["CustomProfileIndex"]?.ToString();
        if (cpi is not null && int.TryParse(cpi, out int cpiV)) CustomProfileIndex = cpiV;
        // TGP 与 Dynamic Boost 的目标值同样只接受 > 0：GCU 在这两项关闭时
        // 会报 0，一旦写进来就被当成「用户设定的目标值 0 W」，
        // 而 Pl1/Pl2 早就用 `CpuAmdSpl > 0` 挡住了同一件事。
        int reportedTgp = OptionalUsablePowerValue(o, "GPU_ConfigurableTGPTarget", -1);
        if (reportedTgp > 0) GpuTgp = reportedTgp;
        var dbS = o["GPU_DynamicBoostSwitch"]?.ToString();
        if (dbS is not null) GpuDbSwitch = dbS == "1";
        int reportedDb = OptionalUsablePowerValue(o, "GPU_DynamicBoost", -1);
        if (reportedDb > 0) GpuDb = reportedDb;
        var tccS = o["CPU_TccOffsetSwitch"]?.ToString();
        if (tccS is not null) TccSwitch = tccS == "1";
        var tj = o["TjMax"]?.ToString();
        if (tj is not null && int.TryParse(tj, out int tjV) && tjV > 0) TjMax = tjV;
        int amdTccTarget = OptionalInt(o, "CPU_AmdTccTarget", -1);
        if (Capabilities.AmdPlatform)
        {
            TccMinimum = 85;
            TccMaximum = TccRawMaximum > 0 ? TccRawMaximum : 95;
            if (TccMinimum >= TccMaximum) TccMinimum = Math.Max(0, TccMaximum - 10);
        }
        else
        {
            int tjMax = TjMax > 0 ? TjMax : 100;
            // New GCU versions may expose 0/0 as an unavailable raw-offset
            // range. Official CCU still presents the Intel target range as
            // 75..95 C; interpreting 0/0 literally collapses it to 95/95.
            bool collapsedZeroRange = TccRawMinimum == 0 && TccRawMaximum == 0;
            int deviceMinimum = !collapsedZeroRange && TccRawMaximum >= 0
                ? tjMax - TccRawMaximum : 75;
            int deviceMaximum = !collapsedZeroRange && TccRawMinimum >= 0
                ? tjMax - TccRawMinimum : 95;
            TccMinimum = Math.Clamp(deviceMinimum, 75, 95);
            TccMaximum = Math.Clamp(deviceMaximum, 75, 95);
            if (TccMinimum > TccMaximum) TccMinimum = TccMaximum;
        }
        int reportedTarget = amdTccTarget >= 0 ? amdTccTarget : TccOffset >= 0 ? TccTargetFromRaw(TccOffset) : -1;
        TccTarget = reportedTarget >= 0 ? Math.Clamp(reportedTarget, TccMinimum, TccMaximum) : -1;
        var pl4d = o["CPU_PL4_Double_Flag"]?.ToString();
        if (pl4d is not null) Pl4Double = pl4d == "1";

        // PL4 必须在 Pl4Double 之后换算：官方对这类机型把线上值乘 2 当面向用户的瓦数。
        //
        // 只 latch 可用值（> 0），与 CpuAmdSpl 那一族同一纪律：
        // OptionalInt 的 fallback 是上一次的值，所以 0 一旦被记住就再也回不到
        // 「未知」，而下面 `_pl4Raw >= 0` 会让它通过所有有效性守卫——
        // 界面上就是一个「已知的 0 W」。上下限同理：0/0 会把滑条量程永久压成 0..0。
        // 真实的功耗墙不可能是 0 或负数。
        _pl4Raw = OptionalUsablePowerValue(o, "CPU_PL4", _pl4Raw);
        _pl4RawMinimum = FirstUsablePowerValue(o, _pl4RawMinimum,
            "CPU_PL4Minimum", "CPU_PL4Min", "CPU_PL4_Minimum", "CPU_PL4_Min");
        _pl4RawMaximum = FirstUsablePowerValue(o, _pl4RawMaximum,
            "CPU_PL4Maximum", "CPU_PL4Max", "CPU_PL4_Maximum", "CPU_PL4_Max");
        int pl4Scale = Pl4Double ? 2 : 1;
        int genericPl4 = _pl4Raw >= 0 ? _pl4Raw * pl4Scale : -1;
        // AMD 的「瞬时功耗墙」是 fPPT：官方在 AMD 分支只读 CPU_AmdFPPT，从不读 CPU_PL4。
        // 平台判定与 Pl1/Pl2 同一纪律——只有确认 AMD 通道且 fPPT 可用时才让它接管读数，
        // 否则 Intel 机型上一个占位值会劫持显示。量程仍取 PL4 的（官方 fPPT 滑条的 MaxValue
        // 就是 CpuPL4Maximum）。
        Pl4 = UsesAmdPowerFields && CpuAmdFppt > 0 ? CpuAmdFppt : genericPl4;
        Pl4Minimum = _pl4RawMinimum >= 0 ? _pl4RawMinimum * pl4Scale : -1;
        Pl4Maximum = _pl4RawMaximum >= 0 ? _pl4RawMaximum * pl4Scale : -1;

        // 风扇转换灵敏度：开关与数值分开上报，两者任一出现就说明机型支持这一项。
        // 这一项是自定义模式的风扇调参，不是系统快捷开关，所以不进 QuickSwitches：
        // 它和 PL1/PL2/温度墙一样由 CustomModeForm 的批量提交路径读写。
        bool? fanSwitchEnabled = FirstOptionalBool(o, "FAN_FanSwitchSpeedEnabled", "FanSwitchSpeedEnabled");
        if (fanSwitchEnabled is not null)
        {
            FanSwitchSpeedEnabled = fanSwitchEnabled.Value;
            FanSwitchSpeedSeen = true;
        }
        int fanSwitchSpeed = OptionalInt(o, -1, "FAN_FanSwitchSpeed", "FanSwitchSpeed");
        if (fanSwitchSpeed >= 0)
        {
            FanSwitchSpeed = fanSwitchSpeed;
            FanSwitchSpeedSeen = true;
        }
        int fanSwitchMin = OptionalInt(o, -1,
            "FAN_FanSwitchSpeedMinimum", "FAN_FanSwitchSpeedMin", "FanSwitchSpeedMinimum");
        int fanSwitchMax = OptionalInt(o, -1,
            "FAN_FanSwitchSpeedMaximum", "FAN_FanSwitchSpeedMax", "FanSwitchSpeedMaximum");
        if (fanSwitchMin >= 0) FanSwitchSpeedMinimum = fanSwitchMin;
        if (fanSwitchMax >= 0) FanSwitchSpeedMaximum = fanSwitchMax;

        // 游戏白名单：官方用它在检测到白名单进程时自动切模式。
        // 值是 JSON 数字 0/1（实测），ParseFlexibleBool 能正确处理。
        // 只有真解析出布尔值才置 Seen——这一项在 SupportsQuickSwitch 里
        // 没有任何别的门禁兜底，无法识别的值会直接变成一个假开关。
        if (OptionalBool(o, "GameWhitelistSwitch") is bool gameWhitelist)
        {
            QuickSwitches["gamewhitelist"] = gameWhitelist;
            GameWhitelistSeen = true;
        }

        // CPU 高级性能 / 超频菜单总闸。
        //
        // 状态字段名是 CPU_PerformanceAndOverClockMenuSwitch——**带下划线**。
        // 这里曾经写成 CPUPerformanceAndOverClockMenuSwitch（那是 GCUService 内部
        // 结构体的成员名，没有被序列化成这个名字发出来），于是 HasField 永远为假、
        // CpuAdvancedPerformanceSeen 恒 false，整项在界面上从来没出现过。
        // 实测载荷：CPU_PerformanceAndOverClockMenuSwitch=1（官方 CCUWinUI L52738
        // 读的也是 MyRamFan1p5.CPU_PerformanceAndOverClockMenuSwitch == "1"）。
        // 旧名留作别名，避免老固件真用了那个名字时又漏掉。
        JToken? cpuAdvPerf = FirstField(o,
            "CPU_PerformanceAndOverClockMenuSwitch", "CPUPerformanceAndOverClockMenuSwitch");
        if (cpuAdvPerf is not null)
        {
            QuickSwitches["cpuadvperf"] = ParseFlexibleBool(cpuAdvPerf.ToString()) == true;
            CpuAdvancedPerformanceSeen = true;
        }
        // 超频总开关的支持位。官方门禁是 IsOcSettingsSupport || HWOCSupport
        // （CCUWinUI L52736），前者来自注册表 ItemSupport\OcSettingsSupport、
        // 后者来自 LCHWOC/Status 的 Support 且默认 true。Fan/Status 同帧也带了
        // OcSupport（实测 True），它是这台机器上唯一随状态刷新的那份证据，
        // 所以一并解析——只靠构造时读一次注册表会漏掉 BIOS 里开了但注册表没写的机型。
        bool? ocSupport = FirstOptionalBool(o, "OcSupport", "OcSettingsSupport");
        if (ocSupport is not null) OverclockMenuSupport = ocSupport;

        // GPU Whisper（静音）模式：**只读**，不提供开关入口。
        //
        // 服务端确实完整上报这一族（开发机实测 GPU_WhisperModeSupport=true、
        // GPU_WhisperModeSwitch="0"、GPU_WhisperModeSetting="0"、
        // GPU_WhisperModeMinFps_QUIETER/_QUIET/_BALANCED="30"/"40"/"60"、
        // MinFpsMaximum/Minimum="60"/"30"），但官方 5.56 界面里
        // GpuWhisperModeSwitch / GpuWhisperModeSetting 只有属性声明、
        // **没有任何下发点**，所以拿不到命令的真实形状。
        //
        // 曾经按猜测实现过一个开关（走 Fan/Control 的 SET_OPERATING_MODE_DETAIL），
        // 真机验证证伪：下发后回读毫无变化。留着就是一个点了没反应的空头开关，
        // 所以入口已撤掉，这里只保留状态解析供诊断与将来接线用。
        //
        // 另外注意 Switch 与 Setting 是两件事：Switch 是开关，
        // Setting 是静音档位（对应三档 MinFps），此前误把 Setting 当开关读。
        bool? whisperSupport = OptionalBool(o, "GPU_WhisperModeSupport");
        if (whisperSupport is not null) WhisperModeSupport = whisperSupport;
        if (HasField(o, "GPU_WhisperModeSwitch"))
        {
            WhisperMode = OptionalBool(o, "GPU_WhisperModeSwitch") == true;
            WhisperModeSeen = true;
        }
        WhisperModeLevel = OptionalInt(o, WhisperModeLevel, "GPU_WhisperModeSetting");
        // GCU firmware revisions are inconsistent here: some use the
        // OC suffix and numeric 0/1 values, while others omit the
        // suffix and return JSON booleans. Parse all known variants
        // through the same case-insensitive helpers so a valid
        // write is not reported as unconfirmed solely because of
        // the status schema used by the model.
        bool? ocSwitch = FirstOptionalBool(o,
            "OverClockingSwitch", "OverclockingSwitch", "GPU_OverClockingSwitch", "GPU_OverclockingSwitch");
        if (ocSwitch.HasValue) OcSwitch = ocSwitch.Value;
        GpuCoreClockOffset = OptionalInt(o, GpuCoreClockOffset,
            "GPU_CoreClockOffsetOC", "GPU_CoreClockOffset", "GpuCoreClockOffsetOC", "GpuCoreClockOffset",
            "GPU_CoreOffsetOC", "GPU_CoreOffset", "GpuCoreOffsetOC", "GpuCoreOffset");
        GpuMemClockOffset = OptionalInt(o, GpuMemClockOffset,
            "GPU_MemoryClockOffsetOC", "GPU_MemoryClockOffset", "GpuMemoryClockOffsetOC", "GpuMemoryClockOffset",
            "GPU_MemoryOffsetOC", "GPU_MemoryOffset", "GpuMemoryOffsetOC", "GpuMemoryOffset");
        // 过去这里是裸字符串比较（只认 "True"/"true"/"1"），"ON"/"ENABLE" 会被判成
        // false，而且无论能否识别都把 FanRespectiveSeen 置位——SupportsFanRespective
        // 完全依赖这个标志，于是能力被误报为支持而状态永远是关闭。
        bool? fanRespective = OptionalBool(o, "FanControlRespective");
        if (fanRespective.HasValue) { FanRespective = fanRespective.Value; FanRespectiveSeen = true; }
        // 字段全部写完之后才计版本。过去它在 case 第一行自增，于是解析中途抛异常时
        // 外部按 FanStatusVersion 轮询的代码会认为「来了一帧新状态」，
        // 而模式、功耗墙、TCC、TGP、超频回读全是旧值。
        Interlocked.Increment(ref _fanStatusVersion);
        if (modeChangedTo is int changedMode)
            RaiseIsolated(ModeChanged, nameof(ModeChanged), changedMode);
        RaiseIsolated(CustomModeChanged, nameof(CustomModeChanged));
    }

    private void OnLchwocStatus(JObject o)
    {
        LchwocStatusSeen = true;
        bool? support = OptionalBool(o, "Support");
        if (support.HasValue)
        {
            LchwocSupportReported = support.Value;
            LchwocSupport = support.Value;
        }
        bool? enabled = OptionalBool(o, "Enable");
        if (enabled.HasValue) LchwocEnable = enabled.Value;

        // Older GCU builds put some HWOC values on this topic instead of Fan/Status.
        UpdateGpuOffsetRanges(o);
        GpuCoreClockOffset = OptionalInt(o, GpuCoreClockOffset,
            "GPU_CoreClockOffsetOC", "GPU_CoreClockOffset", "GpuCoreClockOffsetOC", "GpuCoreClockOffset",
            "GPU_CoreOffsetOC", "GPU_CoreOffset", "GpuCoreOffsetOC", "GpuCoreOffset");
        GpuMemClockOffset = OptionalInt(o, GpuMemClockOffset,
            "GPU_MemoryClockOffsetOC", "GPU_MemoryClockOffset", "GpuMemoryClockOffsetOC", "GpuMemoryClockOffset",
            "GPU_MemoryOffsetOC", "GPU_MemoryOffset", "GpuMemoryOffsetOC", "GpuMemoryOffset");
        bool? lchwocSwitch = FirstOptionalBool(o,
            "OverClockingSwitch", "OverclockingSwitch", "GPU_OverClockingSwitch", "GPU_OverclockingSwitch");
        if (lchwocSwitch.HasValue) OcSwitch = lchwocSwitch.Value;
        Logger.WriteLineIfChanged("hwoc-status", $"HWOC: support={LchwocSupportReported?.ToString() ?? "unknown"} enable={LchwocEnable}");
        RaiseIsolated(CustomModeChanged, nameof(CustomModeChanged));
    }

    internal async Task<bool> WaitForStateAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        // 快速路径同样要走兜底求值：这里过去是裸 predicate()，谓词抛异常会直接穿透，
        // 连订阅事件都还没发生。
        if (SafePredicate(predicate)) return true;

        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(string _)
        {
            // 谓词异常过去走 TrySetException，然后从 await 处穿透给调用方 ——
            // 而 SetGpuMode / SetBatteryProtection / SetPl1Pl2 的签名是返回 bool，
            // 调用方不会为「确认失败」准备异常处理。这里把它归一化成「未确认」。
            try
            {
                if (predicate()) completed.TrySetResult(true);
            }
            catch (Exception ex)
            {
                Logger.WriteLineThrottled(
                    "wait-predicate", "WaitForStateAsync predicate failed: " + ex.Message, 5000);
                completed.TrySetResult(false);
            }
        }

        StateChanged += Check;
        try
        {
            Check(string.Empty); // Close the race between the first check and event subscription.
            try
            {
                return await completed.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return SafePredicate(predicate);
            }
            catch (OperationCanceledException)
            {
                // 取消同样不该变成异常：调用方语义是 bool。超时那条路径会补判一次，
                // 取消这条路径过去直接抛，行为不一致。
                return SafePredicate(predicate);
            }
        }
        finally
        {
            StateChanged -= Check;
        }
    }

    /// <summary>
    /// 逐个调用订阅者，单个订阅者抛异常不影响其余订阅者，也不会中断后续的通知链。
    /// WinForms 里跨线程访问控件、访问已释放控件都很常见，必须假定订阅者会抛。
    /// </summary>
    static void RaiseIsolated(Action? handlers, string eventName)
    {
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
        {
            try { handler(); }
            catch (Exception ex)
            {
                Logger.WriteLineThrottled(
                    "handler-fail-" + eventName, $"{eventName} handler failed: {ex.Message}", 5000);
            }
        }
    }

    /// <inheritdoc cref="RaiseIsolated(Action?, string)"/>
    static void RaiseIsolated(Action<string>? handlers, string eventName, string argument)
    {
        if (handlers is null) return;
        foreach (Action<string> handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try { handler(argument); }
            catch (Exception ex)
            {
                Logger.WriteLineThrottled(
                    "handler-fail-" + eventName, $"{eventName} handler failed: {ex.Message}", 5000);
            }
        }
    }

    /// <inheritdoc cref="RaiseIsolated(Action?, string)"/>
    static void RaiseIsolated(Action<int>? handlers, string eventName, int argument)
    {
        if (handlers is null) return;
        foreach (Action<int> handler in handlers.GetInvocationList().Cast<Action<int>>())
        {
            try { handler(argument); }
            catch (Exception ex)
            {
                Logger.WriteLineThrottled(
                    "handler-fail-" + eventName, $"{eventName} handler failed: {ex.Message}", 5000);
            }
        }
    }

    /// <summary>兜底求值：谓词抛异常时按「未满足」处理，绝不把异常带给返回 bool 的调用方。</summary>
    static bool SafePredicate(Func<bool> predicate)
    {
        try { return predicate(); }
        catch (Exception ex)
        {
            Logger.WriteLineThrottled(
                "wait-predicate", "WaitForStateAsync predicate failed: " + ex.Message, 5000);
            return false;
        }
    }

    internal void NotifyCustomModeChanged() => CustomModeChanged?.Invoke();

    // ---- 控制 API（UI 调用；action 常量对齐原版 Define 字典）----

    /// <summary>
    /// 一次模式切换要发的两条命令。<see cref="SetMode"/> 与
    /// <c>MechrevoService.SwitchMode</c> 共用这一份，避免两条路径的载荷各自漂移。
    ///
    /// 官方 ModeSwitchCommand（CCUWinUI L55090-55146）的形状：
    /// <c>Fan/Control {Action, ProfileIndex}</c>（ProfileIndex 是 JSON 数字），
    /// 紧跟 <c>LCHWOC/Control {IsNormalRun}</c>（Gaming=1 / Turbo=2 / Office=0）
    /// 或自定义档的 <c>{IsCustomRun=true}</c>。
    /// </summary>
    internal static (Dictionary<string, object> Fan, Dictionary<string, object> Overclock) BuildModeSwitchPayloads(
        string action, int operatingMode, int profileIndex, bool custom) =>
        (
            new Dictionary<string, object> { ["Action"] = action, ["ProfileIndex"] = profileIndex },
            custom
                ? new Dictionary<string, object> { ["IsCustomRun"] = true }
                : new Dictionary<string, object> { ["IsNormalRun"] = operatingMode }
        );

    public async Task SetMode(int mode)
    {
        // G-Helper 模式枚举：0=Balanced 1=Turbo 2=Silent → 机械革命 Action
        var action = mode switch
        {
            0 => "OPERATING_GAMING_MODE",
            1 => "OPERATING_TURBO_MODE",
            2 => "OPERATING_OFFICE_MODE",
            3 => "OPERATING_CUSTOM_MODE",
            _ => "OPERATING_GAMING_MODE",
        };
        int expectedOpMode = mode switch { 0 => 1, 1 => 2, 2 => 0, 3 => 3, _ => 1 };
        bool custom = mode == 3;
        var (fanPayload, overclockPayload) = BuildModeSwitchPayloads(
            action, expectedOpMode, custom ? Math.Clamp(CustomProfileIndex, 0, 3) : 0, custom);
        // 先武装再发布是有意的：命令一旦上路，切换前排队的旧状态包随时可能到达。
        // 但发布失败时必须解除武装，否则这 8 秒内一切真实模式上报都会被静默丢弃，
        // UI 会显示一个从未下发成功的模式。
        MarkModeSwitchPending(expectedOpMode);
        Logger.WriteLine($"SetMode({mode}) -> {action}");
        try
        {
            // 实测：缺少 ProfileIndex 字段时服务端切换后回滚——载荷必须对齐原版 {Action, ProfileIndex}
            await Publish(MqttTopics.FanControl, fanPayload);
            // 超频通道的运行标记：不发这一条，从自定义模式切回普通模式时
            // 硬件那边还留在「自定义运行」，超频参数不复位。
            await Publish(MqttTopics.LchwocControl, overclockPayload);
        }
        catch
        {
            ClearModeSwitchPending();
            throw;
        }
        if (!await WaitForStateAsync(() => OperatingMode == expectedOpMode, TimeSpan.FromMilliseconds(180)))
        {
            await Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
            await WaitForStateAsync(() => OperatingMode == expectedOpMode, TimeSpan.FromMilliseconds(900));
        }
        await Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GET_FAN_SPEED_CURVE_SETTING" });
    }

    // SetGpuPowerSaving / SetDisconnectMonitor / SetDcOnce 也一并删除：
    // MechrevoService 里只有 SwitchGpuPowerSaving 保留（带日志版本、仍有调用方）；
    // SwitchDisconnectMonitor / SwitchDcOnce 已随「外接屏断独显 / 电池切独显」两个快捷开关整条移除。

    // 这里曾经有 SetRefreshRate / SetUsbCharger / SetQuickSwitch / SetFanBoost 四个方法，
    // 全部删除：它们是与 MechrevoService 的 SwitchRefreshRate / SwitchUsbCharger /
    // SwitchQuick / SwitchFanBoost 平行的**第二张动作表**，只发不确认，而且零调用方。
    //
    // 留着的害处不是占空间，是它会静默腐化：SetQuickSwitch 那张表停在 11 个开关，
    // 后来补的 8 个官方开关（touchpadtoggle / singlecolorkb / uni / omni /
    // powerlight / batterylogo / gamewhitelist / cpuadvperf）一个都没有，
    // 而且 gamewhitelist 与 cpuadvperf 根本不该走 Setting/Control。
    // 谁要是照它接线，就会得到一个"点了没反应"的开关。
    // 需要下发就走 MechrevoService，那边每条命令都带回读确认。

    public async Task<bool> SetBatteryProtection(int mode)
    {
        if (mode is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(mode));
        // 0=性能(满充) 1=平衡 2=健康 → 机械革命 Action
        var action = mode switch
        {
            0 => "PERFORMANCEDMODE",
            1 => "BALANCEDMODE",
            2 => "HEALTHYMODE",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        Logger.WriteLine($"SetBatteryProtection({mode}) -> {action}");
        await _controlLock.WaitAsync();
        try
        {
            if (BatteryProtection == mode) return true;
            await Publish(MqttTopics.BatteryProtectionControl, new Dictionary<string, object> { ["Action"] = action });
            if (await WaitForStateAsync(() => BatteryProtection == mode, TimeSpan.FromMilliseconds(180))) return true;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await Publish(MqttTopics.BatteryProtectionControl, new Dictionary<string, object> { ["Report"] = "GET" });
                if (await WaitForStateAsync(
                    () => BatteryProtection == mode,
                    TimeSpan.FromMilliseconds(attempt == 0 ? 500 : 700))) return true;
            }
            Logger.WriteLine($"SetBatteryProtection not confirmed: expected={mode} actual={BatteryProtection}");
            return false;
        }
        finally { _controlLock.Release(); }
    }

    public async Task<bool> SetGpuMode(int mode)
    {
        if (mode is < MechrevoService.GpuIGpu or > MechrevoService.GpuAuto) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!CanSwitchGpuMode(mode))
        {
            Logger.WriteLine($"SetGpuMode({mode}) rejected: unsupported by this device");
            return false;
        }
        bool useHotSwitch = mode == MechrevoService.GpuIGpu && SupportsGpuHotSwap;
        bool useMuxTarget = mode == MechrevoService.GpuIGpu && SupportsDgpuDirect &&
            !SupportsIgpuOnly && !useHotSwitch;
        Dictionary<string, object> payload = MechrevoService.CreateGpuSwitchPayload(
            mode, SupportsDgpuDirect, SupportsIgpuOnly, useHotSwitch);
        string action = payload["Action"].ToString() ?? "";
        Logger.WriteLine($"SetGpuMode({mode}) -> {action}");
        await _controlLock.WaitAsync();
        try
        {
            long gpuModeVersion = GpuModeStatusVersion;
            bool leavingDirect = GpuMode == MechrevoService.GpuDgpu && mode != MechrevoService.GpuDgpu;
            if (mode == MechrevoService.GpuDgpu)
            {
                await Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "IGPU_ONLY_CONNECT_RB_OFF", ["SetToWMIEC"] = "OK" });
                await Task.Delay(300);
            }
            await Publish(MqttTopics.SettingControl, payload);
            if (leavingDirect && !useMuxTarget)
            {
                await Task.Delay(300);
                await Publish(MqttTopics.SettingControl, new Dictionary<string, object>
                {
                    ["Action"] = mode == MechrevoService.GpuIGpu
                        ? "DGPU_DIRECT_CONNECT_TOGGLE_IGPU"
                        : "DGPU_DIRECT_CONNECT_TOGGLE_OFF",
                });
            }
            bool TargetReached() => GpuMode == mode &&
                (!useMuxTarget || GpuModeStatusVersion > gpuModeVersion);
            if (await WaitForStateAsync(TargetReached, TimeSpan.FromMilliseconds(1200))) return true;
            for (int attempt = 0; attempt < 7; attempt++)
            {
                await Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                if (await WaitForStateAsync(
                    TargetReached,
                    TimeSpan.FromMilliseconds(1800))) return true;
                if (attempt is 1 or 3 or 5) await Publish(MqttTopics.SettingControl, payload);
            }
            Logger.WriteLine($"SetGpuMode not confirmed: expected={mode} actual={GpuMode}");
            return false;
        }
        finally { _controlLock.Release(); }
    }

    public async Task SetFanCurve(int type, int[] duties)
    {
        if (type is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(type));
        var safeDuties = NormalizeFanCurve(duties, type == 0 ? CpuCurveUpT : GpuCurveUpT);
        // 实测：SET_FAN_SPEED_CURVE_SETTING 的 T0..T15 全是占空比值（16 档温度由服务端固定）
        var payload = new Dictionary<string, object>
        {
            ["Action"] = "SET_FAN_SPEED_CURVE_SETTING",
            ["Name"] = TableName.Length > 0 ? TableName : CurveName,
            ["Type"] = type == 0 ? "CPU" : "GPU",
        };
        for (int i = 0; i < 16; i++)
            payload[$"T{i}"] = safeDuties[i].ToString();
        await Publish(MqttTopics.FanControl, payload);
    }

    public static int[] NormalizeFanCurve(IReadOnlyList<int> duties, IReadOnlyList<byte> temperatures)
    {
        var result = new int[16];
        int validCount = Enumerable.Range(0, Math.Min(16, temperatures.Count))
            .FirstOrDefault(i => temperatures[i] == 255, -1);
        if (validCount < 2) validCount = Math.Min(11, Math.Max(2, duties.Count));

        result[0] = Math.Clamp(duties.Count > 0 ? duties[0] : 0, 0, 100);
        for (int i = 1; i < validCount; i++)
        {
            int requested = i < duties.Count ? duties[i] : result[i - 1];
            // Every editable point, including the first ramp point (often 48 C),
            // is a user value in the full 0-100% range. Keep the curve monotonic
            // without reintroducing the old hard 30% floor.
            result[i] = Math.Max(result[i - 1], Math.Clamp(requested, 0, 100));
        }
        // The firmware accepts a user-defined final effective point.  Keep that
        // value for protocol slots after the temperature sentinel, matching the
        // official console instead of forcing the final point to 100%.
        int trailingDuty = result[validCount - 1];
        for (int i = validCount; i < result.Length; i++) result[i] = trailingDuty;
        return result;
    }

    public async Task<bool> SetPl1Pl2(int pl1, int pl2)
    {
        bool valid = Pl1Minimum >= 0 && Pl1Maximum >= Pl1Minimum && pl1 >= Pl1Minimum && pl1 <= Pl1Maximum
            && Pl2Minimum >= 0 && Pl2Maximum >= Pl2Minimum && pl2 >= Pl2Minimum && pl2 <= Pl2Maximum;
        if (!valid)
        {
            Logger.WriteLine($"SetPl1Pl2 rejected outside capability: PL1={pl1} [{Pl1Minimum},{Pl1Maximum}], PL2={pl2} [{Pl2Minimum},{Pl2Maximum}]");
            return false;
        }
        await _controlLock.WaitAsync();
        try
        {
            // The vendor service applies multi-field payloads inconsistently, so write one field per command.
            string pl1Key = UsesAmdPowerFields ? "CpuAmdSPL" : "PL1";
            string pl2Key = UsesAmdPowerFields ? "CpuAmdSPPT" : "PL2";
            await Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "SET_OPERATING_MODE_DETAIL", [pl1Key] = pl1.ToString() });
            await Task.Delay(120);
            await Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "SET_OPERATING_MODE_DETAIL", [pl2Key] = pl2.ToString() });
            if (Pl1 == pl1 && Pl2 == pl2) return true;
            await Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
            return await WaitForStateAsync(() => Pl1 == pl1 && Pl2 == pl2, TimeSpan.FromMilliseconds(700));
        }
        finally { _controlLock.Release(); }
    }

    /// <summary>
    /// PL4（瞬时功耗墙）。范围用运行时的 Pl4Minimum/Pl4Maximum，它们已按 Pl4Double 换算过。
    /// Pl4Double 机型上线上值是面向用户瓦数的一半，奇数瓦会在折半时被截断，
    /// 所以确认的目标是折半再还原后的「实际可达值」，而不是调用方原始的入参。
    /// AMD 机型上这一项走 CpuAmdFPPT（见 <see cref="Pl4WireKey"/>），原样瓦数、原样回读。
    /// </summary>
    public async Task<bool> SetPl4(int pl4)
    {
        bool valid = Pl4Minimum >= 0 && Pl4Maximum >= Pl4Minimum && pl4 >= Pl4Minimum && pl4 <= Pl4Maximum;
        if (!valid)
        {
            Logger.WriteLine($"SetPl4 rejected outside capability: PL4={pl4} [{Pl4Minimum},{Pl4Maximum}]");
            return false;
        }
        string key = Pl4WireKey;
        int wire = Pl4ToWire(pl4);
        int effective = Pl4Effective(pl4);
        if (effective != pl4)
            Logger.WriteLine($"SetPl4({pl4}) quantised to {effective} W: this model reports PL4 in half-watt units.");
        await _controlLock.WaitAsync();
        try
        {
            await Publish(MqttTopics.FanControl, new Dictionary<string, object>
            {
                ["Action"] = "SET_OPERATING_MODE_DETAIL",
                [key] = wire.ToString(),
            });
            if (Pl4 == effective) return true;
            await Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
            return await WaitForStateAsync(() => Pl4 == effective, TimeSpan.FromMilliseconds(700));
        }
        finally { _controlLock.Release(); }
    }

    /// <summary>
    /// 把灵敏度毫秒值对齐到 EC 的 100 ms 档位并夹到运行时范围内。
    /// EC 侧是单字节的百毫秒计数，非整档的入参会被固件截断，
    /// 不先对齐就会拿一个永远回读不到的值去比对，把成功的写入判成失败。
    /// </summary>
    internal int QuantiseFanSwitchSpeed(int milliseconds)
    {
        int aligned = milliseconds / FanSwitchSpeedStepMs * FanSwitchSpeedStepMs;
        return Math.Clamp(aligned, FanSwitchSpeedMinimum, FanSwitchSpeedMaximum);
    }

    /// <summary>
    /// 单条命令的发布上限。QoS2 要等 PUBCOMP，MQTTnet 默认约 10 秒；
    /// 而 SetGpuMode 在一次操作里最多发十几条，全程持有 _controlLock，
    /// 期间 SetPl1Pl2 / SetBatteryProtection 全部排队。
    /// </summary>
    internal static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(5);

    public Task Publish(string topic, object payload) =>
        Publish(topic, payload, MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce);

    /// <summary>
    /// 发布一条命令。
    ///
    /// 默认 QoS2（ExactlyOnce）：控制命令重复投递会造成开关翻转两次，必须精确一次。
    /// 但退出路径上的清理命令要用 <see cref="MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce"/>——
    /// QoS2 要走 PUBLISH→PUBREC→PUBREL→PUBCOMP 四步才算完成，而退出时只等几百毫秒就会
    /// 断开会话；因为 CleanSession=true，未完成的 PUBREL 会被永久丢弃，命令等于没发。
    /// </summary>
    public async Task Publish(string topic, object payload, MQTTnet.Protocol.MqttQualityOfServiceLevel qos)
    {
        if (_publishOverride is not null)
        {
            await _publishOverride(topic, payload).ConfigureAwait(false);
            return;
        }
        IMqttClient? client = _client;
        if (client is null || !IsConnected) throw new MqttPublishFailedException(topic, "MQTT 未连接");

        try
        {
            await client.PublishStringAsync(topic, Newtonsoft.Json.JsonConvert.SerializeObject(payload), qos, false)
                .WaitAsync(PublishTimeout)
                .ConfigureAwait(false);
        }
        catch (MqttPublishFailedException) { throw; }
        catch (Exception ex)
        {
            // 统一异常类型。过去检查与发布之间有 TOCTOU 窗口：断线正好落在中间时
            // MQTTnet 会抛自己的异常类型，调用方拿到的类型不确定（一半
            // InvalidOperationException、一半 MQTTnet 异常），没法可靠地按类型处理。
            throw new MqttPublishFailedException(topic, ex.Message, ex);
        }
    }

    /// <summary>表名 → 模式：M1*=Gaming(0), M2*=Office(1), M3*=Turbo(2)，其他（M4* 自定义等）→ -1（不推断）。</summary>
    static int TableNameToMode(string name)
    {
        if (name.StartsWith("M1")) return 0;
        if (name.StartsWith("M2")) return 1;
        if (name.StartsWith("M3")) return 2;
        return -1;
    }

    /// <summary>全 0 表表示 GCU 尚未创建用户曲线；其他形状均视为用户的有效设置。</summary>
    static bool IsUninitializedCurve(byte[] duty) => duty.Take(8).All(value => value == 0);

    static (byte[] upT, byte[] duty) ParseCurve(JArray? arr)
    {
        var upT = new byte[16];
        var duty = new byte[16];
        if (arr is null) return (upT, duty);
        for (int i = 0; i < 16 && i < arr.Count; i++)
        {
            var node = arr[i] as JObject;
            if (node is null) continue;
            // 未知值按 0 处理；越界值做范围钳制而不是 byte 截断（旧代码里 300 会变成 44）。
            // upT 保留 255 作为"有效点结束"的哨兵，所以上界是 byte.MaxValue；duty 是百分比。
            upT[i] = (byte)Math.Clamp(Int(node, "UpT"), 0, byte.MaxValue);
            duty[i] = (byte)Math.Clamp(Int(node, "Duty"), 0, 100);
        }
        return (upT, duty);
    }

    /// <summary>
    /// 读取整数字段。JSON 字段名大小写不敏感（服务端可能发 upT/duty 或 UpT/Duty）。
    /// 缺失、null、以及无法解析的值统一返回 -1（未知）。
    /// 这里的 -1 是刻意的：旧实现在无法解析时返回 0，会把"未知"伪装成一个看起来合理的
    /// 真实档位（OperatingMode=0 是办公档、HealthProtectionStatus=0 是满充、功耗=0 W），
    /// 从而绕过所有 `>= 0` 的有效性守卫并误导用户操作。
    /// </summary>
    internal static int Int(JObject o, string key)
    {
        JToken? value = o.GetValue(key, StringComparison.OrdinalIgnoreCase);
        if (value is null || value.Type == JTokenType.Null) return -1;
        return ParseOptionalInt(value.ToString()) ?? -1;
    }

    /// <summary>
    /// 整数解析的单一入口：先按整数解析，再容忍 "62.0" 这类带小数点的固件写法，
    /// 其余一律视为未知（返回 null），不猜测具体值。
    /// </summary>
    internal static int? ParseOptionalInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string trimmed = text.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return parsed;
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) &&
            number >= int.MinValue && number <= int.MaxValue)
            return (int)Math.Round(number);
        return null;
    }

    /// <summary>
    /// 读取浮点字段。无法解析时返回 fallback 而不是抛异常——旧实现用
    /// <c>Value&lt;double&gt;()</c>，字符串数值会抛 FormatException 并中断整帧解析。
    /// </summary>
    static double Double(JObject o, string key, double fallback)
    {
        JToken? token = o.GetValue(key, StringComparison.OrdinalIgnoreCase);
        if (token is null || token.Type == JTokenType.Null) return fallback;
        return double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : fallback;
    }

    public bool EnsureDirectGpuOverclock()
    {
        lock (_gpuOverclockLock)
        {
            if (_gpuOverclock is not null)
            {
                if (!_gpuOverclock.Refresh()) return false;
                _directGpuOverclockEnabled ??=
                    _gpuOverclock.CoreOffset.Current != 0 || _gpuOverclock.MemoryOffset.Current != 0;
                return _gpuOverclock.IsAvailable;
            }
            if (_gpuOverclockFactory is null) return false;

            try
            {
                _gpuOverclock = _gpuOverclockFactory();
                if (_gpuOverclock is null || !_gpuOverclock.IsAvailable)
                {
                    _gpuOverclock?.Dispose();
                    _gpuOverclock = null;
                    return false;
                }

                _directGpuOverclockEnabled =
                    _gpuOverclock.CoreOffset.Current != 0 || _gpuOverclock.MemoryOffset.Current != 0;
                Logger.WriteLine($"Direct GPU overclock backend present via {_gpuOverclock.Name}: " +
                    $"core={_gpuOverclock.CoreOffset.Minimum}..{_gpuOverclock.CoreOffset.Maximum}, " +
                    $"memory={_gpuOverclock.MemoryOffset.Minimum}..{_gpuOverclock.MemoryOffset.Maximum}, " +
                    $"elevated={_isProcessElevated()}, writable={_gpuOverclock.IsAvailable && _isProcessElevated()}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Direct GPU overclock initialization failed: " + ex.Message);
                _gpuOverclock?.Dispose();
                _gpuOverclock = null;
                return false;
            }
        }
    }

    internal bool TryGetDirectGpuOverclockReadback(out int core, out int memory)
    {
        core = memory = 0;
        lock (_gpuOverclockLock)
        {
            if (_gpuOverclock is null && _gpuOverclockFactory is not null)
            {
                try { _gpuOverclock = _gpuOverclockFactory(); }
                catch (Exception ex)
                {
                    Logger.WriteLine("Direct GPU OC readback initialization failed: " + ex.Message);
                    return false;
                }
            }
            if (_gpuOverclock is null) return false;

            // NVIDIA can still expose P-state offsets after a write is denied.
            _gpuOverclock.Refresh();
            core = _gpuOverclock.CoreOffset.Current;
            memory = _gpuOverclock.MemoryOffset.Current;
            return _gpuOverclock.CoreOffset.IsAdjustable || _gpuOverclock.MemoryOffset.IsAdjustable;
        }
    }

    int GetEffectiveDirectGpuCoreOffset()
    {
        int? elevatedReadback;
        lock (_gpuOverclockLock) elevatedReadback = _elevatedGpuCoreReadback;
        if (TryGetDirectGpuOverclockReadback(out int core, out _) &&
            (!elevatedReadback.HasValue || core == elevatedReadback.Value))
            return core;
        return elevatedReadback ?? GpuCoreClockOffset;
    }

    int GetEffectiveDirectGpuMemoryOffset()
    {
        int? elevatedReadback;
        lock (_gpuOverclockLock) elevatedReadback = _elevatedGpuMemoryReadback;
        if (TryGetDirectGpuOverclockReadback(out _, out int memory) &&
            (!elevatedReadback.HasValue || memory == elevatedReadback.Value))
            return memory;
        return elevatedReadback ?? GpuMemClockOffset;
    }

    void ClearElevatedGpuReadback(bool core, bool memory)
    {
        lock (_gpuOverclockLock)
        {
            if (core) _elevatedGpuCoreReadback = null;
            if (memory) _elevatedGpuMemoryReadback = null;
        }
    }

    static bool ElevatedGpuOverclockFieldMatches(
        string key, int value, GpuOverclockApplyResult result) => key switch
        {
            "GpuCoreClockOffsetOC" => result.CoreOffset == value,
            "GpuMemoryClockOffsetOC" => result.MemoryOffset == value,
            // The helper has no separate enabled field. A successful non-zero
            // request is already gated by the offsets it read back; disabling
            // requires both offsets to be zero.
            "OverClockingSwitch" => result.Success &&
                IsGpuOverclockEnableConfirmed(value, result.CoreOffset, result.MemoryOffset),
            _ => false,
        };

    internal static bool IsGpuOverclockEnableConfirmed(int requestedSwitch, int coreOffset, int memoryOffset) =>
        requestedSwitch == 0
            ? coreOffset == 0 && memoryOffset == 0
            : requestedSwitch == 1 && (coreOffset != 0 || memoryOffset != 0);

    internal bool DriverGpuOverclockFieldMatches(string key, int value)
    {
        if (!TryGetDirectGpuOverclockReadback(out int core, out int memory)) return false;
        return key switch
        {
            "GpuCoreClockOffsetOC" => core == value,
            "GpuMemoryClockOffsetOC" => memory == value,
            "OverClockingSwitch" => IsGpuOverclockEnableConfirmed(value, core, memory),
            _ => false,
        };
    }

    internal async Task<bool> ApplyGpuOverclockAsync(
        IReadOnlyCollection<KeyValuePair<string, string>> fields,
        CancellationToken cancellationToken = default)
    {
        int? core = null;
        int? memory = null;
        bool? enabled = null;
        foreach (KeyValuePair<string, string> field in fields)
        {
            if (!int.TryParse(field.Value, out int value)) return false;
            switch (field.Key)
            {
                case "GpuCoreClockOffsetOC": core = value; break;
                case "GpuMemoryClockOffsetOC": memory = value; break;
                case "OverClockingSwitch": enabled = value == 1; break;
            }
        }

        // Validate every requested P-state offset before changing the switch.
        // Otherwise a request such as switch=1 + memory=500 on a driver capped
        // at 250 can leave overclocking enabled at a zero offset while reporting
        // only a generic write failure.
        foreach (KeyValuePair<string, string> field in fields)
        {
            if (int.TryParse(field.Value, out int value) &&
                field.Key is "GpuCoreClockOffsetOC" or "GpuMemoryClockOffsetOC" &&
                !DirectGpuOverclockFieldWithinDriverRange(field.Key, value))
            {
                Logger.WriteLine($"GPU OC rejected outside independent driver range: {field.Key}={value}");
                return false;
            }
        }

        ClearElevatedGpuReadback(core.HasValue, memory.HasValue);

        if (GpuOverclockRequiresElevation)
        {
            // 非提权的 NVAPI 写入恒定失败（NVAPI_INVALID_USER_PRIVILEGE）。不再尝试、不再
            // 弹出提权助手（owner 选择：不自动 UAC），只在日志里给出诚实原因，UI 显示
            // 「超频需要管理员权限」并以重启到管理员进程作为显式入口。
            Logger.WriteLine("GPU OC refused: the NVIDIA direct write requires an elevated process " +
                "(restart as administrator); no write attempted, no UAC prompt raised.");
            return false;
        }

        void MarkEnableUnconfirmed()
        {
            if (enabled != true) return;
            // A bool API cannot represent "unknown".  On a failed enable
            // confirmation, prefer a visible disabled state over claiming that
            // the driver accepted an offset it did not read back.
            lock (_gpuOverclockLock) _directGpuOverclockEnabled = false;
        }

        bool locallyApplied = true;
        foreach (KeyValuePair<string, string> field in fields)
        {
            if (!int.TryParse(field.Value, out int value) || !TrySetDirectGpuOverclockField(field.Key, value))
            {
                locallyApplied = false;
                break;
            }
        }
        if (locallyApplied && fields.All(field =>
            int.TryParse(field.Value, out int value) && DriverGpuOverclockFieldMatches(field.Key, value)))
            return true;
        MarkEnableUnconfirmed();

        if (_elevatedGpuOverclockApplier is null)
        {
            Logger.WriteLine("GPU OC direct write was rejected and no elevated helper is available.");
            return false;
        }

        GpuOverclockApplyResult result = await _elevatedGpuOverclockApplier
            .ApplyAsync(new GpuOverclockApplyRequest(core, memory, enabled), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            MarkEnableUnconfirmed();
            Logger.WriteLine("GPU OC elevated write failed: " + result.Error);
            return false;
        }

        lock (_gpuOverclockLock)
        {
            _directGpuOverclockEnabled = enabled ?? (result.CoreOffset != 0 || result.MemoryOffset != 0);
            _elevatedGpuCoreReadback = result.CoreOffset;
            _elevatedGpuMemoryReadback = result.MemoryOffset;
            if (core.HasValue) SaveDirectGpuOverclockValue("GpuCoreClockOffsetOC", core.Value);
            if (memory.HasValue) SaveDirectGpuOverclockValue("GpuMemoryClockOffsetOC", memory.Value);
            if (enabled.HasValue) SaveDirectGpuOverclockValue("OverClockingSwitch", enabled.Value ? 1 : 0);
        }

        bool confirmed = fields.All(field =>
            int.TryParse(field.Value, out int value) &&
            ElevatedGpuOverclockFieldMatches(field.Key, value, result));
        if (!confirmed) MarkEnableUnconfirmed();
        Logger.WriteLine($"GPU OC elevated readback: core={result.CoreOffset}, memory={result.MemoryOffset}, confirmed={confirmed}");
        if (confirmed) CustomModeChanged?.Invoke();
        return confirmed;
    }

    internal bool CanSetGpuOverclockThroughGcu(string key, int value)
    {
        if (!GcuGpuOverclockAvailable) return false;
        return key switch
        {
            "OverClockingSwitch" => value is 0 or 1,
            "GpuCoreClockOffsetOC" => CanSetGcuOffset(value, GpuCoreOffsetMinimum, GpuCoreOffsetMaximum,
                GpuCoreOffsetUserMinimum, GpuCoreOffsetUserMaximum, key),
            "GpuMemoryClockOffsetOC" => CanSetGcuOffset(value, GpuMemoryOffsetMinimum, GpuMemoryOffsetMaximum,
                GpuMemoryOffsetUserMinimum, GpuMemoryOffsetUserMaximum, key),
            _ => false,
        };
    }

    bool CanSetGcuOffset(int value, int gcuMinimum, int gcuMaximum,
        int userMinimum, int userMaximum, string key)
    {
        if (value >= gcuMinimum && value <= gcuMaximum) return true;
        // The extended range is a compatibility path only when the direct driver
        // backend cannot represent the requested value.
        return LchwocSupportReported == true &&
            !CanSetGpuOverclockThroughDriver(key, value) &&
            value >= userMinimum && value <= userMaximum;
    }

    internal void SelectGcuGpuOverclockBackend()
    {
        lock (_gpuOverclockLock)
        {
            _directGpuOverclockEnabled = false;
            _elevatedGpuCoreReadback = null;
            _elevatedGpuMemoryReadback = null;
        }
        if (!_persistDirectGpuOverclock) return;
        int profileIndex = CustomProfileIndex is >= 0 and <= 3 ? CustomProfileIndex : 0;
        AppConfig.Set(DirectGpuProfileKey("enabled", profileIndex), 0);
        AppConfig.Set(DirectGpuProfileKey("core", profileIndex), 0);
        AppConfig.Set(DirectGpuProfileKey("memory", profileIndex), 0);
    }

    internal void MarkGcuGpuOverclockActive()
    {
        lock (_gpuOverclockLock)
        {
            // Null selects the GCU status as the active backend. A literal false
            // would mask a confirmed GCU enable in GpuOverclockEnabled.
            _directGpuOverclockEnabled = null;
            _elevatedGpuCoreReadback = null;
            _elevatedGpuMemoryReadback = null;
        }
        CustomModeChanged?.Invoke();
    }

    internal bool CanSetGpuOverclockThroughDriver(string key, int value)
    {
        lock (_gpuOverclockLock)
        {
            if (_gpuOverclock is null || !_gpuOverclock.IsAvailable) return false;
            return key switch
            {
                "OverClockingSwitch" => value is 0 or 1,
                "GpuCoreClockOffsetOC" => _gpuOverclock.CoreOffset.IsAdjustable && _gpuOverclock.CoreOffset.Contains(value),
                "GpuMemoryClockOffsetOC" => _gpuOverclock.MemoryOffset.IsAdjustable && _gpuOverclock.MemoryOffset.Contains(value),
                _ => false,
            };
        }
    }

    internal bool DirectGpuOverclockFieldWithinDriverRange(string key, int value)
    {
        lock (_gpuOverclockLock)
        {
            if (_gpuOverclock is null) return true;
            return key switch
            {
                "OverClockingSwitch" => value is 0 or 1,
                "GpuCoreClockOffsetOC" => _gpuOverclock.CoreOffset.IsAdjustable && _gpuOverclock.CoreOffset.Contains(value),
                "GpuMemoryClockOffsetOC" => _gpuOverclock.MemoryOffset.IsAdjustable && _gpuOverclock.MemoryOffset.Contains(value),
                _ => false,
            };
        }
    }

    internal bool TrySetDirectGpuOverclockField(string key, int value)
    {
        if (!EnsureDirectGpuOverclock()) return false;
        bool success;
        lock (_gpuOverclockLock)
        {
            if (_gpuOverclock is null) return false;
            success = key switch
            {
                "OverClockingSwitch" => SetDirectGpuOverclockEnabled(value == 1),
                "GpuCoreClockOffsetOC" =>
                    CanSetGpuOverclockThroughDriver(key, value) && _gpuOverclock.SetCoreOffset(value),
                "GpuMemoryClockOffsetOC" =>
                    CanSetGpuOverclockThroughDriver(key, value) && _gpuOverclock.SetMemoryOffset(value),
                _ => false,
            };

            if (success && key is "GpuCoreClockOffsetOC" or "GpuMemoryClockOffsetOC")
                _directGpuOverclockEnabled = true;
            if (success && key == "OverClockingSwitch")
            {
                _elevatedGpuCoreReadback = null;
                _elevatedGpuMemoryReadback = null;
            }
            if (success && key == "GpuCoreClockOffsetOC") _elevatedGpuCoreReadback = null;
            if (success && key == "GpuMemoryClockOffsetOC") _elevatedGpuMemoryReadback = null;
            if (success) SaveDirectGpuOverclockValue(key, value);
        }

        if (success) CustomModeChanged?.Invoke();
        return success;
    }

    bool SetDirectGpuOverclockEnabled(bool enabled)
    {
        if (_gpuOverclock is null) return false;
        if (!enabled)
        {
            bool coreReset = !_gpuOverclock.CoreOffset.IsAdjustable ||
                _gpuOverclock.CoreOffset.Current == 0 || _gpuOverclock.SetCoreOffset(0);
            bool memoryReset = !_gpuOverclock.MemoryOffset.IsAdjustable ||
                _gpuOverclock.MemoryOffset.Current == 0 || _gpuOverclock.SetMemoryOffset(0);
            if (!coreReset || !memoryReset) return false;
        }
        _directGpuOverclockEnabled = enabled;
        if (!enabled)
        {
            _elevatedGpuCoreReadback = null;
            _elevatedGpuMemoryReadback = null;
        }
        return true;
    }

    public bool RestoreDirectGpuOverclockProfile(int profileIndex)
    {
        if (!_persistDirectGpuOverclock || profileIndex is < 0 or > 3 ||
            !AppConfig.Is(DirectGpuProfileKey("enabled", profileIndex)))
            return false;
        if (!EnsureDirectGpuOverclock()) return false;

        if (GpuOverclockRequiresElevation)
        {
            // 启动恢复档位走的是直连 NVAPI；非提权进程写入会被拒。跳过并给出诚实原因，
            // 不再写出 NVAPI_INVALID_USER_PRIVILEGE 后静默禁用后端。
            Logger.WriteLine($"Restore direct GPU OC profile {profileIndex + 1} skipped: " +
                "the NVIDIA direct write requires an elevated process (restart as administrator).");
            return false;
        }

        int core = AppConfig.Get(DirectGpuProfileKey("core", profileIndex), 0);
        int memory = AppConfig.Get(DirectGpuProfileKey("memory", profileIndex), 0);
        bool coreAdjustable = GpuCoreOffsetAdjustable;
        bool memoryAdjustable = GpuMemoryOffsetAdjustable;
        bool coreValid = !coreAdjustable || core >= GpuCoreOffsetUserMinimum && core <= GpuCoreOffsetUserMaximum;
        bool memoryValid = !memoryAdjustable || memory >= GpuMemoryOffsetUserMinimum && memory <= GpuMemoryOffsetUserMaximum;
        if (!coreValid || !memoryValid)
        {
            Logger.WriteLine($"Restore direct GPU OC profile {profileIndex + 1} rejected: core={core}, memory={memory}");
            return false;
        }

        bool coreOk;
        bool memoryOk;
        lock (_gpuOverclockLock)
        {
            if (_gpuOverclock is null) return false;
            _elevatedGpuCoreReadback = null;
            _elevatedGpuMemoryReadback = null;
            coreOk = !coreAdjustable || _gpuOverclock.CoreOffset.Current == core || _gpuOverclock.SetCoreOffset(core);
            memoryOk = !memoryAdjustable || _gpuOverclock.MemoryOffset.Current == memory || _gpuOverclock.SetMemoryOffset(memory);
            _directGpuOverclockEnabled = coreOk && memoryOk;
        }
        Logger.WriteLine($"Restore direct GPU OC profile {profileIndex + 1}: core={core}/{coreOk}, memory={memory}/{memoryOk}");
        if (_directGpuOverclockEnabled == true) CustomModeChanged?.Invoke();
        return _directGpuOverclockEnabled == true;
    }

    public void SuspendDirectGpuOverclock()
    {
        lock (_gpuOverclockLock)
        {
            if (_gpuOverclock is null || _directGpuOverclockEnabled != true) return;
            SetDirectGpuOverclockEnabled(false);
            Logger.WriteLine("Direct GPU overclock suspended outside custom mode.");
        }
        CustomModeChanged?.Invoke();
    }

    void SaveDirectGpuOverclockValue(string key, int value)
    {
        if (!_persistDirectGpuOverclock) return;
        int profileIndex = CustomProfileIndex is >= 0 and <= 3 ? CustomProfileIndex : 0;
        switch (key)
        {
            case "OverClockingSwitch":
                AppConfig.Set(DirectGpuProfileKey("enabled", profileIndex), value == 1 ? 1 : 0);
                if (value == 0)
                {
                    AppConfig.Set(DirectGpuProfileKey("core", profileIndex), 0);
                    AppConfig.Set(DirectGpuProfileKey("memory", profileIndex), 0);
                }
                break;
            case "GpuCoreClockOffsetOC":
                AppConfig.Set(DirectGpuProfileKey("core", profileIndex), value);
                AppConfig.Set(DirectGpuProfileKey("enabled", profileIndex), 1);
                break;
            case "GpuMemoryClockOffsetOC":
                AppConfig.Set(DirectGpuProfileKey("memory", profileIndex), value);
                AppConfig.Set(DirectGpuProfileKey("enabled", profileIndex), 1);
                break;
        }
    }

    static string DirectGpuProfileKey(string field, int profileIndex) =>
        $"direct_gpu_oc_{field}_{profileIndex}";

    /// <summary>退出时等待进行中的控制操作交出信号量的上限。</summary>
    internal static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// MQTT keepalive 周期。
    ///
    /// 这是「对端已死但 socket 没关」这种静默失效的唯一检测手段：MQTTnet 只在收到
    /// FIN/RST 或 keepalive 超时时才把 IsConnected 翻成 false。默认值 15 秒意味着
    /// GCUService 被冻结/强杀时最长有 15-25 秒的假连接窗口，那期间每条 Publish 都要
    /// 卡到 <see cref="PublishTimeout"/> 才失败。
    ///
    /// 取 3 秒是为了让**心跳先于发布超时**暴露断线：MQTT 的判定是 keepalive × 1.5，
    /// 3 秒 → 约 4.5 秒检测到，略早于 5 秒的 PublishTimeout。反过来（keepalive 更长）
    /// 的话，用户看到的永远是「点了没反应，5 秒后失败」，而不是一次干净的重连。
    /// 本地环回上每 3 秒一个 PINGREQ 的开销可以忽略。
    /// </summary>
    internal static readonly TimeSpan KeepAlivePeriod = TimeSpan.FromSeconds(3);

    public void Dispose()
    {
        // 幂等，且必须原子：过去是 `if (_disposed) return; _disposed = true;`，
        // 两个线程同时 Dispose 会双双进入（退出路径有 _exitStarted 兜住，
        // 但 UI 审计那条路径会反复创建/销毁实例，没有那层保护）。
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;
        _disposed = true;

        // 先把 ConnectAsync 请下来再动 _client。
        //
        // 不等的话存在这样一个窗口：退出时正好在重连中，ConnectAsync 已经越过开头那次
        // _disposed 检查，一边在订阅/发布、这边一边把 client 拆了。更糟的是
        // StopTelemetryBeforeExit 会因为看到 IsConnected=false 而跳过 System_OFF，
        // 而 ConnectAsync 紧接着发出 System_ON——进程都退了，GCU 反而刚被要求打开推流。
        // ConnectAsync 内部现在也会在发初始状态前后各复查一次 _disposed，两侧都收口。
        bool connectGateTaken = false;
        try { connectGateTaken = _connectLock.Wait(DisposeDrainTimeout); }
        catch (Exception ex) { Logger.WriteLine("Dispose: can't drain _connectLock: " + ex.Message); }
        if (!connectGateTaken)
            Logger.WriteLine($"Dispose: ConnectAsync still running after {DisposeDrainTimeout.TotalSeconds:0}s; disposing anyway");

        try
        {
            // 过去这里是无超时的同步阻塞。broker 无响应时整个退出流程会挂住，
            // 而 Dispose 通常跑在 UI 线程上。
            // 保留同步是有意的：IDisposable.Dispose 契约本身是同步的，释放顺序必须在本方法内完成；
            // 2 秒上限已把 UI 线程最长卡顿限制住。
            if (_client?.IsConnected == true)
                _client.DisconnectAsync().WaitAsync(DisposeDrainTimeout).GetAwaiter().GetResult();
        }
        catch (Exception ex) { Logger.WriteLine("MQTT disconnect failed: " + ex.Message); }

        lock (_gpuOverclockLock)
        {
            _gpuOverclock?.Dispose();
            _gpuOverclock = null;
        }
        // 提权写入助手是一个独立的高权限进程，会一直空转到空闲超时。
        // 退出时主动结束它，不留下带着可写超频管道的孤儿进程。
        if (_elevatedGpuOverclockApplier is IDisposable disposableApplier)
        {
            try { disposableApplier.Dispose(); }
            catch (Exception ex) { Logger.WriteLine("Elevated GPU applier dispose failed: " + ex.Message); }
        }
        _client?.Dispose();

        // 清空公开事件。MQTT 客户端侧的 handler 随 _client.Dispose() 一起走，
        // 但这些是外部订阅的：UI 审计路径会反复创建/销毁实例，残留的订阅会让
        // 已 Dispose 的窗体继续被这个对象持有。
        DataChanged = null;
        ConnectionReady = null;
        StateChanged = null;
        ModeChanged = null;
        CurveUpdated = null;
        GpuModeChanged = null;
        CustomModeChanged = null;
        CloseTimerChanged = null;
        LcChanged = null;
        CapabilitiesChanged = null;
        RawMessageObserved = null;

        // SetGpuMode 最长约 14 秒持有 _controlLock。过去这里直接 Dispose 信号量，
        // 那个操作的 finally 里 Release() 会抛 ObjectDisposedException，正卡在
        // WaitAsync 的操作也会抛；这些异常最终只落到 UnobservedTaskException 的日志里。
        // 先有界等待它交出来，拿不到就不释放：SemaphoreSlim 没有分配内核句柄
        // （从未使用 AvailableWaitHandle），进程退出时不释放是无害的。
        if (connectGateTaken)
        {
            try { _connectLock.Dispose(); }
            catch (Exception ex) { Logger.WriteLine("Dispose: _connectLock: " + ex.Message); }
        }
        TryDisposeGate(_controlLock, nameof(_controlLock));
    }

    static void TryDisposeGate(SemaphoreSlim gate, string name)
    {
        try
        {
            if (gate.Wait(DisposeDrainTimeout))
            {
                gate.Dispose();
                return;
            }
            Logger.WriteLine($"Dispose: {name} is still held after {DisposeDrainTimeout.TotalSeconds:0}s; leaving it undisposed");
        }
        catch (Exception ex) { Logger.WriteLine($"Dispose: can't drain {name}: {ex.Message}"); }
    }

    static int OptionalInt(JObject o, string key, int fallback)
    {
        JToken? token = o.GetValue(key, StringComparison.OrdinalIgnoreCase);
        if (token is null || token.Type == JTokenType.Null) return fallback;
        return ParseOptionalInt(token.ToString()) ?? fallback;
    }

    static string[] TryParseMacArray(string text)
    {
        try
        {
            return JArray.Parse(text)
                .Select(token => token?.ToString())
                .Where(mac => !string.IsNullOrWhiteSpace(mac))
                .Cast<string>()
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    void UpdateGpuOffsetRanges(JObject o)
    {
        // 解析出来的范围只用于能力探测（GcuOffsetChannelUsable / CanSetGcuOffset）；
        // 滑条范围走实测边界，见 GetMergedGpuOffsetRange。
        GpuCoreOffsetMinimum = OptionalInt(o, GpuCoreOffsetMinimum,
            "GPU_CoreClockOffsetMinimumHWOC", "GPU_CoreClockOffsetMinimum", "GpuCoreClockOffsetMinimum", "GpuCoreClockOffsetMin");
        GpuCoreOffsetMaximum = OptionalInt(o, GpuCoreOffsetMaximum,
            "GPU_CoreClockOffsetMaximumHWOC", "GPU_CoreClockOffsetMaximum", "GpuCoreClockOffsetMaximum", "GpuCoreClockOffsetMax");
        GpuMemoryOffsetMinimum = OptionalInt(o, GpuMemoryOffsetMinimum,
            "GPU_MemoryClockOffsetMinimumHWOC", "GPU_MemoryClockOffsetMinimum", "GpuMemoryClockOffsetMinimum", "GpuMemoryClockOffsetMin");
        GpuMemoryOffsetMaximum = OptionalInt(o, GpuMemoryOffsetMaximum,
            "GPU_MemoryClockOffsetMaximumHWOC", "GPU_MemoryClockOffsetMaximum", "GpuMemoryClockOffsetMaximum", "GpuMemoryClockOffsetMax");
    }

    static int OptionalInt(JObject o, int fallback, params string[] keys)
    {
        foreach (string key in keys)
        {
            JToken? token = o.GetValue(key, StringComparison.OrdinalIgnoreCase);
            if (token is null || token.Type == JTokenType.Null) continue;
            int? value = ParseOptionalInt(token.ToString());
            if (value.HasValue) return value.Value;
        }
        return fallback;
    }

    public int TccTargetToRaw(int target) => Math.Clamp((TjMax > 0 ? TjMax : 100) - target, 0, 100);

    public int TccTargetFromRaw(int offset) => Math.Clamp((TjMax > 0 ? TjMax : 100) - offset, 0, 100);

    static bool HasField(JObject o, string key) => o.GetValue(key, StringComparison.OrdinalIgnoreCase) is not null;

    /// <summary>
    /// 该功耗字段是否携带一个可用的瓦数值。真实的功耗墙不可能是 0 或负数，
    /// 所以「键存在」不等于「这个字段体系在本机生效」——判定平台必须看值。
    /// </summary>
    internal static bool HasUsablePowerValue(JObject o, string key) =>
        OptionalUsablePowerValue(o, key, -1) > 0;

    /// <summary>
    /// 读取功耗字段：只接受 &gt; 0 的可用值，其余保留 fallback。
    /// 这让一个伪造的 0 不会被 latch 成「已知值」，也不会劫持 Pl1/Pl2 的显示。
    /// </summary>
    internal static int OptionalUsablePowerValue(JObject o, string key, int fallback)
    {
        JToken? token = o.GetValue(key, StringComparison.OrdinalIgnoreCase);
        if (token is null || token.Type == JTokenType.Null) return fallback;
        int? value = ParseOptionalInt(token.ToString());
        return value is > 0 ? value.Value : fallback;
    }

    /// <summary>
    /// 多写法容错版的 <see cref="OptionalUsablePowerValue"/>：按顺序试每个键名，
    /// 取第一个解析出 &gt; 0 的值。用于 PL4 上下限这类固件写法不统一的字段——
    /// 既要容错别名，又不能把 0 latch 成「已知值」。
    /// </summary>
    internal static int FirstUsablePowerValue(JObject o, int fallback, params string[] keys)
    {
        foreach (string key in keys)
        {
            int value = OptionalUsablePowerValue(o, key, -1);
            if (value > 0) return value;
        }
        return fallback;
    }

    static JToken? FirstField(JObject o, params string[] keys)
    {
        foreach (string key in keys)
        {
            JToken? value = o.GetValue(key, StringComparison.OrdinalIgnoreCase);
            if (value is not null && value.Type != JTokenType.Null) return value;
        }
        return null;
    }

    static bool? FirstOptionalBool(JObject o, params string[] keys)
    {
        foreach (string key in keys)
        {
            bool? value = OptionalBool(o, key);
            if (value.HasValue) return value;
        }
        return null;
    }

    static bool? ParseColorCalibrationSwitch(JToken? token)
    {
        if (token is null) return null;
        // 先走统一的精确布尔解析；这里额外保留 ON/OFF 的子串兜底，因为校色开关的固件值
        // 形如 "ColorCalibrationSwitch_ON"，属于该字段特有的记法。
        bool? flexible = ParseFlexibleBool(token.ToString());
        if (flexible.HasValue) return flexible;
        string text = token.ToString() ?? "";
        if (text.Contains("OFF", StringComparison.OrdinalIgnoreCase)) return false;
        if (text.Contains("ON", StringComparison.OrdinalIgnoreCase)) return true;
        return null;
    }

    static int ParseColorCalibrationMode(JToken? token)
    {
        if (token is null) return 0;
        if (int.TryParse(token.ToString(), out int number)) return number;
        string text = token.ToString() ?? "";
        if (text.Contains("sRGB", StringComparison.OrdinalIgnoreCase)) return 2;
        if (text.Contains("P3", StringComparison.OrdinalIgnoreCase)) return 3;
        if (text.Contains("Adobe", StringComparison.OrdinalIgnoreCase)) return 4;
        return text.Contains("Default", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    static bool? OptionalBool(JObject o, string key)
    {
        JToken? token = o.GetValue(key, StringComparison.OrdinalIgnoreCase);
        if (token is null || token.Type == JTokenType.Null) return null;
        return ParseFlexibleBool(token.ToString());
    }

    /// <summary>
    /// 布尔字段的单一解析入口。GCU 固件对布尔的表达方式不统一：同一帧报文里既有 JSON
    /// 布尔，也有 "1"/"0"，还有 ON/OFF、SUPPORT/NOT_SUPPORT 这类枚举字符串。
    /// 这里只接受可枚举的已知记法，其余一律返回 null（未知），绝不猜测成某个具体值。
    /// 关键点：归一化后做**精确**匹配，不做子串匹配——子串匹配会让 "NOT_SUPPORT" 命中
    /// "SUPPORT" 判成真，也会让 "MONITOR" 这类含 "ON" 的词被误判。
    /// </summary>
    internal static bool? ParseFlexibleBool(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string trimmed = text.Trim();
        if (bool.TryParse(trimmed, out bool boolean)) return boolean;
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            return number != 0;

        var normalized = new StringBuilder(trimmed.Length);
        foreach (char c in trimmed)
        {
            if (c is '_' or '-' or ' ' or '.') continue;
            normalized.Append(char.ToUpperInvariant(c));
        }

        return normalized.ToString() switch
        {
            "TRUE" or "YES" or "Y" or "ON" or "OPEN" or "ENABLE" or "ENABLED" or
            "SUPPORT" or "SUPPORTED" or "SUPPORTS" or "AVAILABLE" => true,
            "FALSE" or "NO" or "N" or "OFF" or "CLOSE" or "CLOSED" or "DISABLE" or "DISABLED" or
            "NOTSUPPORT" or "NOTSUPPORTED" or "NOTSUPPORTS" or "UNSUPPORT" or "UNSUPPORTED" or
            "NONSUPPORT" or "NONE" or "UNAVAILABLE" => false,
            _ => null,
        };
    }

    static bool IsAdjustable(int minimum, int maximum) => minimum >= 0 && maximum > minimum;
    static bool IsOffsetAdjustable(int minimum, int maximum) => maximum > minimum;

    /// <summary>
    /// GCU 通道（LCHWOC → 驱动写入）是否可用。显式 false 就是不可用；未上报时
    /// 以状态里出现可调范围为准（与 <see cref="GcuGpuOverclockAvailable"/> 的范围部分同源）。
    /// </summary>
    bool GcuOffsetChannelUsable =>
        LchwocSupportReported == true ||
        (LchwocSupportReported is null &&
         (IsOffsetAdjustable(GpuCoreOffsetMinimum, GpuCoreOffsetMaximum) ||
          IsOffsetAdjustable(GpuMemoryOffsetMinimum, GpuMemoryOffsetMaximum)));

    /// <summary>
    /// 滑条的可选范围。三条路径各自的真值来源不同：
    /// - GCU 通道（本机默认，非提权）：用实测硬边界。GCU 上报字段两个方向都不可信
    ///   （本机显存上报 -1800/1800，实测只有 -1000..2000 生效），只当能力探测用。
    /// - 提权会话：GCU 边界之外由直连驱动写入承担，合并 NVAPI 范围。
    /// - GCU 通道不可用的机型：维持 NVAPI 范围 + 提权助手路径。
    /// </summary>
    (int Minimum, int Maximum) GetMergedGpuOffsetRange(bool core)
    {
        int fallbackMinimum = core ? GcuFallbackCoreOffsetMinimum : GcuFallbackMemoryOffsetMinimum;
        int fallbackMaximum = core ? GcuFallbackCoreOffsetMaximum : GcuFallbackMemoryOffsetMaximum;
        if (!GcuOffsetChannelUsable)
            return TryGetDirectOffsetRange(core, out GpuClockOffsetRange? direct)
                ? (direct.Minimum, direct.Maximum)
                : (-1, -1);
        if (!_isProcessElevated())
            return (fallbackMinimum, fallbackMaximum);
        return TryGetDirectOffsetRange(core, out GpuClockOffsetRange? elevated)
            ? (Math.Min(elevated.Minimum, fallbackMinimum), Math.Max(elevated.Maximum, fallbackMaximum))
            : (fallbackMinimum, fallbackMaximum);
    }

    static bool IsTelemetryTopic(string topic) => topic is
        MqttTopics.SystemCpuInfo or
        MqttTopics.SystemGpuInfo or
        MqttTopics.SystemMemoryInfo or
        MqttTopics.SystemFanInfo or
        MqttTopics.SystemBatteryInfo;
}
