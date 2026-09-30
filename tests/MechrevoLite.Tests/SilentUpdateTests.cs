using System.Diagnostics;
using System.Text;
using MechrevoLite.Update;

namespace MechrevoLite.Tests;

/// <summary>
/// In-app update ("内更新"): the verified setup runs silently over the old install, the app is
/// started again afterwards, and the result (updated / not updated) is reported to the user.
/// </summary>
public class SilentUpdateTests
{
    [Fact]
    public void SetupArguments_AreSilentRelaunchAndWaitForTheApp()
    {
        string args = SilentUpdate.SetupArguments(4242, @"C:\ProgramData\L-Mechrevo\logs\update-setup-1.log");
        Assert.Contains("/VERYSILENT", args, StringComparison.Ordinal);
        Assert.Contains("/SUPPRESSMSGBOXES", args, StringComparison.Ordinal);
        Assert.Contains("/NORESTART", args, StringComparison.Ordinal);
        Assert.Contains("/RELAUNCH=1", args, StringComparison.Ordinal);
        Assert.Contains("/WAITPID=4242", args, StringComparison.Ordinal);
        Assert.Contains("/LOG=\"C:\\ProgramData\\L-Mechrevo\\logs\\update-setup-1.log\"", args, StringComparison.Ordinal);
    }

    [Fact]
    public void WatcherScript_QuotesPathsAndRelaunchesOnlyOnFailure()
    {
        string script = SilentUpdate.WatcherScript(@"C:\Users\o'neil\setup.exe", "/VERYSILENT", @"C:\x\log.exit", @"C:\Program Files\L-Mechrevo\L-Mechrevo.exe");
        Assert.Contains("-FilePath 'C:\\Users\\o''neil\\setup.exe'", script, StringComparison.Ordinal);
        // Waits for setup itself, never with -Wait (that waits for the relaunched app too).
        Assert.Contains("-PassThru\n", script, StringComparison.Ordinal);
        Assert.Contains("$null = $p.Handle; $p.WaitForExit()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-Wait", script, StringComparison.Ordinal);
        Assert.Contains("Set-Content -LiteralPath 'C:\\x\\log.exit' -Value $code", script, StringComparison.Ordinal);
        Assert.Contains("if ($code -ne 0) { Start-Process -FilePath 'C:\\Program Files\\L-Mechrevo\\L-Mechrevo.exe' -ArgumentList '--after-update' }", script, StringComparison.Ordinal);
    }

