using System.Diagnostics;

namespace MechrevoLite.Hardware;

internal sealed class CpuPowerSampler : IDisposable
{
    const string CategoryName = "Energy Meter";
    const string CounterName = "Power";
    static readonly string[] PreferredInstances = [
        "Apu Power",
        "RAPL_Package0_PKG",
        "CPU Power",
        "Socket Power",
        "Current Socket Power",
    ];

    readonly object _sync = new();
    PerformanceCounter? _counter;
    int _initializing;
    long _nextInitializationTick;
    bool _disposed;

    public float? Sample()
    {
        PerformanceCounter? counter;
        lock (_sync)
        {
            if (_disposed) return null;
            counter = _counter;
        }

        if (counter is null)
        {
            StartInitialization();
            return null;
        }

        try
        {
            return NormalizeMilliwatts(counter.NextValue());
        }
        catch (Exception ex)
        {
            InvalidateCounter(counter, ex);
            return null;
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _nextInitializationTick = 0;
        }
        StartInitialization();
    }

    static float? NormalizeMilliwatts(float milliwatts) =>
        float.IsFinite(milliwatts) && milliwatts > 0 ? milliwatts / 1000f : null;

    void StartInitialization()
    {
        long now = Environment.TickCount64;
        lock (_sync)
        {
            if (_disposed || _counter is not null || now < _nextInitializationTick ||
                Interlocked.CompareExchange(ref _initializing, 1, 0) != 0)
                return;
        }

        _ = Task.Run(Initialize);
    }

    void Initialize()
    {
        PerformanceCounter? created = null;
        PerformanceCounter? previous = null;
        try
        {
            string[] instances = new PerformanceCounterCategory(CategoryName).GetInstanceNames();
            string? instance = PreferredInstances.FirstOrDefault(candidate =>
                instances.Contains(candidate, StringComparer.OrdinalIgnoreCase));
            if (instance is null)
            {
                Logger.WriteLineThrottled("cpu-energy-meter", "Energy Meter CPU power counter is unavailable.", 30000);
                return;
            }

            created = new PerformanceCounter(CategoryName, CounterName, instance, true);
            created.NextValue();

            lock (_sync)
            {
                if (_disposed) return;
                previous = _counter;
                _counter = created;
                created = null;
            }

            Logger.WriteLine("CPU power source: Energy Meter " + instance);
        }
        catch (Exception ex)
        {
            Logger.WriteLineThrottled("cpu-energy-meter", "Energy Meter CPU power initialization failed: " + ex.Message, 30000);
        }
        finally
        {
            previous?.Dispose();
            created?.Dispose();
            lock (_sync)
            {
                Interlocked.Exchange(ref _initializing, 0);
                if (_counter is null) _nextInitializationTick = Environment.TickCount64 + 30000;
            }
        }
    }

    void InvalidateCounter(PerformanceCounter counter, Exception error)
    {
        bool ownsCounter = false;
        lock (_sync)
        {
            if (ReferenceEquals(_counter, counter))
            {
                _counter = null;
                _nextInitializationTick = Environment.TickCount64 + 5000;
                ownsCounter = true;
            }
        }

        if (!ownsCounter) return;
        counter.Dispose();
        Logger.WriteLineThrottled("cpu-energy-meter", "Energy Meter CPU power read failed: " + error.Message, 5000);
    }

    public void Dispose()
    {
        PerformanceCounter? counter;
        lock (_sync)
        {
            _disposed = true;
            counter = _counter;
            _counter = null;
        }
        counter?.Dispose();
    }
}
