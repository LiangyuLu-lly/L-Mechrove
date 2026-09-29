using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MechrevoLite.Hardware;

/// <summary>
/// 机械革命键盘 RGB（ITE8291 EC）——BetterRGB（MIT）协议移植。
/// 链路：HID 枚举(048D/600B/FF03/MI_01) → enter_custom_mode → 每帧 feature(step3) + 8×64B 块。
/// 保留 静态/呼吸/波浪 三效果（M1 定稿；M2 扩展效果已按用户要求移除）。
/// </summary>
public class KeyboardRgb : IDisposable
{
    public const int BufSize = 512;
    const int VendorId = 0x048D;
    const int ProductId = 0x600B;   // 苍龙16Pro ITE8291（评分加分项，非硬性）

    // ---- 键位表（LED 帧内偏移，RGB 顺序；C 源码 KEYMAP 完整搬运）----
    record KeyEntry(string Name, int Offset, int Row, int Col);
    const int OffsetShiftLeft = 5, OffsetShiftLeftAux = 29;
    const int OffsetEnter = 349, OffsetEnterAux = 345;

    static readonly KeyEntry[] Keymap =
    {
        new("Esc",21,0,0), new("F1",45,0,1), new("F2",69,0,2), new("F3",93,0,3), new("F4",117,0,4),
        new("F5",141,0,5), new("F6",165,0,6), new("F7",189,0,7), new("F8",213,0,8), new("F9",237,0,9),
        new("F10",261,0,10), new("F11",285,0,11), new("F12",309,0,12), new("FnLockCam",333,0,13),
        new("PrtSc",357,0,14), new("Del",381,0,15), new("Home",405,0,16), new("PgUp",429,0,17),
        new("PgDn",453,0,18), new("End",477,0,19),
        new("Grave",17,1,0), new("1",41,1,1), new("2",65,1,2), new("3",89,1,3), new("4",113,1,4),
        new("5",137,1,5), new("6",161,1,6), new("7",185,1,7), new("8",209,1,8), new("9",233,1,9),
        new("0",257,1,10), new("Minus",281,1,11), new("Equals",305,1,12), new("Backspace",353,1,13),
        new("NumLock",377,1,14), new("NumpadDiv",401,1,15), new("NumpadMul",425,1,16), new("NumpadMinus",449,1,17),
        new("Tab",13,2,0), new("Q",61,2,1), new("W",85,2,2), new("E",109,2,3), new("R",133,2,4),
        new("T",157,2,5), new("Y",181,2,6), new("U",205,2,7), new("I",229,2,8), new("O",253,2,9),
        new("P",277,2,10), new("BracketL",301,2,11), new("BracketR",325,2,12), new("Enter",OffsetEnter,2,13),
        new("Numpad7",373,2,14), new("Numpad8",397,2,15), new("Numpad9",421,2,16), new("NumpadPlus",445,2,17),
        new("CapsLock",9,3,0), new("A",57,3,1), new("S",81,3,2), new("D",105,3,3), new("F",129,3,4),
        new("G",153,3,5), new("H",177,3,6), new("J",201,3,7), new("K",225,3,8), new("L",249,3,9),
        new("Semicolon",273,3,10), new("Quote",297,3,11), new("Backslash",321,3,12),
        new("Numpad4",369,3,13), new("Numpad5",393,3,14), new("Numpad6",417,3,15),
        new("ShiftL",OffsetShiftLeft,4,0), new("ISO_Backslash",53,4,1), new("Z",77,4,2), new("X",101,4,3),
        new("C",125,4,4), new("V",149,4,5), new("B",173,4,6), new("N",197,4,7), new("M",221,4,8),
        new("Comma",245,4,9), new("Period",269,4,10), new("Slash",293,4,11), new("ShiftR",341,4,12),
        new("Numpad1",365,4,13), new("Numpad2",389,4,14), new("Numpad3",413,4,15), new("NumpadEnter",437,4,16),
        new("CtrlL",1,5,0), new("Fn",49,5,1), new("Windows",73,5,2), new("AltL",97,5,3),
        new("Space",169,5,4), new("AltGr",241,5,5), new("CopilotKey",289,5,6), new("ArrowUp",337,5,7),
        new("Numpad0",385,5,8), new("NumpadDecimal",409,5,9),
        new("ArrowLeft",313,6,6), new("ArrowDown",433,6,7), new("ArrowRight",361,6,8),
    };

    // ---- 模式常量（对齐 C 源码 MODE_*）----
    public const int ModeBreath = 0, ModeWave = 1, ModeSparkle = 2, ModeReactive = 3, ModeWheel = 4,
        ModeLightning = 5, ModeFlame = 6, ModeRain = 7, ModeMatrix = 8, ModeStatic = 9;

    HidDeviceWin? _stream;
    readonly object _lock = new();          // 设备锁（对应 g_deviceLock）
    Thread? _effectThread;
    volatile bool _stop;
    volatile int _activeMode = -1;
    int _generation;                        // 效果线程代际：旧线程即使 Join 超时残留也会因代际不匹配自行退出
    int _customModeReinitCount;             // 真正重进自定义帧模式的次数（测试 seam）
    readonly object _startLock = new();     // StartMode 串行化：并发 Start（连点/切页）会产生同代际双效果线程并发写 HID
    readonly object _configLock = new();
    System.Threading.Timer? _configSaveTimer;
    readonly string? _configPathOverride;
    internal Func<bool>? ReconnectProbe;    // 测试 seam：替代真实 HID 重连（生产为 null）
    internal Func<HidDeviceWin?>? ResolveDeviceProbe;   // 测试 seam：替代真实 HID 枚举（生产为 null）
    readonly object _reconnectLock = new(); // 单飞重连门
    volatile bool _reconnecting;            // 重连在飞行：帧循环据此跳过本帧（不阻塞）
    volatile bool _disposed;
    long _reconnectStartedTicks;
    const int ReconnectTimeoutMs = 3000;    // 重连飞行上限：超时释放单飞标志，帧循环不会被永久卡住

    public KeyboardRgb()
    {
    }

    internal KeyboardRgb(string configPathOverride)
    {
        _configPathOverride = configPathOverride;
    }

    /// <summary>测试 seam：注入假 HID 设备，驱动发帧/重连失败路径而不触碰真实硬件。</summary>
    internal KeyboardRgb(string configPathOverride, HidDeviceWin stream) : this(configPathOverride)
    {
        _stream = stream;
    }

    // ---- 效果参数（UI 读写）----
    public volatile int Brightness = 100;
    public volatile int TargetFps = 30;
    public int CloseTimerMinutes = 0;   // 用户期望的键盘灯睡眠时间（持久化用）；实际到期由应用级空闲监视统一决策
    public int KbHidMode = ModeWave;    // HID 模式（默认流畅彩虹）
    public bool KbPowerOn = true;       // 用户明确关闭时不在下次启动自动恢复
    public Color BreathColor = Color.FromArgb(0, 120, 255);
    public volatile int BreathSpeed = 12, BreathSmooth = 60;
    public volatile int WaveAngle = 30, WaveSpeed = 4;
    public volatile int SparkleSpeed = 5, SparkleDensity = 8;
    public Color ReactiveColor = Color.White;
    public volatile bool ReactiveRandom = true;
    public volatile int ReactiveDuration = 20;
    public volatile bool WheelReverse;
    public volatile int WheelSpeed = 4;
    public volatile int LightningSpeed = 5, LightningSmooth = 10, LightningWidth = 2, LightningConcurrent = 1;
    public volatile int FlameSpeed = 5, FlameSmooth = 8;
    public volatile int RainSpeed = 5, RainDensity = 3, RainColorCount = 1;
    public Color[] RainColors =
    {
        Color.FromArgb(80, 140, 255), Color.FromArgb(120, 80, 255), Color.FromArgb(80, 220, 255),
        Color.White, Color.FromArgb(80, 255, 180),
    };
    public volatile int MatrixSpeed = 5, MatrixStyle, MatrixDensity = 2, MatrixColorCount = 1;
    public Color[] MatrixColors = { Color.FromArgb(0, 255, 60), Color.FromArgb(0, 180, 255), Color.FromArgb(255, 0, 120) };
    public volatile int StaticLayout;   // 0=横向分区 1=纵向分区
    public volatile int StaticZones = 1;
    public Color[] StaticColors =
    {
        Color.White, Color.Red, Color.Lime, Color.Blue, Color.Yellow,
        Color.Magenta, Color.Cyan, Color.FromArgb(255, 140, 0), Color.FromArgb(140, 0, 255), Color.FromArgb(0, 140, 140),
    };
    public Color StaticColor { get => StaticColors[0]; set => StaticColors[0] = value; }   // 兼容单色用法

    public bool IsConnected => _stream != null;
    public string DeviceInfo { get; private set; } = "未连接";
    public string LastError { get; private set; } = "";
    public int ActiveMode => _activeMode;

    /// <summary>测试 seam：效果线程实际启动的次数（每次真正重启才自增，用于锁定「每周期只重启一次」）。</summary>
    internal int EffectGeneration => Volatile.Read(ref _generation);

