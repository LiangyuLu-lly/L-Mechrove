using MechrevoLite.Hardware;
using MechrevoLite.UI;
using System.Reflection;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// AMD 功耗字段体系的判定。判错平台的代价是功耗墙永久设不上：
/// 写入会走 CpuAmdSPL/CpuAmdSPPT 键，被 GCU 忽略，确认必然超时。
/// </summary>
public class AmdPowerFieldTests
{
    static MechrevoHw NewIntelLikeHardware() =>
        new((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities { AmdPlatform = false });

    static MechrevoHw NewAmdProfileHardware() =>
        new((_, _) => Task.CompletedTask, new MechrevoDeviceCapabilities { AmdPlatform = true });

    /// <summary>
    /// H5 主回归：Intel 机型上报了一个占位的 CPU_AmdSPL: 0。
    /// 过去这会把平台永久判成 AMD，并让 Pl1 永久锁在 0 W，真实的 CPU_PL1 被丢弃。
    /// </summary>
    [Fact]
    public void PlaceholderZeroAmdFieldDoesNotSwitchAnIntelMachineToTheAmdChannel()
    {
        using MechrevoHw hardware = NewIntelLikeHardware();

        hardware.HandleMessage("Fan/Status", """
            {
              "CPU_AmdSPL": 0,
              "CPU_AmdSPPT": 0,
              "CPU_AmdFPPT": 0,
              "CPU_PL1": 45,
              "CPU_PL2": 65
            }
            """);

        Assert.False(hardware.AmdPowerStatusSeen);
        Assert.False(hardware.UsesAmdPowerFields);
        Assert.Equal(45, hardware.Pl1);
        Assert.Equal(65, hardware.Pl2);
        Assert.Equal(-1, hardware.CpuAmdSpl);
        Assert.Equal(-1, hardware.CpuAmdSppt);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"NOT_SUPPORT\"")]
    [InlineData("-1")]
    [InlineData("0")]
    public void UnusableAmdFieldValuesAreNotPlatformEvidence(string raw)
    {
        using MechrevoHw hardware = NewIntelLikeHardware();

        hardware.HandleMessage("Fan/Status", $"{{\"CPU_AmdSPL\":{raw},\"CPU_PL1\":45}}");

        Assert.False(hardware.UsesAmdPowerFields);
        Assert.Equal(45, hardware.Pl1);
    }

    /// <summary>
    /// 真正的 AMD 机型：一旦上报可用瓦数，平台判定 latch，Pl1/Pl2 由 AMD 字段接管。
    /// </summary>
    [Fact]
    public void UsableAmdFieldValuesTakeOverThePowerReadback()
    {
        using MechrevoHw hardware = NewIntelLikeHardware();

        hardware.HandleMessage("Fan/Status", """
            { "CPU_AmdSPL": 90, "CPU_AmdSPPT": 100, "CPU_AmdFPPT": 110, "CPU_PL1": 45, "CPU_PL2": 65 }
            """);

        Assert.True(hardware.AmdPowerStatusSeen);
        Assert.True(hardware.UsesAmdPowerFields);
        Assert.Equal(90, hardware.Pl1);
        Assert.Equal(100, hardware.Pl2);
        Assert.Equal(110, hardware.CpuAmdFppt);
    }

    /// <summary>
    /// 部分帧不该擦除已知的 AMD 读回值——这是 OptionalInt 用上一次值兜底的正当理由。
    /// </summary>
    [Fact]
    public void PartialFrameKeepsThePreviouslyReportedAmdValues()
    {
        using MechrevoHw hardware = NewIntelLikeHardware();
        hardware.HandleMessage("Fan/Status", "{\"CPU_AmdSPL\":90,\"CPU_AmdSPPT\":100}");

        hardware.HandleMessage("Fan/Status", "{\"OperatingMode\":1}");

        Assert.Equal(90, hardware.CpuAmdSpl);
        Assert.Equal(90, hardware.Pl1);
        Assert.Equal(100, hardware.Pl2);
    }

    /// <summary>
    /// 已经 latch 成 AMD 之后又收到一个占位 0，不能把显示值打回 0 W。
    /// </summary>
    [Fact]
    public void LatchedAmdPlatformIgnoresALaterPlaceholderZero()
    {
        using MechrevoHw hardware = NewIntelLikeHardware();
        hardware.HandleMessage("Fan/Status", "{\"CPU_AmdSPL\":90,\"CPU_AmdSPPT\":100}");

        hardware.HandleMessage("Fan/Status", "{\"CPU_AmdSPL\":0,\"CPU_AmdSPPT\":0}");

        Assert.True(hardware.UsesAmdPowerFields);
        Assert.Equal(90, hardware.Pl1);
        Assert.Equal(100, hardware.Pl2);
    }

