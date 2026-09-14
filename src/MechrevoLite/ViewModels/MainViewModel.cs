using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MechrevoLite.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private object? _currentPage;
    public HomeViewModel Home { get; }

    public MainViewModel(HomeViewModel home)
    {
        Home = home;
        CurrentPage = home;
    }

    [RelayCommand]
    void Navigate(string page) => CurrentPage = page switch
    {
        "home" => Home,
        _ => Home,
    };
}
