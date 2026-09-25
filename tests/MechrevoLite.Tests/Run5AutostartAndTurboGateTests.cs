using MechrevoLite.Hardware;
using System.Windows.Forms;

namespace MechrevoLite.Tests;

/// <summary>
/// Run5 两项新入口的回归测试：
///
/// 1. 「开机自启动」快捷开关（AutomationId = quick_startup）。状态读写全部经由 <see cref="Startup"/>
///    的接缝（ReadScheduledState / WriteScheduledState / ApplyScheduledState）。生产实现复用仓库里
///    既有的用户级自启动计划任务（Helpers/Startup.cs，TaskRunLevel.LUA，不需要管理员）；测试注入
///    纯内存替身，绝不读/写真实的计划任务。
/// 2. 「静音狂暴」入口的门禁：官方只在 <c>ItemSupport\IsTurboSubModeSupport == 1</c> 时展示该子模式
///    （_decompiled/CCUWinUI.decompiled.cs:57721-57739），所以判定必须三态，
///    Unknown / Unsupported 一律不显示（只在证据确认支持时显示）。
/// </summary>
public class Run5AutostartAndTurboGateTests
{
    // ------------------------------------------------------------ 路径引号 / 空格

    /// <summary>
    /// Task Scheduler 用结构化字段保存路径，不需要手工加引号；这里钉死路径规整规则：
    /// 两端成对引号剥掉、首尾空白去掉、路径中的空格原样保留（含空路径）。
    /// </summary>
    [Theory]
    [InlineData(@"C:\Program Files\Mechrevo App\L-Mechrevo.exe", @"C:\Program Files\Mechrevo App\L-Mechrevo.exe")]
    [InlineData("\"C:\\Program Files\\Mechrevo App\\L-Mechrevo.exe\"", @"C:\Program Files\Mechrevo App\L-Mechrevo.exe")]
    [InlineData("  C:\\Apps\\L-Mechrevo.exe  ", @"C:\Apps\L-Mechrevo.exe")]
    [InlineData("", "")]
    public void AutostartExecutablePath_KeepsSpacesAndStripsWrappingQuotes(string input, string expected) =>
        Assert.Equal(expected, Startup.NormalizeExecutablePath(input));

    // ------------------------------------------------------------ 幂等 / 建删

    /// <summary>已是目标态时不得再写：这保证自启动项幂等、永不产生第二条。</summary>
    [Fact]
    public void AutostartApply_AlreadyInDesiredState_DoesNotWriteAgain()
    {
        WithAutostartSeam(read: () => true, write: _ => true, body: writes =>
        {
            Assert.True(Startup.ApplyScheduledState(true));
            Assert.True(Startup.ApplyScheduledState(true));
            Assert.Empty(writes);   // 已是目标态 → 不再注册
        });
    }

    /// <summary>开 → 建，关 → 删；状态与写入顺序都被回读确认。</summary>
    [Fact]
    public void AutostartApply_CreatesThenRemovesTheSingleNamedEntry()
    {
        bool state = false;
        WithAutostartSeam(
            read: () => state,
            write: value =>
            {
                state = value;
                return true;
            },
            body: writes =>
            {
                Assert.True(Startup.ApplyScheduledState(true));
                Assert.True(state);
                Assert.Equal(new[] { true }, writes);

                Assert.True(Startup.ApplyScheduledState(false));
                Assert.False(state);
                Assert.Equal(new[] { true, false }, writes);
            });
    }

    /// <summary>写入失败必须原样返回失败（调用方据此回滚开关）。</summary>
    [Fact]
    public void AutostartApply_PropagatesWriteFailure()
    {
        WithAutostartSeam(read: () => false, write: _ => false, body: _ =>
            Assert.False(Startup.ApplyScheduledState(true)));
    }

    // ------------------------------------------------------------ 真实状态回显

    /// <summary>打开界面时必须显示系统里的真实状态，而不是配置项或默认值。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AutostartSwitch_ShowsTheRealSystemStateOnOpen(bool scheduled)
    {
        WithAutostartSeam(read: () => scheduled, write: _ => true, body: _ =>
        {
            using var form = new SettingsForm();
            form.CreateControl();

            var box = form.Controls.Find("quick_startup", true).OfType<CheckBox>().Single();
            Assert.Equal(scheduled, box.Checked);
        });
    }

