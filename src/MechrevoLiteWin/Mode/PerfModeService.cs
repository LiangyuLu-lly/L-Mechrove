using MechrevoLite.Hardware;
using MechrevoLite.Helpers;
using Microsoft.Win32;

namespace MechrevoLite.Mode;

/// <summary>
/// 性能模式编排（G-Helper 式：每个模式都是一组可改的参数，自定义模式可增删）。
///
/// <para>职责：</para>
/// <list type="bullet">
/// <item>激活：决定路线与固件档位 → 生成下发计划 → 逐步下发并逐项判定 → 落盘档位归属与当前模式；</item>
/// <item>编辑：保存覆盖项；内置模式第一次改固件门控项时用厂商出厂值补齐并转到自定义档承载；
///       正在运行的模式只下发变化项；</item>
/// <item>跟随：Fn 键 / 厂商服务改了模式时映射回用户模式，必要时把固件拉回该模式的承载位置；</item>
/// <item>守护：厂商 GCU 切模式后约 1 秒会自己把 Windows 电源模式同步回它那一档，
///       应用侧项在 1.5/3/6/10 s 复查，被改回就重新落地；</item>
/// <item>重放：启动、唤醒、插拔电源（按供电方式记住的模式）。</item>
/// </list>
///
/// <para>判据纪律：界面上的「已生效」只来自独立硬件判据；<c>Fan/Status</c> 回显只算「已下发」。</para>
/// </summary>
internal sealed class PerfModeService
{
    public static PerfModeService? Instance { get; private set; }

    readonly MechrevoHw _hw;
    readonly IPerfModeBackend _backend;
    readonly Func<int> _powerSource;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly object _requestLock = new();
    CancellationTokenSource? _activationCts;
    CancellationTokenSource? _editCts;
    readonly object _redirectLock = new();
    readonly Queue<long> _recentRedirects = new();
    IReadOnlyList<PerfModeDefinition>? _modes;
    int _requestSeq;   // 激活请求：更新的请求取代排队中的旧请求
    int _guardSeq;     // 守护代次：任何新的切换/编辑/跟随都会结束旧守护
    int _activating;
    int _reconcileSeq;
    string? _pendingModeId;

    /// <summary>模式列表、名字或「是否已自定义」变了（界面重建分段与托盘）。</summary>
    public event Action? ModesChanged;

    /// <summary>当前模式变了，或者开始/结束了一次切换（界面刷新选中态）。</summary>
    public event Action? ActiveChanged;

    /// <summary>一次下发结束（编辑器据此显示逐项结果）。</summary>
    public event Action<PerfApplyOutcome>? Applied;

    internal PerfModeService(MechrevoHw hw, IPerfModeBackend backend, Func<int>? powerSource = null)
    {
        _hw = hw;
        _backend = backend;
        _powerSource = powerSource ?? (() => (int)SystemInformation.PowerStatus.PowerLineStatus);
    }