    /// <summary>
    /// ItemSupport 画像声明是 AMD 平台时，即使运行时还没报可用值也走 AMD 通道；
    /// 此时 Pl1 应回落到通用字段而不是显示 0。
    /// </summary>
    [Fact]
    public void AmdProfileWithoutRuntimeValuesStillUsesTheAmdChannelButReadsGenericWatts()
    {
        using MechrevoHw hardware = NewAmdProfileHardware();

        hardware.HandleMessage("Fan/Status", "{\"CPU_PL1\":45,\"CPU_PL2\":65}");

        Assert.True(hardware.UsesAmdPowerFields);
        Assert.Equal(45, hardware.Pl1);
        Assert.Equal(65, hardware.Pl2);
    }

    /// <summary>
    /// 写入通道的键名选择必须跟随平台判定，这是误判之后真正出故障的地方。
    /// </summary>
    [Fact]
    public async Task PowerLimitWriteUsesTheIntelKeysWhenOnlyPlaceholderAmdFieldsWereSeen()
    {
        var written = new List<Dictionary<string, object>>();
        using var hardware = new MechrevoHw(
            (_, payload) =>
            {
                if (payload is IDictionary<string, object> values)
                    written.Add(new Dictionary<string, object>(values));
                return Task.CompletedTask;
            },
            new MechrevoDeviceCapabilities { AmdPlatform = false });

        hardware.HandleMessage("Fan/Status", """
            {
              "CPU_AmdSPL": 0,
              "CPU_PL1": 45,
              "CPU_PL2": 65,
              "CPU_PL1Minimum": 20,
              "CPU_PL1Maximum": 80,
              "CPU_PL2Minimum": 20,
              "CPU_PL2Maximum": 120
            }
            """);

        await hardware.SetPl1Pl2(50, 70);

        Assert.Contains(written, command => command.ContainsKey("PL1"));
        Assert.Contains(written, command => command.ContainsKey("PL2"));
        Assert.DoesNotContain(written, command => command.ContainsKey("CpuAmdSPL"));
        Assert.DoesNotContain(written, command => command.ContainsKey("CpuAmdSPPT"));
    }

    // ---- AMD「瞬时功耗墙」= fPPT ----
    //
    // 官方 UI 在 AMD 分支只读 CPU_AmdFPPT、只发 CpuAmdFPPT，CPU_PL4 在 AMD 上永不生效
    // （CCUWinUI.decompiled.cs:52776-52812 与 50142-50162）。蛟龙 16 Pro 是 AMD 机型，
    // 过去这一行仍发 PL4 键，被 GCU 忽略、确认必然超时——这就是「无法设置 PL4」的根因。

    /// <summary>真机帧形状：AMD 机型同帧上报 fPPT 与 CPU_PL4 半瓦范围。</summary>
    static string AmdStatus(string fpptWatts) =>
        $$"""
        {"CPU_AmdSPL":75,"CPU_AmdSPPT":85,"CPU_AmdFPPT":{{fpptWatts}},
         "CPU_PL4":210,"CPU_PL4Minimum":10,"CPU_PL4Maximum":210,"CPU_PL4_Double_Flag":"1"}
        """;

    [Fact]
    public void AmdPeakPowerReadbackComesFromFpptNotPl4()
    {
        using MechrevoHw hardware = NewAmdProfileHardware();

        hardware.HandleMessage("Fan/Status", AmdStatus("145"));

        Assert.True(hardware.UsesAmdPowerFields);
        // AMD 上 CPU_PL4(210×2=420) 不是用户设定的功耗墙，fPPT 才是。
        Assert.Equal(145, hardware.Pl4);
        Assert.True(hardware.Pl4Adjustable);
    }