    /// <summary>测试 seam：重进自定义帧模式的次数（恢复/固件接管重申各一次都自增）。</summary>
    internal int CustomModeReinitCount => Volatile.Read(ref _customModeReinitCount);
    public event Action? DeviceLost;                  // 设备消失/连续失败停止（UI 据此提示）
    DateTime _lastSendFailLog = DateTime.MinValue;   // 发帧失败日志节流（每 5s 至多一条，防刷屏）
    long _successFrames;                              // 帧心跳诊断：成功发帧计数
    DateTime _lastHeartbeat = DateTime.MinValue;
    long _heartbeatBase;
    int _failCount;                                   // 连续失败计数：≥10 自动停止（防设备消失时忙循环写死设备）
    static readonly byte[] Step3 = { 0x00, 0x12, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00 };
    readonly byte[] _scaledFrame = new byte[BufSize];
    readonly byte[][] _frameChunks = Enumerable.Range(0, 8).Select(_ => new byte[65]).ToArray();

    void LogSendFail(string why)
    {
        if ((DateTime.Now - _lastSendFailLog).TotalSeconds < 5) return;
        _lastSendFailLog = DateTime.Now;
        Logger.WriteLine($"RGB send_frame FAIL ({why}) dev={DeviceInfo}");
    }

    void Heartbeat()
    {
        if ((DateTime.Now - _lastHeartbeat).TotalSeconds < 60) return;   // 调试心跳降频：3s 一次日志 IO 是常驻噪音
        _lastHeartbeat = DateTime.Now;
        Logger.WriteLine($"RGB heartbeat: {_successFrames - _heartbeatBase} 帧/60s");
        _heartbeatBase = _successFrames;
    }

    // ---- 帧率 pacing（Stopwatch 时钟 + 可注入等待原语 seam）----
    readonly FramePacer _pacer = new(new SystemFrameClock());
    void PaceFrame(int fps) => _pacer.Pace(fps);

    /// <summary>
    /// 最近一次 HID 枚举按官方判据（键盘 PID 表 + Usage 1 + UsagePage）得到的键盘接口形态。
    /// 灯光通道识别用它做离线证据：服务不在时也能分出逐键 / 四区 / 无键盘。
    /// </summary>
    internal HidKeyboardInterfaceKind ScannedKeyboardInterface { get; private set; } = HidKeyboardInterfaceKind.NotScanned;

    /// <summary>枚举 BetterRGB 支持的 ITE8291 键盘 RGB 接口。</summary>
    HidDeviceWin? ResolveDevice()
    {
        if (ResolveDeviceProbe is not null) return ResolveDeviceProbe();   // 测试 seam
        HidDeviceWin? best = null;
        int bestScore = 0;
        var official = HidKeyboardInterfaceKind.None;
        foreach (var d in HidDeviceWin.Enumerate(VendorId))
        {
            official = PreferInterface(official,
                LightingChannelDetector.ClassifyKeyboardInterface(d.VendorID, d.ProductID, d.Usage, d.UsagePage));
            int score = ScoreCandidate(d.ProductID, d.UsagePage, d.Usage, d.InterfaceNumber,
                d.OutputReportLength, d.FeatureReportLength);
            Logger.WriteLine($"RGB HID candidate: {d.DeviceInfo} out={d.OutputReportLength} feature={d.FeatureReportLength} score={score}");
            if (score > bestScore) { bestScore = score; best = d; }
        }
        ScannedKeyboardInterface = official;
        LastScannedKeyboardInterface = official;
        return best;
    }

    /// <summary>进程内最近一次真实 HID 枚举的官方键盘接口形态（灯光通道识别的离线证据）。</summary>
    internal static HidKeyboardInterfaceKind LastScannedKeyboardInterface { get; private set; } = HidKeyboardInterfaceKind.NotScanned;

    /// <summary>同机多接口时按「逐键 &gt; 四区 &gt; 逐键一代」取最能说明键盘形态的那个。</summary>
    internal static HidKeyboardInterfaceKind PreferInterface(HidKeyboardInterfaceKind current, HidKeyboardInterfaceKind next)
    {
        static int Rank(HidKeyboardInterfaceKind kind) => kind switch
        {
            HidKeyboardInterfaceKind.PerKey => 3,
            HidKeyboardInterfaceKind.FourZone => 2,
            HidKeyboardInterfaceKind.PerKeyLegacy => 1,
            _ => 0,
        };
        return Rank(next) > Rank(current) ? next : current;
    }

    /// <summary>
    /// Mirrors BetterRGB: FF03 is preferred, MI_01 is the fallback, and PID 600B is
    /// only a bonus. Report sizes are tie-breakers because compatible controllers
    /// may omit or expose different HID descriptors while using the same protocol.
    /// 描述符里拿不到 Usage 的旧调用按键盘集合（Usage 1）处理。
    /// </summary>
    internal static int ScoreCandidate(ushort productId, ushort usagePage, int interfaceNumber,
        ushort outputReportLength, ushort featureReportLength) =>
        ScoreCandidate(productId, usagePage, 1, interfaceNumber, outputReportLength, featureReportLength);

    /// <summary>
    /// 在 BetterRGB 评分之上加官方判据，杜绝把非键盘接口当键盘写逐键帧：
    /// 官方灯条接口（PID 7000/7001/6005/6008/6010）、FF03 上的 Usage 2 集合（Lighbar4 形态）、
    /// 四区控制器（FF12，逐键帧协议不适用，交官方通道）一律 0 分；
    /// 官方键盘 PID 表内的接口额外加分，保证同机有灯条时键盘接口总是胜出。
    /// </summary>
    internal static int ScoreCandidate(ushort productId, ushort usagePage, ushort usage, int interfaceNumber,
        ushort outputReportLength, ushort featureReportLength)
    {
        if (Array.IndexOf(LightingChannelDetector.LightbarProductIds, productId) >= 0) return 0;
        if (usagePage == LightingChannelDetector.UsagePageFourZone) return 0;
        if (usagePage == LightingChannelDetector.UsagePagePerKey && usage == 2) return 0;
        bool officialKeyboard = LightingChannelDetector.IsKeyboardInterface(VendorId, productId, usage);
        int score = usagePage == LightingChannelDetector.UsagePagePerKey ? 30
            : usagePage == LightingChannelDetector.UsagePagePerKeyLegacy && officialKeyboard ? 25
            : interfaceNumber == 1 ? 20 : 0;
        if (score == 0) return 0;
        if (officialKeyboard) score += 6;
        if (productId == ProductId) score += 4;
        if (outputReportLength == 65) score += 2;
        if (featureReportLength == 9) score += 2;
        return score;
    }

    /// <summary>把 GCU 的 0..4 级硬件亮度转换为 HID 帧的 0..100 缩放值。</summary>
    internal static int MapHardwareBrightnessLevel(int level) => Math.Clamp(level, 0, 4) * 25;

    /// <summary>把 HID 0..100 软件亮度按最近档映射回 GCU 的 0..4 五档（回退通道亮度载体的输入）。</summary>
    internal static int MapSoftwareBrightnessToHardwareLevel(int brightness) =>
        Math.Clamp((brightness + 12) / 25, 0, 4);

    /// <summary>优先使用官方 brightNess；0..4 视为五档值，其余固件值按百分比读取。</summary>
    internal static int MapReportedHardwareBrightness(int brightness, int legacyLevel) =>
        brightness >= 0 ? brightness <= 4 ? MapHardwareBrightnessLevel(brightness) : Math.Clamp(brightness, 0, 100) :
        legacyLevel < 0 ? -1 : legacyLevel <= 4 ? MapHardwareBrightnessLevel(legacyLevel) : Math.Clamp(legacyLevel, 0, 100);

    /// <summary>单次连接尝试的分类：区分「无候选」（确定性结论）与「尝试失败」（可重试一次）。</summary>
    enum ConnectOutcome { Success, NoDevice, Failed }

    /// <summary>打开设备并进入自定义 RGB 模式（step1 → clear → step2）。</summary>
    public bool Connect()
    {
        lock (_lock)
        {
            try { return ConnectCoreLocked() == ConnectOutcome.Success; }
            catch (Exception ex)
            {
                _stream?.Dispose();
                _stream = null;
                ForgetBrightnessObservation();
                LastError = "连接异常: " + ex.Message;
                Logger.WriteLine($"RGB connect FAIL: {LastError}");
                return false;
            }
        }
    }

    /// <summary>
    /// 连接/探测共用的唯一实现体（须持 _lock 调用）：ResolveDevice → Open → EnterCustomMode。
    /// 探测 ≡ 真实路径，杜绝分叉；异常向上抛给调用方分类（Connect 捕获后保持原 false 语义，探测据此判 Unknown）。
    /// </summary>
    ConnectOutcome ConnectCoreLocked()
    {
        _stream?.Dispose();
        _stream = null;
        ForgetBrightnessObservation();
        var dev = ResolveDevice();
        if (dev is null) { LastError = "未找到兼容的 ITE8291/BetterRGB 接口（VID 048D，FF03/MI_01）"; return ConnectOutcome.NoDevice; }
        if (!dev.Open()) { LastError = "打开 HID 失败（设备被占用或权限不足）"; return ConnectOutcome.Failed; }
        _stream = dev;
        DeviceInfo = dev.DeviceInfo;
        // HID 打开后的设备稳定等待：有意的设备时序，勿删（RgbForm 的调用点在 Task.Run 后台线程）。
        Thread.Sleep(20);
        if (!EnterCustomMode())
        {
            _stream.Dispose();
            _stream = null;
            ForgetBrightnessObservation();
            LastError = "初始化 RGB 模式失败（feature report 被拒）";
            Logger.WriteLine($"RGB connect FAIL: {LastError}");
            return ConnectOutcome.Failed;
        }
        LastError = "";
        Logger.WriteLine("RGB connect OK: " + DeviceInfo);
        return ConnectOutcome.Success;
    }

