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
    Requested,
    Unsupported,
    Failed,
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
    internal static int HotSwitchStatusPollLimit { get; set; } = 61;
    internal const int HotSwitchStatusRetryEveryPolls = 4;

    internal static bool ShouldRetryHotSwitchPoll(int poll) =>
        poll >= 0 && poll % HotSwitchStatusRetryEveryPolls == 0;

    public event Action<int>? ModeChanged;        // 模式变化（G-Helper 枚举：0=游戏 1=增强 2=办公 3=自定义）
    public event Action<int>? GpuModeChanged;     // 显卡模式变化（0=核显 1=标准 2=直连 3=自动）
    public event Action? DataChanged;             // 遥测刷新

    readonly SemaphoreSlim _switchLock = new(1, 1);   // 切换串行化：防快速连续切换的确认交叉
    readonly SemaphoreSlim _settingLock = new(1, 1);
    readonly SemaphoreSlim _colorCalibrationLock = new(1, 1);
    readonly SemaphoreSlim _liquidCoolingLock = new(1, 1);
    CancellationTokenSource? _modeSwitchCts;
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
    static int OpToMode(int op) => op switch
    {
        0 => ModeOffice,
        1 => ModeGaming,
        2 => ModeTurbo,
        3 => ModeCustom,
        _ => ModeGaming,
    };

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
            int expectedOperatingMode = mode switch
            {
                ModeOffice => 0,
                ModeGaming => 1,
                ModeTurbo => 2,
                ModeCustom => 3,
                _ => 1,
            };
            _hw.MarkModeSwitchPending(expectedOperatingMode);
            // 载荷与 MechrevoHw.SetMode 共用一份构造：ProfileIndex 是 JSON 数字（不是 "0"），
            // 而且切模式必须紧跟一条 LCHWOC/Control 的运行标记
            // （官方 ModeSwitchCommand，CCUWinUI L55090-55146）。
            // 过去只在 SwitchCustomProfile 里发 IsCustomRun、从不发 IsNormalRun，
            // 于是从自定义模式切回普通模式时超频通道还留在「自定义运行」状态，
            // 硬件那边的超频参数不复位。
            var (fanPayload, overclockPayload) = MechrevoHw.BuildModeSwitchPayloads(
                action, expectedOperatingMode,
                mode == ModeCustom ? Math.Clamp(_hw.CustomProfileIndex, 0, 3) : 0,
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

    /// <summary>切换自定义性能档（原版序列：OPERATING_CUSTOM_MODE + LCHWOC IsCustomRun + 刷新曲线设置 + 回读）。</summary>
    public async Task<bool> SwitchCustomProfile(int index)
    {
        try
        {
            if (!await AcquireSwitchLockAsync($"SwitchCustomProfile({index})", CancellationToken.None))
                return false;
            try
            {
            if (index is < 0 or > 3) return false;
            if (_hw is not { IsConnected: true }) return false;
            _hw.SuspendDirectGpuOverclock();
            _hw.MarkModeSwitchPending(3);
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
            bool confirmed = await _hw.WaitForStateAsync(
                () => _hw.OperatingMode == 3 && _hw.CustomProfileIndex == index,
                TimeSpan.FromMilliseconds(250));
            if (!confirmed)
            {
                await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                confirmed = await _hw.WaitForStateAsync(
                    () => _hw.OperatingMode == 3 && _hw.CustomProfileIndex == index,
                    TimeSpan.FromMilliseconds(900));
            }
            Logger.WriteLine($"SwitchCustomProfile({index}) confirmed={confirmed} actualMode={_hw.OperatingMode} actualProfile={_hw.CustomProfileIndex}");
            if (confirmed)
            {
                long initialFanStatus = _hw.FanStatusVersion;
                await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                await _hw.WaitForStateAsync(
                    () => _hw.CustomProfileIndex == index && _hw.FanStatusVersion > initialFanStatus,
                    TimeSpan.FromMilliseconds(900));
                _hw.NotifyCustomModeChanged();
                _hw.RestoreDirectGpuOverclockProfile(index);
                AppConfig.Set("custom_last_profile", index);
                AppConfig.Flush();
                MechrevoLite.Mode.ModeControl.SyncExternalModeStatic(ModeCustom);
                ModeChanged?.Invoke(ModeCustom);
            }
            return confirmed;
            }
            finally { _switchLock.Release(); }
        }
        catch (Exception ex) { Logger.WriteLine("SwitchCustomProfile fail: " + ex.Message); return false; }
    }

    /// <summary>设置当前自定义档参数（SET_OPERATING_MODE_DETAIL，服务端保存到当前档）。
    /// 实测：同包多字段时服务端只应用部分字段——必须逐字段单独发包（与原版 UI 每命令单字段一致）。</summary>
    public async Task<bool> SetCustomDetail(Dictionary<string, string> fields)
    {
        try
        {
            if (!await AcquireSwitchLockAsync("SetCustomDetail", CancellationToken.None))
                return false;
            try
            {
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
                    TimeSpan.FromMilliseconds(400));
            if (!confirmed)
            {
                await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" });
                confirmed = await _hw.WaitForStateAsync(
                    () => gcuConfirmationEntries.All(field => GcuCustomFieldMatches(field.Key, field.Value)),
                    TimeSpan.FromMilliseconds(2200));
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
                            TimeSpan.FromMilliseconds(6000));
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
            finally { _switchLock.Release(); }
        }
        catch (Exception ex) { Logger.WriteLine("SetCustomDetail fail: " + ex.Message); return false; }
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
            if (expectedOn && _readHdrEnabled())
            {
                Logger.WriteLine($"SetColorCalibration(mode {mode}) blocked: HDR is enabled");
                return false;
            }

            int currentMode = ReadColorCalibrationMode();
            bool currentOn = ReadColorCalibrationOn();
            if (currentOn == expectedOn && (!expectedOn || currentMode == mode))
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
            bool stateMatches = expectedOn ? actualMode == mode : actualOn == expectedOn;
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
    public async Task<bool> SetCustomProfileName(string name)
    {
        try
        {
            if (_hw is not { IsConnected: true }) return false;
            await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "SET_CUSTOM_PROFILE_OSD_STRING", ["ProfileName"] = name });
            return true;
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

    internal static Dictionary<string, object> CreateGpuSwitchPayload(
        int mode,
        bool supportsDgpuDirect,
        bool supportsIgpuOnly,
        bool useHotSwitch) => mode == GpuIGpu && supportsDgpuDirect && !useHotSwitch
            && !supportsIgpuOnly
            ? new() { ["Action"] = "DGPU_DIRECT_CONNECT_TOGGLE_IGPU" }
            : CreateGpuModePayload(mode);

    /// <summary>切换显卡模式：使用核显-only 与 MUX 两套独立协议，随后回读确认。</summary>
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
            if (_hw is not { IsConnected: true } || mode is < GpuIGpu or > GpuAuto || !_hw.CanSwitchGpuMode(mode))
                return GpuRestartRequestOutcome.Failed;

            lockTaken = await AcquireSwitchLockAsync(
                $"RequestGpuModeRestartAsync({mode})", requestCts.Token).ConfigureAwait(false);
            if (!lockTaken) return GpuRestartRequestOutcome.Failed;

            IReadOnlyList<Dictionary<string, object>> route = GpuRestartRouteOverride is { } factory
                ? factory(mode, _hw.SupportsDgpuDirect)
                : CreateGpuRestartTargetPayloads(mode, _hw.SupportsDgpuDirect);
            // Fail closed：空路由意味着没有任何模式会被写进 EC。过去这里仍会发布
            // DGPU_DIRECT_CONNECT_RESTART，GCU 照单重启一次——模式没变，用户白重启
            // （真机证据：40 系 target=0/1 时 `GPU restart route payloads [] sent`）。宁可不动，也不空转重启。
            if (route.Count == 0)
            {
                Logger.WriteLine(
                    $"GPU restart route is empty for target={mode} " +
                    $"(supportsDgpuDirect={_hw.SupportsDgpuDirect}, supportsIgpuOnly={_hw.SupportsIgpuOnly}); " +
                    "refusing to publish DGPU_DIRECT_CONNECT_RESTART so the machine is not rebooted for nothing.");
                return GpuRestartRequestOutcome.Unsupported;
            }
            List<string> sentActions = new();
            foreach (Dictionary<string, object> payload in route)
            {
                requestCts.Token.ThrowIfCancellationRequested();
                sentActions.Add(payload["Action"].ToString() ?? "?");
                await _hw.Publish(MqttTopics.SettingControl, payload).ConfigureAwait(false);
            }
            Logger.WriteLine($"GPU restart route payloads [{string.Join(" -> ", sentActions)}] sent for target={mode}");

            await Task.Delay(800, requestCts.Token).ConfigureAwait(false);
            AppConfig.Flush();
            requestCts.Token.ThrowIfCancellationRequested();
            await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object>
            {
                ["Action"] = "DGPU_DIRECT_CONNECT_RESTART",
            }).ConfigureAwait(false);
            Logger.WriteLine($"Requested GCU GPU restart route for target={mode}");
            return GpuRestartRequestOutcome.Requested;
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

    internal static IReadOnlyList<Dictionary<string, object>> CreateGpuRestartTargetPayloads(
        int mode,
        bool supportsDgpuDirect) => mode switch
    {
        GpuDgpu =>
        [
            new() { ["Action"] = "DGPU_DIRECT_CONNECT_TOGGLE_ON" },
            CreateGpuModePayload(GpuStandard),
            new() { ["Action"] = "DGPU_DIRECT_CONNECT_TOGGLE_ON" },
        ],
        GpuStandard => supportsDgpuDirect
            ? [new() { ["Action"] = "DGPU_DIRECT_CONNECT_TOGGLE_OFF" }]
            : [CreateGpuModePayload(GpuStandard)],
        GpuIGpu => supportsDgpuDirect
            ? [new() { ["Action"] = "DGPU_DIRECT_CONNECT_TOGGLE_IGPU" }]
            : [CreateGpuModePayload(GpuIGpu)],
        GpuAuto => supportsDgpuDirect
            ? [
                new() { ["Action"] = "DGPU_DIRECT_CONNECT_TOGGLE_OFF" },
                CreateGpuModePayload(GpuAuto),
            ]
            : [CreateGpuModePayload(GpuAuto)],
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

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
    /// keepRegisterOnHotSwitchTimeout：热切换确认超时后保留寄存器不回滚。
    /// 2026-09-10 实测证伪旧结论：RB_ON 热载荷只翻转 GCU 的软件寄存器，本机硬件
    /// 既不热切换、重启后也不按该寄存器应用（重启后仍为混合模式）。手动切换已
    /// 全部改走 RequestGpuModeRestartAsync 重启路径，此参数仅剩自动路径默认 false
    /// （超时即回滚，与官方一致）。
    /// </summary>
    public async Task<bool> SwitchGpuMode(int mode, bool autoRestart = false, bool? pluggedForAuto = null, bool keepRegisterOnHotSwitchTimeout = false)
    {
        var requestCts = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _gpuSwitchCts, requestCts);
        previous?.Cancel();
        bool lockTaken = false;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (_hw is not { IsConnected: true } || mode is < GpuIGpu or > GpuAuto || !_hw.CanSwitchGpuMode(mode)) return false;
            lockTaken = await AcquireSwitchLockAsync($"SwitchGpuMode({mode})", requestCts.Token).ConfigureAwait(false);
            if (!lockTaken) return false;
            requestCts.Token.ThrowIfCancellationRequested();
            // 回滚基准必须取自发布之前：热切换期间 GCU 会把目标模式回显进 GpuMode，
            // 超时后从这里已经读不到「切换前是什么」。
            int modeBeforeSwitch = CurrentGpuMode;

            bool leavingDirect = CurrentGpuMode == GpuDgpu && mode != GpuDgpu;
            int expectedAutoRuntime = mode == GpuAuto && pluggedForAuto.HasValue
                ? (pluggedForAuto.Value ? 1 : 2)
                : -1;
            bool hotSwitchRequest = mode == GpuIGpu && _hw.SupportsGpuHotSwap &&
                (CurrentGpuMode == GpuStandard ||
                 (CurrentGpuMode == GpuAuto && _hw.GpuSwitchResult == 1));
            bool muxTargetRequest = mode == GpuIGpu && _hw.SupportsDgpuDirect &&
                !_hw.SupportsIgpuOnly && !hotSwitchRequest;
            long gpuModeStatusVersion = _hw.GpuModeStatusVersion;
            long hotSwitchResultVersion = _hw.GpuSwitchResultVersion;
            bool requiresFreshHotSwitchResult = hotSwitchRequest && _hw.GpuSwitchResultReported;
            // 热切换的确认可以长达约两分钟（61 次轮询 × 2 秒）。登记为可让位的长操作，
            // 让其它切换意图能把它请下来，而不是在 _switchLock 上排队两分钟。
            if (hotSwitchRequest) Volatile.Write(ref _longRunningSwitchCts, requestCts);
            bool TargetReached()
            {
                if (expectedAutoRuntime >= 0)
                    return CurrentGpuMode == GpuAuto && _hw.GpuSwitchResult == expectedAutoRuntime &&
                        (_hw.GpuModeStatusVersion > gpuModeStatusVersion ||
                         _hw.GpuSwitchResultVersion > hotSwitchResultVersion);

                if (CurrentGpuMode != mode) return false;
                if (!hotSwitchRequest && _hw.GpuModeStatusVersion <= gpuModeStatusVersion) return false;
                return !requiresFreshHotSwitchResult ||
                    (_hw.GpuSwitchResultVersion > hotSwitchResultVersion && _hw.GpuSwitchResult == 2);
            }
            Dictionary<string, object> payload = CreateGpuSwitchPayload(
                mode, _hw.SupportsDgpuDirect, _hw.SupportsIgpuOnly, hotSwitchRequest);
            string action = payload["Action"].ToString() ?? "";
            bool leavingIgpuOnly = modeBeforeSwitch == GpuIGpu && mode != GpuIGpu;
            Logger.WriteLine($"MechrevoService.SwitchGpuMode({mode}) -> {action}, leavingDirect={leavingDirect}, leavingIgpuOnly={leavingIgpuOnly}");
            if (mode == GpuDgpu)
            {
                // 直连是独立 MUX 层；进入直连前先退出核显-only。
                await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "IGPU_ONLY_CONNECT_RB_OFF", ["SetToWMIEC"] = "OK" }).ConfigureAwait(false);
                await Task.Delay(300, requestCts.Token).ConfigureAwait(false);
            }
            requestCts.Token.ThrowIfCancellationRequested();
            gpuModeStatusVersion = _hw.GpuModeStatusVersion;
            hotSwitchResultVersion = _hw.GpuSwitchResultVersion;
            await _hw.Publish(MqttTopics.SettingControl, payload).ConfigureAwait(false);
            if (leavingDirect && !muxTargetRequest)
            {
                await Task.Delay(300, requestCts.Token).ConfigureAwait(false);
                await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object>
                {
                    ["Action"] = mode == GpuIGpu
                        ? "DGPU_DIRECT_CONNECT_TOGGLE_IGPU"
                        : "DGPU_DIRECT_CONNECT_TOGGLE_OFF",
                }).ConfigureAwait(false);
            }
            if (leavingIgpuOnly && _hw.SupportsDgpuDirect)
            {
                // 从纯集显离开时，MUX 需要重新启用独显通路（官方流程：发 DGPU_DIRECT_CONNECT_TOGGLE_OFF）
                await Task.Delay(300, requestCts.Token).ConfigureAwait(false);
                await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object>
                {
                    ["Action"] = "DGPU_DIRECT_CONNECT_TOGGLE_OFF",
                }).ConfigureAwait(false);
            }

            bool confirmed = !hotSwitchRequest && await _hw.WaitForStateAsync(
                TargetReached,
                TimeSpan.FromMilliseconds(1200),
                requestCts.Token).ConfigureAwait(false);
            int confirmationAttempts = hotSwitchRequest
                ? HotSwitchStatusPollLimit
                : expectedAutoRuntime >= 0 ? 2 : 7;
            TimeSpan confirmationPollDelay = hotSwitchRequest
                ? TimeSpan.FromSeconds(2)
                : TimeSpan.FromMilliseconds(1800);
            for (int attempt = 0; attempt < confirmationAttempts && !confirmed; attempt++)
            {
                requestCts.Token.ThrowIfCancellationRequested();
                await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" }).ConfigureAwait(false);
                confirmed = await _hw.WaitForStateAsync(
                    TargetReached,
                    confirmationPollDelay,
                    requestCts.Token).ConfigureAwait(false);
                bool retryTarget = hotSwitchRequest
                    ? ShouldRetryHotSwitchPoll(attempt)
                    : expectedAutoRuntime >= 0 ? attempt == 0 : attempt is 1 or 3 or 5;
                if (!confirmed && retryTarget)
                {
                    // The official console retries the iGPU request every fourth poll.
                    await _hw.Publish(MqttTopics.SettingControl, payload).ConfigureAwait(false);
                }
            }

            // 官方 IgpuOnlyOnCommand 超时（count>60）后的动作是回滚：发
            // IGPUONLYCONNECTIONSWITCH_STATUS 把开关退回切换前的值，再刷新状态。
            // 不回滚的话寄存器停留在 RB_ON——界面显示目标模式、用户却没真的得到它，
            // 而且下次重启会以这个从未生效的模式启动。CCUWinUI L53562-53580。
            // 例外：手动切换路径传 keepRegisterOnHotSwitchTimeout=true，把回滚决定
            // 交给 UI 的「重启生效」询问——用户选放弃才在这里补回滚。
            if (!confirmed && hotSwitchRequest)
            {
                if (keepRegisterOnHotSwitchTimeout)
                    Logger.WriteLine("Hot switch not confirmed; register kept for the reboot-to-apply prompt.");
                else
                    await RollbackFailedHotSwitchAsync(modeBeforeSwitch, requestCts.Token).ConfigureAwait(false);
            }

            // 自动模式的runtime验证：某些机型GCU在热切换场景下runtime状态切换较慢，
            // 但模式寄存器已正确设置。如果GpuMode已经是AUTO，接受当前runtime而不强制fallback。
            // 这避免了从纯集显切换到自动模式时错误地降级到标准模式。
            if (!confirmed && expectedAutoRuntime >= 0 && CurrentGpuMode == GpuAuto)
            {
                // 自动模式寄存器已确认，runtime可能稍后更新
                Logger.WriteLine($"Auto mode confirmed with runtime {_hw.GpuSwitchResult} (expected {expectedAutoRuntime}), accepting current state.");
                confirmed = true;
            }

            Logger.WriteLine($"SwitchGpuMode confirmed={confirmed}: expected={mode} actual={CurrentGpuMode} runtime={_hw.GpuSwitchResult}/{expectedAutoRuntime} hotSwitch={hotSwitchRequest} action={action} elapsed={elapsed.ElapsedMilliseconds}ms");
            if (!confirmed) return false;

            if (autoRestart)
            {
                // 原版流程：800ms 后 DGPU_DIRECT_CONNECT_RESTART（服务端自动重启系统）。
                await Task.Delay(800, requestCts.Token).ConfigureAwait(false);
                AppConfig.Flush();
                requestCts.Token.ThrowIfCancellationRequested();
                await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "DGPU_DIRECT_CONNECT_RESTART" }).ConfigureAwait(false);
                Logger.WriteLine("SwitchGpuMode 已发送自动重启命令");
            }
            return true;
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
            if (lockTaken) _switchLock.Release();
            Interlocked.CompareExchange(ref _longRunningSwitchCts, null, requestCts);
            Interlocked.CompareExchange(ref _gpuSwitchCts, null, requestCts);
            requestCts.Dispose();
        }
    }

    /// <summary>
    /// 热切换确认超时后的回滚。逐字对齐官方 IgpuOnlyOnCommand 的超时分支：
    /// IGPUONLYCONNECTIONSWITCH_STATUS 携带切换前的开关值（RB_OFF=0 / RB_ON=1 / RB_AUTO=2），
    /// 随后 GETSTATUS 刷新，并短暂等待状态离开目标值——让调用方拿到 false 时
    /// <see cref="CurrentGpuMode"/> 已经是真实回到的旧模式，而不是目标的回显。
    /// 回滚是清理动作：任何失败只记日志，绝不能从这里抛出打断 SwitchGpuMode 的收尾。
    /// </summary>
    internal async Task RollbackFailedHotSwitchAsync(int modeBeforeSwitch, CancellationToken token)
    {
        try
        {
            int rollbackStatus = modeBeforeSwitch switch
            {
                GpuAuto => 2,
                GpuIGpu => 1,
                _ => 0,
            };
            Logger.WriteLine($"Hot switch not confirmed; rolling back to pre-switch mode {modeBeforeSwitch} (status={rollbackStatus})");
            await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object>
            {
                ["Action"] = "IGPUONLYCONNECTIONSWITCH_STATUS",
                ["Status"] = rollbackStatus,
            }).ConfigureAwait(false);
            await _hw.Publish(MqttTopics.SettingControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" }).ConfigureAwait(false);
            await _hw.WaitForStateAsync(
                () => CurrentGpuMode != GpuIGpu,
                TimeSpan.FromMilliseconds(2500),
                token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.WriteLine("Hot switch rollback failed: " + ex.Message); }
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
            if (!_hw.UsbChargerSeen) return false;
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
            await _hw.Publish(MqttTopics.BtLcControl, new Dictionary<string, object> { ["Action"] = "Connect" });
            Logger.WriteLine("LcConnect 已发送");
            return true;
        });

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
            await _hw.Publish(MqttTopics.BtLcControl, new Dictionary<string, object> { ["Action"] = "DeviceMacSetting", ["DeviceMac"] = mac });
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

            if (lightingReadbackWasSeen || requireReadback)
            {
                bool confirmed = await ConfirmLiquidCoolingStateAsync(
                    statusVersionBeforeCommand,
                    () => LiquidCoolingLightProfileMatches(profile, effectiveColor),
                    $"LcApplyLightProfile({profile})");
                if (!confirmed)
                {
                    Logger.WriteLine($"LcApplyLightProfile({profile}) lighting readback not confirmed");
                    return false;
                }
            }
            else
            {
                // Older GCU builds do not expose LED mode fields. Keep their
                // send-only compatibility, but make the lack of proof explicit.
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
        string effect = string.IsNullOrWhiteSpace(_hw.KeyboardEffect) ? "Single" : _hw.KeyboardEffect;
        return SetLightEffect(MqttTopics.KeyboardCtrl, effect, light: level0to4);
    }

    /// <summary>通用灯效命令（键盘/灯条/Logo 灯共用 MyKeyBoard 载荷结构，仅 topic 不同）。
    /// save=true 时 nv_save=SAVE——效果一次性写入固件 NVRAM（重启/断电后保持）。</summary>
    public async Task<bool> SetLightEffect(string topic, string effect, int light = 4, int speed = 1, string direction = "None", Color? singleColor = null, bool save = true)
    {
        try
        {
            if (!SupportsLightTopic(topic)) return false;
            var color = new Dictionary<string, object>
            {
                ["isCircular"] = true,
                ["ColorBlocks"] = singleColor.HasValue ? 1 : 7,
                ["ColorBuffer"] = singleColor.HasValue
                    ? new object[]
                    {
                        new Dictionary<string, object> { ["R"] = (int)singleColor.Value.R, ["G"] = (int)singleColor.Value.G, ["B"] = (int)singleColor.Value.B },
                    }
                    : new object[]   // 原版默认 7 色块（rkgcolor）
                    {
                        new Dictionary<string, object> { ["R"] = 255, ["G"] = 0, ["B"] = 0 },
                        new Dictionary<string, object> { ["R"] = 255, ["G"] = 165, ["B"] = 0 },
                        new Dictionary<string, object> { ["R"] = 255, ["G"] = 255, ["B"] = 0 },
                        new Dictionary<string, object> { ["R"] = 0, ["G"] = 255, ["B"] = 0 },
                        new Dictionary<string, object> { ["R"] = 0, ["G"] = 255, ["B"] = 255 },
                        new Dictionary<string, object> { ["R"] = 0, ["G"] = 0, ["B"] = 255 },
                        new Dictionary<string, object> { ["R"] = 255, ["G"] = 0, ["B"] = 255 },
                    },
            };
            await _hw.Publish(topic, new Dictionary<string, object>
            {
                ["function"] = "SetEffectALL",
                ["mode"] = "Lighting",
                ["speed"] = speed.ToString(),
                ["light"] = light.ToString(),
                ["effect"] = effect,
                ["direction"] = direction,
                ["nv_save"] = save ? "SAVE" : "NOT_SAVE",
                ["color"] = color,
            });
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
            if (IsSilentTurboActive == silent) return true;
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
        if (topic.StartsWith(MqttTopics.KeyboardPrefix, StringComparison.OrdinalIgnoreCase)) return _hw.SupportsKeyboard;
        return LightTopicToQuickSwitchKey(topic) switch
        {
            "logolight" => _hw.SupportsLogoLight,
            "lightbar" => _hw.SupportsLightbar,
            _ => false,
        };
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
        if (topic.StartsWith(MqttTopics.LightbarPrefix, StringComparison.OrdinalIgnoreCase)) return "lightbar";
        return null;
    }
}
