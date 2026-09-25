using MechrevoLite.Gpu;
using MechrevoLite.Update;
using MechrevoLite.Usage;

namespace MechrevoLite.Tests;

public class UsageTelemetryTests
{
    [Fact]
    public void Sanitize_strips_unsafe_and_clips()
    {
        var raw = new UsageSnapshot(
            Id: "abc<>def",
            Ver: new string('v', 200),
            Event: "hack",
            Model: "JiguangX; DROP",
            Project: "PH6ARxx",
            Bios: "IDY",
            Gpu: "50",
            GpuName: "RTX 5070 Ti Laptop GPU",
            Cpu: "Ryzen 9 7945HX",
            Gcu: true,
            Keyboard: true,
            Lightbar: false);
        UsageSnapshot clean = UsageSnapshot.Sanitize(raw);
        Assert.Equal(UsageSnapshot.EventBeat, clean.Event);
        Assert.DoesNotContain("<", clean.Id);
        Assert.DoesNotContain(";", clean.Model);
        Assert.True(clean.Ver.Length <= UsageSnapshot.MaxFieldChars);
        Assert.Equal("PH6ARxx", clean.Project);
    }

    [Fact]
    public void GpuGenToken_maps_known_generations()
    {
        Assert.Equal("30", UsageSnapshot.GpuGenToken(DgpuGenerationKind.Gen30));
        Assert.Equal("40", UsageSnapshot.GpuGenToken(DgpuGenerationKind.Gen40));
        Assert.Equal("50", UsageSnapshot.GpuGenToken(DgpuGenerationKind.Gen50));
        Assert.Equal("none", UsageSnapshot.GpuGenToken(DgpuGenerationKind.NoDgpu));
        Assert.Equal("unknown", UsageSnapshot.GpuGenToken(DgpuGenerationKind.Unknown));
    }

    [Fact]
    public void BuildUrl_uses_update_base()
    {
        Assert.Equal(
            "https://stats.l-mechrevo.cn/api/heartbeat.php",
            UsageTelemetry.BuildUrl(UpdateChecker.DefaultBaseUrl));
        Assert.Equal(
            "https://example.com/api/heartbeat.php",
            UsageTelemetry.BuildUrl("https://example.com/"));
        Assert.Equal("usage_base_url", UsageTelemetry.BaseUrlKey);
        Assert.Equal("https://stats.l-mechrevo.cn", UsageTelemetry.DefaultBaseUrl);
        Assert.Equal(
            "https://stats.l-mechrevo.cn/api/heartbeat.php",
            UsageTelemetry.BuildUrl(UsageTelemetry.DefaultBaseUrl));
    }

    [Fact]
    public void InstallId_is_stable()
    {
        string first = UsageTelemetry.InstallId();
        string second = UsageTelemetry.InstallId();
        Assert.Equal(first, second);
        Assert.True(first.Length >= 8);
    }

    [Fact]
    public void Serialize_contains_anonymous_fields_only()
    {
        var snap = UsageSnapshot.Sanitize(new UsageSnapshot(
            "abcd1234ef", "0.289.0-beta18", "start", "Model X", "PH6ARxx", "IDY",
            "50", "RTX 5070", "Ryzen 9", true, true, false));
        string json = UsageTelemetry.Serialize(snap);
        Assert.Contains("\"id\":\"abcd1234ef\"", json);
        Assert.Contains("\"event\":\"start\"", json);
        Assert.DoesNotContain("UserName", json);
        Assert.DoesNotContain(Environment.MachineName, json);
    }

    [Fact]
    public void BuildLogUrl_uses_stats_host()
    {
        Assert.Equal(
            "https://stats.l-mechrevo.cn/api/log.php",
            UsageTelemetry.BuildLogUrl(UsageTelemetry.DefaultBaseUrl));
    }

    [Fact]
    public void RedactLog_strips_user_profile()
    {
        string redacted = UsageTelemetry.RedactLog(@"C:\Users\SomeName\AppData\MechrevoLite\log.txt boom");
        Assert.DoesNotContain("SomeName", redacted);
        Assert.Contains("%USERPROFILE%", redacted);
    }
}
