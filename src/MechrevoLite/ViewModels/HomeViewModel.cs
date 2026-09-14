using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MechrevoLite.Hardware;
using MechrevoLite.Models;
using MechrevoLite.Services;

namespace MechrevoLite.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    public const string OfficeMode = "OPERATING_OFFICE_MODE";
    public const string GamingMode = "OPERATING_GAMING_MODE";
    public const string TurboMode = "OPERATING_TURBO_MODE";
    public const string CustomMode = "OPERATING_CUSTOM_MODE";

    public IReadOnlyList<ModeOption> Modes { get; } = new[]
    {
        new ModeOption { Action = OfficeMode, Name = "办公", Description = "安静低功耗" },
        new ModeOption { Action = GamingMode, Name = "游戏", Description = "均衡性能" },
        new ModeOption { Action = TurboMode, Name = "极速", Description = "满血释放" },
        new ModeOption { Action = CustomMode, Name = "自定义", Description = "自定曲线" },
    };

    readonly IHardwareBackend _backend;
    public TelemetrySnapshot Snapshot { get; }
    public bool IsConnected => _backend.IsConnected;

    [ObservableProperty] private string? _statusText;

    public HomeViewModel(IHardwareBackend backend, TelemetryService telemetry)
    {
        _backend = backend;
        Snapshot = telemetry.Snapshot;
        _backend.ConnectionChanged += connected =>
        {
            StatusText = connected ? "已连接" : "服务未连接";
            OnPropertyChanged(nameof(IsConnected));
        };
    }

    [RelayCommand]
    async Task SwitchMode(ModeOption mode)
    {
        StatusText = "切换中…";
        try
        {
            await _backend.PublishAsync("Fan/Control", new { Action = mode.Action });
            StatusText = $"{mode.Name}模式已生效";
        }
        catch (Exception ex)
        {
            StatusText = "写入失败：" + ex.Message;
        }
    }
}
