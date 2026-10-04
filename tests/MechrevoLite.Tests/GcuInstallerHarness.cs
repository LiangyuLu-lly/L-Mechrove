using System.Diagnostics;
using System.Text;

namespace MechrevoLite.Tests;

/// <summary>
/// Wave E harness: locates the repo root, reads installer assets, and runs the installer
/// PowerShell scripts as real child processes. The GCU installer scripts are external
/// surfaces (Inno [Run] invokes them), so the tests exercise the scripts themselves
/// rather than a C# re-implementation. Hardware-dependent end-to-end steps stay BLOCKED-HW.
/// </summary>
internal sealed record PsResult(int ExitCode, string StdOut, string StdErr)
{
    internal string Combined => StdOut + "\n" + StdErr;
}

internal static class GcuInstallerHarness
{
    internal static string RepoRoot { get; } = FindRepoRoot();

    static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        if (directory is null)
            throw new InvalidOperationException("MechrevoLite.slnx not found above " + AppContext.BaseDirectory);
        return directory.FullName;
    }

    internal static string Path(params string[] tail)
        => System.IO.Path.Combine(new[] { RepoRoot }.Concat(tail).ToArray());

    internal static string Read(params string[] tail) => File.ReadAllText(Path(tail));

    internal static PsResult RunScript(string scriptRelative, params string[] args)
    {
        var psi = BaseStartInfo();
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(Path(scriptRelative.Split('\\')));
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        return Run(psi);
    }

    /// <summary>Runs an inline PowerShell statement (used to dot-source a script and call its functions).</summary>
    internal static PsResult RunCommand(string command)
    {
        var psi = BaseStartInfo();
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);
        return Run(psi);
    }

    static ProcessStartInfo BaseStartInfo()
    {
        var info = new ProcessStartInfo
        {
        FileName = "powershell.exe",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
        };
        // PowerShell 7 的模块路径会遮蔽 Windows PowerShell 自带的 Get-FileHash 等命令。
        info.Environment["PSModulePath"] = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\Modules");
        return info;
    }

    static PsResult Run(ProcessStartInfo psi)
    {
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start powershell.exe");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException("installer script did not exit within 120s: " + psi.FileName);
        }
        return new PsResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    /// <summary>Dot-sources an installer script and evaluates <paramref name="expression"/> in that scope.</summary>
    internal static PsResult RunDotSourced(string scriptRelative, string expression)
    {
        string escaped = Path(scriptRelative.Split('\\')).Replace("'", "''");
        return RunCommand(". '" + escaped + "'; " + expression);
    }

    /// <summary>
    /// Dot-sources an installer script WITH parameters (mandatory params would otherwise prompt)
    /// and then evaluates <paramref name="expression"/> in that scope.
    /// </summary>
    internal static PsResult RunDotSourcedArgs(string scriptRelative, string dotSourceArgs, string expression)
    {
        string escaped = Path(scriptRelative.Split('\\')).Replace("'", "''");
        return RunCommand(". '" + escaped + "' " + dotSourceArgs + "; " + expression);
    }
}
