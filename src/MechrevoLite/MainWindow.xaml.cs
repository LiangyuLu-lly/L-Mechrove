using System.Windows;
using MechrevoLite.ViewModels;

namespace MechrevoLite;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
