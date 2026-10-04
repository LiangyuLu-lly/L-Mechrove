using System.Diagnostics;

namespace PawnIO;

internal sealed record IntelOcDomain(int Id, uint Value, uint Capabilities)
{
    public bool VoltageSupported => (Capabilities & (1u << 10)) != 0;
    public bool RatioSupported => (Capabilities & (1u << 8)) != 0 && MaximumRatio >= Ratio && Ratio > 0;
    public int MaximumRatio => (int)(Capabilities & 0xFF);
    public int Ratio => (int)(Value & 0xFF);
    public decimal OffsetMv => IntelOcMailbox.DecodeOffset(Value);
}

internal sealed class IntelOcMailbox : IDisposable
{
    const ulong Busy = 1UL << 63;
    const uint OffsetMask = 0xFFE00000;
    readonly Func<string, ulong[]?, ulong[]?, bool> _execute;
    readonly Mutex _mutex = new(false, @"Global\Access_Intel_OC_Mailbox");
    readonly Dictionary<int, uint> _baseline = new();

    internal IntelOcMailbox(Func<string, ulong[]?, ulong[]?, bool> execute) => _execute = execute;

    internal IntelOcDomain Probe(int domain) => Locked(() =>
    {
        ValidateDomain(domain);
        uint capabilities = Command(0x01, domain, 0);
        uint value = Command(0x10, domain, 0);
        _baseline.TryAdd(domain, value);
        return new IntelOcDomain(domain, value, capabilities);
    });

    internal IntelOcDomain Apply(int domain, decimal? offsetMv = null, int? ratio = null) => Locked(() =>
    {
        IntelOcDomain before = Probe(domain);
        uint desired = before.Value;
        if (offsetMv.HasValue)
        {
            if (!before.VoltageSupported) throw new InvalidOperationException("Voltage offset unsupported or locked.");
            desired = (desired & ~OffsetMask) | EncodeOffset(offsetMv.Value);
        }
        if (ratio.HasValue)
        {
            if (!before.RatioSupported || ratio.Value < 8 || ratio.Value > before.MaximumRatio)
                throw new ArgumentOutOfRangeException(nameof(ratio), "Ratio exceeds the reported OC capability.");
            desired = (desired & ~0xFFu) | (uint)ratio.Value;
        }
        WriteVerified(domain, desired, before.Value);
        return before with { Value = desired };
    });

    internal void Restore(int domain) => Locked(() =>
    {
        ValidateDomain(domain);
        if (!_baseline.TryGetValue(domain, out uint value))
            throw new InvalidOperationException("No captured baseline.");
        uint current = Command(0x10, domain, 0);
        WriteVerified(domain, value, current);
        return true;
    });

    void WriteVerified(int domain, uint desired, uint previous)
    {
        if (desired == previous) return;
        try
        {
            Command(0x11, domain, desired);
            if (Command(0x10, domain, 0) != desired)
                throw new InvalidOperationException("CPU readback differs; firmware protection may have rejected the change.");
        }
        catch (Exception original)
        {
            bool unchanged = false;
            try
            {
                unchanged = Command(0x10, domain, 0) == previous;
            }
            catch (Exception readback) { original.Data["RecoveryReadbackFailure"] = readback.Message; }
            if (unchanged) throw Failure(original, "Previous value unchanged.", unchanged: true);
            try
            {
                Command(0x11, domain, previous);
                if (Command(0x10, domain, 0) != previous)
                    throw new InvalidOperationException("Baseline readback differs.");
            }
            catch (Exception rollback)
            {
                throw Failure(original, $"Restore failed: {rollback.Message}");
            }
            throw Failure(original, "Previous value restored.");
        }
    }

    static InvalidOperationException Failure(Exception original, string detail, bool unchanged = false)
    {
        if (original.Data["RecoveryReadbackFailure"] is string readFailure)
            detail += " Recovery readback failed: " + readFailure;
        var failure = new InvalidOperationException($"{original.Message} {detail}", original);
        if (original.Data.Contains("MailboxWriteRejected")) failure.Data["MailboxWriteRejected"] = true;
        if (unchanged) failure.Data["PreviousValueUnchanged"] = true;
        return failure;
    }

    uint Command(byte command, int domain, uint value)
    {
        ReadIdle();
        ulong request = Busy | ((ulong)domain << 40) | ((ulong)command << 32) | value;
        if (!_execute("ioctl_write_msr", new ulong[] { 0x150, request }, null))
            throw new IOException("OC Mailbox write denied or driver unavailable.");
        ulong response = ReadIdle();
        byte completion = (byte)(response >> 32);
        if (completion != 0)
        {
            var error = new InvalidOperationException($"OC Mailbox rejected command 0x{command:X2}: 0x{completion:X2}.");
            if (command == 0x11) error.Data["MailboxWriteRejected"] = true;
            throw error;
        }
        return (uint)response;
    }

    ulong ReadIdle()
    {
        var timer = Stopwatch.StartNew();
        ulong[] output = new ulong[1];
        do
        {
            if (!_execute("ioctl_read_msr", new ulong[] { 0x150 }, output))
                throw new IOException("OC Mailbox read denied or driver unavailable.");
            if ((output[0] & Busy) == 0) return output[0];
            Thread.Sleep(1);
        } while (timer.ElapsedMilliseconds < 200);
        throw new TimeoutException("OC Mailbox remained busy for 200 ms.");
    }

    T Locked<T>(Func<T> action)
    {
        bool acquired;
        try { acquired = _mutex.WaitOne(1500); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new TimeoutException("Another CPU tuning request holds the mailbox.");
        try { return action(); }
        finally { _mutex.ReleaseMutex(); }
    }

    static void ValidateDomain(int domain)
    {
        if (domain is not (0 or 2)) throw new ArgumentOutOfRangeException(nameof(domain));
    }

    internal static uint EncodeOffset(decimal mv)
    {
        if (mv is < -150 or > 0) throw new ArgumentOutOfRangeException(nameof(mv));
        int units = (int)decimal.Round(mv * 1.024m, 0, MidpointRounding.AwayFromZero);
        return (unchecked((uint)units) & 0x7FF) << 21;
    }

    internal static decimal DecodeOffset(uint value)
    {
        int units = (int)(value >> 21) & 0x7FF;
        if ((units & 0x400) != 0) units -= 0x800;
        return units / 1.024m;
    }

    public void Dispose() => _mutex.Dispose();
}
