using MechrevoLite.ViewModels;
using WinForms = System.Windows.Forms;

namespace MechrevoLite.Services;

/// <summary>托盘：图标 + 模式快捷切换 + 退出。</summary>
public class TrayService : IDisposable
{
    readonly WinForms.NotifyIcon _icon = new();
    readonly Action<string> _onModeSwitch;
    readonly Action _onExit;

    public TrayService(Action<string> onModeSwitch, Action onExit)
    {
        _onModeSwitch = onModeSwitch;
        _onExit = onExit;
        _icon.Icon = System.Drawing.SystemIcons.Application;
        _icon.Text = "MechrevoLite";
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => _onModeSwitch("open"));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("办公模式", null, (_, _) => _onModeSwitch(HomeViewModel.OfficeMode));
        menu.Items.Add("游戏模式", null, (_, _) => _onModeSwitch(HomeViewModel.GamingMode));
        menu.Items.Add("极速模式", null, (_, _) => _onModeSwitch(HomeViewModel.TurboMode));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => _onExit());
        _icon.ContextMenuStrip = menu;
        _icon.Visible = true;
        _icon.DoubleClick += (_, _) => _onModeSwitch("open");
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
