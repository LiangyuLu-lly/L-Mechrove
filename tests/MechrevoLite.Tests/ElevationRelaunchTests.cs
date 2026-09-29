using MechrevoLite.Hardware;
using MechrevoLite.Helpers;

namespace MechrevoLite.Tests;

/// <summary>
/// Always-elevated launch: a filtered admin launch hands over to an elevated instance (the Highest
/// autostart task first, one UAC prompt otherwise) and exits before creating any window; standard
/// users, helper actions and an already handed-over launch never hand over.
/// </summary>
public class ElevationRelaunchTests
{
    // Token kinds as ints: the enum is internal, and xUnit theory methods must be public.
    [Theory]
    [InlineData(3, "", false, false, true)]                 // Limited
    [InlineData(3, "startup", false, false, true)]
    [InlineData(3, "--after-update", false, false, true)]
    [InlineData(3, "", true, false, false)]                 // no loops
    [InlineData(3, "", false, true, false)]                 // UI audit
    [InlineData(3, "charge", false, false, false)]          // helper actions stay
    [InlineData(3, "--gpu-oc-helper", false, false, false)]
    [InlineData(2, "", false, false, false)]                // Full: already elevated
    [InlineData(1, "", false, false, false)]                // Default: standard user / UAC off
    [InlineData(0, "", false, false, false)]                // Unknown
    public void ShouldHandOver_OnlyForAFilteredAdminUiLaunch(int token, string action,
        bool handedOver, bool audit, bool expected) =>
        Assert.Equal(expected, ElevationRelaunch.ShouldHandOver((ElevationRelaunch.TokenKind)token, action, handedOver, audit));

    [Fact]
    public void Request_RoundTripsTheActionWithinItsLifetime()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        Assert.Equal("", ElevationRelaunch.ParseRequest(ElevationRelaunch.FormatRequest(now, ""), now.AddSeconds(3)));
        Assert.Equal("--after-update",
            ElevationRelaunch.ParseRequest(ElevationRelaunch.FormatRequest(now, "--after-update"), now.AddSeconds(30)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("|startup")]
    [InlineData("abc|startup")]
    [InlineData("1800000000|charge")]   // only UI launches can be handed over
    public void Request_RejectsMalformedOrForeignEntries(string? raw) =>
        Assert.Null(ElevationRelaunch.ParseRequest(raw, DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)));

    [Fact]
    public void Request_ExpiresSoALogonAutostartIsNotMistakenForAHandover()
    {
        DateTimeOffset written = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        string raw = ElevationRelaunch.FormatRequest(written, "");
        Assert.Null(ElevationRelaunch.ParseRequest(raw, written + ElevationRelaunch.RequestLifetime + TimeSpan.FromSeconds(1)));
        Assert.Null(ElevationRelaunch.ParseRequest(raw, written - TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Program_HandsOverBeforeTheSingleInstanceCheckAndRestoresTheActionFirst()
    {
        string program = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Program.cs");
        int restore = program.IndexOf("if (action == ElevationRelaunch.RelaunchArgument)", StringComparison.Ordinal);
        int minimized = program.IndexOf("bool startMinimized = IsStartupLaunch(action);", StringComparison.Ordinal);
        int handOver = program.IndexOf("ElevationRelaunch.TryHandOver(action)", StringComparison.Ordinal);
        int singleton = program.IndexOf("ProcessHelper.CheckAlreadyRunning(action)", StringComparison.Ordinal);
        int forms = program.IndexOf("settingsForm = new SettingsForm();", StringComparison.Ordinal);
        Assert.True(restore >= 0 && restore < minimized, "the handed-over action must be restored before startMinimized is computed");
        Assert.True(handOver >= 0 && handOver < singleton, "the filtered process must not own the single-instance event");
        Assert.True(singleton < forms);
        Assert.Contains("!ProcessHelper.HasExistingUiOwner()", program, StringComparison.Ordinal);
        Assert.Contains("ElevationRelaunch.ConsumeRequest()", program, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTaskHandoverRefusesATaskThatWouldRunSomethingElse()
    {
        string startup = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Helpers", "Startup.cs");
        int start = startup.IndexOf("internal static bool TryRunUserTaskForElevation", StringComparison.Ordinal);
        string body = startup.Substring(start, startup.IndexOf("public static bool IsScheduled", start, StringComparison.Ordinal) - start);
        Assert.Contains("!task.Enabled", body, StringComparison.Ordinal);
        Assert.Contains("TaskRunLevel.Highest", body, StringComparison.Ordinal);
        Assert.Contains("Path.GetFullPath(running)", body, StringComparison.Ordinal);
        Assert.Contains("TaskState.Running", body, StringComparison.Ordinal);

        string relaunch = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Helpers", "ElevationRelaunch.cs");
        // The prompt-free path is only taken for an image the user cannot replace.
        Assert.Contains("ExecutableTrust.IsCurrentImageInProtectedLocation", relaunch, StringComparison.Ordinal);
        Assert.Contains("NativeErrorCode == 1223", relaunch, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondLaunchBringsTheRunningWindowUpThroughUipi()
    {
        string process = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Helpers", "ProcessHelper.cs");
        string settings = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Settings.cs");
        Assert.Contains("SingleInstanceSignal.RequestShow()", process, StringComparison.Ordinal);
        Assert.Contains("SingleInstanceSignal.AllowFromLowerIntegrity(Handle)", settings, StringComparison.Ordinal);
        Assert.Contains("m.Msg == SingleInstanceSignal.Message", settings, StringComparison.Ordinal);
    }
}

public class GcuEndpointTests
{
    [Theory]
    [InlineData(13688, 13688)]
    [InlineData(1883, 1883)]
    [InlineData(0, 13688)]
    [InlineData(-1, 13688)]
    [InlineData(70000, 13688)]
    public void ParsePort_AcceptsOnlyRealTcpPorts(int value, int expected) =>
        Assert.Equal(expected, GcuEndpoint.ParsePort(value));

    [Theory]
    [InlineData("1883", 1883)]
    [InlineData(" 13688 ", 13688)]
    [InlineData("abc", 13688)]
    [InlineData("", 13688)]
    public void ParsePort_ParsesStringValues(string value, int expected) =>
        Assert.Equal(expected, GcuEndpoint.ParsePort(value));

    [Fact]
    public void ParsePort_MissingValueMeansTheDefaultBrokerPort() =>
        Assert.Equal(GcuEndpoint.DefaultPort, GcuEndpoint.ParsePort(null));

    [Fact]
    public void TheAppConnectsToTheRecordedPort()
    {
        string hw = GcuInstallerHarness.Read("src", "MechrevoLiteWin", "Hardware", "MechrevoHw.cs");
        Assert.Contains("static int Port => GcuEndpoint.Port;", hw, StringComparison.Ordinal);
        Assert.DoesNotContain("const int Port = 13688", hw, StringComparison.Ordinal);
        string installer = GcuInstallerHarness.Read("installer", "Install-Gcu.ps1");
        Assert.Contains("'GcuMqttPort'", installer, StringComparison.Ordinal);
        Assert.Contains(GcuEndpoint.PortValueName, installer, StringComparison.Ordinal);
    }
}
