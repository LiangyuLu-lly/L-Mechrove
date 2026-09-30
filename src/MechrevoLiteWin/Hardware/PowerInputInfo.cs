namespace MechrevoLite.Hardware;

/// <summary>供电方式（只做展示，不驱动任何行为，见 docs/hardware/hidden-readonly-info-plan.md §3）。</summary>
public enum PowerInputKind
{
    /// <summary>还没采样过。</summary>
    Unknown,

    /// <summary>Windows 报告离线：电池供电。</summary>
    Battery,

    /// <summary>圆口（DC 圆孔）适配器在插。</summary>
    Barrel,

    /// <summary>Type-C（PD）在充电。</summary>
    TypeC,

    /// <summary>圆口 + Type-C 同时在插（双插语义未实测，先按圆口显示）。</summary>
    BarrelAndTypeC,

    /// <summary>Windows 报告交流在线，但读不到 / 不解码 EC：不猜类型。</summary>
    ExternalUnknown,
}

/// <summary>
/// 一次供电采样。<see cref="ComplexStatusRaw"/> / <see cref="BiosInfo3Raw"/> 为 -1 表示没读到（或本机不解码），
/// 进 tooltip 与诊断包，不参与任何判定之外的用途。引用类型：采样线程整体发布，界面线程无锁读取。
/// </summary>
public sealed record PowerInputSample(
    PowerInputKind Kind,
    int? AdapterWatts,
    int ComplexStatusRaw,
    int BiosInfo3Raw,
    long TakenTick,
    bool DecodeEnabled)
{
    /// <summary>这次采样有没有读到 EC（没读到时只按 Windows 分电池/外接电源）。</summary>
    public bool EcReadable => ComplexStatusRaw >= 0;
}

/// <summary>
/// 供电寄存器解码（纯函数）。厂商读法（S40 <c>MyECIO/MyEcCtrl.cs</c>、<c>GCService5/GPUDeviceItem.cs</c>）：
/// <c>0x7CC</c> bit0 = 圆口在插，<c>&amp; 0x06</c> = Type-C 在充电；<c>0x49F &amp; 0x78</c> 查适配器瓦数表。
/// </summary>
public static class PowerInputDecoder
{
    /// <summary>ADDR_COMPLEX_POWER_STATUS（S40 <c>Define/ECSpec.cs:405</c>）。</summary>
    public const int ComplexPowerStatusAddress = 0x7CC;

    /// <summary>ADDR_BIOS_INFO_3_BYTE（S40 <c>Define/ECSpec.cs:131</c>）；同一字节 bit1 是狂暴模式支持，解码前必须掩码。</summary>
    public const int BiosInfo3Address = 0x49F;

    /// <summary>圆口在插（S40 <c>MyEcCtrl.cs:150-157</c> GetRoundSocketStatusFromEC）。</summary>
    public const int RoundSocketMask = 0x01;

    /// <summary>Type-C 在充电（S40 <c>GPUDeviceItem.cs:128-145</c> isTypeCCharging：<c>(TypeC &amp; 6) &gt; 0</c>）。</summary>
    public const int TypeCChargingMask = 0x06;

    /// <summary>适配器码位（S40 <c>MyEcCtrl.cs:106-143</c>）。</summary>
    public const int AdapterCodeMask = 0x78;

    /// <summary>
    /// §2.1 真值表。Windows 的交流状态为准：离线一律「电池」；在线时 EC 可读且本机解码才细分圆口 / Type-C，
    /// 否则只说「外接电源」——bit0 与 Type-C 位都没有时厂商会当成圆口，我方不猜。
    /// </summary>
    public static PowerInputKind Decode(bool acOnline, int complexStatus, bool decodeEnabled)
    {
        if (!acOnline) return PowerInputKind.Battery;
        if (!decodeEnabled || complexStatus < 0) return PowerInputKind.ExternalUnknown;
        bool round = (complexStatus & RoundSocketMask) != 0;
        bool typeC = (complexStatus & TypeCChargingMask) != 0;
        return (round, typeC) switch
        {
            (true, false) => PowerInputKind.Barrel,
            (false, true) => PowerInputKind.TypeC,
            (true, true) => PowerInputKind.BarrelAndTypeC,
            _ => PowerInputKind.ExternalUnknown,
        };
    }

    /// <summary>
    /// 采样与「现在」的 Windows 交流状态合并：采样最多 5 s 前的，插拔后到下一次采样之间以 Windows 为准——
    /// 已拔电就是电池；已插电但采样还停在电池 / 没采过，只说「外接电源」，不沿用旧的细分。
    /// </summary>
    public static PowerInputKind Reconcile(PowerInputSample? sample, bool acOnlineNow)
    {
        if (!acOnlineNow) return PowerInputKind.Battery;
        return sample?.Kind switch
        {
            null or PowerInputKind.Unknown or PowerInputKind.Battery => PowerInputKind.ExternalUnknown,
            PowerInputKind kind => kind,
        };
    }

    /// <summary>
    /// 适配器额定瓦数，只在厂商表内时返回 true。厂商表外默认 150 W（机型展开用，见 <see cref="ModelRegistry"/>），
    /// 展示时绝不回落：本机码 <c>0x58</c> 不在表内，显示「未知」。
    /// </summary>
    public static bool TryDecodeAdapterWatts(int raw, out int watts)
    {
        watts = 0;
        if (raw < 0) return false;
        watts = (raw & AdapterCodeMask) switch
        {
            0x00 => 330,
            0x08 => 230,
            0x10 => 180,
            0x18 => 150,
            0x20 => 120,
            0x28 => 90,
            0x30 => 65,
            0x38 => 40,
            0x40 => 280,
            _ => 0,
        };
        return watts > 0;
    }
}

