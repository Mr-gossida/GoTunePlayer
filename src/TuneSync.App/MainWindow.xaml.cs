using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace TuneSync.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        RootNavigation.SetPageProviderService(new SimplePageProvider());
        RootNavigation.Loaded += (_, _) => RootNavigation.Navigate(typeof(Views.MergeView));
    }
}
