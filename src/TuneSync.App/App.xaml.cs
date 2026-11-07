using System.Windows;
using Wpf.Ui.Appearance;

namespace TuneSync.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationThemeManager.Apply(ApplicationTheme.Light);
    }
}
