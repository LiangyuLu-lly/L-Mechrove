using System.Reflection;
using System.Windows.Forms;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// N12 audit finding (dead button): the dashboard keyboard row is hidden when the machine has no
/// keyboard lighting, but the tray/context-menu action "键盘灯效" was ungated - it opened the RGB
/// form on machines where the feature does not exist. The vendor hides the feature when unsupported;
/// the tray action must use the same predicate as the dashboard row.
///
/// Presence in the menu is the observable contract (ToolStripItem.Visible is unreliable while the
/// menu is not shown), so these tests assert whether the item was added at all.
/// </summary>
public class TrayKeyboardGatingN12Tests
{
    static ContextMenuStrip BuildTrayMenu(SettingsForm form)
    {
        // The menu is rebuilt on every Opening; drive the same entry point the tray uses.
        form.SetContextMenu();
        FieldInfo field = typeof(SettingsForm).GetField("contextMenuStrip", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsAssignableFrom<ContextMenuStrip>(field.GetValue(form));
    }

    static bool HasKeyboardAction(ContextMenuStrip menu) =>
        menu.Items.OfType<ToolStripMenuItem>().Any(item => (item.Text ?? "").Contains("键盘灯效"));

    /// <summary>
    /// Given a machine whose reported capabilities have no keyboard lighting, When the tray menu is
    /// built, Then the keyboard-lighting action is not offered (no dead button).
    /// </summary>
    [Fact]
    public void TheTrayKeyboardActionIsHiddenWhenTheMachineHasNoKeyboardLighting()
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = false;
        Program.hw = new MechrevoHw(null, new MechrevoDeviceCapabilities { Keyboard = false });
        try
        {
            using var form = new SettingsForm();
            form.CreateControl();
            form.PerformLayout();
            ContextMenuStrip menu = BuildTrayMenu(form);

            Assert.False(HasKeyboardAction(menu),
                "无键盘灯效能力的机器不得在托盘菜单暴露「键盘灯效」入口（死按钮）。");
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
        }
    }

    /// <summary>
    /// Given a machine whose reported capabilities DO include keyboard lighting, When the tray menu
    /// is built, Then the action is offered - the gate must not hide a working feature.
    /// </summary>
    [Fact]
    public void TheTrayKeyboardActionIsOfferedWhenTheMachineHasKeyboardLighting()
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = false;
        Program.hw = new MechrevoHw(null, new MechrevoDeviceCapabilities { Keyboard = true });
        try
        {
            using var form = new SettingsForm();
            form.CreateControl();
            form.PerformLayout();
            ContextMenuStrip menu = BuildTrayMenu(form);

            Assert.True(HasKeyboardAction(menu), "有键盘灯效能力的机器必须保留托盘入口。");
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
        }
    }

    /// <summary>
    /// Given audit mode (which forces every entry visible for layout auditing), When the tray menu is
    /// built, Then the keyboard action is still present - the gate must not remove it from the audit
    /// surface, or the layout baseline would silently change.
    /// </summary>
    [Fact]
    public void TheTrayKeyboardActionStaysPresentInAuditMode()
    {
        bool previousAudit = Program.UiAuditMode;
        MechrevoHw? previousHardware = Program.hw;
        Program.UiAuditMode = true;
        Program.hw = new MechrevoHw(null, new MechrevoDeviceCapabilities { Keyboard = false });
        try
        {
            using var form = new SettingsForm();
            form.CreateControl();
            form.PerformLayout();
            ContextMenuStrip menu = BuildTrayMenu(form);

            Assert.True(HasKeyboardAction(menu), "审计模式必须保留入口（布局基线依赖它）。");
        }
        finally
        {
            Program.hw = previousHardware!;
            Program.UiAuditMode = previousAudit;
        }
    }
}