    public static PerfModeService Create(MechrevoService service, MechrevoHw hw)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(hw);
        PerfModeStore.MigrateLegacy();
        var instance = new PerfModeService(hw, new PerfModeBackend(service, hw));
        Instance = instance;
        return instance;
    }

    // ---- 查询 ----

    public IReadOnlyList<PerfModeDefinition> Modes => _modes ??= PerfModeStore.Load();

    public string ActiveModeId => PerfModeStore.ActiveModeId;

    /// <summary>正在切换过去的模式（界面乐观高亮它，直到切换结束）。</summary>
    public string? PendingModeId => Volatile.Read(ref _pendingModeId);

    /// <summary>界面应该高亮的模式：切换中取目标，否则取当前。</summary>
    public string DisplayedModeId => PendingModeId ?? ActiveModeId;

    public PerfModeDefinition? Find(string? id) =>
        id is null ? null : Modes.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    public PerfModeDefinition? Active => Find(ActiveModeId);

    /// <summary>主界面「自定义」段对应的模式：最近用过的自定义模式，没有就取第一个。</summary>
    public PerfModeDefinition LastCustom =>
        Find(PerfModeStore.LastCustomId) is { IsCustom: true } last ? last : Modes.First(m => m.IsCustom);

    /// <summary>这台机器的固件自定义档数量（厂商 <c>CustomizeTarget==9</c> 的机型 3 个，其余 5 个）。</summary>
    public static int SlotCount => FirmwareSlotPlanner.SlotCountFor(ReadCustomizeTarget());

    static int ReadCustomizeTarget()
    {
        try
        {
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = hklm.OpenSubKey(@"SOFTWARE\OEM\GamingCenter2");
            return key?.GetValue("CustomizeTarget") is int v ? v : 1;
        }
        catch { return 1; }
    }

    /// <summary>
    /// 进入超频狂暴时是否下发官方自动超频（+105/+500）。用户没碰过狂暴的显卡超频项时保持官方行为；
    /// 改过的由下发计划在切换之后覆盖（关 = 偏移归零，自定义偏移 = 用户的值）。
    /// </summary>
    public static bool TurboAutoOcEnabled()
    {
        PerfModeSettings s = PerfModeStore.LoadSettings(PerfModeDefinition.BuiltInId(PerfModeKind.Turbo));
        return s.GpuOverclockOn is null && s.GpuCoreOffset is null && s.GpuMemoryOffset is null;
    }

    void Invalidate()
    {
        _modes = null;
        try { ModesChanged?.Invoke(); }
        catch (Exception ex) { Logger.WriteLine("PerfModes ModesChanged handler failed: " + ex.Message); }
    }

    void RaiseActiveChanged()
    {
        try { ActiveChanged?.Invoke(); }
        catch (Exception ex) { Logger.WriteLine("PerfModes ActiveChanged handler failed: " + ex.Message); }
    }

    void RaiseApplied(PerfApplyOutcome outcome)
    {
        try { Applied?.Invoke(outcome); }
        catch (Exception ex) { Logger.WriteLine("PerfModes Applied handler failed: " + ex.Message); }
    }

    PerfModeCapabilities Capabilities() => PerfModeBackend.CapabilitiesFrom(_hw);

    int PowerSourceKey() => _powerSource();

    // ---- 激活 ----

    /// <summary>
    /// 切到一个用户模式并下发它的参数。更新的请求会取代排队中的旧请求（旧请求返回 null）。
    /// </summary>
    /// <param name="forceParameters">档位命中也重写全部固件字段（路线刚变化、或怀疑档位被外部改过）。</param>
    public async Task<PerfApplyOutcome?> ActivateAsync(string modeId, string reason, bool forceParameters = false)
        => await ActivateIfCurrentAsync(modeId, reason, forceParameters, null).ConfigureAwait(false);

    async Task<PerfApplyOutcome?> ActivateIfCurrentAsync(string modeId, string reason, bool forceParameters, int? expectedRequest)
    {
        int request;
        CancellationTokenSource requestCts;
        CancellationTokenSource? previous;
        CancellationTokenSource? previousEdit;
        lock (_requestLock)
        {
            if (expectedRequest is { } expected && expected != _requestSeq) return null;
            request = Interlocked.Increment(ref _requestSeq);
            requestCts = new CancellationTokenSource();
            previous = _activationCts;
            previousEdit = _editCts;
            _activationCts = requestCts;
            Volatile.Write(ref _pendingModeId, modeId);
        }
        try { previous?.Cancel(); }
        catch (ObjectDisposedException) { }
        try { previousEdit?.Cancel(); }
        catch (ObjectDisposedException) { }
        RaiseActiveChanged();
        bool locked = false;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _gate.WaitAsync(requestCts.Token).ConfigureAwait(false);
            locked = true;
            Interlocked.Increment(ref _activating);
            Interlocked.Increment(ref _guardSeq);
            if (request != Volatile.Read(ref _requestSeq)) return null;
            Logger.WriteLine($"PerfModes: {modeId} gate acquired after {elapsed.ElapsedMilliseconds}ms");
            return await ActivateLockedAsync(modeId, reason, forceParameters, requestCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
            Logger.WriteLine($"PerfModes: {modeId} superseded after {elapsed.ElapsedMilliseconds}ms");
            return null;
        }
        finally
        {
            if (locked)
            {
                Interlocked.Decrement(ref _activating);
                _gate.Release();
            }
            lock (_requestLock)
            {
                if (ReferenceEquals(_activationCts, requestCts))
                {
                    _activationCts = null;
                    Volatile.Write(ref _pendingModeId, null);
                }
            }
            requestCts.Dispose();
            RaiseActiveChanged();
        }
    }

    async Task<PerfApplyOutcome?> ActivateLockedAsync(string modeId, string reason, bool forceParameters, CancellationToken ct)
    {
        PerfModeDefinition? mode = Find(modeId);
        if (mode is null)
        {
            Logger.WriteLine($"PerfModes: activate({modeId}) ignored, mode does not exist");
            return null;
        }
        if (_hw is not { IsConnected: true })
        {
            Logger.WriteLine($"PerfModes: activate({modeId}) refused, GCU not connected");
            var refused = new PerfApplyOutcome(modeId, new[]
            {
                new PerfApplyStepOutcome(SwitchStepFor(mode, 0), PerfApplyResult.Failed, "GCU 未连接"),
            });
            RaiseApplied(refused);
            return refused;
        }

        PerfModeCapabilities caps = Capabilities();
        IReadOnlyList<FirmwareSlotState> slots = PerfModeStore.LoadSlots(SlotCount);
        FirmwareSlotPlan? plan = null;
        if (mode.Route == PerfModeRoute.FirmwareSlot)
        {
            string running = ActiveModeId;
            plan = FirmwareSlotPlanner.Plan(mode.Id, mode.Settings.FirmwareSignature(), slots, running);
            if (forceParameters && !plan.NeedsParameterWrite) plan = plan with { NeedsParameterWrite = true, Reason = "forced rewrite" };
        }

        IReadOnlyList<PerfApplyStep> steps = PerfModeApplyPlanner.Build(mode, plan, caps);
        Logger.WriteLine($"PerfModes: activate {mode.Id} ({reason}) route={mode.Route} slot={plan?.SlotIndex.ToString() ?? "-"} " +
            $"write={plan?.NeedsParameterWrite.ToString() ?? "-"} steps={steps.Count}");
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        PerfApplyOutcome outcome;
        try { outcome = await PerfModeRunner.RunAsync(mode.Id, steps, _backend, ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (plan is { NeedsParameterWrite: true })
                PerfModeStore.SaveSlots(slots.Select(s => s.Index == plan.SlotIndex ? s with { Signature = null } : s).ToArray());
            throw;
        }
        Logger.WriteLine($"PerfModes: {mode.Id} -> {outcome.Describe()}");
        Logger.WriteLine($"PerfModes: {mode.Id} apply elapsed={elapsed.ElapsedMilliseconds}ms");

        if (outcome.ModeSwitched)
        {
            if (plan is not null)
            {
                slots = FirmwareSlotPlanner.Assign(slots, plan.SlotIndex, mode.Id,
                    outcome.FirmwareParametersIssued ? mode.Settings.FirmwareSignature() : null, PerfModeStore.NowStamp());
                PerfModeStore.SaveSlots(slots);
            }
            PerfModeStore.ActiveModeId = mode.Id;
            PerfModeStore.SetActiveFor(PowerSourceKey(), mode.Id);
            if (mode.IsCustom) PerfModeStore.LastCustomId = mode.Id;
            if (plan is not null)
            {
                await CaptureSlotAsync(mode, plan.SlotIndex, ct).ConfigureAwait(false);
                // 档位是轮转复用的：切进来时把这个模式的名字写给厂商屏显，Fn 切档的 OSD 才显示对的名字。
                await PushSlotNameAsync(mode.Id, ct).ConfigureAwait(false);
            }
            StartGuard(mode.Id);
        }

        RaiseApplied(outcome);
        return outcome;
    }

    /// <summary>
    /// 该模式正在自定义档上运行时，把它的显示名（最多 12 字）同步为厂商屏显名。
    /// 名字已一致时不发；不在自定义档上返回 false。
    /// </summary>
    public async Task<bool> PushSlotNameAsync(string modeId, CancellationToken ct = default)
    {
        PerfModeDefinition? mode = Find(modeId);
        if (mode is null || mode.Route != PerfModeRoute.FirmwareSlot) return false;
        if (!string.Equals(ActiveModeId, mode.Id, StringComparison.Ordinal)) return false;
        if (_hw is not { IsConnected: true } || _hw.OperatingMode != 3) return false;
        string name = PerfModeText.Name(mode);
        if (name.Length > MechrevoService.CustomProfileNameMaxLength)
            name = name[..MechrevoService.CustomProfileNameMaxLength];
        if (string.Equals(_hw.ProfileName, name, StringComparison.Ordinal)) return true;
        try { return await _backend.SetSlotNameAsync(name, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.WriteLine("PerfModes: slot name push failed: " + ex.Message);
            return false;
        }
    }

    static PerfApplyStep SwitchStepFor(PerfModeDefinition mode, int slot) =>
        mode.Route == PerfModeRoute.FirmwareSlot
            ? new PerfApplyStep(PerfApplyStepKind.SwitchFirmwareSlot, Number: slot, Verify: PerfApplyVerify.ModeReadback)
            : new PerfApplyStep(PerfApplyStepKind.SwitchBuiltIn, Number: (int)mode.Kind, Verify: PerfApplyVerify.ModeReadback);

    /// <summary>
    /// 自定义档里用户没设过的项：把档位现有的值落进模式配置，让模式与档位解耦
    /// （被轮转挤掉后重新装入时仍是这组参数）。只在固件确实停在该档时做。
    /// </summary>
    async Task CaptureSlotAsync(PerfModeDefinition mode, int slot, CancellationToken ct = default)
    {
        try
        {
            if (!PerfModeResolver.IsFirmwareOn(mode, PerfModeStore.LoadSlots(SlotCount),
                    _hw.OperatingMode, _hw.CustomProfileIndex, false, false))
                return;
            // 风扇表只在回读的就是这个档的表（M4T{n}）时才落：Fan/Table 可能乱序晚到。
            string expectedTable = "M4T" + (slot + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            bool TableIsThisSlot() => string.Equals(_hw.CurveName, expectedTable, StringComparison.OrdinalIgnoreCase);
            PerfModeSettings current = PerfModeStore.LoadSettings(mode.Id);
            if (!TableIsThisSlot() && _hw.FanCurveSeen && (current.CpuFanDuty is null || current.GpuFanDuty is null))
                await _hw.WaitForStateAsync(TableIsThisSlot, TimeSpan.FromMilliseconds(1500), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            bool fanTableIsThisSlot = TableIsThisSlot();

            PerfModeSettings filled = SnapshotLive(fanTableIsThisSlot).FillNulls(current);
            if (filled.FirmwareSignature() == current.FirmwareSignature()) return;
            PerfModeStore.SaveSettings(mode.Id, filled);
            IReadOnlyList<FirmwareSlotState> slots = PerfModeStore.LoadSlots(SlotCount);
            bool synchronized = slots.Any(s => s.Index == slot && s.OwnerModeId == mode.Id
                && s.Signature == current.FirmwareSignature());
            PerfModeStore.SaveSlots(FirmwareSlotPlanner.Assign(slots, slot, mode.Id,
                synchronized ? filled.FirmwareSignature() : null, PerfModeStore.NowStamp()));
            Logger.WriteLine($"PerfModes: captured slot {slot} parameters into {mode.Id}");
            Invalidate();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.WriteLine("PerfModes: slot capture failed: " + ex.Message);
        }
    }

    /// <summary>运行时回读值（只取这台机器可调、且回读过的项）。</summary>
    internal PerfLiveSnapshot SnapshotLive(bool includeFanCurves) => SnapshotLive(_hw, includeFanCurves);

    internal static PerfLiveSnapshot SnapshotLive(MechrevoHw hw, bool includeFanCurves)
    {
        static int[]? Duty(byte[] raw) =>
            raw is { Length: 16 } && raw.Any(b => b > 0) ? raw.Select(b => Math.Clamp((int)b, 0, 100)).ToArray() : null;
        return new PerfLiveSnapshot(
            Pl1: hw.Pl1Adjustable && hw.Pl1 > 0 ? hw.Pl1 : null,
            Pl2: hw.Pl2Adjustable && hw.Pl2 > 0 ? hw.Pl2 : null,
            Pl4: hw.Pl4Adjustable && hw.Pl4 > 0 ? hw.Pl4 : null,
            TccOn: hw.TccAdjustable ? hw.TccSwitch : null,
            TccTarget: hw.TccAdjustable && hw.TccTarget > 0 ? hw.TccTarget : null,
            GpuTgp: hw.GpuTgpAdjustable && hw.GpuTgp > 0 ? hw.GpuTgp : null,
            GpuDynamicBoostOn: hw.GpuDynamicBoostAdjustable ? hw.GpuDbSwitch : null,
            GpuDynamicBoost: hw.GpuDynamicBoostAdjustable && hw.GpuDb >= 0 ? hw.GpuDb : null,
            FanSwitchSpeedOn: hw.SupportsFanSwitchSpeed ? hw.FanSwitchSpeedEnabled : null,
            FanSwitchSpeedMs: hw.SupportsFanSwitchSpeed && hw.FanSwitchSpeed > 0 ? hw.FanSwitchSpeed : null,
            CpuFanDuty: includeFanCurves && hw.FanCurveSeen ? Duty(hw.CpuCurveDuty) : null,
            GpuFanDuty: includeFanCurves && hw.FanCurveSeen ? Duty(hw.GpuCurveDuty) : null,
            // 超频按档保存（直连 NVAPI 的每档记录 + GCU 档位）：落进模式，换档后也还是这组偏移。
            GpuOverclockOn: hw.SupportsGpuOverclock ? hw.GpuOverclockEnabled : null,
            GpuCoreOffset: hw.SupportsGpuOverclock ? hw.EffectiveGpuCoreClockOffset : null,
            GpuMemoryOffset: hw.SupportsGpuOverclock ? hw.EffectiveGpuMemoryClockOffset : null);
    }

    // ---- 编辑 ----

    /// <summary>
    /// 改一个模式的参数：先落盘；该模式正在运行就立即下发（只发变化项）。
    /// 内置模式第一次改固件门控项（PL/TGP/DB/风扇曲线）时，用厂商出厂值补齐其余固件项并转到自定义档承载。
    /// 返回 null = 该模式没在运行（已保存，下次切到它时生效）或请求被更新的请求取代。
    /// </summary>
    public async Task<PerfApplyOutcome?> UpdateAsync(string modeId, Func<PerfModeSettings, PerfModeSettings> edit, string reason, bool applyHardware = true)
    {
        ArgumentNullException.ThrowIfNull(edit);
        int request = Volatile.Read(ref _requestSeq);
        (PerfApplyOutcome? outcome, bool needsActivation) = await UpdateCoreAsync(modeId, edit, reason, applyHardware, request).ConfigureAwait(false);
        // 路线变了（内置 ↔ 自定义档）或固件不在该模式上：整体切换一次并重写全部参数。
        return needsActivation
            ? await ActivateIfCurrentAsync(modeId, "route changed by edit", true, request).ConfigureAwait(false)
            : outcome;
    }

    internal void CancelPendingApply()
    {
        CancellationTokenSource? activation, edit;
        lock (_requestLock)
        {
            Interlocked.Increment(ref _requestSeq);
            Interlocked.Increment(ref _guardSeq);
            activation = _activationCts;
            edit = _editCts;
        }
        try { activation?.Cancel(); } catch (ObjectDisposedException) { }
        try { edit?.Cancel(); } catch (ObjectDisposedException) { }
    }

    async Task<(PerfApplyOutcome? Outcome, bool NeedsActivation)> UpdateCoreAsync(
        string modeId, Func<PerfModeSettings, PerfModeSettings> edit, string reason, bool applyHardware, int request)
    {
        using var editCts = new CancellationTokenSource();
        await _gate.WaitAsync().ConfigureAwait(false);
        Interlocked.Increment(ref _activating);
        Interlocked.Increment(ref _guardSeq);
        try
        {
            PerfModeDefinition? mode = Find(modeId);
            if (mode is null) return (null, false);
            PerfModeSettings before = mode.Settings;
            PerfModeSettings after = edit(before);
            if (after == before) return (null, false);

            bool becomesEmulated = !mode.IsCustom && !before.NeedsFirmwareSlot && after.NeedsFirmwareSlot;
            if (becomesEmulated)
            {
                PerfModeSettings seed = VendorModeDefaults.Seed(mode.Kind, _hw);
                // 运行时回读只在该内置模式正在运行时可信（回显的就是它的档位存档）。
                PerfLiveSnapshot? live = string.Equals(ActiveModeId, mode.Id, StringComparison.Ordinal)
                    ? SnapshotLive(includeFanCurves: false)
                    : null;
                after = PerfModeResolver.Materialize(after, seed, live);
                Logger.WriteLine($"PerfModes: {mode.Id} now runs on a firmware slot (seeded from vendor defaults)");
            }

            PerfModeStore.SaveSettings(mode.Id, after);
            Invalidate();
            PerfModeDefinition updated = mode.With(after);

            lock (_requestLock)
            {
                if (!applyHardware || request != _requestSeq || PendingModeId is not null) return (null, false);
                _editCts = editCts;
            }

            if (!string.Equals(ActiveModeId, mode.Id, StringComparison.Ordinal) || _hw is not { IsConnected: true })
                return (null, false);

            IReadOnlyList<FirmwareSlotState> slots = PerfModeStore.LoadSlots(SlotCount);
            bool onIt = PerfModeResolver.IsFirmwareOn(mode, slots, _hw.OperatingMode, _hw.CustomProfileIndex,
                MechrevoService.IsSilentTurboActive,
                _hw.Capabilities.SilentTurboAvailability == FeatureAvailability.Supported);
            if (updated.Route != mode.Route || !onIt) return (null, true);

            PerfModeSettings delta = PerfModeResolver.Delta(before, after);
            FirmwareSlotPlan? plan = null;
            if (updated.Route == PerfModeRoute.FirmwareSlot)
            {
                FirmwareSlotState? slot = slots.FirstOrDefault(s => string.Equals(s.OwnerModeId, mode.Id, StringComparison.Ordinal));
                if (slot is null) return (null, true);
                if (slot.Signature != before.FirmwareSignature()) delta = after;
                plan = new FirmwareSlotPlan(slot.Index, true, "edit");
            }
            IReadOnlyList<PerfApplyStep> steps = PerfModeApplyPlanner.Build(
                updated with { Settings = delta }, plan, Capabilities(), includeSwitch: false, routeOverride: updated.Route);
            PerfApplyOutcome outcome;
            try { outcome = await PerfModeRunner.RunAsync(mode.Id, steps, _backend, editCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (editCts.IsCancellationRequested)
            {
                if (plan is not null)
                    PerfModeStore.SaveSlots(slots.Select(s => s.Index == plan.SlotIndex ? s with { Signature = null } : s).ToArray());
                return (null, false);
            }
            Logger.WriteLine($"PerfModes: edit {mode.Id} ({reason}) -> {outcome.Describe()}");
            if (plan is not null)
                PerfModeStore.SaveSlots(FirmwareSlotPlanner.Assign(slots, plan.SlotIndex, mode.Id,
                    outcome.FirmwareParametersIssued ? after.FirmwareSignature() : null, PerfModeStore.NowStamp()));
            StartGuard(mode.Id);
            RaiseApplied(outcome);
            return (outcome, false);
        }
        finally
        {
            lock (_requestLock)
                if (ReferenceEquals(_editCts, editCts)) _editCts = null;
            Interlocked.Decrement(ref _activating);
            _gate.Release();
        }
    }

    /// <summary>
    /// 让一个内置模式改由自定义档承载（用厂商出厂值补齐全部固件项，参数不变）。
    /// 风扇曲线编辑器打开前调用：内置模式下写风扇表硬件不跟随，不能让用户在那里白改。
    /// 已经在自定义档上的模式直接返回 null。
    /// </summary>
    public async Task<PerfApplyOutcome?> EnsureFirmwareSlotAsync(string modeId)
    {
        PerfModeDefinition? mode = Find(modeId);
        if (mode is null || mode.Route == PerfModeRoute.FirmwareSlot) return null;
        PerfModeSettings seed = VendorModeDefaults.Seed(mode.Kind, _hw);
        PerfLiveSnapshot? live = string.Equals(ActiveModeId, mode.Id, StringComparison.Ordinal)
            ? SnapshotLive(includeFanCurves: true)
            : null;
        // 种子与回读都拿不到任何固件门控值时 Materialize 不会让模式转路线：调用方据此如实报失败，
        // 绝不拿一条全 0 的曲线去凑（那等于停转风扇）。
        return await UpdateAsync(modeId, s => PerfModeResolver.Materialize(s, seed, live), "fan curve editing")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 风扇曲线编辑器写好（并回读确认）了当前档的曲线：记进当前模式，档位换了也还是这条曲线。
    /// 当前模式不在自定义档上时忽略（内置模式的曲线由固件管理，编辑器不会在那里写）。
    /// </summary>
    public void RecordFanCurves(int[]? cpuDuty, int[]? gpuDuty)
    {
        PerfModeDefinition? mode = Active;
        if (mode is null || mode.Route != PerfModeRoute.FirmwareSlot) return;
        PerfModeSettings s = mode.Settings with
        {
            CpuFanDuty = cpuDuty is { Length: 16 } ? cpuDuty.ToArray() : mode.Settings.CpuFanDuty,
            GpuFanDuty = gpuDuty is { Length: 16 } ? gpuDuty.ToArray() : mode.Settings.GpuFanDuty,
        };
        if (s.FirmwareSignature() == mode.Settings.FirmwareSignature()) return;
        PerfModeStore.SaveSettings(mode.Id, s);
        IReadOnlyList<FirmwareSlotState> slots = PerfModeStore.LoadSlots(SlotCount);
        if (slots.FirstOrDefault(x => string.Equals(x.OwnerModeId, mode.Id, StringComparison.Ordinal)) is { } slot)
            PerfModeStore.SaveSlots(FirmwareSlotPlanner.Assign(slots, slot.Index, mode.Id, s.FirmwareSignature(), PerfModeStore.NowStamp()));
        Invalidate();
    }

    /// <summary>该模式此刻占着哪个固件自定义档（没有返回 null）。</summary>
    public int? SlotOf(string modeId) =>
        PerfModeStore.LoadSlots(SlotCount)
            .FirstOrDefault(s => string.Equals(s.OwnerModeId, modeId, StringComparison.Ordinal))?.Index;

    /// <summary>
    /// 恢复出厂：删掉该模式的全部覆盖项。内置模式回到官方原样下发（让出它占的自定义档）；
    /// 自定义模式在运行时让厂商把档位恢复默认，再把恢复后的参数落进配置。
    /// </summary>
    public async Task<bool> ResetAsync(string modeId)
    {
        PerfModeDefinition? mode = Find(modeId);
        if (mode is null) return false;
        bool active = string.Equals(ActiveModeId, mode.Id, StringComparison.Ordinal);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            PerfModeStore.ResetSettings(mode.Id);
            if (!mode.IsCustom)
                PerfModeStore.SaveSlots(FirmwareSlotPlanner.Release(PerfModeStore.LoadSlots(SlotCount), mode.Id));
            Invalidate();
        }
        finally { _gate.Release(); }

        if (!active || _hw is not { IsConnected: true }) return true;

        if (!mode.IsCustom)
        {
            PerfApplyOutcome? outcome = await ActivateAsync(mode.Id, "reset").ConfigureAwait(false);
            if (outcome is not { ModeSwitched: true }) return false;
            // 内置模式里就地写过的项（温度墙/显卡偏移/风扇转换灵敏度）存在厂商该模式的档位里：一并恢复。
            return await RestoreVendorProfileAsync().ConfigureAwait(false);
        }

        bool restored = await RestoreVendorProfileAsync().ConfigureAwait(false);
        FirmwareSlotState? slot = PerfModeStore.LoadSlots(SlotCount)
            .FirstOrDefault(s => string.Equals(s.OwnerModeId, mode.Id, StringComparison.Ordinal));
        if (restored && slot is not null)
            await CaptureSlotAsync(mode.With(PerfModeSettings.Default), slot.Index).ConfigureAwait(false);
        return restored;
    }

    async Task<bool> RestoreVendorProfileAsync()
    {
        try
        {
            long statusBefore = _hw.FanStatusVersion;
            long tableBefore = _hw.FanTableVersion;
            await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "RESTORE_OPERATING_MODE_DETAIL" }).ConfigureAwait(false);
            await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "RESTORE_FAN_SPEED_CURVE_SETTING", ["Name"] = _hw.TableName }).ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false);
            await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GETSTATUS" }).ConfigureAwait(false);
            await _hw.Publish(MqttTopics.FanControl, new Dictionary<string, object> { ["Action"] = "GET_FAN_SPEED_CURVE_SETTING" }).ConfigureAwait(false);
            return await _hw.WaitForStateAsync(
                () => _hw.FanStatusVersion > statusBefore && _hw.FanTableVersion > tableBefore,
                TimeSpan.FromMilliseconds(3000)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("PerfModes: vendor restore failed: " + ex.Message);
            return false;
        }
    }

    // ---- 模式集合 ----

    /// <summary>
    /// 新建自定义模式，以 <paramref name="seedFromId"/> 的**实际生效参数**为起点
    /// （正在运行的模式取运行时回读，内置模式取厂商出厂值）。返回 null = 已达上限。
    /// </summary>
    public PerfModeDefinition? AddCustom(string? seedFromId)
    {
        PerfModeSettings seed = EffectiveSettings(Find(seedFromId));
        PerfModeDefinition? created = PerfModeStore.AddCustom(null);
        if (created is null) return null;
        PerfModeStore.SaveSettings(created.Id, seed);
        Invalidate();
        return Find(created.Id);
    }

    /// <summary>一个模式此刻真正在用的参数（用于复制成新模式）。</summary>
    PerfModeSettings EffectiveSettings(PerfModeDefinition? mode)
    {
        if (mode is null) return PerfModeSettings.Default;
        bool running = string.Equals(ActiveModeId, mode.Id, StringComparison.Ordinal) && _hw.IsConnected;
        PerfModeSettings s = mode.Settings;
        if (!mode.IsCustom && mode.Route == PerfModeRoute.BuiltIn)
            s = PerfModeResolver.Materialize(s, VendorModeDefaults.Seed(mode.Kind, _hw), running ? SnapshotLive(false) : null);
        else if (running)
            s = SnapshotLive(includeFanCurves: true).FillNulls(s);
        return s;
    }

    /// <summary>删除自定义模式（最后一个不能删）。删的是当前模式时切回平衡。</summary>
    public bool RemoveCustom(string id)
    {
        bool wasActive = string.Equals(ActiveModeId, id, StringComparison.Ordinal);
        if (!PerfModeStore.RemoveCustom(id)) return false;
        if (string.Equals(PerfModeStore.LastCustomId, id, StringComparison.Ordinal))
            PerfModeStore.LastCustomId = null;
        Invalidate();
        if (wasActive) _ = ActivateAsync(PerfModeDefinition.BuiltInId(PerfModeKind.Balanced), "active custom mode removed");
        else RaiseActiveChanged();
        return true;
    }

    public void Rename(string id, string? name)
    {
        if (Find(id) is null) return;
        PerfModeStore.SaveName(id, name);
        Invalidate();
    }

    // ---- 跟随外部变化 ----

    /// <summary>
    /// 固件报告了模式变化（任何来源）。自己发起的切换在进行中时忽略——那次切换结束时自己落定状态；
    /// 其余情况去抖 700 ms 后对账一次。
    /// </summary>
    public void OnFirmwareModeChanged()
    {
        if (Volatile.Read(ref _activating) > 0) return;
        int seq = Interlocked.Increment(ref _reconcileSeq);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(700).ConfigureAwait(false);
                if (seq != Volatile.Read(ref _reconcileSeq) || Volatile.Read(ref _activating) > 0) return;
                await ReconcileAsync("firmware mode changed").ConfigureAwait(false);
            }
            catch (Exception ex) { Logger.WriteLine("PerfModes: reconcile failed: " + ex.Message); }
        });
    }

    async Task ReconcileAsync(string reason)
    {
        PerfModeResolution? redirect = null;
        int expectedRequest = Volatile.Read(ref _requestSeq);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (PendingModeId is not null || expectedRequest != Volatile.Read(ref _requestSeq)) return;
            if (_hw is not { IsConnected: true }) return;
            IReadOnlyList<FirmwareSlotState> slots = PerfModeStore.LoadSlots(SlotCount);
            PerfModeResolution? resolution = PerfModeResolver.Resolve(Modes, slots,
                _hw.OperatingMode, _hw.CustomProfileIndex, MechrevoService.IsSilentTurboActive, PerfModeStore.LastCustomId);
            if (resolution is null) return;

            if (resolution.Redirect && AllowRedirect())
            {
                Logger.WriteLine($"PerfModes: {reason}: redirect to {resolution.ModeId} ({resolution.Reason})");
                redirect = resolution;
            }
            else
            {
                if (string.Equals(resolution.ModeId, ActiveModeId, StringComparison.Ordinal)) return;
                PerfModeDefinition? mode = Find(resolution.ModeId);
                if (mode is null) return;
                Logger.WriteLine($"PerfModes: {reason}: follow {mode.Id} ({resolution.Reason})");
                PerfModeStore.ActiveModeId = mode.Id;
                PerfModeStore.SetActiveFor(PowerSourceKey(), mode.Id);
                if (mode.IsCustom) PerfModeStore.LastCustomId = mode.Id;
                RaiseActiveChanged();
                await ApplyAppSideLockedAsync(mode, "follow").ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
        if (redirect is not null && PendingModeId is null)
            await ActivateIfCurrentAsync(redirect.ModeId, "redirect: " + redirect.Reason, false, expectedRequest).ConfigureAwait(false);
    }

    /// <summary>10 秒内最多拉回 2 次：外部持续改模式时退为跟随，避免和它互相覆盖。</summary>
    bool AllowRedirect()
    {
        long now = Environment.TickCount64;
        lock (_redirectLock)
        {
            while (_recentRedirects.Count > 0 && now - _recentRedirects.Peek() > 10_000) _recentRedirects.Dequeue();
            if (_recentRedirects.Count >= 2)
            {
                Logger.WriteLine("PerfModes: redirect budget exhausted, following the external change instead");
                return false;
            }
            _recentRedirects.Enqueue(now);
            return true;
        }
    }

    /// <summary>只下发应用侧项与风扇增强（不切模式、不写固件参数）。</summary>
    async Task<PerfApplyOutcome> ApplyAppSideAsync(PerfModeDefinition mode, string reason)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (PendingModeId is not null || ActiveModeId != mode.Id || Find(mode.Id) is not { } current)
                return new PerfApplyOutcome(mode.Id, Array.Empty<PerfApplyStepOutcome>());
            return await ApplyAppSideLockedAsync(current, reason).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    async Task<PerfApplyOutcome> ApplyAppSideLockedAsync(PerfModeDefinition mode, string reason)
    {
        PerfModeSettings s = mode.Settings;
        var appSide = new PerfModeSettings
        {
            FanBoost = s.FanBoost,
            WindowsPowerMode = s.WindowsPowerMode,
            CpuBoost = s.CpuBoost,
            PowerPlanGuid = s.PowerPlanGuid,
            RefreshHz = s.RefreshHz,
        };
        IReadOnlyList<PerfApplyStep> steps = PerfModeApplyPlanner.Build(
            mode with { Settings = appSide }, new FirmwareSlotPlan(0, false, reason), Capabilities(),
            includeSwitch: false, routeOverride: PerfModeRoute.FirmwareSlot);
        PerfApplyOutcome outcome = await PerfModeRunner.RunAsync(mode.Id, steps, _backend).ConfigureAwait(false);
        if (steps.Count > 0) Logger.WriteLine($"PerfModes: app-side {mode.Id} ({reason}) -> {outcome.Describe()}");
        StartGuard(mode.Id);
        RaiseApplied(outcome);
        return outcome;
    }

    // ---- 重放 ----

    /// <summary>
    /// 启动 / 唤醒 / 插拔电源 / GCU 重连后重放。
    /// <paramref name="powerSourceChanged"/> 为 true 时切到该供电方式上次用的模式（G-Helper 行为）；
    /// <paramref name="appSideOnly"/> 为 true（纯唤醒）时不动模式，只把应用侧项重新落地
    /// （Windows 电源模式覆盖层按交流/电池分别保存，唤醒后可能不是用户选的那档）。
    /// </summary>
    public async Task ReplayAsync(string reason, bool powerSourceChanged, bool appSideOnly)
    {
        if (_hw is not { IsConnected: true }) return;
        PerfModeDefinition? target = null;
        int source = PowerSourceKey();
        if (powerSourceChanged) target = Find(PerfModeStore.ActiveFor(source));
        target ??= PerfModeStore.HasStoredActive ? Active : null;
        if (source == 0 && (target?.Kind is PerfModeKind.Turbo or PerfModeKind.SilentTurbo || powerSourceChanged && target is null))
            target = Find(PerfModeDefinition.BuiltInId(PerfModeKind.Silent));

        if (target is null)
        {
            // 全新安装：没有记录，跟随固件现状，不强行切到任何模式。
            await ReconcileAsync(reason + " (no stored mode)").ConfigureAwait(false);
            return;
        }

        IReadOnlyList<FirmwareSlotState> slots = PerfModeStore.LoadSlots(SlotCount);
        bool synchronized = target.Route != PerfModeRoute.FirmwareSlot || slots.Any(s =>
            s.OwnerModeId == target.Id && s.Signature == target.Settings.FirmwareSignature());
        if (synchronized && appSideOnly && string.Equals(target.Id, ActiveModeId, StringComparison.Ordinal))
        {
            await ApplyAppSideAsync(target, reason).ConfigureAwait(false);
            return;
        }

        bool onIt = PerfModeResolver.IsFirmwareOn(target, slots,
            _hw.OperatingMode, _hw.CustomProfileIndex, MechrevoService.IsSilentTurboActive,
            _hw.Capabilities.SilentTurboAvailability == FeatureAvailability.Supported);
        if (synchronized && onIt && string.Equals(target.Id, ActiveModeId, StringComparison.Ordinal))
        {
            await ApplyAppSideAsync(target, reason).ConfigureAwait(false);
            return;
        }
        await ActivateAsync(target.Id, reason).ConfigureAwait(false);
    }

    // ---- 守护 ----

    /// <summary>守护窗口的复查时刻（相对应用完成）。</summary>
    internal static int[] GuardCheckDelaysMs { get; set; } = { 1500, 3000, 6000, 10000 };

    void StartGuard(string modeId)
    {
        int request = Interlocked.Increment(ref _guardSeq);
        if (Program.UiAuditMode) return;
        PerfModeDefinition? mode = Find(modeId);
        if (mode is null || !mode.Settings.HasAppSideOverride && mode.Settings.FanBoost is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                int elapsed = 0;
                foreach (int at in GuardCheckDelaysMs)
                {
                    await Task.Delay(Math.Max(0, at - elapsed)).ConfigureAwait(false);
                    elapsed = at;
                    await _gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        // 新的切换/编辑/跟随都会结束守护：那些路径自己负责最终状态。
                        if (request != Volatile.Read(ref _guardSeq) || Volatile.Read(ref _activating) > 0 || PendingModeId is not null) return;
                        if (!string.Equals(ActiveModeId, modeId, StringComparison.Ordinal)) return;
                        PerfModeDefinition? current = Find(modeId);
                        if (current is null) return;
                        PerfModeSettings drifted = ProbeDrift(current.Settings);
                        if (!drifted.HasAppSideOverride && drifted.FanBoost is null) continue;
                        Logger.WriteLine($"PerfModes: {modeId} app-side settings changed externally, re-applying");
                        IReadOnlyList<PerfApplyStep> steps = PerfModeApplyPlanner.Build(
                            current with { Settings = drifted }, new FirmwareSlotPlan(0, false, "guard"), Capabilities(),
                            includeSwitch: false, routeOverride: PerfModeRoute.FirmwareSlot);
                        PerfApplyOutcome outcome = await PerfModeRunner.RunAsync(modeId, steps, _backend).ConfigureAwait(false);
                        if (outcome.AnyFailed) return;
                    }
                    finally { _gate.Release(); }
                }
            }
            catch (Exception ex) { Logger.WriteLine("PerfModes: guard failed: " + ex.Message); }
        });
    }

    /// <summary>只读探测：哪些应用侧项此刻不再成立。读不到的不算漂移（不据猜测重写）。</summary>
    PerfModeSettings ProbeDrift(PerfModeSettings s)
    {
        bool overlay = s.WindowsPowerMode is { } power && PowerNative.IsOverlayActive(power) == false;
        bool boost = s.CpuBoost is { } b && WinPowerPlan.TryGetBoost(out int ac, out int dc) && !WinPowerPlan.IsBoostConfirmed(b, ac, dc);
        bool plan = !string.IsNullOrEmpty(s.PowerPlanGuid)
            && WinPowerPlan.GetActivePlan() is { Length: > 0 } activePlan
            && !string.Equals(activePlan, s.PowerPlanGuid, StringComparison.OrdinalIgnoreCase);
        bool fan = s.FanBoost is { } f && _hw is { IsConnected: true, SupportsFanBoost: true } && _hw.FanBoost != f;
        bool hz = s.RefreshHz is { } r && r > 0 && _hw is { IsConnected: true, DcHz: false }
            && _hw.CurrentHz > 0 && _hw.CurrentHz != r && _hw.HzList.Contains(r);
        return new PerfModeSettings
        {
            WindowsPowerMode = overlay ? s.WindowsPowerMode : null,
            CpuBoost = boost ? s.CpuBoost : null,
            PowerPlanGuid = plan ? s.PowerPlanGuid : null,
            FanBoost = fan ? s.FanBoost : null,
            RefreshHz = hz ? s.RefreshHz : null,
        };
    }
}
