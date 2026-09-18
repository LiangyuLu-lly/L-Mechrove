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

        Assert.False(hardware.CanOfferIgpuOnly, "30 系控制台确证没有 IGPU_ONLY_*，能力不得被提供。");
        Assert.False(hardware.CanOfferGpuModeSwitch);
    }

    [Fact]
    public void Gen30DoesNotOfferTheModeSwitchAtAllBecauseRestartIsProvenAbsent()
    {
        using var generation = GpuCapabilityGatingHarness.Generation(GpuCapabilityGatingHarness.Gen30);
        using MechrevoHw hardware = GpuCapabilityGatingHarness.Hardware(new MechrevoDeviceCapabilities
        {
            ProfileAvailable = true,
            DgpuDirect = true,
        });

        // 直连命令族在 30 系载荷里存在，但产品的手动切换一律"应用目标 + 重启"，
        // 没有 RESTART 就没有任何可执行路由 -> 整段不得出现。
        Assert.True(hardware.SupportsDgpuDirect);
        Assert.False(hardware.CanOfferGpuModeSwitch);
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

    /// <summary>
    /// 端到端 UI：同一份「机型画像声称支持直连」在 30 系必须整段隐藏，在 40 系照常出现——
    /// 唯一的差异是代际。机型身份固定为集合内代号，排除 D1 只读降级的干扰。
    /// </summary>
    [Fact]
    public void Gen30HidesTheGpuModeSectionWhileGen40KeepsIt()
    {
        string? previousModel = Environment.GetEnvironmentVariable(ModelOverrideStateMachine.OverrideVariable);
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Environment.SetEnvironmentVariable(ModelOverrideStateMachine.OverrideVariable, "PH4TRX1");
        Program.UiAuditMode = false;
        try
        {
            Assert.False(RefreshGpuSectionOffered(GpuCapabilityGatingHarness.Gen30),
                "30 系没有 RESTART，手动切换整段不得出现（否则用户点到只会失败的按钮）。");
            Assert.True(RefreshGpuSectionOffered(GpuCapabilityGatingHarness.Gen40),
                "40 系有可执行的重启路由，直连入口必须照常出现。");
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