/// <summary>
/// 供电采样器：只读 <c>0x7CC</c>（每次采样 1 字节）与 <c>0x49F</c>（首读、<c>0x7CC</c> 变化、交流变化时各 1 字节），
/// 5 s 限频，失败退化为只按 Windows 分电池/外接电源。线程安全；读取由调用方放后台（见 <c>HardwareControl.RefreshPowerInput</c>）。
/// </summary>
public sealed class PowerInputSampler
{
    public const int RefreshIntervalMilliseconds = 5000;
    const int MaxConsecutiveFailures = 3;

    readonly Func<Probe.IEcReadTransport?> _transport;
    readonly Func<bool> _acOnline;
    readonly Func<bool> _decodeEnabled;
    readonly Func<long> _tick;
    readonly object _sync = new();
    PowerInputSample? _current;
    int _lastComplex = int.MinValue;
    bool? _lastAc;
    int _lastBiosInfo3 = -1;
    int _failures;

    public PowerInputSampler(
        Func<Probe.IEcReadTransport?> transport,
        Func<bool> acOnline,
        Func<bool> decodeEnabled,
        Func<long>? tick = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _acOnline = acOnline ?? throw new ArgumentNullException(nameof(acOnline));
        _decodeEnabled = decodeEnabled ?? throw new ArgumentNullException(nameof(decodeEnabled));
        _tick = tick ?? (() => Environment.TickCount64);
    }

    /// <summary>
    /// 最近一次采样（从未采样时为 null）。不取锁：驱动卡住时采样线程握着锁，界面线程照样能拿到上一次的结果。
    /// </summary>
    public PowerInputSample? Current => Volatile.Read(ref _current);

    /// <summary>是否该采样了（没采过、超过 5 s，或强制）。调用方据此决定要不要起后台读取。</summary>
    public bool NeedsRefresh(bool force = false) =>
        force || Current is not { } cached || SafeTick() - cached.TakenTick >= RefreshIntervalMilliseconds;

    /// <summary>采样一次；5 s 内的重复调用直接返回缓存，<paramref name="force"/> 例外（插拔事件）。</summary>
    public PowerInputSample Refresh(bool force = false)
    {
        lock (_sync)
        {
            long now = SafeTick();
            if (!force && _current is { } cached && now - cached.TakenTick < RefreshIntervalMilliseconds)
                return cached;

            bool ac = SafeAc();
            bool decode = SafeDecodeEnabled();
            int complex = -1;
            int biosInfo3 = -1;
            if (decode)
            {
                Probe.IEcReadTransport? transport = null;
                try
                {
                    transport = _transport();
                    if (transport is not null)
                    {
                        complex = transport.ReadByte(PowerInputDecoder.ComplexPowerStatusAddress);
                        bool needAdapter = _lastBiosInfo3 < 0 || complex != _lastComplex || _lastAc != ac;
                        biosInfo3 = complex >= 0 && needAdapter
                            ? transport.ReadByte(PowerInputDecoder.BiosInfo3Address)
                            : _lastBiosInfo3;
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLineIfChanged("power-input", "Power input EC read failed: " + ex.Message);
                    complex = -1;
                    biosInfo3 = -1;
                }
                finally
                {
                    (transport as IDisposable)?.Dispose();
                }
            }

            if (decode && complex < 0)
            {
                _failures++;
                if (_failures >= MaxConsecutiveFailures) _lastBiosInfo3 = -1;   // 连续失败：不再沿用旧的适配器码
            }
            else
            {
                _failures = 0;
                if (biosInfo3 >= 0) _lastBiosInfo3 = biosInfo3;
            }
            _lastComplex = complex;
            _lastAc = ac;

            PowerInputKind kind = PowerInputDecoder.Decode(ac, complex, decode);
            int? watts = kind is PowerInputKind.Barrel or PowerInputKind.BarrelAndTypeC &&
                         PowerInputDecoder.TryDecodeAdapterWatts(_lastBiosInfo3, out int decoded)
                ? decoded
                : null;
            var sample = new PowerInputSample(kind, watts, complex, decode ? _lastBiosInfo3 : -1, now, decode);
            if (_current is not { } previous || previous.Kind != sample.Kind || previous.ComplexStatusRaw != sample.ComplexStatusRaw)
                Logger.WriteLineIfChanged("power-input",
                    $"Power input: {kind} (ac={ac}, 0x7CC={(complex >= 0 ? "0x" + complex.ToString("X2") : "-")}, 0x49F={(sample.BiosInfo3Raw >= 0 ? "0x" + sample.BiosInfo3Raw.ToString("X2") : "-")}, watts={watts?.ToString() ?? "-"})");
            Volatile.Write(ref _current, sample);
            return sample;
        }
    }

    long SafeTick()
    {
        try { return _tick(); }
        catch { return Environment.TickCount64; }
    }

    bool SafeAc()
    {
        try { return _acOnline(); }
        catch { return false; }
    }

    bool SafeDecodeEnabled()
    {
        try { return _decodeEnabled(); }
        catch { return false; }
    }
}
