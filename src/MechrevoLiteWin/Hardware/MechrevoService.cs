using MechrevoLite.Gpu;
using MechrevoLite.Mode;

namespace MechrevoLite.Hardware;

public enum GpuModeStatusReadback
{
    Unavailable,
    Cached,
    Fresh,
}

/// <summary>
/// 重启切换请求的结果。UI 需要把「该方向没有可用指令」与一般失败区分开，
/// 才能在不支持时给出诚实提示，而不是含糊地说「发送失败」。
/// </summary>
internal enum GpuRestartRequestOutcome
{
    /// <summary>目标已下发，服务的 <c>DGPU_DIRECT_CONNECT_RESTART</c> 已发出（服务自己重启）。</summary>
    Requested,
    Unsupported,
    Failed,

    /// <summary>目标已下发，但服务档位没有可用的重启动作：需要本程序在用户确认下重启 Windows。</summary>
    RequiresAppRestart,
}

/// <summary>热切换 / 首选 GPU 这类「当场确认」的切换结果。</summary>
internal enum GpuApplyResult
{
    /// <summary>硬件回读与目标一致。</summary>
    Confirmed,

    /// <summary>指令已发，回读在时限内没有变成目标（已回滚或保持原状）。</summary>
    NotApplied,

    /// <summary>本机没有这条路径，一条指令都没发。</summary>
    Unsupported,

    /// <summary>未连接 / 发送失败 / 被取代。</summary>
    Failed,
}

/// <summary>重启路由：要发的 MUX 目标载荷 + 重启是否交给服务。</summary>
internal sealed record GpuRestartRoute(IReadOnlyList<Dictionary<string, object>> Payloads, bool ServiceRestart)
{
    public static readonly GpuRestartRoute None = new(Array.Empty<Dictionary<string, object>>(), false);
}

/// <summary>构建显卡路由所需的机器事实（代际 × 服务档位 × 能力位）。</summary>
public readonly record struct GpuRouteContext(
    DgpuGenerationKind Generation,
    GcuServiceTier Tier,
    bool SupportsDgpuDirect,
    bool ThreeMode,
    bool HotSwap,
    bool IgpuMuxTarget)
{
    public static GpuRouteContext From(MechrevoHw hw) => new(
        hw.DgpuGeneration, hw.ServiceTier, hw.SupportsDgpuDirect, hw.SupportsIgpuOnly,
        hw.SupportsGpuHotSwap, hw.CanOfferIgpuMuxTarget);
}

/// <summary>
/// 机械革命专用服务层：直接为 UI 提供模式/显卡切换与状态。
/// 不经过 G-Helper 的 ModeControl/GPUModeControl 中间层（避免 ASUS 语义污染）。
/// 协议依据 m1-findings.md 实测：Fan/Control、Setting/Control、Fan/Status、Setting/Status。
/// </summary>
public class MechrevoService
{
    readonly MechrevoHw _hw;
    readonly Func<bool> _readColorCalibrationOn;
    readonly Func<int> _readColorCalibrationMode;
    readonly Func<bool> _readHdrEnabled;
    readonly Func<bool, int, bool> _syncHighPerformancePowerMode;

    // ---- 模式枚举（UI 使用）：0=游戏 1=增强 2=办公 3=自定义 ----
    public const int ModeGaming = 0;
    public const int ModeTurbo = 1;
    public const int ModeOffice = 2;
    public const int ModeCustom = 3;

    // ---- 显卡模式枚举：保留旧版 0/1/2 配置值，新增独立自动态 3 ----
    public const int GpuIGpu = 0;
    public const int GpuStandard = 1;
    public const int GpuDgpu = 2;
    public const int GpuAuto = 3;
    /// <summary>
    /// 热切换确认轮询次数上限，与官方 IgpuOnlyOnCommand 的 count&gt;60 对齐（61 次 × 2 秒 ≈ 122 秒）。
    /// static 而非 const 只是为了让测试能把轮询压缩到 1 次——生产代码不要改写它。
    /// </summary>
    internal static int HotSwitchStatusPollLimit { get; set; } = IgpuOnlySemantics.PollLimit;
    internal const int HotSwitchStatusRetryEveryPolls = IgpuOnlySemantics.RetryEveryPolls;

    internal static bool ShouldRetryHotSwitchPoll(int poll) =>
        IgpuOnlySemantics.ShouldResend(poll);

    public event Action<int>? ModeChanged;        // 模式变化（G-Helper 枚举：0=游戏 1=增强 2=办公 3=自定义）
    public event Action<int>? GpuModeChanged;     // 显卡模式变化（0=核显 1=标准 2=直连 3=自动）
    public event Action? DataChanged;             // 遥测刷新

    readonly SemaphoreSlim _switchLock = new(1, 1);   // 切换串行化：防快速连续切换的确认交叉
    readonly SemaphoreSlim _settingLock = new(1, 1);
    readonly SemaphoreSlim _colorCalibrationLock = new(1, 1);
    readonly SemaphoreSlim _liquidCoolingLock = new(1, 1);
    // 三套独立 CTS：同槽后一次仍取消前一次（档位连点 T5），跨槽互不 previous.Cancel。
    // 滑条防抖的 SetCustomDetail 不得取消在飞的 SwitchCustomProfile。
    CancellationTokenSource? _modeSwitchCts;
    CancellationTokenSource? _customProfileSwitchCts;
    int _customProfileSwitchSequence;   // 每次 SwitchCustomProfile 开始 +1：重发前判断是否已被更新的切换取代
    CancellationTokenSource? _customDetailCts;
    CancellationTokenSource? _gpuSwitchCts;

    /// <summary>
    /// 正在进行的长确认操作。目前只有显卡热切换会走到这里：
    /// HotSwitchStatusPollLimit(61) × 2 秒 ≈ 122 秒，全程持有 _switchLock。
    /// 而 _switchLock 同时被性能模式、自定义档、自定义参数、重启切换共用，
    /// 过去用户在热切换期间点性能模式最长两分钟没有任何反应。
    /// </summary>
    CancellationTokenSource? _longRunningSwitchCts;

    /// <summary>正常切换序列拿锁的时间上限；超过就认为前面压着一个长操作。</summary>
    internal static readonly TimeSpan SwitchLockHandoverTimeout = TimeSpan.FromSeconds(3);

    /// <summary>请求长操作让位之后，再等锁的时间上限。</summary>
    internal static readonly TimeSpan SwitchLockAcquireTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 获取切换锁。等待超过 <see cref="SwitchLockHandoverTimeout"/> 时先请求在飞的长操作让位：
    /// 这些都是用户对同一子系统的互斥意图，让旧意图让位、由 UI 重新回读真实状态，
    /// 比让新意图排队两分钟更符合预期。长操作本来就支持被更新的请求取消。
    /// </summary>
    async Task<bool> AcquireSwitchLockAsync(string operation, CancellationToken cancellationToken)
    {
        if (await _switchLock.WaitAsync(SwitchLockHandoverTimeout, cancellationToken).ConfigureAwait(false))
            return true;

        CancellationTokenSource? longRunning = Volatile.Read(ref _longRunningSwitchCts);
        if (longRunning is not null)
        {
            Logger.WriteLine($"{operation}: a long-running switch is still confirming; requesting hand-over");
            try { longRunning.Cancel(); }
            catch (ObjectDisposedException) { /* 已经自然结束 */ }
        }

        if (await _switchLock.WaitAsync(SwitchLockAcquireTimeout, cancellationToken).ConfigureAwait(false))
            return true;

        Logger.WriteLine($"{operation} skipped: the switch lock is still held after the hand-over request");
        return false;
    }

    public MechrevoService(MechrevoHw hw)
        : this(hw,
            () => hw.ColorCalibrationSwitchSeen
                ? hw.ColorCalibrationSwitch
                : IsColorCalibrationOn(),
            GetColorCalibrationMode,
            IsAdvancedColorEnabled)
    {
    }

    internal MechrevoService(
        MechrevoHw hw,
        Func<bool> readColorCalibrationOn,
        Func<int> readColorCalibrationMode,
        Func<bool> readHdrEnabled,
        Func<bool, int, bool>? syncHighPerformancePowerMode = null)
    {
        _hw = hw;
        _readColorCalibrationOn = readColorCalibrationOn;
        _readColorCalibrationMode = readColorCalibrationMode;
        _readHdrEnabled = readHdrEnabled;
        _syncHighPerformancePowerMode = syncHighPerformancePowerMode ?? SyncHighPerformancePowerMode;
        _hw.ModeChanged += opMode => ModeChanged?.Invoke(OpToMode(opMode));
        _hw.GpuModeChanged += () => GpuModeChanged?.Invoke(CurrentGpuMode);
        _hw.DataChanged += () => DataChanged?.Invoke();
    }

    /// <summary>机械革命 opMode（0=办公 1=游戏 2=增强 3=自定义）→ 本服务枚举（0=游戏 1=增强 2=办公 3=自定义）。</summary>
    static int OpToMode(int op) => op is >= 0 and <= 3
        ? PowerModeMapping.ToVisualMode((ConsoleOperatingMode)op)
        : ModeGaming;

    /// <summary>将机械革命服务枚举转换为 SettingsForm 使用的视觉枚举。</summary>
    public static int ToVisualMode(int mode) => mode switch
    {
        ModeOffice => AsusACPI.PerformanceSilent,
        ModeGaming => AsusACPI.PerformanceBalanced,
        ModeTurbo => AsusACPI.PerformanceTurbo,
        ModeCustom => AsusACPI.PerformanceManual,
        _ => AsusACPI.PerformanceBalanced,
    };

    public int CurrentMode => OpToMode(_hw.OperatingMode);

    /// <summary>
    /// EC 只读传输工厂（T20 测试接缝）。生产默认打开 <c>\\.\ACPIDriver</c> 的只读通道；
    /// 测试注入假传输。功耗默认值只读，绝不写 EC。
    /// </summary>
    internal static Func<Probe.IEcReadTransport?> EcReadTransportFactory { get; set; } = DefaultEcReadTransport;

    static Probe.IEcReadTransport? DefaultEcReadTransport() =>
        Probe.AcpiDriverReadTransport.TryOpen(out Probe.AcpiDriverReadTransport? transport, out _) ? transport : null;

    /// <summary>
    /// 读该视觉模式对应的 PL/Tcc 默认值（T20）。Customize 没有 EC 默认组；读不到 EC；
    /// 任一字节缺失 —— 三种情况都返回 <c>Editable=false</c>（禁用该模式功耗编辑），绝不猜瓦数。
    /// </summary>
    public static PlDefaultsResult ReadPlDefaults(int visualMode)
    {
        if (PlDefaults.ModeForVisualMode(visualMode) is not { } mode)
            return PlDefaultsResult.Unavailable(null, "customize has no EC default set");

        Probe.IEcReadTransport? transport = EcReadTransportFactory();
        if (transport is null)
            return PlDefaultsResult.Unavailable(mode, "EC read transport unavailable");

        try
        {
            return PlDefaults.Read(mode, transport);
        }
        finally
        {
            (transport as IDisposable)?.Dispose();
        }
    }
    public int CurrentGpuMode => _hw.GpuMode;   // 0=核显 1=标准 2=独显直连 3=自动

    /// <summary>切换运行模式：发布 → 等待切换完成 → 回读确认 → 事件通知。</summary>
    public async Task<bool> SwitchMode(int mode)
    {
        var requestCts = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _modeSwitchCts, requestCts);
        previous?.Cancel();
        bool lockTaken = false;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            lockTaken = await AcquireSwitchLockAsync($"SwitchMode({mode})", requestCts.Token).ConfigureAwait(false);
            if (!lockTaken) return false;
            requestCts.Token.ThrowIfCancellationRequested();
            if (_hw is not { IsConnected: true }) return false;

            if (CurrentMode == mode)
            {
                MechrevoLite.Mode.ModeControl.SyncExternalModeStatic(mode);
                ModeChanged?.Invoke(mode);
                if (mode == ModeTurbo)
                    await _hw.RepairPathologicalTurboFanCurveAsync().ConfigureAwait(false);
                if (MechrevoLite.Mode.PerfModeService.TurboAutoOcEnabled() && ShouldApplyTurboGpuOverclockDefaultsOnModeSwitch(
                    mode, _hw.Capabilities.SilentTurboAvailability == FeatureAvailability.Supported))
                    await ApplyTurboGpuOverclockDefaults().ConfigureAwait(false);
                return true;
            }

            var action = mode switch
            {
                ModeOffice => "OPERATING_OFFICE_MODE",
                ModeGaming => "OPERATING_GAMING_MODE",
                ModeTurbo => "OPERATING_TURBO_MODE",
                ModeCustom => "OPERATING_CUSTOM_MODE",
                _ => "OPERATING_GAMING_MODE",
            };
            Logger.WriteLine($"MechrevoService.SwitchMode({mode}) -> {action}");
            int expectedOperatingMode = mode is >= 0 and <= 3
                ? (int)PowerModeMapping.FromVisualMode(mode)
                : (int)ConsoleOperatingMode.Gaming;
            _hw.MarkModeSwitchPending(expectedOperatingMode);
            // 载荷与 MechrevoHw.SetMode 共用一份构造：ProfileIndex 是 JSON 数字（不是 "0"），
            // 而且切模式必须紧跟一条 LCHWOC/Control 的运行标记
            // （官方 ModeSwitchCommand，CCUWinUI L55090-55146）。
            // 过去只在 SwitchCustomProfile 里发 IsCustomRun、从不发 IsNormalRun，
            // 于是从自定义模式切回普通模式时超频通道还留在「自定义运行」状态，
            // 硬件那边的超频参数不复位。
            var (fanPayload, overclockPayload) = MechrevoHw.BuildModeSwitchPayloads(
                action, expectedOperatingMode,
                mode == ModeCustom ? Math.Clamp(_hw.CustomProfileIndex, 0, MechrevoLite.Mode.FirmwareSlotPlanner.MaxSlotCount - 1) : 0,
                mode == ModeCustom);
            // 发布失败必须解除过期包过滤窗口，否则这 8 秒内一切真实模式上报
            // （含用户按厂商 Fn 热键、GCU 自己回滚）都会被静默丢弃，UI 会一直显示
            // 一个从未下发成功的模式。MechrevoHw.SetMode 早就这么做了，服务层这条路
            // 之前只在 catch 里记日志、既没解除也没走到那段逻辑。
            //
            // 注意只在**发布失败**时解除：确认失败（回读慢）时命令已经上路，
            // 这时解除反而会让在途的切换前旧包被当成新状态接受。
            try
            {
                await _hw.Publish(MqttTopics.FanControl, fanPayload).ConfigureAwait(false);
                await _hw.Publish(MqttTopics.LchwocControl, overclockPayload).ConfigureAwait(false);
            }
            catch
            {
                _hw.ClearModeSwitchPending();
                throw;
            }

            bool ok = await _hw.WaitForStateAsync(
                () => CurrentMode == mode,
                TimeSpan.FromMilliseconds(180),
                requestCts.Token).ConfigureAwait(false);

            if (!ok)
            {
                await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" }).ConfigureAwait(false);
                ok = await _hw.WaitForStateAsync(
                    () => CurrentMode == mode,
                    TimeSpan.FromMilliseconds(700),
                    requestCts.Token).ConfigureAwait(false);
            }

            if (!ok)
            {
                await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" }).ConfigureAwait(false);
                ok = await _hw.WaitForStateAsync(
                    () => CurrentMode == mode,
                    TimeSpan.FromMilliseconds(1000),
                    requestCts.Token).ConfigureAwait(false);
            }

            Logger.WriteLine($"SwitchMode confirmed={ok} expect={mode} actual={CurrentMode} elapsed={elapsed.ElapsedMilliseconds}ms");
            if (!ok) return false;

            if (mode == ModeCustom)
                _hw.RestoreDirectGpuOverclockProfile(_hw.CustomProfileIndex);
            else
                _hw.SuspendDirectGpuOverclock();

