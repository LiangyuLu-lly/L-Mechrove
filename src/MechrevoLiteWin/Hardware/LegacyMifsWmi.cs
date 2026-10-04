using System.Management;

namespace MechrevoLite.Hardware;

internal readonly record struct LegacyMifsSnapshot(int OperatingMode, long Version);

internal sealed class LegacyMifsWmi : IDisposable
{
    const string InterfaceGuid = "B60BFB48-3E5B-49E4-A0E9-8CFFE1B3434B";
    readonly ManagementObject _instance;
    readonly object _gate = new();
    long _version;
    bool _disposed;
    internal bool Available { get; private set; }

    LegacyMifsWmi(ManagementObject instance) => _instance = instance;

    internal static LegacyMifsWmi? TryCreate()
    {
        LegacyMifsWmi? backend = null;
        try
        {
            using var definition = new ManagementClass(@"root\WMI:MICommonInterface");
            definition.Get();
            string guid = definition.Qualifiers["Guid"].Value?.ToString()?.Trim('{', '}') ?? "";
            if (!string.Equals(guid, InterfaceGuid, StringComparison.OrdinalIgnoreCase)) return null;
            using var instances = definition.GetInstances(new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(2) });
            foreach (ManagementObject instance in instances)
            {
                backend = new(instance);
                backend.ReadMode();
                return backend;
            }
        }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidClass or ManagementStatus.NotFound) { backend?.Dispose(); }
        catch (Exception ex) { backend?.Dispose(); Logger.WriteLine("Legacy MIFS detection failed: " + ex.Message); }
        return null;
    }

    internal static byte[] BuildModeRequest(bool write, int operatingMode = 1)
    {
        byte mode = operatingMode switch { 0 => 2, 1 => 0, 2 => 1, _ => throw new ArgumentOutOfRangeException(nameof(operatingMode)) };
        var request = new byte[32];
        request[1] = write ? (byte)251 : (byte)250;
        request[3] = 8;
        if (write) request[4] = mode;
        return request;
    }

    internal static int ReadOperatingMode(byte[] output)
    {
        // Windows exposes the first two ACPI bytes as Reserved and the remaining 30 as OutData.
        int index = output.Length switch { 30 => 2, 32 => 4, _ => throw new IOException("Invalid MIFS response length.") };
        return output[index] switch { 0 => 1, 1 or 3 => 2, 2 => 0, _ => throw new IOException("Unknown MIFS performance mode.") };
    }

    internal LegacyMifsSnapshot ReadMode()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                using ManagementBaseObject input = _instance.GetMethodParameters("MiInterface");
                input["InData"] = BuildModeRequest(false);
                using ManagementBaseObject output = _instance.InvokeMethod("MiInterface", input,
                    new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(2) });
                if (output["OutData"] is not byte[] bytes) throw new IOException("MIFS did not return OutData.");
                int mode = ReadOperatingMode(bytes);
                Available = true;
                return new(mode, ++_version);
            }
            catch { Available = false; throw; }
        }
    }

    internal async Task<bool> SwitchModeAsync(int operatingMode, CancellationToken ct)
    {
        byte[] request = BuildModeRequest(true, operatingMode);
        await Task.Run(() =>
        {
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                using ManagementBaseObject input = _instance.GetMethodParameters("MiInterface");
                input["InData"] = request;
                using ManagementBaseObject output = _instance.InvokeMethod("MiInterface", input,
                    new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(2) });
            }
        }, ct).ConfigureAwait(false);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if ((await Task.Run(ReadMode, ct).ConfigureAwait(false)).OperatingMode == operatingMode) return true;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        return false;
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; Available = false; _instance.Dispose(); }
    }
}
