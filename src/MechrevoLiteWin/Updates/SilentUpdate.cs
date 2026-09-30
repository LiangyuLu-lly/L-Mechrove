using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MechrevoLite.Update;

/// <summary>What a started in-app update expects to find when the app comes back.</summary>
internal sealed record PendingUpdate(string FromVersion, string ToVersion, DateTimeOffset StartedUtc, string SetupLog)
{
    internal string ExitCodeFile => SetupLog + ".exit";
}

internal enum UpdateOutcomeKind { None, Succeeded, Failed, Unknown }

internal sealed record UpdateOutcome(UpdateOutcomeKind Kind, string FromVersion, string ToVersion, int? ExitCode, string SetupLog);

/// <summary>
/// In-app update without a wizard (owner requirement: "内更新"): the verified Inno package runs with
/// <c>/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1</c>, the app exits, setup waits for it,
/// installs over the old version (settings, modes and fan curves are kept) and starts the app again
/// with <c>--after-update</c>. A small watcher process keeps the setup's exit code and brings the app
/// back even when setup fails, so the user is never left without the app and always sees the result.
/// </summary>
internal static class SilentUpdate
{
    internal const string PendingKey = "update_pending";
    static readonly TimeSpan PendingLifetime = TimeSpan.FromDays(2);

    internal static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "L-Mechrevo", "logs");

    /// <summary>Pure: the setup command line. The PID lets setup wait for this process to exit on its own.</summary>
    internal static string SetupArguments(int waitPid, string setupLog) =>
        "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1 /WAITPID=" + waitPid.ToString(CultureInfo.InvariantCulture)
        + " /LOG=\"" + setupLog + "\"";

    static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// Pure: the watcher (Windows PowerShell). It is not an L-Mechrevo.exe process, so setup's
    /// "stop the running app" step cannot take it down. On a failed setup it starts the app again.
    /// <para>It waits for the setup process itself, never for its process tree: <c>Start-Process -Wait</c>
    /// waits for every descendant, and the app that setup relaunches (<c>--after-update</c>) is one of them
    /// when the update runs elevated (the normal case) - the watcher would then stay alive for the whole
    /// session and record the exit code only when the app quits. Reading <c>Handle</c> first keeps
    /// <c>ExitCode</c> available after the exit on Windows PowerShell 5.1.</para>
    /// </summary>
    internal static string WatcherScript(string setupPath, string setupArguments, string exitCodeFile, string appExe) =>
        "$ErrorActionPreference = 'SilentlyContinue'\n"
        + "$p = Start-Process -FilePath " + PsQuote(setupPath) + " -ArgumentList " + PsQuote(setupArguments) + " -PassThru\n"
        + "if ($p) { $null = $p.Handle; $p.WaitForExit() }\n"
        + "$code = if ($p) { $p.ExitCode } else { -1 }\n"
        + "if ($null -eq $code) { $code = -1 }\n"
        + "Set-Content -LiteralPath " + PsQuote(exitCodeFile) + " -Value $code -Encoding ASCII\n"
        + "if ($code -ne 0) { Start-Process -FilePath " + PsQuote(appExe) + " -ArgumentList '--after-update' }\n";

    internal static string Format(PendingUpdate pending) => string.Join("|",
        pending.FromVersion, pending.ToVersion,
        pending.StartedUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), pending.SetupLog);

    internal static PendingUpdate? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string[] parts = raw.Split('|', 4);
        if (parts.Length != 4 || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long stamp))
            return null;
        return new PendingUpdate(parts[0], parts[1], DateTimeOffset.FromUnixTimeSeconds(stamp), parts[3]);
    }

    /// <summary>
    /// Pure verdict: the running version is the target -> succeeded; still the old version with a
    /// recorded setup exit code -> failed; old version and no exit code yet -> unknown (setup may
    /// still be running, or was killed). Stale records are dropped.
    /// </summary>
    internal static UpdateOutcome Evaluate(PendingUpdate? pending, string currentVersion, int? exitCode, DateTimeOffset now)
    {
        if (pending is null || now - pending.StartedUtc > PendingLifetime)
            return new UpdateOutcome(UpdateOutcomeKind.None, "", "", null, "");
        if (UpdateVersion.IsSameVersion(currentVersion, pending.ToVersion))
            return new UpdateOutcome(UpdateOutcomeKind.Succeeded, pending.FromVersion, pending.ToVersion, exitCode, pending.SetupLog);
        UpdateOutcomeKind kind = exitCode is int code && code != 0 ? UpdateOutcomeKind.Failed : UpdateOutcomeKind.Unknown;
        return new UpdateOutcome(kind, pending.FromVersion, pending.ToVersion, exitCode, pending.SetupLog);
    }

    /// <summary>Starts the watcher + silent setup. True = the app must now exit so setup can replace it.</summary>
    internal static bool Start(string setupPath, string targetVersion)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string setupLog = Path.Combine(LogDirectory, $"update-setup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var pending = new PendingUpdate(Program.ReleaseVersion ?? "", targetVersion, DateTimeOffset.UtcNow, setupLog);
            string appExe = Environment.ProcessPath ?? Application.ExecutablePath;
            string script = WatcherScript(setupPath, SetupArguments(Environment.ProcessId, setupLog), pending.ExitCodeFile, appExe);
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

            AppConfig.Set(PendingKey, Format(pending));
            AppConfig.Flush();
            using Process? watcher = Process.Start(new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            });
            if (watcher is null)
            {
                AppConfig.Remove(PendingKey);
                AppConfig.Flush();
                return false;
            }
            Logger.WriteLine($"Silent update started: {pending.FromVersion} -> {targetVersion}, setup log {setupLog}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Silent update could not start: " + ex.Message);
            AppConfig.Remove(PendingKey);
            AppConfig.Flush();
            return false;
        }
    }

    /// <summary>
    /// The outcome of the in-app update that led to this run, as a heartbeat token
    /// (<c>ok-&lt;version&gt;</c> / <c>fail-&lt;exit code&gt;</c> / <c>unknown</c>; empty when no update was pending).
    /// Lets the stats page show whether silent updates actually land on users' machines.
    /// </summary>
    internal static string LastOutcomeToken { get; private set; } = "";

    /// <summary>Pure: outcome -> heartbeat token (only characters the stats server accepts).</summary>
    internal static string OutcomeToken(UpdateOutcome outcome) => outcome.Kind switch
    {
        UpdateOutcomeKind.Succeeded => "ok-" + outcome.ToVersion,
        UpdateOutcomeKind.Failed => "fail-" + (outcome.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "x"),
        UpdateOutcomeKind.Unknown => "unknown",
        _ => "",
    };

    /// <summary>Reads (and, once final, clears) the pending update. Called once the UI is up.</summary>
    internal static UpdateOutcome CollectOutcome()
    {
        PendingUpdate? pending = Parse(AppConfig.GetString(PendingKey));
        if (pending is null) return new UpdateOutcome(UpdateOutcomeKind.None, "", "", null, "");
        int? exitCode = null;
        try
        {
            if (File.Exists(pending.ExitCodeFile)
                && int.TryParse(File.ReadAllText(pending.ExitCodeFile).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                exitCode = code;
        }
        catch (Exception ex) { Logger.WriteLine("Cannot read the update exit code: " + ex.Message); }

        UpdateOutcome outcome = Evaluate(pending, Program.ReleaseVersion ?? "", exitCode, DateTimeOffset.UtcNow);
        LastOutcomeToken = OutcomeToken(outcome);
        if (outcome.Kind != UpdateOutcomeKind.Unknown)
        {
            AppConfig.Remove(PendingKey);
            AppConfig.Flush();
        }
        Logger.WriteLine($"Update outcome: {outcome.Kind} {outcome.FromVersion} -> {outcome.ToVersion} exit={outcome.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        return outcome;
    }
}
