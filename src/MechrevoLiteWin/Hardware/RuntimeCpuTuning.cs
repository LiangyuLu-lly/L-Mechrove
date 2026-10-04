using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using PawnIO;

namespace MechrevoLite.Hardware;

internal sealed record CpuTuningOption(string Id, string LabelKey, decimal Minimum, decimal Maximum,
    string Unit, decimal? Current, bool CanWrite);
internal sealed record CpuTuningProbe(string Cpu, string StatusKey, string Detail, IReadOnlyList<CpuTuningOption> Options);
internal sealed record CpuTuningResult(bool Success, bool Verified, string Detail);

internal sealed class RuntimeCpuTuning : IDisposable
{
    internal static RuntimeCpuTuning Instance { get; } = new();
    internal static readonly object SyncRoot = new();
    internal static bool OwnsCurve { get; private set; }
    readonly PawnIOWrapper _io = new();
    RyzenSmuService? _smu;
    IntelOcMailbox? _intel;
    readonly HashSet<string> _changed = new();
    readonly HashSet<string> _denied = new();
    CpuTuningProbe? _probe;
    internal bool HasChanges { get { lock (SyncRoot) return _changed.Count > 0; } }

    internal CpuTuningProbe Probe()
    {
        lock (SyncRoot)
        {
            var options = new List<CpuTuningOption>();
            var errors = new List<string>();
            try
            {
                if (!X86Base.IsSupported) return Store("CpuTuneUnsupported", "x64 required", options);
                var connection = _io.Connect();
                if (connection != PawnIOWrapper.ConnectResult.OK)
                    return Store(connection == PawnIOWrapper.ConnectResult.NotInstalled ? "CpuTuneDriverMissing" :
                        connection == PawnIOWrapper.ConnectResult.AccessDenied ? "CpuTuneAdminRequired" : "CpuTuneProbeFailed",
                        connection.ToString(), options);
                if (CpuInfo.IsAMD)
                {
                    if (_smu?.IsInitialized != true)
                    {
                        _smu?.Dispose();
                        _smu = new RyzenSmuService();
                    }
                    if (!_smu.Initialize(Assembly.GetExecutingAssembly()))
                        return Store("CpuTuneProbeFailed", "RyzenSMU initialization rejected", options);
                    if (RyzenSmuService.CurveCommand(_smu.CpuCodeName) is not null)
                        options.Add(new("amd-co", "CpuTuneCurve", -30, 0, "CO", null, true));
                    if (RyzenSmuService.SupportsClock(_smu.CpuCodeName))
                        options.Add(new("amd-clock", "CpuTuneClock", 1000, RyzenSmuService.MaximumClock(_smu.CpuCodeName), "MHz", null, true));
                    return Store(options.Count > 0 ? "CpuTuneSmuReady" : "CpuTuneUnsupported",
                        $"{_smu.CpuCodeName}; SMU 0x{_smu.SmuVersion:X}", options);
                }
                if (!IsIntel()) return Store("CpuTuneUnsupported", "Unknown CPU vendor", options);
                if (_intel is null)
                {
                    using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("LMechrevo.IntelMSR.bin")
                        ?? throw new IOException("IntelMSR.bin missing");
                    using var buffer = new MemoryStream();
                    resource.CopyTo(buffer);
                    if (!_io.LoadModule(buffer.ToArray())) return Store("CpuTuneProbeFailed", "Signed IntelMSR module rejected", options);
                    _intel = new(_io.Execute);
                }
                foreach (int domain in new[] { 0, 2 })
                {
                    try
                    {
                        IntelOcDomain state = _intel.Probe(domain);
                        options.Add(new(domain == 0 ? "intel-core" : "intel-cache",
                            domain == 0 ? "CpuTuneCore" : "CpuTuneCache", -150, 0, "mV", state.OffsetMv,
                            state.VoltageSupported && !_denied.Contains(domain == 0 ? "intel-core" : "intel-cache")));
                        // Hybrid CPUs require separate P/E-core ratio validation before exposing runtime ratio writes.
                        if (domain == 0 && !IsHybrid() && state.RatioSupported)
                            options.Add(new("intel-ratio", "CpuTuneRatio", 8, state.MaximumRatio, "x", state.Ratio, !_denied.Contains("intel-ratio")));
                        errors.Add($"domain {domain}: capabilities=0x{state.Capabilities:X}, ratio={state.Ratio}/{state.MaximumRatio}");
                    }
                    catch (Exception ex) { errors.Add(ex.Message); }
                }
                if (_denied.Count > 0) errors.Add("Firmware rejected: " + string.Join(", ", _denied));
                return Store(options.Any(o => o.CanWrite) ? "CpuTuneIntelReady" : "CpuTuneUnsupported",
                    string.Join(Environment.NewLine, errors), options);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("CPU tuning probe: " + ex);
                return Store("CpuTuneProbeFailed", ex.Message, options);
            }
        }
    }

