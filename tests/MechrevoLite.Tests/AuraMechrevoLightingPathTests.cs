using System.Reflection;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// ASUS Aura 存根不得在机械革命用户灯效路径上跑：已连接时 Apply* 不抛、不写 ACPI，
/// LightForm/RgbForm/KeyboardRgb 不得调用 Aura.Apply*，SetAura 在已连接时不得改 UI/配置。
/// </summary>
public class AuraMechrevoLightingPathTests
{
    [Fact]
    public void ApplyAura_OnConnectedMechrevoHw_DoesNotThrowOrWriteAsusAcpi()
    {
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask);
        Assert.True(hardware.IsConnected);

        MechrevoHw? previous = Program.hw;
        Program.hw = hardware;
        try
        {
            Exception? thrown = Record.Exception(() =>
            {
                Aura.ApplyAura();
                Aura.ApplyBrightness();
                Aura.ApplyBrightness(0, "Lid");
                Aura.SleepBrightness();
                Aura.CustomRGB.ApplyGPUColor(1);
            });
            Assert.Null(thrown);
            Assert.Null(typeof(AsusACPI).GetMethod("DeviceSet",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static));
        }
        finally
        {
            Program.hw = previous;
        }
    }

    [Theory]
    [InlineData("LightForm.cs")]
    [InlineData("RgbForm.cs")]
    [InlineData(@"Hardware\KeyboardRgb.cs")]
    public void UserLightingPath_DoesNotCallAuraApply(string relative)
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot, "src", "MechrevoLiteWin", relative));
        Assert.DoesNotContain("Aura.ApplyAura", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Aura.ApplyBrightness", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Aura.SleepBrightness", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SetAura_IsGatedWhenMechrevoConnected()
    {
        string settings = File.ReadAllText(Path.Combine(RepoRoot, "src", "MechrevoLiteWin", "Settings.cs"));
        int setAura = settings.IndexOf("public void SetAura()", StringComparison.Ordinal);
        Assert.True(setAura >= 0);
        string body = settings.Substring(setAura, Math.Min(240, settings.Length - setAura));
        Assert.Contains("IsMechrevoConnected", body, StringComparison.Ordinal);
        Assert.Contains("return", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AuraStubs_DoNotWriteAsusAcpi()
    {
        string stubs = File.ReadAllText(Path.Combine(RepoRoot, "src", "MechrevoLiteWin", "Stubs.cs"));
        int aura = stubs.IndexOf("public static class Aura", StringComparison.Ordinal);
        int next = stubs.IndexOf("public static class PeripheralsProvider", aura, StringComparison.Ordinal);
        Assert.True(aura >= 0 && next > aura);
        string body = stubs[aura..next];
        Assert.DoesNotContain("DeviceSet", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Program.acpi", body, StringComparison.Ordinal);
        Assert.Contains("IsMechrevoConnected", body, StringComparison.Ordinal);
    }

    static string RepoRoot
    {
        get
        {
            _repoRoot ??= FindRepoRoot();
            return _repoRoot;
        }
    }

    static string? _repoRoot;

    static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