    /// <summary>
    /// 程序化赋值 Checked（启动回读/自动化）绝不触发计划任务读写：动作只在 Click 上。
    /// 这是守卫链的第一道闸（run5 UI hardening, item 1）。
    /// </summary>
    [Fact]
    public void AutostartSwitch_ProgrammaticCheckAssignment_NeverTouchesTheTask()
    {
        bool state = false;
        WithAutostartSeam(
            read: () => state,
            write: value => { state = value; return true; },
            body: writes =>
            {
                using var form = new SettingsForm();
                form.CreateControl();
                var box = form.Controls.Find("quick_startup", true).OfType<CheckBox>().Single();

                box.Checked = true;
                box.Checked = false;

                Assert.Empty(writes);
                Assert.False(state);
            });
    }

    /// <summary>用户真实点击 → 经接缝建/删（点击自带新鲜输入，行为与旧版一致）。</summary>
    [Fact]
    public void AutostartSwitch_GenuineClickAppliesThroughTheSeam()
    {
        bool state = false;
        WithAutostartSeam(
            read: () => state,
            write: value =>
            {
                state = value;
                return true;
            },
            body: writes =>
            {
                using var form = new SettingsForm();
                form.CreateControl();
                var box = form.Controls.Find("quick_startup", true).OfType<CheckBox>().Single();
                Assert.False(box.Checked);

                RaiseClick(box);   // ON
                Assert.True(state);
                Assert.True(box.Checked);

                RaiseClick(box);   // OFF
                Assert.False(state);
                Assert.False(box.Checked);

                Assert.Equal(new[] { true, false }, writes);
            });
    }

    /// <summary>
    /// 系统写入失败时开关必须回到系统此刻的真实状态，不能停在用户拨到的位置
    /// （否则下一轮回显再纠正，用户会看到开关自己跳两下）。
    /// </summary>
    [Fact]
    public void AutostartSwitch_FailedApplyRollsBackToTheRealState()
    {
        WithAutostartSeam(read: () => false, write: _ => false, body: _ =>
        {
            using var form = new SettingsForm();
            form.CreateControl();
            var box = form.Controls.Find("quick_startup", true).OfType<CheckBox>().Single();

            RaiseClick(box);   // 真实点击 → 写入失败 → 回滚
            Assert.False(box.Checked, "写入失败后必须回滚到系统真实状态（OFF）。");
        });
    }

    // ------------------------------------------------------------ 静音狂暴三态门禁

    /// <summary>IsTurboSubModeSupport = 1 → 官方展示该子模式 → Supported。</summary>
    [Fact]
    public void SilentTurboAvailability_IsSupportedWhenTheItemSupportFlagIsOne()
    {
        var capabilities = MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>
        {
            ["TurboModeSupport"] = 1,
            ["IsTurboSubModeSupport"] = 1,
        });