    [Fact]
    public void WatcherScript_RunsAndRecordsTheSetupExitCode()
    {
        string temp = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", "silent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string exitFile = Path.Combine(temp, "setup.log.exit");
        try
        {
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            string script = SilentUpdate.WatcherScript(cmd, "/c exit 0", exitFile, cmd);
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process process = Process.Start(psi)!;
            Assert.True(process.WaitForExit(60_000), "the watcher did not finish");
            Assert.True(File.Exists(exitFile), "the watcher must record the setup exit code");
            Assert.Equal("0", File.ReadAllText(exitFile).Trim());
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Regression (beta21 release check): setup relaunches the app before it exits, and in an elevated
    /// in-app update the relaunched app is part of setup's process tree. <c>Start-Process -Wait</c> on
    /// Windows PowerShell 5.1 waits for every descendant, so the watcher stayed alive until the user quit
    /// the app. It must finish when setup finishes. Stand-in: a "setup" that leaves a 25 s child behind.
    /// </summary>
    [Fact]
    public void WatcherScript_DoesNotWaitForTheAppThatSetupLeavesRunning()
    {
        string temp = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", "silent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string exitFile = Path.Combine(temp, "setup.log.exit");
        string marker = "lmechrevo-watcher-child-" + Guid.NewGuid().ToString("N");
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        try
        {
            // The child's command line carries the marker (a comment), so cleanup can find exactly it.
            string fakeSetup = "-NoProfile -WindowStyle Hidden -Command \"Start-Process -WindowStyle Hidden -FilePath powershell.exe "
                + "-ArgumentList '-NoProfile -Command Start-Sleep -Seconds 25 #" + marker + "'; exit 0\"";
            string script = SilentUpdate.WatcherScript(powershell, fakeSetup, exitFile, Path.Combine(Environment.SystemDirectory, "cmd.exe"));
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process process = Process.Start(psi)!;
            Assert.True(process.WaitForExit(15_000), "the watcher waited for the child that setup left running");
            Assert.Equal("0", File.ReadAllText(exitFile).Trim());
        }
        finally
        {
            KillProcessesWithMarker(marker);
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    static void KillProcessesWithMarker(string marker)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'powershell.exe'");
            foreach (System.Management.ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    if (item["CommandLine"] is not string line || !line.Contains(marker, StringComparison.Ordinal)) continue;
                    try
                    {
                        using Process child = Process.GetProcessById(Convert.ToInt32(item["ProcessId"]));
                        child.Kill();
                    }
                    catch { /* already gone */ }
                }
            }
        }
        catch { /* WMI unavailable: the child ends by itself after 25 s */ }
    }

    [Fact]
    public void PendingRecord_RoundTrips()
    {
        var pending = new PendingUpdate("0.289.0-beta20", "0.289.0-beta21",
            DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), @"C:\ProgramData\L-Mechrevo\logs\u.log");
        Assert.Equal(pending, SilentUpdate.Parse(SilentUpdate.Format(pending)));
        Assert.Equal(@"C:\ProgramData\L-Mechrevo\logs\u.log.exit", pending.ExitCodeFile);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a|b")]
    [InlineData("a|b|notanumber|c")]
    public void PendingRecord_RejectsMalformedValues(string? raw) => Assert.Null(SilentUpdate.Parse(raw));

    [Fact]
    public void Evaluate_SucceedsWhenTheTargetVersionRuns()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_100);
        var pending = new PendingUpdate("0.289.0-beta20", "0.289.0-beta21", now.AddMinutes(-2), "log");
        UpdateOutcome outcome = SilentUpdate.Evaluate(pending, "0.289.0-beta21", null, now);
        Assert.Equal(UpdateOutcomeKind.Succeeded, outcome.Kind);
    }

    [Fact]
    public void Evaluate_FailsWhenSetupExitedWithAnErrorAndTheOldVersionRuns()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_100);
        var pending = new PendingUpdate("0.289.0-beta20", "0.289.0-beta21", now.AddMinutes(-2), "log");
        UpdateOutcome failed = SilentUpdate.Evaluate(pending, "0.289.0-beta20", 5, now);
        Assert.Equal(UpdateOutcomeKind.Failed, failed.Kind);
        Assert.Equal(5, failed.ExitCode);
        // No exit code yet: setup may still be running -> keep waiting instead of claiming anything.
        Assert.Equal(UpdateOutcomeKind.Unknown, SilentUpdate.Evaluate(pending, "0.289.0-beta20", null, now).Kind);
    }

    [Fact]
    public void Evaluate_DropsMissingOrStaleRecords()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_100);
        Assert.Equal(UpdateOutcomeKind.None, SilentUpdate.Evaluate(null, "0.289.0-beta21", null, now).Kind);
        var stale = new PendingUpdate("0.289.0-beta20", "0.289.0-beta21", now.AddDays(-3), "log");
        Assert.Equal(UpdateOutcomeKind.None, SilentUpdate.Evaluate(stale, "0.289.0-beta20", 1, now).Kind);
    }

    [Fact]
    public void TheUpdateWindowUsesTheSilentPathAndTheAppReportsTheResult()
    {
        string form = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Updates", "UpdateForm.cs");
        Assert.Contains("SilentUpdate.Start(package", form, StringComparison.Ordinal);
        string program = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Program.cs");
        Assert.Contains("ReportUpdateOutcome();", program, StringComparison.Ordinal);
        Assert.Contains("SilentUpdate.CollectOutcome()", program, StringComparison.Ordinal);
    }
}
