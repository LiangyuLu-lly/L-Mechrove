using System.Diagnostics;
using MechrevoLite.Gpu.NVidia;
using MechrevoLite.Helpers;

namespace MechrevoLite.Gpu;

internal readonly record struct DgpuApplication(
    int ProcessId,
    string ProcessName,
    int SessionId,
    DateTime? StartedAtUtc = null,
    bool HasMainWindow = false);

internal readonly record struct DgpuApplicationSnapshot(
    bool IsAvailable,
    IReadOnlyList<DgpuApplication> Applications);

internal static class DgpuApplicationSafety
{
    static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "dwm", "csrss", "winlogon", "services", "lsass", "smss", "wininit",
        "svchost", "fontdrvhost", "igfxem", "igfxhk", "igfxext", "nvcontainer", "nvdisplay.container",
        "nvsettings", "nvspcaps64", "nvsphelper64", "nvwmi64", "nvcplui", "atieclxx", "atiesrxx",
        "explorer", "taskhostw", "sihost", "runtimebroker", "shellexperiencehost", "searchhost",
        "startmenuexperiencehost", "textinputhost", "applicationframehost", "systemsettings", "dllhost",
        "conhost", "audiodg", "ctfloader", "spoolsv", "wlanext", "msdtc",
        "l-mechrevo", "gcuservice", "gcubridge", "aistoneservice",
    };

    internal static IReadOnlyList<DgpuApplication> FilterClosable(
        IEnumerable<DgpuApplication> applications,
        int currentProcessId,
        int currentSessionId) => applications.Where(application => IsSafeCandidate(
            application, currentProcessId, currentSessionId)).ToArray();

    internal static bool IsSafeCandidate(
        DgpuApplication application,
        int currentProcessId,
        int currentSessionId) =>
            application.ProcessId > 0 &&
            application.ProcessId != currentProcessId &&
            application.SessionId > 0 &&
            application.SessionId == currentSessionId &&
            application.StartedAtUtc.HasValue &&
            !string.IsNullOrWhiteSpace(application.ProcessName) &&
            !IsProtectedProcessName(application.ProcessName);

    static bool IsProtectedProcessName(string processName) =>
        ProtectedProcessNames.Contains(processName) ||
        processName.StartsWith("controlcenter", StringComparison.OrdinalIgnoreCase);
}

internal static class DgpuApplicationCoordinator
{
    internal static DgpuApplicationSnapshot Snapshot()
    {
        using Process current = Process.GetCurrentProcess();
        if (!NvidiaGpuControl.TryGetActiveDgpuApplications(out IReadOnlyList<DgpuApplication> applications))
            return new(false, Array.Empty<DgpuApplication>());

        return new(true, DgpuApplicationSafety.FilterClosable(applications, current.Id, current.SessionId));
    }

    internal static async Task<DgpuApplicationSnapshot> RequestGracefulCloseAsync(
        IEnumerable<DgpuApplication> applications)
    {
        foreach (DgpuApplication candidate in applications)
        {
            try
            {
                using Process process = Process.GetProcessById(candidate.ProcessId);
                if (!CanManage(process, candidate) || !candidate.HasMainWindow || process.HasExited ||
                    process.MainWindowHandle == IntPtr.Zero)
                    continue;

                process.CloseMainWindow();
            }
            catch (ArgumentException)
            {
                Logger.WriteLine($"dGPU application already exited before graceful close: pid={candidate.ProcessId} name={candidate.ProcessName}");
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"dGPU application graceful close failed: pid={candidate.ProcessId} name={candidate.ProcessName} error={ex.Message}");
            }
        }

        await Task.Delay(800).ConfigureAwait(false);
        return Snapshot();
    }

    internal static async Task<DgpuApplicationSnapshot> ForceCloseAsync(
        IEnumerable<DgpuApplication> applications)
    {
        foreach (DgpuApplication candidate in applications)
        {
            try
            {
                using Process process = Process.GetProcessById(candidate.ProcessId);
                if (!CanManage(process, candidate) || process.HasExited)
                    continue;

                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                Logger.WriteLine($"dGPU application already exited before force close: pid={candidate.ProcessId} name={candidate.ProcessName}");
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"dGPU application force close failed: pid={candidate.ProcessId} name={candidate.ProcessName} error={ex.Message}");
            }
        }

        return Snapshot();
    }

    static bool CanManage(Process process, DgpuApplication candidate)
    {
        using Process current = Process.GetCurrentProcess();
        if (!MatchesCandidate(process, candidate)) return false;
        return DgpuApplicationSafety.IsSafeCandidate(candidate, current.Id, current.SessionId);
    }

    static bool MatchesCandidate(Process process, DgpuApplication candidate)
    {
        if (!string.Equals(process.ProcessName, candidate.ProcessName, StringComparison.OrdinalIgnoreCase) ||
            process.SessionId != candidate.SessionId)
            return false;

        if (!candidate.StartedAtUtc.HasValue) return false;
        try
        {
            return process.StartTime.ToUniversalTime() == candidate.StartedAtUtc.Value;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"dGPU application identity check failed: pid={candidate.ProcessId} name={candidate.ProcessName} error={ex.Message}");
            return false;
        }
    }
}