    static readonly byte[] Step1 = { 0x00, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00 };
    static readonly byte[] Step2 = { 0x00, 0x08, 0x02, 0x33, 0x00, 0x32, 0x00, 0x00, 0x00 };

    bool EnterCustomMode()
    {
        if (!_stream!.SetFeature(Step1)) return false;
        if (!_stream.Write(new byte[65])) return false;   // clearFrame 65×0
        if (!_stream.SetFeature(Step2)) return false;
        // 设备回读（0x88）：固件当前效果寄存器应当就是刚写入的自定义帧模式（Control=2, Effect=0x33）。
        // 读不到（控制器不支持回读/测试桩）只记 null，不影响连接结论——那种情况界面只说「已下发」。
        CustomModeReadback = ReadbackCustomMode(_stream);
        return true;
    }

    static readonly byte[] QueryEffect88 = { 0x00, 0x88, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>
    /// 最近一次进入自定义帧模式后的设备回读：true = 0x88 回读确认固件处于自定义帧模式；
    /// false = 回读到了但不是（固件被别的通道改回了固件效果）；null = 没有回读证据。
    /// </summary>
    internal bool? CustomModeReadback { get; private set; }

    /// <summary>0x88 回读是否表明固件处于我方自定义帧模式（与 Step2 的 Control/Effect 字节比对）。</summary>
    internal static bool? IsCustomModeReadback(byte[] report) =>
        report.Length < 6 || report[1] != 0x88 ? null : report[2] == Step2[2] && report[3] == Step2[3];

    static bool? ReadbackCustomMode(HidDeviceWin stream)
    {
        byte[]? report = QueryEffectRegister(stream);
        return report is null ? null : IsCustomModeReadback(report);
    }

    /// <summary>0x88 查询：SetFeature(查询号) + GetFeature。只读，不改任何灯效状态（官方服务热键路径同用法）。</summary>
    static byte[]? QueryEffectRegister(HidDeviceWin stream)
    {
        try
        {
            if (!stream.SetFeature(QueryEffect88)) return null;
            var report = new byte[9];
            return stream.GetFeature(report) && report[1] == 0x88 ? report : null;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("RGB 0x88 readback failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 键盘固件效果寄存器的设备回读（官方通道下发后用来确认）：我方流打开时复用它，
    /// 否则临时以共享方式打开官方判据下的键盘接口（含四区 FF12）查询后立即关闭。
    /// 返回 [2]=Control [3]=Effect [4]=Speed [5]=Light；拿不到返回 null。
    /// </summary>
    internal byte[]? QueryFirmwareEffectRegister()
    {
        if (ResolveDeviceProbe is not null) return null;   // 测试 seam：不碰真实硬件
        lock (_lock)
        {
            if (_stream is not null) return QueryEffectRegister(_stream);
        }
        try
        {
            foreach (var d in HidDeviceWin.Enumerate(VendorId))
            {
                if (LightingChannelDetector.ClassifyKeyboardInterface(d.VendorID, d.ProductID, d.Usage, d.UsagePage)
                    is HidKeyboardInterfaceKind.None) { d.Dispose(); continue; }
                using (d)
                {
                    if (!d.Open()) continue;
                    return QueryEffectRegister(d);
                }
            }
        }
        catch (Exception ex) { Logger.WriteLine("RGB firmware readback failed: " + ex.Message); }
        return null;
    }

    // ---- 控制器能力探测（判定来源；探测 ≡ 真实路径，复用 ConnectCoreLocked）----
    const int ProbeTimeoutMs = 3000;    // 墙钟上限：枚举 + Sleep(20) 只需毫秒级，3 s 是余量；超限判 Unknown
    const int ProbeRetryDelayMs = 100;  // Open/EnterCustomMode 失败后的单次重试间隔

    /// <summary>三态判定：未知（未探测/瞬时失败）、不支持、支持。确定性结论进程内缓存，唤醒/重连绝不重探。</summary>
    internal FeatureAvailability ControllerAvailability { get; private set; } = FeatureAvailability.Unknown;

    readonly object _probeLock = new();   // 单飞：并发探测不会互相覆盖判定
    int _controllerProbeCount;

    /// <summary>测试 seam：探测真正执行的次数（锁定「确定性判定缓存后不再重探」）。</summary>
    internal int ControllerProbeCount => Volatile.Read(ref _controllerProbeCount);

    /// <summary>
    /// 确保 HID 就绪（惰性探测 + 连接，须在后台/接缝处调用，唤醒路径不得调用）。
    /// 判定映射：枚举无候选 → Unsupported（确定性，不重试）；Open/EnterCustomMode 失败 → 100 ms 后重试一次，仍失败 → Unsupported；
    /// 全部通过 → Supported；异常或超过 3 s 上限 → Unknown（保持今天的行为，绝不改走 GCU）。
    /// 副作用：仅 Supported 保持流打开并停留在自定义模式（调用方随即启动灯效）；Unsupported/Unknown 释放流；探测从不写效果帧。
    /// </summary>
    public async Task<bool> EnsureHidReadyAsync()
    {
        if (ControllerAvailability != FeatureAvailability.Unknown)
            return ControllerAvailability == FeatureAvailability.Supported;

        FeatureAvailability verdict;
        try
        {
            verdict = await Task.Run(ProbeController)
                .WaitAsync(TimeSpan.FromMilliseconds(ProbeTimeoutMs)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("RGB probe 超时或异常: " + ex.Message);
            verdict = FeatureAvailability.Unknown;
        }

        if (verdict != FeatureAvailability.Supported)
        {
            lock (_lock)
            {
                _stream?.Dispose();
                _stream = null;
                ForgetBrightnessObservation();
            }
        }
        if (verdict != FeatureAvailability.Unknown)
            ControllerAvailability = verdict;   // 只缓存确定性结论；Unknown 保持今天的行为
        return verdict == FeatureAvailability.Supported;
    }

    /// <summary>后台探测体：共享的连接实现体，失败只重试一次（无候选不重试）。</summary>
    FeatureAvailability ProbeController()
    {
        lock (_probeLock)
        {
            if (ControllerAvailability != FeatureAvailability.Unknown) return ControllerAvailability;
            Interlocked.Increment(ref _controllerProbeCount);
            try
            {
                ConnectOutcome outcome;
                lock (_lock) outcome = ConnectCoreLocked();
                if (outcome == ConnectOutcome.Success) return FeatureAvailability.Supported;
                if (outcome == ConnectOutcome.NoDevice) return FeatureAvailability.Unsupported;
                Thread.Sleep(ProbeRetryDelayMs);
                lock (_lock) outcome = ConnectCoreLocked();
                return outcome == ConnectOutcome.Success ? FeatureAvailability.Supported : FeatureAvailability.Unsupported;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("RGB probe FAIL: " + ex.Message);
                return FeatureAvailability.Unknown;
            }
        }
    }

    /// <summary>
    /// 断线重连：关 → 等待 → 枚举 → 打开 → 重新初始化。
    /// 慢操作（Sleep/枚举/打开）一律在 _lock 之外执行——帧循环只在最终换流的瞬间被短暂阻塞，
    /// 不会像旧实现那样持锁停摆数百毫秒。
    /// </summary>
    bool Reconnect()
    {
        if (ReconnectProbe is not null) return ReconnectProbe();   // 测试 seam
        lock (_lock)
        {
            _stream?.Dispose();
            _stream = null;
            ForgetBrightnessObservation();
        }
        Thread.Sleep(100);
        var dev = ResolveDevice();
        if (dev is null || !dev.Open()) return false;
        Thread.Sleep(20);
        lock (_lock)
        {
            if (_disposed) { dev.Dispose(); return false; }
            _stream?.Dispose();
            _stream = dev;
            return EnterCustomMode();
        }
    }

    /// <summary>
    /// 单飞后台重连：发帧失败后立即返回，重连在独立任务里进行；期间帧被跳过（不阻塞），
    /// 恢复后自动续帧。连续失败仍由 <see cref="RegisterFail"/> 计数，保留 ≥10 次停效果的语义。
    /// </summary>
    void RequestReconnect()
    {
        lock (_reconnectLock)
        {
            if (_disposed) return;
            if (_reconnecting)
            {
                if (Stopwatch.GetElapsedTime(_reconnectStartedTicks).TotalMilliseconds < ReconnectTimeoutMs) return;
                Logger.WriteLine($"RGB reconnect 超时（>{ReconnectTimeoutMs}ms），重置单飞标志后重试");
            }
            _reconnecting = true;
            _reconnectStartedTicks = Stopwatch.GetTimestamp();
        }
        _ = Task.Run(() =>
        {
            bool ok = false;
            try { ok = Reconnect(); }
            catch (Exception ex) { Logger.WriteLine("RGB reconnect fail: " + ex.Message); }
            finally { _reconnecting = false; }
            if (!ok) RegisterFail();   // 重连失败才计一次失败（飞行期间的跳过帧不算）
        });
    }

    /// <summary>发送一帧（亮度缩放 → step3 → 8×64B 块）。失败后不在锁内重连：本帧立即返回，重连交给后台单飞任务。</summary>
    public bool SendFrame(byte[] buf)
    {
        bool sent;
        lock (_lock)
        {
            if (_disposed || _reconnecting || _stream is null) return false;   // 重连在飞行：本帧跳过
            sent = WriteFrameLocked(buf);
            if (sent)
            {
                _failCount = 0;
                _successFrames++;
                Heartbeat();
            }
        }
        if (sent) return true;
        RegisterFail();
        RequestReconnect();
        return false;
    }

    /// <summary>持 _lock 执行一次完整写帧；任一报告失败即返回 false（不重连、不计数）。</summary>
    bool WriteFrameLocked(byte[] buf)
    {
        if (_stream is null) return false;
        byte[] scaled = buf;
        int bright = Brightness;
        if (bright < 100)
        {
            scaled = _scaledFrame;
            for (int i = 0; i < BufSize; i++) scaled[i] = (byte)((int)buf[i] * bright / 100);
        }
        if (!_stream.SetFeature(Step3))
        {
            // 效果帧的 step3 拒绝和显式亮度探测是同一次写入。只记失败：
            // 随后一帧成功不能把已观察到的拒绝藏到断线为止。无流的提前返回不是尝试。
            _hidBrightnessTookEffect = false;
            _brightnessWriteObserved = true;
            LogSendFail("step3");
            return false;
        }
        for (int i = 0; i < 8; i++)
        {
            byte[] chunk = _frameChunks[i];
            Array.Copy(scaled, i * 64, chunk, 1, 64);
            if (!_stream.Write(chunk)) { LogSendFail($"chunk{i}"); return false; }
        }
        return true;
    }

    /// <summary>连续失败计数：≥10 次自动停止效果线程并通知 UI（防设备消失时忙循环写死设备）。</summary>
    void RegisterFail()
    {
        if (Interlocked.Increment(ref _failCount) >= 10)
        {
            _failCount = 0;
            _stop = true;   // 效果线程循环检查后自行退出（SendFrame 持有 lock，不能在此调 StopCurrentEffect）
            LastError = "键盘 RGB 设备异常（连续发送失败），灯效已停止";
            Logger.WriteLine($"RGB device lost: {LastError}");
            _ = Task.Run(() => DeviceLost?.Invoke());
        }
    }

    /// <summary>单键写入：宽键（Enter/左 Shift）同步写辅助 LED 槽。</summary>
    static void SetKey(byte[] buf, int offset, byte r, byte g, byte b)
    {
        if (offset < 0 || offset + 2 >= BufSize) return;
        buf[offset] = r; buf[offset + 1] = g; buf[offset + 2] = b;
        int aux = offset == OffsetShiftLeft ? OffsetShiftLeftAux
            : offset == OffsetEnter ? OffsetEnterAux : -1;
        if (aux >= 0 && aux + 2 < BufSize) { buf[aux] = r; buf[aux + 1] = g; buf[aux + 2] = b; }
    }

    static void HsvToRgb(double h, out byte r, out byte g, out byte b)
    {
        h %= 360.0; if (h < 0) h += 360.0;
        double x = 1.0 - Math.Abs(h / 60.0 % 2 - 1);
        (double rr, double gg, double bb) = h switch
        {
            < 60 => (1.0, x, 0.0), < 120 => (x, 1.0, 0.0), < 180 => (0.0, 1.0, x),
            < 240 => (0.0, x, 1.0), < 300 => (x, 0.0, 1.0), _ => (1.0, 0.0, x),
        };
        r = (byte)(rr * 255); g = (byte)(gg * 255); b = (byte)(bb * 255);
    }

    /// <summary>键的虚拟 X 坐标（波浪投影用，对齐 C 的 get_key_x）。</summary>
    static double GetKeyX(KeyEntry k)
    {
        if (k.Name is "AltGr") return 10.5;
        if (k.Name is "CopilotKey") return 11.5;
        if (k.Name is "ArrowUp") return 13.0;
        if (k.Name is "ArrowLeft") return 12.0;
        if (k.Name is "ArrowDown") return 13.0;
        if (k.Name is "ArrowRight") return 14.0;
        if (k.Name is "Numpad0") return 15.0;
        if (k.Name is "NumpadDecimal") return 16.0;
        const double gap = 1.5;
        if (k.Name.StartsWith("Numpad") || k.Name is "NumLock") return k.Col + gap;
        if (k.Name is "FnLockCam" or "PrtSc" or "Del" or "Home" or "PgUp" or "PgDn" or "End") return k.Col + gap;
        return k.Col;
    }

    /// <summary>启动效果（切换时停止旧效果并发 blank 清屏）。</summary>
    public void StartMode(int mode)
    {
        lock (_startLock)
        {
            if (!ShouldRestartEffect(mode, _activeMode, _stop, _effectThread?.IsAlive == true)) return;
            StopCurrentEffect();   // 不在 SendFrame 锁内调用：Stop 内含 SendFrame 持锁 + Join 等待
            int gen = Interlocked.Increment(ref _generation);   // 代际必须原子：++ 读改写并发时两个线程可能拿到同代际
            _activeMode = mode;
            _stop = false;
            _effectThread = new Thread(() => RunEffect(mode, gen))
            {
                IsBackground = true,
                Name = "L-Mechrevo RGB",
                Priority = ThreadPriority.Normal,
            };
            _effectThread.Start();
        }
    }

    internal static bool ShouldRestartEffect(int requestedMode, int activeMode, bool stopping, bool threadAlive) =>
        requestedMode != activeMode || stopping || !threadAlive;

    /// <summary>
    /// 强制重启效果线程：即使 <see cref="ActiveMode"/> 已等于目标、线程 <c>IsAlive</c>，也先停后起。
    /// 系统唤醒后固件可能已退出帧模式、旧线程可能卡在失效句柄上——"看起来在跑"不等于"还在亮"。
    /// </summary>
    public void StartModeForced(int mode)
    {
        // 强制先停：清 _activeMode/_effectThread，使 StartMode 的 ShouldRestartEffect 必真。
        StopCurrentEffect();
        StartMode(mode);
    }

    bool _brightnessWriteObserved;
    bool _hidBrightnessTookEffect = true;

    /// <summary>断流后清掉上一次连接的写入观察。未连接不是失败，默认回到 true。</summary>
    void ForgetBrightnessObservation()
    {
        _brightnessWriteObserved = false;
        _hidBrightnessTookEffect = true;
        CustomModeReadback = null;
    }

    /// <summary>
    /// 软件（HID 逐键帧）路径的结果三态：流打开且 0x88 回读确认自定义帧模式 → 已确认（设备回读）；
    /// 流打开但没有回读证据 → 已下发；流没打开 → 失败。
    /// </summary>
    internal LightApplyOutcome SoftwarePathOutcome =>
        LightingEffectCatalog.ResolveOutcome(IsConnected, IsConnected ? CustomModeReadback : null);

    /// <summary>
    /// 路由用的亮度写入结果。只有设备已连接、判定为 Supported、且这次写入真的失败时才返回 false。
    /// Unknown 与未尝试（未连接）保持 true，避免把未探测的机器永久送进 GCU 并跳过探测。
    /// </summary>
    internal bool BrightnessWriteTookEffectForRouting()
    {
        if (!IsConnected || ControllerAvailability != FeatureAvailability.Supported)
            return true;
        if (_brightnessWriteObserved) return _hidBrightnessTookEffect;
        return ApplyBrightnessToDevice();
    }

    /// <summary>
    /// N9-2：把当前 <see cref="Brightness"/> 经 HID 下发到设备，并报告这次写入是否真的生效。
    /// 设备在但亮度字段不被接受时返回 <c>false</c>，调用方据此回退官方（GCU）通道，而不是静默无操作。
    /// 无设备时返回 <c>false</c>——调用方必须先确认 <see cref="IsConnected"/> 再采信本结果，
    /// 否则构造期/断连期会把判定污染成「亮度不生效」。
    /// </summary>
    public bool ApplyBrightnessToDevice()
    {
        lock (_lock)
        {
            if (_stream is null) return false;
            // 亮度随效果帧的 step3 feature report 下发；写入被拒即是「该控制器不接受亮度字段」的信号。
            bool tookEffect = _stream.SetFeature(Step3);
            _hidBrightnessTookEffect = tookEffect;
            _brightnessWriteObserved = true;
            return tookEffect;
        }
    }

    /// <summary>重新初始化自定义帧模式（官方固件效果执行后固件退出帧模式，HID 帧被忽略——切回时需重进）。</summary>
    public bool ReInitCustomMode()
    {
        lock (_lock)
        {
            if (_stream is null || !EnterCustomMode()) return false;
            Interlocked.Increment(ref _customModeReinitCount);
            return true;
        }
    }

    public void StopCurrentEffect()
    {
        _stop = true;
        _effectThread?.Join(500);   // Join 在锁外：效果线程可能正阻塞于 SendFrame 的锁，持锁 Join 会超时残留旧线程
        _effectThread = null;
        _activeMode = -1;
        if (_stream is not null) SendFrame(new byte[BufSize]);   // blank 清屏（SendFrame 内部自持锁）
    }

    void RunEffect(int mode, int generation)
    {
        AcquirePreciseTimer();   // 效果播放期把计时器粒度提升到 1ms（缩小 Sleep 量化误差），结束时释放
        try
        {
            switch (mode)
            {
                case ModeBreath: EffectBreath(generation); break;
                case ModeWave: EffectWave(generation); break;
                case ModeSparkle: EffectSparkle(generation); break;
                case ModeReactive: EffectReactive(generation); break;
                case ModeWheel: EffectWheel(generation); break;
                case ModeLightning: EffectLightning(generation); break;
                case ModeFlame: EffectFlame(generation); break;
                case ModeRain: EffectRain(generation); break;
                case ModeMatrix: EffectMatrix(generation); break;
                case ModeStatic: EffectStatic(generation); break;
            }
        }
        finally
        {
            ReleasePreciseTimer();
        }
    }

    // ---- 效果播放期的高精度计时器：timeBeginPeriod(1)，引用计数支持重叠代际 ----
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    static extern uint TimeBeginPeriod(uint period);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    static extern uint TimeEndPeriod(uint period);
    static readonly object _timerPeriodLock = new();
    static int _timerPeriodUsers;
    static bool _timerPeriodActive;

    static void AcquirePreciseTimer()
    {
        lock (_timerPeriodLock)
        {
            if (_timerPeriodUsers++ == 0) _timerPeriodActive = TimeBeginPeriod(1) == 0;
        }
    }

    static void ReleasePreciseTimer()
    {
        lock (_timerPeriodLock)
        {
            if (_timerPeriodUsers > 0 && --_timerPeriodUsers == 0 && _timerPeriodActive)
            {
                TimeEndPeriod(1);
                _timerPeriodActive = false;
            }
        }
    }

    /// <summary>效果循环公共壳：发帧 + 帧率。（无操作熄灯由应用级空闲监视统一决策，不在此各自计时。）</summary>
    void FrameLoop(int generation, Action<byte[]> fill, Func<double> tick)
    {
        double t = 0;
        var buf = new byte[BufSize];
        while (!_stop && generation == _generation)
        {
            Array.Clear(buf);
            fill(buf);
            SendFrame(buf);
            t += tick();
            PaceFrame(TargetFps);
        }
    }

    void EffectBreath(int gen) => FrameLoop(gen, buf =>
    {
        double raw = (Math.Sin(BreathState) + 1) / 2.0;
        int levels = Math.Max(2, BreathSmooth);
        double bright = Math.Floor(raw * levels) / levels;
        byte r = (byte)(BreathColor.R * bright), g = (byte)(BreathColor.G * bright), b = (byte)(BreathColor.B * bright);
        foreach (var k in Keymap) SetKey(buf, k.Offset, r, g, b);
    }, () => { BreathState += BreathSpeed / 100.0; return 0; });
    double BreathState;

    void EffectWave(int gen) => FrameLoop(gen, buf =>
    {
        double angleRad = WaveAngle * Math.PI / 180.0;
        double dirX = Math.Cos(angleRad), dirY = Math.Sin(angleRad);
        foreach (var k in Keymap)
        {
            double proj = GetKeyX(k) * dirX + k.Row * dirY;
            HsvToRgb(WaveState + proj * 15.0, out byte r, out byte g, out byte b);
            SetKey(buf, k.Offset, r, g, b);
        }
    }, () => { WaveState += WaveSpeed; return 0; });
    double WaveState;

    void EffectWheel(int gen)
    {
        (double cx, double cy) = GetWheelCenter();
        FrameLoop(gen, buf =>
        {
            double dirMul = WheelReverse ? -1.0 : 1.0;
            foreach (var k in Keymap)
            {
                double angleDeg = Math.Atan2(k.Row - cy, GetKeyX(k) - cx) * 180.0 / Math.PI;
                HsvToRgb(WheelState * dirMul + angleDeg, out byte r, out byte g, out byte b);
                SetKey(buf, k.Offset, r, g, b);
            }
        }, () => { WheelState += WheelSpeed; return 0; });
    }
    double WheelState;

    internal static (double X, double Y) GetWheelCenter()
    {
        KeyEntry center = Keymap.First(key => key.Name == "K");
        return (GetKeyX(center), center.Row);
    }

    void EffectSparkle(int gen)
    {
        var life = new int[Keymap.Length];
        var maxLife = new int[Keymap.Length];
        var rr = new byte[Keymap.Length]; var gg = new byte[Keymap.Length]; var bb = new byte[Keymap.Length];
        FrameLoop(gen, buf =>
        {
            int target = Math.Clamp(SparkleDensity, 1, 20);
            int lifeDuration = Math.Max(8, 50 - SparkleSpeed * 4);
            int alive = life.Count(l => l > 0);
            for (int n = 0; n < target - alive; n++)
            {
                int idx = -1;
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    int c = Random.Shared.Next(Keymap.Length);
                    if (life[c] == 0) { idx = c; break; }
                }
                if (idx < 0) break;
                life[idx] = lifeDuration;
                maxLife[idx] = lifeDuration;
                rr[idx] = (byte)Random.Shared.Next(256);
                gg[idx] = (byte)Random.Shared.Next(256);
                bb[idx] = (byte)Random.Shared.Next(256);
            }
            for (int i = 0; i < Keymap.Length; i++)
            {
                if (life[i] > 0)
                {
                    double fade = life[i] / (double)maxLife[i];
                    SetKey(buf, Keymap[i].Offset, (byte)(rr[i] * fade), (byte)(gg[i] * fade), (byte)(bb[i] * fade));
                    life[i]--;
                }
            }
        }, () => 1);
    }

    void EffectReactive(int gen)
    {
        var wasDown = new bool[Keymap.Length];
        var fadeLife = new int[Keymap.Length];
        var rr = new byte[Keymap.Length]; var gg = new byte[Keymap.Length]; var bb = new byte[Keymap.Length];
        var vkList = new List<int>();
        for (int vk = 'A'; vk <= 'Z'; vk++) vkList.Add(vk);
        for (int vk = '0'; vk <= '9'; vk++) vkList.Add(vk);
        int[] specials =
        {
            (int)Keys.Space, (int)Keys.Enter, (int)Keys.Back, (int)Keys.Tab, (int)Keys.LShiftKey, (int)Keys.RShiftKey,
            (int)Keys.CapsLock, (int)Keys.LControlKey, (int)Keys.LMenu, (int)Keys.RMenu, (int)Keys.Left, (int)Keys.Right,
            (int)Keys.Up, (int)Keys.Down, (int)Keys.Oem102, (int)Keys.LWin,
            (int)Keys.Oem4, (int)Keys.Oem6, (int)Keys.Oem1, (int)Keys.Oem7, (int)Keys.Oem5,
            (int)Keys.Oemcomma, (int)Keys.OemPeriod, (int)Keys.Oem2, (int)Keys.OemMinus, (int)Keys.Oemplus, (int)Keys.Oem3,
            (int)Keys.Escape, (int)Keys.F1, (int)Keys.F2, (int)Keys.F3, (int)Keys.F4, (int)Keys.F5, (int)Keys.F6,
            (int)Keys.F7, (int)Keys.F8, (int)Keys.F9, (int)Keys.F10, (int)Keys.F11, (int)Keys.F12,
            (int)Keys.PrintScreen, (int)Keys.Delete, (int)Keys.Home, (int)Keys.PageUp, (int)Keys.PageDown, (int)Keys.End,
            (int)Keys.NumPad0, (int)Keys.NumPad1, (int)Keys.NumPad2, (int)Keys.NumPad3, (int)Keys.NumPad4,
            (int)Keys.NumPad5, (int)Keys.NumPad6, (int)Keys.NumPad7, (int)Keys.NumPad8, (int)Keys.NumPad9,
            (int)Keys.Decimal, (int)Keys.Add, (int)Keys.Subtract, (int)Keys.Multiply, (int)Keys.Divide,
        };
        vkList.AddRange(specials);
        FrameLoop(gen, buf =>
        {
            for (int v = 0; v < vkList.Count; v++)
            {
                int idx = VkToKeymapIndex(vkList[v]);
                if (idx < 0) continue;
                bool down = (GetAsyncKeyState(vkList[v]) & 0x8000) != 0;
                if (down)
                {
                    if (!wasDown[idx])
                    {
                        if (ReactiveRandom) { rr[idx] = (byte)Random.Shared.Next(256); gg[idx] = (byte)Random.Shared.Next(256); bb[idx] = (byte)Random.Shared.Next(256); }
                        else { rr[idx] = ReactiveColor.R; gg[idx] = ReactiveColor.G; bb[idx] = ReactiveColor.B; }
                    }
                    wasDown[idx] = true;
                    fadeLife[idx] = ReactiveDuration;
                }
                else wasDown[idx] = false;
            }
            for (int i = 0; i < Keymap.Length; i++)
            {
                if (wasDown[i]) SetKey(buf, Keymap[i].Offset, rr[i], gg[i], bb[i]);
                else if (fadeLife[i] > 0)
                {
                    double fade = fadeLife[i] / (double)Math.Max(1, ReactiveDuration);
                    SetKey(buf, Keymap[i].Offset, (byte)(rr[i] * fade), (byte)(gg[i] * fade), (byte)(bb[i] * fade));
                    fadeLife[i]--;
                }
            }
        }, () => 1);
    }

    const int MaxStrikes = 4;
    void EffectLightning(int gen)
    {
        var headPos = new double[MaxStrikes]; var boltX = new double[MaxStrikes]; var countdown = new int[MaxStrikes];
        for (int i = 0; i < MaxStrikes; i++) { headPos[i] = 100.0; boltX[i] = 10.0; countdown[i] = 10 + Random.Shared.Next(30); }
        FrameLoop(gen, buf =>
        {
            double speed = 0.05 + LightningSpeed * 0.08;
            double bandWidth = 0.4 + LightningSmooth * 0.15;
            double tailLen = bandWidth * 2.5;
            double finishPos = 6.0 + tailLen + 0.5;
            double xWidth = 0.35 + (LightningWidth - 1.0) * 0.383;
            int concurrent = Math.Clamp(LightningConcurrent, 1, MaxStrikes);
            var bright = new double[Keymap.Length];
            for (int s = 0; s < concurrent; s++)
            {
                if (headPos[s] > finishPos)
                {
                    if (--countdown[s] <= 0)
                    {
                        headPos[s] = -2.0;
                        boltX[s] = Random.Shared.Next(20);
                        countdown[s] = 20 + Random.Shared.Next(40);
                    }
                }
                if (headPos[s] <= finishPos)
                {
                    for (int i = 0; i < Keymap.Length; i++)
                    {
                        double dv = headPos[s] - Keymap[i].Row;
                        if (dv < -0.3 || dv > tailLen) continue;
                        double dh = Math.Abs(GetKeyX(Keymap[i]) - boltX[s]);
                        if (dh > xWidth) continue;
                        double vFall = dv < 0 ? 1.0 : (1.0 - dv / tailLen);
                        double hFall = 1.0 - dh / xWidth;
                        if (vFall < 0) vFall = 0;
                        if (hFall < 0) hFall = 0;
                        double b = vFall * hFall;
                        if (b > bright[i]) bright[i] = b;
                    }
                    headPos[s] += speed;
                }
            }
            for (int i = 0; i < Keymap.Length; i++)
            {
                if (bright[i] > 0) SetKey(buf, Keymap[i].Offset, (byte)(bright[i] * 255), (byte)(bright[i] * 255), 255);
                else SetKey(buf, Keymap[i].Offset, 4, 4, 14);
            }
        }, () => 1);
    }

    void EffectFlame(int gen)
    {
        var heat = new double[Keymap.Length];
        for (int i = 0; i < Keymap.Length; i++) heat[i] = 100.0 + Random.Shared.Next(60);
        int frameCounter = 0;
        FrameLoop(gen, buf =>
        {
            double alpha = 1.0 / (1.0 + FlameSmooth * 0.6);
            double volatility = 40.0 + FlameSpeed * 18.0;
            int updateInterval = Math.Max(1, 11 - FlameSpeed);
            if (++frameCounter % updateInterval == 0)
            {
                for (int i = 0; i < Keymap.Length; i++)
                {
                    double rowFrac = Keymap[i].Row / 6.0;
                    double bias = 40.0 * rowFrac;
                    double target = 90.0 + bias + (Random.Shared.NextDouble() * volatility - volatility / 2.0);
                    heat[i] = Math.Clamp(heat[i] * (1.0 - alpha) + target * alpha, 0, 255);
                }
            }
            for (int i = 0; i < Keymap.Length; i++)
            {
                int h = (int)heat[i];
                byte r, g, b;
                if (h < 85) { r = (byte)(h * 3); g = 0; b = 0; }
                else if (h < 170) { r = 255; g = (byte)((h - 85) * 3); b = 0; }
                else { r = 255; g = 255; b = (byte)((h - 170) * 3); }
                SetKey(buf, Keymap[i].Offset, r, g, b);
            }
        }, () => 1);
    }

    const int MaxRainDrops = 20;
    void EffectRain(int gen)
    {
        var dropX = new double[MaxRainDrops]; var dropRow = new double[MaxRainDrops]; var dropColor = new int[MaxRainDrops];
        for (int i = 0; i < MaxRainDrops; i++)
        {
            dropX[i] = Random.Shared.Next(20);
            dropRow[i] = -Random.Shared.Next(8);
            dropColor[i] = Random.Shared.Next(Math.Max(1, RainColorCount));
        }
        FrameLoop(gen, buf =>
        {
            double speed = 0.05 + RainSpeed * 0.03;
            int colorCount = Math.Clamp(RainColorCount, 1, 5);
            int activeDrops = Math.Min(MaxRainDrops, Math.Clamp(RainDensity, 1, 5) * 3);
            for (int d = 0; d < activeDrops; d++)
            {
                var col = RainColors[dropColor[d] % colorCount];
                for (int rr2 = 0; rr2 <= 6; rr2++)
                {
                    double trailDist = dropRow[d] - rr2;
                    if (trailDist < -0.3 || trailDist > 1.8) continue;
                    double bright = trailDist < 0 ? 1.0 : (1.0 - trailDist / 1.8);
                    if (bright < 0) bright = 0;
                    int best = -1; double bestDist = 1e9;
                    for (int i = 0; i < Keymap.Length; i++)
                    {
                        if (Keymap[i].Row != rr2) continue;
                        double dx = Math.Abs(GetKeyX(Keymap[i]) - dropX[d]);
                        if (dx < bestDist) { bestDist = dx; best = i; }
                    }
                    if (best >= 0 && bestDist < 1.2)
                        SetKey(buf, Keymap[best].Offset, (byte)(col.R * bright), (byte)(col.G * bright), (byte)(col.B * bright));
                }
                dropRow[d] += speed;
                if (dropRow[d] > 8)
                {
                    dropRow[d] = -Random.Shared.Next(5);
                    dropX[d] = Random.Shared.Next(20);
                    dropColor[d] = Random.Shared.Next(colorCount);
                }
            }
        }, () => 1);
    }

    const int MatrixStreaks = 5, MatrixColumns = 24, MaxLasers = 3, MaxRipples = 3;
    void EffectMatrix(int gen)
    {
        var angle = new double[MatrixStreaks]; var pos = new double[MatrixStreaks]; var spd = new double[MatrixStreaks];
        var streakColor = new int[MatrixStreaks];
        for (int i = 0; i < MatrixStreaks; i++)
        {
            angle[i] = Random.Shared.Next(360) * Math.PI / 180.0;
            pos[i] = -30.0;
            spd[i] = 0.2 + Random.Shared.Next(100) / 100.0;
            streakColor[i] = Random.Shared.Next(Math.Max(1, MatrixColorCount));
        }
        var colPos = new double[MatrixColumns]; var colSpeed = new double[MatrixColumns]; var colColor = new int[MatrixColumns];
        for (int i = 0; i < MatrixColumns; i++)
        {
            colPos[i] = -Random.Shared.Next(8);
            colSpeed[i] = 0.05 + Random.Shared.Next(100) / 300.0;
            colColor[i] = Random.Shared.Next(Math.Max(1, MatrixColorCount));
        }
        var laserAngle = new double[MaxLasers]; var laserPos = new double[MaxLasers]; var laserSpd = new double[MaxLasers];
        var laserColor = new int[MaxLasers];
        for (int i = 0; i < MaxLasers; i++)
        {
            laserAngle[i] = Random.Shared.Next(4) * (Math.PI / 2.0);
            laserPos[i] = -30.0 - i * 8.0;
            laserSpd[i] = 0.6 + Random.Shared.Next(100) / 100.0;
            laserColor[i] = Random.Shared.Next(Math.Max(1, MatrixColorCount));
        }
        var rippleX = new double[MaxRipples]; var rippleY = new double[MaxRipples]; var rippleR = new double[MaxRipples];
        var rippleColor = new int[MaxRipples];
        for (int i = 0; i < MaxRipples; i++)
        {
            rippleX[i] = Random.Shared.Next(20); rippleY[i] = Random.Shared.Next(7); rippleR[i] = i * 8.0;
            rippleColor[i] = Random.Shared.Next(Math.Max(1, MatrixColorCount));
        }
        int activeStreaks = Math.Clamp(MatrixDensity, 1, 5);
        int rerollCounter = 0;
        FrameLoop(gen, buf =>
        {
            int colorCount = Math.Clamp(MatrixColorCount, 1, 3);
            int style = Math.Clamp(MatrixStyle, 0, 3);
            double speedMul = 0.3 + MatrixSpeed * 0.15;
            if (style == 0)
            {
                if (++rerollCounter >= 60) { activeStreaks = ComputeProbCount(Math.Clamp(MatrixDensity, 1, 5), 1, 5); rerollCounter = 0; }
                for (int i = 0; i < Keymap.Length; i++)
                {
                    double x = GetKeyX(Keymap[i]), y = Keymap[i].Row;
                    double bestBright = 0; int bestColor = 0;
                    for (int s = 0; s < activeStreaks; s++)
                    {
                        double dirX = Math.Cos(angle[s]), dirY = Math.Sin(angle[s]);
                        double dist = Math.Abs(x * dirX + y * dirY - pos[s]);
                        if (dist < 1.5) { double b = 1.0 - dist / 1.5; if (b > bestBright) { bestBright = b; bestColor = streakColor[s] % colorCount; } }
                    }
                    if (bestBright > 1) bestBright = 1;
                    var col = MatrixColors[bestColor];
                    SetKey(buf, Keymap[i].Offset, (byte)(col.R * bestBright), (byte)(col.G * bestBright), (byte)(col.B * bestBright));
                }
                for (int s = 0; s < activeStreaks; s++)
                {
                    pos[s] += spd[s] * speedMul;
                    if (pos[s] > 30) { pos[s] = -30; angle[s] = Random.Shared.Next(360) * Math.PI / 180.0; spd[s] = 0.2 + Random.Shared.Next(100) / 100.0; streakColor[s] = Random.Shared.Next(colorCount); }
                }
            }
            else if (style == 1)
            {
                for (int i = 0; i < Keymap.Length; i++)
                {
                    int col = Math.Clamp((int)(GetKeyX(Keymap[i]) + 0.5), 0, MatrixColumns - 1);
                    double dv = colPos[col] - Keymap[i].Row;
                    double bright = 0;
                    if (dv >= -0.3 && dv < 4.0) bright = dv < 0 ? 1.0 : (1.0 - dv / 4.0);
                    if (bright < 0) bright = 0;
                    var cc = MatrixColors[colColor[col] % colorCount];
                    SetKey(buf, Keymap[i].Offset, (byte)(cc.R * bright), (byte)(cc.G * bright), (byte)(cc.B * bright));
                }
                for (int i = 0; i < MatrixColumns; i++)
                {
                    colPos[i] += colSpeed[i] * speedMul;
                    if (colPos[i] > 10) { colPos[i] = -Random.Shared.Next(6); colSpeed[i] = 0.05 + Random.Shared.Next(100) / 300.0; colColor[i] = Random.Shared.Next(colorCount); }
                }
            }
            else if (style == 2)
            {
                int laserCount = Math.Clamp(MatrixDensity, 1, 5) >= 4 ? 3 : 2;
                for (int i = 0; i < Keymap.Length; i++)
                {
                    double x = GetKeyX(Keymap[i]), y = Keymap[i].Row;
                    double bestBright = 0; int bestColor = 0;
                    for (int s = 0; s < laserCount; s++)
                    {
                        double dirX = Math.Cos(laserAngle[s]), dirY = Math.Sin(laserAngle[s]);
                        double dist = Math.Abs(x * dirX + y * dirY - laserPos[s]);
                        if (dist < 0.6) { double b = 1.0 - dist / 0.6; if (b > bestBright) { bestBright = b; bestColor = laserColor[s] % colorCount; } }
                    }
                    if (bestBright > 1) bestBright = 1;
                    var col = MatrixColors[bestColor];
                    SetKey(buf, Keymap[i].Offset, (byte)(col.R * bestBright), (byte)(col.G * bestBright), (byte)(col.B * bestBright));
                }
                for (int s = 0; s < laserCount; s++)
                {
                    laserPos[s] += laserSpd[s] * speedMul * 1.5;
                    if (laserPos[s] > 30) { laserPos[s] = -30.0 - Random.Shared.Next(20); laserAngle[s] = Random.Shared.Next(4) * (Math.PI / 2.0); laserSpd[s] = 0.6 + Random.Shared.Next(100) / 100.0; laserColor[s] = Random.Shared.Next(colorCount); }
                }
            }
            else
            {
                for (int i = 0; i < Keymap.Length; i++)
                {
                    double x = GetKeyX(Keymap[i]), y = Keymap[i].Row;
                    double bestBright = 0; int bestColor = 0;
                    for (int s = 0; s < MaxRipples; s++)
                    {
                        double dx = x - rippleX[s], dy = y - rippleY[s];
                        double ringDist = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - rippleR[s]);
                        if (ringDist < 1.2) { double b = 1.0 - ringDist / 1.2; if (b > bestBright) { bestBright = b; bestColor = rippleColor[s] % colorCount; } }
                    }
                    if (bestBright > 1) bestBright = 1;
                    var col = MatrixColors[bestColor];
                    SetKey(buf, Keymap[i].Offset, (byte)(col.R * bestBright), (byte)(col.G * bestBright), (byte)(col.B * bestBright));
                }
                for (int s = 0; s < MaxRipples; s++)
                {
                    rippleR[s] += speedMul * 0.25;
                    if (rippleR[s] > 24) { rippleX[s] = Random.Shared.Next(20); rippleY[s] = Random.Shared.Next(7); rippleR[s] = 0; rippleColor[s] = Random.Shared.Next(colorCount); }
                }
            }
        }, () => 1);
    }

    static int ComputeProbCount(int target, int minCount, int maxCount)
    {
        if (target >= maxCount) return maxCount;
        int r = Random.Shared.Next(100);
        if (r < 25) return Math.Max(minCount, target - 1);
        else if (r < 75) return target;
        else return Math.Min(maxCount, target + 1);
    }

    int[]? _xRank;

    void EffectStatic(int gen)
    {
        if (_xRank is null)   // 按 X 坐标排序的键位排名（纵向分区用）
        {
            var ordered = Keymap.Select((k, i) => (x: GetKeyX(k), i)).OrderBy(t => t.x).ToList();
            _xRank = new int[Keymap.Length];
            for (int r = 0; r < ordered.Count; r++) _xRank[ordered[r].i] = r;
        }
        FrameLoop(gen, buf =>
        {
            int maxZones = StaticLayout == 0 ? 6 : 10;
            int zoneCount = Math.Clamp(StaticZones, 1, maxZones);
            for (int i = 0; i < Keymap.Length; i++)
            {
                int zone = 0;
                if (zoneCount > 1)
                {
                    if (StaticLayout == 0) zone = HorizontalZoneForRow(Keymap[i].Row, zoneCount);
                    else
                    {
                        zone = _xRank![i] * zoneCount / Keymap.Length;
                        if (zone >= zoneCount) zone = zoneCount - 1;
                    }
                }
                var c = StaticColors[zone];
                SetKey(buf, Keymap[i].Offset, c.R, c.G, c.B);
            }
        }, () => 1);
    }

    static int HorizontalZoneForRow(int row, int zoneCount) => zoneCount switch
    {
        1 => 0,
        2 => row <= 3 ? 0 : 1,
        3 => row <= 1 ? 0 : row <= 4 ? 1 : 2,
        4 => row == 0 ? 0 : row <= 2 ? 1 : row <= 4 ? 2 : 3,
        5 => row == 0 ? 0 : row == 1 ? 1 : row == 2 ? 2 : row <= 4 ? 3 : 4,
        6 => row == 0 ? 0 : row == 1 ? 1 : row == 2 ? 2 : row == 3 ? 3 : row == 4 ? 4 : 5,
        _ => 0,
    };

    /// <summary>虚拟键码 → 键位表索引（对照 C 的 vk_to_keymap_index）。</summary>
    static int VkToKeymapIndex(int vk)
    {
        for (int i = 0; i < Keymap.Length; i++)
        {
            var n = Keymap[i].Name;
            if (((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9')) && n.Length == 1 && n[0] == (char)vk) return i;
        }
        var table = new (int vk, string name)[]
        {
            ((int)Keys.Space, "Space"), ((int)Keys.Enter, "Enter"), ((int)Keys.Back, "Backspace"), ((int)Keys.Tab, "Tab"),
            ((int)Keys.LShiftKey, "ShiftL"), ((int)Keys.RShiftKey, "ShiftR"), ((int)Keys.CapsLock, "CapsLock"), ((int)Keys.LControlKey, "CtrlL"),
            ((int)Keys.LMenu, "AltL"), ((int)Keys.RMenu, "AltGr"), ((int)Keys.Left, "ArrowLeft"), ((int)Keys.Right, "ArrowRight"),
            ((int)Keys.Up, "ArrowUp"), ((int)Keys.Down, "ArrowDown"), ((int)Keys.Oem102, "ISO_Backslash"), ((int)Keys.LWin, "Windows"),
            ((int)Keys.Oem4, "BracketL"), ((int)Keys.Oem6, "BracketR"), ((int)Keys.Oem1, "Semicolon"), ((int)Keys.Oem7, "Quote"),
            ((int)Keys.Oem5, "Backslash"), ((int)Keys.Oemcomma, "Comma"), ((int)Keys.OemPeriod, "Period"), ((int)Keys.Oem2, "Slash"),
            ((int)Keys.OemMinus, "Minus"), ((int)Keys.Oemplus, "Equals"), ((int)Keys.Oem3, "Grave"),
            ((int)Keys.Escape, "Esc"), ((int)Keys.F1, "F1"), ((int)Keys.F2, "F2"), ((int)Keys.F3, "F3"), ((int)Keys.F4, "F4"),
            ((int)Keys.F5, "F5"), ((int)Keys.F6, "F6"), ((int)Keys.F7, "F7"), ((int)Keys.F8, "F8"), ((int)Keys.F9, "F9"),
            ((int)Keys.F10, "F10"), ((int)Keys.F11, "F11"), ((int)Keys.F12, "F12"),
            ((int)Keys.PrintScreen, "PrtSc"), ((int)Keys.Delete, "Del"), ((int)Keys.Home, "Home"), ((int)Keys.PageUp, "PgUp"),
            ((int)Keys.PageDown, "PgDn"), ((int)Keys.End, "End"),
            ((int)Keys.NumPad0, "Numpad0"), ((int)Keys.NumPad1, "Numpad1"), ((int)Keys.NumPad2, "Numpad2"), ((int)Keys.NumPad3, "Numpad3"),
            ((int)Keys.NumPad4, "Numpad4"), ((int)Keys.NumPad5, "Numpad5"), ((int)Keys.NumPad6, "Numpad6"), ((int)Keys.NumPad7, "Numpad7"),
            ((int)Keys.NumPad8, "Numpad8"), ((int)Keys.NumPad9, "Numpad9"), ((int)Keys.Decimal, "NumpadDecimal"), ((int)Keys.Add, "NumpadPlus"),
            ((int)Keys.Subtract, "NumpadMinus"), ((int)Keys.Multiply, "NumpadMul"), ((int)Keys.Divide, "NumpadDiv"),
        };
        foreach (var (v, name) in table)
        {
            if (vk == v)
                for (int i = 0; i < Keymap.Length; i++)
                    if (Keymap[i].Name == name) return i;
        }
        return -1;
    }

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    // ---- 无操作熄灯由应用级空闲监视统一决策（Program.EvaluateLightingIdleAsync）----
    // 键盘不再持有自己的睡眠计时：三条通道（键盘/灯带/Logo）共用同一个到期判定，
    // 否则键盘会先于灯带/Logo 熄灭、恢复时刻也各不一致。

    // ---- 配置持久化（rgb.cfg，key=value 格式对齐 BetterRGB 的 better_rgb.cfg）----

    /// <summary>配置路径放 %APPDATA%（单文件自解压模式下程序目录在临时区，可能被系统清理丢失）。</summary>
    public string ConfigPath => _configPathOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MechrevoLite", "rgb.cfg");

    internal string SerializeConfig()
    {
        static string Colors(IEnumerable<Color> values) => string.Join(',', values.Select(color => color.ToArgb()));
        var sb = new StringBuilder();
        sb.AppendLine($"mode={(_activeMode >= 0 ? _activeMode : Math.Max(0, _lastSavedMode))}");
        sb.AppendLine($"brightness={Brightness}");
        sb.AppendLine($"targetFps={TargetFps}");
        sb.AppendLine($"breathColor={BreathColor.ToArgb()}");
        sb.AppendLine($"breathSpeed={BreathSpeed}");
        sb.AppendLine($"breathSmooth={BreathSmooth}");
        sb.AppendLine($"waveAngle={WaveAngle}");
        sb.AppendLine($"waveSpeed={WaveSpeed}");
        sb.AppendLine($"sparkleSpeed={SparkleSpeed}");
        sb.AppendLine($"sparkleDensity={SparkleDensity}");
        sb.AppendLine($"reactiveColor={ReactiveColor.ToArgb()}");
        sb.AppendLine($"reactiveRandom={(ReactiveRandom ? 1 : 0)}");
        sb.AppendLine($"reactiveDuration={ReactiveDuration}");
        sb.AppendLine($"wheelReverse={(WheelReverse ? 1 : 0)}");
        sb.AppendLine($"wheelSpeed={WheelSpeed}");
        sb.AppendLine($"lightningSpeed={LightningSpeed}");
        sb.AppendLine($"lightningSmooth={LightningSmooth}");
        sb.AppendLine($"lightningWidth={LightningWidth}");
        sb.AppendLine($"lightningConcurrent={LightningConcurrent}");
        sb.AppendLine($"flameSpeed={FlameSpeed}");
        sb.AppendLine($"flameSmooth={FlameSmooth}");
        sb.AppendLine($"rainSpeed={RainSpeed}");
        sb.AppendLine($"rainDensity={RainDensity}");
        sb.AppendLine($"rainColorCount={RainColorCount}");
        sb.AppendLine($"rainColors={Colors(RainColors)}");
        sb.AppendLine($"matrixSpeed={MatrixSpeed}");
        sb.AppendLine($"matrixStyle={MatrixStyle}");
        sb.AppendLine($"matrixDensity={MatrixDensity}");
        sb.AppendLine($"matrixColorCount={MatrixColorCount}");
        sb.AppendLine($"matrixColors={Colors(MatrixColors)}");
        sb.AppendLine($"staticLayout={StaticLayout}");
        sb.AppendLine($"staticZones={StaticZones}");
        sb.AppendLine($"staticColors={Colors(StaticColors)}");
        sb.AppendLine($"closeTimer={CloseTimerMinutes}");
        sb.AppendLine($"kbHidMode={KbHidMode}");
        sb.AppendLine($"kbPower={(KbPowerOn ? 1 : 0)}");
        return sb.ToString();
    }

    /// <summary>参数连续变化时合并写盘；离散选择也会在短延迟后自动持久化。</summary>
    public void QueueSaveConfig()
    {
        lock (_configLock)
        {
            _configSaveTimer ??= new System.Threading.Timer(_ => SaveConfig(), null, Timeout.Infinite, Timeout.Infinite);
            _configSaveTimer.Change(250, Timeout.Infinite);
        }
    }

    public void SaveConfig()
    {
        try
        {
            lock (_configLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                string temporaryPath = ConfigPath + ".tmp";
                File.WriteAllText(temporaryPath, SerializeConfig(), new UTF8Encoding(false));
                File.Move(temporaryPath, ConfigPath, true);
            }
        }
        catch (Exception ex) { Logger.WriteLine("RGB save cfg fail: " + ex.Message); }
    }

    public void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            LoadConfigLines(File.ReadAllLines(ConfigPath));
        }
        catch (Exception ex) { Logger.WriteLine("RGB load cfg fail: " + ex.Message); }
    }

    internal void LoadConfigLines(IEnumerable<string> lines)
    {
        static void LoadColors(string text, Color[] target)
        {
            string[] values = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 0; i < target.Length && i < values.Length; i++)
                if (int.TryParse(values[i], out int argb)) target[i] = Color.FromArgb(argb);
        }

        foreach (var line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq];
            string text = line[(eq + 1)..].Trim();
            // 旧配置里的 kbEffect/kbLight/kbSpeed/kbSingleColor/kbType/kbFirmwareSignature
            // 属于已移除的官方固件效果，读到时直接忽略（未知键天然跳过）。
            if (key == "rainColors") { LoadColors(text, RainColors); continue; }
            if (key == "matrixColors") { LoadColors(text, MatrixColors); continue; }
            if (key == "staticColors") { LoadColors(text, StaticColors); continue; }
            if (!int.TryParse(text, out int v)) continue;
            switch (key)
            {
                case "mode": _lastSavedMode = v; break;
                case "brightness": Brightness = Math.Clamp(v, 0, 100); break;
                case "targetFps": TargetFps = Math.Clamp(v, 15, 60); break;
                case "breathColor": BreathColor = Color.FromArgb(v); break;
                case "breathSpeed": BreathSpeed = Math.Clamp(v, 1, 100); break;
                case "breathSmooth": BreathSmooth = Math.Clamp(v, 0, 100); break;
                case "waveAngle": WaveAngle = Math.Clamp(v, 0, 360); break;
                case "waveSpeed": WaveSpeed = Math.Clamp(v, 1, 20); break;
                case "sparkleSpeed": SparkleSpeed = Math.Clamp(v, 1, 20); break;
                case "sparkleDensity": SparkleDensity = Math.Clamp(v, 1, 20); break;
                case "reactiveColor": ReactiveColor = Color.FromArgb(v); break;
                case "reactiveRandom": ReactiveRandom = v != 0; break;
                case "reactiveDuration": ReactiveDuration = Math.Clamp(v, 5, 60); break;
                case "wheelReverse": WheelReverse = v != 0; break;
                case "wheelSpeed": WheelSpeed = Math.Clamp(v, 1, 20); break;
                case "lightningSpeed": LightningSpeed = Math.Clamp(v, 1, 20); break;
                case "lightningSmooth": LightningSmooth = Math.Clamp(v, 1, 40); break;
                case "lightningWidth": LightningWidth = Math.Clamp(v, 1, 4); break;
                case "lightningConcurrent": LightningConcurrent = Math.Clamp(v, 1, 4); break;
                case "flameSpeed": FlameSpeed = Math.Clamp(v, 1, 20); break;
                case "flameSmooth": FlameSmooth = Math.Clamp(v, 1, 40); break;
                case "rainSpeed": RainSpeed = Math.Clamp(v, 1, 20); break;
                case "rainDensity": RainDensity = Math.Clamp(v, 1, 5); break;
                case "rainColorCount": RainColorCount = Math.Clamp(v, 1, 5); break;
                case "matrixSpeed": MatrixSpeed = Math.Clamp(v, 1, 20); break;
                case "matrixStyle": MatrixStyle = Math.Clamp(v, 0, 3); break;
                case "matrixDensity": MatrixDensity = Math.Clamp(v, 1, 5); break;
                case "matrixColorCount": MatrixColorCount = Math.Clamp(v, 1, 3); break;
                case "staticLayout": StaticLayout = Math.Clamp(v, 0, 1); break;
                case "staticZones": StaticZones = Math.Clamp(v, 1, 10); break;
                case "staticColor": StaticColor = Color.FromArgb(v); break;
                case "closeTimer": CloseTimerMinutes = Math.Clamp(v, 0, 120); break;
                case "kbHidMode": KbHidMode = Math.Clamp(v, ModeBreath, ModeStatic); break;
                case "kbPower": KbPowerOn = v != 0; break;
            }
        }
    }

    int _lastSavedMode = -1;

    public void Dispose()
    {
        StopCurrentEffect();        // 先关效果并 blank 清屏（保持原有行为）
        _disposed = true;           // 阻止在飞行的重连任务回填 _stream（避免泄漏已打开的句柄）
        lock (_configLock)
        {
            _configSaveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _configSaveTimer?.Dispose();
            _configSaveTimer = null;
        }
        SaveConfig();
        lock (_lock)
        {
            _stream?.Dispose();
            _stream = null;
            ForgetBrightnessObservation();
        }
    }
}
