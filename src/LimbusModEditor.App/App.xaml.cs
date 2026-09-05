using System.Configuration;
using System.Data;
using System.Windows;
using WpfApplication = System.Windows.Application;

namespace LimbusModEditor.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : WpfApplication
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (!OperatingSystem.IsWindows())
        {
            MessageBox.Show("Limbus Mod Editor 仅支持 Windows。", "平台不受支持", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        base.OnStartup(e);
    }
}