        Assert.True(capabilities.ProfileAvailable);
        Assert.Equal(FeatureAvailability.Supported, capabilities.SilentTurboAvailability);
    }

    /// <summary>读到画像但 IsTurboSubModeSupport 为 0/缺失 → 官方隐藏 → Unsupported。</summary>
    [Fact]
    public void SilentTurboAvailability_IsUnsupportedWhenTheItemSupportFlagIsZero()
    {
        var capabilities = MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>
        {
            ["TurboModeSupport"] = 1,
            ["IsTurboSubModeSupport"] = 0,
        });

        Assert.True(capabilities.ProfileAvailable);
        Assert.Equal(FeatureAvailability.Unsupported, capabilities.SilentTurboAvailability);
    }

    /// <summary>没有 ItemSupport 画像（官方控制台没写能力）时为 Unknown，不能凭猜显示。</summary>
    [Fact]
    public void SilentTurboAvailability_IsUnknownWithoutAProfile()
    {
        var capabilities = MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>());

        Assert.False(capabilities.ProfileAvailable);
        Assert.Equal(FeatureAvailability.Unknown, capabilities.SilentTurboAvailability);
    }

    /// <summary>当前子模式值（SilentPerformanceModeSwitch）不是能力位，不得当成支持。</summary>
    [Fact]
    public void SilentTurboAvailability_IgnoresTheCurrentSubModeState()
    {
        var capabilities = MechrevoDeviceCapabilities.FromValues(new Dictionary<string, object?>
        {
            ["SilentPerformanceModeSwitch"] = 1,
        });

        Assert.False(capabilities.TurboSubMode);
        Assert.Equal(FeatureAvailability.Unsupported, capabilities.SilentTurboAvailability);
    }

    // ------------------------------------------------------------ 静音狂暴入口渲染

    /// <summary>不支持的机型：入口不渲染（按钮被移出性能分段行）。</summary>
    [Fact]
    public void SilentTurboEntry_IsNotRenderedWhenUnsupported()
    {
        WithReportedCapabilities(new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            TurboMode = true,
            TurboSubMode = false,
        }, form =>
        {
            form.RefreshDeviceCapabilities();
            Assert.Null(FindButtonByText(form, "静音狂暴"));
        });
    }

    /// <summary>
    /// 狂暴不得因 TurboModeSupport 缺位 FailClosed 藏掉：官方有狂暴时，
    /// MQTT Fan/Status 已到即提供入口。SwitchMode(Turbo) 本身无门禁。
    /// </summary>
    [Fact]
    public void TurboButton_VisibleWhenOfficialTurbo()
    {
        WithReportedCapabilities(new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            TurboMode = false,
        }, form =>
        {
            form.RefreshDeviceCapabilities();
            Assert.Null(FindButtonByText(form, "狂暴"));

            Program.hw!.HandleMessage("Fan/Status", "{\"OperatingMode\":1}");
            form.RefreshDeviceCapabilities();
            Assert.NotNull(FindButtonByText(form, "狂暴"));
        });
    }

    /// <summary>支持的机型：入口照常出现（不能因为加了门禁把支持机型的功能删掉）。</summary>
    [Fact]
    public void SilentTurboEntry_IsRenderedWhenSupported()
    {
        WithReportedCapabilities(new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            TurboMode = true,
            TurboSubMode = true,
        }, form =>
        {
            form.RefreshDeviceCapabilities();
            Assert.NotNull(FindButtonByText(form, "静音狂暴"));
        });
    }

    // ------------------------------------------------------------ helpers

    /// <summary>
    /// 注入自启动接缝并在结束时还原（还原值 = 模块初始化器装的测试替身，保证后续测试
    /// 仍不触碰真实计划任务）。<paramref name="body"/> 收到接缝实际发生的写入序列。
    /// </summary>
    static void WithAutostartSeam(Func<bool?> read, Func<bool, bool> write, Action<List<bool>> body)
    {
        Func<bool?> previousRead = Startup.ReadScheduledState;
        Func<bool, bool> previousWrite = Startup.WriteScheduledState;
        Func<TimeSpan>? previousIdle = NativeMethods.IdleTimeProvider;
        var writes = new List<bool>();
        try
        {
            Startup.ReadScheduledState = read;
            Startup.WriteScheduledState = value =>
            {
                writes.Add(value);
                return write(value);
            };
            // 真实点击 = 刚刚有输入：注入新鲜输入接缝，绝不在测试里产生真实输入。
            NativeMethods.IdleTimeProvider = static () => TimeSpan.Zero;
            body(writes);
        }
        finally
        {
            NativeMethods.IdleTimeProvider = previousIdle;
            Startup.ReadScheduledState = previousRead;
            Startup.WriteScheduledState = previousWrite;
        }
    }

    /// <summary>
    /// 投递一次真实 Click（CheckBox.OnClick → Click 路径：先 AutoCheck 翻转 Checked，
    /// 再触发 Click）。用反射而非 PerformClick，避免依赖整条父链 Visible。
    /// </summary>
    static void RaiseClick(CheckBox box) =>
        typeof(CheckBox).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(box, new object[] { EventArgs.Empty });

    static void WithReportedCapabilities(MechrevoDeviceCapabilities capabilities, Action<SettingsForm> body)
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        using var hardware = new MechrevoHw(null, capabilities);
        try
        {
            Program.UiAuditMode = false;
            Program.hw = hardware;
            using var form = new SettingsForm();
            form.CreateControl();
            body(form);
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
        }
    }

    static Button? FindButtonByText(Control root, string text)
    {
        foreach (Control child in root.Controls)
        {
            if (child is Button button && button.Text == text) return button;
            Button? nested = FindButtonByText(child, text);
            if (nested is not null) return nested;
        }
        return null;
    }
}
