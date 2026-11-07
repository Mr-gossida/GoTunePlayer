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
        // Theme resources (and the control templates that depend on them) must be injected
        // before any Wpf.Ui control is constructed, so this has to run before the window is
        // created - a StartupUri window would already be loading by the time OnStartup returns.
        ApplicationThemeManager.Apply(ApplicationTheme.Light);
        ApplicationAccentColorManager.ApplySystemAccent();
        new MainWindow().Show();
    }
}
