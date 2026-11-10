using TuneSync.App.Views;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace TuneSync.App;

/// <summary>
/// Interaction logic for ShellWindow.xaml
/// </summary>
public partial class ShellWindow : FluentWindow
{
    public ShellWindow(INavigationViewPageProvider pageProvider)
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        RootNavigation.SetPageProviderService(pageProvider);
        RootNavigation.Loaded += (_, _) => RootNavigation.Navigate(typeof(LibraryView));
    }
}
