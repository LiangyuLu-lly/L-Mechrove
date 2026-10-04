using System.Text;
using System.Text.Json;

namespace MechrevoLite.Usage;

internal sealed record TelemetryLogBatch(string Id, string Ver, string Batch, string Log, long CrashStamp, int Errors);

internal sealed class TelemetryLogOutbox(string directory)
{
    internal const int MaxBatches = 32;
    readonly object _gate = new();

    internal string Enqueue(TelemetryLogBatch batch)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{DateTime.UtcNow.Ticks:D19}-{batch.Batch}.json");
            string temporary = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, batch, UsageTelemetry.WireJson);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            string[] files = Files();
            foreach (string old in files.Take(Math.Max(0, files.Length - MaxBatches)))
            {
                File.Delete(old);
                Logger.WriteInfo("usage log backlog capacity reached; the oldest snapshot expired");
            }
            return path;
        }
    }

    internal (string Path, TelemetryLogBatch Batch)? Peek()
    {
        lock (_gate)
        {
            foreach (string path in Files())
            {
                try
                {
                    if (new FileInfo(path).Length > 192 * 1024) throw new InvalidDataException("oversized outbox entry");
                    var batch = JsonSerializer.Deserialize<TelemetryLogBatch>(File.ReadAllText(path, Encoding.UTF8));
                    if (batch is null || string.IsNullOrEmpty(batch.Batch) || batch.Batch.Length != 32 ||
                        string.IsNullOrEmpty(batch.Id) || batch.Id.Length < 8 || batch.Ver is null || batch.Log is null ||
                        Encoding.UTF8.GetByteCount(batch.Log) > UsageTelemetry.MaxLogBytes)
                        throw new InvalidDataException("invalid outbox entry");
                    return (path, batch);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    File.Move(path, path + ".invalid", overwrite: true);
                    Logger.WriteInfo("usage log backlog entry quarantined: " + ex.Message);
                }
            }
            return null;
        }
    }

    internal void Acknowledge(string path)
    {
        lock (_gate) File.Delete(path);
    }

    internal void Clear()
    {
        lock (_gate)
        {
            if (!Directory.Exists(directory)) return;
            foreach (string path in Directory.GetFiles(directory)) File.Delete(path);
        }
    }

    string[] Files() => Directory.Exists(directory)
        ? Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal).ToArray()
        : [];
}