            MechrevoLite.Mode.ModeControl.SyncExternalModeStatic(mode);
            ModeChanged?.Invoke(mode);
            await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GET_FAN_SPEED_CURVE_SETTING" }).ConfigureAwait(false);
            if (mode == ModeTurbo)
                await _hw.RepairPathologicalTurboFanCurveAsync().ConfigureAwait(false);
            // 官方狂暴自动超频只在用户没自定义过狂暴的显卡超频时下发；
            // 自定义过的由 PerfModeService 的下发计划在切换之后覆盖。
            if (MechrevoLite.Mode.PerfModeService.TurboAutoOcEnabled() && ShouldApplyTurboGpuOverclockDefaultsOnModeSwitch(
                mode, _hw.Capabilities.SilentTurboAvailability == FeatureAvailability.Supported))
                await ApplyTurboGpuOverclockDefaults().ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            Logger.WriteLine($"SwitchMode({mode}) superseded after {elapsed.ElapsedMilliseconds}ms");
            return false;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("SwitchMode fail: " + ex.Message);
            return false;
        }
        finally
        {
            if (lockTaken) _switchLock.Release();
            Interlocked.CompareExchange(ref _modeSwitchCts, null, requestCts);
            requestCts.Dispose();
        }
    }

    /// <summary>
    /// 用户发起的自定义档切换：<see cref="SwitchCustomProfile"/> 未确认时重发一次（同一条命令，幂等）。
    /// GCU 刚处理完另一次模式切换时会丢掉这条命令（真机 2026-09-28：平衡 → 自定义，1.15 s 内模式
    /// 没变，之后也没再变，用户再点一次才切过去；另一次是已进自定义但仍停在旧档）。这次请求已被
    /// 更新的切换取代、或 GCU 已断开时不重发。
    /// </summary>
    public async Task<bool> SwitchCustomProfileWithResend(int index)
    {
        int before = Volatile.Read(ref _customProfileSwitchSequence);
        bool confirmed = await SwitchCustomProfile(index).ConfigureAwait(false);
        if (confirmed) return true;
        // 本次调用只会 +1；更多说明期间有更新的切换开始了，那次负责最终结果。
        if (Volatile.Read(ref _customProfileSwitchSequence) != before + 1) return false;
        if (_hw is not { IsConnected: true }) return false;
        Logger.WriteLine($"SwitchCustomProfile({index}) resend: GCU reports mode={_hw.OperatingMode} profile={_hw.CustomProfileIndex}");
        return await SwitchCustomProfile(index).ConfigureAwait(false);
    }

    /// <summary>切换自定义性能档（原版序列：OPERATING_CUSTOM_MODE + LCHWOC IsCustomRun + 刷新曲线设置 + 回读）。</summary>
    public async Task<bool> SwitchCustomProfile(int index)
    {
        Interlocked.Increment(ref _customProfileSwitchSequence);
        var requestCts = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _customProfileSwitchCts, requestCts);
        previous?.Cancel();
        bool lockTaken = false;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            lockTaken = await AcquireSwitchLockAsync($"SwitchCustomProfile({index})", requestCts.Token).ConfigureAwait(false);
            if (!lockTaken) return false;
            requestCts.Token.ThrowIfCancellationRequested();
            // 固件有 5 个自定义档（Mode4_Profile1..5；CustomId==9 的机型 3 个，由调用方按机型限制）。
            if (index < 0 || index >= MechrevoLite.Mode.FirmwareSlotPlanner.MaxSlotCount) return false;
            if (_hw is not { IsConnected: true })
            {
                Logger.WriteLine("SwitchCustomProfile: GCU 未连接");
                return false;
            }
            _hw.SuspendDirectGpuOverclock();
            _hw.MarkModeSwitchPending(3);
            // 先订再发：回读若在 Publish 返回后、WaitForStateAsync 订阅前到达会丢。
            Task<bool> modeWait = _hw.WaitForStateAsync(
                () => _hw.OperatingMode == 3 && _hw.CustomProfileIndex == index,
                TimeSpan.FromMilliseconds(250),
                requestCts.Token);
            // 与 SwitchMode 同理：发布失败要解除过期包过滤窗口。
            try
            {
                // ProfileIndex 是 JSON 数字，与官方 ModeSwitchCommand 一致。
                await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "OPERATING_CUSTOM_MODE", ["ProfileIndex"] = index });
                await _hw.Publish(MqttTopics.LchwocControl, new Dictionary<string, object> { ["IsCustomRun"] = true });
                await _hw.Publish(MqttTopics.LchwocControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });   // 请求超频通道状态（原版页面激活时的序列）
                await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GET_FAN_SPEED_CURVE_SETTING" });
            }
            catch
            {
                _hw.ClearModeSwitchPending();
                throw;
            }
            bool confirmed = await modeWait;
            requestCts.Token.ThrowIfCancellationRequested();
            if (!confirmed)
            {
                await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                confirmed = await _hw.WaitForStateAsync(
                    () => _hw.OperatingMode == 3 && _hw.CustomProfileIndex == index,
                    TimeSpan.FromMilliseconds(900),
                    requestCts.Token);
            }
            Logger.WriteLine($"SwitchCustomProfile({index}) confirmed={confirmed} actualMode={_hw.OperatingMode} actualProfile={_hw.CustomProfileIndex}");
            if (confirmed)
            {
                long initialFanStatus = _hw.FanStatusVersion;
                await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                await _hw.WaitForStateAsync(
                    () => _hw.CustomProfileIndex == index && _hw.FanStatusVersion > initialFanStatus,
                    TimeSpan.FromMilliseconds(900),
                    requestCts.Token);
                _hw.NotifyCustomModeChanged();
                _hw.RestoreDirectGpuOverclockProfile(index);
                AppConfig.Set("custom_last_profile", index);
                AppConfig.Flush();
                MechrevoLite.Mode.ModeControl.SyncExternalModeStatic(ModeCustom);
                ModeChanged?.Invoke(ModeCustom);
            }
            return confirmed;
        }
        catch (OperationCanceledException)
        {
            Logger.WriteLine($"SwitchCustomProfile({index}) superseded after {elapsed.ElapsedMilliseconds}ms");
            return false;
        }
        catch (Exception ex) { Logger.WriteLine("SwitchCustomProfile fail: " + ex.Message); return false; }
        finally
        {
            if (lockTaken) _switchLock.Release();
            Interlocked.CompareExchange(ref _customProfileSwitchCts, null, requestCts);
            requestCts.Dispose();
        }
    }

    /// <summary>设置当前自定义档参数（SET_OPERATING_MODE_DETAIL，服务端保存到当前档）。
    /// 实测：同包多字段时服务端只应用部分字段——必须逐字段单独发包（与原版 UI 每命令单字段一致）。</summary>
    public async Task<bool> SetCustomDetail(Dictionary<string, string> fields)
    {
        var requestCts = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _customDetailCts, requestCts);
        previous?.Cancel();
        bool lockTaken = false;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            lockTaken = await AcquireSwitchLockAsync("SetCustomDetail", requestCts.Token).ConfigureAwait(false);
            if (!lockTaken) return false;
            requestCts.Token.ThrowIfCancellationRequested();
            if (fields.Count == 0) return false;
            bool hasGpuOverclockFields = fields.Keys.Any(IsGpuOverclockField);
            if (hasGpuOverclockFields) _hw.EnsureDirectGpuOverclock();
            if (fields.Any(field => !ValidateCustomField(field.Key, field.Value)))
            {
                Logger.WriteLine("SetCustomDetail rejected outside device capability: " + string.Join(", ", fields.Select(kv => kv.Key + "=" + kv.Value)));
                return false;
            }
            KeyValuePair<string, string>[] entries = fields
                .OrderBy(field => IsGateField(field.Key) ? 0 : 1)
                .ToArray();

            KeyValuePair<string, string>[] gpuEntries = entries.Where(field => IsGpuOverclockField(field.Key)).ToArray();
            // A GCU echo is not enough evidence on its own. Only use the extended
            // GCU range when a real NVIDIA backend exists and explicitly reports
            // that it cannot represent the requested offset.
            bool gpuNeedsGcuExtendedRange = _hw.DirectGpuOverclockBackendPresent && gpuEntries.Any(field =>
                int.TryParse(field.Value, out int value) &&
                field.Key is "GpuCoreClockOffsetOC" or "GpuMemoryClockOffsetOC" &&
                !_hw.CanSetGpuOverclockThroughDriver(field.Key, value));
            bool routeGpuThroughGcu = gpuEntries.Length > 0 && gpuEntries.All(field =>
                int.TryParse(field.Value, out int value) && _hw.CanSetGpuOverclockThroughGcu(field.Key, value));
            if (gpuNeedsGcuExtendedRange && gpuEntries.Any(field =>
                int.TryParse(field.Value, out int value) &&
                field.Key is "GpuCoreClockOffsetOC" or "GpuMemoryClockOffsetOC" &&
                !_hw.CanSetGpuOverclockThroughGcu(field.Key, value)))
            {
                Logger.WriteLine("SetCustomDetail rejected GPU offset outside both driver and GCU ranges: " +
                    string.Join(", ", gpuEntries.Select(kv => kv.Key + "=" + kv.Value)));
                return false;
            }
            // Keep the independent driver + readback path as the default. GCU is the
            // only usable backend for values the driver reports outside its range.
            bool useGcuForGpuFields = routeGpuThroughGcu && gpuNeedsGcuExtendedRange;
            bool directGpuOverclockUsed = false;

            var gcuFields = new Dictionary<string, string>();
            if (routeGpuThroughGcu)
            {
                foreach (KeyValuePair<string, string> field in gpuEntries)
                    gcuFields[field.Key] = field.Value;
            }

            foreach (KeyValuePair<string, string> field in entries.Where(field => !IsGpuOverclockField(field.Key)))
                gcuFields[field.Key] = field.Value;

            if (gcuFields.Count > 0 && _hw is not { IsConnected: true })
            {
                if (gcuFields.Keys.Any(key => !IsGpuOverclockField(key))) return false;

                // GPU offsets can be written and read back through the NVIDIA driver.
                // Do not reject a physical write only because GCU profile persistence is offline.
                gcuFields.Clear();
            }
            KeyValuePair<string, string>[] gcuEntries = entries.Where(field => gcuFields.ContainsKey(field.Key)).ToArray();
            // GCU may echo an accepted profile value without changing the NVIDIA
            // P-state. GPU fields are therefore never confirmation evidence; the
            // final result must come from the independent driver readback below.
            KeyValuePair<string, string>[] gcuConfirmationEntries = gcuEntries
                .Where(field => !IsGpuOverclockField(field.Key))
                .ToArray();
            for (int i = 0; i < gcuEntries.Length; i++)
            {
                KeyValuePair<string, string> kv = gcuEntries[i];

                string wireKey = kv.Key;
                object wireValue = kv.Value;
                if (kv.Key == "CpuTccOffset" && int.TryParse(kv.Value, out int target))
                {
                    if (_hw.UsesAmdPowerFields) wireKey = "CpuAmdTccTarget";
                    else wireValue = _hw.TccTargetToRaw(target);
                }
                else if (kv.Key == "PL1" && _hw.UsesAmdPowerFields)
                    wireKey = "CpuAmdSPL";
                else if (kv.Key == "PL2" && _hw.UsesAmdPowerFields)
                    wireKey = "CpuAmdSPPT";
                // AMD 机型上 PL4 字段被 GCU 忽略；官方 AMD 分支只发 CpuAmdFPPT，且原样下发瓦数。
                else if (kv.Key == "PL4" && _hw.UsesAmdPowerFields)
                    wireKey = _hw.Pl4WireKey;
                else if (kv.Key == "PL4" && int.TryParse(kv.Value, out int pl4Watts))
                    wireValue = _hw.Pl4ToWire(pl4Watts);
                else if (kv.Key == "FanSwitchSpeed" && int.TryParse(kv.Value, out int switchSpeedMs))
                    wireValue = _hw.QuantiseFanSwitchSpeed(switchSpeedMs).ToString();
                await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "SET_OPERATING_MODE_DETAIL", [wireKey] = wireValue });
                if (i + 1 < gcuEntries.Length) await Task.Delay(120);
            }

            bool confirmed = gcuConfirmationEntries.Length == 0 ||
                gcuConfirmationEntries.All(field => GcuCustomFieldMatches(field.Key, field.Value));
            if (!confirmed)
                confirmed = await _hw.WaitForStateAsync(
                    () => gcuConfirmationEntries.All(field => GcuCustomFieldMatches(field.Key, field.Value)),
                    TimeSpan.FromMilliseconds(400),
                    requestCts.Token);
            if (!confirmed)
            {
                await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                confirmed = await _hw.WaitForStateAsync(
                    () => gcuConfirmationEntries.All(field => GcuCustomFieldMatches(field.Key, field.Value)),
                    TimeSpan.FromMilliseconds(2200),
                    requestCts.Token);
            }

            bool nonGpuFieldsConfirmed = gcuConfirmationEntries
                .Where(field => !IsGpuOverclockField(field.Key))
                .All(field => GcuCustomFieldMatches(field.Key, field.Value));
            if (gpuEntries.Length > 0 && nonGpuFieldsConfirmed)
            {
                if (useGcuForGpuFields)
                {
                    // GCU 回显「存储值」不代表已写进驱动（实测：核心 500 被 GCU 丢弃时
                    // echo 仍回 500）。唯一可信的确认是 NVAPI delta 读回（不需要提权）；
                    // GCU 应用有 1-4 秒异步延迟，所以给足 6 秒轮询。读回不可用就是
                    // 无法确认——宁可报失败，也不能拿 echo 冒充物理生效。
                    bool DriverConfirm() => gpuEntries.All(field =>
                        int.TryParse(field.Value, out int value) &&
                        _hw.DriverGpuOverclockFieldMatches(field.Key, value));

                    bool gcuConfirmed = DriverConfirm();
                    if (!gcuConfirmed)
                    {
                        await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                        gcuConfirmed = await _hw.WaitForStateAsync(
                            DriverConfirm,
                            TimeSpan.FromMilliseconds(6000),
                            requestCts.Token);
                    }
                    confirmed = gcuConfirmed;
                    Logger.WriteLine($"SetCustomDetail extended GPU offsets confirmed via driver readback: {confirmed}");
                    if (confirmed) _hw.MarkGcuGpuOverclockActive();
                }
                else
                {
                    directGpuOverclockUsed = await _hw.ApplyGpuOverclockAsync(gpuEntries).ConfigureAwait(false);
                    confirmed = directGpuOverclockUsed;
                }
            }
            else if (gpuEntries.Length > 0)
            {
                confirmed = false;
            }

            string source = gpuEntries.Length > 0
                ? (directGpuOverclockUsed ? "driver" : "driver-readback-failed")
                : "GCU";
            Logger.WriteLine($"SetCustomDetail source={source} confirmed={confirmed}: " +
                string.Join(", ", fields.Select(kv => kv.Key + "=" + kv.Value)));
            return confirmed;
        }
        catch (OperationCanceledException)
        {
            Logger.WriteLine($"SetCustomDetail superseded after {elapsed.ElapsedMilliseconds}ms");
            return false;
        }
        catch (Exception ex) { Logger.WriteLine("SetCustomDetail fail: " + ex.Message); return false; }
        finally
        {
            if (lockTaken) _switchLock.Release();
            Interlocked.CompareExchange(ref _customDetailCts, null, requestCts);
            requestCts.Dispose();
        }
    }

    bool ValidateCustomField(string key, string text)
    {
        if (!int.TryParse(text, out int value)) return false;
        static bool InRange(int value, int min, int max) => min >= 0 && max >= min && value >= min && value <= max;
        static bool InOffsetRange(int value, int min, int max) => max > min && value >= min && value <= max;
        return key switch
        {
            "PL1" => _hw.Pl1Adjustable && InRange(value, _hw.Pl1Minimum, _hw.Pl1Maximum),
            "PL2" => _hw.Pl2Adjustable && InRange(value, _hw.Pl2Minimum, _hw.Pl2Maximum),
            // PL4 在这条路上一律用面向用户的瓦数，折半只发生在发布前的线上换算处。
            "PL4" => _hw.Pl4Adjustable && InRange(value, _hw.Pl4Minimum, _hw.Pl4Maximum),
            // 风扇转换灵敏度。数值这里不要求"当前已启用"：同一批提交可以先开开关再写值，
            // 顺序由 IsGateField 保证。
            "FanSwitchSpeedEnabled" => _hw.SupportsFanSwitchSpeed && value is 0 or 1,
            "FanSwitchSpeed" => _hw.SupportsFanSwitchSpeed &&
                InRange(value, _hw.FanSwitchSpeedMinimum, _hw.FanSwitchSpeedMaximum),
            "CpuTccOffset" => _hw.TccAdjustable && InRange(value, _hw.TccMinimum, _hw.TccMaximum),
            "GpuConfigurableTGPTarget" => _hw.GpuTgpAdjustable && InRange(value, _hw.GpuTgpMinimum, _hw.GpuTgpMaximum),
            "GpuDynamicBoost" => _hw.GpuDynamicBoostAdjustable && InRange(value, _hw.GpuDbMinimum, _hw.GpuDbMaximum),
            "GpuCoreClockOffsetOC" => _hw.SupportsGpuOverclock && _hw.GpuCoreOffsetAdjustable && InOffsetRange(value, _hw.GpuCoreOffsetUserMinimum, _hw.GpuCoreOffsetUserMaximum),
            "GpuMemoryClockOffsetOC" => _hw.SupportsGpuOverclock && _hw.GpuMemoryOffsetAdjustable && InOffsetRange(value, _hw.GpuMemoryOffsetUserMinimum, _hw.GpuMemoryOffsetUserMaximum),
            "CpuTccOffsetSwitch" => _hw.TccAdjustable && value is 0 or 1,
            "GpuDynamicBoostSwitch" => _hw.GpuDynamicBoostAdjustable && value is 0 or 1,
            "OverClockingSwitch" => _hw.SupportsGpuOverclock && value is 0 or 1,
            _ => false,
        };
    }

    bool CustomFieldMatches(string key, string text)
    {
        if (!int.TryParse(text, out int value)) return false;
        return key switch
        {
            "PL1" => _hw.Pl1 == value,
            "PL2" => _hw.Pl2 == value,
            // 半瓦机型上奇数瓦会被折半截断，能回读到的只有量化后的值。
            "PL4" => _hw.Pl4 == _hw.Pl4Effective(value),
            "FanSwitchSpeedEnabled" => _hw.FanSwitchSpeedEnabled == (value == 1),
            "FanSwitchSpeed" => _hw.FanSwitchSpeed == _hw.QuantiseFanSwitchSpeed(value),
            "CpuTccOffset" => _hw.TccTarget == value,
            "GpuConfigurableTGPTarget" => _hw.GpuTgp == value,
            "GpuDynamicBoost" => _hw.GpuDb == value,
            "GpuCoreClockOffsetOC" => _hw.EffectiveGpuCoreClockOffset == value,
            "GpuMemoryClockOffsetOC" => _hw.EffectiveGpuMemoryClockOffset == value,
            "CpuTccOffsetSwitch" => _hw.TccSwitch == (value == 1),
            "GpuDynamicBoostSwitch" => _hw.GpuDbSwitch == (value == 1),
            "OverClockingSwitch" => _hw.GpuOverclockEnabled == (value == 1),
            _ => false,
        };
    }

    bool GcuCustomFieldMatches(string key, string text)
    {
        if (!int.TryParse(text, out int value)) return false;
        return key switch
        {
            "GpuCoreClockOffsetOC" => _hw.GpuCoreClockOffset == value,
            "GpuMemoryClockOffsetOC" => _hw.GpuMemClockOffset == value,
            "OverClockingSwitch" => _hw.OcSwitch == (value == 1),
            _ => CustomFieldMatches(key, text),
        };
    }

    static bool IsGpuOverclockField(string key) => key is
        "GpuCoreClockOffsetOC" or "GpuMemoryClockOffsetOC" or "OverClockingSwitch";

    /// <summary>
    /// Turbo with no silent/extreme split is the vendor 狂暴 button: apply official GPU OC.
    /// When sub-modes exist, wait for <see cref="SwitchTurboSubMode"/> so 静音狂暴 does not get +105/+500.
    /// </summary>
    internal static bool ShouldApplyTurboGpuOverclockDefaultsOnModeSwitch(int mode, bool turboSubModeSupported) =>
        mode == ModeTurbo && !turboSubModeSupported;

    /// <summary>Extreme turbo (silent=false) gets the vendor auto-OC; silent turbo does not.</summary>
    internal static bool ShouldApplyTurboGpuOverclockDefaultsOnSubMode(bool silent) => !silent;

    /// <summary>
    /// Official HomePage turbo GPU OC: <c>SET_OPERATING_MODE_DETAIL</c> one field per packet
    /// (FEATURES.md). NvAPI is suspended outside custom mode, so this path must go through GCU.
    /// </summary>
    internal static readonly (string Key, string Value)[] TurboGpuOverclockDefaultFields =
    {
        ("OverClockingSwitch", "1"),
        ("GpuCoreClockOffsetOC", MechrevoHw.TurboGpuCoreOffsetMhz.ToString()),
        ("GpuMemoryClockOffsetOC", MechrevoHw.TurboGpuMemoryOffsetMhz.ToString()),
    };

    internal async Task<bool> ApplyTurboGpuOverclockDefaults()
    {
        if (_hw is not { IsConnected: true }) return false;
        if (!_hw.SupportsGpuOverclock && !_hw.Capabilities.OverclockSettings) return false;
        try
        {
            for (int i = 0; i < TurboGpuOverclockDefaultFields.Length; i++)
            {
                (string key, string value) = TurboGpuOverclockDefaultFields[i];
                await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object>
                {
                    ["Action"] = "SET_OPERATING_MODE_DETAIL",
                    [key] = value,
                }).ConfigureAwait(false);
                if (i + 1 < TurboGpuOverclockDefaultFields.Length)
                    await Task.Delay(120).ConfigureAwait(false);
            }
            Logger.WriteLine(
                $"ApplyTurboGpuOverclockDefaults sent core={MechrevoHw.TurboGpuCoreOffsetMhz} memory={MechrevoHw.TurboGpuMemoryOffsetMhz}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("ApplyTurboGpuOverclockDefaults fail: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 狂暴模式「显卡自动超频」开关（官方主页 Turbo GPU OC 开关）：开 = 下发官方 +105/+500，
    /// 关 = 偏移归零并关掉超频总闸。
    /// </summary>
    internal Task<bool> ApplyTurboGpuOverclockPreference(bool enabled) => enabled
        ? ApplyGcuGpuOverclock(true, MechrevoHw.TurboGpuCoreOffsetMhz, MechrevoHw.TurboGpuMemoryOffsetMhz)
        : ApplyGcuGpuOverclock(false, 0, 0);

    /// <summary>
    /// 内置模式的显卡超频：只走 GCU（<c>SET_OPERATING_MODE_DETAIL</c>，逐字段一包）——
    /// 非自定义模式下直连 NVAPI 被挂起，GCU 会把偏移写进当前模式的档位并自己应用
    /// （官方狂暴自动超频就是这条路）。关 = 偏移归零并关掉总闸。
    /// 有 NVIDIA 驱动后端时以驱动回读为准（GCU 回显不代表 P-state 已变）；没有驱动后端时
    /// 只能以 GCU 状态帧里的偏移回显为准。
    /// </summary>
    internal async Task<bool> ApplyGcuGpuOverclock(bool enabled, int core, int memory)
    {
        if (_hw is not { IsConnected: true }) return false;
        if (!_hw.SupportsGpuOverclock && !_hw.Capabilities.OverclockSettings) return false;
        if (!enabled) { core = 0; memory = 0; }
        try
        {
            (string Key, string Value)[] fields = enabled
                ? new[]
                {
                    ("OverClockingSwitch", "1"),
                    ("GpuCoreClockOffsetOC", core.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("GpuMemoryClockOffsetOC", memory.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                }
                : new[]
                {
                    ("GpuCoreClockOffsetOC", "0"),
                    ("GpuMemoryClockOffsetOC", "0"),
                    ("OverClockingSwitch", "0"),
                };
            for (int i = 0; i < fields.Length; i++)
            {
                await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object>
                {
                    ["Action"] = "SET_OPERATING_MODE_DETAIL",
                    [fields[i].Key] = fields[i].Value,
                }).ConfigureAwait(false);
                if (i + 1 < fields.Length) await Task.Delay(120).ConfigureAwait(false);
            }

            bool Matches() => _hw.DirectGpuOverclockBackendPresent
                ? _hw.DriverGpuOverclockFieldMatches("GpuCoreClockOffsetOC", core)
                  && _hw.DriverGpuOverclockFieldMatches("GpuMemoryClockOffsetOC", memory)
                : _hw.GpuCoreClockOffset == core && _hw.GpuMemClockOffset == memory;

            if (Matches()) return true;
            await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" }).ConfigureAwait(false);
            bool confirmed = await _hw.WaitForStateAsync(Matches, TimeSpan.FromMilliseconds(6000)).ConfigureAwait(false);
            Logger.WriteLine($"ApplyGcuGpuOverclock({enabled}) core={core} memory={memory} confirmed={confirmed}");
            return confirmed;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("ApplyGcuGpuOverclock fail: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 总闸类字段：必须在同批的数值字段之前下发，否则服务端会丢掉被关着的那一项的值。
    /// 除了 *Switch 后缀，还有 FanSwitchSpeedEnabled 这种以 Enabled 结尾的写法。
    /// </summary>
    static bool IsGateField(string key) =>
        key.EndsWith("Switch", StringComparison.Ordinal) ||
        key.EndsWith("Enabled", StringComparison.Ordinal);

    public bool RestoreCurrentDirectGpuOverclock() =>
        _hw.OperatingMode == 3 && _hw.RestoreDirectGpuOverclockProfile(_hw.CustomProfileIndex);

    /// <summary>风扇独立控制开关（原版 SET_FAN_CONTROL_RESPECTIVE）：false=双风扇共用 GPU 曲线。</summary>
    public async Task<bool> SwitchFanRespective(bool on)
    {
        try
        {
            if (_hw is not { IsConnected: true } || !_hw.SupportsFanRespective) return false;
            await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object>
            {
                ["Action"] = "SET_FAN_CONTROL_RESPECTIVE",
                ["Name"] = string.IsNullOrEmpty(_hw.TableName) ? _hw.CurveName : _hw.TableName,
                ["FanControlRespective"] = on,
            });
            Logger.WriteLine($"SwitchFanRespective({on}) 已发送");
            bool confirmed = await _hw.WaitForStateAsync(
                () => _hw.FanRespective == on,
                TimeSpan.FromMilliseconds(180));
            // 该标志只出现在 Fan/Table 帧里，而 GCU 在 SET 之后既不推表、GETSTATUS 返回的
            // Fan/Status 也根本没有这个字段（真机实测）——过去在这里补查 GETSTATUS，于是
            // 每次都被判成未确认、开关弹回。只有 GET_FAN_SPEED_CURVE_SETTING 会换回一帧
            // 带 FanControlRespective 的 Fan/Table。
            for (int attempt = 0; !confirmed && attempt < 3; attempt++)
            {
                await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GET_FAN_SPEED_CURVE_SETTING" });
                confirmed = await _hw.WaitForStateAsync(
                    () => _hw.FanRespective == on,
                    TimeSpan.FromMilliseconds(attempt == 0 ? 600 : 800));
            }
            Logger.WriteLine($"SwitchFanRespective confirmed={confirmed} expected={on} actual={_hw.FanRespective}");
            return confirmed;
        }
        catch (Exception ex) { Logger.WriteLine("SwitchFanRespective fail: " + ex.Message); return false; }
    }

    /// <summary>
    /// 屏幕校色（原版 DisplayViewModel 协议）：
    /// mode 1-4 = 默认/sRGB/P3/AdobeRGB 色域（直接发送对应 COLOR_CALIBRATION_ON_*）；
    /// mode 0 = 关闭（COLOR_CALIBRATION_OFF，FileName 用当前档映射名）。
    /// </summary>
    public async Task<bool> SetColorCalibration(int mode)
    {
        if (mode is < 0 or > 4) return false;
        await _colorCalibrationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_hw is not { IsConnected: true } || !_hw.SupportsColorCalibration) return false;
            bool expectedOn = mode != 0;
            int currentMode = ReadColorCalibrationMode();
            bool currentOn = ReadColorCalibrationOn();
            ColorCalibrationDecision decision = ColorCalibrationSwitchPolicy.Decide(
                connectedAndSupported: true,
                expectedOn,
                hdrEnabled: expectedOn && _readHdrEnabled(),
                currentOn,
                currentMode,
                mode);
            if (decision == ColorCalibrationDecision.HdrBlocked)
            {
                Logger.WriteLine($"SetColorCalibration(mode {mode}) blocked: HDR is enabled");
                return false;
            }
            if (decision == ColorCalibrationDecision.AlreadyApplied)
            {
                Logger.WriteLine($"SetColorCalibration(mode {mode}) already applied");
                return true;
            }

            long initialCalibrationStatusVersion = _hw.ColorCalibrationStatusVersion;
            if (expectedOn)
            {
                string action = ColorCalibrationAction(mode);
                // Official CCU sends the selected profile directly. Sending a separate
                // COLOR_CALIBRATION_ON first races the profile command and clears the UI switch.
                await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = action });
                Logger.WriteLine($"SetColorCalibration(mode {mode}) -> {action}");
            }
            else
            {
                string file = ColorCalibrationFileName(currentMode);
                await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "COLOR_CALIBRATION_OFF", ["FileName"] = file });
                await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "DISPLAY_FEATURE_STATUS_ON" });
                Logger.WriteLine($"SetColorCalibration(off) FileName={file}");
            }

            // 命令已下发，注册表随时会变：让回读缓存立即失效，
            // 否则确认逻辑可能在 TTL 内一直读到写入前的旧值。
            InvalidateColorCalibrationCache();
            bool confirmed = await ConfirmColorCalibrationAsync(mode, initialCalibrationStatusVersion).ConfigureAwait(false);
            Logger.WriteLine($"SetColorCalibration confirmed={confirmed} expectedMode={mode} actualMode={ReadColorCalibrationMode()} switch={ReadColorCalibrationOn()} result={_hw.ColorCalibrationResult}");
            return confirmed;
        }
        catch (Exception ex) { Logger.WriteLine("SetColorCalibration fail: " + ex.Message); return false; }
        finally { _colorCalibrationLock.Release(); }
    }

    async Task<bool> ConfirmColorCalibrationAsync(int mode, long initialCalibrationStatusVersion)
    {
        bool expectedOn = mode != 0;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        int poll = 0;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(4))
        {
            // 确认路径必须看到真实的注册表状态：这里每轮都让回读缓存失效。
            // 缓存的存在只是为了保护 MQTT 接收线程上的高频解析，不该影响确认的准确性。
            InvalidateColorCalibrationCache();
            bool actualOn = ReadColorCalibrationOn();
            int actualMode = ReadColorCalibrationMode();
            // 开方向只认档位回读：真机实测（2026-09-11）GCU 的开关字段在本机恒为 False——
            // 切 sRGB 后屏幕确实变色、档位也回读到 2，开关却还是 False。要求两者同时成立
            // 会让每次成功的切换都判失败：界面先闪"切换失败"再被回显纠正回"当前：sRGB"。
            // 关方向没有档位可用，仍然看开关。
            bool stateMatches = ColorCalibrationSwitchPolicy.StateMatches(expectedOn, actualOn, actualMode, mode);
            bool freshCalibrationStatus = _hw.ColorCalibrationStatusVersion > initialCalibrationStatusVersion;
            int result = _hw.ColorCalibrationResult;

            if (freshCalibrationStatus && stateMatches && result is -1 or 0) return true;

            if (freshCalibrationStatus && result is 2 or 3 or 4 or 5 or 7) return false;

            await Task.Delay(120).ConfigureAwait(false);
            if (++poll % 4 == 0)
                await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" }).ConfigureAwait(false);
        }
        return false;
    }

    internal static bool? ResolveColorCalibrationSwitch(bool? registryState, bool? runtimeState) =>
        runtimeState ?? registryState;

    bool ReadColorCalibrationOn() => _readColorCalibrationOn();

    int ReadColorCalibrationMode() => _hw.ColorCalibrationModeSeen
        ? _hw.ColorCalibrationMode
        : _readColorCalibrationMode();

    /// <summary>回读当前校色档位（注册表，原版同款来源）：1=默认 2=sRGB 3=P3 4=AdobeRGB，0=未知。</summary>
    public static int GetColorCalibrationMode()
    {
        try
        {
            object? v = ReadCalibrationRegistryValue("ColorCalibration",
                @"SOFTWARE\OEM\GamingCenter2\MySetting\DisplayFeatures",
                @"SOFTWARE\OEM\GamingCenter2\DisplayFeatures",
                @"SOFTWARE\OEM\GamingCenter\MySetting\DisplayFeatures",
                @"SOFTWARE\OEM\ControlCenter\MySetting\DisplayFeatures");
            int mode = ParseColorCalibrationMode(v);
            return mode is >= 1 and <= 4 ? mode : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 校色开关注册表回读的缓存有效期。
    ///
    /// 为什么需要缓存：这个值会在 MechrevoHw 解析 Setting/Status 的过程中被读取，
    /// 而 Setting/Status 是周期推送主题，解析跑在 MQTT 接收线程上。每帧都做一次
    /// （未见校色字段时是两次）阻塞式注册表 I/O 会拖慢整条接收链路；更糟的是注册表
    /// 访问一旦抛异常，会连带把整帧 Setting/Status 的解析废掉。
    /// 该值只在用户切换校色档位时变化，短 TTL 足够。
    /// </summary>
    /// 500ms 是刻意选的：Setting/Status 每秒最多推几帧，这个窗口足以把重复的注册表 I/O
    /// 折叠掉；同时它远小于校色确认循环的 4 秒预算，不会把「写入已生效」拖成「确认失败」。
    /// 确认循环本身还会每轮显式失效缓存，见 ConfirmColorCalibrationAsync。
    internal static readonly TimeSpan ColorCalibrationCacheTtl = TimeSpan.FromMilliseconds(500);

    static readonly object ColorCalibrationCacheLock = new();
    static bool? colorCalibrationCache;
    static long colorCalibrationCacheUntil;

    /// <summary>回读校色开关（注册表，带短 TTL 缓存）：UninstallCalibration 0=开 1=关。</summary>
    internal static bool? TryReadColorCalibrationOn()
    {
        lock (ColorCalibrationCacheLock)
        {
            if (Environment.TickCount64 < colorCalibrationCacheUntil) return colorCalibrationCache;
        }

        bool? value = ReadColorCalibrationOnFromRegistry();

        lock (ColorCalibrationCacheLock)
        {
            colorCalibrationCache = value;
            colorCalibrationCacheUntil =
                Environment.TickCount64 + (long)ColorCalibrationCacheTtl.TotalMilliseconds;
        }
        return value;
    }

    /// <summary>写入校色设置之后调用，保证下一次回读拿到的是新值而不是缓存。</summary>
    internal static void InvalidateColorCalibrationCache()
    {
        lock (ColorCalibrationCacheLock) colorCalibrationCacheUntil = 0;
    }

    static bool? ReadColorCalibrationOnFromRegistry()
    {
        try
        {
            object? v = ReadCalibrationRegistryValue("UninstallCalibration",
                @"SOFTWARE\OEM\GamingCenter2",
                @"SOFTWARE\OEM\GamingCenter",
                @"SOFTWARE\OEM\ControlCenter");
            if (v is not null && int.TryParse(v.ToString(), out int uninstall)) return uninstall == 0;
            v = ReadCalibrationRegistryValue("ColorCalibrationSwitch",
                @"SOFTWARE\OEM\GamingCenter2\MySetting\DisplayFeatures",
                @"SOFTWARE\OEM\GamingCenter\MySetting\DisplayFeatures",
                @"SOFTWARE\OEM\ControlCenter\MySetting\DisplayFeatures");
            if (v is null) return null;
            string state = v?.ToString() ?? "";
            if (state.Contains("OFF", StringComparison.OrdinalIgnoreCase)) return false;
            if (state.Contains("ON", StringComparison.OrdinalIgnoreCase)) return true;
            if (int.TryParse(state, out int numeric)) return numeric != 0;
            return null;
        }
        catch { return null; }
    }

    public static bool IsColorCalibrationOn() => TryReadColorCalibrationOn() ?? false;

    static object? ReadCalibrationRegistryValue(string valueName, params string[] subKeys)
    {
        foreach (Microsoft.Win32.RegistryView view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    Microsoft.Win32.RegistryHive.LocalMachine, view);
                foreach (string subKey in subKeys)
                {
                    using var key = baseKey.OpenSubKey(subKey);
                    object? value = key?.GetValue(valueName);
                    if (value is not null) return value;
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Color calibration registry read failed: value=" + valueName + " view=" + view + " " + ex.GetType().Name + " " + ex.Message);
            }
        }
        return null;
    }

    static int ParseColorCalibrationMode(object? value)
    {
        if (value is null) return 0;
        if (int.TryParse(value.ToString(), out int number)) return number;
        string text = value.ToString() ?? "";
        if (text.Contains("sRGB", StringComparison.OrdinalIgnoreCase)) return 2;
        if (text.Contains("P3", StringComparison.OrdinalIgnoreCase)) return 3;
        if (text.Contains("Adobe", StringComparison.OrdinalIgnoreCase)) return 4;
        return text.Contains("Default", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    public enum AdvancedColorState { Off, Hdr, Acm }

    /// <summary>读取活动内置屏的实时高级色彩状态；不使用会滞后的厂商注册表状态。</summary>
    public static AdvancedColorState GetAdvancedColorState()
    {
        try
        {
            bool hdr = Display.ScreenCCD.GetHDRStatus(out bool acm, log: false);
            return ResolveAdvancedColorState(hdr, acm);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Advanced color state query failed: " + ex.Message);
            return AdvancedColorState.Off;
        }
    }

    public static bool IsHdrEnabled() => GetAdvancedColorState() == AdvancedColorState.Hdr;

    public static bool IsAdvancedColorEnabled() => GetAdvancedColorState() != AdvancedColorState.Off;

    internal static AdvancedColorState ResolveAdvancedColorState(bool hdr, bool acm) =>
        hdr ? AdvancedColorState.Hdr : acm ? AdvancedColorState.Acm : AdvancedColorState.Off;

    static string ColorCalibrationAction(int mode) => mode switch
    {
        1 => "COLOR_CALIBRATION_ON_DEFAULT",
        2 => "COLOR_CALIBRATION_ON_SRGB",
        3 => "COLOR_CALIBRATION_ON_P3",
        4 => "COLOR_CALIBRATION_ON_ADOBERGB",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    static string ColorCalibrationFileName(int mode) => mode switch
    {
        2 => "sRGB",
        3 => "P3",
        4 => "AdobeRGB",
        _ => "Default",
    };

    /// <summary>设置当前自定义档名称（服务端 OSD 显示名，原版 SET_CUSTOM_PROFILE_OSD_STRING）。</summary>
    internal const int CustomProfileNameMaxLength = 12;

    public async Task<bool> SetCustomProfileName(string name)
    {
        try
        {
            if (_hw is not { IsConnected: true }) return false;
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.Length > CustomProfileNameMaxLength) return false;
            // 名字作用于「当前运行的自定义档」：不在自定义模式时服务端没有目标档可改。
            if (CurrentMode != ModeCustom) return false;
            await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "SET_CUSTOM_PROFILE_OSD_STRING", ["ProfileName"] = name });
            // 回读：Fan/Status 的 ProfileName。
            bool confirmed = await _hw.WaitForStateAsync(() => _hw.ProfileName == name, TimeSpan.FromMilliseconds(300));
            if (!confirmed)
            {
                await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                confirmed = await _hw.WaitForStateAsync(() => _hw.ProfileName == name, TimeSpan.FromMilliseconds(1500));
            }
            Logger.WriteLine($"SetCustomProfileName({name}) confirmed={confirmed}");
            return confirmed;
        }
        catch (Exception ex) { Logger.WriteLine("SetCustomProfileName fail: " + ex.Message); return false; }
    }

    internal static Dictionary<string, object> CreateGpuModePayload(int mode) => mode switch
    {
        GpuIGpu => new() { ["Action"] = "IGPU_ONLY_CONNECT_RB_ON", ["SetToWMIEC"] = "OK" },
        GpuStandard => new() { ["Action"] = "IGPU_ONLY_CONNECT_RB_OFF", ["SetToWMIEC"] = "OK" },
        GpuDgpu => new() { ["Action"] = "DGPU_DIRECT_CONNECT_TOGGLE_ON" },
        GpuAuto => new() { ["Action"] = "IGPU_ONLY_CONNECT_RB_AUTO" },
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>自动档（RB_AUTO，服务按 AC/DC 自己切）：只在 50 系热切换机型上有，成功按电源看设备在位。</summary>
    public Task<bool> SwitchAutomaticGpuMode(bool plugged) =>
        SwitchGpuMode(GpuAuto, pluggedForAuto: plugged);

    /// <summary>测试接缝：覆盖重启切换的路由序列，用来锁定「空路由必须 fail closed」。生产代码从不设置它。</summary>
    internal static Func<int, bool, IReadOnlyList<Dictionary<string, object>>>? GpuRestartRouteOverride { get; set; }

    /// <summary>请求厂商服务在重启前应用 MUX 目标，不等待热切换状态完成。</summary>
    public async Task<bool> RequestGpuModeRestartAsync(int mode) =>
        await RequestGpuModeRestartOutcomeAsync(mode).ConfigureAwait(false) == GpuRestartRequestOutcome.Requested;

    /// <summary>
    /// 与 <see cref="RequestGpuModeRestartAsync"/> 同一条实现，只是返回可区分的结果。
    /// 分开的理由：UI 必须能把「该方向没有可用指令」与其它失败区分开，否则只能统一报
    /// 「发送失败」——那正是这次 40 系空路由重启的含糊来源。
    /// </summary>
    internal async Task<GpuRestartRequestOutcome> RequestGpuModeRestartOutcomeAsync(int mode)
    {
        var requestCts = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _gpuSwitchCts, requestCts);
        previous?.Cancel();
        bool lockTaken = false;
        try
        {
            if (_hw is not { IsConnected: true } || mode is < GpuIGpu or > GpuAuto)
                return GpuRestartRequestOutcome.Failed;
            if (!_hw.CanSwitchGpuMode(mode))
                return GpuRestartRequestOutcome.Unsupported;

            lockTaken = await AcquireSwitchLockAsync(
                $"RequestGpuModeRestartAsync({mode})", requestCts.Token).ConfigureAwait(false);
            if (!lockTaken) return GpuRestartRequestOutcome.Failed;

            return await PublishGpuRestartAsync(
                mode, BuildGpuRestartRoute(mode), requestCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Logger.WriteLine($"GPU restart route superseded: target={mode}");
            return GpuRestartRequestOutcome.Failed;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPU restart route failed: target={mode} error={ex.Message}");
            return GpuRestartRequestOutcome.Failed;
        }
        finally
        {
            if (lockTaken) _switchLock.Release();
            Interlocked.CompareExchange(ref _gpuSwitchCts, null, requestCts);
            requestCts.Dispose();
        }
    }

    /// <summary>
    /// 硬件已经处在 <paramref name="currentMode"/>，但 NVRAM 里还挂着另一个重启目标（切换后没有重启）：
    /// 把目标改回当前模式即可，不重启——下次开机也不会切到那个被放弃的目标。
    /// </summary>
    internal async Task<bool> CancelPendingGpuRestartAsync(int currentMode)
    {
        var requestCts = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _gpuSwitchCts, requestCts);
        previous?.Cancel();
        bool lockTaken = false;
        try
        {
            if (_hw is not { IsConnected: true } || !_hw.CanSwitchGpuMode(currentMode)) return false;
            GpuRestartRoute route = BuildGpuRestartRoute(currentMode);
            if (route.Payloads.Count == 0) return false;
            lockTaken = await AcquireSwitchLockAsync($"CancelPendingGpuRestart({currentMode})", requestCts.Token).ConfigureAwait(false);
            if (!lockTaken) return false;
            foreach (Dictionary<string, object> payload in route.Payloads)
            {
                requestCts.Token.ThrowIfCancellationRequested();
                await _hw.Publish(MqttTopics.SettingControl, payload).ConfigureAwait(false);
            }
            Logger.WriteLine($"Pending GPU restart target reverted to the current mode {currentMode} without a restart " +
                $"[{string.Join(" -> ", route.Payloads.Select(ActionOf))}]");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("CancelPendingGpuRestart failed: " + ex.Message);
            return false;
        }
        finally
        {
            if (lockTaken) _switchLock.Release();
            Interlocked.CompareExchange(ref _gpuSwitchCts, null, requestCts);
            requestCts.Dispose();
        }
    }

    /// <summary>构建 GPU 重启路由：手动与自动两条重启路径共用同一序列来源（测试接缝只覆盖载荷序列）。</summary>
    GpuRestartRoute BuildGpuRestartRoute(int mode) =>
        GpuRestartRouteOverride is { } factory
            ? new GpuRestartRoute(factory(mode, _hw.SupportsDgpuDirect), _hw.GpuServiceRestartAvailable)
            : CreateGpuRestartRoute(mode, GpuRouteContext.From(_hw));

    /// <summary>
    /// GPU 重启的唯一发布出口。Fail closed：空路由意味着没有任何模式会被写进 NVRAM，
    /// 此时发布 DGPU_DIRECT_CONNECT_RESTART 只会让 GCU 把机器白重启一次、模式却没变
    /// （真机证据：`GPU restart route payloads [] sent for target=0/1` 后照样重启）。
    /// 守卫放在发布点，任何调用方都绕不过去。调用方必须已持有 _switchLock。
    /// <para>服务档位没有可用的 RESTART（厂商服务 / 30 系、40 两模档的服务）时，载荷照发，
    /// 返回 <see cref="GpuRestartRequestOutcome.RequiresAppRestart"/>，由界面在用户确认下重启 Windows。</para>
    /// </summary>
    async Task<GpuRestartRequestOutcome> PublishGpuRestartAsync(
        int mode,
        GpuRestartRoute route,
        CancellationToken token)
    {
        if (route.Payloads.Count == 0)
        {
            Logger.WriteLine(
                $"GPU restart route is empty for target={mode} " +
                $"(generation={_hw.DgpuGeneration}, tier={_hw.ServiceTier}, supportsDgpuDirect={_hw.SupportsDgpuDirect}, supportsIgpuOnly={_hw.SupportsIgpuOnly}); " +
                "refusing to publish DGPU_DIRECT_CONNECT_RESTART so the machine is not rebooted for nothing.");
            return GpuRestartRequestOutcome.Unsupported;
        }

        List<string> sentActions = new(route.Payloads.Count);
        foreach (Dictionary<string, object> payload in route.Payloads)
        {
            token.ThrowIfCancellationRequested();
            sentActions.Add(payload["Action"].ToString() ?? "?");
            await _hw.Publish(MqttTopics.SettingControl, payload).ConfigureAwait(false);
        }
        Logger.WriteLine($"GPU restart route applied for target={mode}: {sentActions.Count} payload(s) [{string.Join(" -> ", sentActions)}] serviceRestart={route.ServiceRestart}");

        await Task.Delay(GpuRouteCommandLayer.RestartDelayMilliseconds, token).ConfigureAwait(false);
        AppConfig.Flush();
        token.ThrowIfCancellationRequested();
        if (!route.ServiceRestart)
        {
            Logger.WriteLine($"GPU restart route for target={mode}: service tier {_hw.ServiceTier} has no usable RESTART; the app restarts Windows.");
            return GpuRestartRequestOutcome.RequiresAppRestart;
        }
        await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object>
        {
            ["Action"] = DisplayRouteMatrix.Restart,
        }).ConfigureAwait(false);
        Logger.WriteLine($"Requested GCU GPU restart route for target={mode}");
        return GpuRestartRequestOutcome.Requested;
    }

    /// <summary>
    /// MUX 目标路由（§5）。只有有 MUX 的 30/40/50 才有：
    /// <list type="bullet">
    /// <item>直连：50 系 + 我方 1.2 服务 = 官方三连（TOGGLE_ON → RB_OFF[SetToWMIEC] → TOGGLE_ON，CCUWinUI GpuSettingPage.cs:1534-1573）；
    ///   30/40 系与厂商服务只发 TOGGLE_ON；</item>
    /// <item>标准（混合）：TOGGLE_OFF；</item>
    /// <item>集显：TOGGLE_IGPU，仅 40 三模档 / 50 非热切换机型（<see cref="GpuRouteContext.IgpuMuxTarget"/>）；</item>
    /// <item>自动：没有 MUX 目标（过去自创的「重启前发 RB_AUTO」已删除）。</item>
    /// </list>
    /// 服务档位能用 RESTART 时 <see cref="GpuRestartRoute.ServiceRestart"/> 为真，否则由我方重启。
    /// </summary>
    internal static GpuRestartRoute CreateGpuRestartRoute(int mode, GpuRouteContext context)
    {
        if (mode is < GpuIGpu or > GpuAuto) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!context.SupportsDgpuDirect) return GpuRestartRoute.None;
        if (context.Generation is not (DgpuGenerationKind.Gen30 or DgpuGenerationKind.Gen40 or DgpuGenerationKind.Gen50))
            return GpuRestartRoute.None;
        if (context.Tier is not (GcuServiceTier.Modern12 or GcuServiceTier.Foreign)) return GpuRestartRoute.None;

        bool Allowed(string action) => DisplayRoutePolicy.AllowsAction(
            context.Generation, action, context.ThreeMode, context.Tier, context.HotSwap);

        Dictionary<string, object>[] payloads = mode switch
        {
            GpuDgpu when !Allowed(DisplayRouteMatrix.ToggleOn) => Array.Empty<Dictionary<string, object>>(),
            GpuDgpu when context.Generation == DgpuGenerationKind.Gen50 && context.Tier == GcuServiceTier.Modern12 =>
            [
                new() { ["Action"] = DisplayRouteMatrix.ToggleOn },
                CreateGpuModePayload(GpuStandard),
                new() { ["Action"] = DisplayRouteMatrix.ToggleOn },
            ],
            GpuDgpu => [new() { ["Action"] = DisplayRouteMatrix.ToggleOn }],
            GpuStandard when Allowed(DisplayRouteMatrix.ToggleOff) => [new() { ["Action"] = DisplayRouteMatrix.ToggleOff }],
            GpuIGpu when context.IgpuMuxTarget && Allowed(DisplayRouteMatrix.ToggleIgpu) =>
                [new() { ["Action"] = DisplayRouteMatrix.ToggleIgpu }],
            _ => Array.Empty<Dictionary<string, object>>(),
        };
        if (payloads.Length == 0) return GpuRestartRoute.None;
        return new GpuRestartRoute(payloads,
            DisplayRoutePolicy.AllowsServiceRestart(context.Generation, context.ThreeMode, context.Tier));
    }

    static string ActionOf(Dictionary<string, object> payload) =>
        payload.TryGetValue("Action", out object? value) ? value?.ToString() ?? "" : "";

    /// <summary>请求 GCU 回传当前显卡模式，不写入或切换任何硬件状态。</summary>
    public async Task<GpuModeStatusReadback> RefreshGpuModeStatus()
    {
        try
        {
            if (!_hw.IsConnected) return GpuModeStatusReadback.Unavailable;
            long versionBeforeRequest = _hw.GpuModeStatusVersion;
            await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" }).ConfigureAwait(false);
            bool refreshed = await _hw.WaitForStateAsync(
                () => _hw.GpuModeStatusVersion > versionBeforeRequest,
                TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false);
            bool knownState = _hw.GpuModeStatusVersion > 0 &&
                CurrentGpuMode is >= GpuIGpu and <= GpuAuto;
            GpuModeStatusReadback result = refreshed
                ? GpuModeStatusReadback.Fresh
                : knownState ? GpuModeStatusReadback.Cached : GpuModeStatusReadback.Unavailable;
            if (!refreshed && knownState)
                Logger.WriteLine($"RefreshGpuModeStatus did not receive a new packet; using existing mode={CurrentGpuMode} for this request.");
            Logger.WriteLine($"RefreshGpuModeStatus result={result} mode={CurrentGpuMode}");
            return result;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("RefreshGpuModeStatus failed: " + ex.Message);
            return GpuModeStatusReadback.Unavailable;
        }
    }

    /// <summary>
    /// 热切换：50 系热切换机型上的集显 ↔ 标准，以及服务自动档。MUX 目标（直连 / 混合 / NVRAM 核显）
    /// 不走这里，走 <see cref="RequestGpuModeRestartOutcomeAsync"/>。
    ///
    /// <para>「生效」分两步判（§4.2）：服务回报（<c>CheckDGpuStatusforIGpuOnlyOnSuccess</c>，官方同款判据）
    /// **加** NVIDIA 显示设备真的断开 / 恢复（CfgMgr 回读）。2026-09-10 本机实测 RB_ON 只翻转了服务的软件寄存器、
    /// 独显没有断开——只看服务回报就会把这种「切换」报成成功。</para>
    ///
    /// <para>节奏与官方 IgpuOnlyOn/OffCommand 一致：每 2 s 查一次，count&gt;60 放弃，每第 4 次重发；
    /// 超时发 <c>IGPUONLYCONNECTIONSWITCH_STATUS</c> 回滚到切换前的开关值
    /// （CCUWinUI GpuSettingPageViewModel.cs:276-440）。</para>
    /// </summary>
    public async Task<bool> SwitchGpuMode(int mode, bool? pluggedForAuto = null)
    {
        var requestCts = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _gpuSwitchCts, requestCts);
        previous?.Cancel();
        bool lockTaken = false;
        IDisposable? releasedHandles = null;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (_hw is not { IsConnected: true } || mode is < GpuIGpu or > GpuAuto || !_hw.CanSwitchGpuMode(mode)) return false;
            if (mode == GpuDgpu || !_hw.CanOfferGpuHotSwap)
            {
                Logger.WriteLine($"SwitchGpuMode({mode}) refused: no hot-switch path here (hotSwap={_hw.CanOfferGpuHotSwap}); MUX targets use the restart route.");
                return false;
            }
            GpuRouteCommand? command = GpuRouteCommandLayer.BuildHotSwitchCommand(mode, GpuRouteContext.From(_hw));
            if (command is null)
            {
                Logger.WriteLine($"SwitchGpuMode({mode}) refused: not allowed for generation {_hw.DgpuGeneration} / tier {_hw.ServiceTier}");
                return false;
            }
            lockTaken = await AcquireSwitchLockAsync($"SwitchGpuMode({mode})", requestCts.Token).ConfigureAwait(false);
            if (!lockTaken) return false;
            requestCts.Token.ThrowIfCancellationRequested();

            // 回滚基准必须取自发布之前：热切换期间 GCU 会把目标值回显进寄存器，
            // 超时后从这里已经读不到「切换前是什么」。回滚的是热切换寄存器本身（官方 preIGPUOnlyConnectionSwitch）。
            if (CurrentGpuMode == GpuDgpu)
            {
                Logger.WriteLine($"SwitchGpuMode({mode}) refused: the machine is in dGPU-direct; leaving it needs the restart route.");
                return false;
            }
            int modeBeforeSwitch = _hw.IgpuOnlyRegister >= 0 ? _hw.IgpuOnlyRegister : CurrentGpuMode;

            int expectedRuntime = mode switch
            {
                GpuIGpu => IgpuOnlySemantics.SuccessOn,
                GpuStandard => IgpuOnlySemantics.SuccessOff,
                _ => pluggedForAuto is bool plugged ? IgpuOnlySemantics.AutomaticRuntime(plugged) : -1,
            };
            long statusVersion = _hw.GpuModeStatusVersion;
            long resultVersion = _hw.GpuSwitchResultVersion;
            bool requireResult = _hw.GpuSwitchResultReported;
            // 服务侧到位：寄存器是目标值（不是合成后的 GpuMode——TOGGLE_OFF 下 RB_ON 仍显示混合），
            // 且发令之后来过新状态；服务报过运行态时还要运行态等于期望值（官方判据）。
            bool ServiceReached()
            {
                if (_hw.IgpuOnlyRegister != mode) return false;
                if (_hw.GpuModeStatusVersion <= statusVersion && _hw.GpuSwitchResultVersion <= resultVersion) return false;
                if (!requireResult || expectedRuntime < 0) return true;
                return _hw.GpuSwitchResultVersion > resultVersion && _hw.GpuSwitchResult == expectedRuntime;
            }
            bool PresenceMatches(DgpuPresence presence) => expectedRuntime switch
            {
                IgpuOnlySemantics.SuccessOn => presence is DgpuPresence.Disabled or DgpuPresence.Absent,
                IgpuOnlySemantics.SuccessOff => presence == DgpuPresence.Present,
                _ => true,
            };

            // 我方自己也会占住独显：悬浮窗开着时 LHM 打开 GPU 传感器、NVML 持有句柄，断开前先放掉。
            if (expectedRuntime == IgpuOnlySemantics.SuccessOn)
                releasedHandles = HardwareControl.SuspendDgpuHandles($"hot switch to mode {mode}");
            // 热切换的确认可以长达约两分钟（61 次轮询 × 2 秒）。登记为可让位的长操作，
            // 让其它切换意图能把它请下来，而不是在 _switchLock 上排队两分钟。
            Volatile.Write(ref _longRunningSwitchCts, requestCts);
            Logger.WriteLine($"MechrevoService.SwitchGpuMode({mode}) -> {command.Action}, before={modeBeforeSwitch}, expectedRuntime={expectedRuntime}");
            await _hw.Publish(MqttTopics.SettingControl, command.Payload).ConfigureAwait(false);

            TimeSpan pollDelay = TimeSpan.FromMilliseconds(IgpuOnlySemantics.PollIntervalMilliseconds);
            bool confirmed = false;
            DgpuPresence presence = DgpuPresence.Unknown;
            for (int attempt = 0; attempt < HotSwitchStatusPollLimit && !confirmed; attempt++)
            {
                requestCts.Token.ThrowIfCancellationRequested();
                await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" }).ConfigureAwait(false);
                bool serviceReached = await _hw.WaitForStateAsync(ServiceReached, pollDelay, requestCts.Token).ConfigureAwait(false);
                if (serviceReached)
                {
                    presence = GpuRouteProbe.ReadPresence();
                    confirmed = PresenceMatches(presence);
                    if (!confirmed)
                    {
                        Logger.WriteLineThrottled("hot-switch-presence",
                            $"Hot switch: the service reports mode {mode} but the NVIDIA device is {presence}; waiting for the hardware.", 10_000);
                        await Task.Delay(pollDelay, requestCts.Token).ConfigureAwait(false);
                    }
                }
                // The official console re-sends the request every fourth poll.
                if (!confirmed && ShouldRetryHotSwitchPoll(attempt))
                    await _hw.Publish(MqttTopics.SettingControl, command.Payload).ConfigureAwait(false);
            }

            // 官方 IgpuOnlyOn/OffCommand 超时（count>60）后的动作是回滚：发
            // IGPUONLYCONNECTIONSWITCH_STATUS 把开关退回切换前的值，再刷新状态。
            // 不回滚的话寄存器停留在目标值——界面按回显显示目标模式、硬件却没变。
            if (!confirmed)
                await RollbackFailedHotSwitchAsync(modeBeforeSwitch, requestCts.Token).ConfigureAwait(false);

            Logger.WriteLine($"SwitchGpuMode confirmed={confirmed}: expected={mode} actual={CurrentGpuMode} runtime={_hw.GpuSwitchResult}/{expectedRuntime} dgpu={presence} action={command.Action} elapsed={elapsed.ElapsedMilliseconds}ms");
            GpuRouteMonitor.Refresh();
            return confirmed;
        }
        catch (OperationCanceledException)
        {
            Logger.WriteLine($"SwitchGpuMode({mode}) superseded after {elapsed.ElapsedMilliseconds}ms");
            return false;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("SwitchGpuMode fail: " + ex.Message);
            return false;
        }
        finally
        {
            releasedHandles?.Dispose();
            if (lockTaken) _switchLock.Release();
            Interlocked.CompareExchange(ref _longRunningSwitchCts, null, requestCts);
            Interlocked.CompareExchange(ref _gpuSwitchCts, null, requestCts);
            requestCts.Dispose();
        }
    }

    /// <summary>
    /// 热切换确认超时后的回滚。逐字对齐官方 IgpuOnlyOn/OffCommand 的超时分支：
    /// IGPUONLYCONNECTIONSWITCH_STATUS 携带切换前的开关值（RB_OFF=0 / RB_ON=1 / RB_AUTO=2），
    /// 随后 GETSTATUS 刷新，并短暂等待状态回到切换前——让调用方拿到 false 时
    /// <see cref="CurrentGpuMode"/> 已经是真实回到的旧模式，而不是目标的回显。
    /// 回滚是清理动作：任何失败只记日志，绝不能从这里抛出打断 SwitchGpuMode 的收尾。
    /// </summary>
    internal async Task RollbackFailedHotSwitchAsync(int modeBeforeSwitch, CancellationToken token)
    {
        try
        {
            int rollbackStatus = IgpuOnlySemantics.RollbackStatus(modeBeforeSwitch);
            Logger.WriteLine($"Hot switch not confirmed; rolling back to pre-switch mode {modeBeforeSwitch} (status={rollbackStatus})");
            await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object>
            {
                ["Action"] = "IGPUONLYCONNECTIONSWITCH_STATUS",
                ["Status"] = rollbackStatus,
            }).ConfigureAwait(false);
            await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" }).ConfigureAwait(false);
            bool knownBefore = modeBeforeSwitch is GpuIGpu or GpuStandard or GpuAuto;
            await _hw.WaitForStateAsync(
                () => knownBefore ? _hw.IgpuOnlyRegister == modeBeforeSwitch : _hw.IgpuOnlyRegister != GpuIGpu,
                TimeSpan.FromMilliseconds(2500),
                token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.WriteLine("Hot switch rollback failed: " + ex.Message); }
    }

    /// <summary>NVIDIA 首选 GPU 回读节奏：每 500 ms 读一次驱动，5 s 内一致才算生效（测试可压缩）。</summary>
    internal static int NvPreferencePollIntervalMilliseconds { get; set; } = 500;
    internal static int NvPreferencePollLimit { get; set; } = 10;

    /// <summary>
    /// GTX 10/16、RTX 20：设置 NVIDIA 全局首选 GPU（官方设置页的「独立显卡」开关）。
    /// 只在 <see cref="MechrevoHw.CanOfferNvPreferredGpu"/> 时发 <c>NV_CTRL_PANEL_*</c>；
    /// 是否生效只认 NVIDIA 驱动 DRS 里读回来的值（<see cref="NvPreferredGpuReader"/>）。
    /// </summary>
    internal async Task<GpuApplyResult> SetNvPreferredGpuAsync(NvPreferredGpu target)
    {
        if (target is not (NvPreferredGpu.AutoSelect or NvPreferredGpu.HighPerformance)) return GpuApplyResult.Unsupported;
        var requestCts = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _gpuSwitchCts, requestCts);
        previous?.Cancel();
        bool lockTaken = false;
        try
        {
            if (_hw is not { IsConnected: true }) return GpuApplyResult.Failed;
            if (!_hw.CanOfferNvPreferredGpu)
            {
                Logger.WriteLine($"SetNvPreferredGpu({target}) refused: generation={_hw.DgpuGeneration} tier={_hw.ServiceTier} status={_hw.NvControlPanelPreference}");
                return GpuApplyResult.Unsupported;
            }
            string action = target == NvPreferredGpu.HighPerformance
                ? DisplayRouteMatrix.NvCtrlPanelHighPerformance
                : DisplayRouteMatrix.NvCtrlPanelAutoSelect;
            lockTaken = await AcquireSwitchLockAsync($"SetNvPreferredGpu({target})", requestCts.Token).ConfigureAwait(false);
            if (!lockTaken) return GpuApplyResult.Failed;

            NvPreferredGpu before = await Task.Run(NvPreferredGpuReader.ReadFromDriver, requestCts.Token).ConfigureAwait(false);
            await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = action }).ConfigureAwait(false);
            NvPreferredGpu now = before;
            for (int poll = 0; poll < NvPreferencePollLimit; poll++)
            {
                await Task.Delay(NvPreferencePollIntervalMilliseconds, requestCts.Token).ConfigureAwait(false);
                now = await Task.Run(NvPreferredGpuReader.ReadFromDriver, requestCts.Token).ConfigureAwait(false);
                if (now == target) break;
            }
            // 服务的 DGpu 回显只作辅助：刷新一次，让状态与驱动对齐。
            await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" }).ConfigureAwait(false);
            NvPreferredGpuMonitor.Store(now);
            bool confirmed = now == target;
            Logger.WriteLine($"SetNvPreferredGpu({target}) -> {action}: driver before={before} after={now} confirmed={confirmed}");
            return confirmed ? GpuApplyResult.Confirmed : GpuApplyResult.NotApplied;
        }
        catch (OperationCanceledException)
        {
            Logger.WriteLine($"SetNvPreferredGpu({target}) superseded");
            return GpuApplyResult.Failed;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"SetNvPreferredGpu({target}) failed: {ex.Message}");
            return GpuApplyResult.Failed;
        }
        finally
        {
            if (lockTaken) _switchLock.Release();
            Interlocked.CompareExchange(ref _gpuSwitchCts, null, requestCts);
            requestCts.Dispose();
        }
    }

    /// <summary>
    /// 应用独显节能模式（官方 GPU 设置页的"应用"按钮，ApplyCommand，CCUWinUI L53731-53745）。
    ///
    /// 命令形状与官方逐字一致，但**故意没有界面入口**：它是一次性动作、没有对应的
    /// 状态字段可回读，而效果是触发一次独显工作模式切换——和我们已有的显卡模式面板
    /// （GPUModeControl）语义重叠。多一个按钮只会让用户在两处切显卡，
    /// 而显卡切换失败可能连显示输出都没有。留着方法供诊断与将来接线。
    /// </summary>
    public async Task<bool> SwitchGpuPowerSaving()
    {
        try
        {
            await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "GPU_POWERSAVEINGMODE" });
            Logger.WriteLine("SwitchGpuPowerSaving 已发送");
            return true;
        }
        catch (Exception ex) { Logger.WriteLine("SwitchGpuPowerSaving fail: " + ex.Message); return false; }
    }

    /// <summary>自动刷新率：电池供电时自动降刷新率省电（开启后手动刷新率选择失效，原版互斥逻辑）。</summary>
    public async Task<bool> SwitchAutoRefreshRate(bool on)
    {
        try
        {
            if (!_hw.DcHzSeen) return false;
            await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "GPU_DC_HZ", ["Enable"] = on });
            bool confirmed = await ConfirmSettingAsync(() => _hw.DcHzSeen && _hw.DcHz == on);
            Logger.WriteLine($"SwitchAutoRefreshRate({on}) confirmed={confirmed}");
            return confirmed;
        }
        catch (Exception ex) { Logger.WriteLine("SwitchAutoRefreshRate fail: " + ex.Message); return false; }
    }

    /// <summary>局部调光（Mini-LED 分区控光开关）。</summary>
    public async Task<bool> SwitchLocalDimming(bool on)
    {
        try
        {
            if (!_hw.SupportsLocalDimming) return false;
            await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = on ? "LOCALDIMMING_ON" : "LOCALDIMMING_OFF" });
            bool confirmed = await ConfirmSettingAsync(() => _hw.LocalDimmingSeen && _hw.LocalDimming == on);
            Logger.WriteLine($"SwitchLocalDimming({on}) confirmed={confirmed}");
            return confirmed;
        }
        catch (Exception ex) { Logger.WriteLine("SwitchLocalDimming fail: " + ex.Message); return false; }
    }

    /// <summary>LCD 响应加速（Overdrive）：加压驱动加快液晶翻转，减少游戏拖影。</summary>
    public async Task<bool> SwitchLcdOverdrive(bool on)
    {
        try
        {
            if (!_hw.SupportsLcdOverdrive) return false;
            await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = on ? "LCDOverdrive_ON" : "LCDOverdrive_OFF" });
            bool confirmed = await ConfirmSettingAsync(() => _hw.LcdOverdriveSeen && _hw.LcdOverdrive == on);
            Logger.WriteLine($"SwitchLcdOverdrive({on}) confirmed={confirmed}");
            return confirmed;
        }
        catch (Exception ex) { Logger.WriteLine("SwitchLcdOverdrive fail: " + ex.Message); return false; }
    }

    public async Task<bool> SwitchRefreshRate(int hz)
    {
        try
        {
            if (!_hw.SupportsDisplayRefresh || !_hw.HzList.Contains(hz)) return false;
            await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "GPU_HZSETTING", ["Hz"] = hz.ToString() });
            bool confirmed = await ConfirmSettingAsync(() => _hw.CurrentHz == hz);
            Logger.WriteLine($"SwitchRefreshRate({hz}) confirmed={confirmed}");
            return confirmed;
        }
        catch (Exception ex) { Logger.WriteLine("SwitchRefreshRate fail: " + ex.Message); return false; }
    }

    public async Task<bool> SwitchUsbCharger(bool on)
    {
        try
        {
            if (!_hw.SupportsQuickSwitch("usb")) return false;
            await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = on ? "USB_CHARGER_ON" : "USB_CHARGER_OFF" });
            bool confirmed = await ConfirmSettingAsync(() => _hw.UsbCharger == on);
            Logger.WriteLine($"SwitchUsbCharger({on}) confirmed={confirmed}");
            return confirmed;
        }
        catch (Exception ex) { Logger.WriteLine("SwitchUsbCharger fail: " + ex.Message); return false; }
    }

    public async Task<bool> SwitchQuick(string key, bool on)
    {
        try
        {
            if (!_hw.SupportsQuickSwitch(key)) return false;
            string? action = (key, on) switch
            {
                ("touchpad", true) => "TOUCHPAD_ON", ("touchpad", false) => "TOUCHPAD_OFF",
                ("wifi", true) => "WIFI_ON", ("wifi", false) => "WIFI_OFF",
                ("bt", true) => "BT_ON", ("bt", false) => "BT_OFF",
                ("webcam", true) => "WEBCAM_ON", ("webcam", false) => "WEBCAM_OFF",
                ("winkey", true) => "WINKEY_LOCK", ("winkey", false) => "WINKEY_UNLOCK",
                ("fnkey", true) => "FNKEY_LOCK", ("fnkey", false) => "FNKEY_UNLOCK",
                ("osd", true) => "OSD_HIDDEN_OFF", ("osd", false) => "OSD_HIDDEN_ON",
                ("numpad", true) => "NUMPAD_LOCK", ("numpad", false) => "NUMPAD_UNLOCK",
                ("copilot", true) => "COPILOTKEY_LOCK", ("copilot", false) => "COPILOTKEY_UNLOCK",
                ("acrecovery", true) => "ACRECOVERY_TOGGLE_ON", ("acrecovery", false) => "ACRECOVERY_TOGGLE_OFF",
                // 电池 Logo 灯。形状与同族的 TOGGLE 开关完全一致（官方 PowerSwitching，L74043）。
                ("batterylogo", true) => "BATTERYLOGO_TOGGLE_ON", ("batterylogo", false) => "BATTERYLOGO_TOGGLE_OFF",
                ("highperf", true) => "HIGHPERFORMANCEPOWERMODE_ON", ("highperf", false) => "HIGHPERFORMANCEPOWERMODE_OFF",
                ("touchpadtoggle", true) => "TOUCHPAD_TOGGLE_ON", ("touchpadtoggle", false) => "TOUCHPAD_TOGGLE_OFF",
                ("singlecolorkb", true) => "SINGLE_COLOR_KBBL_STATUS_ON", ("singlecolorkb", false) => "SINGLE_COLOR_KBBL_STATUS_OFF",
                ("uni", true) => "Uni_ON", ("uni", false) => "Uni_OFF",
                ("omni", true) => "Omni_ON", ("omni", false) => "Omni_OFF",
                ("powerlight", true) => "PowerLight_ON", ("powerlight", false) => "PowerLight_OFF",
                // 游戏白名单与 CPU 高级性能都不走这条路：它们的主题是 Fan/Control、
                // 开关值在载荷字段里而不是动作名后缀。见 SwitchGameWhitelist /
                // SwitchCpuAdvancedPerformance。
                _ => null,
            };
            if (action is null) return false;
            bool confirmed = false;
            int sendAttempts = key == "highperf" ? 2 : 1;
            long statusVersionBeforeCommand = _hw.SettingStatusVersion;
            for (int attempt = 0; attempt < sendAttempts && !confirmed; attempt++)
            {
                var payload = new Dictionary<string, object> { ["Action"] = action };
                // 官方的电源灯开关命令把亮度一起带上（PowerLight_ON/OFF + Brightness）。
                // 只发 Action 的话服务端拿不到亮度，会按 0 处理，等于开灯的同时把它调暗到看不见。
                if (key == "powerlight" && _hw.PowerLightBrightness >= 0)
                    payload["Brightness"] = _hw.PowerLightBrightness;
                await _hw.Publish(MqttTopics.SettingControl, payload);
                confirmed = await ConfirmSettingAsync(
                    () => _hw.QuickSwitches.TryGetValue(key, out bool actual) && actual == on,
                    statusVersionBeforeCommand);
                if (!confirmed && attempt + 1 < sendAttempts) await Task.Delay(250);
            }
            if (confirmed && key == "highperf")
                confirmed = await Task.Run(() => _syncHighPerformancePowerMode(on, _hw.OperatingMode)).ConfigureAwait(false);
            Logger.WriteLine($"SwitchQuick({key},{on}) confirmed={confirmed}");
            return confirmed;
        }
        catch (Exception ex) { Logger.WriteLine("SwitchQuick fail: " + ex.Message); return false; }
    }

    /// <summary>
    /// 游戏白名单。
    ///
    /// 这一项和别的快捷开关三处都不一样，之前全写错了，真机验证时表现为"点了没反应"：
    /// 主题是 <c>Fan/Control</c> 而不是 <c>Setting/Control</c>；
    /// 动作名是 <c>OPERATING_GAME_WHITE_LIST</c>，**没有** <c>_ON</c> / <c>_OFF</c> 后缀；
    /// 开关值放在载荷字段 <c>GameWhitelistSwitch</c> 里（数字 1/0）。
    /// 依据是官方 CCUWinUI 的两个发送点（开与关各一处，只差载荷里的 1/0）。
    /// </summary>
    public Task<bool> SwitchGameWhitelist(bool on) => SwitchFanChannelToggle(
        "gamewhitelist", on,
        new Dictionary<string, object>
        {
            ["Action"] = "OPERATING_GAME_WHITE_LIST",
            ["GameWhitelistSwitch"] = on ? 1 : 0,
        });

    /// <summary>
    /// CPU 高级性能 / 超频菜单总闸。
    ///
    /// 和游戏白名单一样属于 Fan 通道，之前也全写错了，真机验证时表现为"点了没反应"：
    /// 主题是 <c>Fan/Control</c>、动作固定为 <c>SET_OPERATING_MODE_DETAIL</c>，
    /// 开与关的区别在**载荷字段名**上——开发 <c>CPUPerformanceAndOverClockMenuSwitch_ON="1"</c>、
    /// 关发 <c>CPUPerformanceAndOverClockMenuSwitch_OFF="0"</c>（值是字符串）。
    /// 之前把这两个名字当成 Action 发到了 <c>Setting/Control</c>，还用轮询 Setting 通道的
    /// <see cref="ConfirmSettingAsync"/> 去确认一个在 Fan/Status 里的状态，三处都对不上。
    /// 依据：官方 CCUWinUI 的 CpuAdvancedPerformanceSwitchCommand（L49989-50014）。
    /// </summary>
    public Task<bool> SwitchCpuAdvancedPerformance(bool on) => SwitchFanChannelToggle(
        "cpuadvperf", on,
        new Dictionary<string, object>
        {
            ["Action"] = "SET_OPERATING_MODE_DETAIL",
            [on ? "CPUPerformanceAndOverClockMenuSwitch_ON" : "CPUPerformanceAndOverClockMenuSwitch_OFF"] = on ? "1" : "0",
        });

    /// <summary>
    /// Fan 通道快捷开关的公共下发 + 确认流程。
    ///
    /// 与 <see cref="ConfirmSettingAsync"/> 的关键区别：这些开关的状态回读在
    /// <c>Fan/Status</c> 里，所以刷新要发 <c>Fan/Control</c> 的 GETSTATUS、
    /// 新鲜度要比 <see cref="MechrevoHw.FanStatusVersion"/>。用 Setting 通道那套
    /// 去确认它们，结果是命令其实生效了但永远确认不了。
    /// </summary>
    async Task<bool> SwitchFanChannelToggle(string key, bool on, Dictionary<string, object> payload)
    {
        try
        {
            if (!_hw.SupportsQuickSwitch(key)) return false;
            long versionBefore = _hw.FanStatusVersion;
            await _hw.Publish(MqttTopics.FanControl, payload);
            bool Matches() => _hw.QuickSwitches.TryGetValue(key, out bool actual) && actual == on;
            bool confirmed = Matches() && _hw.FanStatusVersion > versionBefore;
            for (int attempt = 0; attempt < 3 && !confirmed; attempt++)
            {
                await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                confirmed = await _hw.WaitForStateAsync(
                    () => Matches() && _hw.FanStatusVersion > versionBefore,
                    TimeSpan.FromMilliseconds(attempt == 0 ? 500 : 800));
            }
            Logger.WriteLine($"SwitchFanChannelToggle({key},{on}) confirmed={confirmed}");
            return confirmed;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"SwitchFanChannelToggle({key}) fail: " + ex.Message);
            return false;
        }
    }

    /// <summary>Uni 与 Omni 互斥的另一半。开一个就必须关另一个。</summary>
    internal static string? MutuallyExclusivePartner(string key) => key switch
    {
        "uni" => "omni",
        "omni" => "uni",
        _ => null,
    };

    /// <summary>
    /// Uni / Omni 开关。官方 UI 把这两个做成互斥单选，所以开启一个之前要先关掉另一个，
    /// 否则服务端会保留两个都开的状态，界面与硬件对不上。
    /// </summary>
    public async Task<bool> SwitchUniOmni(string key, bool on)
    {
        string? partner = MutuallyExclusivePartner(key);
        if (partner is null) return false;

        bool partnerWasOn = on && _hw.QuickSwitches.TryGetValue(partner, out bool p) && p;
        if (partnerWasOn && !await SwitchQuick(partner, false))
        {
            Logger.WriteLine($"SwitchUniOmni({key}) aborted: could not clear {partner} first.");
            return false;
        }

        if (await SwitchQuick(key, on)) return true;

        // 关伙伴成功、开自己失败时必须把伙伴还原。
        // 不还原的话用户点一下 Omni，结果 Omni 没开、Uni 反而被关掉了——
        // 落到「两个都关」，而这一组是互斥单选，本来不该出现这种组合。
        if (partnerWasOn)
        {
            bool restored = await SwitchQuick(partner, true);
            Logger.WriteLine($"SwitchUniOmni({key},{on}) failed; restored {partner}={restored}");
        }
        return false;
    }

    /// <summary>
    /// 电源指示灯亮度（0..100）。
    ///
    /// 官方有三个动作：PowerLight_ON / PowerLight_OFF（开关，同时带亮度）和
    /// PowerLight_Brightness（只调亮度）。这里用专用的那个——借开关动作调亮度
    /// 会在服务端把开关态一起重写，外部刚改过开关时就会被这条命令覆盖回去。
    /// </summary>
    public async Task<bool> SetPowerLightBrightness(int brightness)
    {
        try
        {
            if (!_hw.SupportsPowerLightBrightness) return false;
            int target = Math.Clamp(brightness, 0, 100);
            long versionBefore = _hw.SettingStatusVersion;
            await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object>
            {
                ["Action"] = "PowerLight_Brightness",
                ["Brightness"] = target,
            });
            bool confirmed = await ConfirmSettingAsync(() => _hw.PowerLightBrightness == target, versionBefore);
            Logger.WriteLine($"SetPowerLightBrightness({target}) confirmed={confirmed}");
            return confirmed;
        }
        catch (Exception ex) { Logger.WriteLine("SetPowerLightBrightness fail: " + ex.Message); return false; }
    }

    // GPU Whisper（静音）模式此前有一个 SwitchWhisperMode，已删除。
    //
    // 它下发的是 Fan/Control 的 SET_OPERATING_MODE_DETAIL 加 GPU_WhisperModeSetting，
    // 两处都是猜的：官方 5.56 界面对这一族只有属性声明、没有任何下发点，
    // 而且 Setting 是静音档位、Switch 才是开关，当时还把两者搞混了。
    // 真机验证的结论是下发后回读毫无变化，所以这是个空头接口，连同界面入口一起撤掉。
    // 状态解析保留在 MechrevoHw（WhisperMode / WhisperModeLevel）供诊断与将来接线。



    internal static bool IsFreshSettingState(long versionBeforeCommand, long observedVersion, bool valueMatches) =>
        observedVersion > versionBeforeCommand && valueMatches;

    async Task<bool> ConfirmSettingAsync(Func<bool> isConfirmed, long? versionBeforeCommand = null)
    {
        await _settingLock.WaitAsync();
        try
        {
            bool IsConfirmedAfterCommand() =>
                versionBeforeCommand is null
                    ? isConfirmed()
                    : IsFreshSettingState(versionBeforeCommand.Value, _hw.SettingStatusVersion, isConfirmed());

            if (IsConfirmedAfterCommand()) return true;
            if (await _hw.WaitForStateAsync(IsConfirmedAfterCommand, TimeSpan.FromMilliseconds(180))) return true;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                if (await _hw.WaitForStateAsync(
                    IsConfirmedAfterCommand,
                    TimeSpan.FromMilliseconds(attempt == 0 ? 500 : 700))) return true;
            }
            return false;
        }
        finally { _settingLock.Release(); }
    }

    static bool SyncHighPerformancePowerMode(bool enabled, int operatingMode)
    {
        int previousSkip = AppConfig.Get("skip_powermode", 0);
        AppConfig.Set("skip_powermode", enabled ? 0 : 1);
        bool confirmed = MechrevoLite.Mode.PowerNative.ApplyMechrevoPowerModeAutomation(enabled, operatingMode);
        if (!confirmed) AppConfig.Set("skip_powermode", previousSkip);
        return confirmed;
    }

    /// <summary>
    /// 键盘灯睡眠计时「只下发」语义：恢复周期必须用它把命令在进入自定义帧模式**之前**发出，
    /// **不等待回读**——这条设置的回读本机可能永远不确认，await 它会把本地 HID 效果推迟约 2 秒。
    /// 与 <see cref="PublishLightPower"/> / <see cref="SetLightPower"/> 同一分拆。
    /// </summary>
    public async Task<bool> PublishKeyboardCloseTimer(int minutes)
    {
        try
        {
            if (!_hw.SupportsKeyboard && !_hw.SupportsLightbar) return false;
            if (minutes <= 0)
                await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "KEYBOARD_LIGHTBAR_TIMER_OFF" });
            else
                await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "KEYBOARD_LIGHTBAR_TIMER_ON", ["Mins"] = minutes });
            return true;
        }
        catch (Exception ex) { Logger.WriteLine("PublishKeyboardCloseTimer fail: " + ex.Message); return false; }
    }

    /// <summary>键盘灯睡眠时间（分钟）：0=关闭，否则 KEYBOARD_LIGHTBAR_TIMER_ON+Mins。原版机制：EC 无输入 N 分钟后自动熄灭。</summary>
    public async Task<bool> SwitchCloseTimer(int minutes)
    {
        try
        {
            if (!await PublishKeyboardCloseTimer(minutes).ConfigureAwait(false)) return false;
            bool confirmed = await ConfirmSettingAsync(() => _hw.CloseTimerMinutes == Math.Max(0, minutes));
            Logger.WriteLine($"SwitchCloseTimer({minutes}) confirmed={confirmed}");
            return confirmed;
        }
        catch (Exception ex) { Logger.WriteLine("SwitchCloseTimer fail: " + ex.Message); return false; }
    }

    /// <summary>液冷泵速档位（原版协议：BT_LC/Control LC_PumpCtrl 0/1/2 → 45/60/90%）。</summary>
    public Task<bool> SwitchLcPump(int index) =>
        RunLiquidCoolingAsync($"SwitchLcPump({index})", LiquidCoolingWriteLockTimeout, async () =>
        {
            if (!CanControlLiquidCooling || index is < 0 or > 2) return false;
            return await PublishAndConfirmLiquidCoolingControlAsync(
                new Dictionary<string, object> { ["Action"] = "LC_PumpCtrl", ["PumpCtrl"] = index.ToString() },
                () => _hw.LcPumpControl == index,
                $"SwitchLcPump({index})");
        });

    /// <summary>液冷风扇档位（原版协议：BT_LC/Control LC_FanCtrl 0-3 → 40/50/60/90%）。</summary>
    public Task<bool> SwitchLcFan(int index) =>
        RunLiquidCoolingAsync($"SwitchLcFan({index})", LiquidCoolingWriteLockTimeout, async () =>
        {
            if (!CanControlLiquidCooling || index is < 0 or > 3) return false;
            return await PublishAndConfirmLiquidCoolingControlAsync(
                new Dictionary<string, object> { ["Action"] = "LC_FanCtrl", ["FanCtrl"] = index.ToString() },
                () => _hw.LcFanControl == index,
                $"SwitchLcFan({index})");
        });

    /// <summary>
    /// 液冷风扇厂商自动档：LC_FanCtrl=4。官方界面在 LiquidCoolingAutoModeSupport 机型上
    /// 提供该选项；实测回读 LC_FanCtrl=4 且 LC_CoolingAuto=true，切回手动档后归位。
    /// </summary>
    public Task<bool> SwitchLcFanAuto() =>
        RunLiquidCoolingAsync("SwitchLcFanAuto()", LiquidCoolingWriteLockTimeout, async () =>
        {
            if (!CanControlLiquidCooling) return false;
            return await PublishAndConfirmLiquidCoolingControlAsync(
                new Dictionary<string, object>
                {
                    ["Action"] = "LC_FanCtrl",
                    ["FanCtrl"] = LiquidCoolingDisplayPolicy.GcuFanAutoIndex.ToString(),
                },
                () => _hw.LcFanControl == LiquidCoolingDisplayPolicy.GcuFanAutoIndex,
                "SwitchLcFanAuto()");
        });

    /// <summary>
    /// 一次液冷写入序列允许等待前一个序列的最长时间。灯效档位是多条命令组成的序列，
    /// 泵/风扇的确认最长约 1.4 秒，这个上限只需覆盖正常序列时长；超时说明有操作卡住，
    /// 此时放弃并给出可解释的失败，比无限等待更好。
    /// </summary>
    internal static readonly TimeSpan LiquidCoolingWriteLockTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 状态查询的等锁上限。查询是幂等的，忙的时候直接跳过比排队更好：既不会拖住 UI 定时器，
    /// 也不会用自己的 GETSTATUS 抢掉正在进行的确认所依赖的那次补查。
    /// </summary>
    internal static readonly TimeSpan LiquidCoolingQueryLockTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 液冷所有 BT_LC 写入与查询的唯一串行化入口。
    /// 之前只有泵/风扇确认路径持这把锁，灯效序列和状态查询完全不加锁，导致两个问题：
    /// 两个灯效序列交叉下发会让设备停在混合档位；别人的 GETSTATUS 会推进 LcStatusVersion，
    /// 把泵/风扇确认唯一一次「排除在途回包」的补查机会消耗掉，把本该成功的写入判成失败。
    /// </summary>
    async Task<bool> RunLiquidCoolingAsync(string operation, TimeSpan lockTimeout, Func<Task<bool>> body)
    {
        if (!await _liquidCoolingLock.WaitAsync(lockTimeout).ConfigureAwait(false))
        {
            Logger.WriteLineThrottled(
                "lc-busy",
                $"{operation} skipped: another liquid-cooling operation is still in progress",
                5000);
            return false;
        }
        try { return await body().ConfigureAwait(false); }
        catch (Exception ex) { Logger.WriteLine($"{operation} fail: {ex.Message}"); return false; }
        finally { _liquidCoolingLock.Release(); }
    }

    /// <summary>调用方必须已持有 <see cref="_liquidCoolingLock"/>。</summary>
    async Task<bool> PublishAndConfirmLiquidCoolingControlAsync(
        Dictionary<string, object> command,
        Func<bool> targetReached,
        string operation)
    {
        long statusVersionBeforeCommand = _hw.LcStatusVersion;
        await _hw.Publish(MqttTopics.BtLcControl, command);
        bool confirmed = await ConfirmLiquidCoolingStateAsync(
            statusVersionBeforeCommand, targetReached, operation);
        Logger.WriteLine($"{operation} confirmed={confirmed}");
        return confirmed;
    }

    async Task<bool> ConfirmLiquidCoolingStateAsync(
        long statusVersionBeforeCommand,
        Func<bool> targetReached,
        string operation)
    {
        bool IsConfirmedAfterCommand() =>
            _hw.LcStatusVersion > statusVersionBeforeCommand && targetReached();

        if (IsConfirmedAfterCommand()) return true;
        if (await _hw.WaitForStateAsync(IsConfirmedAfterCommand, TimeSpan.FromMilliseconds(180))) return true;

        // A status packet already in flight can describe the profile before this write.
        // Query twice at most; never repeat the physical control command.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (IsConfirmedAfterCommand()) return true;

            long statusVersionBeforeQuery = _hw.LcStatusVersion;
            await _hw.Publish(MqttTopics.BtLcControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
            bool receivedFreshStatus = await _hw.WaitForStateAsync(
                () => _hw.LcStatusVersion > statusVersionBeforeQuery,
                TimeSpan.FromMilliseconds(attempt == 0 ? 500 : 700));
            if (!receivedFreshStatus) continue;
            if (targetReached()) return true;

            Logger.WriteLine($"{operation} status mismatch: expected command state was not reported");
        }

        return false;
    }

    /// <summary>液冷蓝牙连接（原版协议：BT_LC/Control Connect）。</summary>
    public Task<bool> LcConnect() =>
        RunLiquidCoolingAsync("LcConnect", LiquidCoolingWriteLockTimeout, async () =>
        {
            // BT_LC is a runtime-discovered subsystem on several models. Do not reject the
            // initial discovery request merely because an older ItemSupport profile lacks it.
            if (!_hw.IsConnected) return false;
            await ArmLiquidCoolingTargetAsync();
            await _hw.Publish(MqttTopics.BtLcControl, new Dictionary<string, object> { ["Action"] = "Connect" });
            Logger.WriteLine("LcConnect 已发送");
            return true;
        });

    Task PublishLcDeviceMacAsync(string mac) =>
        _hw.Publish(MqttTopics.BtLcControl, new Dictionary<string, object> { ["Action"] = "DeviceMacSetting", ["DeviceMac"] = mac });

    /// <summary>
    /// 官方 UI 的实测连接序列是「先选定目标设备再 Connect」：目标未选定（DevMACString 为空）时
    /// Connect 没有设备可连，状态会永远停在 IsConnectable。这里按状态里的 DeviceMacList 原样下发
    /// DeviceMacSetting——必须是 GCU 给出的完整字符串（BluetoothLE#BluetoothLE&lt;addr&gt;-&lt;addr&gt;），
    /// 不能换成裸 MAC。已选定目标时不重复 arm。
    /// </summary>
    async Task ArmLiquidCoolingTargetAsync()
    {
        if (!string.IsNullOrWhiteSpace(_hw.LcCurrentMac) || _hw.LcDeviceMacs.Count == 0) return;
        string mac = _hw.LcDeviceMacs[0];
        await PublishLcDeviceMacAsync(mac);
        Logger.WriteLine($"LcSelectDevice({mac}) 已发送");
    }

    /// <summary>Requests a BT_LC status snapshot for runtime discovery and post-command readback.</summary>
    public Task<bool> RefreshLiquidCoolingStatus() =>
        RunLiquidCoolingAsync("RefreshLiquidCoolingStatus", LiquidCoolingQueryLockTimeout, async () =>
        {
            if (!_hw.IsConnected) return false;
            await _hw.Publish(MqttTopics.BtLcControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
            return true;
        });

    /// <summary>液冷蓝牙断开（原版 Disconnect）。</summary>
    public Task<bool> LcDisconnect() =>
        RunLiquidCoolingAsync("LcDisconnect", LiquidCoolingWriteLockTimeout, async () =>
        {
            if (!_hw.SupportsLiquidCooling) return false;
            await _hw.Publish(MqttTopics.BtLcControl, new Dictionary<string, object> { ["Action"] = "Disconnect" });
            Logger.WriteLine("LcDisconnect 已发送");
            return true;
        });

    /// <summary>选择要连接的液冷设备（原版 DeviceMacSetting）。</summary>
    public Task<bool> LcSelectDevice(string mac) =>
        RunLiquidCoolingAsync($"LcSelectDevice({mac})", LiquidCoolingWriteLockTimeout, async () =>
        {
            if (!_hw.SupportsLiquidCooling || string.IsNullOrWhiteSpace(mac)) return false;
            await PublishLcDeviceMacAsync(mac);
            Logger.WriteLine($"LcSelectDevice({mac}) 已发送");
            return true;
        });

    /// <summary>清除已记忆的液冷设备（原版 ClearDevMAC）。</summary>
    public Task<bool> LcClearMac() =>
        RunLiquidCoolingAsync("LcClearMac", LiquidCoolingWriteLockTimeout, async () =>
        {
            if (!_hw.SupportsLiquidCooling) return false;
            await _hw.Publish(MqttTopics.BtLcControl, new Dictionary<string, object> { ["Action"] = "ClearDevMAC" });
            Logger.WriteLine("LcClearMac 已发送");
            return true;
        });

    /// <summary>水冷灯光效果（原版 BT_LC LED 协议）：
    /// 1=呼吸(LEDBreathing) 2=多彩(LEDColorful) 4=旋转色(FanLEDEffect) 5=彩虹(FanLEDEffect) 0=全部关闭。</summary>
    public async Task<bool> LcLight(int ledMode)
    {
        try
        {
            if (!CanControlLiquidCooling || ledMode is not (0 or 1 or 2 or 4 or 5)) return false;
            bool ok = ledMode switch
            {
                1 => await LcSetBreathingAsync(true),
                2 => await LcSetColorfulAsync(true),
                4 => await LcSetFanLightEffectAsync(4),
                5 => await LcSetFanLightEffectAsync(5),
                0 => await LcDisableAllLightEffectsAsync(),
                _ => false,
            };
            if (ok) Logger.WriteLine($"LcLight({ledMode}) 已发送");
            return ok;
        }
        catch (Exception ex) { Logger.WriteLine("LcLight fail: " + ex.Message); return false; }
    }

    /// <summary>
    /// Maps the liquid-cooling menu profiles to the official BT_LC lighting commands. This route
    /// is used when GCU owns the peripheral; direct BLE uses WaterCoolerBle frames instead.
    /// </summary>
    public Task<bool> LcApplyLightProfile(
        string profile,
        Color color,
        bool persist = true,
        bool requireReadback = false) =>
        RunLiquidCoolingAsync($"LcApplyLightProfile({profile})", LiquidCoolingWriteLockTimeout, async () =>
        {
            if (!CanControlLiquidCooling) return false;
            Color effectiveColor = color.IsEmpty ? Color.FromArgb(0, 255, 255) : color;
            long statusVersionBeforeCommand = _hw.LcStatusVersion;
            bool lightingReadbackWasSeen = _hw.LcLightingStatusSeen;
            bool ok = profile switch
            {
                WaterCoolerBle.LightCyanStatic => await LcApplyStaticHeadLightAsync(Color.FromArgb(0, 255, 255)),
                WaterCoolerBle.LightCyanBreath => await LcApplyBreathingHeadLightAsync(Color.FromArgb(0, 255, 255)),
                WaterCoolerBle.LightCustomStatic => await LcApplyStaticHeadLightAsync(effectiveColor),
                WaterCoolerBle.LightCustomBreath => await LcApplyBreathingHeadLightAsync(effectiveColor),
                WaterCoolerBle.LightColorful => await LcApplyColorfulHeadLightAsync(breathing: false),
                WaterCoolerBle.LightColorfulBreath => await LcApplyColorfulHeadLightAsync(breathing: true),
                WaterCoolerBle.LightFanRotate => await LcSetFanLightEffectAsync(4),
                WaterCoolerBle.LightFanRainbow => await LcSetFanLightEffectAsync(5),
                WaterCoolerBle.LightOff => await LcDisableAllLightEffectsAsync(),
                _ => false,
            };
            if (!ok) return false;

            bool readbackVerified = false;
            if (lightingReadbackWasSeen || requireReadback)
            {
                readbackVerified = await ConfirmLiquidCoolingStateAsync(
                    statusVersionBeforeCommand,
                    () => LiquidCoolingLightProfileMatches(profile, effectiveColor),
                    $"LcApplyLightProfile({profile})");
                if (!readbackVerified && _hw.LcLightingStatusSeen)
                {
                    // The firmware reports LED state and a fresh readback kept
                    // disagreeing: it ignored the write. Fail loudly.
                    Logger.WriteLine($"LcApplyLightProfile({profile}) lighting readback not confirmed");
                    return false;
                }
            }
            if (!readbackVerified)
            {
                // Statusless lighting firmware (e.g. LCT22002 v2.0.0.4) never reports
                // LED mode fields, so a readback can never be satisfied: the write is
                // unverifiable, not failed. Keep its send-only compatibility, but make
                // the lack of proof explicit.
                Logger.WriteLine($"LcApplyLightProfile({profile}) sent without lighting readback");
            }
            if (ok && persist)
            {
                AppConfig.Set("lc_light_profile", profile);
                if (profile is WaterCoolerBle.LightCustomStatic or WaterCoolerBle.LightCustomBreath)
                    AppConfig.Set("lc_light_color", effectiveColor.ToArgb());
            }
            return ok;
        });

    bool LiquidCoolingLightProfileMatches(string profile, Color effectiveColor)
    {
        bool ColorMatches()
        {
            if (_hw.LcLedRed < 0 || _hw.LcLedGreen < 0 || _hw.LcLedBlue < 0) return false;
            int red = ClampLcColor(effectiveColor.R, _hw.LcLedRedMinimum, _hw.LcLedRedMaximum);
            int green = ClampLcColor(effectiveColor.G, _hw.LcLedGreenMinimum, _hw.LcLedGreenMaximum);
            int blue = ClampLcColor(effectiveColor.B, _hw.LcLedBlueMinimum, _hw.LcLedBlueMaximum);
            return _hw.LcLedRed == red && _hw.LcLedGreen == green && _hw.LcLedBlue == blue;
        }
        bool ColorMatchesIfReported()
        {
            bool anyColorReported = _hw.LcLedRed >= 0 || _hw.LcLedGreen >= 0 || _hw.LcLedBlue >= 0;
            return !anyColorReported || ColorMatches();
        }

        return profile switch
        {
            WaterCoolerBle.LightCyanStatic =>
                _hw.LcHeadLightMode == 0 && ColorMatchesIfReported(),
            WaterCoolerBle.LightCustomStatic =>
                _hw.LcHeadLightMode == 0 && ColorMatches(),
            WaterCoolerBle.LightCyanBreath =>
                _hw.LcHeadLightMode == 1 && ColorMatchesIfReported(),
            WaterCoolerBle.LightCustomBreath =>
                _hw.LcHeadLightMode == 1 && ColorMatches(),
            WaterCoolerBle.LightColorful => _hw.LcHeadLightMode == 2,
            WaterCoolerBle.LightColorfulBreath => _hw.LcHeadLightMode == 3,
            WaterCoolerBle.LightFanRotate => _hw.LcFanLightMode == 4,
            WaterCoolerBle.LightFanRainbow => _hw.LcFanLightMode == 5,
            WaterCoolerBle.LightOff =>
                _hw.LcHeadLightMode == 0 && (_hw.LcFanLightMode < 0 || _hw.LcFanLightMode == 0),
            _ => false,
        };
    }

    public Task<bool> LcSetLightColor(Color color) =>
        RunLiquidCoolingAsync("LcSetLightColor", LiquidCoolingWriteLockTimeout,
            () => LcSetLightColorCoreAsync(color));

    /// <summary>
    /// 颜色写入的无锁核心。灯效档位序列已经在 <see cref="RunLiquidCoolingAsync"/> 里持锁，
    /// 序列内部必须调用这个版本；SemaphoreSlim 不可重入，走公开方法会自锁。
    /// </summary>
    async Task<bool> LcSetLightColorCoreAsync(Color color)
    {
        if (!CanControlLiquidCooling) return false;
        int red = ClampLcColor(color.R, _hw.LcLedRedMinimum, _hw.LcLedRedMaximum);
        int green = ClampLcColor(color.G, _hw.LcLedGreenMinimum, _hw.LcLedGreenMaximum);
        int blue = ClampLcColor(color.B, _hw.LcLedBlueMinimum, _hw.LcLedBlueMaximum);
        await _hw.Publish(MqttTopics.BtLcControl, new Dictionary<string, object>
        {
            ["Action"] = "LEDControl",
            ["LCLED_R"] = red.ToString(),
            ["LCLED_G"] = green.ToString(),
            ["LCLED_B"] = blue.ToString(),
        });
        Logger.WriteLine($"LcSetLightColor({red},{green},{blue}) 已发送");
        return true;
    }

    static int ClampLcColor(int value, int minimum, int maximum) =>
        minimum >= 0 && maximum >= minimum
            ? Math.Clamp(Math.Clamp(value, 0, 255), Math.Clamp(minimum, 0, 255), Math.Clamp(maximum, 0, 255))
            : Math.Clamp(value, 0, 255);

    bool CanControlLiquidCooling => _hw.SupportsLiquidCooling &&
        (!_hw.LcStatusSeen ||
         ((!_hw.LcActionSupportReported || _hw.LcActionSupported) &&
          (!_hw.LcConnectionStateReported || _hw.LcGcuControllable)));

    Task<bool> LcSetBreathingAsync(bool enabled) => LcPublishLightActionAsync("LEDBreathing", enabled ? 1 : 0);

    Task<bool> LcSetColorfulAsync(bool enabled) => LcPublishLightActionAsync("LEDColorful", enabled ? 2 : 0);

    Task<bool> LcSetFanLightEffectAsync(int mode) => LcPublishLightActionAsync("FanLEDEffect", mode);

    async Task<bool> LcPublishLightActionAsync(string action, int ledMode)
    {
        try
        {
            if (!CanControlLiquidCooling) return false;
            await _hw.Publish(MqttTopics.BtLcControl, new Dictionary<string, object>
            {
                ["Action"] = action,
                ["LedMode"] = ledMode,
            });
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"{action}({ledMode}) fail: {ex.Message}");
            return false;
        }
    }

    async Task<bool> LcApplyStaticHeadLightAsync(Color color)
    {
        bool colorfulOff = await LcSetColorfulAsync(false);
        bool breathingOff = await LcSetBreathingAsync(false);
        bool colorSet = await LcSetLightColorCoreAsync(color);
        return colorfulOff && breathingOff && colorSet;
    }

    async Task<bool> LcApplyBreathingHeadLightAsync(Color color)
    {
        bool colorfulOff = await LcSetColorfulAsync(false);
        bool colorSet = await LcSetLightColorCoreAsync(color);
        bool breathingOn = await LcSetBreathingAsync(true);
        return colorfulOff && colorSet && breathingOn;
    }

    async Task<bool> LcApplyColorfulHeadLightAsync(bool breathing)
    {
        bool breathingState = breathing
            ? await LcSetColorfulAsync(true)
            : await LcSetBreathingAsync(false);
        bool colorfulOn = breathing
            ? await LcSetBreathingAsync(true)
            : await LcSetColorfulAsync(true);
        return breathingState && colorfulOn;
    }

    async Task<bool> LcDisableAllLightEffectsAsync()
    {
        bool breathingOff = await LcSetBreathingAsync(false);
        bool colorfulOff = await LcSetColorfulAsync(false);
        bool fanOff = await LcSetFanLightEffectAsync(0);
        return breathingOff && colorfulOff && fanOff;
    }

    /// <summary>液冷补水（原版 InputWater 命令）。</summary>
    public Task<bool> LcInputWater() =>
        RunLiquidCoolingAsync("LcInputWater", LiquidCoolingWriteLockTimeout, async () =>
        {
            if (!_hw.SupportsLiquidCooling) return false;
            await _hw.Publish(MqttTopics.BtLcControl, new Dictionary<string, object> { ["Action"] = "InputWater" });
            Logger.WriteLine("LcInputWater 已发送");
            return true;
        });

    /// <summary>官方灯效命令的 function 字段值（N9-2：亮度只能随它打包下发）。</summary>
    internal const string KeyboardEffectFunction = "SetEffectALL";

    /// <summary>官方灯效命令的亮度字段名（0–4 档）。</summary>
    internal const string KeyboardEffectLightField = "light";

    /// <summary>官方键盘灯效果（原版 Keyboard/Ctrl SetEffectALL 协议，固件执行）：
    /// effect=效果名（Single/Breathing/Wave/Rainbow...），light=亮度 0-4，speed=速度，direction=方向。</summary>
    public Task<bool> SetKeyboardEffect(string effect, int light = 4, int speed = 1, string direction = "None",
        Color? singleColor = null, bool save = false)
        => SetLightEffect(MqttTopics.KeyboardCtrl, effect, light, speed, direction, singleColor, save);

    /// <summary>
    /// 回退（GCU）通道的亮度载体（设计 §4）：协议没有独立的「设亮度」命令——light 只能随
    /// SetEffectALL 打包下发。重发 GCU 当前回报的效果名 + 新的 light 档位（0–4），效果因此被保留，
    /// 线上也不会出现中文显示名；回报为空/未知时用唯一规范默认 "Single"。
    /// </summary>
    public Task<bool> SetKeyboardBrightnessPreservingEffect(int level0to4)
    {
        string effect;
        int speed = 1;
        Color? singleColor = null;
        if (LightingSettingsStore.TryLoad(MqttTopics.KeyboardCtrl, out LightChannelSettings settings)
            && KeyboardFirmwareEffects.Contains(settings.Effect))
        {
            effect = settings.Effect;
            speed = settings.Speed;
            singleColor = LightingSettingsStore.ColorForEffect(effect, settings.ColorArgb);
        }
        else
            effect = string.IsNullOrWhiteSpace(_hw.KeyboardEffect) ? "Single" : _hw.KeyboardEffect;
        return SetLightEffect(MqttTopics.KeyboardCtrl, effect, light: level0to4, speed, "None", singleColor);
    }

    /// <summary>通用灯效命令（键盘/灯条/Logo 灯共用 MyKeyBoard 载荷结构，仅 topic 不同）。
    /// save=true 时 nv_save=SAVE——效果一次性写入固件 NVRAM（重启/断电后保持）。</summary>
    public async Task<bool> SetLightEffect(string topic, string effect, int light = 4, int speed = 1, string direction = "None", Color? singleColor = null, bool save = true)
    {
        try
        {
            if (!SupportsLightTopic(topic)) return false;
            // 每个色块都带 ID：服务端 ConvertJsonRGB2RGBColor 逐块读 ColorBuffer[i]["ID"] 赋给 uint，
            // 缺 ID 时 dynamic 转换会抛（HIDKeyboard.cs:179-192）。官方界面发的色块一律带 ID。
            Color[] slots = singleColor.HasValue
                ? new[] { singleColor.Value }
                : LightingEffectCatalog.DefaultPalette;   // 原版默认 7 色块（rkgcolor）
            var color = new Dictionary<string, object>
            {
                ["isCircular"] = true,
                ["ColorBlocks"] = slots.Length,
                ["ColorBuffer"] = slots.Select((c, i) => (object)new Dictionary<string, object>
                {
                    ["ID"] = i, ["R"] = (int)c.R, ["G"] = (int)c.G, ["B"] = (int)c.B,
                }).ToArray(),
            };
            var payload = new Dictionary<string, object>
            {
                ["function"] = "SetEffectALL",
                ["mode"] = "Lighting",
                ["speed"] = speed.ToString(),
                ["light"] = light.ToString(),
                ["effect"] = effect,
                ["direction"] = direction,
                ["nv_save"] = save ? "SAVE" : "NOT_SAVE",
                ["color"] = color,
            };
            // EC 单区键盘的服务端解析要求一组额外字段，缺任何一个整条命令被丢弃。
            if (topic == MqttTopics.KeyboardCtrl && _hw.Lighting.Keyboard == KeyboardLightKind.SingleZone)
                foreach (var (key, value) in LightingEffectCatalog.SingleZoneFields(slots[0]))
                    payload[key] = value;
            await _hw.Publish(topic, payload);
            Logger.WriteLine($"SetLightEffect({topic}, {effect}, light={light}, speed={speed}, save={save}) 已发送");
            return true;
        }
        catch (Exception ex) { Logger.WriteLine("SetLightEffect fail: " + ex.Message); return false; }
    }

    /// <summary>键盘灯开关（原版 SetPower powerstatus 0/1）。</summary>
    public Task<bool> SetKeyboardPower(bool on)
        => SetLightPower(MqttTopics.KeyboardCtrl, on);

    /// <summary>
    /// 灯效通道上电/断电的「只下发」语义：返回命令是否成功发布，**不等待厂商回读**。
    ///
    /// 恢复/熄灯周期必须用它把命令立即发出——本地 HID 与 GCU 通道在同一周期落地。
    /// 回读确认是慢且可能永远不来的外部事实（本机键盘主题长期 <c>not confirmed</c>），
    /// 绝不能拿它当可见结果的闸门，否则「命令已下发、灯已亮」也会被判定成失败并触发重发。
    /// </summary>
    public async Task<bool> PublishLightPower(string topic, bool on)
    {
        try
        {
            if (!SupportsLightTopic(topic)) return false;
            await _hw.Publish(topic, new Dictionary<string, object> { ["function"] = "SetPower", ["powerstatus"] = on ? 1 : 0 });
            return true;
        }
        catch (Exception ex) { Logger.WriteLine("PublishLightPower fail: " + ex.Message); return false; }
    }

    /// <summary>
    /// 等待厂商回读确认某条通道的电源态（best-effort）：先做 180ms 快速等待，再最多 3 次 GETSTATUS。
    ///
    /// 只供 UI / 校验等确实需要回显的调用方使用。恢复周期只把它当遥测
    /// （<see cref="ObserveLightPower"/>），绝不在落地效果前 await 它。
    /// 确认逻辑与 <c>SupportsLightTopic</c> 共用 <c>LightTopicToQuickSwitchKey</c> 一份映射，
    /// 不许再写第二份。
    /// </summary>
    public async Task<bool> ConfirmLightPower(string topic, bool on)
    {
        try
        {
            if (!SupportsLightTopic(topic)) return false;
            string? lightKey = LightTopicToQuickSwitchKey(topic);
            bool IsConfirmed() => topic.StartsWith(MqttTopics.KeyboardPrefix, StringComparison.OrdinalIgnoreCase)
                ? _hw.KeyboardPower == on
                : lightKey is not null &&
                  _hw.QuickSwitches.TryGetValue(lightKey, out bool actual) && actual == on;
            if (await _hw.WaitForStateAsync(IsConfirmed, TimeSpan.FromMilliseconds(180))) return true;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await _hw.Publish(topic, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                if (await _hw.WaitForStateAsync(IsConfirmed, TimeSpan.FromMilliseconds(attempt == 0 ? 500 : 700)))
                {
                    Logger.WriteLine($"SetLightPower({topic}, {on}) confirmed");
                    return true;
                }
            }
            Logger.WriteLine($"SetLightPower({topic}, {on}) not confirmed");
            return false;
        }
        catch (Exception ex) { Logger.WriteLine("ConfirmLightPower fail: " + ex.Message); return false; }
    }

    /// <summary>遥测专用后台确认：立即返回；确认与否只写日志，绝不参与周期成败判定。</summary>
    public void ObserveLightPower(string topic, bool on) => _ = Task.Run(() => ConfirmLightPower(topic, on));

    /// <summary>通用灯开关（键盘/灯条/Logo 灯 SetPower）。UI/校验路径：下发后等待回读确认。</summary>
    public async Task<bool> SetLightPower(string topic, bool on)
    {
        if (!await PublishLightPower(topic, on).ConfigureAwait(false)) return false;
        return await ConfirmLightPower(topic, on).ConfigureAwait(false);
    }

    /// <summary>
    /// 恢复/熄灯周期的「只下发 + 后台遥测确认」唯一入口：命令发布后立即返回，回读降级为遥测。
    ///
    /// 调用点不得再各自拼 <see cref="PublishLightPower"/> + <see cref="ObserveLightPower"/>——
    /// 协议只有这一份，「每个逻辑状态变化只下发一次」才不会被散落的调用点破坏
    /// （同一灯态重复下发会让固件重初始化/闪烁）。
    /// </summary>
    public async Task<bool> IssueLightPower(string topic, bool on)
    {
        bool issued = await PublishLightPower(topic, on).ConfigureAwait(false);
        ObserveLightPower(topic, on);
        return issued;
    }

    /// <summary>
    /// 灯效通道的裸状态查询（GETSTATUS）。UI 打开灯效窗口时请求一次状态；
    /// 与 <see cref="ConfirmLightPower"/> 的补发查询共用同一个服务出口，UI 不得再直接触碰
    /// <c>hw.Publish</c>。不检查通道是否受支持——与调用点原语义一致（连接态由调用点把关）。
    /// </summary>
    public async Task RequestLightStatus(string topic)
    {
        try
        {
            await _hw.Publish(topic, new Dictionary<string, object> { ["Action"] = "GETSTATUS" }).ConfigureAwait(false);
        }
        catch (Exception ex) { Logger.WriteLine("RequestLightStatus fail: " + ex.Message); }
    }

    /// <summary>深度睡眠开关；secs&gt;0 时携带定时（900-1800s），仅开启状态生效。</summary>
    /// <summary>
    /// 深度睡眠的真实结果。服务端对这项的回读要等重启才更新（真机实测），所以「命令已送达但
    /// 暂无回读」是正常结果，必须和「命令根本没发出去」区分开——后者不能提示「重启后生效」。
    /// </summary>
    public enum DeepSleepResult { Failed, SentPendingRestart, Confirmed }

    public async Task<DeepSleepResult> SendDeepSleepAsync(bool on)
    {
        if (_hw is not { IsConnected: true } || !_hw.DeepSleepSeen) return DeepSleepResult.Failed;
        try
        {
            await _hw.Publish(MqttTopics.SettingControl,
                new Dictionary<string, object> { ["Action"] = on ? "DEEPSLEEP_ON" : "DEEPSLEEP_OFF" }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("SendDeepSleep publish failed: " + ex.Message);
            return DeepSleepResult.Failed;
        }
        bool confirmed = await ConfirmSettingAsync(() =>
            _hw.QuickSwitches.TryGetValue("deepsleep", out bool actual) && actual == on).ConfigureAwait(false);
        Logger.WriteLine($"SendDeepSleep({on}) confirmed={confirmed}");
        return confirmed ? DeepSleepResult.Confirmed : DeepSleepResult.SentPendingRestart;
    }

    public async Task<bool> SwitchDeepSleep(bool on, int secs = 0)
    {
        try
        {
            if (!_hw.DeepSleepSeen || secs != 0 && secs is not (900 or 1200 or 1500 or 1800)) return false;
            var payload = new Dictionary<string, object> { ["Action"] = on ? "DEEPSLEEP_ON" : "DEEPSLEEP_OFF" };
            if (on && secs > 0) payload["Secs"] = secs.ToString();
            await _hw.Publish(MqttTopics.SettingControl, payload);
            bool confirmed = await ConfirmSettingAsync(() =>
                _hw.QuickSwitches.TryGetValue("deepsleep", out bool actual) && actual == on
                && (!on || secs <= 0 || _hw.DeepSleepTime == secs));
            Logger.WriteLine($"SwitchDeepSleep({on}, secs={secs}) confirmed={confirmed}");
            return confirmed;
        }
        catch (Exception ex) { Logger.WriteLine("SwitchDeepSleep fail: " + ex.Message); return false; }
    }

    /// <summary>狂暴（Turbo）子模式切换：silent=静音狂暴(SILENT=1)，否则超频狂暴(EXTREME=1)。
    /// 原版协议：Fan/Control SET_CPU_CORE_OFFSET_SILENT/EXTREME；当前子模式存注册表 SilentPerformanceModeSwitch（1=超频 0=静音）。</summary>
    public async Task<bool> SwitchTurboSubMode(bool silent)
    {
        try
        {
            if (_hw is not { IsConnected: true } || _hw.Capabilities.SilentTurboAvailability != FeatureAvailability.Supported) return false;
            if (IsSilentTurboActive == silent)
            {
                if (MechrevoLite.Mode.PerfModeService.TurboAutoOcEnabled() && ShouldApplyTurboGpuOverclockDefaultsOnSubMode(silent))
                    await ApplyTurboGpuOverclockDefaults().ConfigureAwait(false);
                return true;
            }
            var action = silent ? "SET_CPU_CORE_OFFSET_SILENT" : "SET_CPU_CORE_OFFSET_EXTREME";
            await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object>
            {
                ["Action"] = action,
                [silent ? "SILENT" : "EXTREME"] = 1,
            });
            for (int attempt = 0; attempt < 25; attempt++)
            {
                await Task.Delay(100);
                if (IsSilentTurboActive == silent)
                {
                    Logger.WriteLine($"SwitchTurboSubMode(silent={silent}) confirmed");
                    if (MechrevoLite.Mode.PerfModeService.TurboAutoOcEnabled() && ShouldApplyTurboGpuOverclockDefaultsOnSubMode(silent))
                        await ApplyTurboGpuOverclockDefaults().ConfigureAwait(false);
                    return true;
                }
            }
            Logger.WriteLine($"SwitchTurboSubMode(silent={silent}) not confirmed");
            return false;
        }
        catch (Exception ex) { Logger.WriteLine("SwitchTurboSubMode fail: " + ex.Message); return false; }
    }

    /// <summary>当前是否静音狂暴（注册表 SilentPerformanceModeSwitch：0=静音狂暴 1=超频狂暴）。</summary>
    public static bool IsSilentTurboActive
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"SOFTWARE\OEM\GamingCenter2\ItemSupport");
                var v = key?.GetValue("SilentPerformanceModeSwitch");
                return v?.ToString() == "0";
            }
            catch { return false; }
        }
    }

    public async Task<bool> SwitchFanBoost(bool enable)
    {
        try
        {
            if (!_hw.SupportsFanBoost) return false;
            await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = enable ? "FAN_BOOST_ON" : "FAN_BOOST_OFF" });
            if (await _hw.WaitForStateAsync(() => _hw.FanBoost == enable, TimeSpan.FromMilliseconds(180))) return true;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                if (await _hw.WaitForStateAsync(
                    () => _hw.FanBoost == enable,
                    TimeSpan.FromMilliseconds(attempt == 0 ? 500 : 700))) return true;
            }
            Logger.WriteLine($"SwitchFanBoost({enable}) not confirmed, actual={_hw.FanBoost}");
            return false;
        }
        catch (Exception ex) { Logger.WriteLine("SwitchFanBoost fail: " + ex.Message); return false; }
    }

    /// <summary>
    /// 请求全量状态刷新（启动/连接后/遥测超时补发）。
    ///
    /// 与首连时的握手共用同一份序列（<see cref="MechrevoHw.RequestInitialStateAsync"/>）。
    /// 这里过去是第二份手写列表，与首连那份差一条 LCHWOC GETSTATUS——两份并存的话，
    /// 将来加新主题只改一边就会留下「首连缺某个状态、要等恢复流程补」的间歇性症状。
    /// </summary>
    public Task RefreshAll() => _hw.RequestInitialStateAsync();

    bool SupportsLightTopic(string topic)
    {
        // Keyboard/Ctrl 只对服务真有 RGB 控制器的分型生效（逐键/四区/四区单色/单区）；
        // 单色背光走 Setting/Control，逐键一代官方没有控制器——向它们发 Keyboard/Ctrl 是空操作。
        if (topic.StartsWith(MqttTopics.KeyboardPrefix, StringComparison.OrdinalIgnoreCase))
            return _hw.Lighting.KeyboardGcuControllable;
        return LightTopicToQuickSwitchKey(topic) switch
        {
            "logolight" => _hw.SupportsLogoLight,
            "hingelight" => _hw.SupportsHingeLight,
            "synclight" => _hw.SupportsSyncLight,
            "eclightbar" => _hw.SupportsEcLightbar,
            "lightbar" => _hw.SupportsLightbar,
            _ => false,
        };
    }

    /// <summary>
    /// 旧机型 EC 灯带动作（MyRgbLightbar/Control {"Action":…}，MyRgbLightbarManager.Recieve）。
    /// 服务每执行一个动作都会回一帧 MyRgbLightbar/Status，据此判定回读是否与期望一致。
    /// </summary>
    internal async Task<(bool Published, bool Matched)> SendEcLightbarActionAsync(string action, Func<bool> confirmed)
    {
        if (!_hw.SupportsEcLightbar) return (false, false);
        try
        {
            long before = _hw.EcLightbarStatusVersion;
            await _hw.Publish(MqttTopics.EcLightbarControl, new Dictionary<string, object> { ["Action"] = action }).ConfigureAwait(false);
            bool matched = await _hw.WaitForStateAsync(
                () => _hw.EcLightbarStatusVersion > before && confirmed(), TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false);
            Logger.WriteLine($"EC lightbar {action}: confirmed={matched}");
            return (true, matched);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("EC lightbar action failed: " + ex.Message);
            return (false, false);
        }
    }

    /// <summary>灯光通道 Ctrl 主题 → 对应的 Status 主题（回读比对用）。</summary>
    internal static string? StatusTopicFor(string ctrlTopic) => ctrlTopic switch
    {
        MqttTopics.KeyboardCtrl => MqttTopics.KeyboardStatus,
        MqttTopics.LightbarCtrl => MqttTopics.LightbarStatus,
        MqttTopics.LogoLightCtrl => MqttTopics.LogoLightStatus,
        MqttTopics.HingeLightCtrl => MqttTopics.HingeLightStatus,
        MqttTopics.SyncLightCtrl => MqttTopics.SyncLightStatus,
        _ => null,
    };

    /// <summary>服务落盘「上次灯效」用的类型子键（SetACDCLightString 的 forceEffectSaveName / 实际分型名）。</summary>
    string? ServiceRecordTypeKey(string ctrlTopic) => ctrlTopic switch
    {
        MqttTopics.KeyboardCtrl => _hw.KeyboardStatusType,
        MqttTopics.LightbarCtrl => _hw.LightbarStatusType,
        MqttTopics.LogoLightCtrl => "MEZone_Lighbar4_logo",
        MqttTopics.HingeLightCtrl => "MEZone_Lighbar4_hinge",
        MqttTopics.SyncLightCtrl => "MEZone_Lighbar4_sync",
        _ => null,
    };

    /// <summary>
    /// 官方通道灯效：下发 SetEffectALL 后尽力回读，按证据给出三态结果。
    /// 证据顺序：键盘先看设备 0x88 回读（固件效果寄存器，真硬件回读）；
    /// 其余通道（以及拿不到 HID 回读的键盘）看服务回读——GETSTATUS 的新帧里电源为 On 且亮度档一致，
    /// 并且服务落盘的上次灯效（HKLM ...\RGBKeyboard\&lt;类型&gt;\&lt;ProjectID&gt;_LastEffect）效果号一致。
    /// 任一证据缺失或不一致都只报「已下发」，绝不冒充「已确认」。
    /// </summary>
    internal async Task<(LightApplyOutcome Outcome, LightReadbackSource Source)> ApplyLightEffectConfirmedAsync(
        string topic, string effect, int light, int speed, string direction, Color? singleColor, bool save,
        bool brightnessApplies = true, Func<byte[]?>? firmwareReadback = null, Action? onPublished = null)
    {
        bool published = await SetLightEffect(topic, effect, light, speed, direction, singleColor, save).ConfigureAwait(false);
        if (!published) return (LightApplyOutcome.Failed, LightReadbackSource.None);
        // 命令已发出：调用方在这里落盘用户选择。回读要等 ~0.35–1.8 s，期间仪表盘的同步
        // 会按存档重填下拉——存档若还是旧效果，下拉会跳回去，下一次操作也会带着旧效果。
        onPublished?.Invoke();
        int expectedEffect = LightingEffectCatalog.FirmwareEffectId(effect);
        try
        {
            await Task.Delay(350).ConfigureAwait(false);   // 服务执行效果（含 Lighbar4 单色的 500ms 双发）后再读
            if (topic == MqttTopics.KeyboardCtrl && firmwareReadback is not null && expectedEffect > 0)
            {
                byte[]? report = await Task.Run(firmwareReadback).ConfigureAwait(false);
                if (report is { Length: >= 6 } && report[1] == 0x88)
                {
                    bool effectMatches = report[3] == expectedEffect;
                    Logger.WriteLine($"Light readback (device 0x88) {topic}: effect={report[3]} expected={expectedEffect} light={report[5]}");
                    if (effectMatches) return (LightApplyOutcome.Confirmed, LightReadbackSource.Device);
                }
            }
            bool serviceMatched = await ServiceReadbackMatchesAsync(topic, expectedEffect, light, brightnessApplies).ConfigureAwait(false);
            return serviceMatched
                ? (LightApplyOutcome.Confirmed, LightReadbackSource.Service)
                : (LightApplyOutcome.Sent, LightReadbackSource.None);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Light readback failed: " + ex.Message);
            return (LightApplyOutcome.Sent, LightReadbackSource.None);
        }
    }

    async Task<bool> ServiceReadbackMatchesAsync(string topic, int expectedEffect, int light, bool brightnessApplies)
    {
        string? statusTopic = StatusTopicFor(topic);
        if (statusTopic is null) return false;
        long before = _hw.LightStatusReadback.TryGetValue(statusTopic, out LightStatusSnapshot old) ? old.Version : 0;
        bool StatusMatches()
        {
            if (!_hw.LightStatusReadback.TryGetValue(statusTopic, out LightStatusSnapshot s) || s.Version <= before) return false;
            bool powerOn = string.Equals(s.PowerStatus, "On", StringComparison.OrdinalIgnoreCase);
            return powerOn && (!brightnessApplies || s.BrightnessLevel < 0 || s.BrightnessLevel == Math.Clamp(light, 0, 4));
        }
        bool statusOk = false;
        for (int attempt = 0; attempt < 2 && !statusOk; attempt++)
        {
            await _hw.Publish(topic, new Dictionary<string, object> { ["Action"] = "GETSTATUS" }).ConfigureAwait(false);
            statusOk = await _hw.WaitForStateAsync(StatusMatches, TimeSpan.FromMilliseconds(700)).ConfigureAwait(false);
        }
        if (!statusOk) return false;
        string? typeKey = ServiceRecordTypeKey(topic);
        bool recordOk = typeKey is not null && expectedEffect > 0
            && GcuLightingReadback.TryReadLastEffect(typeKey, out GcuLightEffectRecord record)
            && record.Effect == expectedEffect;
        Logger.WriteLine($"Light readback (service) {topic}: status=ok record={(recordOk ? "match" : "no-match")} type={typeKey}");
        return recordOk;
    }

    /// <summary>
    /// 灯带主题 → <c>QuickSwitches</c> 里的键。
    ///
    /// 这层映射必须只有一份。<see cref="SetLightPower"/> 的确认逻辑过去自己写过一个
    /// 独立的三元表达式，把子灯带都算成了 <c>"lightbar"</c>——于是开关某条子灯带时，
    /// 确认看的是**主灯带**的状态。真机验证里的表现是
    /// 「回读确实变了，但命令确认=False」，然后白跑三轮 GETSTATUS 重试。
    ///
    /// 注意顺序：子灯带（Logo）的主题以 <c>HidLightbar_</c> 开头，
    /// 所以必须先判子灯带，最后才能落到主灯带。
    /// </summary>
    internal static string? LightTopicToQuickSwitchKey(string topic)
    {
        if (topic.Contains("Logo", StringComparison.OrdinalIgnoreCase)) return "logolight";
        if (topic.Contains("Hinge", StringComparison.OrdinalIgnoreCase)) return "hingelight";
        if (topic.Contains("_Sync", StringComparison.OrdinalIgnoreCase)) return "synclight";
        if (topic.StartsWith("MyRgbLightbar/", StringComparison.OrdinalIgnoreCase)) return "eclightbar";
        if (topic.StartsWith(MqttTopics.LightbarPrefix, StringComparison.OrdinalIgnoreCase)) return "lightbar";
        return null;
    }
}
