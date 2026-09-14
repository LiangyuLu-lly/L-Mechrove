using System.IO;
using System.Windows;
using MechrevoLite.Hardware;
using MechrevoLite.Services;
using MechrevoLite.ViewModels;
using MechrevoLite.Views;

namespace MechrevoLite;

public partial class App : Application
{
    MqttBackend? _backend;
    TelemetryService? _telemetry;
    TrayService? _tray;
    HomeViewModel? _home;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MechrevoLite");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"{DateTime.Now}: {args.Exception}\n");
            MessageBox.Show("发生错误：" + args.Exception.Message, "MechrevoLite");
            args.Handled = true;
        };

        _backend = new MqttBackend();
        _telemetry = new TelemetryService(_backend);
        _home = new HomeViewModel(_backend, _telemetry);
        var main = new MainViewModel(_home);

        var window = new MainWindow(main);
        MainWindow = window;
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.Hide(); // 最小化到托盘
            }
        };

        _tray = new TrayService(
            action =>
            {
                if (action == "open")
                {
                    window.Show();
                    window.WindowState = WindowState.Normal;
                    window.Activate();
                }
                else
                {
                    _home?.SwitchModeCommand.Execute(action);
                }
            },
            onExit: async () =>
            {
                try { if (_backend is { IsConnected: true }) await _backend.PublishAsync("System/Control", new { Action = "System_OFF" }); }
                catch { /* 尽力而为 */ }
                Shutdown();
            });

        window.Show();

        _ = Task.Run(async () =>
        {
            try
            {
                var ok = await _backend.ConnectAsync();
                if (ok) await _telemetry.StartAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"backend connect fail: {ex.Message}");
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _backend?.Dispose();
        base.OnExit(e);
    }
}