    /// <summary>
    /// 写入必须发官方的 CpuAmdFPPT 键、原样瓦数（不做半瓦折半），
    /// 并由 fPPT 回读确认。发 PL4 键就是被 GCU 忽略的那条死路。
    /// </summary>
    [Fact]
    public async Task AmdPeakPowerWritePublishesFpptAndConfirmsFromReadback()
    {
        var commands = new List<Dictionary<string, object>>();
        MechrevoHw? hardware = null;
        hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Fan/Control" && payload is IDictionary<string, object> values &&
                values.TryGetValue("Action", out object? action) &&
                action?.ToString() == "SET_OPERATING_MODE_DETAIL")
            {
                commands.Add(new Dictionary<string, object>(values));
                if (values.TryGetValue("CpuAmdFPPT", out object? fppt))
                    hardware!.HandleMessage("Fan/Status", AmdStatus(fppt!.ToString()!));
            }
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, AmdPlatform = true });

        using (hardware)
        {
            hardware.HandleMessage("Fan/Status", AmdStatus("145"));
            var service = new MechrevoService(hardware);

            Assert.True(await service.SetCustomDetail(new() { ["PL4"] = "150" }));
            Assert.Contains(commands,
                command => command.TryGetValue("CpuAmdFPPT", out object? value) && value!.ToString() == "150");
            Assert.DoesNotContain(commands, command => command.ContainsKey("PL4"));
            Assert.Equal(150, hardware.Pl4);
        }
    }

    /// <summary>GCU 不接受、不回报 fPPT 时必须诚实报失败，不能拿 echo 冒充生效。</summary>
    [Fact]
    public async Task AmdPeakPowerWriteFailsHonestlyWhenFpptNeverReadsBack()
    {
        var commands = new List<Dictionary<string, object>>();
        using var hardware = new MechrevoHw((topic, payload) =>
        {
            if (topic == "Fan/Control" && payload is IDictionary<string, object> values)
                commands.Add(new Dictionary<string, object>(values));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { ProfileAvailable = true, AmdPlatform = true });

        hardware.HandleMessage("Fan/Status", AmdStatus("145"));
        var service = new MechrevoService(hardware);

        Assert.False(await service.SetCustomDetail(new() { ["PL4"] = "150" }));
        Assert.Contains(commands,
            command => command.TryGetValue("CpuAmdFPPT", out object? value) && value!.ToString() == "150");
        Assert.Equal(145, hardware.Pl4);
    }

    /// <summary>不报任何范围（也没有 fPPT）的机器不提供这一行，绝不猜一个假量程。</summary>
    [Fact]
    public void PeakPowerRowIsNotOfferedWhenNeitherFpptNorPl4RangeIsReported()
    {
        using MechrevoHw hardware = NewAmdProfileHardware();

        hardware.HandleMessage("Fan/Status", "{\"CPU_AmdSPL\":75,\"CPU_AmdSPPT\":85}");

        Assert.False(hardware.Pl4Adjustable);
    }

    /// <summary>Intel 的 PL4 路径（键名与半瓦换算）必须逐字节保持原样。</summary>
    [Fact]
    public async Task IntelPl4WriteStaysOnThePl4FieldAndNeverUsesFppt()
    {
        var written = new List<Dictionary<string, object>>();
        using var hardware = new MechrevoHw((_, payload) =>
        {
            if (payload is IDictionary<string, object> values)
                written.Add(new Dictionary<string, object>(values));
            return Task.CompletedTask;
        }, new MechrevoDeviceCapabilities { AmdPlatform = false });

        hardware.HandleMessage("Fan/Status", """
            {"CPU_PL4_Double_Flag":"1","CPU_PL4":105,"CPU_PL4Minimum":5,"CPU_PL4Maximum":105}
            """);

        await hardware.SetPl4(200);

        Dictionary<string, object> command = Assert.Single(written, entry => entry.ContainsKey("PL4"));
        Assert.Equal("100", command["PL4"]);
        Assert.DoesNotContain(written, entry => entry.ContainsKey("CpuAmdFPPT"));
    }

    [Fact]
    public void AmdFormLabelsThePeakPowerRowWithTheAmdFieldName()
    {
        using var hardware = new MechrevoHw((_, _) => Task.CompletedTask,
            new MechrevoDeviceCapabilities { ProfileAvailable = true, AmdPlatform = true });
        hardware.HandleMessage("Fan/Status", AmdStatus("145"));

        using var _ = UseHardware(hardware);
        using var form = new CustomModeForm();
        form.CreateControl();

        Control row = (Control)Field<RSlider>(form, "_pl4").Tag!;
        string label = Descendants(row).OfType<Label>().First().Text;

        Assert.Contains("fPPT", label);
    }

    static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls)
        {
            yield return control;
            foreach (Control descendant in Descendants(control))
                yield return descendant;
        }
    }

    static T Field<T>(CustomModeForm form, string name) =>
        (T)typeof(CustomModeForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(form)!;

    static IDisposable UseHardware(MechrevoHw hardware)
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = hardware;
        return new Scoped(previousAudit, previousHardware);
    }

    sealed class Scoped(bool previousAudit, MechrevoHw? previousHardware) : IDisposable
    {
        public void Dispose()
        {
            Program.UiAuditMode = previousAudit;
            Program.hw = previousHardware!;
        }
    }
}
