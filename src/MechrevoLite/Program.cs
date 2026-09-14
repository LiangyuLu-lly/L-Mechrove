using System.Windows;

namespace MechrevoLite;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        Mutex mutex = new(true, "MechrevoLite_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("MechrevoLite 已在运行", "MechrevoLite", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var app = new App();
        app.Run();
        mutex.Dispose();
    }
}