    CpuTuningProbe Store(string key, string detail, List<CpuTuningOption> options)
        => _probe = new(CpuInfo.Name, key, detail, options);

    internal CpuTuningResult Apply(string id, decimal value)
    {
        lock (SyncRoot)
        {
            var option = _probe?.Options.FirstOrDefault(o => o.Id == id);
            if (option is null || !option.CanWrite || value < option.Minimum || value > option.Maximum || value != decimal.Truncate(value))
                return new(false, false, "Setting unavailable or outside the permitted range.");
            bool previouslyChanged = _changed.Contains(id);
            try
            {
                _changed.Add(id);
                if (id.StartsWith("intel-", StringComparison.Ordinal))
                {
                    IntelOcDomain state = id == "intel-ratio" ? _intel!.Apply(0, ratio: (int)value) :
                        _intel!.Apply(id == "intel-core" ? 0 : 2, offsetMv: value);
                    return new(true, true, id == "intel-ratio" ? $"{state.Ratio}x" : $"{state.OffsetMv:F3} mV");
                }
                if (id == "amd-co") OwnsCurve = true;
                SmuStatus status = id == "amd-co" ? _smu!.SetCoAll((int)value) : _smu!.SetOcClock((int)value);
                return new(status == SmuStatus.OK, false, $"SMU: {status} (0x{(uint)status:X2})");
            }
            catch (Exception ex)
            {
                if (ex.Data.Contains("MailboxWriteRejected")) _denied.Add(id);
                if (!previouslyChanged && ex.Data.Contains("PreviousValueUnchanged")) _changed.Remove(id);
                Logger.WriteLine("CPU tuning apply: " + ex);
                return new(false, false, ex.Message);
            }
        }
    }

    internal CpuTuningResult Restore()
    {
        lock (SyncRoot)
        {
            var failures = new List<string>();
            var restored = new HashSet<string>();
            bool verified = true;
            foreach (string id in _changed.ToArray())
            {
                try
                {
                    if (id.StartsWith("intel-", StringComparison.Ordinal))
                    {
                        string domain = id == "intel-cache" ? "cache" : "core";
                        if (!restored.Contains(domain))
                        {
                            _intel!.Restore(domain == "cache" ? 2 : 0);
                            restored.Add(domain);
                        }
                    }
                    else
                    {
                        verified = false;
                        SmuStatus status = id == "amd-co" ? _smu!.SetCoAll(0) : _smu!.DisableOc();
                        if (status != SmuStatus.OK) throw new InvalidOperationException($"{id}: {status}");
                        if (id == "amd-co") OwnsCurve = false;
                    }
                    _changed.Remove(id);
                }
                catch (Exception ex) { failures.Add(ex.Message); Logger.WriteLine("CPU tuning restore: " + ex); }
            }
            return new(failures.Count == 0, verified, string.Join(Environment.NewLine, failures));
        }
    }

    internal static bool IsIntel()
    {
        if (!X86Base.IsSupported) return false;
        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        Span<int> vendor = stackalloc[] { ebx, edx, ecx };
        return MemoryMarshal.AsBytes(vendor).SequenceEqual("GenuineIntel"u8);
    }

    internal static bool IsHybrid() => X86Base.IsSupported && (X86Base.CpuId(7, 0).Edx & (1 << 15)) != 0;

    public void Dispose()
    {
        lock (SyncRoot) { _intel?.Dispose(); _smu?.Dispose(); _io.Dispose(); }
    }
}
