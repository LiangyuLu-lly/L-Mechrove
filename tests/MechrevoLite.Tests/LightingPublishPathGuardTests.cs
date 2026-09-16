using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 灯效下发路径收敛的防回归锁（beta17 计划 §2 Wave D）。
///
/// 收敛目标：灯效的电源/效果下发只有 <see cref="MechrevoService"/> 一个出口，UI 只发意图；
/// 恢复/熄灯周期的「只下发 + 后台遥测确认」只有 <c>IssueLightPower</c> 一份协议。
///
/// 固件危害：同一灯态重复下发会让设备重新初始化/闪烁。以下断言防止未来改动重新引入
/// （a）第二个 SetPower 发布者；（b）UI 越过服务接缝直发硬件；
/// （c）遥测确认路径反过来重发电源——让「一次逻辑状态变化」变成两次下发。
/// </summary>
public class LightingPublishPathGuardTests
{
    const string LightbarTopic = "HidLightbar/Ctrl";

    static string RepoRootPath(params string[] tail)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, Path.Combine(tail));
    }

    static IEnumerable<string> SourceFiles()
    {
        string root = RepoRootPath("src");
        Assert.True(Directory.Exists(root), "找不到 src 目录");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    static bool IsSetPower(
        (string Topic, Dictionary<string, object> Payload) entry, string topic, int status) =>
        entry.Topic == topic &&
        entry.Payload.TryGetValue("powerstatus", out object? value) &&
        Convert.ToInt32(value) == status;

    /// <summary>
    /// 「灯电源 = function SetPower」的 payload 字面量必须全库唯一，且落在 MechrevoService。
    /// 出现第二个发布者（UI 直发、或另写一份下发）时立即红。
    /// </summary>
    [Fact]
    public void LightPowerSetPowerPayload_HasExactlyOnePublisher()
    {
        var publishers = new List<string>();
        foreach (string path in SourceFiles())
        {
            string relative = Path.GetRelativePath(RepoRootPath(), path).Replace('\\', '/');
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//")) continue;
                if (lines[i].Contains("[\"function\"] = \"SetPower\"", StringComparison.Ordinal))
                    publishers.Add($"{relative}:{i + 1}");
            }
        }

        Assert.True(publishers.Count == 1 &&
                    publishers[0].StartsWith("src/MechrevoLiteWin/Hardware/MechrevoService.cs:", StringComparison.Ordinal),
            "灯电源（function=SetPower）只能由 MechrevoService 一个出口发布——第二个发布者会让固件重初始化/闪烁。实际：\n  " +
            string.Join("\n  ", publishers));
    }

    /// <summary>UI 灯效窗体不得直接触碰硬件发布（必须经 <c>Program.service</c> 接缝）。</summary>
    [Theory]
    [InlineData("RgbForm.cs")]
    [InlineData("LightForm.cs")]
    [InlineData("Settings.V2.cs")]
    public void UiLightingForms_DoNotPublishDirectlyThroughHardware(string fileName)
    {
        string source = File.ReadAllText(RepoRootPath("src", "MechrevoLiteWin", fileName));
        Assert.DoesNotContain("hw.Publish(", source, StringComparison.Ordinal);
    }

    /// <summary>LightForm 的状态查询必须走服务接缝，而不是 <c>Program.hw.Publish</c> 直发。</summary>
    [Fact]
    public void LightForm_RequestsStatusThroughTheServiceSeam()
    {
        string source = File.ReadAllText(RepoRootPath("src", "MechrevoLiteWin", "LightForm.cs"));
        Assert.Contains("Program.service.RequestLightStatus(_topic)", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// 一次逻辑状态变化只下发一条 SetPower；随后的遥测确认只能查询回读，
    /// 绝不重发电源（重发 = 固件重初始化/闪烁）。
    /// </summary>
    [Fact]
    public async Task TelemetryObserve_NeverRepublishesLightPower()
    {
        var written = new List<(string Topic, Dictionary<string, object> Payload)>();
        var gate = new object();
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (payload is IDictionary<string, object> values)
            {
                var copy = new Dictionary<string, object>(values);
                lock (gate) written.Add((topic, copy));
                if (copy.TryGetValue("powerstatus", out object? power) && topic == LightbarTopic)
                {
                    string state = Convert.ToInt32(power) == 1 ? "On" : "Off";
                    hardware!.HandleMessage("HidLightbar/Status",
                        $"{{\"type\":\"MEZone_Lighbar4\",\"powerStatus\":\"{state}\"}}");
                }
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { Lightbar = true, LogoLight = true });

        using (hardware)
        {
            var service = new MechrevoService(hardware);

            Assert.True(await service.IssueLightPower(LightbarTopic, true));
            lock (gate) Assert.Equal(1, written.Count(entry => IsSetPower(entry, LightbarTopic, 1)));

            // 遥测确认（后台）只能查询回读，绝不重发电源。
            service.ObserveLightPower(LightbarTopic, true);
            await Task.Delay(1200);

            lock (gate) Assert.Equal(1, written.Count(entry => IsSetPower(entry, LightbarTopic, 1)));
        }
    }
}
