using System.Windows.Forms;
using MechrevoLite.Gpu;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 能力级门控失败路径：代际事实表确证不存在的动作（30 系 IGPU_ONLY_* / _RESTART / HOTSWAP =
/// <see cref="EvidenceMark.ProvenAbsent"/>）不得因为机型画像声称支持就被当作可提供的控制项。
/// <see cref="MechrevoHw"/> 的门面谓词就是 UI 可见性判据（SettingsForm.RefreshDeviceCapabilities /
/// GPUModeControl.InitGPUMode），所以这里既锁谓词也锁整段 UI 的隐藏。
/// happy 路径见 <see cref="GpuCapabilityGatingTests"/>。
/// </summary>
[Collection(nameof(SerialGpuSwitchCollection))]
public class GpuCapabilityGatingFailTests
{
    [Fact]
    public void Gen30DoesNotOfferIgpuOnlyEvenWhenTheProfileClaimsSupport()
    {
        using var generation = GpuCapabilityGatingHarness.Generation(GpuCapabilityGatingHarness.Gen30);
        using MechrevoHw hardware = GpuCapabilityGatingHarness.Hardware(new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            IgpuOnly = true,
        });

        hardware.SetIgpuOnlyStatusSupportForTests(true);
        Assert.False(hardware.CanOfferIgpuOnly, "30 系控制台确证没有 IGPU_ONLY_*，能力不得被提供。");
        Assert.False(hardware.CanOfferGpuHotSwap);
    }

    /// <summary>
    /// 30 系官方控制台没有切卡入口，Toggle 点了也不生效。整段不得提供。
    /// </summary>
    [Fact]
    public void Gen30DoesNotOfferGpuModeSwitch()
    {
        using var generation = GpuCapabilityGatingHarness.Generation(GpuCapabilityGatingHarness.Gen30);
        using MechrevoHw hardware = GpuCapabilityGatingHarness.Hardware(new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            DgpuDirect = true,
        });

        Assert.True(hardware.SupportsDgpuDirect);
        Assert.False(hardware.CanOfferGpuModeSwitch);
        Assert.False(hardware.CanOfferIgpuOnly);
    }

    /// <summary>
    /// 30 系事实表：无 IGPU_ONLY_* / TOGGLE_IGPU / RESTART（ProvenAbsent）。
    /// iGPU↔hybrid 在硬件层不存在；CanSwitchGpuMode 不得因画像位把这条路打开。
    /// </summary>
    [Fact]
    public void Gen30_IgpuHybrid_BlockedHw()
    {
        using var generation = GpuCapabilityGatingHarness.Generation(GpuCapabilityGatingHarness.Gen30);
        using MechrevoHw hardware = GpuCapabilityGatingHarness.Hardware(new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            IgpuOnly = true,
            DgpuDirect = true,
            GpuHotSwap = true,
        });
        hardware.SetIgpuOnlyStatusSupportForTests(true);

        Assert.False(DisplayRouteMatrix.AllowsIgpuOnly(DgpuGenerationKind.Gen30));
        Assert.False(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.Gen30, DisplayRouteMatrix.ToggleIgpu));
        Assert.False(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.Gen30, DisplayRouteMatrix.IgpuOnlyOn));
        Assert.False(DisplayRoutePolicy.AllowsAction(DgpuGenerationKind.Gen30, DisplayRouteMatrix.IgpuOnlyOff));
        Assert.Null(GpuRouteCommandLayer.BuildSwitchCommand(
            MechrevoService.GpuIGpu, true, true, false, DgpuGenerationKind.Gen30));
        Assert.Null(GpuRouteCommandLayer.BuildSwitchCommand(
            MechrevoService.GpuStandard, true, true, false, DgpuGenerationKind.Gen30));
        Assert.False(hardware.CanSwitchGpuMode(MechrevoService.GpuIGpu),
            "30 系无 iGPU↔hybrid 硬件路径，CanSwitchGpuMode(iGPU) 必须关。");
        Assert.False(hardware.CanSwitchGpuMode(MechrevoService.GpuStandard),
            "30 系无切卡入口，CanSwitchGpuMode(hybrid) 必须关。");
    }

    [Fact]
    public void Gen30DoesNotOfferHotSwapEvenWithTheServiceHotSwapProfile()
    {
        using var generation = GpuCapabilityGatingHarness.Generation(GpuCapabilityGatingHarness.Gen30);
        using MechrevoHw hardware = GpuCapabilityGatingHarness.Hardware(new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            IgpuOnly = true,
            GpuHotSwap = true,
        });

        Assert.False(hardware.CanOfferGpuHotSwap);
    }

    /// <summary>30 系整段切卡隐藏；40 系直连入口照常出现。</summary>
    [Fact]
    public void Gen30HidesTheGpuSectionWhileGen40StillOffersIt()
    {
        string? previousModel = Environment.GetEnvironmentVariable(ModelOverrideStateMachine.OverrideVariable);
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Environment.SetEnvironmentVariable(ModelOverrideStateMachine.OverrideVariable, "PH4TRX1");
        Program.UiAuditMode = false;
        try
        {
            Assert.False(RefreshGpuSectionOffered(GpuCapabilityGatingHarness.Gen30),
                "30 系官方没有切卡入口，整段不得出现。");
            Assert.True(RefreshGpuSectionOffered(GpuCapabilityGatingHarness.Gen40),
                "40 系直连入口必须照常出现。");
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
            Environment.SetEnvironmentVariable(ModelOverrideStateMachine.OverrideVariable, previousModel);
        }
    }

    /// <summary>
    /// 画像声称有直连/核显，但代际判不出：不得提供 40/50 系那套按钮。
    /// </summary>
    [Fact]
    public void UnresolvedGenerationDoesNotOfferGpuControlsWhenTheProfileClaimsSupport()
    {
        using var generation = GpuCapabilityGatingHarness.Generation(DgpuIdentity.Unknown);
        using MechrevoHw hardware = GpuCapabilityGatingHarness.Hardware(new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            IgpuOnly = true,
            DgpuDirect = true,
            GpuHotSwap = true,
        });
        hardware.SetIgpuOnlyStatusSupportForTests(true);

        Assert.True(hardware.SupportsDgpuDirect);
        Assert.True(hardware.SupportsIgpuOnly);
        Assert.False(hardware.CanOfferGpuModeSwitch);
        Assert.False(hardware.CanOfferIgpuOnly);
        Assert.False(hardware.CanOfferGpuHotSwap);
        Assert.False(hardware.CanSwitchGpuMode(MechrevoService.GpuIGpu));
        Assert.False(hardware.CanSwitchGpuMode(MechrevoService.GpuDgpu));
    }

    /// <summary>
    /// 代际判不出时显卡区消失，必须用既有横幅说明原因，且不得把整机打成只读。
    /// </summary>
    [Fact]
    public void UnresolvedGenerationHidesGpuControlsAndExplainsWhy()
    {
        string? previousModel = Environment.GetEnvironmentVariable(ModelOverrideStateMachine.OverrideVariable);
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Environment.SetEnvironmentVariable(ModelOverrideStateMachine.OverrideVariable, "PH4TRX1");
        Program.UiAuditMode = false;
        try
        {
            using var generation = GpuCapabilityGatingHarness.Generation(DgpuIdentity.Unknown);
            using MechrevoHw hardware = GpuCapabilityGatingHarness.Hardware(new MechrevoDeviceCapabilities
            {
                ProfileAvailable = true,
                DgpuDirect = true,
                IgpuOnly = true,
            });
            hardware.SetIgpuOnlyStatusSupportForTests(true);
            Program.hw = hardware;

            using var form = new SettingsForm { ClientSize = SettingsForm.CompactDashboardLogicalClientSize };
            form.CreateControl();
            form.Show();
            Application.DoEvents();
            form.RefreshDeviceCapabilities();
            Application.DoEvents();

            Control? section = form.Controls.Find("panelGPU", true).FirstOrDefault();
            Assert.True(section is null || !section.Visible, "未识别代际不得显示显卡模式按钮。");
            Control banner = form.Controls.Find("panelUnsupportedModelNotice", true).Single();
            Assert.True(banner.Visible, "显卡控制被代际门禁藏起时必须说明原因。");
            Label notice = form.Controls.Find("labelUnsupportedModelNotice", true).OfType<Label>().Single();
            Assert.Equal(SettingsForm.UnresolvedGenerationNoticeText, notice.Text);
            Assert.False(form.IsReadOnlyDegraded, "代际未知只隐藏显卡切换，不得进入机型只读降级。");
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
            Environment.SetEnvironmentVariable(ModelOverrideStateMachine.OverrideVariable, previousModel);
        }
    }

    [Fact]
    public void NoDgpuHidesGpuControlsAndNamesTheMissingDiscreteGpu()
    {
        string? previousModel = Environment.GetEnvironmentVariable(ModelOverrideStateMachine.OverrideVariable);
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Environment.SetEnvironmentVariable(ModelOverrideStateMachine.OverrideVariable, "PH4TRX1");
        Program.UiAuditMode = false;
        try
        {
            using var generation = GpuCapabilityGatingHarness.Generation(DgpuIdentity.NoDgpu);
            using MechrevoHw hardware = GpuCapabilityGatingHarness.Hardware(new MechrevoDeviceCapabilities
            {
                ProfileAvailable = true,
                DgpuDirect = true,
            });
            Program.hw = hardware;

            using var form = new SettingsForm { ClientSize = SettingsForm.CompactDashboardLogicalClientSize };
            form.CreateControl();
            form.Show();
            Application.DoEvents();
            form.RefreshDeviceCapabilities();
            Application.DoEvents();

            Control? section = form.Controls.Find("panelGPU", true).FirstOrDefault();
            Assert.True(section is null || !section.Visible);
            Label notice = form.Controls.Find("labelUnsupportedModelNotice", true).OfType<Label>().Single();
            Assert.Equal(SettingsForm.NoDgpuGenerationNoticeText, notice.Text);
            Assert.True(form.Controls.Find("panelUnsupportedModelNotice", true).Single().Visible);
            Assert.False(form.IsReadOnlyDegraded);
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
            Environment.SetEnvironmentVariable(ModelOverrideStateMachine.OverrideVariable, previousModel);
        }
    }

    /// <summary>机型只读横幅优先于代际说明，文案不得被代际通知盖掉。</summary>
    [Fact]
    public void UnsupportedModelNoticeIsNotReplacedWhenGenerationIsUnresolved()
    {
        string? previousModel = Environment.GetEnvironmentVariable(ModelOverrideStateMachine.OverrideVariable);
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Environment.SetEnvironmentVariable(ModelOverrideStateMachine.OverrideVariable, "NOTAMODEL");
        Program.UiAuditMode = false;
        try
        {
            using var generation = GpuCapabilityGatingHarness.Generation(DgpuIdentity.Unknown);
            using MechrevoHw hardware = GpuCapabilityGatingHarness.Hardware(new MechrevoDeviceCapabilities
            {
                ProfileAvailable = true,
                DgpuDirect = true,
            });
            // ConnectionGeneration 0 是首连进行中，只读横幅故意不出现。抬过 0 才能测到横幅优先级。
            typeof(MechrevoHw).GetField("_connectionGeneration",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(hardware, 1);
            Program.hw = hardware;

            using var form = new SettingsForm { ClientSize = SettingsForm.CompactDashboardLogicalClientSize };
            form.CreateControl();
            form.Show();
            Application.DoEvents();
            form.RefreshDeviceCapabilities();
            Application.DoEvents();

            Assert.True(form.IsReadOnlyDegraded);
            Label notice = form.Controls.Find("labelUnsupportedModelNotice", true).OfType<Label>().Single();
            Assert.Contains("NOTAMODEL", notice.Text, StringComparison.Ordinal);
            Assert.Contains("只读模式", notice.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("已隐藏显卡模式切换", notice.Text, StringComparison.Ordinal);
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
            Environment.SetEnvironmentVariable(ModelOverrideStateMachine.OverrideVariable, previousModel);
        }
    }

    static bool RefreshGpuSectionOffered(DgpuIdentity identity)
    {
        using var generation = GpuCapabilityGatingHarness.Generation(identity);
        using MechrevoHw hardware = GpuCapabilityGatingHarness.Hardware(new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            DgpuDirect = true,
        });
        Program.hw = hardware;

        using var form = new SettingsForm { ClientSize = SettingsForm.CompactDashboardLogicalClientSize };
        form.CreateControl();
        form.Show();
        Application.DoEvents();
        form.RefreshDeviceCapabilities();
        Application.DoEvents();

        // 整段不可提供时 ArrangeDashboard 不会把它装进 dashboardStack——找不到即未提供。
        Control? section = form.Controls.Find("panelGPU", true).FirstOrDefault();
        return section is not null && section.Visible;
    }
}
